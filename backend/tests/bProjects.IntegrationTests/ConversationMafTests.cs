using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T023 - chat reale end-to-end con il DGX Spark: applicazione ospitata con dispatcher e modello interno reali su
/// PostgreSQL reale, cinque scambi riusciti entro 180 secondi ciascuno (servizio sano, nessun'altra coda), streaming
/// osservato dal canale SSE, nessuna analisi formale e ripresa dello storico in una nuova sessione e dopo il riavvio
/// dell'app. Servono BPROJECTS_TEST_POSTGRES e BPROJECTS_TEST_DGX_ENDPOINT/API_KEY/MODEL: senza, i test risultano
/// Skipped, mai superati. Registra durate e conteggi, non i contenuti integrali.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
public sealed class ConversationMafTests : IAsyncLifetime
{
    private static readonly TimeSpan ExchangeBudget = TimeSpan.FromSeconds(180);

    private static readonly string[] PmMessages =
    [
        "Voglio realizzare un portale clienti chiamato Aurora per gestire i ticket di assistenza. Ecco alcune note sparse: login con email, elenco ticket, allegati.",
        "Gli utenti sono i clienti finali e gli agenti dell'assistenza. Le notifiche via SMS sono escluse dal primo rilascio.",
        "Non so ancora quanti clienti useranno il portale; lo scopriremo dopo il primo trimestre.",
        "Il reparto commerciale vorrebbe anche la fatturazione nel portale, ma la direzione dice che resta fuori perimetro.",
        "Per ora basta cosi' sul perimetro: riassumi in due frasi quello che hai capito, senza fare domande.",
    ];

