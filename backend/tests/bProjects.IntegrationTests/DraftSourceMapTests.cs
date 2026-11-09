using bProjects.App.AI;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>
/// T086 (DET) — mappa storica (itemId, EvidenceId, citationGroupId, part) delle fonti citate da un blocco di bozza (contracts/ai-workflow.md §4.5, raccordo W1): una voce per
/// ogni (item, evidenza, fonte) in ordine di marcatore; due gruppi che condividono lo stesso header restano due voci distinte (non un solo gruppo dedotto dalla sequenza
/// globale dei sourceId); una fonte senza evidenza nel registro (PmRevision o registro assente) è una voce con i campi di gruppo null, mai inventati.
/// </summary>
public sealed class DraftSourceMapTests
{
    private static readonly Guid Item1 = Guid.Parse("b0000000-0000-4000-8000-000000000001");
    private static readonly Guid Item2 = Guid.Parse("b0000000-0000-4000-8000-000000000002");
    private static readonly Guid Header = Guid.Parse("a0000000-0000-4000-8000-000000000001");
    private static readonly Guid Page2 = Guid.Parse("a0000000-0000-4000-8000-000000000002");
    private static readonly Guid Page3 = Guid.Parse("a0000000-0000-4000-8000-000000000003");
    private static readonly Guid PmSource = Guid.Parse("a0000000-0000-4000-8000-000000000004");
    private static readonly Guid Ev1 = Guid.Parse("e0000000-0000-4000-8000-000000000001");
    private static readonly Guid Ev2 = Guid.Parse("e0000000-0000-4000-8000-000000000002");
    private static readonly Guid Group1 = Guid.Parse("c0000000-0000-4000-8000-000000000001");
    private static readonly Guid Group2 = Guid.Parse("c0000000-0000-4000-8000-000000000002");

    private static DraftSourceLedger Ledger() => new(
        new Dictionary<Guid, IReadOnlyList<Guid>> { [Item1] = [Ev1], [Item2] = [Ev2] },
        new Dictionary<Guid, IReadOnlyList<LedgerSource>>
        {
            [Ev1] = [new(Header, Group1, 0), new(Page2, Group1, 1)],
            [Ev2] = [new(Header, Group2, 0), new(Page3, Group2, 1)],   // stesso header sourceId, altro gruppo
        });

    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> ItemSources = new Dictionary<Guid, IReadOnlyList<Guid>>
    {
        [Item1] = [Header, Page2], [Item2] = [Header, Page3, PmSource],
    };

    [Fact]
    public void Two_groups_sharing_the_same_header_source_stay_two_entries_with_their_own_group_and_part_in_marker_order()
    {
        DraftBlock[] blocks =
        [
            new([Item1], "x", [Page2, Header]),   // ordine richiesto: pagina prima dell'header
            new([Item2], "y", [Header, Page3]),
        ];

        var map = DraftSourceMap.Build(blocks, ItemSources, Ledger());

        Assert.Equal(
            [
                new DraftSourceMapEntry(Item1, Ev1, Group1, 1, Page2), new DraftSourceMapEntry(Item1, Ev1, Group1, 0, Header),
                new DraftSourceMapEntry(Item2, Ev2, Group2, 0, Header), new DraftSourceMapEntry(Item2, Ev2, Group2, 1, Page3),
            ],
            map);
    }

    [Fact]
    public void A_source_unknown_to_the_ledger_is_an_entry_with_null_group_fields_and_a_source_not_supporting_the_item_is_never_mapped()
    {
        DraftBlock[] blocks = [new([Item2], "y", [PmSource, Page2])];   // Page2 non sostiene Item2

        var map = DraftSourceMap.Build(blocks, ItemSources, Ledger());

        Assert.Equal([new DraftSourceMapEntry(Item2, null, null, null, PmSource)], map);
    }

    [Fact]
    public void Without_a_ledger_every_cited_source_is_still_mapped_to_its_item_with_null_evidence_and_group()
    {
        DraftBlock[] blocks = [new([Item1], "x", [Header, Page2])];

        var map = DraftSourceMap.Build(blocks, ItemSources, DraftSourceLedger.Empty);

        Assert.Equal([new DraftSourceMapEntry(Item1, null, null, null, Header), new DraftSourceMapEntry(Item1, null, null, null, Page2)], map);
    }

    [Fact]
    public void A_block_with_two_items_sharing_a_source_maps_the_source_once_per_item()
    {
        DraftBlock[] blocks = [new([Item1, Item2], "z", [Header])];

        var map = DraftSourceMap.Build(blocks, ItemSources, Ledger());

        Assert.Equal([new DraftSourceMapEntry(Item1, Ev1, Group1, 0, Header), new DraftSourceMapEntry(Item2, Ev2, Group2, 0, Header)], map);
    }
}
