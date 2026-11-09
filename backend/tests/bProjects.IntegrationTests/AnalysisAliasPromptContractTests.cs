using bProjects.App.AI;
using bProjects.App.Analysis;

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
    public void The_prompt_states_global_uniqueness_of_local_keys_up_front_with_an_example_and_repeats_it_in_the_final_reminder()
    {
        // Sesta esecuzione reale: 4 analisi su 7 scartate per la stessa chiave in piu sezioni (goals+requirements, scopeIncluded+requirements, constraints+conflicts).
        const string rule = "ogni chiave locale è usata UNA SOLA volta in tutto il documento JSON, in qualsiasi sezione";
        var first = Prompt.IndexOf(rule, StringComparison.Ordinal);
        var last = Prompt.LastIndexOf(rule, StringComparison.Ordinal);
        Assert.True(first >= 0, "regola assente");
        Assert.True(first < Prompt.IndexOf("# Input", StringComparison.Ordinal), "la regola deve aprire il prompt, prima di # Input");
        Assert.True(last > Prompt.IndexOf("# Output", StringComparison.Ordinal), "promemoria finale assente");
        Assert.NotEqual(first, last);
        Assert.Contains("una voce appartiene a una sola sezione", Prompt, StringComparison.Ordinal);
        Assert.Contains("anche le alternative dei conflitti usano chiavi nuove", Prompt, StringComparison.Ordinal);
        Assert.Contains("numera le chiavi progressivamente a livello globale", Prompt, StringComparison.Ordinal);
        Assert.Contains("CORRETTO:", Prompt, StringComparison.Ordinal);
        Assert.Contains("ERRATO:", Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_keeps_the_v3_contract_properties_and_the_label_rules()
    {
        Assert.Contains("ESATTAMENTE queste otto proprietà", Prompt, StringComparison.Ordinal);
        Assert.Contains("`\"alias-1\"`", Prompt, StringComparison.Ordinal);
        Assert.Contains("mai le etichette `S<n>`", Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_has_no_copyable_concrete_known_relation_alias_and_declares_that_without_known_relations_the_checks_are_empty()
    {
        // N3 quarta esecuzione: il modello ha copiato l'esempio `R1` pur senza relazioni note.
        Assert.DoesNotMatch(@"R\d+", Prompt);
        Assert.Contains("`Relazioni note: nessuna`", Prompt, StringComparison.Ordinal);
        Assert.Contains("`knownRelationChecks` deve essere `[]`", Prompt, StringComparison.Ordinal);
        Assert.Contains("non inventare alias `R<n>`", Prompt, StringComparison.Ordinal);
        Assert.Contains("\"knownRelationChecks\":[]", Prompt, StringComparison.Ordinal);
    }

    // ---- Classificazione per sezione (N2: item Documented in assumptions, rifiutato correttamente dal validatore) ----

    private static readonly Guid SourceId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static DocumentAnalysisItem Item(DocumentAnalysisClassification c, int n) =>
        new() { Id = Guid.Parse($"00000000-0000-0000-0000-{n:D12}"), Text = "x", Classification = c, EvidenceRefs = [SourceId] };

    private static IReadOnlyList<string> CheckAssumptions(DocumentAnalysisClassification c) =>
        DocumentAnalysisContentRules.Check(
            new DocumentAnalysisContent
            {
                SchemaVersion = DocumentAnalysisContent.SchemaVersionValue,
                AnalysisId = Guid.Parse("00000000-0000-0000-0000-0000000000b1"),
                ProjectId = Guid.Parse("00000000-0000-0000-0000-0000000000b2"),
                SourceCutoffSequence = 0,
                ValidationState = AnalysisContentState.Unvalidated,
                Sources =
                [
                    new DocumentAnalysisSource
                    {
                        Id = SourceId, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null,
                        DocumentId = Guid.NewGuid(), DocumentVersionId = Guid.NewGuid(), PreparationId = Guid.NewGuid(), ArtifactId = Guid.NewGuid(), BlockId = Guid.NewGuid(),
                        Start = 0, End = 1, Quote = "q",
                    },
                ],
                Goals = [],
                ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
                Requirements = [Item(c == DocumentAnalysisClassification.Declared ? DocumentAnalysisClassification.Documented : c, 1)],
                OperationalFlows = [],
                Constraints = [],
                Dependencies = [],
                Assumptions = [Item(c, 2)],
                OpenQuestions = [],
                Conflicts = [],
            },
            new DocumentAnalysisFacts(Guid.Parse("00000000-0000-0000-0000-0000000000b2"), Guid.Parse("00000000-0000-0000-0000-0000000000b1"), 0, 0, 1));

    [Fact]
    public void The_validator_admits_only_Assumed_in_assumptions_as_declared_by_the_prompt_table()
    {
        Assert.Empty(CheckAssumptions(DocumentAnalysisClassification.Assumed));
        Assert.Contains(CheckAssumptions(DocumentAnalysisClassification.Documented), v => v.StartsWith("assumptions[0].classification", StringComparison.Ordinal));
        Assert.Contains(CheckAssumptions(DocumentAnalysisClassification.Inferred), v => v.StartsWith("assumptions[0].classification", StringComparison.Ordinal));
        Assert.Contains(CheckAssumptions(DocumentAnalysisClassification.Declared), v => v.StartsWith("assumptions[0].classification", StringComparison.Ordinal));
        // Le altre sezioni (qui requirements) ammettono Documented, Inferred e Assumed con una fonte documentale.
        Assert.DoesNotContain(CheckAssumptions(DocumentAnalysisClassification.Assumed), v => v.StartsWith("requirements", StringComparison.Ordinal));

        Assert.Contains("| `assumptions` | SOLO `Assumed` (mai `Documented`, `Declared`, `Inferred`) |", Prompt, StringComparison.Ordinal);
        Assert.Contains("| tutte le altre nove sezioni | `Declared`, `Documented`, `Inferred`, `Assumed`, nei limiti della tabella sull'origine |", Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_defines_the_four_classifications_and_keeps_a_documented_fact_with_a_condition_out_of_assumptions()
    {
        Assert.Contains("`Documented` = requisito o fatto sostenuto dai documenti", Prompt, StringComparison.Ordinal);
        Assert.Contains("`Declared` = informazione dichiarata dal project manager", Prompt, StringComparison.Ordinal);
        Assert.Contains("`Assumed` = ipotesi non confermata, introdotta come assunzione", Prompt, StringComparison.Ordinal);
        Assert.Contains("`Inferred` = deduzione che richiede validazione secondo le regole di dominio", Prompt, StringComparison.Ordinal);
        Assert.Contains("NON si sposta in `assumptions` solo perché contiene un dubbio, una condizione o un limite", Prompt, StringComparison.Ordinal);
        Assert.Contains("resta nella sezione sostantiva", Prompt, StringComparison.Ordinal);
        Assert.Contains("va in `openQuestions`", Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_assumptions_example_uses_only_placeholders_that_cannot_be_copied_as_real_content()
    {
        Assert.Contains("da NON copiare", Prompt, StringComparison.Ordinal);
        Assert.Contains("<X>", Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Linee guida", Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"i2\d", Prompt);
    }
}
