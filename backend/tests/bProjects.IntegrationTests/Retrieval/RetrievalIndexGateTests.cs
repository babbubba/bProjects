using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>
/// Strumentazione diagnostica di T040 (003), verificata offline con server finti, senza DGX: readiness (sleeping, modello assente, 401, irraggiungibile) con codici HTTP, errori di embedding con
/// <c>ErrorCode</c> e messaggio per preparazione, conservazione anche con eccezione, rapporto delle chiamate di Analysis (stadio, vista, hash, token, finish, violazione degli items), gate bloccante.
/// </summary>
public sealed class RetrievalIndexGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sleeping(string loaded) => loaded.Replace("\"value\":\"loaded\"", "\"value\":\"sleeping\"", StringComparison.Ordinal);

    private static string TempFile(string name) => Path.Combine(Path.GetTempPath(), "bprojects-gate-tests", Guid.NewGuid().ToString("N"), name);

    private static RetrievalOptions Options() => new()
    {
        Enabled = true,
        RuntimeCheckSeconds = 60,
        Embedding = { Endpoint = "http://dgx.internal:8081/v1/", ApiKey = "chiave-segreta-di-prova" },
    };

    // ---------------------------------------------------------------- readiness: stato, endpoint logico, codice HTTP

    public static TheoryData<string, string, int, string> ReadinessCases() => new()
    {
        { "sleeping", "ModelUnloaded", 200, "" },
        { "model-missing", "ModelMissing", 200, "" },
        { "unauthorized", "Unverified", 401, "" },
        { "unreachable", "Unreachable", 0, "HttpRequestException" },
    };

    [Theory]
    [MemberData(nameof(ReadinessCases))]
    public async Task Readiness_snapshots_record_state_logical_endpoint_and_the_http_outcome_without_any_secret(string kind, string expectedState, int expectedStatus, string expectedException)
    {
        var server = new FakeEmbeddingServer
        {
            Models = kind switch
            {
                "sleeping" => () => FakeEmbeddingServer.Json(Sleeping(FakeEmbeddingServer.ModelsLoaded)),
                "model-missing" => () => FakeEmbeddingServer.Json("{\"object\":\"list\",\"data\":[]}"),
                "unauthorized" => () => FakeEmbeddingServer.Json("{}", HttpStatusCode.Unauthorized),
                _ => () => throw new HttpRequestException("rete assente"),
            },
        };
        var recorder = new RetrievalCallRecorder { Phase = "index" };
        var options = Options();
        using var embedding = new EmbeddingClient(options, recorder.NewHandler(server));
        using var readiness = new RetrievalModelReadiness(options, embedding, recorder.NewHandler(server));

        var records = await RetrievalIndexGate.CaptureAsync(readiness, options, "prima", probe: true, Ct);
        var http = RetrievalIndexGate.HttpSummary(recorder.Calls);
        var diagnostics = new IndexDiagnostics(() => "t") { Http = http };
        diagnostics.Readiness.AddRange(records);
        var path = TempFile("report.json");
        diagnostics.Persist(path);

        var embeddingRecord = records.Single(r => r.Role == "embedding");
        Assert.Equal(expectedState, embeddingRecord.State);
        Assert.Equal("dgx.internal:8081/v1/", embeddingRecord.LogicalEndpoint);
        var first = recorder.Calls.First();
        Assert.Equal("/v1/models", first.Path);
        Assert.Equal(expectedStatus, first.Status);
        Assert.Equal(expectedException.Length == 0 ? null : expectedException, first.Exception);
        var text = await File.ReadAllTextAsync(path, Ct);
        Assert.DoesNotContain("chiave-segreta-di-prova", text, StringComparison.Ordinal);   // nessuna chiave nel file
        Assert.Contains(expectedState, text, StringComparison.Ordinal);
        Assert.Equal(0, server.EmbeddingRequests);                                            // sola osservazione: nessuna richiesta di embedding né di risveglio
    }

    // ---------------------------------------------------------------- indicizzazione: esito per preparazione con codice e messaggio

    [Fact]
    [Trait("Category", TestCategories.Integration)]
    public async Task A_sleeping_embedding_model_fails_every_preparation_at_once_and_the_report_keeps_code_message_and_state()
    {
        await using var database = await RetrievalDb.CreateAsync();
        var server = new FakeEmbeddingServer { Models = () => FakeEmbeddingServer.Json(Sleeping(FakeEmbeddingServer.ModelsLoaded)) };
        using var rig = new RetrievalRig(server);
        var project = await database.SeedProjectAsync();
        await database.AddDocumentAsync(project, "Contratto", ["Il termine di pagamento è di trenta giorni.", "La penale è del cinque per cento."]);
        await database.AddDocumentAsync(project, "Allegato", ["Il canone annuo è fissato in euro."]);
        await using var db = database.NewContext();
        var diagnostics = new IndexDiagnostics(() => "t");
        var path = TempFile("report.json");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var summary = await RetrievalIndexGate.RebuildAsync(() => new RetrievalIndexMaintenance(db, rig.Builder(db)).RebuildAsync(false, Ct), diagnostics, path);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));   // nessuna attesa né retry
        Assert.Equal((2, 0, 2), (summary.Preparations, summary.Ready, summary.Failed));
        Assert.All(summary.Results!, r =>
        {
            Assert.Equal(RetrievalException.EmbeddingUnavailable, r.ErrorCode);
            Assert.Contains("ModelUnloaded", r.ErrorMessage, StringComparison.Ordinal);   // lo stato del modello è nel messaggio tipizzato
        });
        // Politica on-demand: UNA sola richiesta funzionale minima di attivazione per tutte le preparazioni (nessuna ripetizione); il fake non carica il modello, quindi resta non pronto.
        Assert.Equal(1, rig.Server.EmbeddingRequests);
        Assert.Equal(["probe"], Assert.Single(rig.Server.EmbeddingInputs));
        Assert.All(summary.Results!, r => Assert.Contains("Attivazione on-demand tentata", r.ErrorMessage, StringComparison.Ordinal));
        Assert.False(RetrievalIndexGate.IsComplete(summary, 2));
        var report = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!;
        Assert.Equal(2, report["rebuild"]!["results"]!.AsArray().Count);
        Assert.Equal("embedding_unavailable", report["rebuild"]!["results"]![0]!["errorCode"]!.ToString());
        Assert.DoesNotContain("trenta giorni", report.ToJsonString(), StringComparison.Ordinal);   // nessun testo di documento nel rapporto
        Assert.Equal(2L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexStates\" WHERE \"Status\" = 'Failed' AND \"ErrorCode\" = 'embedding_unavailable'"));
    }

    [Theory]
    [Trait("Category", TestCategories.Integration)]
    [InlineData(400, "{}", RetrievalException.EmbeddingInputTooLong)]
    [InlineData(500, "{}", RetrievalException.EmbeddingUnavailable)]
    [InlineData(200, "{\"data\":[]}", RetrievalException.EmbeddingMalformedResponse)]
    public async Task Embedding_failures_of_a_loaded_model_are_persisted_with_their_typed_code_message_and_http_status(int status, string body, string expectedCode)
    {
        await using var database = await RetrievalDb.CreateAsync();
        var server = new FakeEmbeddingServer
        {
            // La sonda funzionale di readiness («probe») resta valida: il guasto riguarda solo le richieste di contenuto dell'indicizzazione.
            EmbeddingsOverride = inputs => inputs is ["probe"] ? FakeEmbeddingServer.Json(FakeEmbeddingServer.EmbeddingsBody([FakeEmbeddingServer.VectorOf("probe", 1024)])) : FakeEmbeddingServer.Json(body, (HttpStatusCode)status),
        };
        using var rig = new RetrievalRig(server);
        var project = await database.SeedProjectAsync();
        await database.AddDocumentAsync(project, "Contratto", ["Il termine di pagamento è di trenta giorni."]);
        await using var db = database.NewContext();
        var diagnostics = new IndexDiagnostics(() => "t");
        var path = TempFile("report.json");

        var summary = await RetrievalIndexGate.RebuildAsync(() => new RetrievalIndexMaintenance(db, rig.Builder(db)).RebuildAsync(false, Ct), diagnostics, path);

        var result = Assert.Single(summary.Results!);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.DoesNotContain("trenta giorni", result.ErrorMessage, StringComparison.Ordinal);
        Assert.False(RetrievalIndexGate.IsComplete(summary, 1));
        var written = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!;
        Assert.Equal(expectedCode, written["rebuild"]!["results"]![0]!["errorCode"]!.ToString());
    }

    [Fact]
    public async Task An_exception_during_the_rebuild_is_persisted_with_its_type_only_and_then_rethrown()
    {
        var diagnostics = new IndexDiagnostics(() => "t");
        var path = TempFile("report.json");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RetrievalIndexGate.RebuildAsync(() => throw new InvalidOperationException("connessione persa"), diagnostics, path));

        var report = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!;
        Assert.Equal("System.InvalidOperationException", report["exception"]!["type"]!.ToString());
        Assert.Null(report["exception"]!["message"]);   // eccezione non tipizzata: solo il tipo, il messaggio potrebbe contenere dati
        Assert.Null(report["rebuild"]);
    }

    // ---------------------------------------------------------------- gate

    [Fact]
    public void The_gate_never_declares_an_empty_partial_or_detail_less_indexing_complete()
    {
        var ok = new RetrievalIndexBuildResult(Guid.NewGuid(), new string('a', 64), RetrievalIndexStatus.Ready, 3, 3, null, 3);
        var failed = ok with { Status = RetrievalIndexStatus.Failed, EntryCount = 0, ErrorCode = RetrievalException.EmbeddingUnavailable, ErrorMessage = "stato ModelUnloaded" };

        Assert.True(RetrievalIndexGate.IsComplete(new(1, 1, 0, 0, [ok]), 1));
        Assert.False(RetrievalIndexGate.IsComplete(new(0, 0, 0, 0, []), 0));
        Assert.False(RetrievalIndexGate.IsComplete(new(2, 1, 1, 0, [ok, failed]), 2));
        Assert.False(RetrievalIndexGate.IsComplete(new(1, 1, 0, 0, [ok]), 2));
        Assert.False(RetrievalIndexGate.IsComplete(new(1, 1, 0, 0, null), 1));
        Assert.Contains("ErrorCode=embedding_unavailable", RetrievalIndexGate.Describe(failed), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_indexing_is_a_blocking_failure_of_the_campaign_so_the_second_corpus_never_starts()
    {
        var failure = new Xunit.Sdk.XunitException("T040-single: 1 verifiche fallite: " + RetrievalGapStageMafRealTests.IndexCheckLabel);   // etichetta REALE del check

        Assert.True(CampaignSequencer.IsBlocking(failure));
    }

    // ---------------------------------------------------------------- rapporto delle chiamate al modello (Analysis compresa)

    private static void WriteCall(string dir, string id, string user, string assistant, string finish = "stop", int completion = 100, string system = "SISTEMA", string secret = "")
    {
        var body = new JsonObject
        {
            ["model"] = "Qwen3.6-35B-Smart", ["temperature"] = 0.1, ["max_completion_tokens"] = 16384,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = user }),
            ["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = "reconcile_alias", ["schema"] = new JsonObject { ["type"] = "object" } } },
        };
        File.WriteAllText(Path.Combine(dir, $"{id}-request.json"), new JsonObject
        {
            ["id"] = id, ["startedAt"] = "2026-11-10T22:00:00Z", ["phaseGuess"] = "structured", ["headers"] = new JsonObject { ["X-Test"] = secret }, ["body"] = body,
        }.ToJsonString());
        File.WriteAllText(Path.Combine(dir, $"{id}-result.json"), new JsonObject
        {
            ["id"] = id, ["outcome"] = "completed", ["httpStatus"] = 200, ["totalMs"] = 1234, ["finishReason"] = finish,
            ["usage"] = new JsonObject { ["prompt_tokens"] = 500, ["completion_tokens"] = completion },
        }.ToJsonString());
        File.WriteAllText(Path.Combine(dir, $"{id}-assistant.txt"), assistant);
    }

    private static string Output(int items, int candidates = 0) => new JsonObject
    {
        ["contract"] = "reconcile-alias-1", ["descriptor"] = "d", ["noFindings"] = null,
        ["items"] = new JsonArray([.. Enumerable.Range(1, items).Select(i => (JsonNode)new JsonObject { ["key"] = $"i{i}", ["section"] = "requirements", ["text"] = "t", ["classification"] = "Documented", ["evidence"] = new JsonArray("E1") })]),
        ["candidates"] = new JsonArray([.. Enumerable.Range(1, candidates).Select(_ => (JsonNode)new JsonObject())]), ["focus"] = null, ["patches"] = new JsonArray(),
    }.ToJsonString();

    [Fact]
    public void The_call_report_gives_stage_view_hashes_tokens_finish_and_flags_items_outside_the_leaf_A_without_leaking_headers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bprojects-gate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        WriteCall(dir, "001", "Tipo di batch: foglia\nVista: A\n<<<VOCE [E1] origine=Document tipo=Requirement polarita=Affirmed>>>\nx", Output(items: 3), secret: "SEGRETO-IN-INTESTAZIONE");
        WriteCall(dir, "002", "Tipo di batch: foglia\nVista: B\n<<<VOCE [E1] origine=Document tipo=Requirement polarita=Affirmed>>>\nx<<<VOCE [E2] origine=Document tipo=Requirement polarita=Affirmed>>>\ny", Output(items: 139), completion: 10587);
        WriteCall(dir, "003", "Tipo di batch: foglia\nVista: B\n", Output(items: 0, candidates: 3));
        WriteCall(dir, "004", "Tipo di batch: riduzione\nVista: A\n", "{\"contract\":\"reconcile-alias-1\",\"items\":[", finish: "length", completion: 16384);

        var calls = AnalysisCallReport.Build(dir);

        Assert.Equal(4, calls.Count);
        Assert.Equal(("leaf", "A", false), (calls[0]!["batchStage"]!.ToString(), calls[0]!["batchView"]!.ToString(), calls[0]!["itemsOutsideLeafA"]!.GetValue<bool>()));
        Assert.Equal(("leaf", "B", true), (calls[1]!["batchStage"]!.ToString(), calls[1]!["batchView"]!.ToString(), calls[1]!["itemsOutsideLeafA"]!.GetValue<bool>()));
        Assert.Equal(139, calls[1]!["itemsCount"]!.GetValue<int>());
        Assert.Equal(2, calls[1]!["entriesInBatch"]!.GetValue<int>());
        Assert.Equal(10587, calls[1]!["completionTokens"]!.GetValue<int>());
        Assert.Null(calls[2]!["itemsOutsideLeafA"]);                          // nessun item: nessuna violazione
        Assert.Equal(3, calls[2]!["candidatesCount"]!.GetValue<int>());
        Assert.Equal("length", calls[3]!["finishReason"]!.ToString());
        Assert.False(calls[3]!["jsonParsable"]!.GetValue<bool>());           // output troncato: non parsabile
        Assert.Equal("reduce", calls[3]!["batchStage"]!.ToString());
        Assert.Equal(calls[0]!["systemPromptSha256"]!.ToString(), calls[1]!["systemPromptSha256"]!.ToString());   // stesso prompt = stesso hash
        Assert.Equal(64, calls[0]!["schemaSha256"]!.ToString().Length);
        Assert.DoesNotContain("SEGRETO-IN-INTESTAZIONE", calls.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_call_report_of_a_missing_archive_is_empty_and_never_throws()
    {
        Assert.Empty(AnalysisCallReport.Build(Path.Combine(Path.GetTempPath(), "bprojects-gate-tests", "assente-" + Guid.NewGuid().ToString("N"))));
    }
}
