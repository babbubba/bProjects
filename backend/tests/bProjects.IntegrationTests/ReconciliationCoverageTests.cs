using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using Cx = bProjects.IntegrationTests.ReconciliationCandidateTests;
using F = bProjects.IntegrationTests.ReconciliationFixtures;

namespace bProjects.IntegrationTests;

/// <summary>
/// T077 (DET) — coverage server del percorso gerarchico (AI §4.5) con output finti: insiemi attesi/visitati di voci A/B, nodi, pagine, candidati, relazioni,
/// patch e frontiera, più la mappa di ogni EvidenceId. Nessuna dichiarazione dell'AI vale come completezza; il gold semantico resta un FAIL separato.
/// </summary>
public sealed class ReconciliationCoverageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid ItemOf(Guid evidence, string suffix) => Guid.Parse(evidence.ToString("D")[..^2] + suffix);

    private static PersistedSource SourceOf(ReconciliationSnapshot s, Guid evidence) => s.Evidence.Single(e => e.EvidenceId == evidence).Sources[0];

    private sealed record Built(DocumentAnalysisContent Analysis, IReadOnlyList<EvidenceMapEntry> Map);

    /// <summary>Un item per evidenza e, se richiesto, un conflitto aperto con due alternative sulle fonti indicate (il 30 contro 45 giorni del caso N4).</summary>
    private static Built Analysis(ReconciliationSnapshot snapshot, params (Guid A, Guid B)[] conflicts)
    {
        DocumentAnalysisSource Passage(PersistedSource p) => new()
        {
            Id = p.SourceId, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = p.DocumentId,
            DocumentVersionId = p.DocumentVersionId, PreparationId = p.PreparationId, ArtifactId = p.ArtifactId, BlockId = p.BlockId, Start = p.Start, End = p.End, Quote = p.Quote,
        };

        var items = snapshot.Evidence.Select(e => new DocumentAnalysisItem
        {
            Id = ItemOf(e.EvidenceId, "cc"), Text = $"Requisito {e.EvidenceId:D}", Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [e.Sources[0].SourceId],
        }).ToList();
        var map = items.Select((item, i) => new EvidenceMapEntry { ItemId = item.Id, EvidenceIds = [snapshot.Evidence[i].EvidenceId] }).ToList();
        var conflictList = new List<DocumentAnalysisConflict>();
        foreach (var (a, b) in conflicts)
        {
            var alternatives = new[] { a, b }.Select((e, i) => new DocumentAnalysisItem
            {
                Id = ItemOf(e, i == 0 ? "dd" : "ee"), Text = i == 0 ? "30 giorni (documento)" : "45 giorni (dichiarato)", Classification = DocumentAnalysisClassification.Documented,
                EvidenceRefs = [SourceOf(snapshot, e).SourceId],
            }).ToList();
            conflictList.Add(new DocumentAnalysisConflict
            {
                Id = ItemOf(a, "ff"), Description = "30 giorni nel documento contro 45 giorni dichiarati", Status = AnalysisConflictStatus.Open, Alternatives = alternatives, Resolution = null,
            });
            map.AddRange(alternatives.Select((alt, i) => new EvidenceMapEntry { ItemId = alt.Id, EvidenceIds = [i == 0 ? a : b] }));
        }

        var analysis = new DocumentAnalysisContent
        {
            SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = Guid.Parse("a0000000-0000-4000-8000-000000000002"), ProjectId = F.Project, SourceCutoffSequence = 0,
            ValidationState = AnalysisContentState.Unvalidated, Sources = [.. snapshot.Evidence.SelectMany(e => e.Sources).DistinctBy(s => s.SourceId).Select(Passage)], Goals = [],
            ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = items, OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = conflictList,
        };
        return new(analysis, map);
    }

    private static readonly HierarchicalPatchComposition NoPatches = new(
        new PatchComposition([], [], [], [], false, "h"), new Dictionary<string, IReadOnlyList<string>>(), []);

    private static async Task<(Cx.Rig Rig, CandidateRun Run, FinalPassResult Final)> RunAsync(Cx.Model? model = null)
    {
        var rig = await Cx.SetupAsync(model ?? new Cx.Model());
        var run = await rig.Engine.RunPhasesAsync(Ct);
        var final = await ReconciliationFinalPass.RunAsync(rig.Engine, rig.Snapshot, rig.Directory, [], Ct);
        return (rig, run, final);
    }

    private static HierarchicalRun Input(Cx.Rig rig, FinalPassResult? final, HierarchicalPatchComposition? patches = null) =>
        new(rig.Snapshot, rig.Directory, rig.Hierarchy, final?.Run ?? rig.Engine.Result(), final, patches ?? NoPatches);

    private static ReconciliationCoverage Evaluate(HierarchicalRun input, Built built) => ReconciliationCompletion.EvaluateHierarchical(input, built.Analysis, built.Map);

    // ------------------------------------------------------------------ caso nominale e N4

    [Fact]
    public async Task A_full_run_with_the_open_conflict_represented_is_complete_with_an_open_point_and_the_server_sets_are_exact()
    {
        var (rig, _, final) = await RunAsync();
        var built = Analysis(rig.Snapshot, (Cx.Id(1), Cx.Id(30)));

        var coverage = Evaluate(Input(rig, final), built);

        Assert.Empty(coverage.Report.Gaps);
        Assert.True(coverage.IsComplete);
        Assert.Equal(30, coverage.Report.ExpectedEntryIds.Count);
        Assert.Equal(coverage.Report.ExpectedEntryIds, coverage.Report.VisitedEntryIdsA);
        Assert.Equal(coverage.Report.ExpectedEntryIds, coverage.Report.VisitedEntryIdsB);
        Assert.Empty(coverage.Report.MissingEntryIds);
        Assert.NotEmpty(coverage.ExpectedNodeIds);
        Assert.Equal(coverage.ExpectedNodeIds, coverage.VisitedNodeIds);
        Assert.Empty(coverage.MissingNodeIds);
        Assert.Equal(rig.Hierarchy.Pages.Count, coverage.PageHashes.Count);
        Assert.Equal(30, coverage.MappedEvidenceIds.Count);
        Assert.Empty(coverage.UnmappedEvidenceIds);
        Assert.Empty(coverage.UnresolvedCandidateIds);
        Assert.True(ReconciliationContractRules.IsHash(coverage.Report.CoverageHash));
        Assert.False(coverage.Report.AdditionalWorkJustified);
        Assert.False(coverage.Report.SemanticQualityCertified);   // la completezza strutturale non certifica il significato: il gold semantico è un FAIL separato
    }

    [Fact]
    public async Task N4_regression_the_30_vs_45_days_open_candidate_must_be_represented_otherwise_the_analysis_is_flagged_incomplete()
    {
        var (rig, run, final) = await RunAsync();
        var open = Assert.Single(final.Run.Dispositions, d => d.State == CandidateState.Open);
        Assert.Contains("30 giorni contro 45", open.Reason);
        Assert.Empty(run.Unresolved);

        var withoutConflict = Evaluate(Input(rig, final), Analysis(rig.Snapshot));
        var represented = Evaluate(Input(rig, final), Analysis(rig.Snapshot, (Cx.Id(1), Cx.Id(30))));

        var gap = Assert.Single(withoutConflict.Report.Gaps);
        Assert.Equal("open_candidate_not_represented", gap.Code);
        Assert.Contains(open.CandidateId, gap.Detail);
        Assert.False(withoutConflict.IsComplete);
        Assert.True(withoutConflict.Report.AdditionalWorkJustified);
        Assert.Empty(withoutConflict.Report.MissingEntryIds);   // visite al 100% ma conflitto non rappresentato
        Assert.True(represented.IsComplete);
    }

    // ------------------------------------------------------------------ visite al 100% non bastano

    [Fact]
    public async Task Full_visits_with_a_necessary_invalid_or_stale_patch_or_an_unrepresented_patch_conflict_do_not_complete()
    {
        var (rig, _, final) = await RunAsync();
        var built = Analysis(rig.Snapshot, (Cx.Id(1), Cx.Id(30)));
        var blocking = new HierarchicalPatchComposition(
            new PatchComposition([new("p-bad", PatchDispositionKind.Invalid, "endpoint")], [], [], ["p-bad"], false, "h"), new Dictionary<string, IReadOnlyList<string>>(),
            [new("p-bad", RunPatchDispositionKind.Invalid, null, "endpoint")]);

        var blocked = Evaluate(Input(rig, final, blocking), built);

        Assert.Empty(blocked.Report.MissingEntryIds);
        Assert.Contains(blocked.Report.Gaps, g => g.Code == "patch_blocking" && g.Detail.Contains("p-bad"));
        Assert.Equal(["p-bad"], blocked.BlockingPatchIds);
        Assert.False(blocked.IsComplete);

        var group = new ConflictGroup("g1", ConflictKind.SameRuleContradicts, ["pa", "pb"], [Cx.Id(2), Cx.Id(3)], [SourceOf(rig.Snapshot, Cx.Id(2)).SourceId, SourceOf(rig.Snapshot, Cx.Id(3)).SourceId],
            [new("pa", PatchOperation.AddRelation, "SameRule", ["b"]), new("pb", PatchOperation.AddRelation, "Contradicts", ["b"])], true);
        var preserved = new HierarchicalPatchComposition(
            new PatchComposition([new("pa", PatchDispositionKind.PreservedAsConflict, null), new("pb", PatchDispositionKind.PreservedAsConflict, null)], [], [group], [], false, "h2"),
            new Dictionary<string, IReadOnlyList<string>>(), [new("pa", RunPatchDispositionKind.PreservedAsConflict, "h2", null), new("pb", RunPatchDispositionKind.PreservedAsConflict, "h2", null)]);

        var unrepresented = Evaluate(Input(rig, final, preserved), built);
        var withConflict = Evaluate(Input(rig, final, preserved), Analysis(rig.Snapshot, (Cx.Id(1), Cx.Id(30)), (Cx.Id(2), Cx.Id(3))));

        Assert.Contains(unrepresented.Report.Gaps, g => g.Code == "conflict_not_represented" && g.Detail.Contains("g1"));
        Assert.False(unrepresented.IsComplete);
        Assert.True(withConflict.IsComplete);   // PreservedAsConflict terminale solo con tutte le alternative e fonti nel conflitto della revisione
    }

    [Fact]
    public async Task Residual_pending_or_not_processed_candidates_and_a_missing_or_incomplete_final_round_prevent_completion()
    {
        var (rig, _, final) = await RunAsync();
        var built = Analysis(rig.Snapshot, (Cx.Id(1), Cx.Id(30)));
        var some = final.Run.Candidates[0];

        var notProcessed = final.Run with
        {
            Dispositions = [.. final.Run.Dispositions.Select(d => d.CandidateId == some.CandidateId
                ? new CandidateDisposition { CandidateId = d.CandidateId, State = CandidateState.NotProcessed, ChildCandidateIds = [], ComparisonOutputHashes = [], Reason = "pair_does_not_fit" }
                : d)],
        };
        var residual = Evaluate(Input(rig, final) with { Candidates = notProcessed }, built);
        Assert.Contains(residual.Report.Gaps, g => g.Code == "candidate_not_terminal" && g.Detail.Contains(some.CandidateId));
        Assert.Contains(some.CandidateId, residual.UnresolvedCandidateIds);
        Assert.True(residual.Report.AdditionalWorkJustified);

        var pending = final.Run with
        {
            Dispositions = [.. final.Run.Dispositions.Select(d => d.CandidateId == some.CandidateId
                ? new CandidateDisposition { CandidateId = d.CandidateId, State = CandidateState.Pending, ChildCandidateIds = [], ComparisonOutputHashes = [], Reason = null }
                : d)],
        };
        Assert.Contains(Evaluate(Input(rig, final) with { Candidates = pending }, built).Report.Gaps, g => g.Code == "candidate_not_terminal");

        var missingFinal = Evaluate(Input(rig, null), built);
        Assert.Contains(missingFinal.Report.Gaps, g => g.Code == "final_pass_missing");

        var limited = new FinalPassResult(FinalPassOutcome.RoundLimit, DocumentWorkException.ReconciliationRoundLimit, "reconciliation_round_limit: nuovo lavoro", [new("c-frontier", "reconciliation_round_limit")], final.Run, true);
        var capped = Evaluate(Input(rig, limited), built);
        Assert.Contains(capped.Report.Gaps, g => g.Code == DocumentWorkException.ReconciliationRoundLimit);
        Assert.Contains(capped.Report.Gaps, g => g.Code == "frontier" && g.Detail.Contains("c-frontier"));
        Assert.Equal(["c-frontier"], capped.FrontierIds);
        Assert.False(capped.IsComplete);
    }

    [Fact]
    public async Task A_closed_candidate_without_the_hash_of_its_original_comparison_is_not_grounded_and_does_not_complete()
    {
        var (rig, _, final) = await RunAsync();
        var built = Analysis(rig.Snapshot, (Cx.Id(1), Cx.Id(30)));
        var open = final.Run.Dispositions.Single(d => d.State == CandidateState.Open);
        var ungrounded = final.Run with
        {
            Dispositions = [.. final.Run.Dispositions.Select(d => d.CandidateId == open.CandidateId ? new CandidateDisposition { CandidateId = d.CandidateId, State = CandidateState.Open, ChildCandidateIds = [], ComparisonOutputHashes = [], Reason = d.Reason } : d)],
        };

        var coverage = Evaluate(Input(rig, final) with { Candidates = ungrounded }, built);

        Assert.Contains(coverage.Report.Gaps, g => g.Code == "candidate_not_grounded" && g.Detail.Contains(open.CandidateId));
    }

    // ------------------------------------------------------------------ visite, nodi, pagine, mappe

    [Fact]
    public async Task A_missing_visit_a_missing_node_visit_or_cross_a_broken_page_chain_and_an_unmapped_evidence_are_all_server_gaps()
    {
        var (rig, _, final) = await RunAsync();
        var built = Analysis(rig.Snapshot, (Cx.Id(1), Cx.Id(30)));
        var input = Input(rig, final);
        var victim = rig.Directory.Entries.Single(e => e.EvidenceId == Cx.Id(7)).EntryId;

        var leaves = rig.Hierarchy.Outcomes.Select(o => o.Batch.Stage == "leaf" && o.Result.View == ReconcileView.B && o.Result.VisitedEntryIds.Contains(victim)
            ? o with { Result = o.Result with { VisitedEntryIds = [.. o.Result.VisitedEntryIds.Where(id => id != victim)] } } : o).ToList();
        var noVisit = Evaluate(input with { Hierarchy = rig.Hierarchy with { Outcomes = leaves } }, built);
        Assert.Equal([victim], noVisit.Report.MissingEntryIds);
        Assert.Equal(30, noVisit.Report.VisitedEntryIdsA.Count);
        Assert.Equal(29, noVisit.Report.VisitedEntryIdsB.Count);
        Assert.Contains(noVisit.Report.Gaps, g => g.Code == "entry_not_visited" && g.Detail.Contains("B"));

        var noReduce = rig.Hierarchy.Outcomes.Where(o => o.Batch.Stage != "reduce").ToList();
        var nodeGaps = Evaluate(input with { Hierarchy = rig.Hierarchy with { Outcomes = noReduce } }, built);
        Assert.NotEmpty(nodeGaps.MissingNodeIds);
        Assert.Contains(nodeGaps.Report.Gaps, g => g.Code == "node_not_visited");

        var noCross = rig.Hierarchy.Outcomes.Where(o => o.Batch.Stage != "cross").ToList();
        Assert.Contains(Evaluate(input with { Hierarchy = rig.Hierarchy with { Outcomes = noCross } }, built).Report.Gaps, g => g.Code is "cross_not_visited" or "node_not_visited");

        var gapPages = rig.Hierarchy.Pages.Skip(1).ToList();
        Assert.Contains(Evaluate(input with { Hierarchy = rig.Hierarchy with { Pages = gapPages } }, built).Report.Gaps, g => g.Code == "catalog_invalid");

        var unmapped = built with { Map = [.. built.Map.Where(m => m.ItemId != ItemOf(Cx.Id(5), "cc"))] };
        var noMap = Evaluate(input, unmapped);
        Assert.Equal([Cx.Id(5)], noMap.UnmappedEvidenceIds);
        Assert.Contains(noMap.Report.Gaps, g => g.Code == "evidence_not_mapped");

        var ghost = built with { Map = [.. built.Map, new EvidenceMapEntry { ItemId = Guid.Parse("99999999-0000-4000-8000-000000000001"), EvidenceIds = [Cx.Id(5)] }] };
        Assert.Contains(Evaluate(input, ghost).Report.Gaps, g => g.Code == "evidence_map_item_unknown");
    }

    [Fact]
    public async Task The_coverage_hash_is_deterministic_and_changes_with_any_set()
    {
        var (rig, _, final) = await RunAsync();
        var built = Analysis(rig.Snapshot, (Cx.Id(1), Cx.Id(30)));

        var first = Evaluate(Input(rig, final), built);
        var second = Evaluate(Input(rig, final), built);
        var different = Evaluate(Input(rig, final), Analysis(rig.Snapshot));

        Assert.Equal(first.Report.CoverageHash, second.Report.CoverageHash);
        Assert.NotEqual(first.Report.CoverageHash, different.Report.CoverageHash);
    }

    [Fact]
    public async Task A_known_relation_without_a_terminal_candidate_is_an_unresolved_obligation()
    {
        var relation = new SnapshotRelation(Guid.Parse("77000000-0000-4000-8000-000000000001"), Cx.Id(4), Cx.Id(25), EvidenceRelationType.Qualifies);
        var rig = await Cx.SetupAsync(new Cx.Model(), relations: [relation]);
        await rig.Engine.RunPhasesAsync(Ct);
        var final = await ReconciliationFinalPass.RunAsync(rig.Engine, rig.Snapshot, rig.Directory, [], Ct);
        var built = Analysis(rig.Snapshot, (Cx.Id(1), Cx.Id(30)));

        Assert.True(Evaluate(Input(rig, final), built).IsComplete);

        var dropped = final.Run with { Candidates = [.. final.Run.Candidates.Where(c => c.Rationale != ReconciliationDirectory.KnownRelationRationale)] };
        var coverage = Evaluate(Input(rig, final) with { Candidates = dropped }, built);
        Assert.Contains(coverage.Report.Gaps, g => g.Code == "known_relation_unresolved");
    }
}
