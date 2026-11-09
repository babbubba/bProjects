using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>
/// T088 (DET) — roundtrip conferma → bozza delle citazioni (contracts/ai-workflow.md §4.5, raccordo W1): i marcatori atomici <c>[fonte:UUID]</c> del Markdown coincidono con i
/// sourceIds di ogni blocco e con la mappa storica (itemId, EvidenceId, citationGroupId, part); due item che condividono lo stesso header restano due gruppi distinti; citazioni
/// di frase e di tabella multipagina e testo non-BMP attraversano revisione, validatore e mappa senza alterazioni. Un edit PM che toglie un riferimento o una fonte rende quella
/// fonte inutilizzabile per il testo nuovo, mentre la revisione storica (e la sua mappa) restano valide e non vengono convertite; una revisione 1.0.0 non è decodificabile come 1.1.0.
/// Nessun database né modello: sono funzioni pure del contenuto, del validatore e del registro.
/// </summary>
public sealed class DocumentCitationRoundtripTests
{
    private static readonly Guid Msg = Guid.Parse("a8000000-0000-4000-8000-000000000001");
    private static readonly Guid Header = Guid.Parse("a8000000-0000-4000-8000-000000000002");
    private static readonly Guid Page2 = Guid.Parse("a8000000-0000-4000-8000-000000000003");
    private static readonly Guid Page3 = Guid.Parse("a8000000-0000-4000-8000-000000000004");
    private static readonly Guid Music = Guid.Parse("a8000000-0000-4000-8000-000000000005");
    private static readonly Guid PmSource = Guid.Parse("a8000000-0000-4000-8000-000000000006");
    private static readonly Guid Goal = Guid.Parse("b8000000-0000-4000-8000-000000000001");
    private static readonly Guid TableA = Guid.Parse("b8000000-0000-4000-8000-000000000002");
    private static readonly Guid TableB = Guid.Parse("b8000000-0000-4000-8000-000000000003");
    private static readonly Guid Unicode = Guid.Parse("b8000000-0000-4000-8000-000000000004");
    private static readonly Guid PmStatement = Guid.Parse("b8000000-0000-4000-8000-000000000005");
    private static readonly Guid EvA = Guid.Parse("e8000000-0000-4000-8000-000000000001");
    private static readonly Guid EvB = Guid.Parse("e8000000-0000-4000-8000-000000000002");
    private static readonly Guid GroupA = Guid.Parse("c8000000-0000-4000-8000-000000000001");
    private static readonly Guid GroupB = Guid.Parse("c8000000-0000-4000-8000-000000000002");

    // Quote non-BMP: coppie surrogate (𝄞 U+1D11E ed emoji): gli offset [start,end) sono unità UTF-16, mai punti di codice.
    private const string MusicQuote = "nota 𝄞 e 😀 fine";

    private static readonly Guid[] Analysis = [Guid.Parse("d8000000-0000-4000-8000-000000000001"), Guid.Parse("d8000000-0000-4000-8000-000000000002")];

    private static DocumentAnalysisItem Item(Guid id, string text, DocumentAnalysisClassification c, params Guid[] refs) => new() { Id = id, Text = text, Classification = c, EvidenceRefs = refs };

    private static DocumentAnalysisSource Passage(Guid id, string quote) => new()
    {
        Id = id, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = Guid.Parse("f8000000-0000-4000-8000-000000000001"),
        DocumentVersionId = Guid.Parse("f8000000-0000-4000-8000-000000000002"), PreparationId = Guid.Parse("f8000000-0000-4000-8000-000000000003"),
        ArtifactId = Guid.Parse("f8000000-0000-4000-8000-000000000004"), BlockId = Guid.NewGuid(), Start = 0, End = quote.Length, Quote = quote,
    };

