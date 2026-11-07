using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T062 (DET) — `coverage-2` (contracts/ai-workflow.md §errori; http-api.md §coverage): errori deterministici, rischi semantici stimati e giudizi PM
/// restano separati; l'omissione di un'evidenza nota è un errore bloccante; un gold semanticamente errato è rilevato (separatamente) anche con
/// copertura per ID del 100%; nessun booleano «complete» autorevole. Esempi in domini inventati, senza database.
/// </summary>
public sealed class DocumentCoverageCheckerTests
{
    private static readonly IReadOnlyDictionary<string, FindingCategory> Codes = new Dictionary<string, FindingCategory>
    {
        ["evidence_unrepresented"] = FindingCategory.DeterministicError,
        ["source_range_invalid"] = FindingCategory.DeterministicError,
        ["evidence_source_mismatch"] = FindingCategory.DeterministicError,
        ["item_source_unresolved"] = FindingCategory.DeterministicError,
        ["item_without_fragment"] = FindingCategory.DeterministicError,
        ["portion_not_processed"] = FindingCategory.DeterministicError,
        ["candidate_not_terminal"] = FindingCategory.DeterministicError,
        ["patch_blocking"] = FindingCategory.DeterministicError,
        ["known_relation_unrepresented"] = FindingCategory.DeterministicError,
        ["revision_source_not_cited"] = FindingCategory.DeterministicError,
        ["qualifier_changed"] = FindingCategory.SemanticRisk,
        ["negation_inverted"] = FindingCategory.SemanticRisk,
        ["instrument_detached"] = FindingCategory.SemanticRisk,
        ["revision_item_missing_in_document"] = FindingCategory.SemanticRisk,
        ["item_removed_by_pm"] = FindingCategory.PmAssessment,
        ["item_rewritten_by_pm"] = FindingCategory.PmAssessment,
        ["conflict_resolved_by_pm"] = FindingCategory.PmAssessment,
        ["gap_open_reference_removed_by_pm"] = FindingCategory.PmAssessment,
    };

    // ---------------------------------------------------------------- omissione nota bloccante

    [Fact]
    public void Omitting_a_known_evidence_is_a_blocking_deterministic_error_and_the_id_coverage_shows_it()
    {
        var input = Build(new Spec("Il cliente A emette al massimo 5 ordini al giorno."), new Spec("Il magazzino B chiude alle 18.", Linked: false));

        var findings = DocumentCoverage.Check(input);

        var finding = Assert.Single(findings);
        Assert.Equal(("evidence_unrepresented", FindingCategory.DeterministicError, true), (finding.Code, finding.Category, finding.Blocking));
        Assert.Equal("E2", finding.Alias);
        Assert.Null(finding.Confidence);
        var summary = DocumentCoverage.Summarize(input, findings);
        Assert.Equal((2, 1, 1, 0, 0), (summary.ExpectedEvidence, summary.RepresentedEvidence, summary.DeterministicErrors, summary.SemanticRisks, summary.PmAssessments));
        Assert.Equal(1, summary.BlockingFindings);
    }

    [Fact]
    public void A_report_without_findings_has_no_blocking_finding_and_a_checked_input_is_not_called_complete()
    {
        var input = Build(new Spec("Il cliente A emette ordini."));

        var findings = DocumentCoverage.Check(input);

        Assert.Empty(findings);
        Assert.Equal(0, DocumentCoverage.Summarize(input, findings).BlockingFindings);
    }

    // ---------------------------------------------------------------- gold semanticamente errato con ID al 100%

    [Fact]
    public void A_semantically_wrong_gold_is_detected_as_separate_semantic_risks_even_with_100_percent_id_coverage()
    {
        var input = Build(
            new Spec("Il cliente A emette al massimo 5 ordini al giorno.", Qualifiers: ["al massimo 5 ordini"], ItemText: "Il cliente A emette al massimo 8 ordini al giorno."),
            new Spec("Il portale non invia SMS.", Polarity: EvidencePolarity.Prohibited, ItemText: "Il portale invia SMS ai clienti."));

        var findings = DocumentCoverage.Check(input);
        var summary = DocumentCoverage.Summarize(input, findings);

        Assert.Equal((2, 2), (summary.ExpectedEvidence, summary.RepresentedEvidence));
        Assert.Equal(0, summary.DeterministicErrors);
        Assert.Equal(0, summary.BlockingFindings);
        Assert.Equal(["negation_inverted", "qualifier_changed"], findings.Select(f => f.Code).Order());
        Assert.All(findings, f =>
        {
            Assert.Equal(FindingCategory.SemanticRisk, f.Category);
            Assert.False(f.Blocking);
            Assert.NotNull(f.Confidence);
        });
        Assert.Equal(2, summary.SemanticRisks);
    }

    [Fact]
    public void A_faithful_representation_raises_neither_deterministic_nor_semantic_findings()
    {
        var input = Build(
            new Spec("Il cliente A emette al massimo 5 ordini al giorno.", Qualifiers: ["al massimo 5 ordini"]),
            new Spec("Il portale non invia SMS.", Polarity: EvidencePolarity.Prohibited));

        Assert.Empty(DocumentCoverage.Check(input));
    }

    // ---------------------------------------------------------------- porzioni

    [Fact]
    public void Pending_and_failed_portions_are_deterministic_errors_while_a_no_evidence_portion_is_a_reasoned_outcome()
    {
        var prep = Guid.NewGuid();
        var (done, empty, pending, failed) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var input = Build(new Spec("Il cliente A emette ordini.")) with
        {
            Portions =
            [
                new(done, prep, 1, PortionStatus.Completed, null),
                new(empty, prep, 2, PortionStatus.Completed, "solo intestazione"),
                new(pending, prep, 3, PortionStatus.Pending, null),
                new(failed, prep, 4, PortionStatus.Failed, null),
            ],
        };

        var findings = DocumentCoverage.Check(input);
        var summary = DocumentCoverage.Summarize(input, findings);

        Assert.Equal(["portion_not_processed", "portion_not_processed"], findings.Select(f => f.Code));
        Assert.All(findings, f => Assert.True(f.Blocking && f.Category == FindingCategory.DeterministicError));
        Assert.Equal((4, 2, 1, 2), (summary.PortionsExpected, summary.PortionsProcessed, summary.PortionsNoEvidence, summary.PortionsMissing));
        Assert.Equal(2, summary.BlockingFindings);
    }

    // ---------------------------------------------------------------- provenienza

    [Fact]
    public void A_source_range_that_no_longer_matches_the_original_text_or_whose_block_is_gone_is_a_deterministic_error()
    {
        var input = Build(new Spec("Il cliente A emette ordini."), new Spec("Il magazzino B chiude alle 18."));
        var first = input.Evidence[0].Sources[0];
        var second = input.Evidence[1].Sources[0];
        var texts = input.OriginalTexts.ToDictionary(t => t.Key, t => t.Value);
        texts[first.BlockId!.Value] = "Testo del blocco cambiato del tutto.";
        texts.Remove(second.BlockId!.Value);

        var findings = DocumentCoverage.Check(input with { OriginalTexts = texts });

        Assert.Equal(["source_range_invalid", "source_range_invalid"], findings.Select(f => f.Code));
        Assert.All(findings, f => Assert.True(f.Blocking));
    }

    [Fact]
    public void An_item_that_cites_nothing_of_its_evidence_or_a_source_outside_the_revision_is_a_deterministic_error()
    {
        var input = Build(new Spec("Il cliente A emette ordini."), new Spec("Il magazzino B chiude alle 18."), new Spec("Il corriere C ritira alle 9."));
        var requirements = input.Target.Requirements.ToList();
        requirements[0] = requirements[0] with { EvidenceRefs = [] };                                   // Documented senza frammento
        requirements[1] = requirements[1] with { EvidenceRefs = [Guid.NewGuid()] };                      // fonte fuori dalla revisione
        var target = input.Target with { Requirements = requirements };

        var findings = DocumentCoverage.Check(input with { Target = target, Ai = target });

        Assert.Contains(findings, f => f.Code == "item_without_fragment" && f.ItemId == requirements[0].Id);
        Assert.Contains(findings, f => f.Code == "item_source_unresolved" && f.ItemId == requirements[1].Id);
        Assert.Contains(findings, f => f.Code == "evidence_source_mismatch" && f.Alias == "E2");
        Assert.All(findings, f => Assert.Equal(FindingCategory.DeterministicError, f.Category));
    }

    [Fact]
    public void A_known_contradiction_must_be_represented_in_a_conflict_that_holds_both_items()
    {
        var input = Build(new Spec("Il cliente A emette al massimo 5 ordini."), new Spec("Il cliente A emette almeno 10 ordini.")) with
        {
            Relations = [new(Guid.Empty, Guid.Empty, EvidenceRelationType.Contradicts)],
        };
        input = input with { Relations = [new(input.Evidence[0].EvidenceId, input.Evidence[1].EvidenceId, EvidenceRelationType.Contradicts)] };

        var unrepresented = DocumentCoverage.Check(input);
        Assert.Equal("known_relation_unrepresented", Assert.Single(unrepresented).Code);
        Assert.True(unrepresented[0].Blocking);

        var items = input.Target.Requirements;
        var conflict = new DocumentAnalysisConflict
        {
            Id = Guid.NewGuid(), Description = "Ordini del cliente A", Status = AnalysisConflictStatus.Open, Resolution = null, Alternatives = items,
        };
        var target = input.Target with { Requirements = [], Conflicts = [conflict] };

        Assert.Empty(DocumentCoverage.Check(input with { Target = target, Ai = target }));
    }