    private readonly PostgresDatabaseFixture _database = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task Five_real_exchanges_complete_within_180s_with_streaming_no_formal_analysis_and_resumed_history()
    {
        var options = TestEnvironment.RequireDgxOptions();
        TestEnvironment.RequirePostgres();
        var timings = new List<double>();
        var replies = new List<string>();
        Guid projectId;
        Guid conversationId;

        await using (var hosted = await StartHostedAsync(options))
        {
            using var pm = await hosted.LoginAsync();
            var created = await pm.PostAsJsonAsync("/api/projects", new { name = $"Chat reale {Guid.NewGuid():N}" }, Ct);
            projectId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
            conversationId = await ScalarAsync<Guid>("""SELECT "Id" FROM "Conversations" WHERE "ProjectId" = @p""", ("p", projectId));
            await using var sse = await ProjectEventsTests.Sse.OpenAsync(pm, projectId);

            foreach (var text in PmMessages)
            {
                var deltasBefore = sse.Snapshot().Count(e => e.Name == "chat.delta");
                var clock = Stopwatch.StartNew();
                var turnId = await PostAsync(pm, projectId, text);
                var status = await WaitTerminalAsync(pm, projectId, turnId);
                clock.Stop();

                Assert.Equal("Completed", status);
                Assert.True(clock.Elapsed <= ExchangeBudget, $"scambio {timings.Count + 1}: {clock.Elapsed.TotalSeconds:F1}s oltre i 180s");
                timings.Add(Math.Round(clock.Elapsed.TotalSeconds, 1));

                var deltas = sse.Snapshot().Where(e => e.Name == "chat.delta" && e.Json.GetProperty("chatTurnId").GetGuid() == turnId).ToList();
                Assert.True(deltas.Count > 1, $"scambio {timings.Count}: attesi piu delta in streaming, ricevuti {deltas.Count}");
                Assert.True(sse.Snapshot().Count(e => e.Name == "chat.delta") > deltasBefore);

                var reply = (await MessagesAsync(pm, projectId)).Last(m => m.Role == "assistant");
                Assert.Equal(string.Concat(deltas.Select(d => d.Json.GetProperty("text").GetString())), reply.Text);
                Assert.InRange(reply.Text.Count(c => c == '?'), 0, 1);               // al massimo una domanda per turno
                Assert.DoesNotContain("<think>", reply.Text, StringComparison.OrdinalIgnoreCase);
                replies.Add(reply.Text);
            }

            // Nessuna domanda ripetuta dopo che il PM ha risposto.
            var questions = replies.Select(Question).Where(q => q is not null).ToList();
            Assert.Equal(questions.Count, questions.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // Nessuna analisi formale: solo dialogo (utente/assistente) e nessuna tabella di analisi/elaborazione con righe.
            var messages = await MessagesAsync(pm, projectId);
            Assert.Equal(PmMessages.Length * 2, messages.Count);
            Assert.All(messages, m => Assert.Contains(m.Role, new[] { "user", "assistant" }));
            Assert.Equal(PmMessages, messages.Where(m => m.Role == "user").Select(m => m.Text));   // originali intatti
            Assert.Equal(0, await FormalAnalysisRowsAsync());

            // Nuova sessione (nuovo login): lo storico e' quello persistito.
            using var secondSession = await hosted.LoginAsync();
            Assert.Equal(messages.Select(m => m.Text), (await MessagesAsync(secondSession, projectId)).Select(m => m.Text));
        }

        // Riavvio dell'app (nuovo processo applicativo, stesso database): la chat riprende dalla sessione committata.
        await using (var restarted = await StartHostedAsync(options))
        {
            using var pm = await restarted.LoginAsync();
            Assert.Equal(PmMessages.Length * 2, (await MessagesAsync(pm, projectId)).Count);

            var started = Stopwatch.StartNew();
            var turnId = await PostAsync(pm, projectId, "Come si chiama il portale di cui parlavamo? Rispondi solo con il nome.");
            Assert.Equal("Completed", await WaitTerminalAsync(pm, projectId, turnId));
            Assert.True(started.Elapsed <= ExchangeBudget);

            var resumed = (await MessagesAsync(pm, projectId)).Last(m => m.Role == "assistant");
            Assert.Contains("Aurora", resumed.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(PmMessages.Length * 2 + 2, await ScalarAsync<long>("""SELECT COUNT(*) FROM "ConversationMessages" WHERE "ConversationId" = @c""", ("c", conversationId)));
        }

        // Solo durate e conteggi nel log del test, mai i contenuti.
        TestContext.Current.SendDiagnosticMessage($"T023 durate scambi (s): {string.Join(", ", timings)}");
    }

    // ---------------------------------------------------------------- supporto

    private Task<ApiHarness> StartHostedAsync(AiOptions options) =>
        ApiHarness.StartAsync(_database.ConnectionString, configure: services =>
        {
            var model = new InternalModelClient(options);
            services.AddSingleton(new ConversationalAgent(AgentFactory.Create(options, model), model.CreateChatOptions(temperature: 0.2f)));
            services.AddSingleton<IChatTurnExecutor, ConversationalTurnExecutor>();
        });

    private sealed record Message(string Role, string Text);

    private static async Task<Guid> PostAsync(HttpClient pm, Guid projectId, string text)
    {
        var response = await pm.PostAsJsonAsync($"/api/projects/{projectId}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("chatTurnId").GetGuid();
    }

    private static async Task<string> WaitTerminalAsync(HttpClient pm, Guid projectId, Guid turnId)
    {
        var deadline = DateTime.UtcNow + ExchangeBudget + TimeSpan.FromSeconds(30);
        var status = "";
        while (DateTime.UtcNow < deadline && status is not ("Completed" or "Failed"))
        {
            await Task.Delay(500, Ct);
            status = (await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/chat-turns/{turnId}", Ct)).GetProperty("status").GetString()!;
        }

        return status;
    }

    private static async Task<List<Message>> MessagesAsync(HttpClient pm, Guid projectId)
    {
        var all = new List<Message>();
        long? after = null;
        for (; ; )
        {
            var page = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/messages" + (after is null ? "" : $"?afterSequence={after}"), Ct);
            var items = page.GetProperty("items").EnumerateArray().ToList();
            all.AddRange(items.Select(i => new Message(i.GetProperty("role").GetString()!, i.GetProperty("text").GetString()!)));
            var next = page.GetProperty("nextAfterSequence");
            if (items.Count == 0 || next.ValueKind == JsonValueKind.Null || next.GetInt64() == after)
            {
                return all;
            }

            after = next.GetInt64();
        }
    }

    private static string? Question(string reply)
    {
        var end = reply.IndexOf('?');
        if (end < 0)
        {
            return null;
        }

        var start = reply.LastIndexOfAny(['.', '!', '\n'], end) + 1;
        return reply[start..(end + 1)].Trim();
    }

    private async Task<int> FormalAnalysisRowsAsync()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var tables = new NpgsqlCommand(
            """SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND (table_name ILIKE '%Analysis%' OR table_name ILIKE '%ProcessingRun%')""", connection);
        var names = new List<string>();
        await using (var reader = await tables.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                names.Add(reader.GetString(0));
            }
        }

        var rows = 0;
        foreach (var name in names)
        {
            await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM \"{name.Replace("\"", "\"\"")}\"", connection);
            rows += Convert.ToInt32(await count.ExecuteScalarAsync(Ct));
        }

        return rows;
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
