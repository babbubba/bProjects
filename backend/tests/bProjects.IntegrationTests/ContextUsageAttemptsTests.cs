using System.Text.Json;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// T102 — dettaglio dei tentativi e impronta del modello effettivo nel monitor del contesto (<c>GET context-usage</c>): tentativi falliti,
/// interrotti e vincente dello stesso lavoro logico, tempo attivo distinto da recupero e coda senza doppio conteggio, modello letto dalla
/// chiamata (non dalla configurazione né dal monitor), storici non disponibili e redazione. PostgreSQL reale, nessun modello (DGX non impostato).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ContextUsageAttemptsTests
{
    private const string Config = "{\"phaseMaxOutputTokens\":512,\"safetyMarginTokens\":256}";
    private const string Base = "'2026-01-01T00:00:00Z'::timestamptz";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Call(
        Guid project, string ownerColumn, Guid owner, int attempt, long fence, long startMs, long? durationMs, string state = "Completed", bool final = true,
        string phase = "Chat", string model = "modello-A", int? window = 32768, int? prompt = 100, int? completion = 20, string finishReason = "stop")
    {
        var total = prompt is { } p && completion is { } c ? (p + c).ToString() : "NULL";
        return $"""
            INSERT INTO "InferenceCalls" ("Id","ProjectId","{ownerColumn}","Attempt","FencingToken","Phase","ModelId","StartedAt","DurationMs","ContextWindowTokens","ReasoningRequested","ReasoningEffective",
                "PromptTokens","CompletionTokens","TotalTokens","FinishReason","State","IsFinal","Config")
            VALUES ('{Guid.NewGuid()}','{project}','{owner}',{attempt},{fence},'{phase}','{model}',{Base} + interval '{startMs} milliseconds',{durationMs?.ToString() ?? "NULL"},{window?.ToString() ?? "NULL"},false,'off',
                {prompt?.ToString() ?? "NULL"},{completion?.ToString() ?? "NULL"},{total},'{finishReason}','{state}',{(final ? "true" : "false")},'{Config}')
            """;
    }

    private static async Task<(MetricsHost Host, HttpClient Pm, SeededRoot Root, ParsingDatabase Database)> StartAsync(RootKind kind, int attempts, ParsingDatabase database)
    {
        var host = await MetricsHost.StartAsync(database.ConnectionString, new CannedLlamaServer(), MetricsHost.CannedOptions(), chat: false, formal: false);
        var pm = await host.App.LoginAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { ElapsedSeconds = 60, AttemptCount = attempts, Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Extended });
        return (host, pm, root, database);
    }

    private static string Column(SeededRoot root) => root.Kind == RootKind.Turn ? "ChatTurnId" : "ProcessingRunId";

    /// <summary>Formato normativo: primi 16 esadecimali minuscoli di SHA-256 di «modelId|finestra verificata» (o «unavailable»).</summary>
    private static string Expected(string modelId, int? window) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{modelId}|{window?.ToString() ?? "unavailable"}"))).ToLowerInvariant()[..16];

    private static JsonElement Attempts(JsonElement usage) => usage.GetProperty("last").GetProperty("attempts");

    [Fact]
    public async Task A_failed_attempt_and_the_winning_one_are_distinct_and_traceable_to_the_same_work()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var (host, pm, root, _) = await StartAsync(RootKind.Turn, 2, database);
        await using var _h = host;
        using var _p = pm;
        await database.ExecuteAsync(Call(root.Project, Column(root), root.Id, 1, 1, 0, 3000, state: "Failed", final: false, finishReason: "error", prompt: 80, completion: null));
        await database.ExecuteAsync(Call(root.Project, Column(root), root.Id, 2, 2, 10_000, 5000));

        var usage = await MetricsHost.ContextUsageAsync(pm, root.Project);

        var last = usage.GetProperty("last");
        Assert.Equal(1, last.GetProperty("metrics").GetProperty("calls").GetInt32()); // le metriche restano quelle del solo vincente
        var attempts = Attempts(usage).EnumerateArray().ToList();
        Assert.Equal([1, 2], attempts.Select(a => a.GetProperty("attempt").GetInt32()));
        Assert.Equal(["failed", "winning"], attempts.Select(a => a.GetProperty("outcome").GetString()));
        Assert.Equal(["failed", "completed"], attempts.Select(a => a.GetProperty("state").GetString()));
        Assert.Equal("error", attempts[0].GetProperty("finishReason").GetString());
        Assert.Equal(3000, attempts[0].GetProperty("activeMs").GetInt64());
        Assert.Equal(80, attempts[0].GetProperty("promptTokens").GetProperty("value").GetInt32());
        Assert.Equal("measured", attempts[0].GetProperty("promptTokens").GetProperty("source").GetString());
        Assert.Equal("unavailable", attempts[0].GetProperty("completionTokens").GetProperty("source").GetString());
        Assert.Equal(5000, attempts[1].GetProperty("activeMs").GetInt64());
        Assert.InRange(last.GetProperty("execution").GetProperty("elapsedMs").GetInt64(), 59_000, 66_000); // t0 resta autorevole
    }

    [Fact]
    public async Task A_crash_then_resume_shows_the_unrecorded_attempt_as_unavailable_and_the_interrupted_one_without_invented_time()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var (host, pm, root, _) = await StartAsync(RootKind.Run, 3, database);
        await using var _h = host;
        using var _p = pm;
        // Tentativo 1: lease scaduto prima di qualsiasi scrittura (nessuna riga). Tentativo 2: chiamata interrotta senza durata né token.
        // Tentativo 3 (ripresa): vincente.
        await database.ExecuteAsync(Call(root.Project, Column(root), root.Id, 2, 2, 5_000, null, state: "Interrupted", final: false, phase: "Analysis", prompt: null, completion: null, finishReason: "x"));
        await database.ExecuteAsync(Call(root.Project, Column(root), root.Id, 3, 3, 20_000, 4000, phase: "Analysis"));
        await DocumentExecutionIntervalsTests.SeedIntervalAsync(database, root, "Recovery", 1, 12, 19);
        await DocumentExecutionIntervalsTests.SeedIntervalAsync(database, root, "Recovery", 2, 25, 28);

        var usage = await MetricsHost.ContextUsageAsync(pm, root.Project);

        var attempts = Attempts(usage).EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], attempts.Select(a => a.GetProperty("attempt").GetInt32()));
        Assert.Equal(["unrecorded", "interrupted", "winning"], attempts.Select(a => a.GetProperty("outcome").GetString()));
        Assert.Equal(0, attempts[0].GetProperty("calls").GetInt32());
        Assert.Equal(JsonValueKind.Null, attempts[0].GetProperty("activeMs").ValueKind);   // mai zero inventato
        Assert.Equal(JsonValueKind.Null, attempts[0].GetProperty("model").ValueKind);
        Assert.Equal(JsonValueKind.Null, attempts[0].GetProperty("state").ValueKind);
        Assert.Equal(JsonValueKind.Null, attempts[1].GetProperty("activeMs").ValueKind);
        Assert.Equal("interrupted", attempts[1].GetProperty("state").GetString());
        Assert.Equal(4000, attempts[2].GetProperty("activeMs").GetInt64());
        // Il recupero resta un tempo distinto dall'attività e non si somma ad essa.
        var execution = usage.GetProperty("last").GetProperty("execution");
        Assert.Equal(10_000, execution.GetProperty("recoveryMs").GetInt64());
    }

    [Fact]
    public async Task Concurrent_calls_of_the_same_attempt_are_united_and_other_attempts_are_not_added()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var (host, pm, root, _) = await StartAsync(RootKind.Turn, 2, database);
        await using var _h = host;
        using var _p = pm;
        await database.ExecuteAsync(Call(root.Project, Column(root), root.Id, 1, 1, 0, 10_000, state: "Failed", final: false, finishReason: "error"));
        await database.ExecuteAsync(Call(root.Project, Column(root), root.Id, 1, 1, 5_000, 10_000, state: "Failed", final: false, finishReason: "error")); // sovrapposta alla prima
        await database.ExecuteAsync(Call(root.Project, Column(root), root.Id, 2, 2, 8_000, 6_000));                                                          // sovrapposta nel tempo ma altro tentativo

        var attempts = Attempts(await MetricsHost.ContextUsageAsync(pm, root.Project)).EnumerateArray().ToList();

        Assert.Equal(2, attempts[0].GetProperty("calls").GetInt32());
        Assert.Equal(15_000, attempts[0].GetProperty("activeMs").GetInt64()); // unione, non 20 s
        Assert.Equal(6_000, attempts[1].GetProperty("activeMs").GetInt64());
    }

    [Fact]
    public async Task The_model_fingerprint_comes_from_the_call_not_from_the_configuration_or_the_runtime_monitor()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var (host, pm, root, _) = await StartAsync(RootKind.Turn, 2, database);
        await using var _h = host;
        using var _p = pm;
        await database.ExecuteAsync(Call(root.Project, Column(root), root.Id, 1, 1, 0, 1000, state: "Failed", final: false, model: "modello-primo", window: 16384));
        await database.ExecuteAsync(Call(root.Project, Column(root), root.Id, 2, 2, 5_000, 2000, model: "modello-effettivo", window: 8192));

        var usage = await MetricsHost.ContextUsageAsync(pm, root.Project);

        var configured = usage.GetProperty("runtime").GetProperty("modelId").GetString();
        var fingerprint = usage.GetProperty("last").GetProperty("modelFingerprint");
        Assert.Equal("modello-effettivo", fingerprint.GetProperty("modelId").GetString());
        Assert.NotEqual(configured, fingerprint.GetProperty("modelId").GetString());
        Assert.Equal(8192, fingerprint.GetProperty("contextWindowTokens").GetProperty("value").GetInt32());
        var hash = fingerprint.GetProperty("hash").GetString()!;
        Assert.Matches("^[0-9a-f]{16}$", hash);

        var attempts = Attempts(usage).EnumerateArray().ToList();
        Assert.Equal("modello-primo", attempts[0].GetProperty("model").GetProperty("modelId").GetString());
        Assert.NotEqual(hash, attempts[0].GetProperty("model").GetProperty("hash").GetString());
        Assert.Equal(hash, attempts[1].GetProperty("model").GetProperty("hash").GetString());
        // Il campo storico non muta con la finestra corrente: stesso modello con finestra diversa dà un'impronta diversa.
        Assert.Equal(Expected("modello-effettivo", 8192), hash);
        Assert.NotEqual(Expected("modello-effettivo", 4096), hash);
    }

    [Fact]
    public async Task A_historical_row_without_window_has_an_unavailable_window_and_a_single_attempt_never_an_invented_one()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var (host, pm, root, _) = await StartAsync(RootKind.Turn, 1, database);
        await using var _h = host;
        using var _p = pm;
        await database.ExecuteAsync(Call(root.Project, Column(root), root.Id, 1, 1, 0, null, window: null, prompt: null, completion: null));

        var usage = await MetricsHost.ContextUsageAsync(pm, root.Project);

        var fingerprint = usage.GetProperty("last").GetProperty("modelFingerprint");
        Assert.Equal("unavailable", fingerprint.GetProperty("contextWindowTokens").GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, fingerprint.GetProperty("contextWindowTokens").GetProperty("value").ValueKind);
        var attempt = Assert.Single(Attempts(usage).EnumerateArray());
        Assert.Equal("winning", attempt.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, attempt.GetProperty("activeMs").ValueKind);
        Assert.Equal("unavailable", attempt.GetProperty("promptTokens").GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_document_job_lists_only_its_own_attempts_and_never_those_of_sibling_jobs_and_nothing_sensitive_leaks()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var host = await MetricsHost.StartAsync(database.ConnectionString, new CannedLlamaServer(), MetricsHost.CannedOptions(), chat: false, formal: false);
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        var user = Guid.Parse(await database.ScalarAsync<string>($"""SELECT "CreatedBy"::text FROM "Projects" WHERE "Id" = '{project}'"""));
        var parse = await database.SeedParseAsync(owner: (user, project));
        var (job, sibling) = (Guid.NewGuid(), Guid.NewGuid());
        const string secrets = "http://dgx.interno:8080/v1 Bearer sk-segretissimo prompt: DOCUMENTO RISERVATO";
        foreach (var (id, count) in new[] { (job, 2), (sibling, 3) })
        {
            await database.ExecuteAsync($"""
                INSERT INTO "DocumentJobs" ("Id","ProjectId","PreparationId","Kind","UnitId","Status","RequestedBy","RequestedAt","DeadlineAt","AttemptCount","MaxAttempts","FencingToken","ErrorMessage")
                VALUES ('{id}','{project}','{parse.Preparation}','ExtractUnit','{Guid.NewGuid()}','Running','{user}',now(),now() + interval '1 hour',{count},3,{count},'{secrets}')
                """);
        }

        await database.ExecuteAsync(Call(project, "DocumentJobId", job, 1, 1, 0, 2000, state: "Failed", final: false, phase: "Document", finishReason: "error"));
        await database.ExecuteAsync(Call(project, "DocumentJobId", job, 2, 2, 6_000, 3000, phase: "Document"));
        await database.ExecuteAsync(Call(project, "DocumentJobId", sibling, 1, 7, 100_000, 500, state: "Failed", final: false, phase: "Document"));
        await database.ExecuteAsync(Call(project, "DocumentJobId", sibling, 3, 8, 200_000, 500, final: false, phase: "Document")); // chiamata completa ma non vincente: rifiutata dal validatore

        var usage = await MetricsHost.ContextUsageAsync(pm, project);

        var last = usage.GetProperty("last");
        Assert.Equal(job.ToString(), last.GetProperty("refId").GetString());
        var attempts = Attempts(usage).EnumerateArray().ToList();
        Assert.Equal(["failed", "winning"], attempts.Select(a => a.GetProperty("outcome").GetString()));

        var text = usage.GetRawText();
        foreach (var forbidden in new[] { "dgx.interno", "sk-segretissimo", "Bearer", "RISERVATO", "prompt:", "errorMessage" })
        {
            Assert.DoesNotContain(forbidden, text);
        }

        Assert.Equal(
            ["activeMs", "attempt", "calls", "completionTokens", "finishReason", "model", "outcome", "promptTokens", "state"],
            attempts[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["contextWindowTokens", "hash", "modelId"], attempts[1].GetProperty("model").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_completed_non_final_attempt_is_rejected_and_not_confused_with_the_winner()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var (host, pm, root, _) = await StartAsync(RootKind.Turn, 2, database);
        await using var _h = host;
        using var _p = pm;
        await database.ExecuteAsync(Call(root.Project, Column(root), root.Id, 1, 1, 0, 1500, final: false));
        await database.ExecuteAsync(Call(root.Project, Column(root), root.Id, 2, 2, 5_000, 1500));

        var attempts = Attempts(await MetricsHost.ContextUsageAsync(pm, root.Project)).EnumerateArray().ToList();

        Assert.Equal(["rejected", "winning"], attempts.Select(a => a.GetProperty("outcome").GetString()));
    }
}
