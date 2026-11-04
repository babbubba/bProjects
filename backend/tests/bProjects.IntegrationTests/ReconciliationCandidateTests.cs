using System.Security.Cryptography;
using System.Text;
using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T074 (DET) — riapertura dei candidati (AI §4.3) con modello FINTO: ordine relazioni note→A→B→Cross→candidateId, espansione integrale di Node/Set sull'unione
/// esatta dei membri, confronto Evidence con entrambi gli endpoint integrali, NotProcessed per ciò che non entra o non si risolve, nessuna disposition terminale
/// fondata sui soli descriptor, nessuna scansione dichiarata senza l'hash di tutti gli output.
/// </summary>
public sealed class ReconciliationCandidateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static Guid Id(int n) => Guid.Parse($"{n:x8}-0000-4000-8000-000000000000");

    private static SnapshotEvidence Ev(int n) =>
        new(Id(n), Guid.Parse($"{n:x8}-0000-4000-8000-0000000000aa"), Sha($"content-{n}"), [ReconciliationFixtures.Source(Guid.Parse($"{n:x8}-0000-4000-8000-0000000000bb"))]);

    private static readonly ReconciliationContext Context = new("owner", "manifest", "project-analysis-documents-v2", new PlannerLimits(20, 1_000_000));

    private static readonly ReconciliationMeasurers Measurers = new(
        (members, _) => new ValueTask<UnitMeasure>(new UnitMeasure(10 + 5 * members.Count, TokenCountQuality.Measured, Sha("leaf|" + string.Join(',', members.Select(m => m.EntryId))), members.Count)),
        (members, _) => new ValueTask<UnitMeasure>(new UnitMeasure(10, TokenCountQuality.Measured, Sha("page|" + members.Count), members.Count)));

    private static readonly PackMeasurer<CatalogNode> Descriptors = (members, _) =>
        new ValueTask<UnitMeasure>(new UnitMeasure(10 + 5 * members.Count, TokenCountQuality.Measured, Sha("nodes|" + string.Join(',', members.Select(m => m.NodeId))), members.Count));

    internal static readonly PackMeasurer<CatalogEntry> Leaf = Measurers.Leaf;

    internal static ReconcileCandidate Link(Guid left, Guid right, string parent, string batch, ReconcileProposedType type = ReconcileProposedType.Contradicts)
    {
        var l = ReconciliationFixtures.Endpoint(left, Sha($"content-{Convert.ToInt32(left.ToString("D")[..8], 16)}"));
        var r = ReconciliationFixtures.Endpoint(right, Sha($"content-{Convert.ToInt32(right.ToString("D")[..8], 16)}"));
        var kind = type is ReconcileProposedType.Contradicts or ReconcileProposedType.AlternativeTo ? CandidateKind.Conflict : CandidateKind.Relation;
        return new()
        {
            CandidateId = ReconciliationIds.CandidateId(kind, l, r, type, ReconciliationFixtures.SourceHash), Kind = kind, Left = l, Right = r, ProposedType = type,
            Rationale = "30 giorni nel documento contro 45 giorni dichiarati", Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = [batch], ParentCandidateId = parent,
        };
    }

    internal sealed class Model
    {
        public List<ReconcileBatchRequest> Calls { get; } = [];
        public Func<ReconcileBatchRequest, ReconcileResult, ReconcileResult>? Hierarchy { get; init; }
        public Func<ReconcileBatchRequest, ReconcileResult, ReconcileResult>? Expand { get; init; }
        public Func<ReconcileBatchRequest, ReconcileResult, ReconcileResult>? Compare { get; init; }
        public Action<ReconcileBatchRequest>? OnCall { get; init; }

        public ValueTask<string> Invoke(ReconcileBatchRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            OnCall?.Invoke(request);
            var result = new ReconcileResult
            {
                BatchId = request.Batch.BatchId, InputHash = request.Batch.InputHash, View = request.View, Level = request.Level,
                VisitedEntryIds = [.. request.Entries.Select(e => e.EntryId).Order(StringComparer.Ordinal)],
                VisitedNodeIds = [.. request.Nodes.Select(n => n.NodeId).Order(StringComparer.Ordinal)],
                Candidates = [], Dispositions = [], ItemProposals = [], Patches = [],
                Descriptor = request.Batch.Stage is "leaf" or "reduce" ? $"gruppo {request.Batch.BatchId[..8]}" : null, NoFindingsReason = "nessun collegamento emerso",
            };
            result = request.Batch.Stage switch
            {
                "expand" => (Expand ?? DefaultExpand)(request, result),
                "compare" => (Compare ?? DefaultCompare)(request, result),
                _ => Hierarchy?.Invoke(request, result) ?? result,
            };
            return new(ReconciliationCodec.Encode(result));
        }

        /// <summary>Il batch che contiene E1 propone il conflitto con E30, che sta nello stesso manifest ma in un altro batch (documenti e posizioni lontani).</summary>
        private static ReconcileResult DefaultExpand(ReconcileBatchRequest q, ReconcileResult r)
        {
            return q.Entries.Any(e => e.EvidenceId == Id(1))
                ? r with { Candidates = [Link(Id(1), Id(30), q.Focus!.Single().CandidateId, q.Batch.BatchId)], NoFindingsReason = null }
                : r;
        }

        private static ReconcileResult DefaultCompare(ReconcileBatchRequest q, ReconcileResult r)
        {
            var focus = q.Focus!.Single();
            var distant = q.Entries.Select(e => e.EvidenceId).SequenceEqual([Id(1), Id(30)]);
            return r with
            {
                Dispositions =
                [
                    new CandidateDisposition
                    {
                        CandidateId = focus.CandidateId, State = distant ? CandidateState.Open : CandidateState.Compared, ChildCandidateIds = [],
                        ComparisonOutputHashes = [q.Batch.InputHash], Reason = distant ? "30 giorni contro 45 giorni: alternative integrali da sottoporre al PM" : null,
                    },
                ],
            };
        }
    }

    internal sealed record Rig(ReconciliationSnapshot Snapshot, ReconciliationDirectoryResult Directory, HierarchyResult Hierarchy, ReconciliationCandidates Engine, Model Model);

    internal static async Task<Rig> SetupAsync(
        Model model, int evidence = 30, IReadOnlyList<SnapshotRelation>? relations = null, PackMeasurer<CatalogEntry>? measurer = null, ReconciliationLimits? limits = null,
        CallBudget? calls = null, TimeProvider? time = null)
    {
        var snapshot = new ReconciliationSnapshot(ReconciliationFixtures.Project, ReconciliationFixtures.SourceHash, [.. Enumerable.Range(1, evidence).Select(Ev)], relations ?? []);
        var facts = Enumerable.Range(1, evidence).ToDictionary(n => Id(n), n => new DirectoryFacts(new EvidenceStructure($"s{n:D2}", n is 2 or 3 or 4 ? "stessa azione" : $"a{n:D2}", null, null, null, null, null, null), []));
        facts[Id(1)] = new(new EvidenceStructure("s01", "a01", "regola", null, null, null, null, null), []);
        facts[Id(30)] = new(new EvidenceStructure("s30", "a30", "regola", null, null, null, null, null), []);
        var directory = await ReconciliationDirectory.BuildAsync(snapshot, facts, Context, Measurers, Ct);
        var hierarchy = await ReconciliationHierarchy.BuildAsync(snapshot, directory, Context, Descriptors, model.Invoke, Ct);
        return new(snapshot, directory, hierarchy, new ReconciliationCandidates(snapshot, directory, hierarchy, Context, measurer ?? Leaf, model.Invoke, limits, calls, time), model);
    }

    // ------------------------------------------------------------------ conflitto cross-documento / distante

    [Fact]
    public async Task A_distant_cross_document_conflict_is_reached_through_integral_expansion_and_ends_open_for_the_pm()
    {
        var rig = await SetupAsync(new Model());

        var run = await rig.Engine.RunPhasesAsync(Ct);

        var group = Assert.Single(run.Candidates, c => c.Rationale == ReconciliationDirectory.SharedKeyRationale && rig.Directory.Sets.Single(s => s.SetId == c.Left.Id).View == ReconcileView.A);
        var expanded = run.Dispositions.Single(d => d.CandidateId == group.CandidateId);
        Assert.Equal(CandidateState.Expanded, expanded.State);
        var child = run.Candidates.Single(c => c.ParentCandidateId == group.CandidateId);
        Assert.Equal((CandidateKind.Conflict, ReconcileProposedType.Contradicts), (child.Kind, child.ProposedType));
        Assert.Equal([child.CandidateId], expanded.ChildCandidateIds);
        var open = run.Dispositions.Single(d => d.CandidateId == child.CandidateId);
        Assert.Equal(CandidateState.Open, open.State);
        Assert.False(string.IsNullOrWhiteSpace(open.Reason));
        Assert.Single(open.ComparisonOutputHashes);

        // L'espansione ha riaperto TUTTI i membri integrali dei due insiemi (unione esatta, deduplicata) e i suoi hash sono tutti nella disposition.
        var expandCalls = rig.Model.Calls.Where(c => c.Batch.Stage == "expand" && c.Focus!.Single().CandidateId == group.CandidateId).ToList();
        Assert.Equal([Id(1), Id(2), Id(29), Id(30)], expandCalls.SelectMany(c => c.Entries).Select(e => e.EvidenceId).Order());
        Assert.Equal(expandCalls.Count, expanded.ComparisonOutputHashes.Count);
        Assert.Equal(run.Outcomes.Where(o => o.Batch.Stage == "expand" && o.Result.View == ReconcileView.A && expandCalls.Any(c => c.Batch.BatchId == o.Batch.BatchId)).Select(o => o.OutputHash).Order(StringComparer.Ordinal), expanded.ComparisonOutputHashes);

        // Il confronto Evidence ha ricevuto ENTRAMBI gli endpoint integrali, non i descriptor.
        var compare = Assert.Single(rig.Model.Calls, c => c.Batch.Stage == "compare");
        Assert.Equal([Id(1), Id(30)], compare.Entries.Select(e => e.EvidenceId));
        Assert.DoesNotContain(rig.Model.Calls, c => c.Batch.Stage is "expand" or "compare" && c.Nodes.Count > 0);
        Assert.Empty(run.Unresolved);
    }

    [Fact]
    public async Task Order_is_known_relations_then_A_then_B_then_Cross_then_candidate_id()
    {
        var relation = new SnapshotRelation(Guid.Parse("60000000-0000-4000-8000-000000000001"), Id(5), Id(6), EvidenceRelationType.Confirms, EvidenceRelationBasis.Declared);
        var model = new Model { Hierarchy = (q, r) => q.View == ReconcileView.Cross ? r with { Candidates = [CrossNodeCandidate(q)], NoFindingsReason = null } : r };
        var rig = await SetupAsync(model, relations: [relation]);

        var run = await rig.Engine.RunPhasesAsync(Ct);

        var known = rig.Directory.KnownRelations.Select(c => c.CandidateId).ToList();
        var a = rig.Directory.Candidates.Where(c => rig.Directory.Sets.Single(s => s.SetId == c.Left.Id).View == ReconcileView.A).Select(c => c.CandidateId).Order(StringComparer.Ordinal).ToList();
        var b = rig.Directory.Candidates.Where(c => rig.Directory.Sets.Single(s => s.SetId == c.Left.Id).View == ReconcileView.B).Select(c => c.CandidateId).Order(StringComparer.Ordinal).ToList();
        var cross = rig.Hierarchy.Outcomes.Single(o => o.Result.View == ReconcileView.Cross).Result.Candidates.Select(c => c.CandidateId).ToList();
        Assert.NotEmpty(known);
        Assert.NotEmpty(a);
        Assert.NotEmpty(b);
        Assert.NotEmpty(cross);
        var topLevel = run.ProcessingOrder.Where(id => known.Concat(a).Concat(b).Concat(cross).Contains(id)).ToList();
        Assert.Equal([.. known, .. a, .. b, .. cross], topLevel);
    }

    private static ReconcileCandidate CrossNodeCandidate(ReconcileBatchRequest q)
    {
        var l = new CandidateEndpoint { Kind = CandidateEndpointKind.Node, Id = q.Nodes[0].NodeId, Hash = q.Nodes[0].MemberSetHash };
        var r = new CandidateEndpoint { Kind = CandidateEndpointKind.Node, Id = q.Nodes[1].NodeId, Hash = q.Nodes[1].MemberSetHash };
        return new()
        {
            CandidateId = ReconciliationIds.CandidateId(CandidateKind.Relation, l, r, ReconcileProposedType.SameRule, ReconciliationFixtures.SourceHash), Kind = CandidateKind.Relation, Left = l,
            Right = r, ProposedType = ReconcileProposedType.SameRule, Rationale = "raccordo fra le radici", Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = [q.Batch.BatchId],
            ParentCandidateId = null,
        };
    }

    // ------------------------------------------------------------------ irriducibile, non risolto, scansione senza esito

    [Fact]
    public async Task A_pair_that_does_not_fit_is_not_processed_never_declared_open_or_compared()
    {
        PackMeasurer<CatalogEntry> strict = (members, ct) => members.Count >= 2
            ? new ValueTask<UnitMeasure>(new UnitMeasure(1000, TokenCountQuality.Measured, Sha("big"), members.Count))
            : Leaf(members, ct);
        var rig = await SetupAsync(new Model(), measurer: strict);

        var run = await rig.Engine.RunPhasesAsync(Ct);

        var child = run.Candidates.Single(c => c.ParentCandidateId is not null);
        var d = run.Dispositions.Single(x => x.CandidateId == child.CandidateId);
        Assert.Equal(CandidateState.NotProcessed, d.State);
        Assert.StartsWith(ReconciliationCandidates.PairDoesNotFit, d.Reason);
        Assert.Empty(d.ComparisonOutputHashes);
        Assert.DoesNotContain(rig.Model.Calls, c => c.Batch.Stage == "compare");
        Assert.Contains(child.CandidateId, run.Unresolved);
        Assert.Contains(child.ParentCandidateId!, run.Unresolved);   // Expanded con un figlio non terminale non è terminale
        Assert.DoesNotContain(run.Dispositions.Where(x => x.CandidateId == child.CandidateId), x => x.State is CandidateState.Open or CandidateState.Compared);
    }

    [Fact]
    public async Task A_full_scan_without_any_link_is_compared_with_the_hashes_of_all_expansion_outputs_and_says_nothing_more()
    {
        var rig = await SetupAsync(new Model { Expand = (_, r) => r });

        var run = await rig.Engine.RunPhasesAsync(Ct);

        Assert.DoesNotContain(rig.Model.Calls, c => c.Batch.Stage == "compare");
        Assert.All(rig.Directory.Candidates, c =>
        {
            var d = run.Dispositions.Single(x => x.CandidateId == c.CandidateId);
            Assert.Equal((CandidateState.Compared, ReconciliationCandidates.NoCandidateAfterFullScan), (d.State, d.Reason));
            var calls = rig.Model.Calls.Where(q => q.Batch.Stage == "expand" && q.Focus!.Single().CandidateId == c.CandidateId).ToList();
            Assert.Equal(calls.Count, d.ComparisonOutputHashes.Count);
            Assert.True(calls.Count >= 2);   // più batch: l'hash di TUTTI gli output, non del primo
            Assert.Empty(ReconciliationContractRules.CheckDisposition(d));
        });
    }

    [Fact]
    public async Task A_possible_link_without_identifiable_endpoints_stays_not_processed_candidate_unresolved()
    {
        Func<ReconcileBatchRequest, ReconcileResult, ReconcileResult> unresolved = (q, r) => r with
        {
            Dispositions = [new CandidateDisposition { CandidateId = q.Focus!.Single().CandidateId, State = CandidateState.NotProcessed, ChildCandidateIds = [], ComparisonOutputHashes = [], Reason = ReconciliationCandidates.CandidateUnresolved }],
        };
        var rig = await SetupAsync(new Model { Expand = unresolved });

        var run = await rig.Engine.RunPhasesAsync(Ct);

        var d = run.Dispositions.Single(x => x.CandidateId == rig.Directory.Candidates[0].CandidateId);
        Assert.Equal((CandidateState.NotProcessed, ReconciliationCandidates.CandidateUnresolved), (d.State, d.Reason));
        Assert.Contains(d.CandidateId, run.Unresolved);
    }

    // ------------------------------------------------------------------ doppioni, validazione, non visitato

    [Fact]
    public async Task The_same_evidence_pair_is_compared_at_most_once_and_duplicates_are_logical_cache_hits()
    {
        var relations = new[]
        {
            new SnapshotRelation(Guid.Parse("60000000-0000-4000-8000-000000000001"), Id(5), Id(6), EvidenceRelationType.Confirms, EvidenceRelationBasis.Declared),
            new SnapshotRelation(Guid.Parse("60000000-0000-4000-8000-000000000002"), Id(6), Id(5), EvidenceRelationType.Qualifies, EvidenceRelationBasis.Inferred),
        };
        var rig = await SetupAsync(new Model(), relations: relations);

        var run = await rig.Engine.RunPhasesAsync(Ct);

        var known = rig.Directory.KnownRelations;
        Assert.Equal(2, known.Count);
        Assert.Single(rig.Model.Calls, c => c.Batch.Stage == "compare" && c.Entries.Select(e => e.EvidenceId).SequenceEqual([Id(5), Id(6)]));
        var hashes = known.Select(c => run.Dispositions.Single(d => d.CandidateId == c.CandidateId).ComparisonOutputHashes.Single()).Distinct().ToList();
        Assert.Single(hashes);
        Assert.All(known, c => Assert.Equal(CandidateState.Compared, run.Dispositions.Single(d => d.CandidateId == c.CandidateId).State));
    }

    [Fact]
    public async Task An_output_that_omits_a_manifest_entry_is_invalid_and_the_candidate_stays_pending_never_open_or_compared()
    {
        var model = new Model { Expand = (q, r) => r with { VisitedEntryIds = [r.VisitedEntryIds[0]] } };
        var rig = await SetupAsync(model);
        var id = rig.Directory.Candidates[0].CandidateId;
        rig.Engine.Add(rig.Directory.Candidates[0], ReconcileView.A);

        var ex = await Assert.ThrowsAsync<DocumentWorkException>(async () => await rig.Engine.ProcessAsync(id, Ct));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ex.Code);
        Assert.Equal(CandidateState.Pending, rig.Engine.Disposition(id).State);
        Assert.Contains(id, rig.Engine.Pending);
    }

    [Fact]
    public async Task A_comparison_must_answer_compared_or_open_for_its_candidate_and_children_must_cite_the_parent()
    {
        var wrong = new Model { Compare = (q, r) => r with { Dispositions = [new CandidateDisposition { CandidateId = q.Focus!.Single().CandidateId, State = CandidateState.Pending, ChildCandidateIds = [], ComparisonOutputHashes = [], Reason = null }] } };
        var rig = await SetupAsync(wrong);
        var ex = await Assert.ThrowsAsync<DocumentWorkException>(async () => await rig.Engine.RunPhasesAsync(Ct));
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ex.Code);

        var orphan = new Model { Expand = (q, r) => r with { Candidates = [Link(Id(1), Id(30), new string('0', 64), q.Batch.BatchId)], NoFindingsReason = null } };
        var rig2 = await SetupAsync(orphan);
        var ex2 = await Assert.ThrowsAsync<DocumentWorkException>(async () => await rig2.Engine.RunPhasesAsync(Ct));
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ex2.Code);
    }

    [Fact]
    public void Expanded_is_terminal_only_when_it_has_children_and_all_of_them_are_terminal()
    {
        ReconcileCandidate C(char c) => ReconciliationFixtures.Candidate() with { CandidateId = new string(c, 64) };
        CandidateDisposition D(char c, CandidateState s, params char[] children) => new()
        {
            CandidateId = new string(c, 64), State = s, ChildCandidateIds = [.. children.Select(x => new string(x, 64))], ComparisonOutputHashes = [new string('9', 64)], Reason = "r",
        };

        var run = new CandidateRun([C('a'), C('b'), C('c'), C('d')], [D('a', CandidateState.Expanded, 'b'), D('b', CandidateState.Pending), D('c', CandidateState.Expanded, 'd'), D('d', CandidateState.Compared)], [], []);

        Assert.Equal([new string('a', 64), new string('b', 64)], run.Unresolved);
    }
}
