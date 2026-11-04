using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T070 (DET) — gold degli schemi gerarchici `reconcile-1` (CatalogEntry/Node/Page/Set): union strict, JSON canonico RFC 8785, ordinamenti, identità server
/// senza cicli, chiavi letterali vs inferite, membership e riferimenti. Riusa le fixture del nucleo T120/T121; nessun database, nessun DGX.
/// </summary>
public sealed class DocumentReconciliationContractTests
{
    private static string H(char c) => new(c, 64);

    private static readonly string InputHash = H('7');

    // ------------------------------------------------------------------ fixture

    private static CatalogKey Key(string value, CatalogKeyKind kind = CatalogKeyKind.Subject, ReconcileView view = ReconcileView.A, bool inferred = false) => new()
    {
        View = view, Kind = kind, Value = value, Origin = inferred ? CatalogKeyOrigin.Inferred : CatalogKeyOrigin.Literal, OriginOutputHash = inferred ? H('9') : null,
    };

    private static CatalogEntry Entry(Guid evidenceId, string contentHash, Guid sourceId, params CatalogKey[] keys) => new()
    {
        EntryId = ReconciliationFixtures.EntryId(evidenceId, contentHash), EvidenceId = evidenceId, EvidenceVersionId = Guid.Parse("50000000-0000-4000-8000-000000000001"),
        ContentHash = contentHash, SourceIds = [sourceId], Keys = keys, KnownRelationIds = [], UnclassifiedViews = [],
    };

    private static MemberPageEntry Member(ReconciliationSnapshot s, Guid id)
    {
        var e = s.Evidence.Single(x => x.EvidenceId == id);
        return new() { EvidenceId = id, EvidenceVersionId = e.EvidenceVersionId, ContentHash = e.ContentHash };
    }

    private static CatalogPage Page(string nodeId, CatalogPageKind kind, int ordinal, IReadOnlyList<PageEntry> entries, string? previous)
    {
        var page = new CatalogPage { PageId = "x", NodeId = nodeId, Kind = kind, Ordinal = ordinal, Entries = entries, PreviousPageHash = previous };
        return page with { PageId = ReconciliationIds.PageId(page) };
    }

    /// <summary>Catena di pagine contigue: ogni pagina cita l'hash della precedente.</summary>
    private static List<CatalogPage> Chain(string nodeId, CatalogPageKind kind, params IReadOnlyList<PageEntry>[] pages)
    {
        var list = new List<CatalogPage>();
        string? previous = null;
        for (var i = 0; i < pages.Length; i++)
        {
            var page = Page(nodeId, kind, i, pages[i], previous);
            list.Add(page);
            previous = ReconciliationIds.PageHash(page);
        }

        return list;
    }

    private static CatalogNode Node(ReconcileView view, int level, int ordinal, IReadOnlyList<MemberPageEntry> members, IReadOnlyList<string> children, IEnumerable<string> keyHashes, IEnumerable<string> relHashes, string descriptor = "Ordini cliente")
    {
        var memberHash = ReconciliationIds.MemberSetHash(members);
        return new()
        {
            NodeId = ReconciliationIds.NodeId(view, level, ordinal, memberHash, children, InputHash), View = view, Level = level, Ordinal = ordinal, ChildNodeIds = children,
            MemberSetHash = memberHash, MemberCount = members.Count, KeyPageHashes = [.. keyHashes], RelationPageHashes = [.. relHashes], Descriptor = descriptor, DescriptorOutputHash = H('8'),
        };
    }