    [Fact]
    public void A_draft_that_omits_the_marker_of_a_confirmed_item_source_is_a_deterministic_error_and_missing_words_are_only_a_risk()
    {
        var input = Build(new Spec("Il cliente emette ordini giornalieri verso magazzini regionali."));

        var findings = DocumentCoverage.Check(input with { DraftMarkdown = "# Titolo\n\nTesto qualsiasi non correlato." });

        Assert.Equal(FindingCategory.DeterministicError, Assert.Single(findings, f => f.Code == "revision_source_not_cited").Category);
        Assert.Equal(FindingCategory.SemanticRisk, Assert.Single(findings, f => f.Code == "revision_item_missing_in_document").Category);
        var marker = input.Target.Sources[0].Id;
        Assert.DoesNotContain(
            DocumentCoverage.Check(input with { DraftMarkdown = $"Il cliente emette ordini giornalieri verso magazzini regionali. [fonte:{marker}]" }),
            f => f.Code is "revision_source_not_cited" or "revision_item_missing_in_document");
    }

    // ---------------------------------------------------------------- giudizi PM

    [Fact]
    public void Pm_edits_are_assessments_never_errors_and_never_blocking()
    {
        var ai = Build(new Spec("Primo requisito del cliente A."), new Spec("Secondo requisito del cliente A."), new Spec("Terzo requisito del cliente A."));
        var requirements = ai.Ai.Requirements;
        var conflict = new DocumentAnalysisConflict
        {
            Id = Guid.NewGuid(), Description = "Scelta", Status = AnalysisConflictStatus.Open, Resolution = null, Alternatives = [],
        };
        var aiContent = ai.Ai with { Requirements = [requirements[0], requirements[1]], Conflicts = [conflict with { Alternatives = [requirements[2]] }] };
        var resolved = conflict with
        {
            Status = AnalysisConflictStatus.Resolved,
            Alternatives = [requirements[2]],
            Resolution = new AnalysisConflictResolution { SelectedAlternativeIds = [requirements[2].Id], Reason = "deciso dal PM", EvidenceRefs = [aiContent.Sources[0].Id] },
        };
        var target = aiContent with { Requirements = [requirements[0] with { Text = "Primo requisito riscritto dal PM." }], Conflicts = [resolved] };

        var findings = DocumentCoverage.Check(ai with { Ai = aiContent, Target = target });

        Assert.Equal(["conflict_resolved_by_pm", "item_removed_by_pm", "item_rewritten_by_pm"], findings.Select(f => f.Code).Order());
        Assert.All(findings, f =>
        {
            Assert.Equal(FindingCategory.PmAssessment, f.Category);
            Assert.False(f.Blocking);
        });
    }

    // ---------------------------------------------------------------- limiti, hash, nessun «complete»

    [Fact]
    public void The_report_declares_its_limits_and_exposes_no_authoritative_complete_boolean()
    {
        Assert.Equal("coverage-2", DocumentCoverage.CheckerVersion);
        Assert.Contains(DocumentCoverage.Limits, l => l.Contains("fedele", StringComparison.Ordinal));
        foreach (var type in new[]
        {
            typeof(DocumentCoverageSummary), typeof(DocumentCoverageFinding), typeof(DocumentCoverageContent), typeof(DocumentCoverageSummary3), typeof(DocumentCoverage3Content),
            typeof(GapAssessmentRecord),
        })
        {
            Assert.DoesNotContain(type.GetProperties(), p =>
                p.PropertyType == typeof(bool) && Regex.IsMatch(p.Name, "complet|valid|ok|pass|verified|success", RegexOptions.IgnoreCase) && p.Name != nameof(DocumentCoverageFinding.Blocking));
        }
    }

