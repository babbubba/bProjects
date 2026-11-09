using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T059 (DET) — regole del server sul workflow documentale a singola chiamata, senza database: base immutabile calcolata dal server a partire
/// dagli itemProposals e dalle relazioni note (le patch si validano tutte contro quella base), alternative incompatibili conservate solo se
/// rappresentate nei conflitti della revisione, e completezza che non dipende mai da una dichiarazione dell'AI.
/// </summary>
public sealed class DocumentAnalysisServerRulesTests
{
    private static readonly ReconciliationSnapshot Snapshot = ReconciliationFixtures.Snapshot();
    private static readonly DocumentAnalysisFacts Facts = new(ReconciliationFixtures.Project, Guid.Parse("a0000000-0000-4000-8000-000000000002"), 0, 0, 1);

    private static SingleCallOutput WithPatches(params Func<ReconciliationBase, Patch>[] patches)
    {
        var output = ReconciliationFixtures.Valid();
        var baseState = DocumentAnalysisBase.Compute(output, Snapshot);
        var results = output.Results.ToList();
        results[0] = results[0] with { Patches = [.. patches.Select(p => p(baseState))] };
        return output with { Results = results };
    }

    private static Patch Alternative(ReconciliationBase baseState, string text, Guid? evidence = null, Guid? source = null, string? target = null, string? expected = null)
    {
        var e = evidence ?? ReconciliationFixtures.E2;
        var s = source ?? ReconciliationFixtures.S2;
        var key = target ?? "r1";
        return ReconciliationFixtures.WithId(new Patch
        {
            PatchId = "x", BaseHash = baseState.BaseHash,
            Target = new PatchTarget { Kind = PatchTargetKind.Item, Id = key, ExpectedHash = expected ?? baseState.TargetHashes[ReconciliationBase.TargetKey(PatchTargetKind.Item, key)] },
            Operation = PatchOperation.ProposeAlternative,
            Value = new ProposeAlternativeValue { Text = text, Classification = DocumentAnalysisClassification.Documented, EvidenceIds = [e], SourceIds = [s], Reason = "valore diverso" },
            EvidenceIds = [e], SourceIds = [s], OriginBatchIds = ["batch-1"],
        });
    }

    private static ReconciliationValidation Validate(SingleCallOutput output) =>
        DocumentAnalysisValidator.Validate(ReconciliationCodec.Encode(output), Snapshot, Facts, "batch-1", ReconciliationFixtures.InputHash);

    private static DocumentAnalysisContent WithConflictAlternatives(DocumentAnalysisContent analysis)
    {
        DocumentAnalysisItem Alt(Guid id, string text, Guid source) =>
            new() { Id = id, Text = text, Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [source] };
        return analysis with
        {
            Conflicts =
            [
                new DocumentAnalysisConflict
                {
                    Id = Guid.Parse("c0000000-0000-4000-8000-000000000001"), Description = "Limite ordini al giorno", Status = AnalysisConflictStatus.Open, Resolution = null,
                    Alternatives =
                    [
                        Alt(Guid.Parse("c0000000-0000-4000-8000-0000000000a1"), "limite 8 ordini", ReconciliationFixtures.S2),
                        Alt(Guid.Parse("c0000000-0000-4000-8000-0000000000a2"), "limite 9 ordini", ReconciliationFixtures.S2),
                    ],
                },
            ],
        };
    }

    [Fact]
    public void The_server_base_follows_item_proposals_and_known_relations_and_is_deterministic()
    {
        var output = ReconciliationFixtures.Valid();
        var a = DocumentAnalysisBase.Compute(output, Snapshot);
        var b = DocumentAnalysisBase.Compute(ReconciliationCodec.DecodeSingleCall(ReconciliationCodec.Encode(output)), Snapshot);

        Assert.Equal(a.BaseHash, b.BaseHash);
        Assert.True(ReconciliationContractRules.IsHash(a.BaseHash));
        Assert.Equal([ReconciliationBase.TargetKey(PatchTargetKind.Item, "r1")], a.TargetHashes.Keys);

        var results = output.Results.ToList();
        results[0] = results[0] with { ItemProposals = [results[0].ItemProposals[0] with { Text = "Massimo 6 ordini per cliente A" }] };
        var changed = DocumentAnalysisBase.Compute(output with { Results = results }, Snapshot);
        Assert.NotEqual(a.BaseHash, changed.BaseHash);
        Assert.NotEqual(a.TargetHashes.Values.Single(), changed.TargetHashes.Values.Single());

        var relation = new SnapshotRelation(Guid.Parse("e0000000-0000-4000-8000-000000000001"), ReconciliationFixtures.E1, ReconciliationFixtures.E2, EvidenceRelationType.Confirms);
        var withRelation = DocumentAnalysisBase.Compute(output, Snapshot with { Relations = [relation] });
        Assert.NotEqual(a.BaseHash, withRelation.BaseHash);
        Assert.Contains(ReconciliationBase.TargetKey(PatchTargetKind.Relation, relation.RelationId.ToString("D")), withRelation.TargetHashes.Keys);
    }

