using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-020 (M2, T168) — la proiezione deterministica degli item delle foglie A (<c>leaf-projection-1</c>, plan §4). Una funzione pura: voce → item (testo = enunciato letterale,
/// evidenza = la voce, fonti della voce, sezione e classificazione dalla tabella per tipo, origine e CERTEZZA). La proiezione è una rappresentazione INTERMEDIA: i test provano che la tabella preserva le
/// distinzioni del prodotto (fatti documentati, requisiti, decisioni, ipotesi, alternative, domande aperte), non che la documentazione finale sia di buona qualità (livelli 7–8, con gold annotato prima).
/// La copertura completa non è il criterio di nessuno di questi test. Nessun database, nessuna inferenza.
/// </summary>
public sealed class ReconcileLeafProjectionTests
{
    private static readonly EvidenceKind[] Kinds = Enum.GetValues<EvidenceKind>();
    private static readonly EvidenceOriginKind[] Origins = Enum.GetValues<EvidenceOriginKind>();
    private static readonly EvidenceCertainty[] Certainties = Enum.GetValues<EvidenceCertainty>();

    private static DocumentAnalysisClassification ByOrigin(EvidenceOriginKind origin) =>
        origin == EvidenceOriginKind.Message ? DocumentAnalysisClassification.Declared : DocumentAnalysisClassification.Documented;

    /// <summary>
    /// Attesa INDIPENDENTE dalla produzione, scritta per colonna del piano §4 (condizioni nell'ordine): Gap; <c>ToClarify</c>; <c>Hypothesized</c> o Assumption; Alternative; <c>Inferred</c>; infine la sezione del tipo.
    /// Un valore di enum non previsto da qui fa fallire il test: aggiungere un valore alle enum senza una regola non passa inosservato.
    /// </summary>
    private static (AiSection Section, DocumentAnalysisClassification Classification) Expected(EvidenceKind kind, EvidenceOriginKind origin, EvidenceCertainty certainty)
    {
        AiSection OfKind() => kind switch
        {
            EvidenceKind.Goal => AiSection.Goals,
            EvidenceKind.Context => AiSection.ScopeOverview,
            EvidenceKind.Requirement => AiSection.Requirements,
            EvidenceKind.Action => AiSection.Flows,
            EvidenceKind.Constraint => AiSection.Constraints,
            EvidenceKind.Exclusion => AiSection.ScopeExcluded,
            EvidenceKind.Dependency => AiSection.Dependencies,
            EvidenceKind.Decision => AiSection.Requirements,
            EvidenceKind.Other => AiSection.ScopeOverview,
            _ => throw new InvalidOperationException($"Tipo {kind} senza attesa nel test: serve una regola in leaf-projection-1 (nuova versione) e una riga qui."),
        };

        if (kind == EvidenceKind.Gap)
        {
            return (AiSection.OpenQuestions, ByOrigin(origin));
        }

        return certainty switch
        {
            EvidenceCertainty.ToClarify => (AiSection.OpenQuestions, ByOrigin(origin)),
            EvidenceCertainty.Hypothesized => (AiSection.Assumptions, DocumentAnalysisClassification.Assumed),
            EvidenceCertainty.Inferred => kind switch
            {
                EvidenceKind.Assumption => (AiSection.Assumptions, DocumentAnalysisClassification.Assumed),
                EvidenceKind.Alternative => (AiSection.OpenQuestions, ByOrigin(origin)),
                _ => (OfKind(), DocumentAnalysisClassification.Inferred),
            },
            EvidenceCertainty.Declared => kind switch
            {
                EvidenceKind.Assumption => (AiSection.Assumptions, DocumentAnalysisClassification.Assumed),
                EvidenceKind.Alternative => (AiSection.OpenQuestions, ByOrigin(origin)),
                _ => (OfKind(), ByOrigin(origin)),
            },
            _ => throw new InvalidOperationException($"Certezza {certainty} senza attesa nel test."),
        };
    }

    // ---------------------------------------------------------------- tabella esaustiva

    [Fact]
    public void The_enums_are_exactly_the_twelve_kinds_the_two_origins_and_the_four_certainties_the_table_covers()
    {
        // Se una enum cresce, la tabella esaustiva sotto non è più esaustiva: il test deve fallire e chiedere una regola (nuova versione di leaf-projection).
        Assert.Equal(12, Kinds.Length);
        Assert.Equal(2, Origins.Length);
        Assert.Equal(4, Certainties.Length);
        Assert.Equal("leaf-projection-1", ReconcileLeafProjection.Version);
        Assert.Equal(ReconcileAliasContracts.StageItemsPolicy, ReconcileLeafProjection.Version);
    }

    [Fact]
    public void The_rule_matches_the_plan_table_for_every_kind_origin_and_certainty()
    {
        var checkedCombinations = 0;
        foreach (var kind in Kinds)
        {
            foreach (var origin in Origins)
            {
                foreach (var certainty in Certainties)
                {
                    var expected = Expected(kind, origin, certainty);
                    var actual = ReconcileLeafProjection.Rule(kind, origin, certainty);
                    Assert.True(expected == actual, $"{kind}/{origin}/{certainty}: attesa {expected}, ottenuta {actual}");
                    checkedCombinations++;
                }
            }
        }

        Assert.Equal(12 * 2 * 4, checkedCombinations);
    }

    [Fact]
    public void An_evidence_kind_the_table_does_not_know_is_an_error_never_a_silent_default()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileLeafProjection.Rule((EvidenceKind)999, EvidenceOriginKind.Document, EvidenceCertainty.Declared));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileLeafProjection.Rule(EvidenceKind.Requirement, (EvidenceOriginKind)999, EvidenceCertainty.Declared));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileLeafProjection.Rule(EvidenceKind.Requirement, EvidenceOriginKind.Document, (EvidenceCertainty)999));
    }

    // ---------------------------------------------------------------- distinzioni del prodotto preservate

    private static readonly AiSection[] Structural =
        [AiSection.Goals, AiSection.ScopeOverview, AiSection.ScopeIncluded, AiSection.ScopeExcluded, AiSection.Requirements, AiSection.Flows, AiSection.Constraints, AiSection.Dependencies];

    [Fact]
    public void No_alternative_no_hypothesis_no_to_clarify_and_no_gap_ever_lands_in_a_structural_section()
    {
        foreach (var kind in Kinds)
        {
            foreach (var origin in Origins)
            {
                foreach (var certainty in Certainties)
                {
                    var (section, classification) = ReconcileLeafProjection.Rule(kind, origin, certainty);
                    if (!Structural.Contains(section))
                    {
                        continue;
                    }

                    var label = $"{kind}/{origin}/{certainty}";
                    Assert.NotEqual(EvidenceKind.Alternative, kind);
                    Assert.NotEqual(EvidenceKind.Gap, kind);
                    Assert.NotEqual(EvidenceKind.Assumption, kind);
                    Assert.True(certainty is EvidenceCertainty.Declared or EvidenceCertainty.Inferred, label);
                    Assert.NotEqual(DocumentAnalysisClassification.Assumed, classification);
                }
            }
        }
    }

    [Fact]
    public void Each_category_of_the_product_keeps_its_own_section_and_classification_and_none_collapses_into_another()
    {
        const EvidenceOriginKind doc = EvidenceOriginKind.Document;
        const EvidenceOriginKind pm = EvidenceOriginKind.Message;
        var documentedFact = ReconcileLeafProjection.Rule(EvidenceKind.Requirement, doc, EvidenceCertainty.Declared);
        var pmRequirement = ReconcileLeafProjection.Rule(EvidenceKind.Requirement, pm, EvidenceCertainty.Declared);
        var decision = ReconcileLeafProjection.Rule(EvidenceKind.Decision, doc, EvidenceCertainty.Declared);
        var hypothesis = ReconcileLeafProjection.Rule(EvidenceKind.Requirement, doc, EvidenceCertainty.Hypothesized);
        var assumption = ReconcileLeafProjection.Rule(EvidenceKind.Assumption, doc, EvidenceCertainty.Declared);
        var alternative = ReconcileLeafProjection.Rule(EvidenceKind.Alternative, doc, EvidenceCertainty.Declared);
        var toClarify = ReconcileLeafProjection.Rule(EvidenceKind.Requirement, doc, EvidenceCertainty.ToClarify);
        var gapPm = ReconcileLeafProjection.Rule(EvidenceKind.Gap, pm, EvidenceCertainty.Declared);
        var gapDoc = ReconcileLeafProjection.Rule(EvidenceKind.Gap, doc, EvidenceCertainty.Declared);

        // Fatto documentato e requisito del PM: sezione dei requisiti, autorità diversa (Documented vs Declared).
        Assert.Equal((AiSection.Requirements, DocumentAnalysisClassification.Documented), documentedFact);
        Assert.Equal((AiSection.Requirements, DocumentAnalysisClassification.Declared), pmRequirement);

        // Decisione: nella sezione dei requisiti solo se dichiarata (la tabella non la rende mai una domanda né un'assunzione quando è dichiarata).
        Assert.Equal((AiSection.Requirements, DocumentAnalysisClassification.Documented), decision);
        Assert.Equal(AiSection.OpenQuestions, ReconcileLeafProjection.Rule(EvidenceKind.Decision, doc, EvidenceCertainty.ToClarify).Section);   // una decisione da chiarire non è una decisione acquisita
        Assert.Equal(AiSection.Assumptions, ReconcileLeafProjection.Rule(EvidenceKind.Decision, doc, EvidenceCertainty.Hypothesized).Section);   // né una decisione ipotizzata

        // Ipotesi e assunzione: sempre `assumptions` e `Assumed`, mai Documented né Declared né una sezione strutturale.
        Assert.Equal((AiSection.Assumptions, DocumentAnalysisClassification.Assumed), hypothesis);
        Assert.Equal((AiSection.Assumptions, DocumentAnalysisClassification.Assumed), assumption);
        Assert.NotEqual(documentedFact.Classification, hypothesis.Classification);

        // Alternativa, `ToClarify` e Gap: domande aperte, con la classificazione dell'origine (non un requisito acquisito).
        Assert.Equal((AiSection.OpenQuestions, DocumentAnalysisClassification.Documented), alternative);
        Assert.Equal((AiSection.OpenQuestions, DocumentAnalysisClassification.Documented), toClarify);
        Assert.Equal((AiSection.OpenQuestions, DocumentAnalysisClassification.Declared), gapPm);
        Assert.Equal((AiSection.OpenQuestions, DocumentAnalysisClassification.Documented), gapDoc);
        Assert.NotEqual(documentedFact.Section, toClarify.Section);

        // Il PM e il documento restano distinguibili anche nelle domande aperte.
        Assert.NotEqual(gapPm.Classification, gapDoc.Classification);

        // La certezza «dedotta» non si confonde con il fatto dichiarato: stessa sezione, classificazione diversa.
        var inferred = ReconcileLeafProjection.Rule(EvidenceKind.Requirement, doc, EvidenceCertainty.Inferred);
        Assert.Equal((AiSection.Requirements, DocumentAnalysisClassification.Inferred), inferred);
        Assert.NotEqual(documentedFact.Classification, inferred.Classification);
    }

    // ---------------------------------------------------------------- Project: testo letterale, chiavi, fonti, determinismo

    /// <summary>Evidenze del ledger per id (come le passa il piano al compositore).</summary>
    private sealed record LedgerView(IReadOnlyDictionary<Guid, DocumentAnalysisEvidence> Evidence);

    /// <summary>Foglia della vista A su tutte le evidenze del ledger, costruita come fa il piano (profilo v11): nessun motore, nessun database.</summary>
    private static (LedgerView Ledger, ReconcileBatchRequest Leaf) LeafA(DocumentAnalysisInput input) =>
        (new LedgerView(M2Ledger.ById(input)), ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafA, ContractProfile.V11, input, [.. Enumerable.Range(1, input.Evidence.Count)]).Request);

    [Fact]
    public void The_projected_text_is_the_statement_byte_for_byte_with_one_evidence_and_the_sources_of_the_entry_in_a_stable_order()
    {
        var input = M2Ledger.Input();
        var (ledger, leaf) = LeafA(input);

        var (proposals, sections) = ReconcileLeafProjection.Project(leaf, ledger.Evidence);

        Assert.Equal(leaf.Entries.Count, proposals.Count);
        for (var n = 1; n <= proposals.Count; n++)
        {
            var entry = leaf.Entries[n - 1];
            var statement = ledger.Evidence[entry.EvidenceId].Statement;
            var proposal = proposals[n - 1];
            Assert.Equal(statement, proposal.Text);                                              // letterale: nessuna normalizzazione né riscrittura
            Assert.Equal([entry.EvidenceId], proposal.EvidenceIds);
            Assert.Equal(ReconciliationIds.SortedIds(entry.SourceIds.Distinct()).Select(Guid.Parse), proposal.SourceIds);
            Assert.Equal(ReconcileAliasComposer.LocalKey(leaf, "p" + n), proposal.LocalKey);   // chiave locale canonica: stadio, vista, ordinale della foglia e posizione 1-based
            var evidence = ledger.Evidence[entry.EvidenceId];
            var (section, classification) = ReconcileLeafProjection.Rule(evidence.Kind, evidence.Origin, evidence.Certainty);
            Assert.Equal(section, sections[proposal.LocalKey]);
            Assert.Equal(classification, proposal.Classification);
        }

        Assert.Equal(proposals.Select(p => p.LocalKey).Order(StringComparer.Ordinal), sections.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(proposals.Count, proposals.Select(p => p.LocalKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_projection_is_deterministic_and_does_not_depend_on_the_evidence_dictionary_order()
    {
        var input = M2Ledger.Input();
        var (ledger, leaf) = LeafA(input);

        var first = ReconcileLeafProjection.Project(leaf, ledger.Evidence);
        var second = ReconcileLeafProjection.Project(leaf, ledger.Evidence);
        var reversed = ReconcileLeafProjection.Project(leaf, ledger.Evidence.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value));

        string Canon((IReadOnlyList<ItemProposal> Proposals, IReadOnlyDictionary<string, AiSection> Sections) r) =>
            ReconciliationCodec.Canonical(new { proposals = r.Proposals, sections = r.Sections.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList() });

        Assert.Equal(Canon(first), Canon(second));
        Assert.Equal(Canon(first), Canon(reversed));
    }

    [Fact]
    public void A_projection_of_an_evidence_missing_from_the_ledger_is_an_error_never_an_item_with_a_made_up_text()
    {
        var input = M2Ledger.Input();
        var (ledger, leaf) = LeafA(input);
        var partial = ledger.Evidence.Skip(1).ToDictionary(kv => kv.Key, kv => kv.Value);

        var error = Assert.Throws<InvalidOperationException>(() => ReconcileLeafProjection.Project(leaf, partial));

        Assert.Contains("evidenza", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