    [Fact]
    public void The_input_hash_covers_snapshot_parse_extraction_revision_and_draft_and_ignores_link_order()
    {
        var baseline = new DocumentCoverageHashes("s", "v", ["p1"], ["x1"], "r", "d");
        var links = new[] { (Guid.NewGuid(), Guid.NewGuid()), (Guid.NewGuid(), Guid.NewGuid()) };
        var hash = DocumentCoverage.InputHash(baseline, links);

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, DocumentCoverage.InputHash(baseline, [links[1], links[0]]));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline with { SourceSnapshotHash = "s2" }, links));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline with { ParseHashes = ["p2"] }, links));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline with { ExtractionHashes = ["x2"] }, links));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline with { RevisionHash = "r2" }, links));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline with { DraftHash = "d2" }, links));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline, [links[0]]));
    }

    [Fact]
    public void The_checker_is_deterministic_and_every_code_in_its_source_is_listed_here_with_its_category()
    {
        var input = Build(new Spec("Il cliente A emette ordini."), new Spec("Il magazzino B chiude.", Linked: false));
        Assert.Equal(DocumentCoverage.Check(input).Select(f => f.Message), DocumentCoverage.Check(input).Select(f => f.Message));

        var source = File.ReadAllText(Locate("src/bProjects.App/Evidence/EvidenceCoverage.cs"));
        var found = Regex.Matches(source, @"Doc\(FindingCategory\.(?<cat>\w+),\s*""(?<code>[a-z_]+)""");
        var fromSource = found.GroupBy(m => m.Groups["code"].Value).ToDictionary(g => g.Key, g => g.Select(m => Enum.Parse<FindingCategory>(m.Groups["cat"].Value)).Distinct().Single());
        Assert.Equal(Regex.Matches(source, @"Doc\(FindingCategory\.").Count, found.Count);
        Assert.Equal(Codes.OrderBy(c => c.Key).Select(c => $"{c.Key}:{c.Value}"), fromSource.OrderBy(c => c.Key).Select(c => $"{c.Key}:{c.Value}"));
    }

    // ---------------------------------------------------------------- coverage-3: valutazioni Gap (002 T139)

    private static GapAssessmentRecord Record(
        DocumentCoverageEvidence gap, GapOutcome outcome = GapOutcome.AnsweredInSnapshot, GapReason reason = GapReason.Answered, IReadOnlyList<Guid>? support = null,
        Guid? openItem = null, Guid? openConflict = null) => new(
        gap.EvidenceId, Guid.NewGuid(), new string('c', 64), outcome, reason, "Valutazione AI nello snapshot.", support ?? [], openItem, openConflict, new string('b', 64));

    private static DocumentCoverageInput WithOpenQuestion(DocumentCoverageInput input, Guid questionId)
    {
        var question = new DocumentAnalysisItem { Id = questionId, Text = "Qual è il limite righe per ordine?", Classification = DocumentAnalysisClassification.Inferred, EvidenceRefs = [] };
        var content = input.Ai with { OpenQuestions = [question] };
        return input with { Ai = content, Target = content };
    }

    private static (int, int, int, int, int) Counters(DocumentCoverageSummary3 s) => (s.ExpectedGaps, s.AssessedGaps, s.AnsweredGaps, s.OpenGaps, s.UnassessedGaps);

    [Fact]
    public void A_reported_gap_needs_no_substantive_item_and_the_five_counters_keep_their_invariants_apart_from_item_support()
    {
        var input = Build(
            new Spec("Il cliente A emette ordini."),
            new Spec("Qual è il limite righe per ordine?", Linked: false, Kind: EvidenceKind.Gap),
            new Spec("Quale canale usa il corriere C?", Linked: false, Kind: EvidenceKind.Gap),
            new Spec("Quale tariffa applica il corriere D?", Linked: false, Kind: EvidenceKind.Gap));
        var (answered, open, missing) = (input.Evidence[1], input.Evidence[2], input.Evidence[3]);
        var question = Guid.NewGuid();
        input = WithOpenQuestion(input, question) with
        {
            Gaps = [Record(answered, support: [input.Evidence[0].EvidenceId]), Record(open, GapOutcome.Open, GapReason.NoAnswer, openItem: question)],
        };

        var findings = DocumentCoverage.Check(input);
        var summary = DocumentCoverage.SummarizeGaps(input, findings);

        // Il solo Gap senza record resta evidence_unrepresented; i due rendicontati no.
        var unrepresented = Assert.Single(findings, f => f.Code == "evidence_unrepresented");
        Assert.Equal(missing.EvidenceId, unrepresented.EvidenceId);
        Assert.True(unrepresented.Blocking);
        Assert.Equal((3, 2, 1, 1, 1), Counters(summary));
        Assert.Equal(summary.ExpectedGaps, summary.AssessedGaps + summary.UnassessedGaps);
        Assert.Equal(summary.AssessedGaps, summary.AnsweredGaps + summary.OpenGaps);
        // Distinti dal supporto item: le evidenze rappresentate da item restano quelle del coverage-2 (1 su 4), i Gap non vi si sommano.
        Assert.Equal((4, 1), (summary.ExpectedEvidence, summary.RepresentedEvidence));
        Assert.Equal(1, summary.DeterministicErrors);
    }

    [Fact]
    public void The_unrepresented_rule_is_unchanged_for_gaps_without_a_record_and_for_every_other_kind()
    {
        var input = Build(new Spec("Il cliente A emette ordini."), new Spec("Qual è il limite righe per ordine?", Linked: false, Kind: EvidenceKind.Gap));
        var gap = input.Evidence[1];

        // coverage-2 (Gaps null): nessuna eccezione per i Gap.
        Assert.Equal("evidence_unrepresented", Assert.Single(DocumentCoverage.Check(input)).Code);
        // coverage-3 con lista vuota (nessun record per questo Gap): resta non rendicontato, quindi ancora errore e unassessed.
        var empty = input with { Gaps = [] };
        Assert.Equal("evidence_unrepresented", Assert.Single(DocumentCoverage.Check(empty)).Code);
        Assert.Equal((1, 0, 0, 0, 1), Counters(DocumentCoverage.SummarizeGaps(empty, DocumentCoverage.Check(empty))));
        // Con il record il Gap è rendicontato.
        Assert.Empty(DocumentCoverage.Check(input with { Gaps = [Record(gap, support: [input.Evidence[0].EvidenceId])] }));

        // Un record su un'evidenza NON Gap non la rende rappresentata né la conta fra i Gap attesi.
        var requirement = Build(new Spec("Il cliente A emette ordini.", Linked: false));
        requirement = requirement with { Gaps = [Record(requirement.Evidence[0])] };
        Assert.Equal("evidence_unrepresented", Assert.Single(DocumentCoverage.Check(requirement)).Code);
        Assert.Equal((0, 0, 0, 0, 0), Counters(DocumentCoverage.SummarizeGaps(requirement, DocumentCoverage.Check(requirement))));
    }

    [Fact]
    public void A_v5_revision_without_any_gap_has_an_empty_list_and_zero_counters()
    {
        var input = Build(new Spec("Il cliente A emette ordini.")) with { Gaps = [] };

        var summary = DocumentCoverage.SummarizeGaps(input, DocumentCoverage.Check(input));

        Assert.Equal((0, 0, 0, 0, 0), Counters(summary));
        Assert.Equal((1, 1), (summary.ExpectedEvidence, summary.RepresentedEvidence));
        Assert.Throws<ArgumentException>(() => DocumentCoverage.SummarizeGaps(Build(new Spec("Il cliente A emette ordini.")), []));
    }

    [Fact]
    public void A_pm_edit_that_removes_the_open_item_or_conflict_is_a_distinct_pm_assessment_and_the_ai_record_is_never_rewritten()
    {
        var input = Build(new Spec("Qual è il limite righe per ordine?", Linked: false, Kind: EvidenceKind.Gap), new Spec("Quale canale usa il corriere C?", Linked: false, Kind: EvidenceKind.Gap));
        var (gapQuestion, gapConflict) = (input.Evidence[0], input.Evidence[1]);
        var question = Guid.NewGuid();
        var conflict = new DocumentAnalysisConflict { Id = Guid.NewGuid(), Description = "Canale discordante", Status = AnalysisConflictStatus.Open, Resolution = null, Alternatives = [] };
        var aiContent = WithOpenQuestion(input, question).Ai with { Conflicts = [conflict] };
        IReadOnlyList<GapAssessmentRecord> records =
        [
            Record(gapQuestion, GapOutcome.Open, GapReason.Ambiguous, [Guid.NewGuid()], openItem: question),
            Record(gapConflict, GapOutcome.Open, GapReason.Partial, [Guid.NewGuid()], openConflict: conflict.Id),
        ];
        // La domanda aperta è anche collegata al Gap: senza la regola dedicata la sua rimozione sarebbe un generico item_removed_by_pm.
        var untouched = input with { Ai = aiContent, Target = aiContent, Gaps = records, Links = [(question, gapQuestion.EvidenceId)] };

        Assert.DoesNotContain(DocumentCoverage.Check(untouched), f => f.Category == FindingCategory.PmAssessment);

        var edited = untouched with { Target = aiContent with { OpenQuestions = [], Conflicts = [] } };
        var findings = DocumentCoverage.Check(edited);

        var pm = findings.Where(f => f.Category == FindingCategory.PmAssessment).ToList();
        Assert.Equal(2, pm.Count);
        Assert.All(pm, f =>
        {
            Assert.Equal("gap_open_reference_removed_by_pm", f.Code);
            Assert.False(f.Blocking);
        });
        Assert.Equal((gapQuestion.EvidenceId, question.ToString()), (pm[0].EvidenceId!.Value, pm[0].Details));
        Assert.Equal((gapConflict.EvidenceId, conflict.Id.ToString()), (pm[1].EvidenceId!.Value, pm[1].Details));
        // I record AI e i contatori non cambiano per l'edit; nessun item_removed_by_pm doppio e nessun evidence_unrepresented per i Gap rendicontati.
        Assert.Same(records, edited.Gaps);
        Assert.DoesNotContain(findings, f => f.Code is "item_removed_by_pm" or "evidence_unrepresented");
        var summary = DocumentCoverage.SummarizeGaps(edited, findings);
        Assert.Equal((2, 2, 0, 2, 0), Counters(summary));
        Assert.Equal(2, summary.PmAssessments);
        Assert.Equal(0, summary.BlockingFindings);

        // Un conflitto ancora presente ma risolto dal PM è già «conflict_resolved_by_pm», non una rimozione.
        var resolution = new AnalysisConflictResolution { SelectedAlternativeIds = [], Reason = "deciso", EvidenceRefs = [] };
        var resolved = untouched with { Target = aiContent with { Conflicts = [conflict with { Status = AnalysisConflictStatus.Resolved, Resolution = resolution }] } };
        Assert.DoesNotContain(DocumentCoverage.Check(resolved), f => f.Code == "gap_open_reference_removed_by_pm" && f.Details == conflict.Id.ToString());
    }

    [Fact]
    public void The_coverage_3_input_hash_covers_the_gap_assessments_hash_and_is_never_equal_to_the_coverage_2_hash()
    {
        var baseline = new DocumentCoverageHashes("s", "v", ["p1"], ["x1"], "r", "d");
        var links = new[] { (Guid.NewGuid(), Guid.NewGuid()), (Guid.NewGuid(), Guid.NewGuid()) };
        var gapHash = new string('a', 64);
        var hash = DocumentCoverage.InputHash(baseline, links, gapHash);

        Assert.Equal("coverage-3", DocumentCoverage.CheckerVersionV3);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, DocumentCoverage.InputHash(baseline, [links[1], links[0]], gapHash));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline, links, new string('b', 64)));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline with { RevisionHash = "r2" }, links, gapHash));
        Assert.NotEqual(DocumentCoverage.InputHash(baseline, links), hash);
        Assert.Throws<ArgumentException>(() => DocumentCoverage.InputHash(baseline, links, string.Empty));
        Assert.Contains(DocumentCoverage.LimitsV3, l => l.Contains("snapshot", StringComparison.OrdinalIgnoreCase) && l.Contains("Gap", StringComparison.Ordinal));
        Assert.Equal(DocumentCoverage.Limits.Count + 1, DocumentCoverage.LimitsV3.Count);
    }

    // ---------------------------------------------------------------- supporto

    private sealed record Spec(
        string Statement, EvidencePolarity Polarity = EvidencePolarity.Affirmed, string[]? Qualifiers = null, bool Linked = true, string? ItemText = null,
        EvidenceKind Kind = EvidenceKind.Requirement);

    private static DocumentCoverageInput Build(params Spec[] specs)
    {
        var evidence = new List<DocumentCoverageEvidence>();
        var texts = new Dictionary<Guid, string>();
        var sources = new List<DocumentAnalysisSource>();
        var items = new List<DocumentAnalysisItem>();
        var links = new List<(Guid, Guid)>();
        var document = Guid.NewGuid();
        var (version, preparation, artifact) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        for (var i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            var (evidenceId, block, source) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            texts[block] = spec.Statement;
            evidence.Add(new(evidenceId, $"E{i + 1}", EvidenceOriginKind.Document, null, spec.Kind, spec.Polarity, spec.Statement,
                [.. (spec.Qualifiers ?? []).Select(q => new CoverageQualifier("Maximum", q))], null, null,
                [new(source, null, block, 0, spec.Statement.Length, spec.Statement)]));
            sources.Add(new DocumentAnalysisSource
            {
                Id = source, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = document,
                DocumentVersionId = version, PreparationId = preparation, ArtifactId = artifact, BlockId = block, Start = 0, End = spec.Statement.Length, Quote = spec.Statement,
            });
            if (spec.Linked)
            {
                var item = new DocumentAnalysisItem
                {
                    Id = Guid.NewGuid(), Text = spec.ItemText ?? spec.Statement, Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [source],
                };
                items.Add(item);
                links.Add((item.Id, evidenceId));
            }
        }

        var content = new DocumentAnalysisContent
        {
            SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), SourceCutoffSequence = 0,
            ValidationState = AnalysisContentState.Unvalidated, Sources = sources, Goals = [],
            ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = items, OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = [],
        };
        return new DocumentCoverageInput(evidence, links, content, content, null, texts, [], []);
    }

    private static string Locate(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var backend = Path.Combine(dir.FullName, "backend", relative);
            if (File.Exists(backend))
            {
                return backend;
            }
        }

        throw new FileNotFoundException(relative);
    }
}

