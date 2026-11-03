using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// T125 dopo la prima prova reale N1-N3 (I4-real-v3-N1-N3.md): regole del prompt v3 fissate come contratto deterministico del TESTO inviato.
/// Cause dimostrate: Declared per evidenze solo documentali (N1/N2/N3), noFindings A/B null senza finding di quella vista (N1), chiave locale
/// ripetuta in due sezioni o fra conflitto e item (N2/N3). La qualita della risposta resta provata solo dal run reale.
/// </summary>
public sealed class AnalysisAliasPromptContractTests
{
    private static string Prompt => DocumentAnalysisWorkflow.InstructionsV3.Replace("\r\n", "\n");

    [Fact]
    public void The_prompt_maps_the_evidence_origin_to_the_admitted_classifications_with_a_table_and_an_example()
    {
        Assert.Contains("| origine delle evidenze citate | classificazioni ammesse |", Prompt, StringComparison.Ordinal);
        Assert.Contains("| solo `origine=Document` | `Documented`, `Inferred`, `Assumed` (MAI `Declared`) |", Prompt, StringComparison.Ordinal);
        Assert.Contains("| solo `origine=Message` | `Declared`, `Inferred`, `Assumed` (MAI `Documented`) |", Prompt, StringComparison.Ordinal);
        Assert.Contains("| almeno una `origine=Message` e almeno una `origine=Document` | `Declared`, `Documented`, `Inferred`, `Assumed` |", Prompt, StringComparison.Ordinal);
        Assert.Contains("Esempio:", Prompt, StringComparison.Ordinal);
        Assert.Contains("un elemento che cita SOLO evidenze `origine=Document` non è mai `Declared`", Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_no_longer_describes_an_evidence_certainty_that_could_be_copied_as_a_classification()
    {
        Assert.DoesNotContain("certezza", Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_prompt_requires_an_explicit_reason_for_every_view_without_findings_never_null()
    {
        Assert.Contains("`noFindings.A` è OBBLIGATORIAMENTE una stringa non vuota se nessun finding ha `view` `A`", Prompt, StringComparison.Ordinal);
        Assert.Contains("`noFindings.B` è OBBLIGATORIAMENTE una stringa non vuota se nessun finding ha `view` `B`", Prompt, StringComparison.Ordinal);
        Assert.Contains("anche se esistono finding di altre viste", Prompt, StringComparison.Ordinal);
        Assert.Contains("`null` solo per una vista che ha almeno un finding", Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_states_that_a_key_lives_in_one_place_and_conflict_alternatives_only_in_the_conflict()
    {
        Assert.Contains("ogni chiave `i<n>` compare UNA sola volta in tutto l'output, in UNA sola sezione", Prompt, StringComparison.Ordinal);
        Assert.Contains("le alternative di un conflitto vivono SOLO in `conflicts[].alternatives`: non ripeterle mai in `items`", Prompt, StringComparison.Ordinal);
        Assert.Contains("Esempio di conflitto:", Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_keeps_the_v3_contract_properties_and_the_label_rules()
    {
        Assert.Contains("ESATTAMENTE queste otto proprietà", Prompt, StringComparison.Ordinal);
        Assert.Contains("`\"alias-1\"`", Prompt, StringComparison.Ordinal);
        Assert.Contains("mai le etichette `S<n>`", Prompt, StringComparison.Ordinal);
    }
}