    [Fact]
    public void A_patch_on_the_server_base_is_applied_and_a_stale_expected_hash_is_stale_and_blocks_completion()
    {
        var applied = Validate(WithPatches(b => Alternative(b, "limite 8 ordini")));
        Assert.Equal(ReconciliationOutcome.Complete, applied.Outcome);
        Assert.All(applied.Patches!.Dispositions, d => Assert.Equal(PatchDispositionKind.Applied, d.Kind));

        var stale = Validate(WithPatches(b => Alternative(b, "limite 8 ordini", expected: new string('9', 64))));
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, stale.Outcome);
        Assert.Contains(stale.Completion!.Gaps, g => g.Code == "patch_blocking");
        Assert.True(stale.Completion.AdditionalWorkJustified);
    }

    [Fact]
    public void Incompatible_alternatives_complete_only_when_the_revision_conflict_represents_every_proposal_and_source()
    {
        var output = WithPatches(b => Alternative(b, "limite 8 ordini"), b => Alternative(b, "limite 9 ordini"));

        var missing = Validate(output);
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, missing.Outcome);
        Assert.All(missing.Patches!.Dispositions, d => Assert.Equal(PatchDispositionKind.PreservedAsConflict, d.Kind));
        Assert.Contains(missing.Completion!.Gaps, g => g.Code == "conflict_not_represented");

        var represented = Validate(output with { Analysis = WithConflictAlternatives(output.Analysis) });
        Assert.Equal(ReconciliationOutcome.Complete, represented.Outcome);
        Assert.Single(represented.Patches!.Conflicts);
        Assert.True(represented.Patches.Conflicts[0].RequiresPmDecision);
    }

    [Fact]
    public void A_represented_conflict_that_omits_a_source_of_the_group_is_not_enough()
    {
        // Le due alternative citano soltanto S1: la fonte S2 delle proposte non è rappresentata nel conflitto.
        var output = WithPatches(b => Alternative(b, "limite 8 ordini"), b => Alternative(b, "limite 9 ordini"));
        var analysis = WithConflictAlternatives(output.Analysis);
        var conflict = analysis.Conflicts[0] with
        {
            Alternatives = [.. analysis.Conflicts[0].Alternatives.Select(a => a with { EvidenceRefs = [ReconciliationFixtures.S1] })],
        };

        var result = Validate(output with { Analysis = analysis with { Conflicts = [conflict] } });

        Assert.Equal(ReconciliationOutcome.ValidIncomplete, result.Outcome);
        Assert.Contains(result.Completion!.Gaps, g => g.Code == "conflict_not_represented");
    }

    [Fact]
    public void The_identity_of_the_batch_is_the_servers_and_a_result_with_another_batch_or_input_hash_is_invalid()
    {
        var output = ReconciliationFixtures.Valid();
        var results = output.Results.ToList();
        results[1] = results[1] with { BatchId = "batch-altro" };
        results[2] = results[2] with { InputHash = new string('f', 64) };

        var result = Validate(output with { Results = results });

        Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Violations, v => v.Contains("results[1].batchId"));
        Assert.Contains(result.Violations, v => v.Contains("results[2].inputHash"));
    }

    [Fact]
    public void Candidates_and_patches_may_only_name_the_servers_batch_as_origin()
    {
        var output = WithPatches(b => Alternative(b, "limite 8 ordini") with { OriginBatchIds = ["batch-altro"] });

        var result = Validate(output);

        Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void Text_that_is_not_the_closed_wrapper_is_invalid_and_never_a_reusable_output()
    {
        var result = DocumentAnalysisValidator.Validate("""{"results":[],"analysis":""", Snapshot, Facts, "batch-1", ReconciliationFixtures.InputHash);

        Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);
        Assert.False(result.IsReusableWorkOutput);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
    }
}