    /// <summary>Revisione confermata 1.1: un obiettivo, due righe di una tabella multipagina che condividono l'header, un requisito non-BMP e un enunciato nato da un edit PM.</summary>
    private static DocumentAnalysisContent Confirmed() => new()
    {
        SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = Analysis[0], ProjectId = Guid.Parse("d8000000-0000-4000-8000-0000000000aa"), SourceCutoffSequence = 1,
        ValidationState = AnalysisContentState.PmConfirmed,
        Sources =
        [
            new DocumentAnalysisSource
            {
                Id = Msg, Kind = DocumentAnalysisSourceKind.Message, MessageId = Guid.NewGuid(), MessageRole = AnalysisMessageRole.User, RevisionId = null, DocumentId = null,
                DocumentVersionId = null, PreparationId = null, ArtifactId = null, BlockId = null, Start = null, End = null, Quote = null,
            },
            Passage(Header, "Codice | Descrizione"), Passage(Page2, "A1 | Accesso"), Passage(Page3, "B7 | Esportazione"), Passage(Music, MusicQuote),
            new DocumentAnalysisSource
            {
                Id = PmSource, Kind = DocumentAnalysisSourceKind.PmRevision, MessageId = null, MessageRole = null, RevisionId = Guid.Parse("d8000000-0000-4000-8000-0000000000bb"), DocumentId = null,
                DocumentVersionId = null, PreparationId = null, ArtifactId = null, BlockId = null, Start = null, End = null, Quote = null,
            },
        ],
        Goals = [Item(Goal, "Realizzare un portale clienti", DocumentAnalysisClassification.Declared, Msg)],
        ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
        Requirements =
        [
            Item(TableA, "Accesso (riga A1)", DocumentAnalysisClassification.Documented, Header, Page2),
            Item(TableB, "Esportazione (riga B7)", DocumentAnalysisClassification.Documented, Header, Page3),
            Item(Unicode, "Annotazioni musicali 𝄞 e 😀", DocumentAnalysisClassification.Documented, Music),
            Item(PmStatement, "Il PM aggiunge l'archiviazione", DocumentAnalysisClassification.Declared, PmSource),
        ],
        OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = [],
    };

    // Registro Evidence: due gruppi di citazione che condividono lo stesso header/sourceId.
    private static DraftSourceLedger Ledger() => new(
        new Dictionary<Guid, IReadOnlyList<Guid>> { [TableA] = [EvA], [TableB] = [EvB] },
        new Dictionary<Guid, IReadOnlyList<LedgerSource>>
        {
            [EvA] = [new(Header, GroupA, 0), new(Page2, GroupA, 1)],
            [EvB] = [new(Header, GroupB, 0), new(Page3, GroupB, 1)],
        });

    private static string Cite(params Guid[] sources) => string.Concat(sources.Select(s => $" [fonte:{s:D}]"));

    private static JsonObject BlockJson(Guid[] items, string markdown, Guid[] sources) => new()
    {
        ["itemIds"] = new JsonArray([.. items.Select(i => (JsonNode)i.ToString("D"))]),
        ["markdown"] = markdown,
        ["sourceIds"] = new JsonArray([.. sources.Select(s => (JsonNode)s.ToString("D"))]),
    };

    /// <summary>Output del modello: frase, tabella multipagina (parti richieste: pagina prima dell'header, header condiviso citato una volta), requisito non-BMP e enunciato PM.</summary>
    private static string Output(params JsonObject[] blocks) => new JsonObject { ["sectionKey"] = "documento", ["blocks"] = new JsonArray([.. blocks]) }.ToJsonString();

    private static JsonObject[] AllBlocks() =>
    [
        BlockJson([Goal], "Obiettivo: portale clienti" + Cite(Msg), [Msg]),
        BlockJson([TableA, TableB], "| Codice | Funzione |\n|---|---|\n| A1 | Accesso" + Cite(Page2, Header) + " |\n| B7 | Esportazione" + Cite(Page3) + " |", [Page2, Header, Page3]),
        BlockJson([Unicode], "Annotazioni 𝄞 e 😀 nel documento" + Cite(Music), [Music]),
        BlockJson([PmStatement], "Archiviazione richiesta dal PM" + Cite(PmSource), [PmSource]),
    ];

    private static IEnumerable<Guid> Markers(string markdown) =>
        Regex.Matches(markdown, @"\[fonte:([0-9a-f-]{36})\]").Select(m => Guid.Parse(m.Groups[1].Value));

