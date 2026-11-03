using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// T067: regole del prompt di analisi documentale emerse dal modello reale (DGX), fissate come contratto deterministico del TESTO inviato.
/// Prova reale (I4-real.md): con «id nuovo» il modello ha emesso etichette (`req-1`) invece di UUID, e con `certezza=Declared` nell'intestazione
/// di una voce ha classificato `Declared` elementi che vengono solo da documenti. La qualita della risposta resta provata solo dal run reale.
/// </summary>
public sealed class DocumentAnalysisPromptContractTests
{
    private static string Normalized => DocumentAnalysisWorkflow.Instructions.Replace("\r\n", "\n");

    [Fact]
    public void The_prompt_requires_new_item_ids_to_be_lowercase_UUIDs_never_labels()
    {
        Assert.Contains("`id` = UUID minuscolo nuovo e distinto che generi tu (formato 8-4-4-4-12, mai etichette come `req-1`)", Normalized, StringComparison.Ordinal);
        Assert.Contains("`itemId` = l'`id` UUID dell'elemento", Normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_separates_the_evidence_certainty_from_the_item_classification()
    {
        Assert.Contains("La `certezza` nell'intestazione di una voce è un attributo dell'evidenza estratta e NON è la classificazione dell'elemento", Normalized, StringComparison.Ordinal);
        Assert.Contains("`origine=Document` → `Documented`", Normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_has_the_model_copy_the_servers_sorted_visitedEntryIds_instead_of_sorting_hashes()
    {
        Assert.Contains("`visitedEntryIds` copiato identico dall'elenco `visitedEntryIds` dei dati del server (stessi elementi, stesso ordine, ciascuno una sola volta: non ordinare né ricalcolare)", Normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("TUTTI gli `entryId` ricevuti, in ordine alfabetico", Normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_lists_every_required_property_of_a_result_because_the_real_model_omitted_visitedNodeIds()
    {
        Assert.Contains("ESATTAMENTE queste dodici proprietà, nessuna omessa: `view`, `level`, `descriptor`, `batchId`, `inputHash`, `visitedEntryIds`, `visitedNodeIds`, `candidates`, `dispositions`, `itemProposals`, `patches`, `noFindingsReason`", Normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_forbids_repeating_items_because_the_real_model_looped_over_the_requirements_array()
    {
        Assert.Contains("Ogni elemento compare UNA sola volta, in una sola sezione", Normalized, StringComparison.Ordinal);
        Assert.Contains("chiudi ogni array dopo l'ultimo elemento", Normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_lists_every_required_property_of_a_source_because_the_real_model_omitted_the_null_ones()
    {
        Assert.Contains("ESATTAMENTE tredici proprietà, nessuna omessa: `id`, `kind`, `messageId`, `messageRole`, `revisionId`, `documentId`, `documentVersionId`, `preparationId`, `artifactId`, `blockId`, `start`, `end`, `quote`", Normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_never_asks_the_model_for_candidates_it_cannot_build_and_routes_conflicts_to_analysis_conflicts()
    {
        Assert.Contains("Non emettere mai candidati, disposizioni, proposte o patch", Normalized, StringComparison.Ordinal);
        Assert.Contains("Conflitti e relazioni fra evidenze si esprimono SOLO in `analysis.conflicts`", Normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("è un candidato con una disposition terminale", Normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_lists_every_required_property_of_analysis_and_asks_for_distinct_ids_because_the_real_model_omitted_sourceCutoffSequence()
    {
        Assert.Contains("ESATTAMENTE queste quindici proprietà, nessuna omessa: `schemaVersion`, `analysisId`, `projectId`, `sourceCutoffSequence`, `validationState`, `sources`, `goals`, `contextAndScope`, `requirements`, `operationalFlows`, `constraints`, `dependencies`, `assumptions`, `openQuestions`, `conflicts`", Normalized, StringComparison.Ordinal);
        Assert.Contains("Tutti gli `id` dell'analisi (elementi, alternative, conflitti) sono UUID distinti fra loro, mai ripetuti", Normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_asks_for_compact_JSON_to_save_output_tokens()
    {
        Assert.Contains("JSON compatto (nessuna indentazione né a capo)", Normalized, StringComparison.Ordinal);
    }
}
