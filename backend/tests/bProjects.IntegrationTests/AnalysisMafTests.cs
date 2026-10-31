using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T031 - Analizza reale end-to-end con il DGX Spark: applicazione ospitata con il workflow di Analysis nativo e il modello
/// interno reale su PostgreSQL reale. A servizio sano il JSON strutturato e' prodotto entro 180 secondi, validato e persistito
/// come revisione AI Unvalidated (stato UI derivato WaitingForPM: nessun run attivo, nessun lease), senza Markdown ne' bozze.
/// Servono BPROJECTS_TEST_POSTGRES e BPROJECTS_TEST_DGX_ENDPOINT/API_KEY/MODEL: senza, il test risulta Skipped, mai superato.
/// Registra durata e conteggi, non i contenuti integrali.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
public sealed class AnalysisMafTests : IAsyncLifetime
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
    public async Task Real_analysis_produces_validated_persisted_unvalidated_json_within_180s_and_no_markdown()
    {
        var options = TestEnvironment.RequireDgxOptions();
        TestEnvironment.RequirePostgres();

        // Nessun ChatTurnExecutor: i messaggi PM restano in coda come turni e solo il run formale usa il modello.
        await using var hosted = await ApiHarness.StartAsync(_database.ConnectionString, configure: services =>
        {
            var model = new InternalModelClient(options);
            var agent = new ConversationalAgent(AgentFactory.Create(options, model), model.CreateChatOptions(temperature: 0.2f));
            services.AddSingleton(agent);
            services.AddSingleton(sp => new AnalysisWorkflow(
                sp.GetRequiredService<IServiceScopeFactory>(),
                agent,
                model.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0)));
        });

        using var pm = await hosted.LoginAsync();
        var created = await pm.PostAsJsonAsync("/api/projects", new { name = $"Analisi reale {Guid.NewGuid():N}" }, Ct);
        var project = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        foreach (var text in PmMessages)
        {
            var posted = await pm.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
            Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var clock = Stopwatch.StartNew();
        var accepted = await pm.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var runId = (await accepted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var run = await WaitTerminalAsync(pm, project, runId);
        clock.Stop();
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.True(clock.Elapsed <= Budget, $"analisi: {clock.Elapsed.TotalSeconds:F1}s oltre i 180s");
        Assert.Equal(JsonValueKind.Null, run.GetProperty("error").ValueKind);
        var revisionId = run.GetProperty("resultRevisionId").GetGuid();

        // Persistito e rivalidato: il JSON salvato e' un AnalysisContent valido, Unvalidated, con fonti e id coerenti.
        var json = await ScalarAsync<string>("""SELECT "ContentJson" FROM "AnalysisRevisions" WHERE "Id" = @r""", ("r", revisionId));
        var content = AnalysisContentJson.Deserialize(json);
        Assert.Equal(runId, content.AnalysisId);
        Assert.Equal(project, content.ProjectId);
        Assert.Equal(AnalysisContentState.Unvalidated, content.ValidationState);
        Assert.NotEmpty(content.Sources);
        Assert.NotEmpty(content.Goals);
        var sourceIds = content.Sources.Select(s => s.Id).ToHashSet();
        var items = content.Goals.Concat(content.Requirements).Concat(content.Constraints).Concat(content.Assumptions).Concat(content.OpenQuestions);
        Assert.All(items, i => Assert.All(i.EvidenceRefs, e => Assert.Contains(e, sourceIds)));
        Assert.Equal("Unvalidated", await ScalarAsync<string>("""SELECT "ValidationState"::text FROM "AnalysisRevisions" WHERE "Id" = @r""", ("r", revisionId)));

        // Stato derivato WaitingForPM: nessun run attivo e revisione corrente Unvalidated.
        var view = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}", Ct);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("activeProcessingRunId").ValueKind);
        Assert.Equal(revisionId, view.GetProperty("currentRevisionId").GetGuid());
        Assert.Equal("Unvalidated", view.GetProperty("validationState").GetString());
        var preview = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/analyses/{runId}", Ct);
        Assert.Equal("Unvalidated", preview.GetProperty("validationState").GetString());
        Assert.False(preview.GetProperty("canGenerate").GetBoolean());
        Assert.Equal(0, await ScalarAsync<long>("""SELECT COUNT(*) FROM "ProcessingRuns" WHERE "Status" IN ('Queued','Running')"""));

        // Zero Markdown: nessuna proprieta', nessuna bozza/run documentale, nessun marcatore nei testi.
        Assert.False(preview.TryGetProperty("markdown", out _));
        Assert.Equal(0, await ScalarAsync<long>("""SELECT COUNT(*) FROM "ProcessingRuns" WHERE "Kind" = 'Document'"""));
        var texts = items.Select(i => i.Text).Concat(content.ContextAndScope.Overview.Select(i => i.Text)).ToList();
        Assert.All(texts, t =>
        {
            Assert.DoesNotContain("```", t);
            Assert.DoesNotContain("**", t);
            Assert.DoesNotMatch(@"(?m)^\s{0,3}#{1,6}\s", t);
        });

        TestContext.Current.SendDiagnosticMessage(
            $"T031 analisi reale: {clock.Elapsed.TotalSeconds:F1}s, fonti={content.Sources.Count}, obiettivi={content.Goals.Count}, requisiti={content.Requirements.Count}, domande aperte={content.OpenQuestions.Count}");
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
}