/// <summary>
/// T062 (PG) — API reale + PostgreSQL: coverage-2 sul report di analisi con revisione 1.1 e GET di preparazione/coverage; i report coverage-v1
/// storici restano leggibili e immutati. Dati documentali scritti direttamente (preparazione, blocchi, porzioni, evidenze, selezione, revisione):
/// nessun modello, nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentCoverageEndpointTests : ArchiveTestBase
{
    private const string TextA = "Il cliente A emette al massimo 5 ordini al giorno.";
    private const string TextB = "Il magazzino B chiude alle 18.";

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;
        options.ProjectQuotaBytes = 50_000;
    }

    // ================================================================ GET preparazione/coverage

    [Fact]
    public async Task The_preparation_coverage_reports_expected_processed_no_evidence_and_missing_portions_and_hashes_without_side_effects()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed, PortionStatus.Completed, PortionStatus.Pending], withAnalysis: false);
        var jobs = await Count("SELECT count(*) FROM \"DocumentJobs\"");
        var reports = await Count("SELECT count(*) FROM \"EvidenceCoverageReports\"");
        var url = $"/api/projects/{w.Project}/documents/{w.Document}/versions/{w.Version}/preparations/latest/coverage";

        var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("coverage-2", body.GetProperty("checkerVersion").GetString());
        Assert.Equal(w.Preparation, body.GetProperty("preparationId").GetGuid());
        var portions = body.GetProperty("portions");
        Assert.Equal((3, 2, 1, 1), (portions.GetProperty("expected").GetInt32(), portions.GetProperty("processed").GetInt32(), portions.GetProperty("noEvidence").GetInt32(), portions.GetProperty("missing").GetInt32()));
        Assert.Equal(w.PendingPortion, Assert.Single(portions.GetProperty("missingPortionIds").EnumerateArray()).GetGuid());
        var hashes = body.GetProperty("hashes");
        Assert.Equal(new string('1', 64), hashes.GetProperty("manifestHash").GetString());
        Assert.Equal(w.ParseSha, hashes.GetProperty("parseSha256").GetString());
        Assert.Equal(w.EvidenceVersion, hashes.GetProperty("evidenceVersionId").GetGuid());
        var finding = Assert.Single(body.GetProperty("findings").EnumerateArray());
        Assert.Equal(("DeterministicError", "portion_not_processed", true), (finding.GetProperty("category").GetString(), finding.GetProperty("code").GetString(), finding.GetProperty("blocking").GetBoolean()));
        Assert.DoesNotMatch("(?i)\"(is)?complete", body.GetRawText());

        Assert.Equal(jobs, await Count("SELECT count(*) FROM \"DocumentJobs\""));
        Assert.Equal(reports, await Count("SELECT count(*) FROM \"EvidenceCoverageReports\""));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{await NewProject(client)}/documents/{w.Document}/versions/{w.Version}/preparations/latest/coverage", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{w.Project}/documents/{w.Document}/versions/{w.Version}/preparations/99/coverage", Ct)).StatusCode);
    }

    // ================================================================ coverage-2 sul report di analisi

    [Fact]
    public async Task A_known_omission_is_a_blocking_deterministic_error_in_the_persisted_coverage_2_report_with_exact_input_hashes()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed], linkB: false);

        var created = await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var report = await Json(created);
        Assert.Equal("coverage-2", report.GetProperty("checkerVersion").GetString());
        var counts = report.GetProperty("counts");
        Assert.Equal((1, 0, 0), (counts.GetProperty("deterministicErrors").GetInt32(), counts.GetProperty("semanticRisks").GetInt32(), counts.GetProperty("pmAssessments").GetInt32()));
        var finding = Assert.Single(report.GetProperty("findings").EnumerateArray());
        Assert.Equal(("evidence_unrepresented", true), (finding.GetProperty("code").GetString(), finding.GetProperty("blocking").GetBoolean()));
        Assert.Equal(w.EvidenceB, finding.GetProperty("evidenceId").GetGuid());
        var summary = report.GetProperty("summary");
        Assert.Equal((2, 1, 1), (summary.GetProperty("expectedEvidence").GetInt32(), summary.GetProperty("representedEvidence").GetInt32(), summary.GetProperty("blockingFindings").GetInt32()));
        var hashes = report.GetProperty("hashes");
        Assert.Equal(await Scalar<string>("SELECT \"Hash\" FROM \"SourceSnapshots\" WHERE \"Id\" = (SELECT \"SourceSnapshotId\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r)", ("r", w.Run)), hashes.GetProperty("sourceSnapshotHash").GetString());
        Assert.Equal(w.ParseSha, Assert.Single(hashes.GetProperty("parseHashes").EnumerateArray()).GetString());
        Assert.Matches("^[0-9a-f]{64}$", hashes.GetProperty("revisionHash").GetString()!);
        Assert.Matches("^[0-9a-f]{64}$", report.GetProperty("inputHash").GetString()!);
        Assert.Single(report.GetProperty("preparations").EnumerateArray());
        Assert.DoesNotMatch("(?i)\"(is)?complete", report.GetRawText());

        var again = await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(report.GetRawText(), (await Json(again)).GetRawText());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"EvidenceCoverageReports\" WHERE \"AnalysisId\" = @a", ("a", w.Run)));
        var detail = await client.GetFromJsonAsync<JsonElement>(ReportsUrl(w) + "/" + report.GetProperty("id").GetGuid(), Ct);
        Assert.Equal(report.GetRawText(), detail.GetRawText());
    }

    [Fact]
    public async Task A_semantically_wrong_gold_with_every_evidence_linked_is_reported_as_a_semantic_risk_apart_from_the_errors()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed], itemTextA: "Il cliente A emette al massimo 8 ordini al giorno.");

        var report = await Json(await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct));

        var counts = report.GetProperty("counts");
        Assert.Equal((0, 1, 0), (counts.GetProperty("deterministicErrors").GetInt32(), counts.GetProperty("semanticRisks").GetInt32(), counts.GetProperty("pmAssessments").GetInt32()));
        var summary = report.GetProperty("summary");
        Assert.Equal((2, 2, 0), (summary.GetProperty("expectedEvidence").GetInt32(), summary.GetProperty("representedEvidence").GetInt32(), summary.GetProperty("blockingFindings").GetInt32()));
        var finding = Assert.Single(report.GetProperty("findings").EnumerateArray());
        Assert.Equal(("SemanticRisk", "qualifier_changed", false), (finding.GetProperty("category").GetString(), finding.GetProperty("code").GetString(), finding.GetProperty("blocking").GetBoolean()));
        Assert.Equal("medium", finding.GetProperty("confidence").GetString());
    }

    // ================================================================ riconciliazione gerarchica (T081)

    [Fact]
    public async Task The_report_lists_levels_pages_open_candidates_blocking_patches_and_frontier_with_ids_and_reasons_and_preserves_open_alternatives()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed], openConflict: true);
        var (done, open, stuck) = (
            ReconciliationFixtures.Candidate(),
            ReconciliationFixtures.Candidate(CandidateKind.Relation, ReconcileProposedType.Contradicts),
            ReconciliationFixtures.Candidate(CandidateKind.Relation, ReconcileProposedType.Qualifies, swap: true));
        var applied = ReconciliationFixtures.AddSupport();
        var hash3 = new string('3', 64);
        using (var scope = App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var c in new[] { done, open, stuck })
            {
                db.AddRange(ReconciliationProjection.Candidate(w.Project, w.Run, ReconciliationFixtures.SourceHash, new string('d', 64), c with { OriginBatchIds = [] }, new Dictionary<string, string>()));
            }

            db.Add(ReconciliationProjection.Patch(w.Project, w.Run, ReconciliationFixtures.SourceHash, applied, [new string('1', 64)]));
            await db.SaveChangesAsync(Ct);
            db.AddRange(
                new RunCandidateDisposition { RunId = w.Run, CandidateId = done.CandidateId, Version = 1, State = CandidateState.Pending, ChildCandidateIdsJson = "[]", ComparisonOutputHashesJson = "[]" },
                new RunCandidateDisposition { RunId = w.Run, CandidateId = done.CandidateId, Version = 2, State = CandidateState.Compared, ChildCandidateIdsJson = "[]", ComparisonOutputHashesJson = $"[\"{hash3}\"]" },
                new RunCandidateDisposition { RunId = w.Run, CandidateId = open.CandidateId, Version = 1, State = CandidateState.Open, ChildCandidateIdsJson = "[]", ComparisonOutputHashesJson = $"[\"{hash3}\"]", Reason = "orario discordante" },
                new RunCandidateDisposition { RunId = w.Run, CandidateId = stuck.CandidateId, Version = 1, State = CandidateState.NotProcessed, ChildCandidateIdsJson = "[]", ComparisonOutputHashesJson = "[]", Reason = "limite di chiamate" },
                new RunPatchDisposition { RunId = w.Run, PatchId = applied.PatchId, Version = 1, Kind = RunPatchDispositionKind.Stale, Reason = "base cambiata" });
            await db.SaveChangesAsync(Ct);
        }

        var report = await Json(await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct));

        var rec = report.GetProperty("reconciliation");
        Assert.True(rec.GetProperty("projected").GetBoolean());
        var candidates = rec.GetProperty("candidates").EnumerateArray().ToDictionary(c => c.GetProperty("candidateId").GetString()!);
        Assert.Equal(3, candidates.Count);
        Assert.Equal(("Compared", true), (candidates[done.CandidateId].GetProperty("state").GetString(), candidates[done.CandidateId].GetProperty("terminal").GetBoolean()));
        Assert.Equal(("Open", "orario discordante", true), (candidates[open.CandidateId].GetProperty("state").GetString(), candidates[open.CandidateId].GetProperty("reason").GetString(), candidates[open.CandidateId].GetProperty("terminal").GetBoolean()));
        Assert.Equal(("NotProcessed", "limite di chiamate", false), (candidates[stuck.CandidateId].GetProperty("state").GetString(), candidates[stuck.CandidateId].GetProperty("reason").GetString(), candidates[stuck.CandidateId].GetProperty("terminal").GetBoolean()));
        var frontier = Assert.Single(rec.GetProperty("frontier").EnumerateArray());
        Assert.Equal((stuck.CandidateId, "limite di chiamate"), (frontier.GetProperty("candidateId").GetString(), frontier.GetProperty("reason").GetString()));
        var patch = Assert.Single(rec.GetProperty("patches").EnumerateArray());
        Assert.Equal((applied.PatchId, "Stale", "base cambiata", true), (patch.GetProperty("patchId").GetString(), patch.GetProperty("state").GetString(), patch.GetProperty("reason").GetString(), patch.GetProperty("blocking").GetBoolean()));
        var point = Assert.Single(rec.GetProperty("openPoints").EnumerateArray());
        Assert.Equal("Orario di chiusura discordante", point.GetProperty("description").GetString());
        Assert.Equal(["Il magazzino B chiude alle 18.", "Il magazzino B chiude alle 20 il venerdi."], point.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("text").GetString()));

        var blocking = report.GetProperty("findings").EnumerateArray().Where(f => f.GetProperty("blocking").GetBoolean()).ToList();
        Assert.Equal(["candidate_not_terminal", "patch_blocking"], blocking.Select(f => f.GetProperty("code").GetString()).Order());
        Assert.Contains(blocking, f => f.GetProperty("details").GetString() == stuck.CandidateId && f.GetProperty("message").GetString()!.Contains("limite di chiamate"));
        Assert.Contains(blocking, f => f.GetProperty("details").GetString() == applied.PatchId && f.GetProperty("message").GetString()!.Contains("base cambiata"));
        Assert.Equal(2, report.GetProperty("summary").GetProperty("blockingFindings").GetInt32());
        Assert.DoesNotMatch("(?i)\"(is)?complete", report.GetRawText());
    }

    [Fact]
    public async Task A_report_without_hierarchical_projections_says_so_and_adds_no_reconciliation_finding()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed]);

        var report = await Json(await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct));

        var rec = report.GetProperty("reconciliation");
        Assert.False(rec.GetProperty("projected").GetBoolean());
        Assert.Empty(rec.GetProperty("candidates").EnumerateArray());
        Assert.Empty(rec.GetProperty("patches").EnumerateArray());
        Assert.Empty(rec.GetProperty("frontier").EnumerateArray());
        Assert.Empty(rec.GetProperty("openPoints").EnumerateArray());
        Assert.Empty(report.GetProperty("findings").EnumerateArray());
    }

    // ================================================================ storico coverage-v1

    [Fact]
    public async Task Historic_coverage_v1_reports_stay_readable_and_byte_identical_next_to_coverage_2_and_001_analyses_keep_using_v1()
    {
        using var client = await App.LoginAsync();
        var old = await SeededAnalysisFactory.CreateAsync(client, App.Services);
        var oldUrl = $"/api/projects/{old.Project}/analyses/{old.Analysis}/coverage-reports";
        var v1 = await Json(await client.PostAsJsonAsync(oldUrl, new { }, Ct));
        Assert.Equal("coverage-v1", v1.GetProperty("checkerVersion").GetString());
        Assert.False(v1.TryGetProperty("summary", out _));
        Assert.False(v1.TryGetProperty("hashes", out _));
        var digest = await SqlDigest("coverage-v1");

        var w = await SeedAsync(client, [PortionStatus.Completed]);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct)).StatusCode);

        Assert.Equal(digest, await SqlDigest("coverage-v1"));
        var detail = await client.GetFromJsonAsync<JsonElement>(oldUrl + "/" + v1.GetProperty("id").GetGuid(), Ct);
        Assert.Equal(v1.GetRawText(), detail.GetRawText());
        var stored = await Scalar<string>("SELECT \"Report\"::text FROM \"EvidenceCoverageReports\" WHERE \"CheckerVersion\" = 'coverage-v1'");
        Assert.Equal("coverage-v1", EvidenceJson.Deserialize<CoverageReportContent>(stored).CheckerVersion);
        var list = await client.GetFromJsonAsync<JsonElement>(oldUrl, Ct);
        Assert.Equal("coverage-v1", Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("checkerVersion").GetString());
        var newList = await client.GetFromJsonAsync<JsonElement>(ReportsUrl(w), Ct);
        Assert.Equal("coverage-2", Assert.Single(newList.GetProperty("items").EnumerateArray()).GetProperty("checkerVersion").GetString());
    }

    // ================================================================ coverage-3: valutazioni Gap single-call v5 (002 T139)

    private static readonly GapSpec Answered = new(GapOutcome.AnsweredInSnapshot, GapReason.Answered, SupportA: true);
    private static readonly GapSpec OpenQuestion = new(GapOutcome.Open, GapReason.NoAnswer, Ref: GapRef.Question);
    private static readonly GapSpec OpenConflict = new(GapOutcome.Open, GapReason.Partial, SupportA: true, Ref: GapRef.Conflict);

    private Task<string> WorkDigest() => Scalar<string>(
        "SELECT coalesce(md5((SELECT string_agg(t::text, '|' ORDER BY t::text) FROM \"WorkOutputs\" t) || (SELECT string_agg(m::text, '|' ORDER BY m::text) FROM \"WorkManifests\" m)), '-')");

    [Fact]
    public async Task A_single_call_v5_revision_with_gaps_gets_coverage_3_with_the_exact_assessments_hashes_and_provenance()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed], v5: V5Spec.Single(Answered, OpenQuestion, OpenConflict));
        var v5 = w.V5!;
        var work = await WorkDigest();

        var created = await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var report = await Json(created);
        Assert.Equal("coverage-3", report.GetProperty("checkerVersion").GetString());
        Assert.Equal(v5.AiRevision, report.GetProperty("aiRevisionId").GetGuid());
        Assert.Empty(report.GetProperty("findings").EnumerateArray());
        var summary = report.GetProperty("summary");
        Assert.Equal((3, 3, 1, 2, 0), (summary.GetProperty("expectedGaps").GetInt32(), summary.GetProperty("assessedGaps").GetInt32(), summary.GetProperty("answeredGaps").GetInt32(),
            summary.GetProperty("openGaps").GetInt32(), summary.GetProperty("unassessedGaps").GetInt32()));
        // Distinti dal supporto item: 5 evidenze attese (2 requisiti + 3 Gap), solo i 2 requisiti rappresentati da item.
        Assert.Equal((5, 2, 0, 0), (summary.GetProperty("expectedEvidence").GetInt32(), summary.GetProperty("representedEvidence").GetInt32(),
            summary.GetProperty("deterministicErrors").GetInt32(), summary.GetProperty("blockingFindings").GetInt32()));

        var gaps = report.GetProperty("gapAssessments").EnumerateArray().ToList();
        Assert.Equal(w.V5!.Gaps, gaps.Select(g => g.GetProperty("gapEvidenceId").GetGuid()));
        Assert.All(gaps, g =>
        {
            Assert.Equal(w.EvidenceVersion, g.GetProperty("gapEvidenceVersionId").GetGuid());
            Assert.Equal(v5.CatalogSource, g.GetProperty("sourceSnapshotHash").GetString());
            Assert.Equal(v5.BatchId, g.GetProperty("originBatchId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(g.GetProperty("rationale").GetString()));
        });
        Assert.Equal(("AnsweredInSnapshot", "Answered", JsonValueKind.Null, JsonValueKind.Null), (gaps[0].GetProperty("outcome").GetString(), gaps[0].GetProperty("reason").GetString(),
            gaps[0].GetProperty("openItemId").ValueKind, gaps[0].GetProperty("openConflictId").ValueKind));
        Assert.Equal([w.EvidenceA], gaps[0].GetProperty("supportingEvidenceIds").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal(("Open", "NoAnswer", v5.Questions[0]), (gaps[1].GetProperty("outcome").GetString(), gaps[1].GetProperty("reason").GetString(), gaps[1].GetProperty("openItemId").GetGuid()));
        Assert.Empty(gaps[1].GetProperty("supportingEvidenceIds").EnumerateArray());
        Assert.Equal(("Open", "Partial", v5.Conflict), (gaps[2].GetProperty("outcome").GetString(), gaps[2].GetProperty("reason").GetString(), gaps[2].GetProperty("openConflictId").GetGuid()));
        Assert.Equal(v5.GapHash, report.GetProperty("gapAssessmentsHash").GetString());

        // Identità input: copre le valutazioni (hash) ed è la funzione dei soli hash/link esposti.
        var hashes = report.GetProperty("hashes");
        var links = await LinksOf(w);
        var expected = DocumentCoverage.InputHash(
            new(hashes.GetProperty("sourceSnapshotHash").GetString()!, hashes.GetProperty("viewHash").GetString()!, [.. hashes.GetProperty("parseHashes").EnumerateArray().Select(x => x.GetString()!)],
                [.. hashes.GetProperty("extractionHashes").EnumerateArray().Select(x => x.GetString()!)], hashes.GetProperty("revisionHash").GetString()!, null), links, v5.GapHash);
        Assert.Equal(expected, report.GetProperty("inputHash").GetString());
        Assert.DoesNotMatch("(?i)\"(is)?complete", report.GetRawText());

        // Idempotenza per CheckerVersion; dettaglio e lista; lo store dei lavori non è toccato.
        var again = await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(report.GetRawText(), (await Json(again)).GetRawText());
        var detail = await client.GetFromJsonAsync<JsonElement>(ReportsUrl(w) + "/" + report.GetProperty("id").GetGuid(), Ct);
        Assert.Equal(report.GetRawText(), detail.GetRawText());
        var list = await client.GetFromJsonAsync<JsonElement>(ReportsUrl(w), Ct);
        Assert.Equal("coverage-3", Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("checkerVersion").GetString());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"EvidenceCoverageReports\" WHERE \"AnalysisId\" = @a AND \"CheckerVersion\" = 'coverage-3'", ("a", w.Run)));
        var stored = await Scalar<string>("SELECT \"Report\"::text FROM \"EvidenceCoverageReports\" WHERE \"AnalysisId\" = @a", ("a", w.Run));
        Assert.Equal("coverage-3", EvidenceJson.Deserialize<DocumentCoverage3Content>(stored).CheckerVersion);
        Assert.Equal(work, await WorkDigest());
    }

    private async Task<IReadOnlyCollection<(Guid, Guid)>> LinksOf(World w)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.AnalysisEvidenceLinks.AsNoTracking().Where(l => l.RunId == w.Run).Select(l => new { l.ItemId, l.EvidenceId }).ToListAsync(Ct)).Select(l => (l.ItemId, l.EvidenceId)).ToList();
    }

    [Fact]
    public async Task A_single_call_v5_revision_without_any_gap_still_gets_coverage_3_with_an_empty_list_and_zero_counters()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed], v5: V5Spec.Single());

        var created = await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var report = await Json(created);
        Assert.Equal("coverage-3", report.GetProperty("checkerVersion").GetString());
        Assert.Empty(report.GetProperty("gapAssessments").EnumerateArray());
        Assert.Equal(GapAssessmentRules.Hash([]), report.GetProperty("gapAssessmentsHash").GetString());
        var summary = report.GetProperty("summary");
        foreach (var counter in new[] { "expectedGaps", "assessedGaps", "answeredGaps", "openGaps", "unassessedGaps" })
        {
            Assert.Equal(0, summary.GetProperty(counter).GetInt32());
        }

        Assert.Equal((2, 2), (summary.GetProperty("expectedEvidence").GetInt32(), summary.GetProperty("representedEvidence").GetInt32()));
    }

    [Fact]
    public async Task A_gap_without_a_record_in_a_v5_revision_stays_unassessed_and_a_blocking_unrepresented_error_while_a_reported_one_is_not()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed], v5: V5Spec.Single(Answered, OpenQuestion with { Assessed = false }));

        var report = await Json(await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct));

        var summary = report.GetProperty("summary");
        Assert.Equal((2, 1, 1, 0, 1), (summary.GetProperty("expectedGaps").GetInt32(), summary.GetProperty("assessedGaps").GetInt32(), summary.GetProperty("answeredGaps").GetInt32(),
            summary.GetProperty("openGaps").GetInt32(), summary.GetProperty("unassessedGaps").GetInt32()));
        var finding = Assert.Single(report.GetProperty("findings").EnumerateArray());
        Assert.Equal(("evidence_unrepresented", true, w.V5!.Gaps[1]), (finding.GetProperty("code").GetString(), finding.GetProperty("blocking").GetBoolean(), finding.GetProperty("evidenceId").GetGuid()));
        Assert.Equal(1, summary.GetProperty("blockingFindings").GetInt32());
    }

    [Fact]
    public async Task A_hierarchical_v5_revision_without_gaps_keeps_coverage_2_and_never_gets_a_server_made_assessment_or_hash()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed], v5: V5Spec.Hierarchical());
        // La sola definizione v5 del run non decide: contano algoritmo e schema dei WorkOutput.
        await Execute("UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = 'project-analysis-documents-v5' WHERE \"Id\" = @r", ("r", w.Run));

        var created = await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var report = await Json(created);
        Assert.Equal("coverage-2", report.GetProperty("checkerVersion").GetString());
        Assert.False(report.TryGetProperty("gapAssessments", out _));
        Assert.False(report.TryGetProperty("gapAssessmentsHash", out _));
        Assert.False(report.TryGetProperty("aiRevisionId", out _));
        Assert.False(report.GetProperty("summary").TryGetProperty("expectedGaps", out _));
        var stored = await Scalar<string>("SELECT \"Report\"::text FROM \"EvidenceCoverageReports\" WHERE \"AnalysisId\" = @a", ("a", w.Run));
        Assert.DoesNotContain("gapAssessments", stored, StringComparison.Ordinal);
        Assert.Equal("coverage-2", await Scalar<string>("SELECT \"CheckerVersion\" FROM \"EvidenceCoverageReports\" WHERE \"AnalysisId\" = @a", ("a", w.Run)));
    }

    [Fact]
    public async Task Historical_single_call_v2_and_runs_without_work_data_keep_coverage_2_even_with_an_alias_1_output()
    {
        using var client = await App.LoginAsync();
        var historic = await SeedAsync(client, [PortionStatus.Completed], v5: V5Spec.HistoricSingle());
        var plain = await SeedAsync(client, [PortionStatus.Completed]);

        foreach (var w in new[] { historic, plain })
        {
            var report = await Json(await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct));
            Assert.Equal("coverage-2", report.GetProperty("checkerVersion").GetString());
            Assert.False(report.TryGetProperty("gapAssessments", out _));
        }
    }

    [Theory]
    [InlineData("analysis-single-v3", "reconcile-alias-1")]        // algoritmo single-call v5 ma WorkOutput gerarchico
    [InlineData("global-reconcile-v1", "document-analysis-alias-2")] // algoritmo gerarchico ma WorkOutput single-call v5
    [InlineData("analysis-single-v2", "document-analysis-alias-2")]  // storico con output v5
    [InlineData("analysis-single-v3", null)]                       // single-call v5 senza alcun WorkOutput
    public async Task An_inconsistent_origin_of_the_ai_revision_is_a_conflict_and_never_a_report(string algorithm, string? schema)
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed], v5: new V5Spec(algorithm, schema, []));

        var response = await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"EvidenceCoverageReports\" WHERE \"AnalysisId\" = @a", ("a", w.Run)));
    }

    [Theory]
    [InlineData("tampered-hash")]
    [InlineData("dangling-support")]
    [InlineData("missing-open-question")]
    public async Task An_incoherent_gap_payload_prevents_the_report_instead_of_producing_a_green_empty_list(string defect)
    {
        using var client = await App.LoginAsync();
        var spec = V5Spec.Single(Answered, OpenQuestion);
        spec = defect switch
        {
            "tampered-hash" => spec with { TamperGapHash = true },
            "dangling-support" => spec with { DanglingSupport = true },
            _ => spec with { OmitQuestionFromRevision = true },
        };
        var w = await SeedAsync(client, [PortionStatus.Completed], v5: spec);

        var response = await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"EvidenceCoverageReports\" WHERE \"AnalysisId\" = @a", ("a", w.Run)));
    }

    [Fact]
    public async Task A_pm_edit_removing_the_open_references_is_a_distinct_pm_assessment_and_the_original_ai_assessments_and_old_report_stay_untouched()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed], v5: V5Spec.Single(Answered, OpenQuestion, OpenConflict));
        var v5 = w.V5!;
        var original = await Json(await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct));
        var digest = await SqlDigest("coverage-3");
        var work = await WorkDigest();
        var pmRevision = Guid.NewGuid();
        var edited = v5.AiContent with { OpenQuestions = [], Conflicts = [] };
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AnalysisRevisions.Add(new AnalysisRevision
            {
                Id = pmRevision, AnalysisId = w.Run, Version = 2, ParentRevisionId = v5.AiRevision, Origin = AnalysisRevisionOrigin.PmEdit, CreatedBy = v5.User, CreatedAt = DateTimeOffset.UtcNow,
                SchemaVersion = AnalysisRevision.DocumentSchemaVersion, ContentJson = DocumentAnalysisContentJson.Serialize(edited), ValidationState = AnalysisValidationState.Unvalidated,
            });
            await db.SaveChangesAsync(Ct);
        }

        var response = await client.PostAsJsonAsync(ReportsUrl(w), new { revisionId = pmRevision }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var report = await Json(response);
        Assert.Equal("coverage-3", report.GetProperty("checkerVersion").GetString());
        Assert.Equal((v5.AiRevision, pmRevision), (report.GetProperty("aiRevisionId").GetGuid(), report.GetProperty("revisionId").GetGuid()));
        var pm = report.GetProperty("findings").EnumerateArray().Where(f => f.GetProperty("category").GetString() == "PmAssessment").ToList();
        Assert.Equal(2, pm.Count);
        Assert.All(pm, f => Assert.Equal(("gap_open_reference_removed_by_pm", false), (f.GetProperty("code").GetString(), f.GetProperty("blocking").GetBoolean())));
        Assert.Equal([v5.Questions[0].ToString(), v5.Conflict.ToString()], pm.Select(f => f.GetProperty("details").GetString()!));
        Assert.Equal(2, report.GetProperty("counts").GetProperty("pmAssessments").GetInt32());
        // Le valutazioni AI e i contatori sono quelli della revisione originaria, byte per byte.
        Assert.Equal(original.GetProperty("gapAssessments").GetRawText(), report.GetProperty("gapAssessments").GetRawText());
        Assert.Equal(original.GetProperty("gapAssessmentsHash").GetString(), report.GetProperty("gapAssessmentsHash").GetString());
        foreach (var counter in new[] { "expectedGaps", "assessedGaps", "answeredGaps", "openGaps", "unassessedGaps" })
        {
            Assert.Equal(original.GetProperty("summary").GetProperty(counter).GetInt32(), report.GetProperty("summary").GetProperty(counter).GetInt32());
        }

        Assert.NotEqual(original.GetProperty("inputHash").GetString(), report.GetProperty("inputHash").GetString());
        Assert.Equal(work, await WorkDigest());
        var firstAgain = await client.GetFromJsonAsync<JsonElement>(ReportsUrl(w) + "/" + original.GetProperty("id").GetGuid(), Ct);
        Assert.Equal(original.GetRawText(), firstAgain.GetRawText());
        Assert.Equal(2, await Count("SELECT count(*) FROM \"EvidenceCoverageReports\" WHERE \"AnalysisId\" = @a AND \"CheckerVersion\" = 'coverage-3'", ("a", w.Run)));
        Assert.NotEqual(digest, await SqlDigest("coverage-3"));
    }

    [Fact]
    public async Task An_existing_coverage_2_report_never_shadows_the_coverage_3_lookup_and_reads_have_no_side_effects()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed], v5: V5Spec.Single(Answered));
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var revision = await db.AnalysisRevisions.AsNoTracking().SingleAsync(r => r.AnalysisId == w.Run, Ct);
            db.EvidenceCoverageReports.Add(new EvidenceCoverageReport
            {
                Id = Guid.NewGuid(), ProjectId = w.Project, AnalysisId = w.Run, RevisionId = revision.Id, DraftId = null, CheckerVersion = "coverage-2", InputHash = Hex('9'),
                CreatedBy = w.V5!.User, CreatedAt = DateTimeOffset.UtcNow, Report = "{}",
            });
            await db.SaveChangesAsync(Ct);
        }

        var before = (await Count("SELECT count(*) FROM \"DocumentJobs\""), await Count("SELECT count(*) FROM \"ProcessingRuns\""), await WorkDigest());
        var created = await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var report = await Json(created);
        Assert.Equal("coverage-3", report.GetProperty("checkerVersion").GetString());
        var list = await client.GetFromJsonAsync<JsonElement>(ReportsUrl(w), Ct);
        Assert.Equal(["coverage-2", "coverage-3"], list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("checkerVersion").GetString()!).Order());
        _ = await client.GetFromJsonAsync<JsonElement>(ReportsUrl(w) + "/" + report.GetProperty("id").GetGuid(), Ct);
        Assert.Equal(before, (await Count("SELECT count(*) FROM \"DocumentJobs\""), await Count("SELECT count(*) FROM \"ProcessingRuns\""), await WorkDigest()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/projects/{await NewProject(client)}/analyses/{w.Run}/coverage-reports", new { }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await App.Anonymous().PostAsJsonAsync(ReportsUrl(w), new { }, Ct)).StatusCode);
    }

    private Task<string> SqlDigest(string checker) =>
        Scalar<string>("SELECT coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '-') FROM \"EvidenceCoverageReports\" t WHERE \"CheckerVersion\" = @c", ("c", checker));

    // ================================================================ seed

    private sealed record World(
        Guid Project, Guid Document, Guid Version, Guid Preparation, Guid Run, Guid EvidenceA, Guid EvidenceB, Guid EvidenceVersion, Guid PendingPortion, string ParseSha,
        V5Info? V5 = null);

    private static string ReportsUrl(World w) => $"/api/projects/{w.Project}/analyses/{w.Run}/coverage-reports";

    // ---- origine della revisione AI (002 T139): manifesto e WorkOutput scritti con lo store reale, mai con un modello

    private enum GapRef { None, Question, Conflict }

    private sealed record GapSpec(GapOutcome Outcome, GapReason Reason, bool SupportA = false, GapRef Ref = GapRef.None, bool Assessed = true);

    private sealed record V5Spec(
        string Algorithm, string? OutputSchema, GapSpec[] Gaps, bool TamperGapHash = false, bool DanglingSupport = false, bool OmitQuestionFromRevision = false)
    {
        public static V5Spec Single(params GapSpec[] gaps) => new("analysis-single-v3", "document-analysis-alias-2", gaps);

        public static V5Spec Hierarchical() => new("global-reconcile-v1", "reconcile-alias-1", []);

        public static V5Spec HistoricSingle() => new("analysis-single-v2", "document-analysis-alias-1", []);
    }

    private sealed record V5Info(
        IReadOnlyList<Guid> Gaps, IReadOnlyList<Guid> Questions, Guid Conflict, string BatchId, string GapHash, string CatalogSource, Guid AiRevision,
        DocumentAnalysisContent AiContent, Guid User);

    private static string Hex(char c) => new(c, 64);

    private static string GapText(int index) => $"Qual è il limite {index + 1} non indicato nel documento?";

    private async Task<World> SeedAsync(
        HttpClient client, PortionStatus[] portions, bool linkB = true, string? itemTextA = null, bool withAnalysis = true, bool openConflict = false, V5Spec? v5 = null)
    {
        var gapSpecs = v5?.Gaps ?? [];
        var gapIds = gapSpecs.Select(_ => Guid.NewGuid()).ToList();
        var gapBlocks = gapSpecs.Select(_ => Guid.NewGuid()).ToList();
        var gapSources = gapSpecs.Select(_ => Guid.NewGuid()).ToList();
        var documentEvidence = new List<DocumentAnalysisEvidence>();
        var project = await NewProject(client);
        var created = await Json(await Upload(client, project, Text("regole"), "regole.txt", displayName: "regole"));
        var (document, version) = (created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid());
        var (artifactId, blockA, blockB, sourceA, sourceB, evidenceA, evidenceB, evidenceVersion) =
            (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var sha = new string('a', 64);
        var pending = Guid.Empty;
        Guid preparationId;
        Guid user;
        using (var scope = App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == version, Ct);
            preparationId = preparation.Id;
            user = await db.Projects.AsNoTracking().Where(p => p.Id == project).Select(p => p.CreatedBy).SingleAsync(Ct);
            var now = DateTimeOffset.UtcNow;
            db.ParsedArtifacts.Add(new ParsedArtifact
            {
                Id = artifactId, ProjectId = project, PreparationId = preparation.Id, JobId = preparation.ParseJobId!.Value, FencingToken = 1, SchemaVersion = "parsed-manifest-1",
                StorageKey = "k" + artifactId.ToString("N"), Sha256 = sha, ByteLength = 10, ParserConfigHash = sha, BlockCount = 2 + gapSpecs.Length, IssueCount = 0, CreatedAt = now,
            });
            await db.SaveChangesAsync(Ct);
            DocumentBlock Block(Guid id, int ordinal, string text) => new()
            {
                Id = id, ProjectId = project, ArtifactId = artifactId, Ordinal = ordinal, Ref = $"b{ordinal}", Type = BlockType.Paragraph, CanonicalText = text, TextHash = sha, HeadingPathJson = "[]",
            };
            db.DocumentBlocks.AddRange(Block(blockA, 0, TextA), Block(blockB, 1, TextB));
            db.DocumentBlocks.AddRange(gapBlocks.Select((id, i) => Block(id, 2 + i, GapText(i))));
            await db.SaveChangesAsync(Ct);
            for (var i = 0; i < portions.Length; i++)
            {
                var id = Guid.NewGuid();
                pending = portions[i] == PortionStatus.Pending ? id : pending;
                db.DocumentPortions.Add(new DocumentPortion
                {
                    Id = id, ProjectId = project, PreparationId = preparation.Id, Ordinal = i, RangesJson = "[]", ContextRangesJson = "[]", TextHash = sha, Status = portions[i],
                    ResultHash = portions[i] == PortionStatus.Completed ? sha : null, NoEvidenceReason = i == 1 && portions[i] == PortionStatus.Completed ? "solo intestazione" : null,
                });
            }

            var job = Guid.NewGuid();
            db.EvidenceExtractionJobs.Add(new EvidenceExtractionJob
            {
                Id = job, ProjectId = project, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Reason = ExtractionJobReason.Initial,
                Status = ExtractionJobStatus.Queued, RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
            });
            db.EvidenceExtractionVersions.Add(new EvidenceExtractionVersion
            {
                Id = evidenceVersion, ProjectId = project, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, ManifestHash = new string('1', 64),
                Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = 2 + gapSpecs.Length, CreatedAt = now,
            });
            await db.SaveChangesAsync(Ct);
            void AddEvidence(Guid id, int ordinal, string text, EvidenceKind kind, DocumentEvidenceContent content)
            {
                db.Evidences.Add(Evidence(project, evidenceVersion, preparation.Id, id, ordinal, text, kind, content));
                documentEvidence.Add(new DocumentAnalysisEvidence(
                    id, evidenceVersion, EvidenceOriginKind.Document, kind, EvidencePolarity.Affirmed, EvidenceCertainty.Declared, text, Hex("0123456789abcdef"[ordinal % 16]),
                    [.. content.Sources]));
            }

            AddEvidence(evidenceA, 1, TextA, EvidenceKind.Requirement,
                Content(document, version, preparation.Id, artifactId, blockA, sourceA, TextA, [new DocumentEvidenceQualifier { Type = QualifierType.Maximum, Text = "al massimo 5 ordini" }]));
            AddEvidence(evidenceB, 2, TextB, EvidenceKind.Requirement, Content(document, version, preparation.Id, artifactId, blockB, sourceB, TextB, []));
            for (var i = 0; i < gapSpecs.Length; i++)
            {
                AddEvidence(gapIds[i], 3 + i, GapText(i), EvidenceKind.Gap, Content(document, version, preparation.Id, artifactId, gapBlocks[i], gapSources[i], GapText(i), []));
            }

            db.EvidenceDocumentStates.Add(new EvidenceDocumentState
            {
                DocumentVersionId = version, ProjectId = project, CurrentPreparationId = preparation.Id, CurrentEvidenceVersionId = evidenceVersion, Revision = 1, UpdatedAt = now,
            });
            await db.SaveChangesAsync(Ct);
            preparation.Status = PreparationStatus.Ready;
            preparation.CurrentEvidenceVersionId = evidenceVersion;
            preparation.ManifestHash = new string('1', 64);
            await db.SaveChangesAsync(Ct);
            await db.ProjectDocuments.Where(d => d.Id == document).ExecuteUpdateAsync(x => x.SetProperty(d => d.LatestReadyVersionId, version), Ct);
        }

        var run = Guid.Empty;
        V5Info? v5Info = null;
        if (withAnalysis)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var started = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            run = (await Json(started)).GetProperty("id").GetGuid();
            await Execute(
                "UPDATE \"ProcessingRuns\" SET \"Status\" = 'Running', \"LeaseOwner\" = 'test-owner', \"FencingToken\" = 1, \"AttemptCount\" = 1, \"StartedAt\" = now(), \"LeaseUntil\" = now() + interval '10 minutes' WHERE \"Id\" = @r",
                ("r", run));
            using var scope = App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var frozen = await new EvidenceLedgerStore(db).FreezeDocumentSelectionAsync(new AnalysisRunClaim(run, "test-owner", 1, 1), Ct);
            Assert.True(frozen.Outcome is SelectionOutcome.Frozen, frozen.Outcome.ToString());
            var (itemA, itemB) = (Guid.NewGuid(), Guid.NewGuid());
            var catalogSource = Hex('5');
            var questions = gapSpecs.Select((g, i) => g.Ref == GapRef.Question ? AnalysisAliasComposer.CanonicalId("item", run, catalogSource, $"i{3 + i}") : Guid.Empty).ToList();
            var conflictId = gapSpecs.Any(g => g.Ref == GapRef.Conflict) ? AnalysisAliasComposer.CanonicalId("conflict", run, catalogSource, "c1") : Guid.Empty;
            DocumentAnalysisSource Source(Guid id, Guid block, string text) => new()
            {
                Id = id, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = document,
                DocumentVersionId = version, PreparationId = preparationId, ArtifactId = artifactId, BlockId = block, Start = 0, End = text.Length, Quote = text,
            };
            var items = new List<DocumentAnalysisItem>
            {
                new() { Id = itemA, Text = itemTextA ?? TextA, Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [sourceA] },
            };
            if (linkB)
            {
                items.Add(new() { Id = itemB, Text = TextB, Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [sourceB] });
            }

            var content = new DocumentAnalysisContent
            {
                SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = run, ProjectId = project, SourceCutoffSequence = 0, ValidationState = AnalysisContentState.Unvalidated,
                Sources = [Source(sourceA, blockA, TextA), Source(sourceB, blockB, TextB)], Goals = [],
                ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
                Requirements = items, OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [],
                OpenQuestions = v5?.OmitQuestionFromRevision == true ? [] :
                [
                    .. questions.Select((id, i) => (id, i)).Where(t => t.id != Guid.Empty)
                        .Select(t => new DocumentAnalysisItem { Id = t.id, Text = GapText(t.i).Replace("limite", "quesito"), Classification = DocumentAnalysisClassification.Inferred, EvidenceRefs = [] }),
                ],
                Conflicts = !openConflict && conflictId == Guid.Empty ? [] :
                [
                    new DocumentAnalysisConflict
                    {
                        Id = conflictId != Guid.Empty ? conflictId : Guid.NewGuid(), Description = "Orario di chiusura discordante", Status = AnalysisConflictStatus.Open, Resolution = null,
                        Alternatives =
                        [
                            new() { Id = Guid.NewGuid(), Text = "Il magazzino B chiude alle 18.", Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [sourceB] },
                            new() { Id = Guid.NewGuid(), Text = "Il magazzino B chiude alle 20 il venerdi.", Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [sourceA] },
                        ],
                    },
                ],
            };
            var aiRevisionId = Guid.NewGuid();
            db.AnalysisRevisions.Add(new AnalysisRevision
            {
                Id = aiRevisionId, AnalysisId = run, Version = 1, Origin = AnalysisRevisionOrigin.Ai, CreatedBy = user, CreatedAt = DateTimeOffset.UtcNow,
                SchemaVersion = AnalysisRevision.DocumentSchemaVersion, ContentJson = DocumentAnalysisContentJson.Serialize(content), ValidationState = AnalysisValidationState.Unvalidated,
            });
            await db.SaveChangesAsync(Ct);
            db.AnalysisEvidenceLinks.Add(new AnalysisEvidenceLink { RunId = run, ItemId = itemA, EvidenceId = evidenceA, ProjectId = project });
            if (linkB)
            {
                db.AnalysisEvidenceLinks.Add(new AnalysisEvidenceLink { RunId = run, ItemId = itemB, EvidenceId = evidenceB, ProjectId = project });
            }

            await db.SaveChangesAsync(Ct);

            if (v5 is not null)
            {
                v5Info = await CommitWorkAsync(db, project, run, v5, documentEvidence, gapIds, questions, conflictId, catalogSource, aiRevisionId, content, user);
            }
        }

        return new(project, document, version, preparationId, run, evidenceA, evidenceB, evidenceVersion, pending, sha, v5Info);
    }

    /// <summary>Manifesto, batch e WorkOutput dell'unica chiamata, come li scrive il workflow (payload alias-2 con valutazioni, oppure v4/gerarchico senza): nessun modello.</summary>
    private static async Task<V5Info> CommitWorkAsync(
        AppDbContext db, Guid project, Guid run, V5Spec spec, IReadOnlyList<DocumentAnalysisEvidence> evidence, IReadOnlyList<Guid> gapIds, IReadOnlyList<Guid> questions,
        Guid conflictId, string catalogSource, Guid aiRevisionId, DocumentAnalysisContent aiContent, Guid user)
    {
        var writer = new CheckpointWriter(run, "test-owner", 1, WorkOwnerKind.ProcessingRun, "project-analysis-documents-v5", "1.26.0");
        var store = new DocumentWorkStore(db, writer);
        var batchId = $"batch-{run:N}";
        var schema = spec.OutputSchema ?? "document-analysis-alias-2";
        var payload = new JsonObject { ["schemaVersion"] = schema, ["outcome"] = "Complete" };
        var gapHash = string.Empty;
        if (spec.OutputSchema == "document-analysis-alias-2")
        {
            var snapshot = new ReconciliationSnapshot(project, catalogSource, [.. evidence.Select(e => new SnapshotEvidence(e.EvidenceId, e.VersionId, e.ContentHash, e.Sources))], []);
            var input = new DocumentAnalysisInput(project, run, 2, 1, 3, 0, Hex('6'), snapshot, evidence, []);
            var catalog = AnalysisAliasCatalog.Build(input, AnalysisAliasContracts.SchemaVersionV2);
            var aliases = catalog.Data.Evidence.Select(e => e.Alias).ToList();
            var records = new List<AiGapAssessment>();
            for (var i = 0; i < spec.Gaps.Length; i++)
            {
                var gap = spec.Gaps[i];
                if (!gap.Assessed)
                {
                    continue;
                }

                var support = gap.SupportA ? new List<string> { "E1" } : [];
                if (spec.DanglingSupport && records.Count == 0)
                {
                    support.Add("E99");
                }

                records.Add(new AiGapAssessment
                {
                    Gap = catalog.EvidenceById(gapIds[i])!.Alias, Outcome = gap.Outcome, Reason = gap.Reason, Rationale = $"Valutazione AI {i + 1} nello snapshot.", SupportingEvidence = support,
                    OpenItem = gap.Ref == GapRef.Question ? $"i{3 + i}" : null, OpenConflict = gap.Ref == GapRef.Conflict ? "c1" : null,
                });
            }

            var hasConflict = spec.Gaps.Any(g => g.Ref == GapRef.Conflict);
            var dto = new AiAnalysisOutputV2
            {
                Contract = "alias-2", Visited = new() { A = aliases, B = aliases },
                NoFindings = new() { A = "Nessuna relazione fra le evidenze.", B = "Nessuna relazione fra le evidenze.", Cross = null },
                Items = new()
                {
                    Goals = [], ScopeOverview = [], ScopeIncluded = [], ScopeExcluded = [],
                    Requirements = [AliasFixtures.Item("i1", TextA, DocumentAnalysisClassification.Documented, "E1"), AliasFixtures.Item("i2", TextB, DocumentAnalysisClassification.Documented, "E2")],
                    Flows = [], Constraints = [], Dependencies = [], Assumptions = [],
                    OpenQuestions = [.. spec.Gaps.Select((g, i) => (g, i)).Where(t => t.g.Ref == GapRef.Question)
                        .Select(t => AliasFixtures.Item($"i{3 + t.i}", $"Quesito {t.i + 1}?", DocumentAnalysisClassification.Inferred, catalog.EvidenceById(gapIds[t.i])!.Alias))],
                },
                Conflicts = !hasConflict ? [] :
                [
                    new AiConflict
                    {
                        Key = "c1", Description = "Orario discordante.",
                        Alternatives = [AliasFixtures.Item("i80", "Alternativa uno.", DocumentAnalysisClassification.Documented, "E1"), AliasFixtures.Item("i81", "Alternativa due.", DocumentAnalysisClassification.Documented, "E2")],
                    },
                ],
                Findings = [], KnownRelationChecks = [], Patches = [], GapAssessments = records,
            };
            gapHash = spec.TamperGapHash ? Hex('f') : GapAssessmentRules.Hash(records);
            payload["gaps"] = new JsonArray();
            payload["coverageHash"] = Hex('d');
            payload["aiOutputHash"] = AnalysisAliasContracts.Hash(dto);
            payload["catalogHash"] = catalog.CatalogHash;
            payload["canonicalHash"] = Hex('e');
            payload["ai"] = JsonNode.Parse(AnalysisAliasContracts.Canonical(dto));
            payload["catalog"] = JsonNode.Parse(catalog.Encode());
            payload["gapAssessmentsVersion"] = GapAssessmentRules.Version;
            payload["gapAssessments"] = JsonNode.Parse(GapAssessmentRules.Canonical(records));
            payload["gapAssessmentsHash"] = gapHash;
        }

        await store.PublishManifestAsync(new WorkManifestInput(Hex('a'), Hex('b'), "fp-1", writer.DefinitionVersion!, spec.Algorithm, Hex('c'), """{"nodes":["Load","PrepareBatch"]}"""), Ct);
        if (spec.OutputSchema is not null)
        {
            await store.PublishBatchesAsync([new DerivedBatchInput(batchId, "analysis", null, null, null, 0, [], """{"all":true}""", 100, "measured", Hex('1'), schema, "document-analysis-v4")], Ct);
            var json = payload.ToJsonString();
            await store.CommitOutputAsync(
                new WorkOutputInput(batchId, DocumentBatchWorkflow.InvokeId, Hex('1'), CanonicalJson.Sha256Hex(json), json, null, null, "[]", null, null, schema, "document-analysis-validation-v3", null), null, Ct);
        }

        return new V5Info(gapIds, questions.Where(q => q != Guid.Empty).ToList(), conflictId, batchId, gapHash, catalogSource, aiRevisionId, aiContent, user);
    }

    private static EvidenceItem Evidence(Guid project, Guid version, Guid preparation, Guid id, int ordinal, string statement, EvidenceKind kind, DocumentEvidenceContent content) => new()
    {
        Id = id, VersionId = version, ProjectId = project, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation, Ordinal = ordinal, Key = $"e{ordinal}", Kind = kind,
        Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = statement, Content = DocumentEvidenceCodec.EncodeContent(content),
    };

    private static DocumentEvidenceContent Content(
        Guid document, Guid version, Guid preparation, Guid artifact, Guid block, Guid source, string text, IReadOnlyList<DocumentEvidenceQualifier> qualifiers) => new()
    {
        SchemaVersion = DocumentEvidenceContent.SchemaVersionValue, KindRationale = null,
        Structure = new DocumentEvidenceStructure { Subject = null, Action = null, Object = null, Scope = null, Purpose = null, Instrument = null, Channel = null, Recipient = null },
        Qualifiers = qualifiers,
        Sources =
        [
            new PersistedSource
            {
                SourceId = source, Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Guid.NewGuid(), Part = 0, MessageId = null, Sequence = null,
                DocumentId = document, DocumentVersionId = version, PreparationId = preparation, ArtifactId = artifact, BlockId = block, Start = 0, End = text.Length, Quote = text,
            },
        ],
    };
}