    [Fact]
    public void A_confirmed_revision_roundtrips_into_a_draft_whose_markers_equal_the_block_sourceIds_and_the_historical_map()
    {
        var content = Confirmed();
        var restored = DocumentAnalysisContentJson.Deserialize(DocumentAnalysisContentJson.Serialize(content));
        Assert.Equal(DocumentAnalysisContent.SchemaVersionValue, restored.SchemaVersion);   // 1.1.0 resta 1.1.0, mai convertita
        var package = DocumentDraftPackage.From(restored);

        var validation = DocumentDraftValidator.ValidateBlocks(Output(AllBlocks()), package);

        Assert.True(validation.Accepted, string.Join(" | ", validation.Violations));
        var blocks = validation.Output!.Blocks;
        // Marcatori atomici nell'ordine richiesto, uno per sourceId, coincidenti con sourceIds di ogni blocco (e con quelli dell'intero Markdown).
        Assert.All(blocks, b => Assert.Equal(b.SourceIds, Markers(b.Markdown)));
        Assert.Equal(blocks.SelectMany(b => b.SourceIds), Markers(validation.Markdown!));

        var map = DraftSourceMap.Build(blocks, package.ItemSources, Ledger());

        Assert.Equal(
            [
                new DraftSourceMapEntry(Goal, null, null, null, Msg),
                new DraftSourceMapEntry(TableA, EvA, GroupA, 1, Page2), new DraftSourceMapEntry(TableA, EvA, GroupA, 0, Header),
                new DraftSourceMapEntry(TableB, EvB, GroupB, 0, Header), new DraftSourceMapEntry(TableB, EvB, GroupB, 1, Page3),
                new DraftSourceMapEntry(Unicode, null, null, null, Music),
                new DraftSourceMapEntry(PmStatement, null, null, null, PmSource),
            ],
            map);
        // I due gruppi condividono l'header ma non il gruppo: la mappa ha due voci distinte per lo stesso sourceId, non un unico gruppo dedotto dalla sequenza globale.
        Assert.Equal([GroupA, GroupB], map.Where(e => e.SourceId == Header).Select(e => e.CitationGroupId!.Value));
        Assert.Equal(2, map.Where(e => e.SourceId == Header).Select(e => e.CitationGroupId).Distinct().Count());
    }

    [Fact]
    public void A_table_cited_on_several_pages_keeps_one_atomic_marker_per_part_and_a_marker_sequence_that_does_not_define_the_groups()
    {
        var package = DocumentDraftPackage.From(Confirmed());
        // La sequenza globale dei sourceIds [Page2, Header, Page3] non basta a stabilire i gruppi: l'header e' di due gruppi, ma compare una sola volta.
        var blocks = DocumentDraftValidator.ValidateBlocks(Output(AllBlocks()), package).Output!.Blocks;
        var ledgerless = DraftSourceMap.Build(blocks, package.ItemSources, DraftSourceLedger.Empty);

        Assert.Equal(2, ledgerless.Count(e => e.SourceId == Header));
        Assert.All(ledgerless, e => Assert.Null(e.CitationGroupId));   // senza registro i gruppi non si inventano dalla sequenza
        Assert.Equal([TableA, TableB], ledgerless.Where(e => e.SourceId == Header).Select(e => e.ItemId));
    }

    [Fact]
    public void Non_BMP_text_and_quotes_survive_the_revision_the_validator_and_the_draft_with_utf16_offsets()
    {
        var restored = DocumentAnalysisContentJson.Deserialize(DocumentAnalysisContentJson.Serialize(Confirmed()));
        var music = restored.Sources.Single(s => s.Id == Music);

        Assert.Equal(MusicQuote, music.Quote);
        Assert.Equal(music.End - music.Start, music.Quote!.Length);   // [start,end) in unità UTF-16: le coppie surrogate contano due
        Assert.True(music.Quote.Length > MusicQuote.EnumerateRunes().Count());
        var validation = DocumentDraftValidator.ValidateBlocks(Output(AllBlocks()), DocumentDraftPackage.From(restored));
        Assert.True(validation.Accepted, string.Join(" | ", validation.Violations));
        Assert.Contains("𝄞 e 😀", validation.Markdown);
    }

