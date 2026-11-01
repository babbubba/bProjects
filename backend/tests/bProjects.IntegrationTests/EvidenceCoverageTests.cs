using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T066 — verifica di copertura e provenienza <c>coverage-v1</c> (contracts/ai-workflow.md §10.8), parte deterministica senza database:
/// per ogni codice che il checker può emettere un caso positivo (codice e <see cref="FindingCategory"/> giusti) e uno negativo, la
/// separazione fra errori deterministici (ID, fonti, span, relazioni), rischi semantici (euristiche lessicali con Confidence) e
/// valutazioni PM, e la prova che il checker NON certifica la fedeltà semantica: un collegamento per ID non dimostra nulla, e una
/// riformulazione errata senza numeri, negazioni o strumenti è un falso negativo documentato, dichiarato dai Limits.
/// Gli esempi usano domini inventati: nessuna regola dipende dai casi N1–N5.
/// </summary>
public sealed class EvidenceCoverageTests
{
    /// <summary>Tutti i codici che <see cref="EvidenceCoverage"/> può emettere, con la categoria attesa. Un codice nuovo senza test fa fallire la guardia.</summary>
    private static readonly IReadOnlyDictionary<string, FindingCategory> Codes = new Dictionary<string, FindingCategory>
    {
        ["evidence_span_invalid"] = FindingCategory.DeterministicError,
        ["evidence_unrepresented"] = FindingCategory.DeterministicError,
        ["evidence_source_mismatch"] = FindingCategory.DeterministicError,
        ["open_conflict_as_decision"] = FindingCategory.DeterministicError,
        ["relation_stale"] = FindingCategory.DeterministicError,
        ["revision_source_not_cited"] = FindingCategory.DeterministicError,
        ["qualifier_changed"] = FindingCategory.SemanticRisk,
        ["negation_inverted"] = FindingCategory.SemanticRisk,
        ["instrument_detached"] = FindingCategory.SemanticRisk,
        ["revision_item_missing_in_document"] = FindingCategory.SemanticRisk,
        ["item_removed_by_pm"] = FindingCategory.PmAssessment,
        ["item_rewritten_by_pm"] = FindingCategory.PmAssessment,
        ["pm_decided_alternative"] = FindingCategory.PmAssessment,
    };

    // ---------------------------------------------------------------- guardia: nessun codice senza test

    [Fact]
    public void Every_code_emitted_by_the_checker_source_is_listed_here_with_its_category_so_a_new_code_needs_a_new_test()
    {
        var source = File.ReadAllText(Locate("src/bProjects.App/Evidence/EvidenceCoverage.cs"));
        var found = Regex.Matches(source, @"new\(FindingCategory\.(?<cat>\w+),\s*""(?<code>[a-z_]+)""");
        var fromSource = found.GroupBy(m => m.Groups["code"].Value).ToDictionary(g => g.Key, g => g.Select(m => Enum.Parse<FindingCategory>(m.Groups["cat"].Value)).Distinct().Single());

        // Ogni costruzione di un CoverageFinding è catturata dall'espressione: nessun codice può sfuggire alla scansione.
        Assert.Equal(Regex.Matches(source, @"new\(FindingCategory\.").Count, found.Count);
        Assert.DoesNotContain("new CoverageFinding(", source, StringComparison.Ordinal);
        Assert.Equal(Codes.OrderBy(c => c.Key).Select(c => $"{c.Key}:{c.Value}"), fromSource.OrderBy(c => c.Key).Select(c => $"{c.Key}:{c.Value}"));
    }

