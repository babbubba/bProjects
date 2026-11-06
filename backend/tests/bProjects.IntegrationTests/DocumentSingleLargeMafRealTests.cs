namespace bProjects.IntegrationTests;

/// <summary>
/// T082 (DGX) — analisi di UN documento grande con tutto reale: API di produzione (Analizza, v4), parser Docling reale, PostgreSQL, MAF e modello interno reale. Il testo REALMENTE estratto dal
/// parser supera 2x il budget di input Analysis effettivo (calcolato a runtime dal profilo del runtime; nessuna riduzione del documento, finestra non aumentata). Gold, conflitti e qualificatori sono
/// dichiarati in <see cref="LargeDocumentCorpus"/> (16 fatti tra inizio/meta/fine, due conflitti lontani, negazioni, condizioni, eccezioni, numeri) e non si adattano alle risposte.
/// Verifiche etichettate STRUTTURALE / COPERTURA / SEMANTICA / PROVENIENZA / COMPLETAMENTO; un run non Completed o un errore gestito e un FAIL. Un solo test per volta sul DGX (slot unico):
/// eseguire con <c>--filter-method</c>, mai in parallelo con T083. Servono BPROJECTS_TEST_POSTGRES, BPROJECTS_TEST_PARSER_ENDPOINT/_SECRET e BPROJECTS_TEST_DGX_* con opt-in; senza, Skipped.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
[Collection(RealParser.CollectionName)]
public sealed class DocumentSingleLargeMafRealTests : LargeDocumentRealFlow
{
    private const int BulkRules = 260;

    [Fact]
    public async Task T082_a_single_large_document_goes_through_the_hierarchical_v4_analysis_with_every_gold_fact_conflict_and_citation()
    {
        TestEnvironment.RequireDgx();
        await Require();
        await RunScenarioAsync("T082", filler => LargeDocumentCorpus.Single(filler, BulkRules));
    }
}
