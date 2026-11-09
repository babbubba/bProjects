using System.Security.Cryptography;
using System.Text;
using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T073 (DET) — foglie e riduzione di livelli (AI §4.2 punti 4-5) con modello FINTO deterministico: visite integrali per A/B, descriptor solo per instradare,
/// Pack di almeno due nodi, singleton inoltrato senza AI, membership esatta calcolata dal server, cardinalità strettamente decrescente fino alle radici e Cross,
/// massimo 32 livelli. Nessun database, nessun DGX.
/// </summary>
public sealed class ReconciliationHierarchyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static Guid Id(int n) => Guid.Parse($"{n:x8}-0000-4000-8000-000000000000");

    private static SnapshotEvidence Ev(int n) =>
        new(Id(n), Guid.Parse($"{n:x8}-0000-4000-8000-0000000000aa"), Sha($"content-{n}"), [ReconciliationFixtures.Source(Guid.Parse($"{n:x8}-0000-4000-8000-0000000000bb"))]);

    private static ReconciliationSnapshot Snapshot(int count) => new(ReconciliationFixtures.Project, ReconciliationFixtures.SourceHash, [.. Enumerable.Range(1, count).Select(Ev)], []);

    /// <summary>Foglie da 2 evidenze e riduzioni da 2 nodi (budget 20 token = cornice 10 + 5 per membro); le ultime evidenze senza chiavi (bucket unclassified).</summary>
    private static ReconciliationContext Context(int maxLevels = 32) => new("owner", "manifest", "project-analysis-documents-v2", new PlannerLimits(20, 1_000_000), MaxLevels: maxLevels);

    private static readonly ReconciliationMeasurers Measurers = new(
        (members, _) => new ValueTask<UnitMeasure>(new UnitMeasure(10 + 5 * members.Count, TokenCountQuality.Measured, Sha("leaf|" + string.Join(',', members.Select(m => m.EntryId))), members.Count)),
        (members, _) => new ValueTask<UnitMeasure>(new UnitMeasure(10, TokenCountQuality.Measured, Sha("page|" + members.Count), members.Count)));

    private static readonly PackMeasurer<CatalogNode> Descriptors = (members, _) =>
        new ValueTask<UnitMeasure>(new UnitMeasure(10 + 5 * members.Count, TokenCountQuality.Measured, Sha("nodes|" + string.Join(',', members.Select(m => m.NodeId))), members.Count));

    private sealed class FakeModel
    {
        public List<ReconcileBatchRequest> Calls { get; } = [];
        public Func<ReconcileBatchRequest, ReconcileResult, string>? Respond { get; init; }

        public ValueTask<string> Invoke(ReconcileBatchRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            var result = new ReconcileResult
            {
                BatchId = request.Batch.BatchId, InputHash = request.Batch.InputHash, View = request.View, Level = request.Level,
                VisitedEntryIds = [.. request.Entries.Select(e => e.EntryId).Order(StringComparer.Ordinal)],
                VisitedNodeIds = [.. request.Nodes.Select(n => n.NodeId).Order(StringComparer.Ordinal)],
                Candidates = [], Dispositions = [], ItemProposals = [], Patches = [],
                Descriptor = request.View == ReconcileView.Cross ? null : $"gruppo {request.Batch.BatchId[..8]}", NoFindingsReason = "nessun collegamento emerso dalla visita",
            };
            return new(Respond is null ? ReconciliationCodec.Encode(result) : Respond(request, result));
        }
    }

    private static async Task<(ReconciliationSnapshot Snapshot, ReconciliationDirectoryResult Directory, HierarchyResult Hierarchy)> RunAsync(
        int evidence, FakeModel model, int unclassified = 0, ReconciliationContext? context = null, PackMeasurer<CatalogNode>? descriptors = null)
    {
        var snapshot = Snapshot(evidence);
        var facts = Enumerable.Range(1, evidence - unclassified).ToDictionary(n => Id(n), n => new DirectoryFacts(new EvidenceStructure($"s{n:D3}", $"a{n:D3}", null, null, null, null, null, null), []));
        var ctx = context ?? Context();
        var directory = await ReconciliationDirectory.BuildAsync(snapshot, facts, ctx, Measurers, Ct);
        var hierarchy = await ReconciliationHierarchy.BuildAsync(snapshot, directory, ctx, descriptors ?? Descriptors, model.Invoke, Ct);
        return (snapshot, directory, hierarchy);
    }

    [Fact]
    public async Task Three_or_more_levels_reduce_to_one_root_per_view_with_strictly_decreasing_nodes_exact_membership_and_a_cross_pass()
    {
        var model = new FakeModel();

        var (snapshot, directory, h) = await RunAsync(16, model, unclassified: 3);

        foreach (var view in new[] { ReconcileView.A, ReconcileView.B })
        {
            var counts = h.Nodes.Where(n => n.View == view).GroupBy(n => n.Level).OrderBy(g => g.Key).Select(g => g.Count()).ToList();
            Assert.Equal([8, 4, 2, 1], counts);   // >= 3 livelli, cardinalità strettamente decrescente
            var root = h.Nodes.Single(n => n.NodeId == h.Roots[view]);
            Assert.Equal((3, 16), (root.Level, root.MemberCount));
            // Il bucket unclassified è coperto da foglie e livelli come tutto il resto.
            Assert.Equal(16, h.Outcomes.Where(o => o.Result.View == view && o.Result.Level == 0).SelectMany(o => o.Result.VisitedEntryIds).Distinct().Count());
        }

        var catalog = new ReconciliationCatalog(h.Nodes, h.Pages, directory.Sets);
        Assert.Empty(ReconciliationReferences.CheckCatalog(catalog, snapshot, directory.InputHash, complete: true));

        var cross = Assert.Single(model.Calls, c => c.View == ReconcileView.Cross);
        Assert.Equal([h.Roots[ReconcileView.A], h.Roots[ReconcileView.B]], cross.Nodes.Select(n => n.NodeId));
        Assert.Empty(cross.Entries);
        Assert.Equal(h.Outcomes.Count, model.Calls.Count);   // ogni chiamata è un batch derivato pubblicato
        Assert.Equal(2 * (8 + 4 + 2 + 1) + 1, model.Calls.Count);
        Assert.All(h.Outcomes, o => Assert.Equal(CanonicalJson.HashText(ReconciliationCodec.Canonical(o.Result)), o.OutputHash));
    }

    [Fact]
    public async Task Leaf_batches_receive_integral_entries_and_reductions_receive_descriptors_only_never_entries()
    {
        var model = new FakeModel();

        var (_, _, h) = await RunAsync(8, model);

        Assert.All(model.Calls.Where(c => c.Level == 0), c => Assert.Equal((2, 0), (c.Entries.Count, c.Nodes.Count)));
        var reductions = model.Calls.Where(c => c.Level > 0 && c.View != ReconcileView.Cross).ToList();
        Assert.NotEmpty(reductions);
        Assert.All(reductions, c => Assert.Empty(c.Entries));
        Assert.All(reductions, c => Assert.True(c.Nodes.Count >= 2));   // Pack di almeno due descriptor
        // La membership di un genitore viene dal server (unione esatta dei figli), mai dal testo del descriptor del modello.
        var liar = new FakeModel { Respond = (_, r) => ReconciliationCodec.Encode(r with { Descriptor = r.Level > 0 && r.View != ReconcileView.Cross ? "contiene tutte le evidenze e anche altre mille" : r.Descriptor }) };
        var (_, _, h2) = await RunAsync(8, liar);
        Assert.Equal(h.Nodes.Select(n => (n.NodeId, n.MemberSetHash, n.MemberCount)), h2.Nodes.Select(n => (n.NodeId, n.MemberSetHash, n.MemberCount)));
    }

    [Fact]
    public async Task A_last_single_node_is_carried_without_ai_and_the_carry_is_traced()
    {
        var model = new FakeModel();

        var (_, _, h) = await RunAsync(6, model);   // 3 foglie per vista: [2 nodi] + 1 inoltrato

        var level1 = h.Nodes.Where(n => n is { View: ReconcileView.A, Level: 1 }).OrderBy(n => n.Ordinal).ToList();
        Assert.Equal(2, level1.Count);
        var carried = level1[1];
        var leaf = h.Nodes.Single(n => n.NodeId == Assert.Single(carried.ChildNodeIds));
        Assert.Equal((leaf.MemberSetHash, leaf.DescriptorOutputHash, leaf.Descriptor), (carried.MemberSetHash, carried.DescriptorOutputHash, carried.Descriptor));
        Assert.Contains(carried.NodeId, h.Carries);
        Assert.DoesNotContain(h.Outcomes, o => o.Result.View == ReconcileView.A && o.Result.Level == 1 && o.Result.VisitedNodeIds.Contains(leaf.NodeId));
        Assert.Equal(2 * (3 + 1 + 1) + 1, model.Calls.Count);   // nessuna chiamata per il carry
        Assert.Single(h.Carries, c => h.Nodes.Single(n => n.NodeId == c).View == ReconcileView.A);
    }

    [Fact]
    public async Task A_single_leaf_is_its_own_root_and_an_empty_snapshot_needs_no_ai()
    {
        var model = new FakeModel();
        var (_, _, h) = await RunAsync(2, model);
        Assert.Equal(2, h.Nodes.Count);
        Assert.All(h.Roots, r => Assert.Equal(0, h.Nodes.Single(n => n.NodeId == r.Value).Level));
        Assert.Equal(3, model.Calls.Count);   // due foglie e il Cross sulle radici

        var empty = new FakeModel();
        var (_, _, none) = await RunAsync(0, empty);
        Assert.Empty(none.Nodes);
        Assert.Empty(empty.Calls);
    }

    [Fact]
    public async Task Two_descriptors_that_do_not_fit_together_are_a_reconciliation_irreducible_failure()
    {
        PackMeasurer<CatalogNode> huge = (members, _) => new ValueTask<UnitMeasure>(new UnitMeasure(members.Count >= 2 ? 1000 : 10, TokenCountQuality.Measured, Sha("n" + members.Count), members.Count));

        var ex = await Assert.ThrowsAsync<DocumentWorkException>(() => RunAsync(8, new FakeModel(), descriptors: huge));

        Assert.Equal(DocumentWorkException.ReconciliationIrreducible, ex.Code);
    }

    [Fact]
    public async Task Stall_or_more_than_the_frozen_levels_is_coverage_incomplete_and_the_guard_requires_a_strict_decrease()
    {
        var ex = await Assert.ThrowsAsync<DocumentWorkException>(() => RunAsync(16, new FakeModel(), context: Context(maxLevels: 3)));   // servono 4 livelli
        Assert.Equal(DocumentWorkException.CoverageIncomplete, ex.Code);
        Assert.Contains("livell", ex.Message);

        Assert.Equal(DocumentWorkException.CoverageIncomplete, Assert.Throws<DocumentWorkException>(() => ReconciliationHierarchy.EnsureStrictlyDecreasing(ReconcileView.A, 1, 4, 4)).Code);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, Assert.Throws<DocumentWorkException>(() => ReconciliationHierarchy.EnsureStrictlyDecreasing(ReconcileView.A, 1, 4, 5)).Code);
        ReconciliationHierarchy.EnsureStrictlyDecreasing(ReconcileView.A, 1, 4, 3);
        Assert.Equal(32, Context().MaxLevels);

        await RunAsync(16, new FakeModel(), context: Context(maxLevels: 4));   // esattamente 4 livelli: ammesso
    }

    [Fact]
    public async Task Invalid_model_output_is_a_permanent_work_output_invalid_never_repaired_or_advanced()
    {
        var cases = new (string Name, Func<ReconcileBatchRequest, ReconcileResult, string> Respond)[]
        {
            ("json non valido", (_, _) => "{\"nope\":1}"),
            ("voce non visitata", (q, r) => ReconciliationCodec.Encode(q.Level == 0 && q.View != ReconcileView.Cross ? r with { VisitedEntryIds = [r.VisitedEntryIds[0]] } : r)),
            ("nodo non visitato", (q, r) => ReconciliationCodec.Encode(q.Level > 0 && q.View != ReconcileView.Cross ? r with { VisitedNodeIds = [r.VisitedNodeIds[0]] } : r)),
            ("descriptor assente nel batch che costruisce nodi", (q, r) => ReconciliationCodec.Encode(q.View != ReconcileView.Cross ? r with { Descriptor = null } : r)),
            ("descriptor oltre 512", (q, r) => ReconciliationCodec.Encode(q.View != ReconcileView.Cross ? r with { Descriptor = new string('x', 513) } : r)),
            ("descriptor nel Cross", (q, r) => ReconciliationCodec.Encode(q.View == ReconcileView.Cross ? r with { Descriptor = "no" } : r)),
            ("inputHash diverso", (_, r) => ReconciliationCodec.Encode(r with { InputHash = new string('0', 64) })),
            ("candidato su nodo con hash errato", (q, r) => ReconciliationCodec.Encode(q.Nodes.Count >= 2
                ? r with { Candidates = [BadNodeCandidate(q)], NoFindingsReason = null } : r)),
        };

        foreach (var (name, respond) in cases)
        {
            var ex = await Assert.ThrowsAsync<DocumentWorkException>(() => RunAsync(8, new FakeModel { Respond = respond }));
            Assert.True(ex.Code == DocumentWorkException.WorkOutputInvalid, $"{name}: {ex.Code}");
        }

    }

    private static ReconcileCandidate BadNodeCandidate(ReconcileBatchRequest q)
    {
        var left = new CandidateEndpoint { Kind = CandidateEndpointKind.Node, Id = q.Nodes[0].NodeId, Hash = new string('0', 64) };
        var right = new CandidateEndpoint { Kind = CandidateEndpointKind.Node, Id = q.Nodes[1].NodeId, Hash = q.Nodes[1].MemberSetHash };
        return new()
        {
            CandidateId = ReconciliationIds.CandidateId(CandidateKind.Relation, left, right, ReconcileProposedType.SameRule, ReconciliationFixtures.SourceHash), Kind = CandidateKind.Relation,
            Left = left, Right = right, ProposedType = ReconcileProposedType.SameRule, Rationale = "x", Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = [q.Batch.BatchId], ParentCandidateId = null,
        };
    }

    [Fact]
    public async Task Replay_gives_the_same_nodes_batches_and_requests()
    {
        var first = new FakeModel();
        var second = new FakeModel();

        var (_, _, a) = await RunAsync(12, first, unclassified: 2);
        var (_, _, b) = await RunAsync(12, second, unclassified: 2);

        Assert.Equal(a.Nodes.Select(n => n.NodeId), b.Nodes.Select(n => n.NodeId));
        Assert.Equal(first.Calls.Select(c => c.Batch.BatchId), second.Calls.Select(c => c.Batch.BatchId));
        Assert.Equal(first.Calls.Select(c => c.Batch.InputHash), second.Calls.Select(c => c.Batch.InputHash));
        Assert.Equal(a.Outcomes.Select(o => o.OutputHash), b.Outcomes.Select(o => o.OutputHash));
        // I genitori dipendono dagli output Committed dei figli: l'identità del batch di riduzione li include.
        var reduction = first.Calls.First(c => c.Level == 1);
        Assert.Equal(reduction.Nodes.Select(n => n.DescriptorOutputHash), reduction.Batch.ParentOutputHashes);
    }

    [Fact]
    public void The_reconcile_prompt_is_embedded_and_states_the_routing_only_contract()
    {
        var text = ReconciliationHierarchy.Instructions;

        Assert.Equal("document-reconcile-v2", ReconciliationHierarchy.PromptVersion);
        Assert.Contains("descriptor", text);
        Assert.Contains("512", text);
        Assert.Contains("visitedEntryIds", text);
        Assert.Contains("visitedNodeIds", text);
        Assert.Contains("noFindingsReason", text);
    }
}
