using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 T152 (DET) — D3: avviso STRUTTURALE e non bloccante in coverage per la ripetizione fra un item ordinario e l'alternativa di un conflitto (stesse evidenze, stessa classificazione, stesso
/// multiinsieme di token numerici, polarità/negazione concordi). Nessuna eliminazione, correzione o rifiuto; nessuna deduplicazione semantica; solo revisioni di definizione v6 (flag
/// `StructuralRepetition`), i report v5 restano byte-identici. La `Coherence` esatta del composer (AnalysisAliasComposer) resta invariata ed è provata dai suoi test storici.
/// </summary>
public sealed class DocumentStructuralRepetitionTests
{
    private const string Code = "item_repeats_conflict_alternative";

    private static readonly Guid E1 = Guid.Parse("00000001-0000-4000-8000-000000000001");
    private static readonly Guid E2 = Guid.Parse("00000002-0000-4000-8000-000000000002");
    private static readonly Guid E3 = Guid.Parse("00000003-0000-4000-8000-000000000003");

    private sealed record Spec(string Text, Guid[] Evidence, DocumentAnalysisClassification Classification = DocumentAnalysisClassification.Documented);

    private static DocumentCoverageInput Build(Spec ordinary, Spec alternative, Spec? otherAlternative = null, bool structural = true)
    {
        var ordinaryItem = Item(ordinary);
        var altItem = Item(alternative);
        (DocumentAnalysisItem Item, List<(Guid ItemId, Guid EvidenceId)> Links)? other = otherAlternative is null ? null : Item(otherAlternative);
        DocumentAnalysisItem[] alternatives = other is null ? [altItem.Item] : [altItem.Item, other.Value.Item];
        var content = new DocumentAnalysisContent
        {
            SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), SourceCutoffSequence = 0,
            ValidationState = AnalysisContentState.Unvalidated, Sources = [], Goals = [],
            ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = [ordinaryItem.Item], OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [],
            Conflicts =
            [
                new DocumentAnalysisConflict
                {
                    Id = Guid.NewGuid(), Description = "Limite discordante.", Status = AnalysisConflictStatus.Open, Alternatives = alternatives, Resolution = null,
                },
            ],
        };
        var links = ordinaryItem.Links.Concat(altItem.Links).Concat(other?.Links ?? []).ToList();
        var evidence = new[] { E1, E2, E3 }.Select((id, i) => new DocumentCoverageEvidence(
            id, $"E{i + 1}", EvidenceOriginKind.Document, null, EvidenceKind.Requirement, EvidencePolarity.Affirmed, "x", [], null, null, [])).ToList();
        return new DocumentCoverageInput(evidence, links, content, content, null, new Dictionary<Guid, string>(), [], []) with { StructuralRepetition = structural };
    }

    private static (DocumentAnalysisItem Item, List<(Guid ItemId, Guid EvidenceId)> Links) Item(Spec spec)
    {
        var item = new DocumentAnalysisItem { Id = Guid.NewGuid(), Text = spec.Text, Classification = spec.Classification, EvidenceRefs = [] };
        return (item, [.. spec.Evidence.Select(e => (item.Id, e))]);
    }

    private static List<DocumentCoverageFinding> Warnings(DocumentCoverageInput input) => [.. DocumentCoverage.Check(input).Where(f => f.Code == Code)];

    private static readonly Guid[] Both = [E1, E2];

    // ---------------------------------------------------------------- rilevazione

    [Fact]
    public void An_item_that_repeats_an_alternative_with_same_evidence_classification_and_numbers_is_a_non_blocking_semantic_warning()
    {
        var input = Build(new("Il limite giornaliero è di 5 ordini.", Both), new("Il limite è 5 ordini al giorno.", Both));

        var warning = Assert.Single(Warnings(input));

        Assert.Equal(FindingCategory.SemanticRisk, warning.Category);
        Assert.False(warning.Blocking);
        Assert.NotNull(warning.Confidence);
        Assert.Equal(input.Target.Requirements[0].Id, warning.ItemId);
        Assert.Equal(input.Target.Conflicts[0].Alternatives[0].Id.ToString(), warning.Details);
        var summary = DocumentCoverage.Summarize(input, DocumentCoverage.Check(input));
        Assert.Equal(DocumentCoverage.Check(input with { StructuralRepetition = false }).Count(f => f.Blocking), summary.BlockingFindings);   // l'avviso non aggiunge alcun blocco
        Assert.True(summary.SemanticRisks >= 1);
    }

    [Fact]
    public void Number_words_and_digits_count_as_the_same_token_and_the_evidence_order_does_not_matter()
    {
        var input = Build(new("Al massimo cinque ordini al giorno.", [E1, E2]), new("Il tetto è 5 ordini giornalieri.", [E2, E1]));

        Assert.Single(Warnings(input));
    }

    [Fact]
    public void The_check_never_alters_the_revision_it_only_adds_a_warning()
    {
        var input = Build(new("Il limite è di 5 ordini.", Both), new("Il limite è 5 ordini.", Both));
        var before = (input.Target.Requirements.Count, input.Target.Conflicts[0].Alternatives.Count, input.Links.Count);

        var findings = DocumentCoverage.Check(input);

        Assert.Equal(before, (input.Target.Requirements.Count, input.Target.Conflicts[0].Alternatives.Count, input.Links.Count));
        Assert.Contains(findings, f => f.Code == Code);
    }

    // ---------------------------------------------------------------- niente falsi positivi strutturali

    [Fact]
    public void Different_numbers_evidence_or_classification_are_not_a_repetition()
    {
        Assert.Empty(Warnings(Build(new("Il limite è di 5 ordini.", Both), new("Il limite è di 8 ordini.", Both))));                                   // numeri diversi
        Assert.Empty(Warnings(Build(new("Il limite è di 5 ordini.", Both), new("Il limite è di 5 ordini.", [E1, E3]))));                              // evidenze diverse
        Assert.Empty(Warnings(Build(new("Il limite è di 5 ordini.", Both), new("Il limite è di 5 ordini.", [E1]))));                                  // sottoinsieme non è uguaglianza
        Assert.Empty(Warnings(Build(new("Il limite è di 5 ordini.", Both), new("Il limite è di 5 ordini.", Both, DocumentAnalysisClassification.Declared)))); // classificazione diversa
    }

    [Fact]
    public void The_number_multiset_not_the_set_decides()
    {
        Assert.Empty(Warnings(Build(new("Fra 5 e 5 ordini.", Both), new("Entro 5 ordini.", Both))));
        Assert.Single(Warnings(Build(new("Fra 5 e 8 ordini.", Both), new("Da 8 a 5 ordini.", Both))));
    }

    [Theory]
    [InlineData("La consegna avviene entro 30 giorni.", "La consegna non avviene entro 30 giorni.")]
    [InlineData("Il portale invia 2 notifiche.", "Il portale non invia 2 notifiche.")]
    [InlineData("Sono ammessi 3 allegati.", "Sono vietati 3 allegati.")]
    public void An_inversion_of_polarity_between_the_two_texts_is_never_a_repetition(string ordinary, string alternative)
    {
        Assert.Empty(Warnings(Build(new(ordinary, Both), new(alternative, Both))));
        Assert.Empty(Warnings(Build(new(alternative, Both), new(ordinary, Both))));                                                                       // simmetrico
    }

    [Fact]
    public void Items_without_links_alternatives_among_themselves_and_ordinary_items_among_themselves_are_not_compared()
    {
        Assert.Empty(Warnings(Build(new("Il limite è di 5 ordini.", []), new("Il limite è di 5 ordini.", []))));                                      // nessun collegamento: nessun insieme di evidenze
        Assert.Empty(Warnings(Build(new("Altro 9.", [E3]), new("Il limite è di 5 ordini.", Both), new("Il limite è 5 ordini.", Both))));              // due alternative uguali fra loro: non è un item ordinario
    }

    [Fact]
    public void Without_the_v6_flag_the_report_is_byte_identical_to_the_v5_one()
    {
        var spec = (new Spec("Il limite è di 5 ordini.", Both), new Spec("Il limite è 5 ordini.", Both));
        var off = DocumentCoverage.Check(Build(spec.Item1, spec.Item2, structural: false));
        var on = DocumentCoverage.Check(Build(spec.Item1, spec.Item2, structural: true));

        Assert.DoesNotContain(off, f => f.Code == Code);
        Assert.Equal(off.Count + 1, on.Count);
        Assert.Equal(off.Select(f => f.Code), on.Where(f => f.Code != Code).Select(f => f.Code));
    }

    [Theory]
    [InlineData("project-analysis-documents-v6", true)]
    [InlineData("project-analysis-documents-v5", false)]
    [InlineData("project-analysis-documents-v4", false)]
    [InlineData("project-analysis-documents-v3", false)]
    [InlineData("project-analysis-documents-v2", false)]
    [InlineData("", false)]
    public void Only_revisions_of_the_v6_definition_get_the_structural_warning(string definition, bool expected) =>
        Assert.Equal(expected, DocumentCoverage.AppliesStructuralRepetition(definition));
}