    private static CatalogSet Set(ReconcileView view, IReadOnlyList<CatalogNode> nodes, IReadOnlyList<MemberPageEntry> members)
    {
        var ids = nodes.Select(n => n.NodeId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var hash = ReconciliationIds.MemberSetHash(members);
        return new() { SetId = ReconciliationIds.SetId(view, ids, hash), View = view, MemberNodeIds = ids, MemberSetHash = hash, MemberCount = members.Count };
    }

    /// <summary>Due foglie (E1+E2, E3) con pagine Members/Keys e il genitore che le unisce; tutto coerente con lo snapshot a tre evidenze.</summary>
    private sealed record Hierarchy(ReconciliationSnapshot Snapshot, CatalogNode Leaf0, CatalogNode Leaf1, CatalogNode Parent, List<CatalogPage> Pages, CatalogSet Set)
    {
        public ReconciliationCatalog Catalog => new([Leaf0, Leaf1, Parent], Pages, [Set]);
    }

    private static Hierarchy Build()
    {
        var s = ReconciliationFixtures.Snapshot(withThird: true);
        MemberPageEntry[] m0 = [Member(s, ReconciliationFixtures.E1), Member(s, ReconciliationFixtures.E2)];
        MemberPageEntry[] m1 = [Member(s, ReconciliationFixtures.E3)];
        var leaf0Id = ReconciliationIds.NodeId(ReconcileView.A, 0, 0, ReconciliationIds.MemberSetHash(m0), [], InputHash);
        var leaf1Id = ReconciliationIds.NodeId(ReconcileView.A, 0, 1, ReconciliationIds.MemberSetHash(m1), [], InputHash);
        var members0 = Chain(leaf0Id, CatalogPageKind.Members, m0);
        var keys0 = Chain(leaf0Id, CatalogPageKind.Keys,
            [new KeyPageEntry { Key = Key("cliente a"), MemberIds = [ReconciliationFixtures.E1, ReconciliationFixtures.E2], MemberPageRefs = [] }]);
        var members1 = Chain(leaf1Id, CatalogPageKind.Members, m1);
        var leaf0 = Node(ReconcileView.A, 0, 0, m0, [], keys0.Select(ReconciliationIds.PageHash), []);
        var leaf1 = Node(ReconcileView.A, 0, 1, m1, [], [], []);
        var all = new[] { m0[0], m0[1], m1[0] };
        var parent = Node(ReconcileView.A, 1, 0, all, [leaf0.NodeId, leaf1.NodeId], leaf0.KeyPageHashes, [], "Ordini e anagrafiche");
        var set = Set(ReconcileView.A, [leaf0, leaf1], all);
        return new(s, leaf0, leaf1, parent, [.. members0, .. keys0, .. members1], set);
    }

    // ------------------------------------------------------------------ gold canonico

    [Fact]
    public void Entry_canonical_json_is_rfc8785_with_literal_and_inferred_keys_kept_distinct()
    {
        var entry = Entry(ReconciliationFixtures.E1, ReconciliationFixtures.Hash1, ReconciliationFixtures.S1,
            Key("Cliente A"), Key("cliente a", inferred: true), Key("invio", CatalogKeyKind.Action, ReconcileView.B));

        var expected = "{\"contentHash\":\"" + ReconciliationFixtures.Hash1 + "\",\"entryId\":\"" + entry.EntryId + "\",\"evidenceId\":\"" + ReconciliationFixtures.E1 + "\","
            + "\"evidenceVersionId\":\"50000000-0000-4000-8000-000000000001\",\"keys\":["
            + "{\"kind\":\"subject\",\"origin\":\"Literal\",\"originOutputHash\":null,\"value\":\"Cliente A\",\"view\":\"A\"},"
            + "{\"kind\":\"subject\",\"origin\":\"Inferred\",\"originOutputHash\":\"" + H('9') + "\",\"value\":\"cliente a\",\"view\":\"A\"},"
            + "{\"kind\":\"action\",\"origin\":\"Literal\",\"originOutputHash\":null,\"value\":\"invio\",\"view\":\"B\"}],"
            + "\"knownRelationIds\":[],\"sourceIds\":[\"" + ReconciliationFixtures.S1 + "\"],\"unclassifiedViews\":[]}";

        Assert.Equal(expected, ReconciliationCodec.Canonical(entry));
        Assert.Equal(entry, ReconciliationCodec.Decode<CatalogEntry>(ReconciliationCodec.Encode(entry)).WithSameLists(entry));
        Assert.Empty(ReconciliationCatalogRules.CheckEntry(entry, ReconciliationFixtures.SourceHash));
    }

    [Fact]
    public void Page_union_roundtrips_per_kind_and_pageId_excludes_nothing_but_pageHash_excludes_pageId()
    {
        var s = ReconciliationFixtures.Snapshot();
        var members = Page(H('a'), CatalogPageKind.Members, 0, [Member(s, ReconciliationFixtures.E1)], null);
        var keys = Page(H('a'), CatalogPageKind.Keys, 0, [new KeyPageEntry { Key = Key("x"), MemberIds = [], MemberPageRefs = [ReconciliationIds.PageHash(members)] }], null);
        var relation = Page(H('a'), CatalogPageKind.Relations, 0, [new RelationPageEntry
        {
            RelationId = Guid.Parse("60000000-0000-4000-8000-000000000001"), FromEvidenceId = ReconciliationFixtures.E1, ToEvidenceId = ReconciliationFixtures.E2,
            Type = EvidenceRelationType.Confirms, Basis = EvidenceRelationBasis.Declared,
        }], null);

        foreach (var page in new[] { members, keys, relation })
        {
            var json = ReconciliationCodec.Encode(page);
            var back = ReconciliationCodec.Decode<CatalogPage>(json);
            Assert.Equal(page.Kind, back.Kind);
            Assert.Equal(page.Entries.Single().GetType(), back.Entries.Single().GetType());
            Assert.Equal(ReconciliationCodec.Canonical(page), ReconciliationCodec.Canonical(back));
            Assert.Empty(ReconciliationCatalogRules.CheckPage(page));
            // Hash della pagina senza pageId (evita il ciclo); pageId invece dipende da contenuto e catena.
            Assert.Equal(ReconciliationIds.PageHash(page), ReconciliationIds.PageHash(page with { PageId = H('0') }));
        }

        Assert.NotEqual(ReconciliationIds.PageId(members), ReconciliationIds.PageId(members with { Ordinal = 1, PreviousPageHash = H('1') }));
        Assert.NotEqual(ReconciliationIds.PageId(members), ReconciliationIds.PageId(members with { PreviousPageHash = H('1') }));
        Assert.NotEqual(ReconciliationIds.PageId(members), ReconciliationIds.PageId(keys));
    }

    [Fact]
    public void Node_id_depends_on_membership_and_input_not_on_its_own_pages_and_ids_are_type_prefixed()
    {
        var h = Build();
        var withOtherPages = h.Leaf0 with { KeyPageHashes = [H('f')], RelationPageHashes = [H('e')] };
        Assert.Equal(h.Leaf0.NodeId, ReconciliationIds.NodeId(withOtherPages.View, withOtherPages.Level, withOtherPages.Ordinal, withOtherPages.MemberSetHash, withOtherPages.ChildNodeIds, InputHash));
        Assert.NotEqual(h.Leaf0.NodeId, ReconciliationIds.NodeId(ReconcileView.A, 0, 0, h.Leaf0.MemberSetHash, [], H('6')));
        Assert.NotEqual(h.Leaf0.NodeId, ReconciliationIds.NodeId(ReconcileView.B, 0, 0, h.Leaf0.MemberSetHash, [], InputHash));
        Assert.NotEqual(h.Leaf0.NodeId, ReconciliationIds.NodeId(ReconcileView.A, 0, 1, h.Leaf0.MemberSetHash, [], InputHash));
        // Set e nodo con le stesse componenti non collidono: prefisso tipizzato.
        Assert.NotEqual(ReconciliationIds.NodeId(ReconcileView.A, 0, 0, H('1'), [], H('2')), ReconciliationIds.SetId(ReconcileView.A, [], H('1')));
        Assert.Equal(ReconciliationIds.MemberSetHash([Member(h.Snapshot, ReconciliationFixtures.E2), Member(h.Snapshot, ReconciliationFixtures.E1)]),
            ReconciliationIds.MemberSetHash([Member(h.Snapshot, ReconciliationFixtures.E1), Member(h.Snapshot, ReconciliationFixtures.E2)]));
        Assert.Empty(ReconciliationCatalogRules.CheckNode(h.Leaf0, InputHash));
        Assert.Empty(ReconciliationCatalogRules.CheckNode(h.Parent, InputHash));
        Assert.Empty(ReconciliationCatalogRules.CheckSet(h.Set));
    }

    // ------------------------------------------------------------------ decoder strict

    [Fact]
    public void Strict_decoder_rejects_extra_duplicate_null_wrong_enum_and_wrong_shapes()
    {
        var entry = ReconciliationCodec.Encode(Entry(ReconciliationFixtures.E1, ReconciliationFixtures.Hash1, ReconciliationFixtures.S1, Key("x")));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogEntry>(entry.Replace("{\"entryId\"", "{\"extra\":1,\"entryId\"")));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogEntry>(entry.Replace("\"keys\"", "\"sourceIds\":[],\"keys\"")));   // duplicato
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogEntry>(entry.Replace("\"kind\":\"subject\"", "\"kind\":\"Subject\"")));   // enum con maiuscola errata
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogEntry>(entry.Replace("\"kind\":\"subject\"", "\"kind\":\"owner\"")));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogEntry>(entry.Replace("\"view\":\"A\"", "\"view\":\"a\"")));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogEntry>(entry.Replace("\"origin\":\"Literal\"", "\"origin\":\"literal\"")));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogEntry>(entry.Replace("\"value\":\"x\"", "\"value\":null")));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogEntry>(entry.Replace("\"keys\":[", "\"keys\":null,\"x\":[")));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogEntry>(entry.Replace(ReconciliationFixtures.E1.ToString(), ReconciliationFixtures.E1.ToString().ToUpperInvariant())));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogEntry>(entry.Replace(",\"originOutputHash\":null", string.Empty)));   // proprietà obbligatoria assente

        var node = ReconciliationCodec.Encode(Build().Leaf0);
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogNode>(node.Replace("\"level\":0", "\"level\":\"0\"")));   // numero fra virgolette
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogNode>(node.Replace("\"descriptor\":\"Ordini cliente\"", "\"descriptor\":null")));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogNode>(node.Replace("\"view\":\"A\"", "\"view\":\"Z\"")));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogSet>(ReconciliationCodec.Encode(Build().Set).Replace("\"setId\"", "\"unknown\":true,\"setId\"")));

        var page = ReconciliationCodec.Encode(Build().Pages[0]);
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogPage>(page.Replace("\"kind\":\"Members\"", "\"kind\":\"Keys\"")));   // entries di un'altra union
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogPage>(page.Replace("\"kind\":\"Members\"", "\"kind\":\"members\"")));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogPage>(page.Replace("\"kind\":\"Members\"", "\"kind\":\"Members\",\"kind\":\"Members\"")));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogPage>(page.Replace(",\"previousPageHash\":null", string.Empty)));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogPage>(page.Replace("\"entries\":[{", "\"entries\":[{\"extra\":1,")));
        Assert.Throws<JsonException>(() => ReconciliationCodec.Decode<CatalogPage>(page.Replace("\"entries\":[", "\"entries\":{\"a\":1}, \"x\":[")));
    }

    // ------------------------------------------------------------------ regole di forma

    [Fact]
    public void Entry_rules_reject_unordered_empty_inferred_without_origin_and_cross_view()
    {
        var good = Entry(ReconciliationFixtures.E1, ReconciliationFixtures.Hash1, ReconciliationFixtures.S1, Key("a"), Key("b"));
        Assert.Empty(ReconciliationCatalogRules.CheckEntry(good, ReconciliationFixtures.SourceHash));

        Assert.NotEmpty(ReconciliationCatalogRules.CheckEntry(good with { EntryId = H('0') }, ReconciliationFixtures.SourceHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckEntry(good with { SourceIds = [] }, ReconciliationFixtures.SourceHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckEntry(good with { ContentHash = "XYZ" }, ReconciliationFixtures.SourceHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckEntry(good with { Keys = [Key("b"), Key("a")] }, ReconciliationFixtures.SourceHash));   // ordine
        Assert.NotEmpty(ReconciliationCatalogRules.CheckEntry(good with { Keys = [Key("a"), Key("a")] }, ReconciliationFixtures.SourceHash));   // duplicato
        Assert.NotEmpty(ReconciliationCatalogRules.CheckEntry(good with { KnownRelationIds = [Guid.Parse("f0000000-0000-4000-8000-000000000001"), Guid.Parse("10000000-0000-4000-8000-000000000001")] }, ReconciliationFixtures.SourceHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckEntry(good with { UnclassifiedViews = [ReconcileView.Cross] }, ReconciliationFixtures.SourceHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckEntry(good with { UnclassifiedViews = [ReconcileView.B, ReconcileView.A] }, ReconciliationFixtures.SourceHash));

        Assert.NotEmpty(ReconciliationCatalogRules.CheckKey(Key("")));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckKey(Key("  ")));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckKey(Key("x", view: ReconcileView.Cross)));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckKey(Key("x") with { OriginOutputHash = H('9') }));   // letterale con origine
        Assert.NotEmpty(ReconciliationCatalogRules.CheckKey(Key("x", inferred: true) with { OriginOutputHash = null }));   // inferita senza origine
        Assert.NotEmpty(ReconciliationCatalogRules.CheckKey(Key("x", inferred: true) with { OriginOutputHash = "nope" }));
        Assert.Empty(ReconciliationCatalogRules.CheckKey(Key("x", CatalogKeyKind.Alias, inferred: true)));
    }

    [Fact]
    public void Node_rules_bound_the_descriptor_and_require_consistent_shape()
    {
        var h = Build();
        Assert.Empty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { Descriptor = new string('a', 512) }, InputHash));
        Assert.Empty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { Descriptor = string.Concat(Enumerable.Repeat("\U0001F600", 512)) }, InputHash));   // 512 punti di codice (1024 UTF-16)
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { Descriptor = new string('a', 513) }, InputHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { Descriptor = string.Concat(Enumerable.Repeat("\U0001F600", 513)) }, InputHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { Descriptor = "" }, InputHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { Descriptor = "   " }, InputHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { NodeId = H('0') }, InputHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Leaf0, H('6')));   // inputHash diverso = identità diversa
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { MemberCount = 0 }, InputHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { Level = -1 }, InputHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { ChildNodeIds = [h.Leaf1.NodeId] }, InputHash));   // una foglia non ha figli
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Parent with { ChildNodeIds = [] }, InputHash));   // un genitore ha figli
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Parent with { ChildNodeIds = [h.Leaf0.NodeId, h.Leaf0.NodeId] }, InputHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { KeyPageHashes = ["bad"] }, InputHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { RelationPageHashes = [H('1'), H('1')] }, InputHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { DescriptorOutputHash = "x" }, InputHash));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckNode(h.Leaf0 with { MemberSetHash = "x" }, InputHash));
    }

    [Fact]
    public void Page_rules_enforce_kind_union_exactly_one_member_form_and_ordering()
    {
        var h = Build();
        var s = h.Snapshot;
        var members = h.Pages[0];
        Assert.Empty(ReconciliationCatalogRules.CheckPage(members));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(members with { Entries = [] }));   // pagina vuota
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(members with { Kind = CatalogPageKind.Keys }));   // entries di un'altra union
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(members with { PageId = H('0') }));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(members with { Ordinal = -1 }));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(members with { PreviousPageHash = H('1') }));   // ordinal 0 senza precedente
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(members with { Ordinal = 1, PreviousPageHash = null }));   // ordinal > 0 con precedente
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(members with { Entries = [Member(s, ReconciliationFixtures.E2), Member(s, ReconciliationFixtures.E1)] }));   // ordine
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(members with { Entries = [Member(s, ReconciliationFixtures.E1), Member(s, ReconciliationFixtures.E1)] }));   // duplicato
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(members with { Entries = [Member(s, ReconciliationFixtures.E1) with { ContentHash = "bad" }] }));

        KeyPageEntry K(IReadOnlyList<Guid> ids, IReadOnlyList<string> refs) => new() { Key = Key("k"), MemberIds = ids, MemberPageRefs = refs };
        var keys = h.Pages.Single(p => p.Kind == CatalogPageKind.Keys);
        Assert.Empty(ReconciliationCatalogRules.CheckPage(keys));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(keys with { Entries = [K([], [])] }));   // nessuna forma
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(keys with { Entries = [K([ReconciliationFixtures.E1], [H('1')])] }));   // entrambe
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(keys with { Entries = [K([ReconciliationFixtures.E2, ReconciliationFixtures.E1], [])] }));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(keys with { Entries = [K([], [H('2'), H('1')])] }));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(keys with { Entries = [K([], ["nope"])] }));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(keys with { Entries = [K([ReconciliationFixtures.E1], []) with { Key = Key("k", view: ReconcileView.Cross) }] }));

        var rel = new RelationPageEntry { RelationId = Guid.NewGuid(), FromEvidenceId = ReconciliationFixtures.E1, ToEvidenceId = ReconciliationFixtures.E1, Type = EvidenceRelationType.Confirms, Basis = EvidenceRelationBasis.Declared };
        var page = Page(h.Leaf0.NodeId, CatalogPageKind.Relations, 0, [rel], null);
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPage(page));   // endpoint coincidenti
    }

    [Fact]
    public void Page_chain_requires_contiguous_ordinals_and_concatenated_hashes()
    {
        var s = ReconciliationFixtures.Snapshot(withThird: true);
        var node = H('a');
        var chain = Chain(node, CatalogPageKind.Members, [Member(s, ReconciliationFixtures.E1)], [Member(s, ReconciliationFixtures.E2)], [Member(s, ReconciliationFixtures.E3)]);
        Assert.Empty(ReconciliationCatalogRules.CheckPageChain(chain));
        Assert.Empty(ReconciliationCatalogRules.CheckPageChain([chain[2], chain[0], chain[1]]));   // l'ordine di arrivo non conta, l'ordinal sì

        Assert.NotEmpty(ReconciliationCatalogRules.CheckPageChain([chain[0], chain[2]]));   // buco
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPageChain([chain[1], chain[2]]));   // non parte da 0
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPageChain([chain[0], chain[1] with { PreviousPageHash = H('1') }, chain[2]]));   // hash non concatenato
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPageChain([chain[0], chain[0], chain[1]]));   // ordinal duplicato
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPageChain([chain[0], chain[1] with { NodeId = H('b') }]));   // nodi diversi
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPageChain([chain[0], chain[1] with { Kind = CatalogPageKind.Keys }]));   // kind diversi
        Assert.NotEmpty(ReconciliationCatalogRules.CheckPageChain([]));   // una catena vuota non è una catena
    }

    [Fact]
    public void Set_rules_require_derived_id_sorted_distinct_nodes_and_positive_count()
    {
        var h = Build();
        Assert.Empty(ReconciliationCatalogRules.CheckSet(h.Set));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckSet(h.Set with { SetId = H('0') }));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckSet(h.Set with { MemberNodeIds = [.. h.Set.MemberNodeIds.Reverse()] }));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckSet(h.Set with { MemberNodeIds = [h.Leaf0.NodeId, h.Leaf0.NodeId] }));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckSet(h.Set with { MemberNodeIds = [] }));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckSet(h.Set with { MemberCount = 0 }));
        Assert.NotEmpty(ReconciliationCatalogRules.CheckSet(h.Set with { MemberSetHash = "x" }));
    }

    // ------------------------------------------------------------------ membership e riferimenti

    [Fact]
    public void Consistent_hierarchy_passes_and_carry_forward_keeps_the_member_set_hash()
    {
        var h = Build();
        Assert.Empty(ReconciliationReferences.CheckCatalog(h.Catalog, h.Snapshot, InputHash, complete: true));

        // Ultimo nodo singolo: stesso memberSetHash del figlio, nessun nuovo riassunto.
        var carry = Node(ReconcileView.A, 2, 0, [Member(h.Snapshot, ReconciliationFixtures.E1), Member(h.Snapshot, ReconciliationFixtures.E2), Member(h.Snapshot, ReconciliationFixtures.E3)], [h.Parent.NodeId], h.Parent.KeyPageHashes, [], h.Parent.Descriptor);
        Assert.Equal(h.Parent.MemberSetHash, carry.MemberSetHash);
        Assert.Empty(ReconciliationReferences.CheckCatalog(new([h.Leaf0, h.Leaf1, h.Parent, carry], h.Pages, [h.Set]), h.Snapshot, InputHash, complete: true));
    }

    [Fact]
    public void Membership_violations_are_rejected()
    {
        var h = Build();
        var s = h.Snapshot;
        string[] Check(ReconciliationCatalog c, bool complete = false) => [.. ReconciliationReferences.CheckCatalog(c, s, InputHash, complete)];

        // Cardinalità e hash di membership.
        Assert.NotEmpty(Check(new([h.Leaf0 with { MemberCount = 5 }, h.Leaf1, h.Parent], h.Pages, [])));
        Assert.NotEmpty(Check(new([h.Leaf0 with { MemberSetHash = H('0') }, h.Leaf1, h.Parent], h.Pages, [])));
        Assert.NotEmpty(Check(new([h.Leaf0, h.Leaf1, h.Parent with { MemberCount = 2 }], h.Pages, [])));
        // Foglia senza pagine Members.
        Assert.NotEmpty(Check(new([h.Leaf0, h.Leaf1, h.Parent], [.. h.Pages.Where(p => p.NodeId != h.Leaf1.NodeId)], [])));
        // Figlio inesistente o non del livello precedente.
        Assert.NotEmpty(Check(new([h.Leaf0, h.Parent], h.Pages, [])));
        Assert.NotEmpty(Check(new([h.Leaf0, h.Leaf1, h.Parent with { Level = 3 }], h.Pages, [])));
        // Pagine Keys concatenate male: il genitore deve citare esattamente le pagine dei figli.
        Assert.NotEmpty(Check(new([h.Leaf0, h.Leaf1, h.Parent with { KeyPageHashes = [] }], h.Pages, [])));
        Assert.NotEmpty(Check(new([h.Leaf0 with { KeyPageHashes = [H('f')] }, h.Leaf1, h.Parent], h.Pages, [])));
        // Pagine con buco.
        var holes = h.Pages.Where(p => !(p.Kind == CatalogPageKind.Members && p.NodeId == h.Leaf0.NodeId)).ToList();
        holes.AddRange(Chain(h.Leaf0.NodeId, CatalogPageKind.Members, [Member(s, ReconciliationFixtures.E1)], [Member(s, ReconciliationFixtures.E2)]).Select((p, i) => i == 1 ? p with { Ordinal = 2 } : p));
        Assert.NotEmpty(Check(new([h.Leaf0, h.Leaf1, h.Parent], holes, [])));
        // Pagina di un nodo sconosciuto.
        Assert.NotEmpty(Check(new([h.Leaf0, h.Leaf1, h.Parent], [.. h.Pages, Page(H('c'), CatalogPageKind.Members, 0, [Member(s, ReconciliationFixtures.E1)], null)], [])));

        // Membri fuori snapshot o con versione/hash diversi.
        var other = Member(s, ReconciliationFixtures.E3) with { EvidenceId = Guid.Parse("99000000-0000-4000-8000-000000000009") };
        var foreignLeaf = ForeignLeaf(h, [other]);
        Assert.NotEmpty(Check(new([h.Leaf0, h.Leaf1, foreignLeaf.Node], [.. h.Pages, .. foreignLeaf.Pages], [])));
        var staleLeaf = ForeignLeaf(h, [Member(s, ReconciliationFixtures.E3) with { ContentHash = H('0') }]);
        Assert.NotEmpty(Check(new([h.Leaf0, h.Leaf1, staleLeaf.Node], [.. h.Pages, .. staleLeaf.Pages], [])));

        // Stessa evidenza in due foglie della stessa vista/livello: l'unione non è disgiunta.
        var dup = ForeignLeaf(h, [Member(s, ReconciliationFixtures.E1)]);
        Assert.NotEmpty(Check(new([h.Leaf0, h.Leaf1, dup.Node], [.. h.Pages, .. dup.Pages], [])));

        // Copertura: con complete=true una foglia mancante lascia fuori un'evidenza.
        Assert.Empty(Check(new([h.Leaf0], h.Pages, []), complete: false));
        Assert.NotEmpty(Check(new([h.Leaf0], h.Pages, []), complete: true));
    }

    private static (CatalogNode Node, List<CatalogPage> Pages) ForeignLeaf(Hierarchy h, IReadOnlyList<MemberPageEntry> members)
    {
        var node = Node(ReconcileView.A, 0, 2, members, [], [], []);
        return (node, Chain(node.NodeId, CatalogPageKind.Members, members));
    }

    [Fact]
    public void Key_and_relation_pages_reference_known_members_pages_and_relations()
    {
        var h = Build();
        var s = h.Snapshot;
        var relationId = Guid.Parse("60000000-0000-4000-8000-000000000001");
        var withRelation = new ReconciliationSnapshot(s.ProjectId, s.SourceHash, s.Evidence, [new(relationId, ReconciliationFixtures.E1, ReconciliationFixtures.E2, EvidenceRelationType.Confirms, EvidenceRelationBasis.Declared)]);
        RelationPageEntry R(Guid id = default, Guid from = default) => new()
        {
            RelationId = id == default ? relationId : id, FromEvidenceId = from == default ? ReconciliationFixtures.E1 : from, ToEvidenceId = ReconciliationFixtures.E2,
            Type = EvidenceRelationType.Confirms, Basis = EvidenceRelationBasis.Declared,
        };

        var relationPages = Chain(h.Leaf0.NodeId, CatalogPageKind.Relations, [R()]);
        var leaf0 = h.Leaf0 with { RelationPageHashes = [ReconciliationIds.PageHash(relationPages[0])] };
        var parent = h.Parent with { RelationPageHashes = leaf0.RelationPageHashes };
        string[] Check(CatalogNode l0, CatalogNode p, CatalogPageKind replaced, IEnumerable<CatalogPage> extra) =>
            [.. ReconciliationReferences.CheckCatalog(new([l0, h.Leaf1, p], [.. h.Pages.Where(x => !(x.NodeId == h.Leaf0.NodeId && x.Kind == replaced)), .. extra], []), withRelation, InputHash)];

        // I nodeId non dipendono dalle pagine: i riferimenti aggiuntivi non cambiano l'identità.
        Assert.Equal(h.Leaf0.NodeId, leaf0.NodeId);
        Assert.Empty(Check(leaf0, parent, CatalogPageKind.Relations, relationPages));

        var unknown = Chain(h.Leaf0.NodeId, CatalogPageKind.Relations, [R(Guid.Parse("60000000-0000-4000-8000-0000000000ff"))]);
        Assert.NotEmpty(Check(h.Leaf0 with { RelationPageHashes = [ReconciliationIds.PageHash(unknown[0])] }, h.Parent with { RelationPageHashes = [ReconciliationIds.PageHash(unknown[0])] }, CatalogPageKind.Relations, unknown));   // relazione non nello snapshot
        var wrongEndpoint = Chain(h.Leaf0.NodeId, CatalogPageKind.Relations, [R(from: ReconciliationFixtures.E3)]);
        Assert.NotEmpty(Check(h.Leaf0 with { RelationPageHashes = [ReconciliationIds.PageHash(wrongEndpoint[0])] }, h.Parent with { RelationPageHashes = [ReconciliationIds.PageHash(wrongEndpoint[0])] }, CatalogPageKind.Relations, wrongEndpoint));   // endpoint diversi da quelli noti

        // Keys: memberIds fuori dalla foglia o memberPageRefs inesistenti.
        var badMember = Chain(h.Leaf0.NodeId, CatalogPageKind.Keys, [new KeyPageEntry { Key = Key("k"), MemberIds = [ReconciliationFixtures.E3], MemberPageRefs = [] }]);
        Assert.NotEmpty(Check(h.Leaf0 with { KeyPageHashes = [ReconciliationIds.PageHash(badMember[0])] }, h.Parent with { KeyPageHashes = [ReconciliationIds.PageHash(badMember[0])] }, CatalogPageKind.Keys, badMember));
        var badRef = Chain(h.Leaf0.NodeId, CatalogPageKind.Keys, [new KeyPageEntry { Key = Key("k"), MemberIds = [], MemberPageRefs = [H('d')] }]);
        Assert.NotEmpty(Check(h.Leaf0 with { KeyPageHashes = [ReconciliationIds.PageHash(badRef[0])] }, h.Parent with { KeyPageHashes = [ReconciliationIds.PageHash(badRef[0])] }, CatalogPageKind.Keys, badRef));
    }

    [Fact]
    public void Sets_are_the_exact_union_of_their_nodes_and_never_drop_overlapping_members()
    {
        var h = Build();
        var s = h.Snapshot;
        string[] Check(CatalogSet set) => [.. ReconciliationReferences.CheckCatalog(new([h.Leaf0, h.Leaf1, h.Parent], h.Pages, [set]), s, InputHash)];
        Assert.Empty(Check(h.Set));

        // Cardinalità o hash non coincidenti con l'unione dei nodi.
        var two = new[] { Member(s, ReconciliationFixtures.E1), Member(s, ReconciliationFixtures.E2) };
        Assert.NotEmpty(Check(Set(ReconcileView.A, [h.Leaf0, h.Leaf1], two)));
        Assert.NotEmpty(Check(h.Set with { MemberCount = 2 }));
        // Nodo inesistente o di un'altra vista.
        var missing = h.Set with { MemberNodeIds = [h.Leaf0.NodeId, H('0')] };
        Assert.NotEmpty(Check(missing with { SetId = ReconciliationIds.SetId(missing.View, missing.MemberNodeIds, missing.MemberSetHash) }));
        var otherView = h.Set with { View = ReconcileView.B };
        Assert.NotEmpty(Check(otherView with { SetId = ReconciliationIds.SetId(ReconcileView.B, otherView.MemberNodeIds, otherView.MemberSetHash) }));
        // Un nodo e il suo genitore nello stesso insieme sovrappongono i membri: errore, non deduplica silenziosa.
        var overlapping = Set(ReconcileView.A, [h.Leaf0, h.Parent], [Member(s, ReconciliationFixtures.E1), Member(s, ReconciliationFixtures.E2), Member(s, ReconciliationFixtures.E3)]);
        Assert.NotEmpty(Check(overlapping));
    }

    [Fact]
    public void Candidates_to_nodes_and_sets_resolve_in_the_catalog_and_non_existent_endpoints_are_rejected()
    {
        var h = Build();
        var s = h.Snapshot;
        ReconcileResult Result(ReconcileCandidate candidate) => new()
        {
            BatchId = "b-1", InputHash = H('d'), View = ReconcileView.A, Level = 1, VisitedEntryIds = [], VisitedNodeIds = [h.Leaf0.NodeId], Candidates = [candidate], Dispositions = [],
            ItemProposals = [], Patches = [], Descriptor = null, NoFindingsReason = null,
        };

        ReconcileCandidate Candidate(CandidateEndpoint left, CandidateEndpoint right)
        {
            var c = ReconciliationFixtures.Candidate();
            return c with { Left = left, Right = right, CandidateId = ReconciliationIds.CandidateId(c.Kind, left, right, c.ProposedType, s.SourceHash) };
        }

        CandidateEndpoint N(CatalogNode n) => new() { Kind = CandidateEndpointKind.Node, Id = n.NodeId, Hash = n.MemberSetHash };
        var set = new CandidateEndpoint { Kind = CandidateEndpointKind.Set, Id = h.Set.SetId, Hash = h.Set.MemberSetHash };

        var ok = Result(Candidate(N(h.Leaf0), N(h.Leaf1)));
        Assert.Empty(ReconciliationReferences.CheckResult(ok, s, h.Catalog));
        Assert.Empty(ReconciliationReferences.CheckResult(Result(Candidate(N(h.Leaf0), set)), s, h.Catalog));
        Assert.Empty(ReconciliationReferences.CheckResult(Result(Candidate(ReconciliationFixtures.Endpoint(ReconciliationFixtures.E1, ReconciliationFixtures.Hash1), N(h.Leaf1))), s, h.Catalog));

        // Endpoint inesistente, hash discordante, nodo scambiato per insieme, nodo visitato sconosciuto.
        Assert.NotEmpty(ReconciliationReferences.CheckResult(Result(Candidate(N(h.Leaf0), N(h.Leaf1) with { Id = H('0') })), s, h.Catalog));
        Assert.NotEmpty(ReconciliationReferences.CheckResult(Result(Candidate(N(h.Leaf0), N(h.Leaf1) with { Hash = H('0') })), s, h.Catalog));
        Assert.NotEmpty(ReconciliationReferences.CheckResult(Result(Candidate(N(h.Leaf0), N(h.Leaf1) with { Kind = CandidateEndpointKind.Set })), s, h.Catalog));
        Assert.NotEmpty(ReconciliationReferences.CheckResult(Result(Candidate(N(h.Leaf0), set with { Hash = H('0') })), s, h.Catalog));
        Assert.NotEmpty(ReconciliationReferences.CheckResult(ok with { VisitedNodeIds = [H('0')] }, s, h.Catalog));
        Assert.NotEmpty(ReconciliationReferences.CheckResult(Result(Candidate(ReconciliationFixtures.Endpoint(Guid.NewGuid(), ReconciliationFixtures.Hash1), N(h.Leaf1))), s, h.Catalog));   // evidenza fuori snapshot
        // Il percorso I4 invariato: senza catalogo gli endpoint di nodo restano non risolvibili.
        Assert.NotEmpty(ReconciliationReferences.CheckResult(ok, s));
    }

    // ------------------------------------------------------------------ grandezze

    [Fact]
    public void Huge_directory_pages_are_validated_without_a_model_and_without_loading_a_single_prompt()
    {
        const int count = 20_000;
        var evidence = Enumerable.Range(0, count).Select(i => new SnapshotEvidence(
            Guid.Parse($"00000000-0000-4000-8000-{i:x12}"), Guid.Parse($"50000000-0000-4000-8000-{i:x12}"), new string("0123456789abcdef"[i % 16], 64), [])).ToList();
        var snapshot = new ReconciliationSnapshot(ReconciliationFixtures.Project, ReconciliationFixtures.SourceHash, evidence, []);
        var members = evidence.Select(e => new MemberPageEntry { EvidenceId = e.EvidenceId, EvidenceVersionId = e.EvidenceVersionId, ContentHash = e.ContentHash }).ToList();
        var nodeId = ReconciliationIds.NodeId(ReconcileView.A, 0, 0, ReconciliationIds.MemberSetHash(members), [], InputHash);

        // 200 pagine da 100 membri: catena concatenata, nessuna pagina contiene l'intera directory.
        var chain = Chain(nodeId, CatalogPageKind.Members, [.. members.Chunk(100).Select(c => (IReadOnlyList<PageEntry>)[.. c])]);
        Assert.Equal(200, chain.Count);
        Assert.Empty(ReconciliationCatalogRules.CheckPageChain(chain));
        Assert.All(chain, p => Assert.Empty(ReconciliationCatalogRules.CheckPage(p)));
        var node = Node(ReconcileView.A, 0, 0, members, [], [], [], new string('d', 512));
        Assert.Equal(nodeId, node.NodeId);
        Assert.Empty(ReconciliationReferences.CheckCatalog(new([node], chain, []), snapshot, InputHash, complete: true));

        // Rompere una sola pagina intermedia basta a farla rifiutare.
        var broken = chain.Where(p => p.Ordinal != 123).ToList();
        Assert.NotEmpty(ReconciliationReferences.CheckCatalog(new([node], broken, []), snapshot, InputHash, complete: true));

        // Directory di 20.000 voci (chiavi): ordinate e senza duplicati.
        var entries = evidence.Select(e => new CatalogEntry
        {
            EntryId = ReconciliationIds.EntryId(snapshot.SourceHash, e.EvidenceId, e.ContentHash), EvidenceId = e.EvidenceId, EvidenceVersionId = e.EvidenceVersionId, ContentHash = e.ContentHash,
            SourceIds = [Guid.Parse("11000000-0000-4000-8000-000000000001")], Keys = [Key("chiave comune"), Key($"k{e.EvidenceId}")], KnownRelationIds = [], UnclassifiedViews = [],
        }).ToList();
        Assert.All(entries, e => Assert.Empty(ReconciliationCatalogRules.CheckEntry(e, snapshot.SourceHash)));
    }
}

internal static class CatalogEntryTestExtensions
{
    /// <summary>I record con liste non confrontano gli elementi: confronta la forma canonica del decodificato con quella dell'originale.</summary>
    public static CatalogEntry WithSameLists(this CatalogEntry decoded, CatalogEntry original) =>
        ReconciliationCodec.Canonical(decoded) == ReconciliationCodec.Canonical(original) ? original : decoded;
}
