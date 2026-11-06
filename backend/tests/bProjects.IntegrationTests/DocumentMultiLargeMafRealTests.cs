namespace bProjects.IntegrationTests;

/// <summary>
/// T083 (DGX) — analisi AGGREGATA di tre documenti di formati diversi (TXT, Markdown, DOCX) con tutto reale (come T082): testo aggregato estratto oltre 2x il budget di input Analysis effettivo,
/// fatti complementari fra documenti, conflitti fra documenti e documento-PM (dichiarazione di chat, fonte Message/Declared), chiavi parafrasate, riferimenti a porzioni distanti, alternative
/// preservate, nessuna risoluzione arbitraria. Gold in <see cref="LargeDocumentCorpus"/>; nessun aumento della finestra, riduzione o esclusione di file. Un solo test per volta sul DGX (slot unico),
/// serialmente con T082: eseguire con <c>--filter-method</c>. Servono BPROJECTS_TEST_POSTGRES, BPROJECTS_TEST_PARSER_ENDPOINT/_SECRET e BPROJECTS_TEST_DGX_* con opt-in; senza, Skipped.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
[Collection(RealParser.CollectionName)]
public sealed class DocumentMultiLargeMafRealTests : LargeDocumentRealFlow
{
    private const int BulkRulesPerDocument = 90;

    [Fact]
    public async Task T083_three_mixed_format_documents_and_a_pm_statement_go_through_the_hierarchical_v4_analysis_keeping_complements_conflicts_and_alternatives()
    {
        TestEnvironment.RequireDgx();
        await Require();
        await RunScenarioAsync("T083", filler => LargeDocumentCorpus.Multi(filler, BulkRulesPerDocument));
    }
}