    [Fact]
    public void The_contract_table_lists_every_code_under_its_category()
    {
        var path = TryLocate("specs/001-project-draft/contracts/ai-workflow.md");
        Assert.SkipWhen(path is null, "contracts/ai-workflow.md non raggiungibile da questo albero di build.");
        var text = File.ReadAllText(path!);
        var section = text[text.IndexOf("### 10.8", StringComparison.Ordinal)..];
        var rows = new Dictionary<FindingCategory, string>
        {
            [FindingCategory.DeterministicError] = Row(section, "| Errore deterministico"),
            [FindingCategory.SemanticRisk] = Row(section, "| Rischio semantico stimato"),
            [FindingCategory.PmAssessment] = Row(section, "| Valutazione PM"),
        };
        foreach (var (code, category) in Codes)
        {
            Assert.Contains($"`{code}`", rows[category], StringComparison.Ordinal);
            foreach (var other in rows.Where(r => r.Key != category))
            {
                Assert.DoesNotContain($"`{code}`", other.Value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void One_scenario_emits_every_code_and_each_finding_respects_its_category()
    {
        var s = Build("Serve un report mensile. La cauzione è richiesta. Il prestito dura al massimo 10 giorni. Niente SMS. Il magazziniere registra i rientri con il lettore del tesserino. Sesto punto. Settimo punto. Ottavo punto.",
            new Spec("Serve un report mensile", ItemText: "Serve un report mensile", BadSpan: true),                                    // evidence_span_invalid
            new Spec("La cauzione è richiesta", ItemText: "Cauzione", Linked: false),                                                   // evidence_unrepresented
            new Spec("Sesto punto", ItemText: "Sesto punto", Cites: false),                                                             // evidence_source_mismatch
            new Spec("Settimo punto", ItemText: "Settimo punto", Pm: PmChange.Removed),                                                 // item_removed_by_pm
            new Spec("Ottavo punto", ItemText: "Ottavo punto", Pm: PmChange.Rewritten),                                                 // item_rewritten_by_pm
            new Spec("Il prestito dura", Kind: EvidenceKind.Alternative, ItemText: "Il prestito ha una durata", Pm: PmChange.DecidedByPm), // pm_decided_alternative
            new Spec("La cauzione", Kind: EvidenceKind.Alternative, ItemText: "Serve la cauzione"),                                     // open_conflict_as_decision
            new Spec("al massimo 10 giorni", ItemText: "Il prestito ha una durata massima",
                Qualifiers: [new(QualifierType.Maximum, "al massimo 10 giorni")]),                                                       // qualifier_changed
            new Spec("Niente SMS", Polarity: EvidencePolarity.Prohibited, ItemText: "Avvisi via SMS"),                                  // negation_inverted
            new Spec("registra i rientri con il lettore del tesserino", Kind: EvidenceKind.Action, ItemText: "Il magazziniere registra i rientri",
                Structure: new("Il magazziniere", "registra i rientri", null, null, null, "lettore del tesserino", null, null)));        // instrument_detached
        var stale = new StaleRelation(Guid.NewGuid(), s.View.Evidences[0].EvidenceId, Guid.NewGuid(), EvidenceRelationType.DependsOn, "bersaglio non risolvibile");
        s = s with { View = s.View with { StaleRelations = [stale] } };                                                                 // relation_stale

        var findings = Check(s, $"# Documento\n\nTesto non correlato. [fonte:{Guid.NewGuid()}]");                                      // revision_source_not_cited, revision_item_missing_in_document

        Assert.Equal(Codes.Keys.Order(), findings.Select(f => f.Code).Distinct().Order());
        AssertCategoryDiscipline(findings);
    }

    // ---------------------------------------------------------------- evidence_span_invalid (deterministico)

    [Fact]
    public void Evidence_span_invalid_is_emitted_when_the_quote_does_not_match_the_original_message()
    {
        var shifted = Check(Build("Serve un report mensile delle consegne.", new Spec("Serve un report mensile", ItemText: "Serve un report mensile", BadSpan: true)));
        var finding = Assert.Single(shifted, f => f.Code == "evidence_span_invalid");
        Assert.Equal(FindingCategory.DeterministicError, finding.Category);
        Assert.Null(finding.Confidence);
        Assert.Equal("E1", finding.Alias);

        var missingMessage = Build("Serve un report mensile delle consegne.", new Spec("Serve un report mensile", ItemText: "Serve un report mensile"));
        missingMessage.Bodies.Remove(missingMessage.Message);
        Assert.Contains(Check(missingMessage), f => f is { Code: "evidence_span_invalid", Category: FindingCategory.DeterministicError });
    }

    [Fact]
    public void Evidence_span_invalid_is_not_emitted_for_an_exact_quote()
    {
        var findings = Check(Build("Serve un report mensile delle consegne.", new Spec("Serve un report mensile", ItemText: "Serve un report mensile")));
        Assert.DoesNotContain(findings, f => f.Code == "evidence_span_invalid");
        Assert.Empty(findings);
    }

    // ---------------------------------------------------------------- evidence_unrepresented (deterministico)

    [Fact]
    public void Evidence_unrepresented_is_emitted_when_no_item_is_linked_to_the_evidence()
    {
        var findings = Check(Build("Serve un report mensile.", new Spec("Serve un report mensile", ItemText: "Report mensile", Linked: false)));
        var finding = Assert.Single(findings);
        Assert.Equal(("evidence_unrepresented", FindingCategory.DeterministicError), (finding.Code, finding.Category));
        Assert.Equal("E1", finding.Alias);
        Assert.Null(finding.ItemId);
    }

    [Fact]
    public void Evidence_unrepresented_is_not_emitted_when_an_item_is_linked()
    {
        Assert.DoesNotContain(Check(Build("Serve un report mensile.", new Spec("Serve un report mensile", ItemText: "Report mensile"))), f => f.Code == "evidence_unrepresented");
    }

    // ---------------------------------------------------------------- evidence_source_mismatch (deterministico)

    [Fact]
    public void Evidence_source_mismatch_is_emitted_when_the_linked_item_does_not_cite_the_evidence_message()
    {
        var s = Build("Serve un report mensile.", new Spec("Serve un report mensile", ItemText: "Serve un report mensile", Cites: false));
        var finding = Assert.Single(Check(s));
        Assert.Equal(("evidence_source_mismatch", FindingCategory.DeterministicError), (finding.Code, finding.Category));
        Assert.Equal(s.ItemIds[0], finding.ItemId);
        Assert.Equal("requirements", finding.Section);
    }

    [Fact]
    public void Evidence_source_mismatch_is_not_emitted_when_the_item_cites_the_message()
    {
        Assert.DoesNotContain(Check(Build("Serve un report mensile.", new Spec("Serve un report mensile", ItemText: "Serve un report mensile"))), f => f.Code == "evidence_source_mismatch");
    }

    // ---------------------------------------------------------------- item_removed_by_pm (valutazione PM)

    [Fact]
    public void Item_removed_by_pm_is_a_pm_assessment_when_the_target_revision_drops_the_item()
    {
        var findings = Check(Build("Serve un report mensile.", new Spec("Serve un report mensile", ItemText: "Serve un report mensile", Pm: PmChange.Removed)));
        var finding = Assert.Single(findings);
        Assert.Equal(("item_removed_by_pm", FindingCategory.PmAssessment), (finding.Code, finding.Category));
        Assert.Null(finding.Confidence);
    }

    [Fact]
    public void Item_removed_by_pm_is_not_emitted_when_the_item_is_still_in_the_target_revision()
    {
        Assert.DoesNotContain(Check(Build("Serve un report mensile.", new Spec("Serve un report mensile", ItemText: "Serve un report mensile"))), f => f.Code == "item_removed_by_pm");
    }

    // ---------------------------------------------------------------- item_rewritten_by_pm (valutazione PM)

    [Fact]
    public void Item_rewritten_by_pm_is_a_pm_assessment_that_shows_before_and_after()
    {
        var findings = Check(Build("Serve un report mensile.", new Spec("Serve un report mensile", ItemText: "Serve un report mensile", Pm: PmChange.Rewritten)));
        var finding = Assert.Single(findings);
        Assert.Equal(("item_rewritten_by_pm", FindingCategory.PmAssessment), (finding.Code, finding.Category));
        Assert.Contains("prima:", finding.Details, StringComparison.Ordinal);
        Assert.Contains("dopo:", finding.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Item_rewritten_by_pm_is_not_emitted_when_the_text_is_unchanged()
    {
        Assert.DoesNotContain(Check(Build("Serve un report mensile.", new Spec("Serve un report mensile", ItemText: "Serve un report mensile"))), f => f.Code == "item_rewritten_by_pm");
    }

    // ---------------------------------------------------------------- pm_decided_alternative (valutazione PM)

    [Fact]
    public void Pm_decided_alternative_is_a_pm_assessment_not_an_error_when_the_pm_carried_an_open_alternative_into_a_structural_section()
    {
        var findings = Check(Build("Il consiglio vuole la cauzione.", new Spec("Il consiglio vuole la cauzione", Kind: EvidenceKind.Alternative, ItemText: "Il prestito richiede una cauzione", Pm: PmChange.DecidedByPm)));
        var finding = Assert.Single(findings, f => f.Code == "pm_decided_alternative");
        Assert.Equal(FindingCategory.PmAssessment, finding.Category);
        Assert.DoesNotContain(findings, f => f.Code == "open_conflict_as_decision");
        Assert.DoesNotContain(findings, f => f.Category == FindingCategory.DeterministicError);
    }

    [Fact]
    public void Pm_decided_alternative_is_not_emitted_outside_structural_sections_or_when_a_declared_decision_settles_the_alternative()
    {
        var assumption = Check(Build("Il consiglio vuole la cauzione.", new Spec("Il consiglio vuole la cauzione", Kind: EvidenceKind.Alternative,
            ItemText: "Il prestito richiede una cauzione", Section: "assumptions", Pm: PmChange.DecidedByPm)));
        Assert.DoesNotContain(assumption, f => f.Code is "pm_decided_alternative" or "open_conflict_as_decision");

        var settled = Build("Il consiglio vuole la cauzione. La cauzione è confermata.",
            new Spec("Il consiglio vuole la cauzione", Kind: EvidenceKind.Alternative, ItemText: "Il prestito richiede una cauzione", Pm: PmChange.DecidedByPm),
            new Spec("La cauzione è confermata", Kind: EvidenceKind.Decision, ItemText: "La cauzione è confermata"));
        settled = settled with { View = settled.View with { Relations = [new("E2", "E1", EvidenceRelationType.Corrects, EvidenceRelationBasis.Declared, null, false, false)] } };
        Assert.DoesNotContain(Check(settled), f => f.Code is "pm_decided_alternative" or "open_conflict_as_decision");
    }

    // ---------------------------------------------------------------- open_conflict_as_decision (deterministico)

    [Fact]
    public void Open_conflict_as_decision_is_a_deterministic_error_for_an_open_alternative_presented_as_a_choice()
    {
        var byKind = Check(Build("Il consiglio vuole la cauzione.", new Spec("Il consiglio vuole la cauzione", Kind: EvidenceKind.Alternative, ItemText: "Il prestito richiede una cauzione")));
        var finding = Assert.Single(byKind, f => f.Code == "open_conflict_as_decision");
        Assert.Equal(FindingCategory.DeterministicError, finding.Category);
        Assert.DoesNotContain(byKind, f => f.Code == "pm_decided_alternative");

        // Anche un'evidenza non Alternative è «non decisa» se la selezione la lega con una relazione aperta.
        var byRelation = Build("Serve la cauzione. Non serve la cauzione.",
            new Spec("Serve la cauzione", ItemText: "Serve la cauzione"), new Spec("Non serve la cauzione", ItemText: "Non serve la cauzione", Section: "assumptions"));
        byRelation = byRelation with { View = byRelation.View with { Relations = [new("E1", "E2", EvidenceRelationType.Contradicts, EvidenceRelationBasis.Inferred, null, true, false)] } };
        Assert.Contains(Check(byRelation), f => f is { Code: "open_conflict_as_decision", Alias: "E1", Category: FindingCategory.DeterministicError });
    }

    [Fact]
    public void Open_conflict_as_decision_is_not_emitted_for_alternatives_kept_in_conflicts_or_for_decided_evidence()
    {
        var inConflicts = Check(Build("Il consiglio vuole la cauzione.", new Spec("Il consiglio vuole la cauzione", Kind: EvidenceKind.Alternative,
            ItemText: "Cauzione sì", Section: "conflicts")));
        Assert.DoesNotContain(inConflicts, f => f.Code is "open_conflict_as_decision" or "pm_decided_alternative");

        var plain = Check(Build("Serve un report mensile.", new Spec("Serve un report mensile", ItemText: "Serve un report mensile")));
        Assert.DoesNotContain(plain, f => f.Code == "open_conflict_as_decision");
    }

    // ---------------------------------------------------------------- relation_stale (deterministico)

    [Fact]
    public void Relation_stale_is_a_deterministic_error_for_every_relation_the_frozen_view_could_not_resolve()
    {
        var s = Build("Serve un report mensile.", new Spec("Serve un report mensile", ItemText: "Serve un report mensile"));
        var stale = new StaleRelation(Guid.NewGuid(), s.View.Evidences[0].EvidenceId, Guid.NewGuid(), EvidenceRelationType.Corrects, "bersaglio senza abbinamento");
        var finding = Assert.Single(Check(s with { View = s.View with { StaleRelations = [stale] } }));
        Assert.Equal(("relation_stale", FindingCategory.DeterministicError), (finding.Code, finding.Category));
        Assert.Equal(stale.RelationId.ToString(), finding.Details);
        Assert.Equal(stale.FromEvidenceId, finding.EvidenceId);

        // Stessa condizione prodotta dalla vista reale: bersaglio in un messaggio che la selezione non contiene.
        var (message, other, oldVersion) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var a1 = Snapshot(message, 1, 0, 40);
        var old = Snapshot(other, 1, 0, 40, oldVersion);
        var view = EvidenceSelectionView.Build([new(message, 1, a1.VersionId, 1, 1, null)], [a1],
            [new(Guid.NewGuid(), a1.VersionId, a1.Id, old.Id, oldVersion, other, EvidenceRelationType.Corrects, EvidenceRelationBasis.Declared, null)],
            new Dictionary<Guid, IReadOnlyList<EvidenceSnapshot>> { [oldVersion] = [old] });
        Assert.Single(view.StaleRelations);
        Assert.Contains(Check(s with { View = view }), f => f.Code == "relation_stale");
    }

    [Fact]
    public void Relation_stale_is_not_emitted_for_resolved_or_remapped_relations()
    {
        var s = Build("Serve un report mensile.", new Spec("Serve un report mensile", ItemText: "Serve un report mensile"));
        Assert.DoesNotContain(Check(s), f => f.Code == "relation_stale");

        // Il bersaglio sta in una versione non selezionata ma l'abbinamento è unico: rimappata, non stale.
        var (message, oldVersion, newVersion) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var from = Snapshot(Guid.NewGuid(), 2, 0, 40);
        var old = Snapshot(message, 1, 0, 40, oldVersion);
        var current = Snapshot(message, 1, 0, 40, newVersion);
        var view = EvidenceSelectionView.Build([new(message, 1, newVersion, 2, 1, null), new(from.MessageId, 2, from.VersionId, 1, 1, null)], [current, from],
            [new(Guid.NewGuid(), from.VersionId, from.Id, old.Id, oldVersion, message, EvidenceRelationType.Corrects, EvidenceRelationBasis.Declared, null)],
            new Dictionary<Guid, IReadOnlyList<EvidenceSnapshot>> { [oldVersion] = [old] });
        Assert.Empty(view.StaleRelations);
        Assert.True(Assert.Single(view.Relations).Remapped);
        Assert.DoesNotContain(Check(s with { View = view }), f => f.Code == "relation_stale");
    }

    // ---------------------------------------------------------------- qualifier_changed (rischio semantico)

    [Fact]
    public void Qualifier_changed_is_a_semantic_risk_with_declared_confidence_when_a_number_is_lost()
    {
        var findings = Check(Build("Il prestito dura al massimo 10 giorni.", new Spec("al massimo 10 giorni", Kind: EvidenceKind.Constraint,
            ItemText: "Il prestito ha una durata massima", Qualifiers: [new(QualifierType.Maximum, "al massimo 10 giorni")])));
        var finding = Assert.Single(findings);
        Assert.Equal(("qualifier_changed", FindingCategory.SemanticRisk), (finding.Code, finding.Category));
        Assert.Equal("medium", finding.Confidence);
    }

    [Fact]
    public void Qualifier_changed_is_not_emitted_when_the_number_survives_in_digits_or_words_or_the_qualifier_has_no_number()
    {
        Spec WithItem(string text, string qualifier) => new("dura", Kind: EvidenceKind.Constraint, ItemText: text, Qualifiers: [new(QualifierType.Maximum, qualifier)]);

        Assert.DoesNotContain(Check(Build("Il prestito dura al massimo 10 giorni.", WithItem("Il prestito dura al massimo 10 giorni", "al massimo 10 giorni"))), f => f.Code == "qualifier_changed");
        Assert.DoesNotContain(Check(Build("Il prestito dura al massimo 10 giorni.", WithItem("Il prestito dura al massimo dieci giorni", "al massimo 10 giorni"))), f => f.Code == "qualifier_changed");
        Assert.DoesNotContain(Check(Build("Il prestito dura al massimo dieci giorni.", WithItem("Il prestito dura al massimo 10 giorni", "al massimo dieci giorni"))), f => f.Code == "qualifier_changed");

        // Limite documentato: un qualificatore senza numeri non è verificato dal checker.
        Assert.DoesNotContain(Check(Build("Il prestito dura in orario d'ufficio.", WithItem("Il prestito dura sempre", "in orario d'ufficio"))), f => f.Code == "qualifier_changed");
    }

    // ---------------------------------------------------------------- negation_inverted (rischio semantico)

    [Theory]
    [InlineData(EvidencePolarity.Negated)]
    [InlineData(EvidencePolarity.Excluded)]
    [InlineData(EvidencePolarity.Prohibited)]
    public void Negation_inverted_is_a_semantic_risk_for_a_negative_evidence_linked_to_text_without_negation(EvidencePolarity polarity)
    {
        var finding = Assert.Single(Check(Build("Niente SMS.", new Spec("Niente SMS", Polarity: polarity, ItemText: "Avvisi via SMS"))));
        Assert.Equal(("negation_inverted", FindingCategory.SemanticRisk), (finding.Code, finding.Category));
        Assert.Equal("medium", finding.Confidence);
    }

    [Fact]
    public void Negation_inverted_is_a_low_confidence_risk_for_an_affirmation_represented_only_among_the_exclusions()
    {
        var finding = Assert.Single(Check(Build("Serve un report mensile.", new Spec("Serve un report mensile", ItemText: "Report mensile", Section: "contextAndScope.excluded"))));
        Assert.Equal(("negation_inverted", FindingCategory.SemanticRisk), (finding.Code, finding.Category));
        Assert.Equal("low", finding.Confidence);
    }

    [Fact]
    public void Negation_inverted_is_not_emitted_when_the_negation_or_the_exclusion_is_preserved()
    {
        Assert.DoesNotContain(Check(Build("Niente SMS.", new Spec("Niente SMS", Polarity: EvidencePolarity.Prohibited, ItemText: "Gli avvisi non usano SMS"))), f => f.Code == "negation_inverted");
        Assert.DoesNotContain(Check(Build("Niente SMS.", new Spec("Niente SMS", Polarity: EvidencePolarity.Prohibited, ItemText: "Avvisi via SMS", Section: "contextAndScope.excluded"))),
            f => f.Code == "negation_inverted");
        Assert.DoesNotContain(Check(Build("Serve un report mensile.", new Spec("Serve un report mensile", ItemText: "Report mensile"))), f => f.Code == "negation_inverted");
    }

    // ---------------------------------------------------------------- instrument_detached (rischio semantico)

    private static readonly EvidenceStructure Scanner = new("Il magazziniere", "registra i rientri", null, null, null, "lettore del tesserino", null, null);

    [Fact]
    public void Instrument_detached_is_a_semantic_risk_when_tool_and_action_never_share_a_linked_item()
    {
        var finding = Assert.Single(Check(Build("Il magazziniere registra i rientri con il lettore del tesserino.",
            new Spec("registra i rientri con il lettore del tesserino", Kind: EvidenceKind.Action, ItemText: "Il magazziniere registra i rientri", Structure: Scanner))));
        Assert.Equal(("instrument_detached", FindingCategory.SemanticRisk), (finding.Code, finding.Category));
        Assert.Equal("medium", finding.Confidence);
    }

    [Fact]
    public void Instrument_detached_is_not_emitted_when_tool_and_action_appear_together_or_there_is_no_instrument()
    {
        var together = Build("Il magazziniere registra i rientri con il lettore del tesserino.",
            new Spec("registra i rientri con il lettore del tesserino", Kind: EvidenceKind.Action, ItemText: "Il magazziniere registra i rientri con il lettore del tesserino", Structure: Scanner));
        Assert.DoesNotContain(Check(together), f => f.Code == "instrument_detached");

        var noInstrument = Build("Il magazziniere registra i rientri.",
            new Spec("registra i rientri", Kind: EvidenceKind.Action, ItemText: "Altro", Structure: Scanner with { Instrument = null }));
        Assert.DoesNotContain(Check(noInstrument), f => f.Code == "instrument_detached");
    }

    // ---------------------------------------------------------------- revision_source_not_cited (deterministico)

    [Fact]
    public void Revision_source_not_cited_is_a_deterministic_error_when_the_draft_omits_the_source_marker()
    {
        var s = Build("Serve un report mensile delle consegne.", new Spec("Serve un report mensile delle consegne", ItemText: "Serve un report mensile delle consegne"));
        var findings = Check(s, $"# Documento\n\nServe un report mensile delle consegne. [fonte:{Guid.NewGuid()}]");
        var finding = Assert.Single(findings);
        Assert.Equal(("revision_source_not_cited", FindingCategory.DeterministicError), (finding.Code, finding.Category));
        Assert.Equal(s.ItemIds[0], finding.ItemId);
    }

    [Fact]
    public void Revision_source_not_cited_is_not_emitted_when_cited_without_a_draft_or_for_items_without_sources()
    {
        var s = Build("Serve un report mensile delle consegne.", new Spec("Serve un report mensile delle consegne", ItemText: "Serve un report mensile delle consegne"));
        Assert.Empty(Check(s, $"# Documento\n\nServe un report mensile delle consegne. [fonte:{s.Message}]"));
        Assert.Empty(Check(s, $"# Documento\n\nServe un report mensile delle consegne. [fonte:{s.Message.ToString().ToUpperInvariant()}]"));   // maiuscole ammesse
        Assert.Empty(Check(s));                                                                                                                     // senza bozza nessun controllo documentale

        var withoutRefs = s with { Target = s.Target with { Requirements = [.. s.Target.Requirements.Select(i => i with { EvidenceRefs = [] })] } };
        Assert.DoesNotContain(Check(withoutRefs, "# Documento\n\nServe un report mensile delle consegne."), f => f.Code == "revision_source_not_cited");
    }

    // ---------------------------------------------------------------- revision_item_missing_in_document (rischio semantico)

    [Fact]
    public void Revision_item_missing_in_document_is_a_semantic_risk_whose_confidence_follows_the_missing_ratio()
    {
        var s = Build("Report mensile consegne.", new Spec("Report mensile consegne", ItemText: "Report mensile consegne"));
        var absent = Assert.Single(Check(s, $"# Documento\n\nSi parla d'altro. [fonte:{s.Message}]"));
        Assert.Equal(("revision_item_missing_in_document", FindingCategory.SemanticRisk), (absent.Code, absent.Category));
        Assert.Equal("medium", absent.Confidence);                                                                // 0 parole su 3

        var partial = Assert.Single(Check(s, $"# Documento\n\nUn report qualunque. [fonte:{s.Message}]"));
        Assert.Equal("low", partial.Confidence);                                                                  // 1 parola su 3: 0,33 ≥ 0,3
    }

    [Fact]
    public void Revision_item_missing_in_document_is_not_emitted_when_words_are_present_or_the_item_is_too_short_to_judge()
    {
        var s = Build("Report mensile consegne.", new Spec("Report mensile consegne", ItemText: "Report mensile consegne"));
        Assert.Empty(Check(s, $"# Documento\n\nIl report con cadenza mensile delle consegne. [fonte:{s.Message}]"));

        // Meno di tre parole significative: il checker non giudica (limite documentato, non una verifica positiva).
        var shortItem = Build("Report mensile.", new Spec("Report mensile", ItemText: "Report mensile"));
        Assert.Empty(Check(shortItem, $"# Documento\n\nTutt'altro argomento. [fonte:{shortItem.Message}]"));
    }

    // ---------------------------------------------------------------- il checker non certifica la fedeltà semantica

    [Fact]
    public void A_link_by_id_with_the_right_message_never_proves_fidelity_even_when_the_text_inverts_or_ignores_the_evidence()
    {
        // Stessa mappa, stesso messaggio citato, ma testi senza rapporto o di senso opposto che condividono le parole dell'evidenza.
        var inverted = Build("Il portale deve inviare un promemoria via email ai clienti.",
            new Spec("Il portale deve inviare un promemoria via email ai clienti", ItemText: "Il portale deve evitare ogni promemoria via email ai clienti"));
        var unrelated = Build("Il portale deve inviare un promemoria via email ai clienti.",
            new Spec("Il portale deve inviare un promemoria via email ai clienti", ItemText: "Archiviazione dei contratti fornitori"));

        foreach (var s in new[] { inverted, unrelated })
        {
            var findings = Check(s);
            Assert.DoesNotContain(findings, f => f.Category == FindingCategory.DeterministicError);
            Assert.Empty(findings);
            AssertNoCertificationClaim(findings);
        }

        AssertLimitsDisclaimFidelity();
    }

    [Fact]
    public void A_wrong_paraphrase_without_numbers_negations_or_instruments_is_a_documented_false_negative()
    {
        // Soggetto e azione sono scambiati di senso («approva» → «rifiuta», magazziniere → direttore): il checker non può saperlo.
        var s = Build("Il magazziniere approva le richieste di acquisto.",
            new Spec("Il magazziniere approva le richieste di acquisto", ItemText: "Il direttore rifiuta le richieste di acquisto",
                Structure: new("Il magazziniere", "approva", "richieste di acquisto", null, null, null, null, null)));
        var findings = Check(s);

        Assert.Empty(findings);                                                    // falso negativo noto: nessuna segnalazione
        AssertNoCertificationClaim(findings);
        AssertLimitsDisclaimFidelity();
        Assert.Contains(EvidenceCoverage.Limits, l => l.Contains("sinonimi", StringComparison.Ordinal) && l.Contains("riformulazioni", StringComparison.Ordinal));
        Assert.Contains(EvidenceCoverage.Limits, l => l.Contains("revisione PM", StringComparison.Ordinal));
    }

    [Fact]
    public void A_report_without_findings_still_carries_the_limits_and_the_checker_never_returns_a_verified_flag_or_score()
    {
        var content = new CoverageReportContent(EvidenceCoverage.CheckerVersion, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "hash", [], EvidenceCoverage.Limits);
        using var document = JsonDocument.Parse(EvidenceJson.Serialize(content));
        Assert.Equal(
            ["aiRevisionId", "analysisId", "checkerVersion", "draftId", "findings", "limits", "revisionId", "viewHash"],
            document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(EvidenceCoverage.Limits.Count, document.RootElement.GetProperty("limits").GetArrayLength());
        Assert.Equal("coverage-v1", document.RootElement.GetProperty("checkerVersion").GetString());
    }

    [Fact]
    public void The_check_is_deterministic_and_does_not_modify_its_inputs()
    {
        var s = Build("Il prestito dura al massimo 10 giorni. Niente SMS.",
            new Spec("al massimo 10 giorni", Kind: EvidenceKind.Constraint, ItemText: "Durata massima", Qualifiers: [new(QualifierType.Maximum, "al massimo 10 giorni")]),
            new Spec("Niente SMS", Polarity: EvidencePolarity.Prohibited, ItemText: "Avvisi via SMS", Pm: PmChange.Rewritten));
        var (view, ai, target) = (s.View.Serialize(), AnalysisContentJson.Serialize(s.Ai), AnalysisContentJson.Serialize(s.Target));

        var first = Check(s, "# Documento");
        var second = Check(s, "# Documento");

        Assert.Equal(EvidenceJson.Serialize(first), EvidenceJson.Serialize(second));
        Assert.Equal(view, s.View.Serialize());
        Assert.Equal(ai, AnalysisContentJson.Serialize(s.Ai));
        Assert.Equal(target, AnalysisContentJson.Serialize(s.Target));
    }

    // ---------------------------------------------------------------- hash degli input

    [Fact]
    public void The_input_hash_changes_with_every_exact_input_and_ignores_the_order_of_the_links()
    {
        var (item1, item2, evidence1, evidence2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (ai, revision, draft) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var links = new List<(Guid, Guid)> { (item1, evidence1), (item2, evidence2) };
        string Hash(string view = "vista", IReadOnlyCollection<(Guid, Guid)>? l = null, Guid? aiId = null, Guid? rev = null, Guid? d = null, string json = """{"a":1}""", string? md = "# Bozza") =>
            EvidenceCoverage.InputHash(view, l ?? links, aiId ?? ai, rev ?? revision, d ?? draft, json, md);

        var baseline = Hash();
        Assert.Equal(baseline, Hash());
        Assert.Equal(baseline, Hash(l: [links[1], links[0]]));
        Assert.Matches("^[0-9a-f]{64}$", baseline);

        var variations = new[]
        {
            Hash(view: "altra vista"), Hash(l: [links[0]]), Hash(aiId: Guid.NewGuid()), Hash(rev: Guid.NewGuid()), Hash(d: Guid.NewGuid()),
            Hash(json: """{"a":2}"""), Hash(md: "# Bozza modificata"), Hash(md: null),
            EvidenceCoverage.InputHash("vista", links, ai, revision, null, """{"a":1}""", "# Bozza"),
        };
        Assert.All(variations, h => Assert.NotEqual(baseline, h));
        Assert.Equal(variations.Length, variations.Distinct().Count());
    }

    // ---------------------------------------------------------------- supporto

    private static void AssertCategoryDiscipline(IReadOnlyList<CoverageFinding> findings)
    {
        Assert.NotEmpty(findings);
        foreach (var f in findings)
        {
            Assert.Equal(Codes[f.Code], f.Category);
            if (f.Category == FindingCategory.SemanticRisk)
            {
                Assert.Contains(f.Confidence, new[] { "low", "medium" });      // la stima dichiara sempre la propria incertezza
            }
            else
            {
                Assert.Null(f.Confidence);                                      // errori deterministici e valutazioni PM non sono stime
            }
        }
    }

    private static void AssertNoCertificationClaim(IReadOnlyList<CoverageFinding> findings)
    {
        Assert.DoesNotContain(findings, f => Regex.IsMatch(f.Code + f.Message, "verified|verificat|certific|fedel|confermat", RegexOptions.IgnoreCase));
    }

    private static void AssertLimitsDisclaimFidelity()
    {
        Assert.Contains(EvidenceCoverage.Limits, l => l.Contains("un collegamento non dimostra", StringComparison.Ordinal) && l.Contains("fedele", StringComparison.Ordinal));
        Assert.Contains(EvidenceCoverage.Limits, l => l.Contains("euristiche lessicali", StringComparison.Ordinal));
        Assert.Contains(EvidenceCoverage.Limits, l => l.Contains("Nessun modello linguistico", StringComparison.Ordinal) && l.Contains("revisione PM", StringComparison.Ordinal));
    }

    private static string Row(string section, string prefix) =>
        section.Split('\n').Single(l => l.StartsWith(prefix, StringComparison.Ordinal));

    private static string Locate(string relative) =>
        TryLocate(relative) ?? throw new FileNotFoundException($"{relative} non trovato risalendo da {AppContext.BaseDirectory}.");

    private static string? TryLocate(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IReadOnlyList<CoverageFinding> Check(Scenario s, string? draft = null) =>
        EvidenceCoverage.Check(s.View, s.Links, s.Ai, s.Target, draft, s.Bodies);

    private static EvidenceSnapshot Snapshot(Guid message, long sequence, int start, int end, Guid? version = null)
    {
        var quote = new string('x', end - start);
        return new(Guid.NewGuid(), version ?? Guid.NewGuid(), message, sequence, 1, "e1", EvidenceKind.Requirement, EvidencePolarity.Affirmed, EvidenceCertainty.Declared, quote,
            new EvidenceContent(null, new EvidenceStructure(null, null, null, null, null, null, null, null), [],
                [new EvidenceSpan(message, sequence, EvidenceSourceRole.Primary, start, end, quote)]));
    }

    private enum PmChange { None, Removed, Rewritten, DecidedByPm }

    private sealed record Spec(
        string Quote, EvidenceKind Kind = EvidenceKind.Requirement, EvidencePolarity Polarity = EvidencePolarity.Affirmed, string ItemText = "",
        string Section = "requirements", EvidenceStructure? Structure = null, IReadOnlyList<EvidenceQualifier>? Qualifiers = null,
        bool Linked = true, bool Cites = true, bool BadSpan = false, PmChange Pm = PmChange.None);

    private sealed record Scenario(
        EvidenceSelectionView View, List<(Guid ItemId, Guid EvidenceId)> Links, AnalysisContent Ai, AnalysisContent Target,
        Dictionary<Guid, string> Bodies, Guid Message, IReadOnlyList<Guid> ItemIds);

    /// <summary>Un messaggio PM, una evidenza per <see cref="Spec"/>, un elemento per evidenza nella revisione AI e (secondo <see cref="PmChange"/>) in quella target.</summary>
    private static Scenario Build(string body, params Spec[] specs)
    {
        var (message, pmSource) = (Guid.NewGuid(), Guid.NewGuid());
        var evidences = new List<ViewEvidence>();
        var links = new List<(Guid, Guid)>();
        var ai = new Dictionary<string, List<AnalysisItem>>();
        var target = new Dictionary<string, List<AnalysisItem>>();
        var itemIds = new List<Guid>();
        for (var i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            var start = body.IndexOf(spec.Quote, StringComparison.Ordinal);
            Assert.True(start >= 0, $"la citazione «{spec.Quote}» non è nel corpo");
            var (from, to) = spec.BadSpan ? (start + 1, start + 1 + spec.Quote.Length) : (start, start + spec.Quote.Length);
            if (spec.BadSpan)
            {
                Assert.NotEqual(spec.Quote, body.Length >= to ? body[from..to] : string.Empty);
            }

            var evidence = new ViewEvidence($"E{i + 1}", Guid.NewGuid(), message, 1, i + 1, spec.Kind, spec.Polarity, EvidenceCertainty.Declared, spec.Quote,
                new EvidenceContent(null, spec.Structure ?? new EvidenceStructure(null, null, null, null, null, null, null, null), spec.Qualifiers ?? [],
                    [new EvidenceSpan(message, 1, EvidenceSourceRole.Primary, from, to, spec.Quote)]));
            evidences.Add(evidence);

            var item = new AnalysisItem { Id = Guid.NewGuid(), Text = spec.ItemText, Classification = AnalysisClassification.Declared, EvidenceRefs = spec.Cites ? [message] : [Guid.NewGuid()] };
            itemIds.Add(item.Id);
            if (spec.Linked)
            {
                links.Add((item.Id, evidence.EvidenceId));
            }

            Add(ai, spec.Section, item);
            switch (spec.Pm)
            {
                case PmChange.Removed:
                    break;
                case PmChange.Rewritten:
                    Add(target, spec.Section, item with { Text = item.Text + " (riscritto dal PM)" });
                    break;
                case PmChange.DecidedByPm:
                    Add(target, spec.Section, item with { EvidenceRefs = [.. item.EvidenceRefs, pmSource] });
                    break;
                default:
                    Add(target, spec.Section, item);
                    break;
            }
        }

        var view = new EvidenceSelectionView([new(message, 1, Guid.NewGuid(), 1, evidences.Count, null)], evidences, [], []);
        var messageSource = new AnalysisSource { Id = message, Kind = AnalysisSourceKind.Message, MessageId = message, MessageRole = AnalysisMessageRole.User, RevisionId = null };
        var pm = new AnalysisSource { Id = pmSource, Kind = AnalysisSourceKind.PmRevision, MessageId = null, MessageRole = null, RevisionId = Guid.NewGuid() };
        return new(view, links, Content([messageSource], ai), Content([messageSource, pm], target), new() { [message] = body }, message, itemIds);
    }

    private static void Add(Dictionary<string, List<AnalysisItem>> sections, string section, AnalysisItem item)
    {
        if (!sections.TryGetValue(section, out var list))
        {
            sections[section] = list = [];
        }

        list.Add(item);
    }

    private static AnalysisContent Content(IReadOnlyList<AnalysisSource> sources, Dictionary<string, List<AnalysisItem>> sections)
    {
        IReadOnlyList<AnalysisItem> Of(string name) => sections.TryGetValue(name, out var list) ? [.. list] : [];
        return new()
        {
            SchemaVersion = AnalysisContent.CurrentSchemaVersion,
            AnalysisId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            SourceCutoffSequence = 1,
            ValidationState = AnalysisContentState.Unvalidated,
            Sources = sources,
            Goals = Of("goals"),
            ContextAndScope = new AnalysisContextAndScope { Overview = Of("contextAndScope.overview"), Included = Of("contextAndScope.included"), Excluded = Of("contextAndScope.excluded") },
            Requirements = Of("requirements"),
            OperationalFlows = Of("operationalFlows"),
            Constraints = Of("constraints"),
            Dependencies = Of("dependencies"),
            Assumptions = Of("assumptions"),
            OpenQuestions = Of("openQuestions"),
            Conflicts = sections.ContainsKey("conflicts")
                ? [new AnalysisConflict { Id = Guid.NewGuid(), Description = "Alternative", Status = AnalysisConflictStatus.Open, Alternatives = Of("conflicts"), Resolution = null }]
                : [],
        };
    }
}

// ============================================================================================================================
// API e persistenza dei report su PostgreSQL
// ============================================================================================================================

/// <summary>
/// T066 — endpoint dei report di copertura (contracts/http-api.md §Registro) su PostgreSQL reale: report persistito con CheckerVersion e
/// hash degli input, stessa richiesta con gli stessi input = stesso report (200, mai duplicato, anche in concorrenza), input diversi =
/// report diverso, ID di un altro progetto 404, bozza non derivata dalla revisione 422, nessuna selezione 409, 401/403 e nessuna
/// modifica di analisi, revisioni, bozze o registro. L'analisi completata con selezione congelata è inserita con le entità del modello
/// sul registro estratto dai test (stesso percorso di claim e commit), senza modello.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class EvidenceCoveragePersistenceTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();
    private ApiHarness? _app;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            _app = await ApiHarness.StartAsync(_database.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    private ApiHarness App
    {
        get
        {
            TestEnvironment.RequirePostgres();
            return _app!;
        }
    }

    private static string Url(SeededAnalysis w, string path = "") => $"/api/projects/{w.Project}/analyses/{w.Analysis}/coverage-reports{path}";

    private static readonly string[] ProtectedTables =
        ["ProcessingRuns", "AnalysisRevisions", "Drafts", "AnalysisEvidenceSelections", "AnalysisEvidenceSelectionItems", "AnalysisEvidenceLinks",
         "EvidenceExtractionVersions", "Evidences", "EvidenceRelations", "EvidenceMessageStates", "EvidencePromotions", "ConversationMessages"];

    [Fact]
    public async Task A_report_is_persisted_with_checker_version_and_the_hash_of_its_exact_inputs_and_exposes_findings_by_category()
    {
        using var client = await App.LoginAsync();
        var w = await SeededAnalysisFactory.CreateAsync(client, App.Services);
        var edited = await SeededAnalysisFactory.AddPmRevisionAsync(App.Services, w, confirmed: false, SeededAnalysisFactory.DropItem2AndRewriteItem1);
        var draft = await SeededAnalysisFactory.AddDraftAsync(App.Services, w, edited, "# Documento\n\nTesto non correlato.");

        var created = await client.PostAsJsonAsync(Url(w), new { revisionId = edited, draftId = draft }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var report = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();

        Assert.Equal("coverage-v1", report.GetProperty("checkerVersion").GetString());
        Assert.Equal(edited, report.GetProperty("revisionId").GetGuid());
        Assert.Equal(draft, report.GetProperty("draftId").GetGuid());
        Assert.Matches("^[0-9a-f]{64}$", report.GetProperty("inputHash").GetString()!);
        Assert.Equal("pm.uno", report.GetProperty("createdBy").GetString());
        Assert.Contains("fedele", string.Join(' ', report.GetProperty("limits").EnumerateArray().Select(l => l.GetString())), StringComparison.Ordinal);

        // Categorie separate: item2 rimosso e item1 riscritto dal PM; la bozza non cita la fonte e non contiene le parole dell'elemento.
        var findings = report.GetProperty("findings").EnumerateArray().ToList();
        string[] Of(string category) => [.. findings.Where(f => f.GetProperty("category").GetString() == category).Select(f => f.GetProperty("code").GetString()!).Order()];
        Assert.Equal(["item_removed_by_pm", "item_rewritten_by_pm"], Of("PmAssessment"));
        Assert.Equal(["revision_source_not_cited"], Of("DeterministicError"));
        Assert.Equal(["revision_item_missing_in_document"], Of("SemanticRisk"));
        Assert.All(findings.Where(f => f.GetProperty("category").GetString() != "SemanticRisk"), f => Assert.Equal(JsonValueKind.Null, f.GetProperty("confidence").ValueKind));
        Assert.Equal("medium", findings.Single(f => f.GetProperty("category").GetString() == "SemanticRisk").GetProperty("confidence").GetString());
        var counts = report.GetProperty("counts");
        Assert.Equal((1, 1, 2), (counts.GetProperty("deterministicErrors").GetInt32(), counts.GetProperty("semanticRisks").GetInt32(), counts.GetProperty("pmAssessments").GetInt32()));

        // Riga persistita: versione del checker, hash ricalcolato dagli input esatti letti dal database, report integrale.
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.EvidenceCoverageReports.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((w.Project, w.Analysis, edited, draft, "coverage-v1"), (row.ProjectId, row.AnalysisId, row.RevisionId, row.DraftId, row.CheckerVersion));
        Assert.Equal((1, 1, 2), (row.DeterministicErrors, row.SemanticRisks, row.PmAssessments));
        var selection = await db.AnalysisEvidenceSelections.AsNoTracking().SingleAsync(Ct);
        var links = await db.AnalysisEvidenceLinks.AsNoTracking().Select(l => new { l.ItemId, l.EvidenceId }).ToListAsync(Ct);
        var revision = await db.AnalysisRevisions.AsNoTracking().SingleAsync(r => r.Id == edited, Ct);
        var markdown = await db.Drafts.AsNoTracking().Where(d => d.Id == draft).Select(d => d.Markdown).SingleAsync(Ct);
        Assert.Equal(
            EvidenceCoverage.InputHash(selection.ViewHash, [.. links.Select(l => (l.ItemId, l.EvidenceId))], w.AiRevision, edited, draft, revision.ContentJson, markdown),
            row.InputHash);
        var stored = EvidenceJson.Deserialize<CoverageReportContent>(row.Report);
        Assert.Equal((EvidenceCoverage.CheckerVersion, selection.ViewHash, w.AiRevision), (stored.CheckerVersion, stored.ViewHash, stored.AiRevisionId));
        Assert.Equal(EvidenceCoverage.Limits, stored.Limits);

        // Lettura: dettaglio identico al creato ed elenco con il solo riepilogo.
        var detail = await client.GetFromJsonAsync<JsonElement>(Url(w, "/" + row.Id), Ct);
        Assert.Equal(report.GetRawText(), detail.GetRawText());
        var list = await client.GetFromJsonAsync<JsonElement>(Url(w), Ct);
        var summary = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(row.Id, summary.GetProperty("id").GetGuid());
        Assert.False(summary.TryGetProperty("findings", out _));
        Assert.False(summary.TryGetProperty("inputHash", out _));
        Assert.Equal(2, summary.GetProperty("counts").GetProperty("pmAssessments").GetInt32());

        var raw = report.GetRawText();
        foreach (var forbidden in new[] { "reasoning", "\"prompt\"", "rawOutput", "instructions" })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_same_inputs_return_the_same_report_with_200_and_never_a_duplicate_even_when_requested_concurrently()
    {
        using var client = await App.LoginAsync();
        var w = await SeededAnalysisFactory.CreateAsync(client, App.Services);

        // Richieste concorrenti sullo stesso input: una sola creazione, le altre rispondono con lo stesso report.
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.PostAsync(Url(w), null, Ct)));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"{r.StatusCode}"));
        var ids = new List<Guid>();
        foreach (var r in responses)
        {
            ids.Add((await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
        }

        Assert.Single(ids.Distinct());
        Assert.Equal(1L, await Scalar<long>("""SELECT count(*) FROM "EvidenceCoverageReports" """));

        // Ripetizioni successive, con body vuoto, revisione esplicita corrente o body {} : stesso report, mai 201.
        var before = await Digest("EvidenceCoverageReports");
        foreach (var body in new object?[] { null, new { }, new { revisionId = w.AiRevision }, new { revisionId = w.AiRevision, draftId = (Guid?)null } })
        {
            var again = body is null ? await client.PostAsync(Url(w), null, Ct) : await client.PostAsJsonAsync(Url(w), body, Ct);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(ids[0], (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
        }

        Assert.Equal(before, await Digest("EvidenceCoverageReports"));      // CreatedAt e contenuto invariati: nessuna riscrittura
        Assert.Equal(1L, await Scalar<long>("""SELECT count(*) FROM "EvidenceCoverageReports" """));
    }

    [Fact]
    public async Task A_different_revision_or_draft_gives_a_different_report_and_changed_revision_content_changes_the_input_hash()
    {
        using var client = await App.LoginAsync();
        var w = await SeededAnalysisFactory.CreateAsync(client, App.Services);
        var edited = await SeededAnalysisFactory.AddPmRevisionAsync(App.Services, w, confirmed: false, SeededAnalysisFactory.DropItem2AndRewriteItem1);
        var draftOnAi = await SeededAnalysisFactory.AddDraftAsync(App.Services, w, w.AiRevision, "# Documento\n\nIl portale invia un promemoria via email entro 24 ore.");
        var draftOnEdited = await SeededAnalysisFactory.AddDraftAsync(App.Services, w, edited, "# Documento\n\nIl portale invia un promemoria entro 48 ore.");

        var requests = new (string Name, object Body)[]
        {
            ("ai", new { revisionId = w.AiRevision }),
            ("edited", new { revisionId = edited }),
            ("draft-ai", new { revisionId = w.AiRevision, draftId = draftOnAi }),
            ("draft-edited", new { draftId = draftOnEdited }),      // la revisione si deduce dalla bozza
        };
        var reports = new Dictionary<string, JsonElement>();
        foreach (var (name, body) in requests)
        {
            var response = await client.PostAsJsonAsync(Url(w), body, Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            reports[name] = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();
        }

        Assert.Equal(4, reports.Values.Select(r => r.GetProperty("id").GetGuid()).Distinct().Count());
        Assert.Equal(4, reports.Values.Select(r => r.GetProperty("inputHash").GetString()).Distinct().Count());
        Assert.Equal(4L, await Scalar<long>("""SELECT count(*) FROM "EvidenceCoverageReports" """));
        Assert.Equal(edited, reports["draft-edited"].GetProperty("revisionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, reports["ai"].GetProperty("draftId").ValueKind);

        // Contenuto della revisione diverso ⇒ findings e hash diversi; la revisione AI non ha rimozioni né riscritture.
        Assert.Equal(0, reports["ai"].GetProperty("counts").GetProperty("pmAssessments").GetInt32());
        Assert.Equal(2, reports["edited"].GetProperty("counts").GetProperty("pmAssessments").GetInt32());

        // Lista: dal più recente, tutti e quattro.
        var list = await client.GetFromJsonAsync<JsonElement>(Url(w), Ct);
        Assert.Equal(4, list.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Ids_of_another_project_or_a_non_analysis_run_are_404_and_nothing_is_created()
    {
        using var client = await App.LoginAsync();
        var a = await SeededAnalysisFactory.CreateAsync(client, App.Services);
        var b = await SeededAnalysisFactory.CreateAsync(client, App.Services);
        var report = (await (await client.PostAsync(Url(a), null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        // L'analisi di A sotto il progetto B (e viceversa): 404 su creazione, elenco e dettaglio.
        var crossBase = $"/api/projects/{b.Project}/analyses/{a.Analysis}/coverage-reports";
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(crossBase, null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(crossBase, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{crossBase}/{report}", Ct)).StatusCode);

        // Il report di A letto con l'analisi di B (stesso progetto A o B): non esiste per quella analisi.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{b.Project}/analyses/{b.Analysis}/coverage-reports/{report}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{a.Project}/analyses/{b.Analysis}/coverage-reports/{report}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(a, "/" + report), Ct)).StatusCode);

        // Analisi o progetto inesistenti, e un run Document usato come analisi.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/projects/{a.Project}/analyses/{Guid.NewGuid()}/coverage-reports", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/analyses/{a.Analysis}/coverage-reports", Ct)).StatusCode);
        var draft = await SeededAnalysisFactory.AddDraftAsync(App.Services, a, a.AiRevision, "# Documento");
        var documentRun = await Scalar<Guid>("""SELECT "ProcessingRunId" FROM "Drafts" WHERE "Id" = @d""", ("d", draft));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/projects/{a.Project}/analyses/{documentRun}/coverage-reports", null, Ct)).StatusCode);

        // Una bozza o una revisione di un altro progetto/analisi non sono input validi: 422, nessun report.
        var foreignDraft = await SeededAnalysisFactory.AddDraftAsync(App.Services, b, b.AiRevision, "# Altro");
        var foreign = await client.PostAsJsonAsync(Url(a), new { draftId = foreignDraft }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal("coverage_input_invalid", await Code(foreign));
        var foreignRevision = await client.PostAsJsonAsync(Url(a), new { revisionId = b.AiRevision }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignRevision.StatusCode);
        Assert.Equal("coverage_input_invalid", await Code(foreignRevision));
        Assert.Equal(1L, await Scalar<long>("""SELECT count(*) FROM "EvidenceCoverageReports" """));
    }

    [Fact]
    public async Task A_draft_not_derived_from_the_revision_is_422_coverage_input_invalid_and_creates_nothing()
    {
        using var client = await App.LoginAsync();
        var w = await SeededAnalysisFactory.CreateAsync(client, App.Services);
        var edited = await SeededAnalysisFactory.AddPmRevisionAsync(App.Services, w, confirmed: false, SeededAnalysisFactory.DropItem2AndRewriteItem1);
        var draftOnAi = await SeededAnalysisFactory.AddDraftAsync(App.Services, w, w.AiRevision, "# Documento");

        var mismatch = await client.PostAsJsonAsync(Url(w), new { revisionId = edited, draftId = draftOnAi }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        var problem = (await mismatch.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();
        Assert.Equal("coverage_input_invalid", problem.GetProperty("code").GetString());
        Assert.Contains("draftId", problem.GetProperty("errors").EnumerateObject().Select(p => p.Name));

        var unknownDraft = await client.PostAsJsonAsync(Url(w), new { draftId = Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownDraft.StatusCode);
        Assert.Equal("coverage_input_invalid", await Code(unknownDraft));
        var unknownRevision = await client.PostAsJsonAsync(Url(w), new { revisionId = Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownRevision.StatusCode);

        Assert.Equal(0L, await Scalar<long>("""SELECT count(*) FROM "EvidenceCoverageReports" """));
    }

    [Fact]
    public async Task An_analysis_without_a_frozen_selection_or_without_an_ai_revision_is_409_selection_not_available()
    {
        using var client = await App.LoginAsync();
        var legacy = await SeededAnalysisFactory.CreateAsync(client, App.Services, withSelection: false);     // revisione AI ma nessuna selezione congelata
        var response = await client.PostAsync(Url(legacy), null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("selection_not_available", await Code(response));

        // Run non ancora concluso: né selezione né revisione AI.
        var queued = await SeededAnalysisFactory.CreateAsync(client, App.Services, withSelection: false, completed: false);
        var notReady = await client.PostAsync(Url(queued), null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
        Assert.Equal("selection_not_available", await Code(notReady));

        // L'elenco di un'analisi senza report resta vuoto e 200: la mancanza di selezione non è un errore di lettura.
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>(Url(legacy), Ct)).GetProperty("items").EnumerateArray());
        Assert.Equal(0L, await Scalar<long>("""SELECT count(*) FROM "EvidenceCoverageReports" """));
    }

    [Fact]
    public async Task Every_coverage_route_needs_a_pm()
    {
        using var client = await App.LoginAsync();
        var w = await SeededAnalysisFactory.CreateAsync(client, App.Services);
        var report = (await (await client.PostAsync(Url(w), null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        foreach (var (method, path) in new[] { (HttpMethod.Post, Url(w)), (HttpMethod.Get, Url(w)), (HttpMethod.Get, Url(w, "/" + report)) })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await App.Anonymous().SendAsync(new HttpRequestMessage(method, path), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await App.WithoutPmRole().SendAsync(new HttpRequestMessage(method, path), Ct)).StatusCode);
        }

        Assert.Equal(1L, await Scalar<long>("""SELECT count(*) FROM "EvidenceCoverageReports" """));
    }

    [Fact]
    public async Task Creating_reports_never_modifies_analyses_revisions_drafts_or_the_evidence_registry()
    {
        using var client = await App.LoginAsync();
        var w = await SeededAnalysisFactory.CreateAsync(client, App.Services);
        var edited = await SeededAnalysisFactory.AddPmRevisionAsync(App.Services, w, confirmed: true, SeededAnalysisFactory.DropItem2AndRewriteItem1);
        var draft = await SeededAnalysisFactory.AddDraftAsync(App.Services, w, edited, "# Documento\n\nTesto non correlato.");
        var before = await Digests(ProtectedTables);
        var project = await Digest("Projects");

        foreach (var body in new object[] { new { }, new { revisionId = w.AiRevision }, new { revisionId = edited }, new { draftId = draft } })
        {
            Assert.True((await client.PostAsJsonAsync(Url(w), body, Ct)).IsSuccessStatusCode);
        }

        // {} e revisionId=edited coincidono: senza indicazione vale la revisione corrente (la più recente).
        Assert.Equal(3L, await Scalar<long>("""SELECT count(*) FROM "EvidenceCoverageReports" """));
        Assert.Equal(before, await Digests(ProtectedTables));
        Assert.Equal(project, await Digest("Projects"));
        Assert.Equal("PmConfirmed", await Scalar<string>("""SELECT "ValidationState" FROM "AnalysisRevisions" WHERE "Id" = @r""", ("r", edited)));
        Assert.Equal(2, await Scalar<int>("""SELECT count(*)::int FROM "AnalysisRevisions" WHERE "AnalysisId" = @a""", ("a", w.Analysis)));
    }

    // ---------------------------------------------------------------- supporto

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    private async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters) =>
        await SqlHelper.ScalarAsync<T>(_database.ConnectionString, sql, parameters);

    private Task<string> Digest(string table) => SqlHelper.DigestAsync(_database.ConnectionString, table);

    private async Task<Dictionary<string, string>> Digests(IEnumerable<string> tables)
    {
        var result = new Dictionary<string, string>();
        foreach (var table in tables)
        {
            result[table] = await Digest(table);
        }

        return result;
    }
}

/// <summary>Accesso SQL diretto dei test di integrazione del registro: scalari e impronte delle tabelle per provare che nulla è stato riscritto.</summary>
internal static class SqlHelper
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default! : (T)result;
    }

    public static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Impronta del contenuto testuale di tutte le righe di una tabella (o di quelle che soddisfano <paramref name="where"/>), indipendente dall'ordine.</summary>
    public static Task<string> DigestAsync(string connectionString, string table, string? where = null, params (string Name, object Value)[] parameters) =>
        ScalarAsync<string>(connectionString,
            $"""SELECT coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '-') || ':' || count(*) FROM "{table}" t {(where is null ? "" : "WHERE " + where)}""",
            parameters);
}

internal sealed record SeededAnalysis(
    Guid Project, Guid Conversation, Guid User, Guid Analysis, Guid AiRevision, Guid Message1, Guid Message2, Guid Version1, Guid Version2,
    Guid Evidence1, Guid Evidence2, Guid Item1, Guid Item2);

/// <summary>
/// Costruisce, su un database reale, un'analisi completata con selezione congelata e mappa elementi→evidenze: progetto e messaggi dall'API,
/// estrazioni dal percorso reale del registro (claim, validazione, commit), vista dal <see cref="EvidenceLedgerStore"/> e righe del run, della
/// selezione e della revisione AI come le scriverebbe il workflow. Nessun modello.
/// </summary>
internal static class SeededAnalysisFactory
{
    public const string Text1 = "Il portale invia un promemoria via email entro 24 ore.";
    public const string Text2 = "Le fatture restano fuori perimetro.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>La revisione del PM: toglie l'elemento 2 e riscrive l'elemento 1.</summary>
    public static AnalysisContent DropItem2AndRewriteItem1(AnalysisContent content) =>
        content with { Requirements = [content.Requirements[0] with { Text = "Il portale invia un promemoria entro 48 ore." }] };

    public static async Task<Guid> CreateProjectAsync(HttpClient client, params string[] messages)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name = $"Copertura {Guid.NewGuid():N}" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var project = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        foreach (var text in messages)
        {
            var post = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
            Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        }

        return project;
    }

    public static async Task<SeededAnalysis> CreateAsync(HttpClient client, IServiceProvider services, bool withSelection = true, bool completed = true)
    {
        var project = await CreateProjectAsync(client, Text1, Text2);
        await EvidenceTestSeed.CompleteAsync(services, Ct);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await db.Conversations.AsNoTracking().Where(c => c.ProjectId == project).Select(c => c.Id).SingleAsync(Ct);
        var user = await db.Projects.AsNoTracking().Where(p => p.Id == project).Select(p => p.CreatedBy).SingleAsync(Ct);
        var messages = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversation && m.Role == bProjects.App.Conversations.MessageRole.User).OrderBy(m => m.Sequence).ToListAsync(Ct);
        var states = await db.EvidenceMessageStates.AsNoTracking().Where(s => s.ProjectId == project).ToDictionaryAsync(s => s.MessageId, Ct);
        var versions = await db.EvidenceExtractionVersions.AsNoTracking().Where(v => v.ProjectId == project).ToListAsync(Ct);
        var items = messages.Select(m => versions.Single(v => v.Id == states[m.Id].CurrentVersionId))
            .Select(v => new SelectionItem(v.MessageId, v.MessageSequence, v.Id, v.Version, v.EvidenceCount, v.NoEvidenceReason)).ToList();
        var view = await new EvidenceLedgerStore(db).BuildViewAsync(items, Ct);
        var now = DateTimeOffset.UtcNow;

        var run = new ProcessingRun
        {
            Id = Guid.NewGuid(),
            ProjectId = project,
            Kind = ProcessingRunKind.Analysis,
            ConversationId = conversation,
            SourceCutoffSequence = messages[^1].Sequence,
            RequestKey = Guid.NewGuid(),
            RequestedBy = user,
            RequestedAt = now,
            Status = completed ? ProcessingRunStatus.Running : ProcessingRunStatus.Queued,
            StartedAt = completed ? now : null,
            DeadlineAt = now.AddHours(1),
            WorkflowDefinitionVersion = ProcessingRun.AnalysisWorkflowVersion,
            MafVersion = "1.26.0",
            AttemptCount = completed ? 1 : 0,
            MaxAttempts = 3,
            FencingToken = completed ? 1 : 0,
            UpdatedAt = now,
        };
        db.ProcessingRuns.Add(run);
        await db.SaveChangesAsync(Ct);
        if (!completed)
        {
            return new(project, conversation, user, run.Id, Guid.Empty, messages[0].Id, messages[1].Id, items[0].VersionId, items[1].VersionId,
                view.Evidences[0].EvidenceId, view.Evidences[1].EvidenceId, Guid.Empty, Guid.Empty);
        }

        if (withSelection)
        {
            db.AnalysisEvidenceSelections.Add(new AnalysisEvidenceSelection { RunId = run.Id, ProjectId = project, CreatedAt = now, ViewHash = view.Hash(), View = view.Serialize() });
            await db.SaveChangesAsync(Ct);
            db.AnalysisEvidenceSelectionItems.AddRange(items.Select(i => new AnalysisEvidenceSelectionItem { RunId = run.Id, MessageId = i.MessageId, MessageSequence = i.Sequence, VersionId = i.VersionId }));
            await db.SaveChangesAsync(Ct);
        }

        var (item1, item2) = (new AnalysisItem { Id = Guid.NewGuid(), Text = Text1, Classification = AnalysisClassification.Declared, EvidenceRefs = [messages[0].Id] },
            new AnalysisItem { Id = Guid.NewGuid(), Text = Text2, Classification = AnalysisClassification.Declared, EvidenceRefs = [messages[1].Id] });
        var content = new AnalysisContent
        {
            SchemaVersion = AnalysisContent.CurrentSchemaVersion,
            AnalysisId = run.Id,
            ProjectId = project,
            SourceCutoffSequence = (int)messages[^1].Sequence,
            ValidationState = AnalysisContentState.Unvalidated,
            Sources = [.. messages.Select(m => new AnalysisSource { Id = m.Id, Kind = AnalysisSourceKind.Message, MessageId = m.Id, MessageRole = AnalysisMessageRole.User, RevisionId = null })],
            Goals = [],
            ContextAndScope = new AnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = [item1, item2],
            OperationalFlows = [],
            Constraints = [],
            Dependencies = [],
            Assumptions = [],
            OpenQuestions = [],
            Conflicts = [],
        };
        var revision = new AnalysisRevision
        {
            Id = Guid.NewGuid(),
            AnalysisId = run.Id,
            Version = 1,
            Origin = AnalysisRevisionOrigin.Ai,
            CreatedBy = user,
            CreatedAt = now,
            SchemaVersion = AnalysisRevision.CurrentSchemaVersion,
            ContentJson = AnalysisContentJson.Serialize(content),
            ValidationState = AnalysisValidationState.Unvalidated,
        };
        db.AnalysisRevisions.Add(revision);
        await db.SaveChangesAsync(Ct);
        if (withSelection)
        {
            db.AnalysisEvidenceLinks.AddRange(
                new AnalysisEvidenceLink { RunId = run.Id, ItemId = item1.Id, EvidenceId = view.Evidences[0].EvidenceId, ProjectId = project },
                new AnalysisEvidenceLink { RunId = run.Id, ItemId = item2.Id, EvidenceId = view.Evidences[1].EvidenceId, ProjectId = project });
        }

        var tracked = await db.ProcessingRuns.SingleAsync(r => r.Id == run.Id, Ct);
        tracked.Status = ProcessingRunStatus.Completed;
        tracked.ResultRevisionId = revision.Id;
        tracked.CompletedAt = now;
        await db.SaveChangesAsync(Ct);

        return new(project, conversation, user, run.Id, revision.Id, messages[0].Id, messages[1].Id, items[0].VersionId, items[1].VersionId,
            view.Evidences[0].EvidenceId, view.Evidences[1].EvidenceId, item1.Id, item2.Id);
    }

    /// <summary>Aggiunge la revisione v2 del PM (opzionalmente confermata) a partire dal contenuto della revisione AI.</summary>
    public static async Task<Guid> AddPmRevisionAsync(IServiceProvider services, SeededAnalysis world, bool confirmed, Func<AnalysisContent, AnalysisContent> edit)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var parent = await db.AnalysisRevisions.AsNoTracking().SingleAsync(r => r.Id == world.AiRevision, Ct);
        var content = edit(AnalysisContentJson.Deserialize(parent.ContentJson)) with { ValidationState = confirmed ? AnalysisContentState.PmConfirmed : AnalysisContentState.Unvalidated };
        var now = DateTimeOffset.UtcNow;
        var revision = new AnalysisRevision
        {
            Id = Guid.NewGuid(),
            AnalysisId = world.Analysis,
            Version = 2,
            ParentRevisionId = parent.Id,
            Origin = AnalysisRevisionOrigin.PmEdit,
            CreatedBy = world.User,
            CreatedAt = now,
            SchemaVersion = AnalysisRevision.CurrentSchemaVersion,
            ContentJson = AnalysisContentJson.Serialize(content),
            ValidationState = confirmed ? AnalysisValidationState.PmConfirmed : AnalysisValidationState.Unvalidated,
            ConfirmedBy = confirmed ? world.User : null,
            ConfirmedAt = confirmed ? now : null,
        };
        db.AnalysisRevisions.Add(revision);
        await db.SaveChangesAsync(Ct);
        return revision.Id;
    }

    /// <summary>Un nuovo run di Analysis in esecuzione (lease valido) sulla stessa conversazione, pronto a congelare la propria selezione.</summary>
    public static async Task<AnalysisRunClaim> InsertRunningAnalysisAsync(IServiceProvider services, SeededAnalysis world)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (run, key, owner) = (Guid.NewGuid(), Guid.NewGuid(), "seed-run");
        var cutoff = await db.ConversationMessages.AsNoTracking().Where(m => m.ConversationId == world.Conversation).MaxAsync(m => m.Sequence, Ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","ConversationId","SourceCutoffSequence","RequestKey","RequestedBy","RequestedAt","Status","StartedAt","DeadlineAt",
                "WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","LeaseOwner","LeaseUntil","FencingToken","UpdatedAt")
            VALUES ({run},{world.Project},'Analysis',{world.Conversation},{cutoff},{key},{world.User},now(),'Running',now(),now() + interval '1 hour',
                {ProcessingRun.AnalysisWorkflowVersion},'1.26.0',1,3,{owner}, now() + interval '10 minutes', 1, now())
            """, Ct);
        return new AnalysisRunClaim(run, owner, 1);
    }

    /// <summary>Bozza di un run Document concluso, derivata dalla revisione indicata.</summary>
    public static async Task<Guid> AddDraftAsync(IServiceProvider services, SeededAnalysis world, Guid revision, string markdown)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var run = new ProcessingRun
        {
            Id = Guid.NewGuid(),
            ProjectId = world.Project,
            Kind = ProcessingRunKind.Document,
            AnalysisId = world.Analysis,
            InputRevisionId = revision,
            RequestKey = Guid.NewGuid(),
            RequestedBy = world.User,
            RequestedAt = now,
            Status = ProcessingRunStatus.Running,
            StartedAt = now,
            DeadlineAt = now.AddHours(1),
            WorkflowDefinitionVersion = ProcessingRun.DocumentWorkflowVersion,
            MafVersion = "1.26.0",
            AttemptCount = 1,
            MaxAttempts = 3,
            FencingToken = 1,
            UpdatedAt = now,
        };
        db.ProcessingRuns.Add(run);
        await db.SaveChangesAsync(Ct);
        var draft = new Draft { Id = Guid.NewGuid(), ProjectId = world.Project, ProcessingRunId = run.Id, AnalysisRevisionId = revision, Markdown = markdown, CreatedAt = now };
        db.Drafts.Add(draft);
        await db.SaveChangesAsync(Ct);
        var tracked = await db.ProcessingRuns.SingleAsync(r => r.Id == run.Id, Ct);
        tracked.Status = ProcessingRunStatus.Completed;
        tracked.DraftId = draft.Id;
        tracked.CompletedAt = now;
        await db.SaveChangesAsync(Ct);
        return draft.Id;
    }
}