/// <summary>
/// T059 (PG, IChatClient finto a livello HTTP) — analisi documentale a singola chiamata sul grafo T043: snapshot congelato (Ready + messaggi PM),
/// risultato A/B/Cross + analysis 1.1 + evidenceMap validati dal nucleo T120/T121 PRIMA del WorkOutput, completezza calcolata dal server e commit
/// unico di revisione + Completed + mappa item→evidenze + relazioni di run. Nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentAnalysisWorkflowTests : ArchiveTestBase
{
    private const string Statement1 = "Il cliente A puo emettere al massimo 5 ordini al giorno.";
    private const string Statement2 = "Il cliente A puo emettere al massimo 8 ordini al giorno.";

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;
        options.ProjectQuotaBytes = 50_000;
    }

    // ================================================================ scenari

    [Fact]
    public async Task Document_only_run_with_cutoff_zero_sends_one_call_and_commits_revision_completed_and_links_atomically()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        var input = await PrepareAsync(run);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Respond(input) };

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        var request = Assert.Single(model.Requests);
        Assert.Contains(Statement1, request.AllText);
        Assert.Contains($"fonte:{input.Evidence[0].Sources[0].SourceId}", request.AllText);
        Assert.Contains(input.BatchKey, request.AllText);
        Assert.Contains(input.InputHash, request.AllText);
        // T067: il server fornisce l'elenco gia ordinato (con il conteggio) degli entryId da copiare: il modello reale non sa ordinare/ricopiare hash sparsi.
        var entryIds = input.Evidence.Select(e => ReconciliationIds.EntryId(input.Snapshot.SourceHash, e.EvidenceId, e.ContentHash)).Order(StringComparer.Ordinal).ToList();
        Assert.Contains($"visitedEntryIds ({entryIds.Count} elementi, gia in ordine alfabetico", request.AllText);
        Assert.Contains("[" + string.Join(",", entryIds.Select(id => "\"" + id + "\"")) + "]", request.AllText);
        Assert.DoesNotContain("evidenceKeys", request.System + request.AllText);
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));

        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r AND \"Origin\" = 'Ai' AND \"SchemaVersion\" = '1.1.0' AND \"Version\" = 1", ("r", run)));
        Assert.Equal(result.ResultRevisionId, await Scalar<Guid>("SELECT \"ResultRevisionId\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(run, await Scalar<Guid>("SELECT \"CurrentAnalysisId\" FROM \"Projects\" WHERE \"Id\" = @p", ("p", project)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r AND \"EvidenceId\" = @e", ("r", run), ("e", input.Evidence[0].EvidenceId)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));

        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(0, content.SourceCutoffSequence);
        Assert.Equal(doc.SourceId, Assert.Single(content.Sources).Id);
        Assert.Equal(DocumentAnalysisClassification.Documented, content.Requirements[0].Classification);
    }

    [Fact]
    public async Task A_conflict_between_a_document_and_a_pm_message_stays_open_with_both_alternatives_and_no_automatic_choice()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        await SeedPmEvidenceAsync(client, project, "Il cliente A deve poter emettere almeno 10 ordini al giorno.");
        var run = await StartRunAsync(client, project);
        var input = await PrepareAsync(run);
        var pm = input.Evidence.Single(e => e.Origin == EvidenceOriginKind.Message);
        var document = input.Evidence.Single(e => e.Origin == EvidenceOriginKind.Document);
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = _ => Respond(input, o => o with { Analysis = WithOpenConflict(o.Analysis, pm, document) }),
        };

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        var conflict = Assert.Single(content.Conflicts);
        Assert.Equal(AnalysisConflictStatus.Open, conflict.Status);
        Assert.Null(conflict.Resolution);
        Assert.Equal(2, conflict.Alternatives.Count);
        Assert.Contains(conflict.Alternatives, a => a.Classification == DocumentAnalysisClassification.Declared);
        Assert.Contains(conflict.Alternatives, a => a.Classification == DocumentAnalysisClassification.Documented);
        Assert.Equal(2, content.Sources.Count);
        var request = Assert.Single(model.Requests);
        Assert.Contains("Il cliente A deve poter emettere almeno 10 ordini al giorno.", request.AllText);
        Assert.Equal(2, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
    }

    [Theory]
    [InlineData("source_outside_snapshot")]
    [InlineData("extra_property")]
    [InlineData("truncated_json")]
    [InlineData("wrong_cutoff")]
    [InlineData("source_ids_as_evidence_ids")]
    public async Task An_invalid_output_fails_the_run_permanently_with_no_revision_no_reusable_output_and_no_second_call(string kind)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        var input = await PrepareAsync(run);
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = _ => kind switch
            {
                "truncated_json" => Respond(input)[..40],
                "extra_property" => Mutate(Respond(input), n => n["extra"] = 1),
                "wrong_cutoff" => Respond(input, o => o with { Analysis = o.Analysis with { SourceCutoffSequence = 7 } }),
                // T067 (prova reale, N2): il modello ha scritto nella mappa gli id delle FONTI al posto degli id delle EVIDENZE; deve essere work_output_invalid, mai model_error.
                "source_ids_as_evidence_ids" => Respond(input, o => o with { EvidenceMap = [.. o.EvidenceMap.Select(m => m with { EvidenceIds = [.. input.Evidence.Where(e => m.EvidenceIds.Contains(e.EvidenceId)).SelectMany(e => e.Sources.Select(x => x.SourceId))] })] }),
                _ => Respond(input, o => o with { Analysis = o.Analysis with { Sources = [.. o.Analysis.Sources.Select(s => s with { Id = Guid.NewGuid() })] } }),
            },
        };

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
        Assert.Single(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"Status\" = 'Committed'"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Projects\" WHERE \"Id\" = @p AND \"CurrentAnalysisId\" IS NOT NULL", ("p", project)));
    }

    [Fact]
    public async Task A_valid_output_with_unmapped_evidence_is_persisted_as_reusable_but_never_completes_the_run()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        var input = await PrepareAsync(run);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Respond(input, o => o with { EvidenceMap = [] }) };

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, result.ErrorCode);
        Assert.Single(model.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\""));
    }

    [Fact]
    public async Task A_complete_claim_by_the_model_is_not_trusted_when_a_visit_is_missing()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        var input = await PrepareAsync(run);
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = _ => Respond(input, o => o with { Results = [o.Results[0], o.Results[1] with { VisitedEntryIds = [], NoFindingsReason = "tutto visitato, complete" }, o.Results[2]] }),
        };

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, result.ErrorCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task Applied_relation_patches_are_stored_append_only_in_the_same_transaction_as_the_revision()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        await NewDocument(client, project, "altre", Statement2);
        var run = await StartRunAsync(client, project);
        var input = await PrepareAsync(run);
        var (first, second) = (input.Evidence[0], input.Evidence[1]);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Respond(input, o => WithRelationPatch(o, input, first, second)) };

        var result = await RunAsync(run, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Scalar<string>("SELECT coalesce(\"ErrorMessage\",'') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(1, await Count(
            "SELECT count(*) FROM \"AnalysisEvidenceRelations\" WHERE \"RunId\" = @r AND \"FromEvidenceId\" = @f AND \"ToEvidenceId\" = @t AND \"Type\" = 'Contradicts' AND \"Basis\" = 'Inferred'",
            ("r", run), ("f", first.EvidenceId), ("t", second.EvidenceId)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));

        await Assert.ThrowsAnyAsync<Exception>(() => Execute("UPDATE \"AnalysisEvidenceRelations\" SET \"Rationale\" = 'x' WHERE \"RunId\" = @r", ("r", run)));
        await Assert.ThrowsAnyAsync<Exception>(() => Execute("DELETE FROM \"AnalysisEvidenceRelations\" WHERE \"RunId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task Incompatible_alternatives_are_kept_in_an_open_conflict_and_a_missing_conflict_blocks_completion()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        var input = await PrepareAsync(run);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Respond(input, o => WithAlternativePatches(o, input, represented: false)) };

        var blocked = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Failed, blocked.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, blocked.ErrorCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));

        var project2 = await NewProject(client);
        await NewDocument(client, project2, "regole2", Statement1);
        var run2 = await StartRunAsync(client, project2);
        var input2 = await PrepareAsync(run2);
        var ok = await RunAsync(run2, new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Respond(input2, o => WithAlternativePatches(o, input2, represented: true)) });

        Assert.Equal(ProcessingRunStatus.Completed, ok.Status);
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run2)));
        Assert.Equal(2, Assert.Single(content.Conflicts).Alternatives.Count);
        Assert.Null(content.Conflicts[0].Resolution);
    }

    [Fact]
    public async Task Running_the_same_run_again_after_completion_returns_the_committed_result_without_a_second_call_or_revision()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        var input = await PrepareAsync(run);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Respond(input) };
        var first = await RunAsync(run, model);

        var second = await RunAsync(run, model);

        Assert.Equal(first.ResultRevisionId, second.ResultRevisionId);
        Assert.Equal(ProcessingRunStatus.Completed, second.Status);
        Assert.Single(model.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task A_stale_fencing_token_touches_nothing_and_never_reaches_the_model()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        var claim = await ClaimAsync(run);
        var model = new DocumentChatWorkflowTests.FakeChatModel();

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim with { FencingToken = claim.FencingToken + 5 }, model));

        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\""));
        Assert.Equal("Running", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_run_with_the_001_definition_is_incompatible_and_never_reaches_the_model()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        await Execute("UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = 'project-analysis-v3' WHERE \"Id\" = @r", ("r", run));
        var model = new DocumentChatWorkflowTests.FakeChatModel();

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal("workflow_incompatible", result.ErrorCode);
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData("""{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v3"}""")]   // solo single-call: gerarchia e policy mancanti
    [InlineData("""{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v3","reconcile":"document-reconcile-v3","reconcileAiSchema":"reconcile-alias-1","reconcileAlgorithm":"global-reconcile-v1","reconcileCatalog":"reconcile-alias-catalog-1"}""")]   // policy mancante
    [InlineData("""{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v3","reconcile":"document-reconcile-v4","reconcileAiSchema":"reconcile-alias-2","reconcileAlgorithm":"global-reconcile-v2","reconcileCatalog":"reconcile-alias-catalog-1","reconcileGapPolicy":"hierarchical-gap-incomplete-1"}""")]   // gerarchia nominale futura
    [InlineData("""{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v3","reconcile":"document-reconcile-v3","reconcileAiSchema":"reconcile-alias-1","reconcileAlgorithm":"global-reconcile-v1","reconcileCatalog":"reconcile-alias-catalog-1","reconcileGapPolicy":"hierarchical-gap-incomplete-2"}""")]   // policy diversa
    [InlineData("""{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v3","reconcileAiSchema":"reconcile-alias-1","reconcileAlgorithm":"global-reconcile-v1","reconcileCatalog":"reconcile-alias-catalog-1","reconcileGapPolicy":"hierarchical-gap-incomplete-1"}""")]   // reconcile mancante
    [InlineData("""{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v3","reconcile":"document-reconcile-v3","reconcileAlgorithm":"global-reconcile-v1","reconcileCatalog":"reconcile-alias-catalog-1","reconcileGapPolicy":"hierarchical-gap-incomplete-1"}""")]   // schema AI gerarchico mancante
    [InlineData("""{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v3","reconcile":"document-reconcile-v3","reconcileAiSchema":"reconcile-alias-1","reconcileCatalog":"reconcile-alias-catalog-1","reconcileGapPolicy":"hierarchical-gap-incomplete-1"}""")]   // algoritmo gerarchico mancante
    [InlineData("""{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v3","reconcile":"document-reconcile-v3","reconcileAiSchema":"reconcile-alias-1","reconcileAlgorithm":"global-reconcile-v1","reconcileGapPolicy":"hierarchical-gap-incomplete-1"}""")]   // catalogo gerarchico mancante
    [InlineData("""{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v3","reconcile":"document-reconcile-v3","reconcileAiSchema":"reconcile-alias-1","reconcileAlgorithm":"global-reconcile-v1","reconcileCatalog":"reconcile-alias-catalog-1","reconcileGapPolicy":""}""")]   // policy vuota
    public async Task A_v5_run_without_the_full_hierarchical_versions_and_gap_policy_is_workflow_incompatible_and_never_reaches_the_model(string prompts)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        await Execute("UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = 'project-analysis-documents-v5', \"PromptVersions\" = @p::jsonb WHERE \"Id\" = @r", ("p", prompts), ("r", run));
        var model = new DocumentChatWorkflowTests.FakeChatModel();

        var result = await RunAsync(run, model);

        Assert.Equal((ProcessingRunStatus.Failed, "workflow_incompatible"), (result.Status, result.ErrorCode));
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkManifests\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task A_payload_beyond_the_capacity_of_one_call_fails_explicitly_before_any_model_call()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", string.Join(' ', Enumerable.Repeat("parola", 3_000)));
        var run = await StartRunAsync(client, project);
        var model = new DocumentChatWorkflowTests.FakeChatModel();

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        // T078: oltre una chiamata si passa alla riconciliazione gerarchica; una singola evidenza che non entra nemmeno da sola è irriducibile (ID e motivo, mai troncata), prima di ogni chiamata.
        Assert.Equal(DocumentWorkException.DocumentIncomplete, result.ErrorCode);
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task The_frozen_selection_is_written_once_and_holds_document_and_pm_versions_for_the_links()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        await SeedPmEvidenceAsync(client, project, "Il cliente A emette ordini.");
        var run = await StartRunAsync(client, project);

        await PrepareAsync(run);
        await PrepareAsync(run);

        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisEvidenceSelections\" WHERE \"RunId\" = @r", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisEvidenceSelectionItems\" WHERE \"RunId\" = @r AND \"OriginKind\" = 'Document' AND \"PreparationId\" IS NOT NULL", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisEvidenceSelectionItems\" WHERE \"RunId\" = @r AND \"OriginKind\" = 'Message' AND \"MessageId\" IS NOT NULL", ("r", run)));
    }

    // ================================================================ supporto: output del modello finto

    private static string Respond(DocumentAnalysisInput input, Func<SingleCallOutput, SingleCallOutput>? change = null)
    {
        var output = ValidOutput(input);
        return ReconciliationCodec.Encode(change is null ? output : change(output));
    }

    private static string Mutate(string json, Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        mutate(node);
        return node.ToJsonString();
    }

    private static DocumentAnalysisSource AnalysisSource(DocumentAnalysisEvidence e, PersistedSource s) => s.Kind == EvidenceSourceKind.Message
        ? new()
        {
            Id = s.SourceId, Kind = DocumentAnalysisSourceKind.Message, MessageId = s.MessageId, MessageRole = AnalysisMessageRole.User, RevisionId = null, DocumentId = null,
            DocumentVersionId = null, PreparationId = null, ArtifactId = null, BlockId = null, Start = null, End = null, Quote = null,
        }
        : new()
        {
            Id = s.SourceId, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = s.DocumentId,
            DocumentVersionId = s.DocumentVersionId, PreparationId = s.PreparationId, ArtifactId = s.ArtifactId, BlockId = s.BlockId, Start = s.Start, End = s.End, Quote = s.Quote,
        };

    private static ReconcileResult ResultOf(DocumentAnalysisInput input, ReconcileView view) => new()
    {
        BatchId = input.BatchKey, InputHash = input.InputHash, View = view, Level = 0,
        VisitedEntryIds = [.. input.Snapshot.Evidence.Select(e => ReconciliationIds.EntryId(input.Snapshot.SourceHash, e.EvidenceId, e.ContentHash)).Order(StringComparer.Ordinal)],
        VisitedNodeIds = [], Candidates = [], Dispositions = [], ItemProposals = [], Patches = [], Descriptor = null,
        NoFindingsReason = "nessun collegamento oltre quelli già noti",
    };

    private static SingleCallOutput ValidOutput(DocumentAnalysisInput input)
    {
        var sources = input.Evidence.SelectMany(e => e.Sources.Select(s => AnalysisSource(e, s))).ToList();
        var items = input.Evidence.Select(e => new DocumentAnalysisItem
        {
            Id = ItemId(e.EvidenceId), Text = e.Statement,
            Classification = e.Origin == EvidenceOriginKind.Message ? DocumentAnalysisClassification.Declared : DocumentAnalysisClassification.Documented,
            EvidenceRefs = [.. e.Sources.Select(s => s.SourceId)],
        }).ToList();
        return new()
        {
            Results = [ResultOf(input, ReconcileView.A), ResultOf(input, ReconcileView.B), ResultOf(input, ReconcileView.Cross)],
            Analysis = new DocumentAnalysisContent
            {
                SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = input.AnalysisId, ProjectId = input.ProjectId, SourceCutoffSequence = input.Cutoff,
                ValidationState = AnalysisContentState.Unvalidated, Sources = sources, Goals = [],
                ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
                Requirements = items, OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = [],
            },
            EvidenceMap = [.. input.Evidence.Select(e => new EvidenceMapEntry { ItemId = ItemId(e.EvidenceId), EvidenceIds = [e.EvidenceId] })],
        };
    }

    private static Guid ItemId(Guid evidence) => new(System.Security.Cryptography.SHA256.HashData(evidence.ToByteArray())[..16]);

    private static DocumentAnalysisContent WithOpenConflict(DocumentAnalysisContent analysis, DocumentAnalysisEvidence pm, DocumentAnalysisEvidence document) =>
        analysis with
        {
            // Le due posizioni stanno solo nel conflitto: nessuna scelta automatica, nessuna risoluzione.
            Requirements = [],
            Conflicts =
            [
                new DocumentAnalysisConflict
                {
                    Id = Guid.NewGuid(), Description = "Ordini al giorno del cliente A", Status = AnalysisConflictStatus.Open, Resolution = null,
                    Alternatives =
                    [
                        new DocumentAnalysisItem { Id = ItemId(pm.EvidenceId), Text = pm.Statement, Classification = DocumentAnalysisClassification.Declared, EvidenceRefs = [.. pm.Sources.Select(s => s.SourceId)] },
                        new DocumentAnalysisItem { Id = ItemId(document.EvidenceId), Text = document.Statement, Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [.. document.Sources.Select(s => s.SourceId)] },
                    ],
                },
            ],
        };

    private static SingleCallOutput WithRelationPatch(SingleCallOutput output, DocumentAnalysisInput input, DocumentAnalysisEvidence from, DocumentAnalysisEvidence to)
    {
        var proposal = new ItemProposal
        {
            LocalKey = "r1", Text = from.Statement, Classification = DocumentAnalysisClassification.Documented,
            EvidenceIds = [from.EvidenceId], SourceIds = [.. from.Sources.Select(s => s.SourceId)],
        };
        var withItem = output with { Results = [output.Results[0] with { ItemProposals = [proposal], NoFindingsReason = null }, output.Results[1], output.Results[2]] };
        var baseState = DocumentAnalysisBase.Compute(withItem, input.Snapshot);
        var evidence = new[] { from.EvidenceId, to.EvidenceId }.OrderBy(g => g.ToString("D"), StringComparer.Ordinal).ToList();
        var sources = from.Sources.Concat(to.Sources).Select(s => s.SourceId).OrderBy(g => g.ToString("D"), StringComparer.Ordinal).ToList();
        var patch = ReconciliationFixtures.WithId(new Patch
        {
            PatchId = "x", BaseHash = baseState.BaseHash,
            Target = new PatchTarget { Kind = PatchTargetKind.Item, Id = "r1", ExpectedHash = baseState.TargetHashes["Item:r1"] },
            Operation = PatchOperation.AddRelation,
            Value = new AddRelationValue { FromEvidenceId = from.EvidenceId, ToEvidenceId = to.EvidenceId, Type = EvidenceRelationType.Contradicts, Basis = EvidenceRelationBasis.Inferred, Rationale = "limiti diversi" },
            EvidenceIds = evidence, SourceIds = sources, OriginBatchIds = [input.BatchKey],
        });
        var candidate = CandidateFor(input, from, to);
        var result = withItem.Results[0] with
        {
            Patches = [patch], Candidates = [candidate],
            Dispositions = [new CandidateDisposition { CandidateId = candidate.CandidateId, State = CandidateState.Compared, ChildCandidateIds = [], ComparisonOutputHashes = [input.BatchKey], Reason = "confronto integrale" }],
        };
        return withItem with { Results = [result, withItem.Results[1], withItem.Results[2]] };
    }

    private static ReconcileCandidate CandidateFor(DocumentAnalysisInput input, DocumentAnalysisEvidence from, DocumentAnalysisEvidence to)
    {
        CandidateEndpoint Endpoint(DocumentAnalysisEvidence e) => new() { Kind = CandidateEndpointKind.Evidence, Id = e.EvidenceId.ToString("D"), Hash = e.ContentHash };
        var (left, right) = (Endpoint(from), Endpoint(to));
        return new()
        {
            CandidateId = ReconciliationIds.CandidateId(CandidateKind.Conflict, left, right, ReconcileProposedType.Contradicts, input.Snapshot.SourceHash),
            Kind = CandidateKind.Conflict, Left = left, Right = right, ProposedType = ReconcileProposedType.Contradicts, Rationale = "limiti diversi", Basis = EvidenceRelationBasis.Inferred,
            OriginBatchIds = [input.BatchKey], ParentCandidateId = null,
        };
    }

    private static SingleCallOutput WithAlternativePatches(SingleCallOutput output, DocumentAnalysisInput input, bool represented)
    {
        var e = input.Evidence[0];
        var proposal = new ItemProposal
        {
            LocalKey = "r1", Text = e.Statement, Classification = DocumentAnalysisClassification.Documented, EvidenceIds = [e.EvidenceId], SourceIds = [.. e.Sources.Select(s => s.SourceId)],
        };
        var withItem = output with { Results = [output.Results[0] with { ItemProposals = [proposal], NoFindingsReason = null }, output.Results[1], output.Results[2]] };
        var baseState = DocumentAnalysisBase.Compute(withItem, input.Snapshot);
        Patch Alt(string text) => ReconciliationFixtures.WithId(new Patch
        {
            PatchId = "x", BaseHash = baseState.BaseHash,
            Target = new PatchTarget { Kind = PatchTargetKind.Item, Id = "r1", ExpectedHash = baseState.TargetHashes["Item:r1"] },
            Operation = PatchOperation.ProposeAlternative,
            Value = new ProposeAlternativeValue
            {
                Text = text, Classification = DocumentAnalysisClassification.Documented, EvidenceIds = [e.EvidenceId], SourceIds = [.. e.Sources.Select(s => s.SourceId)], Reason = "valore diverso",
            },
            EvidenceIds = [e.EvidenceId], SourceIds = [.. e.Sources.Select(s => s.SourceId)], OriginBatchIds = [input.BatchKey],
        });
        var analysis = withItem.Analysis;
        if (represented)
        {
            DocumentAnalysisItem AltItem(string text) => new()
            {
                Id = Guid.NewGuid(), Text = text, Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [.. e.Sources.Select(s => s.SourceId)],
            };
            analysis = analysis with
            {
                Conflicts = [new DocumentAnalysisConflict { Id = Guid.NewGuid(), Description = "Ordini al giorno", Status = AnalysisConflictStatus.Open, Resolution = null, Alternatives = [AltItem("al massimo 8 ordini"), AltItem("al massimo 9 ordini")] }],
            };
        }

        return withItem with { Analysis = analysis, Results = [withItem.Results[0] with { Patches = [Alt("al massimo 8 ordini"), Alt("al massimo 9 ordini")] }, withItem.Results[1], withItem.Results[2]] };
    }

    // ================================================================ supporto: dati e esecuzione

    private sealed record Doc(Guid Document, Guid Version, Guid SourceId);

    private async Task<Doc> NewDocument(HttpClient client, Guid project, string name, string statement)
    {
        var created = await Json(await Upload(client, project, Text(name), name + ".txt", displayName: name));
        var doc = new Doc(created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid(), Guid.NewGuid());
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == doc.Version, Ct);
        var now = DateTimeOffset.UtcNow;
        var (job, version) = (Guid.NewGuid(), Guid.NewGuid());
        db.EvidenceExtractionJobs.Add(new EvidenceExtractionJob
        {
            Id = job, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Reason = ExtractionJobReason.Initial,
            Status = ExtractionJobStatus.Queued, RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
        });
        db.EvidenceExtractionVersions.Add(new EvidenceExtractionVersion
        {
            Id = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, ManifestHash = new string('1', 64),
            Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = 1, CreatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        var content = new DocumentEvidenceContent
        {
            SchemaVersion = DocumentEvidenceContent.SchemaVersionValue,
            KindRationale = null,
            Structure = new DocumentEvidenceStructure { Subject = null, Action = null, Object = null, Scope = null, Purpose = null, Instrument = null, Channel = null, Recipient = null },
            Qualifiers = [],
            Sources =
            [
                new PersistedSource
                {
                    SourceId = doc.SourceId, Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Guid.NewGuid(), Part = 0,
                    MessageId = null, Sequence = null, DocumentId = doc.Document, DocumentVersionId = doc.Version, PreparationId = preparation.Id,
                    ArtifactId = Guid.NewGuid(), BlockId = Guid.NewGuid(), Start = 0, End = statement.Length, Quote = statement,
                },
            ],
        };
        db.Evidences.Add(new EvidenceItem
        {
            Id = Guid.NewGuid(), VersionId = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Ordinal = 1,
            Key = "e1", Kind = EvidenceKind.Requirement, Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = statement,
            Content = DocumentEvidenceCodec.EncodeContent(content),
        });
        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = doc.Version, ProjectId = preparation.ProjectId, CurrentPreparationId = preparation.Id, CurrentEvidenceVersionId = version, Revision = 1, UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        preparation.Status = PreparationStatus.Ready;
        preparation.CurrentEvidenceVersionId = version;
        await db.SaveChangesAsync(Ct);
        await db.ProjectDocuments.Where(d => d.Id == doc.Document).ExecuteUpdateAsync(x => x.SetProperty(d => d.LatestReadyVersionId, doc.Version), Ct);
        return doc;
    }

    /// <summary>Messaggio PM con evidenza estratta dal percorso reale del registro (claim e commit), come per la 001.</summary>
    private async Task SeedPmEvidenceAsync(HttpClient client, Guid project, string text)
    {
        var post = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
    }

    private async Task<Guid> StartRunAsync(HttpClient client, Guid project)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = (await Json(response)).GetProperty("id").GetGuid();
        // T125: le nuove richieste congelano la v3 con alias. Questi test provano il workflow v2 (storico, frozen, invariato) su un run richiesto prima della
        // v3: definizione e prompt v2 congelati sul root. Nessuna asserzione di T059 è cambiata; il contratto v3 è in DocumentAnalysisAliasWorkflowTests.
        await Execute(
            "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = 'project-analysis-documents-v2', \"PromptVersions\" = '{\"analysis\":\"document-analysis-v2\",\"analysisSchema\":\"1.1.0\"}'::jsonb WHERE \"Id\" = @r",
            ("r", run));
        return run;
    }

    /// <summary>Presa in carico come il dispatcher: Running con owner, fence e lease validi (il claim reale non è parte di T059).</summary>
    private async Task<AnalysisRunClaim> ClaimAsync(Guid run)
    {
        await Execute(
            """
            UPDATE "ProcessingRuns" SET "Status" = 'Running', "LeaseOwner" = 'test-owner', "FencingToken" = 1, "AttemptCount" = 1, "StartedAt" = now(),
                   "LeaseUntil" = now() + interval '10 minutes'
            WHERE "Id" = @r AND "Status" = 'Queued'
            """,
            ("r", run));
        return new AnalysisRunClaim(run, "test-owner", 1, 1);
    }

    /// <summary>Congela la selezione (idempotente) e carica lo snapshot che il modello finto deve citare.</summary>
    private async Task<DocumentAnalysisInput> PrepareAsync(Guid run)
    {
        var claim = await ClaimAsync(run);
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var frozen = await new EvidenceLedgerStore(db).FreezeDocumentSelectionAsync(claim, Ct);
        Assert.True(frozen.Outcome is SelectionOutcome.Frozen or SelectionOutcome.Existing, frozen.Outcome.ToString());
        return await DocumentAnalysisSources.LoadAsync(db, run, Ct);
    }

    private async Task<AnalysisRunResult> RunAsync(Guid run, DocumentChatWorkflowTests.FakeChatModel model) => await RunAsync(await ClaimAsync(run), model);

    private async Task<AnalysisRunResult> RunAsync(AnalysisRunClaim claim, DocumentChatWorkflowTests.FakeChatModel model)
    {
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = DocumentChatWorkflowTests.FakeChatModel.Window,
            MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        using var client = new InternalModelClient(options, model);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        var workflow = new DocumentAnalysisWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(new DocumentOptions()));
        return await workflow.ExecuteAsync(claim, Ct);
    }
}
