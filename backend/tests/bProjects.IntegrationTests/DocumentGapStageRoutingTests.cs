using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// Run Analizza v5 STORICO: creato dal vero endpoint (oggi v6, D1) e riportato alla definizione e alle versioni che l'endpoint congelava dal T141 al T148, come un run richiesto allora.
/// Solo fixture: nessun codice di produzione e nessuna asserzione dei test sul comportamento v5 cambia.
/// </summary>
internal static class HistoricalAnalysisV5
{
    public const string Definition = "project-analysis-documents-v5";

    public const string Versions =
        """{"analysis":"document-analysis-v6","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","analysisOutputPolicy":"single-call-output-estimate-1","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v3","reconcile":"document-reconcile-v3","reconcileAiSchema":"reconcile-alias-1","reconcileAlgorithm":"global-reconcile-v1","reconcileCatalog":"reconcile-alias-catalog-1","reconcileGapPolicy":"hierarchical-gap-incomplete-1"}""";

    public static string Sql => "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = '" + Definition + "', \"PromptVersions\" = @v::jsonb WHERE \"Id\" = @r";
}

/// <summary>
/// 002 T148 (DET) — <see cref="DocumentAnalysisWorkflow.ContractOf"/> per la definizione v6 (AI §13.7): 6 solo con TUTTE le chiavi del single-call v6, della gerarchia v4 e dello stadio Gap e con
/// `reconcileGapPolicy` assente; il 5 resta invariato e rifiuta le chiavi `gap*`; ogni combinazione mista è `null` (workflow_incompatible), mai reinterpretata.
/// </summary>
public sealed class DocumentAnalysisContractV6Tests
{
    private static ProcessingRun Run(Dictionary<string, string> keys, string definition = "project-analysis-documents-v6") =>
        new() { WorkflowDefinitionVersion = definition, MafVersion = "1.26.0", PromptVersions = JsonSerializer.Serialize(keys) };

    private static Dictionary<string, string> V6() => AnalysisV6Fixture.Keys();

    private static Dictionary<string, string> V5() => JsonSerializer.Deserialize<Dictionary<string, string>>(HistoricalAnalysisV5.Versions)!;

    [Fact]
    public void A_v6_run_with_every_key_is_contract_6_and_the_freeze_of_a_new_request_matches_exactly()
    {
        Assert.Equal(6, DocumentAnalysisWorkflow.ContractOf(Run(V6())));
        var frozen = JsonSerializer.Deserialize<Dictionary<string, string>>(SourceSnapshotStore.AnalysisPromptVersionsV6())!;
        var withFormat = V6();
        withFormat["analysisMessageFormat"] = "message-format-2";   // D2 (T151): le nuove richieste v6 congelano anche il formato del messaggio
        Assert.Equal(withFormat.OrderBy(k => k.Key, StringComparer.Ordinal), frozen.OrderBy(k => k.Key, StringComparer.Ordinal));   // né più né meno
        Assert.Equal(19, frozen.Count);
        Assert.DoesNotContain("reconcileGapPolicy", frozen.Keys);
        Assert.Equal("project-analysis-documents-v6", SourceSnapshotStore.AnalysisDefinitionV6);
        Assert.Equal(6, DocumentAnalysisWorkflow.ContractOf(Run(frozen)));
    }

