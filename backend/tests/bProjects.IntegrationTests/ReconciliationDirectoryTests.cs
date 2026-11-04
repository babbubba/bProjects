using System.Security.Cryptography;
using System.Text;
using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T072 (DET) — directory esaustiva `reconcile-1` (AI §4.2 punti 1-3): CatalogEntry per ogni evidenza, normalizzazione delle sole chiavi, primaria minima,
/// foglie e pagine concatenate con Pack, merge esatto delle chiavi senza all-pairs né top-k, insiemi di routing e obbligazioni delle relazioni note.
/// Nessun database né LLM: misuratori finti (token per membro).
/// </summary>
public sealed class ReconciliationDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static Guid Id(int n) => Guid.Parse($"{n:x8}-0000-4000-8000-000000000000");

    private static string ContentHash(int n) => Sha($"content-{n}");

    private static SnapshotEvidence Ev(int n) =>
        new(Id(n), Guid.Parse($"{n:x8}-0000-4000-8000-0000000000aa"), ContentHash(n), [ReconciliationFixtures.Source(Guid.Parse($"{n:x8}-0000-4000-8000-0000000000bb"))]);

    private static ReconciliationSnapshot Snapshot(int count, IReadOnlyList<SnapshotRelation>? relations = null) =>
        new(ReconciliationFixtures.Project, ReconciliationFixtures.SourceHash, [.. Enumerable.Range(1, count).Select(Ev)], relations ?? []);

    private static DirectoryFacts Facts(string? subject = null, string? action = null, string? obj = null, string? channel = null, string[]? qualifiers = null) =>
        new(new EvidenceStructure(subject, action, obj, null, null, null, channel, null), qualifiers ?? []);

    private static ReconciliationContext Context(int leafMembers) => new("owner-1", "manifest-1", "project-analysis-documents-v2", new PlannerLimits(10 + 5 * leafMembers, 1_000_000));

    private static ReconciliationMeasurers Measurers(int pageEntries = 1000) => new(
        (members, _) => new ValueTask<UnitMeasure>(new UnitMeasure(10 + 5 * members.Count, TokenCountQuality.Measured, Sha("leaf|" + string.Join(',', members.Select(m => m.EntryId))), members.Count)),
        (members, _) => new ValueTask<UnitMeasure>(new UnitMeasure(10 + 40 * members.Count * 1000 / (pageEntries * 1000), TokenCountQuality.Measured, Sha("page|" + members.Count), members.Count)));

    private static Task<ReconciliationDirectoryResult> BuildAsync(
        ReconciliationSnapshot snapshot, IReadOnlyDictionary<Guid, DirectoryFacts> facts, int leafMembers = 6, int pageEntries = 1000) =>
        ReconciliationDirectory.BuildAsync(snapshot, facts, Context(leafMembers), Measurers(pageEntries), Ct).AsTask();

    // ------------------------------------------------------------------ voci e chiavi

    [Fact]
    public async Task Every_evidence_gets_an_entry_with_normalized_keys_per_view_and_unclassified_when_a_view_has_none()
    {
        var facts = new Dictionary<Guid, DirectoryFacts>
        {
            [Id(1)] = Facts(subject: "  Cliente \tA ", action: "Invia", obj: "Ordini", qualifiers: ["Massimo 5"]),
            [Id(2)] = Facts(subject: "Café", action: "  "),     // e + accento combinante = NFC "café"; azione vuota = nessuna chiave B
            // Id(3): evidenza PM senza struttura nel dizionario: unclassified in entrambe le viste
        };

        var result = await BuildAsync(Snapshot(3), facts);

        Assert.Equal(3, result.Entries.Count);
        var e1 = result.Entries.Single(e => e.EvidenceId == Id(1));
        Assert.Equal(
            [(ReconcileView.A, CatalogKeyKind.Subject, "cliente a"), (ReconcileView.A, CatalogKeyKind.Object, "ordini"), (ReconcileView.B, CatalogKeyKind.Action, "invia"), (ReconcileView.B, CatalogKeyKind.Qualifier, "massimo 5")],
            e1.Keys.Select(k => (k.View, k.Kind, k.Value)));
        Assert.All(e1.Keys, k => Assert.Equal((CatalogKeyOrigin.Literal, (string?)null), (k.Origin, k.OriginOutputHash)));
        Assert.Empty(e1.UnclassifiedViews);

        var e2 = result.Entries.Single(e => e.EvidenceId == Id(2));
        Assert.Equal("café", Assert.Single(e2.Keys).Value);
        Assert.Equal([ReconcileView.B], e2.UnclassifiedViews);

        var e3 = result.Entries.Single(e => e.EvidenceId == Id(3));
        Assert.Empty(e3.Keys);
        Assert.Equal([ReconcileView.A, ReconcileView.B], e3.UnclassifiedViews);
        Assert.All(result.Entries, e => Assert.Empty(ReconciliationCatalogRules.CheckEntry(e, ReconciliationFixtures.SourceHash)));
    }

    [Fact]
    public async Task Leaf_order_is_primary_key_then_evidence_id_with_the_unclassified_bucket_last()
    {
        var facts = new Dictionary<Guid, DirectoryFacts>
        {
            [Id(1)] = Facts(subject: "zeta"),
            [Id(2)] = Facts(subject: "alfa"),
            [Id(3)] = Facts(subject: "alfa"),
            // Id(4) senza chiavi A
        };

        var result = await BuildAsync(Snapshot(4), facts, leafMembers: 6);
        var leafA = Assert.Single(result.Leaves, l => l.View == ReconcileView.A);

        Assert.Equal([Id(2), Id(3), Id(1), Id(4)], leafA.Entries.Select(e => e.EvidenceId));
    }

    // ------------------------------------------------------------------ foglie, pagine, completezza

    [Fact]
    public async Task Catalog_larger_than_one_batch_covers_every_evidence_once_per_view_with_contiguous_concatenated_pages()
    {
        var facts = Enumerable.Range(1, 40).ToDictionary(n => Id(n), n => Facts(subject: $"s{n % 9}", action: $"a{n % 5}"));
        var snapshot = Snapshot(40);

        var result = await BuildAsync(snapshot, facts, leafMembers: 6, pageEntries: 2);

        foreach (var view in new[] { ReconcileView.A, ReconcileView.B })
        {
            var leaves = result.Leaves.Where(l => l.View == view).OrderBy(l => l.Ordinal).ToList();
            Assert.True(leaves.Count > 1);   // oltre B: più foglie
            Assert.Equal(Enumerable.Range(0, leaves.Count), leaves.Select(l => l.Ordinal));
            Assert.Equal(snapshot.Evidence.Select(e => e.EvidenceId).Order(), leaves.SelectMany(l => l.Entries).Select(e => e.EvidenceId).Order());
        }

        Assert.Contains(result.Leaves, l => l.Pages.Count(p => p.Kind == CatalogPageKind.Keys) > 1);   // catalogo troppo grande = pagine concatenate
        var nodes = result.Leaves.Select(l => ReconciliationDirectory.LeafNode(l, result.InputHash, "d", Sha("out-" + l.BatchId))).ToList();
        var catalog = new ReconciliationCatalog(nodes, [.. result.Leaves.SelectMany(l => l.Pages)], result.Sets);
        Assert.Empty(ReconciliationReferences.CheckCatalog(catalog, snapshot, result.InputHash, complete: true));
        Assert.All(result.Candidates, c => Assert.Empty(ReconciliationCatalogRules.CheckSet(result.Sets.Single(s => s.SetId == c.Left.Id))));
    }

    [Fact]
    public async Task Pack_failure_for_an_entry_that_does_not_fit_alone_is_an_explicit_incomplete_with_the_id()
    {
        var facts = new Dictionary<Guid, DirectoryFacts> { [Id(1)] = Facts(subject: "a") };
        var tiny = new ReconciliationContext("o", "m", "d", new PlannerLimits(5, 1_000_000));

        var ex = await Assert.ThrowsAsync<DocumentWorkException>(async () =>
            await ReconciliationDirectory.BuildAsync(Snapshot(1), facts, tiny, Measurers(), Ct));

        Assert.Equal(DocumentWorkException.DocumentIncomplete, ex.Code);
        Assert.Contains(ReconciliationIds.EntryId(ReconciliationFixtures.SourceHash, Id(1), ContentHash(1)), ex.Message);
    }

    // ------------------------------------------------------------------ chiavi condivise e relazioni note

    [Fact]
    public async Task A_shared_key_across_far_leaves_yields_two_halves_and_one_candidate_never_pairs_or_top_k()
    {
        // 30 evidenze, 5 per foglia in vista A (6 foglie): la chiave "regola" compare nelle evidenze 1 e 30 (prima e ultima), "lotto" in 5 foglie.
        var facts = Enumerable.Range(1, 30).ToDictionary(n => Id(n), n => Facts(subject: $"s{n:D2}"));
        facts[Id(1)] = Facts(subject: "s01", obj: "regola");
        facts[Id(30)] = Facts(subject: "s30", obj: "regola");
        foreach (var n in new[] { 3, 9, 14, 20, 27 })
        {
            facts[Id(n)] = Facts(subject: $"s{n:D2}", obj: "lotto");
        }

        var result = await BuildAsync(Snapshot(30), facts, leafMembers: 5);

        var leavesA = result.Leaves.Where(l => l.View == ReconcileView.A).OrderBy(l => l.Ordinal).ToList();
        Assert.Equal(6, leavesA.Count);
        Assert.Equal(2, result.Candidates.Count);   // una per chiave condivisa fra foglie, non C(n,2)
        Assert.All(result.Candidates, c =>
        {
            Assert.Equal((CandidateKind.Relation, ReconcileProposedType.SameRule, "shared_literal_key", EvidenceRelationBasis.Inferred), (c.Kind, c.ProposedType, c.Rationale, c.Basis));
            Assert.Equal((CandidateEndpointKind.Set, CandidateEndpointKind.Set), (c.Left.Kind, c.Right.Kind));
            Assert.Empty(ReconciliationContractRules.CheckCandidate(c, ReconciliationFixtures.SourceHash));
        });

        // "lotto": k = numero di foglie coinvolte (3,9,14,20,27 -> foglie diverse) = 5: metà 3 + 2 senza membri persi.
        var lotto = result.Candidates.Single(c => SetOf(result, c.Left).MemberCount + SetOf(result, c.Right).MemberCount == 25);
        Assert.Equal((15, 10), (SetOf(result, lotto.Left).MemberCount, SetOf(result, lotto.Right).MemberCount));
        // "regola": evidenze 1 e 30 (documenti/distanza diversi) -> due insiemi da una foglia ciascuno.
        var regola = result.Candidates.Single(c => c.CandidateId != lotto.CandidateId);
        Assert.Equal((5, 5), (SetOf(result, regola.Left).MemberCount, SetOf(result, regola.Right).MemberCount));
        Assert.All(result.Candidates, c => Assert.Equal(c.OriginBatchIds.Order(StringComparer.Ordinal), c.OriginBatchIds));
        Assert.Equal(2, result.Candidates.Select(c => c.CandidateId).Distinct().Count());
    }

    [Fact]
    public async Task A_key_whose_members_share_one_leaf_produces_no_candidate()
    {
        var facts = new Dictionary<Guid, DirectoryFacts> { [Id(1)] = Facts(subject: "x"), [Id(2)] = Facts(subject: "x"), [Id(3)] = Facts(subject: "y") };

        var result = await BuildAsync(Snapshot(3), facts, leafMembers: 6);

        Assert.Empty(result.Candidates);
        Assert.Empty(result.Sets);
    }

    [Fact]
    public async Task Known_relations_become_direct_obligations_on_their_two_evidence_ids_without_any_extra_pairs()
    {
        var relations = new[]
        {
            new SnapshotRelation(Guid.Parse("60000000-0000-4000-8000-000000000001"), Id(2), Id(9), EvidenceRelationType.Contradicts, EvidenceRelationBasis.Declared),
            new SnapshotRelation(Guid.Parse("60000000-0000-4000-8000-000000000002"), Id(1), Id(2), EvidenceRelationType.Confirms, EvidenceRelationBasis.Inferred),
        };
        var facts = Enumerable.Range(1, 12).ToDictionary(n => Id(n), n => Facts(subject: $"s{n:D2}"));

        var result = await BuildAsync(Snapshot(12, relations), facts, leafMembers: 3);

        Assert.Equal(2, result.KnownRelations.Count);
        var conflict = result.KnownRelations.Single(c => c.ProposedType == ReconcileProposedType.Contradicts);
        Assert.Equal(CandidateKind.Conflict, conflict.Kind);
        Assert.Equal((CandidateEndpointKind.Evidence, Id(2).ToString("D"), ContentHash(2)), (conflict.Left.Kind, conflict.Left.Id, conflict.Left.Hash));
        Assert.Equal((Id(9).ToString("D"), ContentHash(9)), (conflict.Right.Id, conflict.Right.Hash));
        Assert.Equal(EvidenceRelationBasis.Declared, conflict.Basis);
        Assert.NotEmpty(conflict.OriginBatchIds);
        Assert.All(result.KnownRelations, c => Assert.Empty(ReconciliationContractRules.CheckCandidate(c, ReconciliationFixtures.SourceHash)));
        Assert.Single(result.Entries.Single(e => e.EvidenceId == Id(1)).KnownRelationIds);
        Assert.Equal(2, result.Entries.Single(e => e.EvidenceId == Id(2)).KnownRelationIds.Count);
    }

    // ------------------------------------------------------------------ replay e determinismo

    [Fact]
    public async Task Replay_and_input_order_give_the_same_hash_batches_and_candidates_and_the_two_views_are_both_complete()
    {
        var facts = Enumerable.Range(1, 25).ToDictionary(n => Id(n), n => Facts(subject: $"s{n % 4}", action: $"a{n % 3}"));
        var snapshot = Snapshot(25);
        var shuffled = snapshot with { Evidence = [.. snapshot.Evidence.OrderByDescending(e => e.EvidenceId)] };

        var first = await BuildAsync(snapshot, facts, leafMembers: 4);
        var replay = await BuildAsync(snapshot, facts, leafMembers: 4);
        var reordered = await BuildAsync(shuffled, facts, leafMembers: 4);

        foreach (var other in new[] { replay, reordered })
        {
            Assert.Equal(first.InputHash, other.InputHash);
            Assert.Equal(first.Leaves.Select(l => l.BatchId), other.Leaves.Select(l => l.BatchId));
            Assert.Equal(first.Candidates.Select(c => c.CandidateId), other.Candidates.Select(c => c.CandidateId));
            Assert.Equal(first.Sets.Select(s => s.SetId), other.Sets.Select(s => s.SetId));
        }

        Assert.All(new[] { ReconcileView.A, ReconcileView.B }, view =>
            Assert.Equal(25, first.Leaves.Where(l => l.View == view).SelectMany(l => l.Entries).Select(e => e.EvidenceId).Distinct().Count()));
        Assert.True(first.Candidates.Count <= 7);   // al più una per chiave (4 soggetti + 3 azioni): lineare, mai quadratica
    }

    private static CatalogSet SetOf(ReconciliationDirectoryResult result, CandidateEndpoint endpoint) => result.Sets.Single(s => s.SetId == endpoint.Id);
}
