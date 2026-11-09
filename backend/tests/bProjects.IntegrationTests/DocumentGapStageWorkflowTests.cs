using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// Run Analizza nella definizione `project-analysis-documents-v6` (D1, AI §13.7): definizione e versioni congelate dalle nuove richieste. Fixture dei test: l'endpoint crea il run e le
/// chiavi vengono scritte esplicitamente, come per i run storici v4/v5 (nessun pin sul comportamento di produzione).
/// </summary>
internal static class AnalysisV6Fixture
{
    public const string Definition = "project-analysis-documents-v6";

    /// <summary>Chiavi del single-call v6 (prompt document-analysis-v6, schema AI alias-2, policy di stima dell'output) e della gerarchia v4 più le sei chiavi `gap*` dello stadio; `reconcileGapPolicy` assente.</summary>
    public static Dictionary<string, string> Keys() => new(StringComparer.Ordinal)
    {
        ["analysis"] = "document-analysis-v6", ["analysisSchema"] = "1.1.0", ["analysisAiSchema"] = "document-analysis-alias-2", ["analysisOutputPolicy"] = "single-call-output-estimate-1",
        ["aliasCatalog"] = "alias-catalog-1", ["canonIds"] = "canon-ids-1", ["algorithm"] = "analysis-single-v3", ["validation"] = "document-analysis-validation-v3",
        ["reconcile"] = "document-reconcile-v3", ["reconcileAiSchema"] = "reconcile-alias-1", ["reconcileAlgorithm"] = "global-reconcile-v1", ["reconcileCatalog"] = "reconcile-alias-catalog-1",
        ["gapStage"] = "gap-stage-1", ["gapPrompt"] = "document-gap-v1", ["gapAiSchema"] = "gap-stage-alias-1", ["gapMessageFormat"] = "gap-stage-message-1", ["gapPolicy"] = "gap-stage-ledger-1",
        ["gapEstimate"] = "gap-stage-estimate-1",
    };

    public static string Json(Dictionary<string, string>? keys = null) => JsonSerializer.Serialize(keys ?? Keys());
}