    [Theory]
    [InlineData("gapStage")]
    [InlineData("gapPrompt")]
    [InlineData("gapAiSchema")]
    [InlineData("gapMessageFormat")]
    [InlineData("gapPolicy")]
    [InlineData("gapEstimate")]
    [InlineData("analysisOutputPolicy")]
    [InlineData("analysis")]
    [InlineData("analysisSchema")]
    [InlineData("analysisAiSchema")]
    [InlineData("aliasCatalog")]
    [InlineData("canonIds")]
    [InlineData("algorithm")]
    [InlineData("validation")]
    [InlineData("reconcile")]
    [InlineData("reconcileAiSchema")]
    [InlineData("reconcileAlgorithm")]
    [InlineData("reconcileCatalog")]
    public void A_v6_run_missing_any_single_key_is_incompatible(string key)
    {
        var keys = V6();
        keys.Remove(key);
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Theory]
    [InlineData("gapStage", "gap-stage-2")]
    [InlineData("gapPrompt", "document-gap-v2")]
    [InlineData("gapAiSchema", "gap-stage-alias-2")]
    [InlineData("gapMessageFormat", "gap-stage-message-2")]
    [InlineData("gapPolicy", "gap-stage-ledger-2")]
    [InlineData("gapEstimate", "gap-stage-estimate-2")]
    [InlineData("analysis", "document-analysis-v5")]
    [InlineData("analysisOutputPolicy", "single-call-output-estimate-2")]
    [InlineData("reconcile", "document-reconcile-v4")]
    [InlineData("reconcileAiSchema", "reconcile-alias-2")]
    [InlineData("reconcileAlgorithm", "global-reconcile-v2")]
    public void A_v6_run_with_a_different_value_is_incompatible_and_never_reinterpreted(string key, string value)
    {
        var keys = V6();
        keys[key] = value;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Fact]
    public void The_v5_gap_policy_cannot_coexist_with_the_v6_keys_and_a_v6_without_frozen_versions_is_not_comparable()
    {
        var mixed = V6();
        mixed["reconcileGapPolicy"] = "hierarchical-gap-incomplete-1";
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(mixed)));                                            // v6 con la policy v5
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(new ProcessingRun { WorkflowDefinitionVersion = "project-analysis-documents-v6", MafVersion = "1.26.0", PromptVersions = null }));
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(new ProcessingRun { WorkflowDefinitionVersion = "project-analysis-documents-v6", MafVersion = "1.25.0", PromptVersions = JsonSerializer.Serialize(V6()) }));
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V6(), "project-analysis-documents-v7")));
    }

    [Fact]
    public void A_v5_run_is_unchanged_and_rejects_any_gap_key_while_v4_and_older_definitions_reject_them_too()
    {
        Assert.Equal(5, DocumentAnalysisWorkflow.ContractOf(Run(V5(), "project-analysis-documents-v5")));
        var historic = JsonSerializer.Deserialize<Dictionary<string, string>>(SourceSnapshotStore.AnalysisPromptVersions())!;
        Assert.Equal(5, DocumentAnalysisWorkflow.ContractOf(Run(historic, "project-analysis-documents-v5")));   // le chiavi congelate dalle richieste v5 (T141) restano v5

        foreach (var (key, value) in GapStage.FreezeKeys)
        {
            var mixed = V5();
            mixed[key] = value;
            Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(mixed, "project-analysis-documents-v5")));    // v5 con una chiave gap*: mai reinterpretata
        }

        var v5WithoutPolicy = V5();
        v5WithoutPolicy.Remove("reconcileGapPolicy");
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v5WithoutPolicy, "project-analysis-documents-v5")));
        // Le chiavi v6 su una definizione v5 o v4 non promuovono nulla.
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V6(), "project-analysis-documents-v5")));
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V6(), "project-analysis-documents-v4")));
    }
}