    [Fact]
    public void After_a_pm_edit_that_drops_a_reference_or_a_source_the_removed_source_cannot_support_new_text_while_the_historical_revision_stays_valid()
    {
        var historical = Confirmed();
        var historicalPackage = DocumentDraftPackage.From(historical);
        var historicalDraft = DocumentDraftValidator.ValidateBlocks(Output(AllBlocks()), historicalPackage);
        Assert.True(historicalDraft.Accepted);

        // Edit 1: il PM toglie l'header dai riferimenti di entrambe le righe. Edit 2: la pagina 3 viene rimossa del tutto (fonte e riferimento).
        var edited = historical with
        {
            Requirements =
            [
                historical.Requirements[0] with { EvidenceRefs = [Page2] }, historical.Requirements[1] with { EvidenceRefs = [Page3] }, .. historical.Requirements.Skip(2),
            ],
        };
        var removedPage = historical with
        {
            Sources = [.. historical.Sources.Where(s => s.Id != Page3)],
            Requirements = [historical.Requirements[0], historical.Requirements[1] with { EvidenceRefs = [] }, .. historical.Requirements.Skip(2)],
        };

        var droppedReference = DocumentDraftValidator.ValidateBlocks(Output(AllBlocks()), DocumentDraftPackage.From(edited));
        var removedSource = DocumentDraftValidator.ValidateBlocks(Output(AllBlocks()), DocumentDraftPackage.From(removedPage));

        Assert.False(droppedReference.Accepted);
        Assert.Contains(droppedReference.Violations, v => v.Contains(Header.ToString("D"), StringComparison.Ordinal));
        Assert.False(removedSource.Accepted);
        Assert.Contains(removedSource.Violations, v => v.Contains(Page3.ToString("D"), StringComparison.Ordinal));
        // Il testo nuovo che cita solo cio' che l'edit conserva e' valido.
        var survivors = AllBlocks();
        survivors[1] = BlockJson([TableA, TableB], "| Codice | Funzione |\n|---|---|\n| A1 | Accesso" + Cite(Page2) + " |\n| B7 | Esportazione" + Cite(Page3) + " |", [Page2, Page3]);
        Assert.True(DocumentDraftValidator.ValidateBlocks(Output(survivors), DocumentDraftPackage.From(edited)).Accepted);
        // La revisione storica non e' modificata dall'edit e la sua bozza resta valida e apribile: la mappa di allora conserva i due gruppi dell'header.
        Assert.Equal(new[] { Header, Page3 }, historical.Requirements[1].EvidenceRefs.ToArray());
        Assert.True(DocumentDraftValidator.ValidateBlocks(Output(AllBlocks()), DocumentDraftPackage.From(historical)).Accepted);
        var historicalMap = DraftSourceMap.Build(historicalDraft.Output!.Blocks, historicalPackage.ItemSources, Ledger());
        Assert.Equal([GroupA, GroupB], historicalMap.Where(e => e.SourceId == Header).Select(e => e.CitationGroupId!.Value));
    }

    [Fact]
    public void A_new_pm_statement_cites_the_pm_revision_source_with_no_group_while_a_historical_pm_unrelated_source_cannot_support_it()
    {
        var package = DocumentDraftPackage.From(Confirmed());
        var wrong = AllBlocks();
        wrong[3] = BlockJson([PmStatement], "Archiviazione richiesta dal PM" + Cite(Header), [Header]);   // l'header non sostiene l'enunciato del PM

        var rejected = DocumentDraftValidator.ValidateBlocks(Output(wrong), package);
        var accepted = DocumentDraftValidator.ValidateBlocks(Output(AllBlocks()), package);

        Assert.False(rejected.Accepted);
        Assert.True(accepted.Accepted);
        var entry = Assert.Single(DraftSourceMap.Build(accepted.Output!.Blocks, package.ItemSources, Ledger()), e => e.ItemId == PmStatement);
        Assert.Equal(new DraftSourceMapEntry(PmStatement, null, null, null, PmSource), entry);
    }

    [Fact]
    public void A_historical_1_0_revision_is_never_decoded_as_1_1_and_a_1_1_marker_is_never_a_1_0_fact()
    {
        var legacy = new JsonObject
        {
            ["schemaVersion"] = "1.0.0", ["analysisId"] = Analysis[1].ToString(), ["projectId"] = Guid.NewGuid().ToString(), ["sourceCutoffSequence"] = 1, ["validationState"] = "PmConfirmed",
            ["sources"] = new JsonArray(new JsonObject
            {
                ["id"] = Msg.ToString(), ["kind"] = "Message", ["messageId"] = Guid.NewGuid().ToString(), ["messageRole"] = "user", ["revisionId"] = null,
            }),
            ["goals"] = new JsonArray(new JsonObject
            {
                ["id"] = Goal.ToString(), ["text"] = "Portale", ["classification"] = "Declared", ["evidenceRefs"] = new JsonArray(Msg.ToString()),
            }),
            ["contextAndScope"] = new JsonObject { ["overview"] = new JsonArray(), ["included"] = new JsonArray(), ["excluded"] = new JsonArray() },
            ["requirements"] = new JsonArray(), ["operationalFlows"] = new JsonArray(), ["constraints"] = new JsonArray(), ["dependencies"] = new JsonArray(),
            ["assumptions"] = new JsonArray(), ["openQuestions"] = new JsonArray(), ["conflicts"] = new JsonArray(),
        }.ToJsonString();

        var v10 = AnalysisContentJson.Deserialize(legacy);

        Assert.Equal("1.0.0", v10.SchemaVersion);
        Assert.Throws<JsonException>(() => DocumentAnalysisContentJson.Deserialize(legacy));   // nessun decoder permissivo, nessuna conversione
        Assert.Throws<JsonException>(() => AnalysisContentJson.Deserialize(DocumentAnalysisContentJson.Serialize(Confirmed())));   // e viceversa
        Assert.Equal(Msg, v10.Sources[0].Id);
    }
}