/// <summary>
/// 002 T146 (MAF-PG, modello FINTO) — lo stadio Gap dedicato nel piano: un nuovo valore di `Stage` del batch (nessun nuovo executor, DocumentBatchWorkflow invariato), eseguito solo a gerarchia
/// Complete, UNA chiamata sull'intero ledger, WorkOutput `gap-stage-output-1`, riserva di una chiamata sul tetto del run, claim/fencing/replay con verifica di hash, nessun retry dedicato.
/// SDK MAF 1.26.0 e PostgreSQL reali; il modello finto legge SOLO il messaggio inviato. I test provano il protocollo, non la qualità di un modello reale.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentGapStageWorkflowTests : ReconcileRig
{
    internal const int Documents = 6;
    internal const int Words = 15;

    private static string Statement(int n, int words = Words) => $"Requisito {n}: " + string.Join(' ', Enumerable.Range(0, words).Select(i => $"w{n}x{i}"));

    // ================================================================ modello finto: gerarchia v4-equivalente + stadio Gap

    internal static bool IsStage(DocumentChatWorkflowTests.ChatRequest r) => r.AllText.Contains("Formato: gap-stage-message-1", StringComparison.Ordinal);

    /// <summary>Risposta della gerarchia: come i test di T131, con gli item delle evidenze Gap nella sezione openQuestions (o in requirements se <paramref name="gapsAsQuestions"/> è falso).</summary>
    internal static string Hierarchy(DocumentChatWorkflowTests.ChatRequest r, bool gapsAsQuestions = true)
    {
        var p = DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText);
        var gaps = Regex.Matches(r.AllText, @"<<<VOCE \[(E\d+)\] origine=\w+ tipo=Gap").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return DocumentReconcileAliasWorkflowTests.Dto(p, (_, o) =>
        {
            foreach (var item in o["items"]!.AsArray())
            {
                if (item!["evidence"]!.AsArray().Any(e => gaps.Contains(e!.GetValue<string>())))
                {
                    item["section"] = gapsAsQuestions ? "openQuestions" : "requirements";
                }
            }

            return o;
        });
    }

    internal sealed record StageView(IReadOnlyList<(string Alias, string? Question)> Gaps, IReadOnlyList<string> Candidates, IReadOnlyList<string> Questions, IReadOnlyList<string> Conflicts);

    internal static StageView ParseStage(string text)
    {
        var gaps = Regex.Matches(text, @"<<<VOCE \[(E\d+)\] origine=\w+ tipo=Gap[^>\n]*?(?: domandaCanonica=(i\d+))?>>>").Select(m => (m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null)).ToList();
        var candidates = Regex.Matches(text, @"<<<VOCE \[(E\d+)\] origine=\w+ tipo=(?!Gap)\w+").Select(m => m.Groups[1].Value).ToList();
        var questions = Regex.Matches(text, @"^\[(i\d+)\] ", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var conflicts = Regex.Matches(text, @"^\[(c\d+)\] ", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        return new(gaps, candidates, questions, conflicts);
    }

    private static JsonObject Answered(string gap, string support) => new()
    {
        ["gap"] = gap, ["outcome"] = "AnsweredInSnapshot", ["reason"] = "Answered", ["rationale"] = "Il riscontro risponde alla domanda.", ["supportingEvidence"] = new JsonArray(support),
        ["openItem"] = null, ["openConflict"] = null,
    };

    private static JsonObject OpenQuestion(string gap, string? item) => new()
    {
        ["gap"] = gap, ["outcome"] = "Open", ["reason"] = "NoAnswer", ["rationale"] = "Nessuna evidenza risponde.", ["supportingEvidence"] = new JsonArray(),
        ["openItem"] = item, ["openConflict"] = null,
    };

    /// <summary>Risposta dello stadio: il primo Gap ha risposta documentata, gli altri sono Open/NoAnswer sulla propria domanda canonica. <paramref name="change"/> modifica l'elenco dei record.</summary>
    internal static string Stage(DocumentChatWorkflowTests.ChatRequest r, Func<StageView, List<JsonObject>, List<JsonObject>>? change = null)
    {
        var view = ParseStage(r.AllText);
        var records = new List<JsonObject>();
        for (var i = 0; i < view.Gaps.Count; i++)
        {
            records.Add(i == 0 ? Answered(view.Gaps[i].Alias, view.Candidates[0]) : OpenQuestion(view.Gaps[i].Alias, view.Gaps[i].Question));
        }

        return new JsonObject { ["contract"] = "gap-stage-1", ["gapAssessments"] = new JsonArray([.. (change?.Invoke(view, records) ?? records).Select(x => (JsonNode)x.DeepClone())]) }.ToJsonString();
    }

    internal static string Respond(DocumentChatWorkflowTests.ChatRequest r) => IsStage(r) ? Stage(r) : Hierarchy(r);

    // ================================================================ scenario

    private async Task<(Guid Run, AnalysisRunClaim Claim, Guid Project)> ArrangeAsync(
        int documents = Documents, int gaps = 2, int words = Words, HttpClient? existing = null, Guid? existingProject = null)
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

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var run = (await Json(response)).GetProperty("id").GetGuid();
        await Execute(
            "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = @v::jsonb WHERE \"Id\" = @r",
            ("d", AnalysisV6Fixture.Definition), ("v", AnalysisV6Fixture.Json()), ("r", run));
        return (run, await ClaimAsync(run), project);
    }

    private async Task<(string Status, string Code, string Message, int Calls)> RunRow(Guid run) => (
        await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<string>("SELECT coalesce(\"ErrorCode\",'') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<string>("SELECT coalesce(\"ErrorMessage\",'') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<int>("SELECT coalesce(\"CallCount\",0) FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));

    private Task<long> Outputs(Guid run, string? schema = null) => Count(
        "SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'" + (schema is null ? "" : " AND \"SchemaVersion\" = '" + schema + "'"),
        ("r", run));

    // ================================================================ una chiamata, ledger integrale

    [Fact]
    public async Task The_stage_sends_exactly_one_dedicated_call_with_the_whole_ledger_after_the_hierarchy_and_completes_the_run()
    {
        var (run, claim, _) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };

        var result = await RunAsync(claim, model);

        var row = await RunRow(run);
        Assert.True(result.Status == ProcessingRunStatus.Completed, $"{row.Code}: {row.Message}");
        var stageRequests = model.Requests.Where(IsStage).ToList();
        Assert.Single(stageRequests);                                             // esattamente UNA chiamata dello stadio
        Assert.True(IsStage(model.Requests[^1]));                                 // dopo l'ultimo batch della gerarchia
        var hierarchyCalls = model.Requests.Count - 1;
        Assert.True(hierarchyCalls >= 2);
        Assert.Equal(hierarchyCalls + 1, row.Calls);                              // il tetto conta la gerarchia più 1
        Assert.Equal(hierarchyCalls, await Outputs(run, "reconcile-alias-1"));
        Assert.Equal(1, await Outputs(run, "gap-stage-alias-1"));

        // Ledger integrale: ogni enunciato non Gap compare una sola volta, ogni Gap è valutato; nessun top-k, nessuna sintesi.
        var stage = stageRequests[0];
        var view = ParseStage(stage.AllText);
        Assert.Equal(2, view.Gaps.Count);
        Assert.Equal(Documents - 2, view.Candidates.Count);
        for (var n = 1; n <= Documents; n++)
        {
            Assert.True(1 == Regex.Count(stage.AllText, Regex.Escape(Statement(n)) + "\n<<<FONTE"), $"n={n}");   // l'enunciato di ogni voce compare una sola volta come enunciato (la citazione è un'altra riga)
        }

        Assert.Contains("searchBasis: ledger_exhaustive", stage.AllText, StringComparison.Ordinal);
        Assert.Contains("retrievalComplete: true", stage.AllText, StringComparison.Ordinal);
        Assert.Equal(GapStage.Instructions.Replace("\r\n", "\n"), stage.System.Replace("\r\n", "\n"));
        Assert.DoesNotMatch("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-", stage.AllText);
        Assert.Matches("documento=\"doc[1-6]\" versione=1", stage.AllText);

        // Nuovo valore di Stage, nessun nuovo executor: il grafo registrato è sempre quello fisso.
        Assert.Equal("gap", await Scalar<string>("SELECT \"Stage\" FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r ORDER BY \"Ordinal\" DESC LIMIT 1", ("r", run)));
        Assert.Equal(hierarchyCalls, await Scalar<int>("SELECT \"Ordinal\" FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" = 'gap'", ("r", run)));
        Assert.Equal(["Load", "PrepareBatch", "LookupOutput", "InvokeBatch", "ValidatePersistBatch", "Advance", "Assemble", "Commit"], DocumentBatchWorkflow.Stages);
        Assert.Equal("gap-stage-1", await Scalar<string>("SELECT \"AlgorithmVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal("project-analysis-documents-v6", await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run)));

        // WorkOutput dello stadio: record canonici, hash e catalogo.
        var payload = JsonDocument.Parse(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1'", ("r", run)));
        Assert.Equal("gap-stage-output-1", payload.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("Complete", payload.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("gapAssessments").GetArrayLength());
        Assert.Equal(64, payload.RootElement.GetProperty("gapAssessmentsHash").GetString()!.Length);
        Assert.Equal("ledger_exhaustive", payload.RootElement.GetProperty("stage").GetProperty("searchBasis").GetString());
        Assert.True(payload.RootElement.GetProperty("stage").GetProperty("retrievalComplete").GetBoolean());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task The_hierarchy_batches_of_a_v6_run_with_gaps_are_the_unchanged_v4_hierarchy_batches_and_only_the_last_one_is_the_stage()
    {
        var (run, claim, _) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };

        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(claim, model)).Status);

        var hierarchy = model.Requests.Where(r => !IsStage(r)).ToList();
        Assert.All(hierarchy, r => Assert.Equal(ReconcileAliasContracts.Instructions.Replace("\r\n", "\n"), r.System.Replace("\r\n", "\n")));   // stesso prompt document-reconcile-v3
        Assert.All(hierarchy, r => Assert.Matches("^Tipo di batch: (foglia|riduzione|cross|espansione|confronto)$", r.AllText.Split('\n')[0]));
        Assert.Equal(model.Requests.Count - 1, hierarchy.Count);
        Assert.True(IsStage(model.Requests[^1]));
        // Nessun dato dello stadio nei batch della gerarchia e nessun dato della gerarchia (tipi di batch) nel messaggio dello stadio.
        Assert.All(hierarchy, r => Assert.DoesNotContain("gap-stage", r.AllText, StringComparison.Ordinal));
        Assert.DoesNotContain("Tipo di batch", model.Requests[^1].AllText, StringComparison.Ordinal);
        Assert.Equal(["document-reconcile-v3"], (await Scalar<string>("SELECT string_agg(DISTINCT \"PromptVersion\", ',') FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" <> 'gap'", ("r", run))).Split(','));
        Assert.Equal(["gap-stage-alias-1"], (await Scalar<string>("SELECT string_agg(DISTINCT \"SchemaVersion\", ',') FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" = 'gap'", ("r", run))).Split(','));
    }

    // ================================================================ validazione: nessun retry, nessuna riparazione

    [Fact]
    public async Task A_missing_record_is_a_valid_incomplete_output_kept_but_never_completes_the_run_and_nothing_is_retried()
    {
        var (run, claim, _) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => IsStage(r) ? Stage(r, (_, records) => records.Take(1).ToList()) : Hierarchy(r) };

        var result = await RunAsync(claim, model);

        Assert.Equal((ProcessingRunStatus.Failed, "coverage_incomplete"), (result.Status, result.ErrorCode));
        Assert.Single(model.Requests, IsStage);                                    // nessun retry dedicato
        Assert.Equal(1, await Outputs(run, "gap-stage-alias-1"));                         // l'output è riutilizzabile ma incompleto
        Assert.Equal("ValidIncomplete", JsonDocument.Parse(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1'", ("r", run))).RootElement.GetProperty("outcome").GetString());
        var row = await RunRow(run);
        Assert.Contains("gap_not_assessed", row.Message, StringComparison.Ordinal);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.True(await Outputs(run, "reconcile-alias-1") >= 2);                         // gerarchia conservata
    }

    [Fact]
    public async Task A_malformed_or_unresolvable_stage_response_is_a_permanent_work_output_invalid_with_no_stage_output_and_no_retry()
    {
        foreach (var scenario in new (string Name, Func<StageView, List<JsonObject>, List<JsonObject>> Change)[]
        {
            ("openItem non offerto", (_, r) => [r[0], OpenQuestion((string)r[1]["gap"]!, "i99")]),
            ("Gap come riscontro", (v, r) => [Answered(v.Gaps[0].Alias, v.Gaps[1].Alias), r[1]]),
            ("alias inventato", (_, r) => [Answered((string)r[0]["gap"]!, "E999"), r[1]]),
            ("record duplicato", (_, r) => [r[0], r[1], r[1]]),
        })
        {
            var (run, claim, _) = await ArrangeAsync();
            var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => IsStage(r) ? Stage(r, scenario.Change) : Hierarchy(r) };

            var result = await RunAsync(claim, model);

            Assert.True((result.Status, result.ErrorCode) == (ProcessingRunStatus.Failed, "work_output_invalid"), scenario.Name + ": " + await Message(run));
            Assert.Single(model.Requests, IsStage);
            Assert.Equal(0, await Outputs(run, "gap-stage-alias-1"));
            Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        }
    }

    [Fact]
    public async Task A_stage_response_with_an_extra_field_is_rejected_by_the_strict_codec()
    {
        var (run, claim, _) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => IsStage(r) ? Stage(r).Replace("\"gapAssessments\"", "\"openQuestion\":{\"key\":\"i9\",\"text\":\"nuova\"},\"gapAssessments\"") : Hierarchy(r),
        };

        var result = await RunAsync(claim, model);

        Assert.Equal((ProcessingRunStatus.Failed, "work_output_invalid"), (result.Status, result.ErrorCode));
        Assert.Equal(0, await Outputs(run, "gap-stage-alias-1"));
    }

    // ================================================================ precondizione: la domanda canonica del Gap

    [Fact]
    public async Task A_gap_the_hierarchy_did_not_turn_into_a_canonical_question_fails_before_the_stage_without_creating_anything()
    {
        var (run, claim, _) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => IsStage(r) ? Stage(r) : Hierarchy(r, gapsAsQuestions: false) };

        var result = await RunAsync(claim, model);

        Assert.Equal((ProcessingRunStatus.Failed, "coverage_incomplete"), (result.Status, result.ErrorCode));
        Assert.DoesNotContain(model.Requests, IsStage);                                      // nessun invio dello stadio
        var row = await RunRow(run);
        Assert.Matches(@"gap_reference_missing:E\d+", row.Message);
        Assert.Equal(row.Calls, model.Requests.Count);                                    // la gerarchia è stata eseguita
        Assert.True(await Outputs(run, "reconcile-alias-1") >= 2);                         // WorkOutput Committed conservati
        Assert.Equal(0, await Outputs(run, "gap-stage-alias-1"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    // ================================================================ tetto, crash, takeover, replay

    [Fact]
    public async Task When_the_call_ceiling_is_exhausted_before_the_stage_the_run_fails_with_the_hierarchy_outputs_preserved()
    {
        // Un run identico senza tetto misura le chiamate della gerarchia; i limiti si fissano nello snapshot alla creazione del run, quindi il tetto va impostato PRIMA della richiesta.
        var (_, probeClaim, _) = await ArrangeAsync();
        var probe = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(probeClaim, probe)).Status);
        var hierarchyCalls = probe.Requests.Count - 1;
        Assert.True(hierarchyCalls >= 2);

        await SetLimitsAsync(l => l with { MaxAiCallsPerRun = hierarchyCalls });
        var (run, claim, _) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };

        var result = await RunAsync(claim, model);

        Assert.Equal((ProcessingRunStatus.Failed, "document_limit_exceeded"), (result.Status, result.ErrorCode));
        Assert.DoesNotContain(model.Requests, IsStage);                                   // la riserva rifiuta la chiamata dello stadio prima della rete
        Assert.Equal(hierarchyCalls, model.Requests.Count);
        Assert.Equal(hierarchyCalls, await Outputs(run, "reconcile-alias-1"));         // WorkOutput della gerarchia conservati
        Assert.Equal(0, await Outputs(run, "gap-stage-alias-1"));
        Assert.Equal(hierarchyCalls, (await RunRow(run)).Calls);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));   // mai Completed senza valutazioni
    }

    private async Task SetLimitsAsync(Func<DocumentLimits, DocumentLimits> change)
    {
        using var scope = App.Services.CreateScope();
        var (_, snapshot) = await scope.ServiceProvider.GetRequiredService<DocumentPolicyService>().GetAsync(Ct);
        var limits = change(snapshot.Limits);
        Assert.Empty(limits.Validate());
        await Execute("UPDATE \"DocumentPolicies\" SET \"LimitsJson\" = @j::jsonb, \"PolicyHash\" = @h WHERE \"Id\" = 1", ("j", limits.ToJson()), ("h", limits.Hash()));
    }

    [Fact]
    public async Task A_crash_during_the_stage_call_keeps_the_reservation_and_the_hierarchy_and_the_takeover_commits_once_with_no_double_commit()
    {
        var (run, claim, _) = await ArrangeAsync();
        var crashing = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => IsStage(r) ? throw new InvalidOperationException("rete caduta durante lo stadio") : Hierarchy(r),
        };

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, crashing));

        var hierarchyCalls = crashing.Requests.Count(r => !IsStage(r));
        Assert.Single(crashing.Requests, IsStage);
        Assert.Equal(hierarchyCalls, await Outputs(run, "reconcile-alias-1"));
        Assert.Equal(0, await Outputs(run, "gap-stage-alias-1"));
        Assert.Equal(hierarchyCalls + 1, (await RunRow(run)).Calls);                      // la riserva già consumata resta (costo prudenziale)
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));

        var healthy = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };
        var resumed = await RunAsync(claim, healthy);

        Assert.Equal(ProcessingRunStatus.Completed, resumed.Status);
        Assert.Single(healthy.Requests);                                                  // riprende solo lo stadio: nessun batch della gerarchia è reinviato
        Assert.True(IsStage(healthy.Requests[0]));
        Assert.Equal(1, await Outputs(run, "gap-stage-alias-1"));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(hierarchyCalls + 2, (await RunRow(run)).Calls);                      // riserva persa + riserva della ripresa

        // Replay di un run Completed: nessun invio, stesso risultato, nessun secondo commit.
        var again = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => throw new InvalidOperationException("nessun invio al replay") };
        var replay = await RunAsync(claim, again);
        Assert.Equal(resumed.ResultRevisionId, replay.ResultRevisionId);
        Assert.Empty(again.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_takeover_after_the_stage_output_was_committed_replays_it_without_inference_and_verifies_its_hash()
    {
        var (run, claim, _) = await ArrangeAsync();
        // Il commit finale della revisione fallisce una volta (crash fra WorkOutput dello stadio e revisione): resta l'output Committed dello stadio.
        await Execute(
            """
            CREATE OR REPLACE FUNCTION t146_block_rev() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 't146 crash simulato'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER t146_block_rev BEFORE INSERT ON "AnalysisRevisions" FOR EACH ROW EXECUTE FUNCTION t146_block_rev();
            """);
        var first = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, first));
        await Execute("DROP TRIGGER t146_block_rev ON \"AnalysisRevisions\"; DROP FUNCTION t146_block_rev();");
        Assert.Equal(1, await Outputs(run, "gap-stage-alias-1"));
        var stageHash = await Scalar<string>("SELECT \"OutputHash\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1'", ("r", run));

        await Execute(
            "UPDATE \"ProcessingRuns\" SET \"LeaseOwner\" = 'owner-2', \"FencingToken\" = 2, \"AttemptCount\" = 2, \"LeaseUntil\" = now() + interval '10 minutes' WHERE \"Id\" = @r", ("r", run));
        var resumed = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => throw new InvalidOperationException("nessuna inferenza al replay") };
        var result = await RunAsync(new AnalysisRunClaim(run, "owner-2", 2, 2), resumed);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        Assert.Empty(resumed.Requests);                                                    // né gerarchia né stadio reinviati
        Assert.Equal(stageHash, await Scalar<string>("SELECT \"OutputHash\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1'", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_tampered_committed_stage_output_is_never_trusted_on_replay()
    {
        var (run, claim, _) = await ArrangeAsync();
        await Execute(
            """
            CREATE OR REPLACE FUNCTION t146_block_rev2() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 't146 crash simulato'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER t146_block_rev2 BEFORE INSERT ON "AnalysisRevisions" FOR EACH ROW EXECUTE FUNCTION t146_block_rev2();
            """);
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond }));
        await Execute("DROP TRIGGER t146_block_rev2 ON \"AnalysisRevisions\"; DROP FUNCTION t146_block_rev2();");

        // Manomissione del payload (hash di output ora diverso dal contenuto): il lookup rifiuta l'output, mai una reinferenza silenziosa né un Completed.
        await Execute(
            "ALTER TABLE \"WorkOutputs\" DISABLE TRIGGER USER; UPDATE \"WorkOutputs\" SET \"PayloadJson\" = jsonb_set(\"PayloadJson\"::jsonb, '{outcome}', '\"Complete \"') WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1'; ALTER TABLE \"WorkOutputs\" ENABLE TRIGGER USER;", ("r", run));
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };
        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

}