/// <summary>
/// 002 T148 (MAF-PG, modello FINTO) — instradamento (a)/(b1)/(b2) della definizione v6 (AI §13.7): single-call invariato, fail-fast tipizzato a ZERO invii del controllo (1) prima di ogni chiamata,
/// gerarchia v4-equivalente senza stadio per l'analisi senza Gap, v5 e storico invariati. Le nuove richieste congelano la v6 dal POST reale (nessuna riscrittura). Prove simulate: dimostrano
/// protocollo e confini, non la qualità di un modello reale.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentGapStageRoutingTests : ReconcileRig
{
    private static string Statement(int n, int words) => $"Requisito {n}: " + string.Join(' ', Enumerable.Range(0, words).Select(i => $"w{n}x{i}"));

    /// <summary>Corpus con <paramref name="gaps"/> documenti Gap (i primi) e il resto Requirement; il run è creato dal vero POST (v6).</summary>
    private async Task<(Guid Run, AnalysisRunClaim Claim, Guid Project)> ArrangeAsync(
        int documents, int gaps, int words, HttpClient? existing = null, Guid? existingProject = null)
    {
        using var owned = existing is null ? await App.LoginAsync() : null;
        var client = existing ?? owned!;
        var project = existingProject ?? await NewProject(client);
        if (existingProject is null)
        {
            for (var n = 1; n <= documents; n++)
            {
                await NewDocument(client, project, $"doc{n}", Statement(n, words), n == 1 ? "aaa" : $"mmm{n}", kind: n <= gaps ? EvidenceKind.Gap : EvidenceKind.Requirement);
            }
        }

        var run = await StartNativeAsync(client, project);
        return (run, await ClaimAsync(run), project);
    }

    private async Task<Guid> StartNativeAsync(HttpClient client, Guid project)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<(string Status, string Code, string Message, int Calls)> RunRow(Guid run) => (
        await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<string>("SELECT coalesce(\"ErrorCode\",'') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<string>("SELECT coalesce(\"ErrorMessage\",'') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<int>("SELECT coalesce(\"CallCount\",0) FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));

    private async Task AssertNothingProducedAsync(Guid run)
    {
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    private static DocumentChatWorkflowTests.FakeChatModel Tripwire(int window = DocumentChatWorkflowTests.FakeChatModel.Window) =>
        new() { ContextWindow = window, Responder = _ => throw new InvalidOperationException("il fail-fast non deve invocare il modello") };

    // ================================================================ freeze alla richiesta

    [Fact]
    public async Task The_real_analyze_endpoint_freezes_the_v6_definition_with_the_single_call_hierarchy_and_gap_stage_keys_and_no_v5_policy()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement(1, 10));

        var run = await StartNativeAsync(client, project);

        Assert.Equal("project-analysis-documents-v6", await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        var frozen = JsonSerializer.Deserialize<Dictionary<string, string>>(await Scalar<string>("SELECT \"PromptVersions\"::text FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)))!;
        // D2 (T151): le NUOVE richieste v6 congelano in più `analysisMessageFormat`; `Keys()` resta il v6 storico senza la chiave (formato 1, ancora valido).
        var expected = AnalysisV6Fixture.Keys();
        expected["analysisMessageFormat"] = "message-format-2";
        Assert.Equal(expected.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}"), frozen.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}"));
        Assert.DoesNotContain("reconcileGapPolicy", frozen.Keys);
        Assert.Equal("document-analysis-v6", frozen["analysis"]);
        Assert.Equal("document-analysis-alias-2", frozen["analysisAiSchema"]);
        Assert.Equal("gap-stage-1", frozen["gapStage"]);
        Assert.Equal("document-gap-v1", frozen["gapPrompt"]);
        using var scope = App.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<bProjects.App.Persistence.AppDbContext>().ProcessingRuns.FindAsync([run], Ct);
        Assert.Equal(6, DocumentAnalysisWorkflow.ContractOf(row!));
    }

    // ================================================================ (b1) fail-fast del controllo (1): zero invii, nulla prodotto

    [Fact]
    public async Task A_ledger_beyond_the_stage_input_budget_fails_fast_before_any_call_even_the_hierarchy_with_the_typed_reason()
    {
        var (run, claim, _) = await ArrangeAsync(documents: 8, gaps: 2, words: 120);
        var model = Tripwire();

        var result = await RunAsync(claim, model);

        Assert.Equal((ProcessingRunStatus.Failed, "document_limit_exceeded"), (result.Status, result.ErrorCode));
        Assert.Empty(model.Requests);                                                          // zero invii in TUTTO il run, gerarchia compresa
        var row = await RunRow(run);
        Assert.Equal(("Failed", "document_limit_exceeded", 0), (row.Status, row.Code, row.Calls));   // CallCount invariato: nessuna riserva
        Assert.StartsWith("gap_ledger_exceeds_input: ", row.Message, StringComparison.Ordinal);
        Assert.Matches(@"\d+ token", row.Message);                    // token misurati
        Assert.Matches(@"budget di input[^.]*\(\d+ token\)", row.Message);   // budget di input
        Assert.Matches(@"(?i)qualit", row.Message);                   // qualità della misura
        Assert.Matches(@"\b2 evidenze di tipo Gap", row.Message);     // numero di Gap
        Assert.True(row.Message.Length <= 1000, row.Message.Length.ToString());
        Assert.DoesNotContain("hierarchical_gap_assessment_unsupported", row.Message, StringComparison.Ordinal);   // il motivo v5 non compare mai in v6
        await AssertNothingProducedAsync(run);
    }

    [Fact]
    public async Task Gap_assessments_beyond_the_output_budget_fail_fast_with_zero_calls_and_just_inside_the_boundary_the_run_completes_with_one_stage_call()
    {
        // Finestra 6000 / output 1500 / margine 20: l'input dello stadio entra sempre (ledger minuscolo), conta il solo budget di output O = 1500 (stima 1425 a 4 Gap, 1750 a 5 Gap).
        var (over, overClaim, _) = await ArrangeAsync(documents: 7, gaps: 5, words: 4);
        var tripwire = Tripwire(6000);
        var failed = await RunAsync(overClaim, tripwire, contextWindow: 6000);

        Assert.Equal((ProcessingRunStatus.Failed, "document_limit_exceeded"), (failed.Status, failed.ErrorCode));
        Assert.Empty(tripwire.Requests);
        var row = await RunRow(over);
        Assert.Equal(("Failed", "document_limit_exceeded", 0), (row.Status, row.Code, row.Calls));
        Assert.StartsWith("gap_assessment_exceeds_output: ", row.Message, StringComparison.Ordinal);
        Assert.Contains($"{GapStageEstimate.Estimate(5)} token", row.Message, StringComparison.Ordinal);
        Assert.Contains("1500 token", row.Message, StringComparison.Ordinal);
        Assert.Matches(@"\b5 evidenze di tipo Gap", row.Message);
        await AssertNothingProducedAsync(over);

        var (inside, insideClaim, _) = await ArrangeAsync(documents: 7, gaps: 4, words: 4);
        var model = new DocumentChatWorkflowTests.FakeChatModel { ContextWindow = 6000, Responder = DocumentGapStageWorkflowTests.Respond };
        var completed = await RunAsync(insideClaim, model, contextWindow: 6000);

        Assert.True(completed.Status == ProcessingRunStatus.Completed, await Message(inside));
        Assert.Single(model.Requests, DocumentGapStageWorkflowTests.IsStage);
        Assert.Equal(model.Requests.Count, (await RunRow(inside)).Calls);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1'", ("r", inside)));
    }

    [Fact]
    public async Task The_fail_fast_is_deterministic_on_takeover_and_replay_with_the_same_outcome_and_no_calls()
    {
        var (run, claim, _) = await ArrangeAsync(documents: 8, gaps: 2, words: 120);
        var first = Tripwire();
        await RunAsync(claim, first);
        var before = await RunRow(run);

        await Execute("UPDATE \"ProcessingRuns\" SET \"LeaseOwner\" = 'owner-2', \"FencingToken\" = 2, \"AttemptCount\" = 2, \"LeaseUntil\" = now() + interval '10 minutes' WHERE \"Id\" = @r", ("r", run));
        var replay = Tripwire();
        var again = await RunAsync(new AnalysisRunClaim(run, "owner-2", 2, 2), replay);

        Assert.Equal((ProcessingRunStatus.Failed, "document_limit_exceeded"), (again.Status, again.ErrorCode));   // Failed è terminale: stesso esito, nessuna chiamata
        Assert.Empty(replay.Requests);
        Assert.Equal(before, await RunRow(run));
        await AssertNothingProducedAsync(run);
    }

    // ================================================================ (b2) senza Gap: gerarchia v4-equivalente, nessuno stadio

    [Fact]
    public async Task Without_a_gap_a_v6_run_is_the_v4_equivalent_hierarchy_with_the_same_payloads_and_calls_as_v5_and_never_runs_the_stage()
    {
        using var client = await App.LoginAsync();
        var (v6Run, v6Claim, project) = await ArrangeAsync(documents: 8, gaps: 0, words: 120, existing: client);
        var model6 = new DocumentChatWorkflowTests.FakeChatModel { Responder = DocumentGapStageWorkflowTests.Respond };
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(v6Claim, model6)).Status);

        // Stessi documenti, run v5 STORICO (fixture): i payload dei batch, le chiamate e i hash di input sono gli stessi.
        var (v5Run, v5Claim, _) = await ArrangeAsync(documents: 8, gaps: 0, words: 120, existing: client, existingProject: project);
        await Execute(HistoricalAnalysisV5.Sql, ("v", HistoricalAnalysisV5.Versions), ("r", v5Run));
        var model5 = new DocumentChatWorkflowTests.FakeChatModel { Responder = DocumentGapStageWorkflowTests.Respond };
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(v5Claim, model5)).Status);

        Assert.DoesNotContain(model6.Requests, DocumentGapStageWorkflowTests.IsStage);
        Assert.True(model6.Requests.Count >= 3);
        Assert.Equal(model5.Requests.Select(r => r.AllText), model6.Requests.Select(r => r.AllText));
        Assert.Equal(model5.Requests.Select(r => r.System), model6.Requests.Select(r => r.System));
        Assert.Equal(
            await Scalar<string>("SELECT string_agg(\"InputHash\", ',' ORDER BY \"Ordinal\") FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r", ("r", v5Run)),
            await Scalar<string>("SELECT string_agg(\"InputHash\", ',' ORDER BY \"Ordinal\") FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r", ("r", v6Run)));
        Assert.Equal((await RunRow(v5Run)).Calls, (await RunRow(v6Run)).Calls);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" = 'gap'", ("r", v6Run)));
        Assert.Equal("global-reconcile-v1", await Scalar<string>("SELECT \"AlgorithmVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", v6Run)));   // nessun gap-stage-1
        Assert.Equal("project-analysis-documents-v6", await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", v6Run)));
        Assert.Equal("project-analysis-documents-v5", await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", v5Run)));

        // Il report è coverage-2 per entrambi: nessuna valutazione Gap sintetizzata dal server.
        foreach (var (run, projectId) in new[] { (v6Run, project), (v5Run, project) })
        {
            var report = await Json(await client.PostAsJsonAsync($"/api/projects/{projectId}/analyses/{run}/coverage-reports", new { }, Ct));
            Assert.Equal("coverage-2", report.GetProperty("checkerVersion").GetString());
            Assert.False(report.TryGetProperty("gapAssessments", out _));
        }
    }

    // ================================================================ (a) single-call invariato

    private async Task<AnalysisRunResult> RunWideAsync(AnalysisRunClaim claim, DocumentChatWorkflowTests.FakeChatModel model, int window, int output)
    {
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = window, MaxOutputTokens = output, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        using var client = new InternalModelClient(options, model);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        return await new DocumentAnalysisWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(new DocumentOptions())).ExecuteAsync(claim, Ct);
    }

    [Fact]
    public async Task A_payload_that_fits_one_call_and_the_estimated_output_stays_single_call_with_the_v5_artifacts_and_no_stage()
    {
        using var client = await App.LoginAsync();
        var (run, claim, project) = await ArrangeAsync(documents: 3, gaps: 1, words: 12, existing: client);
        var model = new DocumentChatWorkflowTests.FakeChatModel { ContextWindow = 40_000, Responder = r => AliasModelRepliesV5.Reply(r.AllText) };

        var result = await RunWideAsync(claim, model, window: 40_000, output: 8_000);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        var request = Assert.Single(model.Requests);
        Assert.Equal(DocumentAnalysisWorkflow.InstructionsV6.Replace("\r\n", "\n"), request.System.Replace("\r\n", "\n"));   // prompt di analisi, non quello dello stadio
        Assert.DoesNotContain("gap-stage", request.AllText, StringComparison.Ordinal);
        Assert.Equal("analysis-single-v3", await Scalar<string>("SELECT \"AlgorithmVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal("project-analysis-documents-v6", await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal("document-analysis-alias-2", await Scalar<string>("SELECT \"SchemaVersion\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal(1, (await RunRow(run)).Calls);

        // coverage-3 del single-call: valutazioni nel DTO, nessun campo derivato dello stadio (report v5 invariato nella forma).
        var report = await Json(await client.PostAsJsonAsync($"/api/projects/{project}/analyses/{run}/coverage-reports", new { }, Ct));
        Assert.Equal("coverage-3", report.GetProperty("checkerVersion").GetString());
        Assert.Equal(1, report.GetProperty("summary").GetProperty("expectedGaps").GetInt32());
        Assert.False(report.GetProperty("summary").TryGetProperty("absenceProvenGaps", out _));
        Assert.All(report.GetProperty("gapAssessments").EnumerateArray(), r => Assert.False(r.TryGetProperty("searchBasis", out _)));
    }

    // ================================================================ v5, definizioni miste e storico invariati

    /// <summary>
    /// 002 T151 (D2) — il single-call di una NUOVA richiesta v6 manda il formato 2 (documento/versione/certezza letti dal DB nella mappa laterale); una v6 congelata prima di D2 (chiave assente) e una v5
    /// storica mandano il formato 1 con lo STESSO inputHash del batch (run in volo e storici non rotti); catalogo e manifesto non dipendono dal formato.
    /// </summary>
    [Fact]
    public async Task The_single_call_of_a_new_v6_run_sends_message_format_2_while_a_pre_D2_v6_and_a_v5_run_keep_the_byte_identical_format_1()
    {
        using var client = await App.LoginAsync();
        var (v6Run, v6Claim, project) = await ArrangeAsync(documents: 3, gaps: 1, words: 12, existing: client);
        var model6 = new DocumentChatWorkflowTests.FakeChatModel { ContextWindow = 40_000, Responder = r => AliasModelRepliesV5.Reply(r.AllText) };
        Assert.Equal(ProcessingRunStatus.Completed, (await RunWideAsync(v6Claim, model6, window: 40_000, output: 8_000)).Status);
        var text6 = Assert.Single(model6.Requests).AllText;
        Assert.Contains("documento=\"doc1", text6);
        Assert.Contains(" versione=1", text6);
        Assert.Contains(" certezza=", text6);
        Assert.DoesNotContain(" sezione=", text6);                                                                   // fixture senza intestazioni: mai inventata

        var (legacyRun, legacyClaim, _) = await ArrangeAsync(documents: 3, gaps: 1, words: 12, existing: client, existingProject: project);
        await Execute("UPDATE \"ProcessingRuns\" SET \"PromptVersions\" = \"PromptVersions\" - 'analysisMessageFormat' WHERE \"Id\" = @r", ("r", legacyRun));
        var modelLegacy = new DocumentChatWorkflowTests.FakeChatModel { ContextWindow = 40_000, Responder = r => AliasModelRepliesV5.Reply(r.AllText) };
        Assert.Equal(ProcessingRunStatus.Completed, (await RunWideAsync(legacyClaim, modelLegacy, window: 40_000, output: 8_000)).Status);
        var textLegacy = Assert.Single(modelLegacy.Requests).AllText;
        Assert.DoesNotContain("documento=", textLegacy);
        Assert.DoesNotContain(" certezza=", textLegacy);

        var (v5Run, v5Claim, _) = await ArrangeAsync(documents: 3, gaps: 1, words: 12, existing: client, existingProject: project);
        await Execute(HistoricalAnalysisV5.Sql, ("v", HistoricalAnalysisV5.Versions), ("r", v5Run));
        var model5 = new DocumentChatWorkflowTests.FakeChatModel { ContextWindow = 40_000, Responder = r => AliasModelRepliesV5.Reply(r.AllText) };
        Assert.Equal(ProcessingRunStatus.Completed, (await RunWideAsync(v5Claim, model5, window: 40_000, output: 8_000)).Status);
        Assert.Equal(textLegacy, Assert.Single(model5.Requests).AllText);

        var hashes = new List<string>();
        foreach (var run in new[] { v6Run, legacyRun, v5Run })
        {
            hashes.Add(await Scalar<string>("SELECT string_agg(\"InputHash\", ',' ORDER BY \"Ordinal\") FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r", ("r", run)));
        }

        Assert.NotEqual(hashes[0], hashes[1]);                                                                       // il formato 2 è una nuova identità solo per le nuove richieste
        Assert.Equal(hashes[1], hashes[2]);                                                                          // v6 pre-D2 e v5 storica: stesso inputHash del formato 1
    }

    [Fact]
    public async Task A_v6_run_with_an_unknown_message_format_is_workflow_incompatible_with_zero_calls()
    {
        using var client = await App.LoginAsync();
        var (run, claim, _) = await ArrangeAsync(documents: 3, gaps: 1, words: 12, existing: client);
        await Execute("UPDATE \"ProcessingRuns\" SET \"PromptVersions\" = jsonb_set(\"PromptVersions\", '{analysisMessageFormat}', '\"message-format-9\"') WHERE \"Id\" = @r", ("r", run));
        var model = Tripwire();

        var result = await RunWideAsync(claim, model, window: 40_000, output: 8_000);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal("workflow_incompatible", (await RunRow(run)).Code);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task A_v5_run_above_context_with_a_gap_still_fails_fast_with_the_v5_reason_and_never_reaches_the_stage()
    {
        var (run, claim, _) = await ArrangeAsync(documents: 8, gaps: 1, words: 120);
        await Execute(HistoricalAnalysisV5.Sql, ("v", HistoricalAnalysisV5.Versions), ("r", run));
        var model = Tripwire();

        var result = await RunAsync(claim, model);

        Assert.Equal((ProcessingRunStatus.Failed, "document_limit_exceeded"), (result.Status, result.ErrorCode));
        Assert.Empty(model.Requests);
        var row = await RunRow(run);
        Assert.StartsWith("hierarchical_gap_assessment_unsupported: ", row.Message, StringComparison.Ordinal);
        Assert.Equal(0, row.Calls);
        await AssertNothingProducedAsync(run);
    }

    [Theory]
    [InlineData("v6-with-v5-policy")]
    [InlineData("v5-with-gap-keys")]
    [InlineData("v6-missing-a-gap-key")]
    public async Task A_mixed_combination_of_v5_and_v6_keys_is_workflow_incompatible_with_zero_calls(string scenario)
    {
        var (run, claim, _) = await ArrangeAsync(documents: 4, gaps: 1, words: 12);
        var keys = AnalysisV6Fixture.Keys();
        var definition = AnalysisV6Fixture.Definition;
        switch (scenario)
        {
            case "v6-with-v5-policy":
                keys["reconcileGapPolicy"] = "hierarchical-gap-incomplete-1";
                break;
            case "v5-with-gap-keys":
                definition = HistoricalAnalysisV5.Definition;
                keys = JsonSerializer.Deserialize<Dictionary<string, string>>(HistoricalAnalysisV5.Versions)!;
                keys["gapStage"] = "gap-stage-1";
                break;
            default:
                keys.Remove("gapPolicy");
                break;
        }

        await Execute("UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = @v::jsonb WHERE \"Id\" = @r", ("d", definition), ("v", AnalysisV6Fixture.Json(keys)), ("r", run));
        var model = Tripwire();

        var result = await RunAsync(claim, model);

        Assert.Equal((ProcessingRunStatus.Failed, "workflow_incompatible"), (result.Status, result.ErrorCode));
        Assert.Empty(model.Requests);
        await AssertNothingProducedAsync(run);
    }

    [Fact]
    public async Task The_regime_payload_of_a_v6_run_is_measured_on_the_alias_2_schema_like_v5()
    {
        using var scope = App.Services.CreateScope();
        var options = new AiOptions { Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = 4000, MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60 };
        using var client = new InternalModelClient(options, new DocumentChatWorkflowTests.FakeChatModel());
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        var workflow = new DocumentAnalysisWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(new DocumentOptions()));

        var v6 = workflow.BuildRegimePayload(DocumentAnalysisWorkflow.DefinitionVersionV6);
        var v5 = workflow.BuildRegimePayload(DocumentAnalysisWorkflow.DefinitionVersionV5);

        Assert.Equal(v5.SchemaJson, v6.SchemaJson);
        Assert.Contains("gapAssessments", v6.SchemaJson!, StringComparison.Ordinal);
        await Task.CompletedTask;
    }
}
