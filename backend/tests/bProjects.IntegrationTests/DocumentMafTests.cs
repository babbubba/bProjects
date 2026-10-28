using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T043 - Percorso completo con il modello reale (DGX Spark) su PostgreSQL reale: chat salvata, Analizza (entro 180 s),
/// conferma esplicita della revisione, Genera (entro 180 s dal proprio comando) e bozza Markdown Proposed/NotApproved legata
/// alla revisione confermata, con citazioni valide. Il modello e' contato da un decoratore: dopo i messaggi PM e prima di
/// Analizza/Genera le invocazioni sono zero (nessuna invocazione automatica) e Genera ne produce esattamente una.
/// Servono BPROJECTS_TEST_POSTGRES e BPROJECTS_TEST_DGX_ENDPOINT/API_KEY/MODEL: senza, il test risulta Skipped, mai superato.
/// Un errore o un timeout e' un fallimento, non un successo. Registra durate e conteggi, non i contenuti integrali.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
public sealed class DocumentMafTests : IAsyncLifetime
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(180);

    private static readonly string[] PmMessages =
    [
        "Voglio realizzare un portale clienti chiamato Aurora per gestire i ticket di assistenza: login con email, elenco ticket, allegati.",
        "Gli utenti sono i clienti finali e gli agenti dell'assistenza. Le notifiche via SMS sono escluse dal primo rilascio.",
        "Non so ancora quanti clienti useranno il portale; lo scopriremo dopo il primo trimestre. La fatturazione resta fuori perimetro.",
    ];

    private readonly PostgresDatabaseFixture _database = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task Real_model_analysis_then_confirmed_document_within_180s_each_bound_to_the_confirmed_revision_with_zero_automatic_invocations()
    {
        var options = TestEnvironment.RequireDgxOptions();
        TestEnvironment.RequirePostgres();

        var counter = new CountingChatClient();
        await using var hosted = await ApiHarness.StartAsync(_database.ConnectionString, configure: services =>
        {
            var model = new InternalModelClient(options);
            var factory = new AgentFactory(options, counter.Wrap(model.ChatClient), async (t, ct) => await model.CountTokensAsync(t, ct));
            var agent = new ConversationalAgent(AgentFactory.Create(options, model), model.CreateChatOptions(temperature: 0.2f));
            services.AddSingleton(agent);
            services.AddSingleton(sp => new AnalysisWorkflow(
                sp.GetRequiredService<IServiceScopeFactory>(),
                agent,
                model.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0)));
            services.AddSingleton(new DocumentAgent(factory, model.CreateChatOptions(temperature: 0.2f)));
            services.AddSingleton(sp => new DocumentWorkflow(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<DocumentAgent>()));
        });

        using var pm = await hosted.LoginAsync();
        var created = await pm.PostAsJsonAsync("/api/projects", new { name = $"Documento reale {Guid.NewGuid():N}" }, Ct);
        var project = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        foreach (var text in PmMessages)
        {
            var posted = await pm.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
            Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        }

        // Zero invocazioni automatiche: salvare i messaggi PM non chiama il modello; nessun run formale esiste.
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.Equal(0, counter.Calls);
        Assert.Equal(0, await ScalarAsync<long>("""SELECT COUNT(*) FROM "ProcessingRuns" """));

        // --- Analizza (<= 180 s)
        var analyze = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        analyze.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var clock = Stopwatch.StartNew();
        var accepted = await pm.SendAsync(analyze, Ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var analysisId = (await accepted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var analysisRun = await WaitTerminalAsync(pm, project, analysisId);
        clock.Stop();
        var analysisSeconds = clock.Elapsed.TotalSeconds;
        Assert.Equal("Completed", analysisRun.GetProperty("status").GetString()); // un errore non conta come successo
        Assert.True(clock.Elapsed <= Budget, $"analisi: {analysisSeconds:F1}s oltre i 180s");
        var revisionId = analysisRun.GetProperty("resultRevisionId").GetGuid();
        var callsAfterAnalysis = counter.Calls; // il contatore osserva solo DocumentAgent: Analysis non deve averlo toccato
        Assert.Equal(0, callsAfterAnalysis);

        // Genera prima della conferma e' vietato e non invoca il modello.
        var early = await GenerateAsync(pm, project, analysisId, revisionId);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(callsAfterAnalysis, counter.Calls);

        // --- Conferma esplicita (non genera nulla)
        var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysisId}/confirm");
        confirm.Headers.Add("If-Match", $"\"{revisionId}\"");
        var confirmed = await pm.SendAsync(confirm, Ct);
        Assert.True(confirmed.IsSuccessStatusCode, $"conferma: {(int)confirmed.StatusCode}");
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.Equal(callsAfterAnalysis, counter.Calls); // la conferma non invoca il modello
        Assert.Equal(0, await ScalarAsync<long>("""SELECT COUNT(*) FROM "ProcessingRuns" WHERE "Kind" = 'Document'"""));
        Assert.Equal("PmConfirmed", await ScalarAsync<string>("""SELECT "ValidationState"::text FROM "AnalysisRevisions" WHERE "Id" = @r""", ("r", revisionId)));

        // --- Genera (<= 180 s dal proprio comando, nessun'altra coda)
        var clock2 = Stopwatch.StartNew();
        var generating = await GenerateAsync(pm, project, analysisId, revisionId);
        Assert.Equal(HttpStatusCode.Accepted, generating.StatusCode);
        var docRunId = (await generating.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var docRun = await WaitTerminalAsync(pm, project, docRunId);
        clock2.Stop();
        Assert.Equal("Completed", docRun.GetProperty("status").GetString());
        Assert.True(clock2.Elapsed <= Budget, $"generazione: {clock2.Elapsed.TotalSeconds:F1}s oltre i 180s");
        Assert.Equal(1, counter.Calls - callsAfterAnalysis); // una sola invocazione di DocumentAgent, nessuna riparazione

        // --- Bozza legata alla revisione confermata, Proposed/NotApproved, citazioni valide
        var draft = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/draft", Ct);
        Assert.Equal(revisionId, draft.GetProperty("analysisRevisionId").GetGuid());
        Assert.Equal(analysisId, draft.GetProperty("analysisId").GetGuid());
        Assert.Equal(docRunId, draft.GetProperty("processingRunId").GetGuid());
        Assert.Equal("Proposed", draft.GetProperty("status").GetString());
        Assert.Equal("NotApproved", draft.GetProperty("approvalStatus").GetString());
        var markdown = draft.GetProperty("markdown").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(markdown));
        var json = await ScalarAsync<string>("""SELECT "ContentJson" FROM "AnalysisRevisions" WHERE "Id" = @r""", ("r", revisionId));
        var content = AnalysisContentJson.Deserialize(json);
        var sourceIds = content.Sources.Select(s => s.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var citations = Regex.Matches(markdown, @"\[fonte:([^\]\s]*)\]", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(citations);
        Assert.All(citations, c => Assert.Contains(c, sourceIds));
        Assert.Equal(analysisId, await ScalarAsync<Guid>("""SELECT "AnalysisId" FROM "ProcessingRuns" WHERE "Id" = @r""", ("r", docRunId)));
        Assert.Equal(revisionId, await ScalarAsync<Guid>("""SELECT "InputRevisionId" FROM "ProcessingRuns" WHERE "Id" = @r""", ("r", docRunId)));
        Assert.Equal(0, await ScalarAsync<long>("""SELECT COUNT(*) FROM "ProcessingRuns" WHERE "Status" IN ('Queued','Running')"""));
        Assert.Equal(1, await ScalarAsync<long>("""SELECT COUNT(*) FROM "Drafts" """));

        TestContext.Current.SendDiagnosticMessage(
            $"T043 reale: analisi {analysisSeconds:F1}s, generazione {clock2.Elapsed.TotalSeconds:F1}s, citazioni={citations.Count}, invocazioni modello (documento)=1, caratteri bozza={markdown.Length}");
    }

    private static Task<HttpResponseMessage> GenerateAsync(HttpClient pm, Guid project, Guid analysisId, Guid revisionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysisId}/document-generations");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(new { revisionId });
        return pm.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> WaitTerminalAsync(HttpClient pm, Guid project, Guid runId)
    {
        var deadline = DateTime.UtcNow + Budget + TimeSpan.FromSeconds(30);
        JsonElement run = default;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, Ct);
            run = (await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/processing-runs/{runId}", Ct)).Clone();
            if (run.GetProperty("status").GetString() is "Completed" or "Failed")
            {
                break;
            }
        }

        return run;
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

    /// <summary>Decoratore che conta le invocazioni del modello fatte dal DocumentAgent.</summary>
    private sealed class CountingChatClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IChatClient Wrap(IChatClient model) => new Counting(model, () => Interlocked.Increment(ref _calls));

        private sealed class Counting(IChatClient inner, Action onCall) : DelegatingChatClient(inner)
        {
            public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                onCall();
                return base.GetResponseAsync(messages, options, cancellationToken);
            }

            public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                onCall();
                return base.GetStreamingResponseAsync(messages, options, cancellationToken);
            }
        }
    }
}
