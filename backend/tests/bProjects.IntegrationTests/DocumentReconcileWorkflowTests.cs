using System.Text.Json;
using System.Text.RegularExpressions;
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
/// T078 (PG + modello FINTO) — la riconciliazione gerarchica collegata al grafo fisso T043, al port T044, a WorkOutput/DerivedBatch e alle proiezioni T122.
/// Un'analisi oltre la capacità di una chiamata percorre più batch, ciascuno in una sessione nuova (un solo messaggio, nessuna storia accumulata); ripresa e
/// replay riusano gli output Committed senza nodi nuovi; una promozione valida mantiene t0 e cap; non esiste un Completed parziale.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentReconcileWorkflowTests : ReconcileRig
{
    private const int Documents = 8;

    private static string Statement(int n) => $"Requisito {n}: " + string.Join(' ', Enumerable.Range(0, 120).Select(i => $"w{n}x{i}"));

    // ================================================================ scenari

    [Fact]
    public async Task An_analysis_above_one_call_runs_isolated_batches_and_commits_the_revision_with_projections_atomically()
    {
        var (run, claim) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Respond(r) };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.True(model.Requests.Count > 1);
        Assert.All(model.Requests, r => Assert.Single(r.Messages));   // sessione nuova per invio: nessuna storia accumulata
        var parsed = model.Requests.Select(r => Parse(r.AllText)).ToList();
        Assert.Equal(parsed.Count, parsed.Select(p => p.BatchId).Distinct().Count());
        Assert.Equal(Documents, parsed.Where(p => p.Stage == "leaf" && p.View == ReconcileView.A).SelectMany(p => p.Voci).Select(v => v.Evidence).Distinct().Count());
        Assert.Contains(parsed, p => p.Stage == "cross");
        // Nessun batch porta il testo delle voci di un altro: ogni enunciato compare solo nelle richieste che lo riaprono.
        Assert.All(parsed.Where(p => p.Stage is "reduce" or "cross"), p => Assert.Empty(p.Voci));

        Assert.Equal(model.Requests.Count, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(model.Requests.Count, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r AND \"SchemaVersion\" = '1.1.0'", ("r", run)));
        Assert.Equal(Documents, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
        Assert.Equal(2 * Documents, await Count("SELECT count(*) FROM \"RunCatalogMembers\" WHERE \"RunId\" = @r", ("r", run)));
        Assert.True(await Count("SELECT count(*) FROM \"RunCatalogNodes\" WHERE \"RunId\" = @r", ("r", run)) >= 3);
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(Documents, content.Requirements.Count);
        Assert.Empty(content.Conflicts);
    }

    [Fact]
    public async Task Replaying_a_completed_run_and_resuming_an_interrupted_one_send_nothing_already_committed()
    {
        var (run, claim) = await ArrangeAsync();
        var seen = 0;
        var failing = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => Interlocked.Increment(ref seen) == 4 ? throw new InvalidOperationException("rete caduta") : Respond(r),
        };

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, failing));
        Assert.Equal(3, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));

        var healthy = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Respond(r) };
        var resumed = await RunAsync(claim, healthy);

        Assert.True(resumed.Status == ProcessingRunStatus.Completed, await Message(run));
        var all = healthy.Requests.Count + 3;
        Assert.Equal(all, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(all, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r", ("r", run)));
        var committedBefore = failing.Requests.Take(3).Select(r => Parse(r.AllText).BatchId).ToHashSet();
        Assert.Equal(3, committedBefore.Count);
        Assert.DoesNotContain(healthy.Requests.Select(r => Parse(r.AllText).BatchId), committedBefore.Contains);   // nessun batch già Committed è reinviato

        var again = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Respond(r) };
        var replay = await RunAsync(claim, again);
        Assert.Equal(resumed.ResultRevisionId, replay.ResultRevisionId);
        Assert.Empty(again.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_valid_output_that_leaves_an_evidence_unmapped_is_kept_but_never_completes_the_run()
    {
        var (run, claim) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => Respond(r, (p, result) => result with { ItemProposals = [.. result.ItemProposals.Skip(1)], NoFindingsReason = result.ItemProposals.Count > 1 ? null : "nessun collegamento emerso" }),
        };

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, result.ErrorCode);
        Assert.Equal(model.Requests.Count, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"RunCatalogNodes\" WHERE \"RunId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task An_invalid_batch_output_is_a_permanent_error_with_no_work_output_and_no_repair()
    {
        var (run, claim) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => Respond(r, (p, result) => p.Stage == "reduce" ? result with { VisitedNodeIds = [] } : result),
        };

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
        Assert.Equal(model.Requests.Count - 1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task N4_regression_an_open_30_vs_45_conflict_proposed_by_the_fake_model_is_kept_as_an_open_conflict_with_both_alternatives()
    {
        var (run, claim) = await ArrangeAsync(sharedKey: true);
        var known = new Dictionary<int, (Guid Evidence, string Hash)>();
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => Respond(r, (p, result) =>
            {
                foreach (var v in p.Voci)
                {
                    known[int.Parse(Regex.Match(v.Statement, @"Requisito (\d+):").Groups[1].Value)] = (v.Evidence, v.Hash);
                }

                return p.Stage switch
                {
                    // L'espansione che riapre il documento 1 trova il collegamento con il documento 8, che sta in un'altra foglia (documenti e posizioni lontani).
                    "expand" when p.Voci.Any(v => v.Statement.StartsWith("Requisito 1:", StringComparison.Ordinal)) && known.ContainsKey(8) =>
                        result with { Candidates = [Link(known[1], known[8], p.FocusId!, p.BatchId, p.SourceHash)], NoFindingsReason = null },
                    "compare" => result with
                    {
                        Dispositions =
                        [
                            new CandidateDisposition
                            {
                                CandidateId = p.FocusId!, State = CandidateState.Open, ChildCandidateIds = [], ComparisonOutputHashes = [p.InputHash],
                                Reason = "30 giorni nel documento contro 45 giorni dichiarati: alternative integrali da sottoporre al PM",
                            },
                        ],
                    },
                    _ => result,
                };
            }),
        };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        var conflict = Assert.Single(content.Conflicts);
        Assert.Equal(AnalysisConflictStatus.Open, conflict.Status);
        Assert.Null(conflict.Resolution);
        Assert.Equal(2, conflict.Alternatives.Count);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"RunCandidateDispositions\" WHERE \"RunId\" = @r AND \"State\" = 'Open'", ("r", run)));
    }

    [Fact]
    public async Task A_valid_promotion_keeps_t0_and_the_hard_cap_and_does_not_reset_the_counters()
    {
        var (run, claim) = await ArrangeAsync(ordinary: true);
        var before = await Scalar<DateTime>("SELECT \"RequestedAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));
        var hardBefore = await Scalar<DateTime>("SELECT \"HardDeadlineAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Respond(r) };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Equal(("Extended", 1), (await Scalar<string>("SELECT \"Regime\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)), await Scalar<int>("SELECT \"PromotionCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run))));
        Assert.Equal(before, await Scalar<DateTime>("SELECT \"RequestedAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(hardBefore, await Scalar<DateTime>("SELECT \"HardDeadlineAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"RegimeTransitions\" WHERE \"RootId\" = @r AND \"Reason\" = 'additional_work'", ("r", run)));
        Assert.Equal(model.Requests.Count, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal("Ordinary", await Scalar<string>("SELECT \"InitialRegime\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_payload_that_fits_in_one_call_still_uses_the_single_call_path_without_hierarchical_batches()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "unico", "Il cliente A puo emettere al massimo 5 ordini al giorno.");
        var run = await StartRunAsync(client, project);
        var claim = await ClaimAsync(run);
        var input = await PrepareAsync(claim);
        var single = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => ReconciliationCodec.Encode(SingleCall(input)) };

        var result = await RunAsync(claim, single);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Single(single.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"RunCatalogNodes\" WHERE \"RunId\" = @r", ("r", run)));
    }

    private static SingleCallOutput SingleCall(DocumentAnalysisInput input)
    {
        var e = input.Evidence[0];
        var s = input.Snapshot.Evidence[0].Sources[0];
        ReconcileResult Result(ReconcileView view, bool item) => new()
        {
            BatchId = input.BatchKey, InputHash = input.InputHash, View = view, Level = 0,
            VisitedEntryIds = [ReconciliationIds.EntryId(input.Snapshot.SourceHash, e.EvidenceId, e.ContentHash)], VisitedNodeIds = [], Candidates = [], Dispositions = [],
            ItemProposals = item ? [new ItemProposal { LocalKey = "r1", Text = e.Statement, Classification = DocumentAnalysisClassification.Documented, EvidenceIds = [e.EvidenceId], SourceIds = [s.SourceId] }] : [],
            Patches = [], Descriptor = null, NoFindingsReason = item ? null : "nessun collegamento",
        };
        var itemId = Guid.Parse("40000000-0000-4000-8000-000000000001");
        var analysis = new DocumentAnalysisContent
        {
            SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = input.AnalysisId, ProjectId = input.ProjectId, SourceCutoffSequence = input.Cutoff,
            ValidationState = AnalysisContentState.Unvalidated,
            Sources = [new DocumentAnalysisSource
            {
                Id = s.SourceId, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = s.DocumentId, DocumentVersionId = s.DocumentVersionId,
                PreparationId = s.PreparationId, ArtifactId = s.ArtifactId, BlockId = s.BlockId, Start = s.Start, End = s.End, Quote = s.Quote,
            }],
            Goals = [], ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = [new DocumentAnalysisItem { Id = itemId, Text = e.Statement, Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [s.SourceId] }],
            OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = [],
        };
        return new SingleCallOutput
        {
            Results = [Result(ReconcileView.A, true), Result(ReconcileView.B, false), Result(ReconcileView.Cross, false)], Analysis = analysis,
            EvidenceMap = [new EvidenceMapEntry { ItemId = itemId, EvidenceIds = [e.EvidenceId] }],
        };
    }

    // ================================================================ T132: tetti dalla policy congelata e patch Applied

    [Fact]
    public async Task The_frozen_policy_level_cap_stops_the_hierarchy_with_coverage_incomplete_and_commits_no_revision()
    {
        var (run, claim) = await ArrangeAsync();
        await SetLimitsAsync(l => l with { MaxReconciliationLevels = 1 });
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Respond(r) };

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, result.ErrorCode);
        Assert.Contains("livelli congelati", await Message(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task The_frozen_policy_candidate_cap_stops_the_run_when_a_proposed_candidate_exceeds_it()
    {
        var (run, claim) = await ArrangeAsync(sharedKey: true);
        await SetLimitsAsync(l => l with { MaxCandidatesPerRun = 1 });
        var model = N4Model(compared: false);

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, result.ErrorCode);
        Assert.Contains("oltre 1 candidati", await Message(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task The_frozen_policy_derived_bytes_cap_stops_the_run_before_the_next_candidate_batch()
    {
        var (run, claim) = await ArrangeAsync(sharedKey: true);
        await SetLimitsAsync(l => l with { MaxDerivedBytes = 1 });
        var model = N4Model(compared: false);

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, result.ErrorCode);
        Assert.Contains("byte derivati esauriti", await Message(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_valid_applied_patch_composed_on_the_server_base_is_stored_and_completes_the_run_with_its_relation()
    {
        var (run, claim) = await ArrangeAsync(sharedKey: true);
        var snapshot = (await PrepareAsync(claim)).Snapshot;
        var model = N4Model(compared: true, snapshot);

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"RunPatchDispositions\" WHERE \"RunId\" = @r AND \"Kind\" = 'Applied'", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"RunPatchDispositions\" WHERE \"RunId\" = @r AND \"Kind\" <> 'Applied'", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisEvidenceRelations\" WHERE \"RunId\" = @r AND \"Type\" = 'Confirms' AND \"Basis\" = 'Inferred'", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    /// <summary>Modello finto del corpus «sharedKey»: l'espansione collega i documenti 1 e 8; il confronto è Compared e, se richiesto, porta una patch AddRelation sulla base calcolata dal server.</summary>
    private DocumentChatWorkflowTests.FakeChatModel N4Model(bool compared, ReconciliationSnapshot? snapshot = null)
    {
        var known = new Dictionary<int, (Guid Evidence, string Hash)>();
        var items = new List<ItemProposal>();
        return new()
        {
            Responder = r => Respond(r, (p, result) =>
            {
                foreach (var v in p.Voci)
                {
                    known[int.Parse(Regex.Match(v.Statement, @"Requisito (\d+):").Groups[1].Value)] = (v.Evidence, v.Hash);
                    if (p.Stage == "leaf" && p.View == ReconcileView.A)
                    {
                        items.Add(new ItemProposal { LocalKey = $"k-{v.Evidence:N}", Text = v.Statement, Classification = DocumentAnalysisClassification.Documented, EvidenceIds = [v.Evidence], SourceIds = [v.Source] });
                    }
                }

                switch (p.Stage)
                {
                    case "expand" when p.Voci.Any(v => v.Statement.StartsWith("Requisito 1:", StringComparison.Ordinal)) && known.ContainsKey(8):
                        return result with { Candidates = [Link(known[1], known[8], p.FocusId!, p.BatchId, p.SourceHash)], NoFindingsReason = null };
                    case "compare":
                        var disposition = new CandidateDisposition { CandidateId = p.FocusId!, State = CandidateState.Compared, ChildCandidateIds = [], ComparisonOutputHashes = [p.InputHash], Reason = "confronto integrale" };
                        if (!compared || snapshot is null)
                        {
                            return result with { Dispositions = [disposition] };
                        }

                        var baseState = DocumentAnalysisBase.Compute(items, snapshot);
                        var (from, to) = (p.Voci.Single(v => v.Evidence == known[1].Evidence), p.Voci.Single(v => v.Evidence == known[8].Evidence));
                        var key = $"k-{from.Evidence:N}";
                        var patch = ReconciliationFixtures.WithId(new Patch
                        {
                            PatchId = "x", BaseHash = baseState.BaseHash,
                            Target = new PatchTarget { Kind = PatchTargetKind.Item, Id = key, ExpectedHash = baseState.TargetHashes[ReconciliationBase.TargetKey(PatchTargetKind.Item, key)] },
                            Operation = PatchOperation.AddRelation,
                            Value = new AddRelationValue { FromEvidenceId = from.Evidence, ToEvidenceId = to.Evidence, Type = EvidenceRelationType.Confirms, Basis = EvidenceRelationBasis.Inferred, Rationale = "stesso requisito ribadito" },
                            EvidenceIds = [.. ReconciliationIds.SortedIds([from.Evidence, to.Evidence]).Select(Guid.Parse)],
                            SourceIds = [.. new[] { from.Source, to.Source }.OrderBy(g => g.ToString("D"), StringComparer.Ordinal)], OriginBatchIds = [p.BatchId],
                        });
                        return result with { Dispositions = [disposition], Patches = [patch] };
                    default:
                        return result;
                }
            }),
        };
    }

    private async Task SetLimitsAsync(Func<DocumentLimits, DocumentLimits> change)
    {
        using var scope = App.Services.CreateScope();
        var (_, snapshot) = await scope.ServiceProvider.GetRequiredService<DocumentPolicyService>().GetAsync(Ct);
        var limits = change(snapshot.Limits);
        Assert.Empty(limits.Validate());
        await Execute("UPDATE \"DocumentPolicies\" SET \"LimitsJson\" = @j::jsonb, \"PolicyHash\" = @h WHERE \"Id\" = 1", ("j", limits.ToJson()), ("h", limits.Hash()));
    }

    // ================================================================ supporto: dati e esecuzione

    private async Task<(Guid Run, AnalysisRunClaim Claim)> ArrangeAsync(bool ordinary = false, bool sharedKey = false)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        for (var n = 1; n <= Documents; n++)
        {
            // Soggetti distinti ordinano le evidenze in A; con sharedKey i documenti 1 e 8 (primo e ultimo) condividono la chiave di scope e cadono in foglie diverse.
            var subject = n == 1 ? "aaa" : n == Documents ? "zzz" : $"mmm{n}";
            await NewDocument(client, project, $"doc{n}", Statement(n), subject, sharedKey && n is 1 or Documents ? "regola" : null);
        }

        var run = await StartRunAsync(client, project);
        if (ordinary)
        {
            await Execute(
                """
                UPDATE "ProcessingRuns" SET "Regime" = 'Ordinary', "InitialRegime" = 'Ordinary', "ClassificationStatus" = 'Classified', "PromotionCount" = 0,
                       "OrdinaryDeadlineAt" = "RequestedAt" + interval '180 seconds', "EffectiveDeadlineAt" = "RequestedAt" + interval '180 seconds',
                       "DeadlineAt" = "RequestedAt" + interval '180 seconds', "HardDeadlineAt" = "RequestedAt" + interval '3600 seconds', "CallCount" = 0,
                       "PolicyHash" = repeat('a', 64)
                WHERE "Id" = @r
                """,
                ("r", run));
        }

        return (run, await ClaimAsync(run));
    }

}
