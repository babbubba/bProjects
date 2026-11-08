using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T136 (DET) — composizione e completezza del single-call v5 (contratto <c>alias-2</c>, contracts/ai-workflow.md §13.2/§13.4): valutazioni dei Gap
/// (<c>gap-assessment-1</c>), <c>gap_not_assessed</c>, Open motivato non bloccante, coerenze verificabili e nessun supporto automatico. Nessun database, nessun LLM:
/// provano il protocollo e i controlli del server, mai la qualità semantica di un modello reale (la valutazione è interpretazione AI non validata).
/// Il fixture è l'N4 semplificato: E1 messaggio PM «45», E2 domanda (Gap) sulla scheda tecnica, E3 la scheda «30», E4 un requisito estraneo, E5 un secondo Gap senza risposta.
/// </summary>
public sealed class AnalysisAliasGapAssessmentTests
{
    private static readonly Guid ScheduleSource = AliasFixtures.G(951);

    private static List<DocumentAnalysisEvidence> Evidences() =>
    [
        AliasFixtures.Ev(1, EvidenceOriginKind.Message, "Servono 45 pezzi al minuto.", AliasFixtures.Msg(AliasFixtures.Sm1, AliasFixtures.M1, 1, "Servono 45 pezzi al minuto")),
        AliasFixtures.Ev(2, EvidenceOriginKind.Document, "Cosa dice la scheda tecnica sul numero di pezzi al minuto?", AliasFixtures.Doc(AliasFixtures.Sa, AliasFixtures.G1, 0, 1, 1, 100, "Qual è la cadenza riportata dalla scheda?")) with { Kind = EvidenceKind.Gap },
        AliasFixtures.Ev(3, EvidenceOriginKind.Document, "La scheda tecnica indica 30 pezzi al minuto.", AliasFixtures.Doc(AliasFixtures.Sx, AliasFixtures.G3, 0, 2, 4, 50, "Cadenza: 30 pezzi/min")),
        AliasFixtures.Ev(4, EvidenceOriginKind.Document, "Il turno dura 8 ore.", AliasFixtures.Doc(AliasFixtures.Sz, AliasFixtures.G3, 0, 3, 6, 10, "Turno di 8 ore")),
        AliasFixtures.Ev(5, EvidenceOriginKind.Document, "Chi approva la modifica?", AliasFixtures.Doc(AliasFixtures.Sy, AliasFixtures.G2, 0, 3, 5, 90, "Da chiarire chi approva")) with { Kind = EvidenceKind.Gap },
    ];

    private static DocumentAnalysisInput Input(List<DocumentAnalysisEvidence>? evidence = null) => AliasFixtures.Input(evidence: evidence ?? Evidences());

    private static AnalysisAliasCatalog Catalog(DocumentAnalysisInput input, string contract = AnalysisAliasContracts.SchemaVersionV2) => AnalysisAliasCatalog.Build(input, contract);

    private static readonly string[] All = ["E1", "E2", "E3", "E4", "E5"];

    private static AiGapAssessment Answered(string gap, params string[] support) => new()
    {
        Gap = gap, Outcome = GapOutcome.AnsweredInSnapshot, Reason = GapReason.Answered, Rationale = "Il riscontro indica il valore richiesto.", SupportingEvidence = support, OpenItem = null, OpenConflict = null,
    };

    private static AiGapAssessment OpenItem(string gap, GapReason reason, string item, params string[] support) => new()
    {
        Gap = gap, Outcome = GapOutcome.Open, Reason = reason, Rationale = "Il punto resta da chiarire.", SupportingEvidence = support, OpenItem = item, OpenConflict = null,
    };

    private static AiGapAssessment OpenConflict(string gap, GapReason reason, string conflict, params string[] support) => new()
    {
        Gap = gap, Outcome = GapOutcome.Open, Reason = reason, Rationale = "I riscontri sono in conflitto.", SupportingEvidence = support, OpenItem = null, OpenConflict = conflict,
    };

    /// <summary>
    /// N4 corretto: E2 AnsweredInSnapshot/Answered con riscontro [E3]; conflitto Open E1 (Declared, 45) contro E3 (Documented, 30) con il finding Contradicts Open; nessuna domanda
    /// duplicata «cosa dice la scheda»; E5 (Gap senza risposta) resta una domanda aperta motivata.
    /// </summary>
    private static AiAnalysisOutputV2 N4() => new()
    {
        Contract = "alias-2",
        Visited = new() { A = All, B = All },
        NoFindings = new() { A = null, B = "Nessuna relazione in vista B.", Cross = null },
        Items = new()
        {
            Goals = [], ScopeOverview = [], ScopeIncluded = [], ScopeExcluded = [],
            Requirements = [AliasFixtures.Item("i3", "Il turno dura 8 ore.", DocumentAnalysisClassification.Documented, "E4")],
            Flows = [], Constraints = [], Dependencies = [], Assumptions = [],
            OpenQuestions = [AliasFixtures.Item("i4", "Chi approva la modifica?", DocumentAnalysisClassification.Documented, "E5")],
        },
        Conflicts =
        [
            new()
            {
                Key = "c1", Description = "Pezzi al minuto discordanti fra PM e scheda tecnica.", Alternatives =
                [
                    AliasFixtures.Item("i1", "Servono 45 pezzi al minuto.", DocumentAnalysisClassification.Declared, "E1"),
                    AliasFixtures.Item("i2", "La scheda tecnica indica 30 pezzi al minuto.", DocumentAnalysisClassification.Documented, "E3"),
                ],
            },
        ],
        Findings =
        [
            new() { Key = "f1", View = ReconcileView.A, Left = "E1", Right = "E3", Type = EvidenceRelationType.Contradicts, Basis = EvidenceRelationBasis.Inferred, Rationale = "45 contro 30.", Outcome = AiFindingOutcome.Open, Reason = "Il project manager deve scegliere il valore." },
        ],
        KnownRelationChecks = [], Patches = [],
        GapAssessments = [Answered("E2", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4")],
    };

    private static AliasComposition Compose(AiAnalysisOutputV2 dto, DocumentAnalysisInput? input = null, string? expectedCanonicalHash = null)
    {
        input ??= Input();
        return AnalysisAliasComposer.Compose(dto, Catalog(input), input, expectedCanonicalHash: expectedCanonicalHash);
    }

    private static string CodesOf(AliasComposition result) => string.Join(" | ", result.Violations.Concat(result.Validation?.Completion?.Gaps.Select(g => g.Code + ": " + g.Detail) ?? []));

    private static AliasComposition Complete(AiAnalysisOutputV2 dto, DocumentAnalysisInput? input = null)
    {
        var result = Compose(dto, input);
        Assert.True(result.Outcome == ReconciliationOutcome.Complete, CodesOf(result));
        return result;
    }

    private static AliasComposition Rejected(AiAnalysisOutputV2 dto, string fragment, DocumentAnalysisInput? input = null)
    {
        var result = Compose(dto, input);
        Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
        Assert.False(result.IsReusableWorkOutput);
        Assert.Null(result.Output);
        Assert.Contains(result.Violations, v => v.Contains(fragment, StringComparison.Ordinal));
        return result;
    }

    private static AliasComposition Incomplete(AiAnalysisOutputV2 dto, string code, DocumentAnalysisInput? input = null)
    {
        var result = Compose(dto, input);
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, result.Outcome);
        Assert.True(result.IsReusableWorkOutput);
        Assert.NotNull(result.Output);
        Assert.Contains(result.Validation!.Completion!.Gaps, g => g.Code == code);
        return result;
    }

    private static AiAnalysisOutputV2 WithAssessments(AiAnalysisOutputV2 dto, params AiGapAssessment[] records) => dto with { GapAssessments = records };

    private static AiAnalysisOutputV2 WithOpenQuestions(AiAnalysisOutputV2 dto, params AiItem[] items) => dto with { Items = dto.Items with { OpenQuestions = items } };

    private static Guid EvidenceId(int n) => AliasFixtures.EvidenceId(n);

    // ---------------------------------------------------------------- N4 e matrice degli esiti

    [Fact]
    public void The_corrected_N4_completes_with_E2_answered_by_E3_and_an_open_conflict_between_E1_and_E3_and_no_duplicate_question()
    {
        var result = Complete(N4());

        var records = result.GapAssessments!;
        Assert.Equal(["E2", "E5"], records.Select(r => r.Gap));
        Assert.Equal((GapOutcome.AnsweredInSnapshot, GapReason.Answered), (records[0].Outcome, records[0].Reason));
        Assert.Equal(["E3"], records[0].SupportingEvidence);
        var conflict = Assert.Single(result.Output!.Analysis.Conflicts);
        Assert.Equal(AnalysisConflictStatus.Open, conflict.Status);
        Assert.Equal(["Servono 45 pezzi al minuto.", "La scheda tecnica indica 30 pezzi al minuto."], conflict.Alternatives.Select(a => a.Text));
        Assert.Equal([DocumentAnalysisClassification.Declared, DocumentAnalysisClassification.Documented], conflict.Alternatives.Select(a => a.Classification));
        // Nessuna domanda sulla scheda e nessuna scelta fra i termini: la sola domanda aperta è quella del secondo Gap.
        Assert.Equal("Chi approva la modifica?", Assert.Single(result.Output.Analysis.OpenQuestions).Text);
        Assert.Empty(result.Validation!.Completion!.Gaps);
        Assert.Equal(GapAssessmentRules.Hash(records), result.GapAssessmentsHash);
    }

    [Fact]
    public void An_answered_gap_does_not_enter_the_evidence_map_or_the_sources_and_the_support_is_not_added_to_any_item()
    {
        var result = Complete(N4());

        var mapped = result.Output!.EvidenceMap.SelectMany(m => m.EvidenceIds).ToHashSet();
        Assert.DoesNotContain(EvidenceId(2), mapped);                                    // il Gap E2 non è mappato (e non lo richiede)
        Assert.Contains(EvidenceId(3), mapped);                                          // E3 è mappata solo dall'alternativa che la cita esplicitamente
        var sources = result.Output.Analysis.Sources.Select(s => s.Id).ToList();
        Assert.DoesNotContain(AliasFixtures.Sa, sources);                                // fonte del Gap: mai fra le fonti dell'analisi
        var citing = result.Output.EvidenceMap.Where(m => m.EvidenceIds.Contains(EvidenceId(3))).ToList();
        Assert.Single(citing);                                                           // E3 sostiene la sola alternativa i2, non altri item né il requisito estraneo
        var refs = result.Output.Analysis.Requirements.SelectMany(r => r.EvidenceRefs).Concat(result.Output.Analysis.OpenQuestions.SelectMany(q => q.EvidenceRefs)).ToList();
        Assert.DoesNotContain(AliasFixtures.Sx, refs);
        Assert.DoesNotContain(AliasFixtures.Sa, refs);
    }

    [Fact]
    public void A_support_cited_only_by_an_assessment_is_never_mapped_so_a_non_gap_support_without_an_item_stays_evidence_not_mapped()
    {
        // E3 compare solo come riscontro del Gap: nessun item la cita ⇒ resta una evidenza non-Gap non mappata (nessun supporto automatico).
        var dto = N4() with
        {
            Conflicts = [], Findings = [],
            Items = N4().Items with { Requirements = [AliasFixtures.Item("i3", "Il turno dura 8 ore.", DocumentAnalysisClassification.Documented, "E4"), AliasFixtures.Item("i1", "Servono 45 pezzi al minuto.", DocumentAnalysisClassification.Declared, "E1")] },
            NoFindings = new() { A = "Nessuna relazione.", B = "Nessuna relazione.", Cross = null },
        };

        var result = Incomplete(dto, "evidence_not_mapped");

        var gaps = result.Validation!.Completion!.Gaps;
        Assert.Contains(gaps, g => g.Code == "evidence_not_mapped" && g.Detail.Contains(EvidenceId(3).ToString("D"), StringComparison.Ordinal));
        Assert.DoesNotContain(gaps, g => g.Code == "evidence_not_mapped" && g.Detail.Contains(EvidenceId(2).ToString("D"), StringComparison.Ordinal));
        Assert.DoesNotContain(EvidenceId(3), result.Output!.EvidenceMap.SelectMany(m => m.EvidenceIds));
        Assert.DoesNotContain(AliasFixtures.Sx, result.Output.Analysis.Sources.Select(s => s.Id));
    }

    [Fact]
    public void A_gap_without_a_record_is_valid_incomplete_gap_not_assessed_even_when_it_is_not_mapped()
    {
        var result = Incomplete(WithAssessments(N4(), OpenItem("E5", GapReason.NoAnswer, "i4")), "gap_not_assessed");

        var gaps = result.Validation!.Completion!.Gaps;
        var missing = Assert.Single(gaps, g => g.Code == "gap_not_assessed");
        Assert.Contains(EvidenceId(2).ToString("D"), missing.Detail, StringComparison.Ordinal);
        Assert.Equal(["gap_not_assessed"], gaps.Select(g => g.Code));                    // unico residuo: l'E2 non mappata non è un evidence_not_mapped
        Assert.True(result.Validation.Completion.AdditionalWorkJustified);
        Assert.NotNull(result.CanonicalHash);                                            // riutilizzabile: output Committed senza revisione
    }

    [Fact]
    public void A_gap_without_a_record_is_gap_not_assessed_even_when_an_item_already_maps_it()
    {
        var dto = WithOpenQuestions(
            WithAssessments(N4(), OpenItem("E5", GapReason.NoAnswer, "i4")),
            AliasFixtures.Item("i4", "Chi approva la modifica?", DocumentAnalysisClassification.Documented, "E5"),
            AliasFixtures.Item("i5", "Cosa dice la scheda tecnica sul numero di pezzi al minuto?", DocumentAnalysisClassification.Documented, "E2"));

        var result = Incomplete(dto, "gap_not_assessed");

        Assert.Equal(["gap_not_assessed"], result.Validation!.Completion!.Gaps.Select(g => g.Code));
        Assert.Contains(EvidenceId(2), result.Output!.EvidenceMap.SelectMany(m => m.EvidenceIds));        // mappato, ma non per questo valutato
    }

    [Fact]
    public void An_empty_assessment_list_leaves_every_gap_unassessed_and_each_one_is_reported()
    {
        var result = Incomplete(WithAssessments(N4()), "gap_not_assessed");

        Assert.Equal(2, result.Validation!.Completion!.Gaps.Count(g => g.Code == "gap_not_assessed"));
        Assert.Empty(result.GapAssessments!);
    }

    [Fact]
    public void A_mapped_gap_with_a_valid_record_completes_and_a_gap_needs_no_item_when_it_is_assessed()
    {
        var mapped = WithOpenQuestions(
            N4(),
            AliasFixtures.Item("i4", "Chi approva la modifica?", DocumentAnalysisClassification.Documented, "E5"),
            AliasFixtures.Item("i5", "Quale cadenza riporta la scheda tecnica?", DocumentAnalysisClassification.Documented, "E2"));

        Complete(mapped);
        Complete(N4());      // E2 non mappato
    }

    [Fact]
    public void A_non_gap_evidence_still_has_to_be_mapped_on_an_item()
    {
        var dto = N4() with { Items = N4().Items with { Requirements = [] } };      // E4 non citata

        var result = Incomplete(dto, "evidence_not_mapped");

        var gaps = result.Validation!.Completion!.Gaps;
        Assert.Equal(["evidence_not_mapped"], gaps.Select(g => g.Code));
        Assert.Contains(EvidenceId(4).ToString("D"), gaps[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_visit_is_still_a_residual_obligation_even_when_the_gap_is_assessed()
    {
        var dto = N4() with { Visited = new() { A = ["E1", "E3", "E4", "E5"], B = All } };      // E2 non visitato in A

        var result = Incomplete(dto, "entry_not_visited");

        Assert.Equal(["entry_not_visited"], result.Validation!.Completion!.Gaps.Select(g => g.Code));
        Assert.Single(result.Validation.Completion.MissingEntryIds);
    }

    [Fact]
    public void A_gap_visited_in_both_views_but_not_assessed_is_not_complete_the_visit_does_not_stand_for_the_assessment()
    {
        var result = Incomplete(WithAssessments(N4(), OpenItem("E5", GapReason.NoAnswer, "i4")), "gap_not_assessed");

        Assert.Empty(result.Validation!.Completion!.MissingEntryIds);                    // visite complete
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, result.Outcome);
    }

    // ---------------------------------------------------------------- distinzione degli esiti: riscontrata / senza risposta / parziale / ambigua / conflitto aperto

    [Fact]
    public void A_motivated_open_no_answer_is_a_final_assessment_it_does_not_block_completion_and_is_not_technically_incomplete()
    {
        var result = Complete(WithAssessments(N4(), Answered("E2", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4")));

        var open = Assert.Single(result.GapAssessments!, r => r.Outcome == GapOutcome.Open);
        Assert.Equal((GapReason.NoAnswer, 0), (open.Reason, open.SupportingEvidence.Count));
        Assert.DoesNotContain(result.Validation!.Completion!.Gaps, g => g.Code == "gap_not_assessed");
        Assert.True(result.Validation.Completion.IsComplete);
    }

    [Fact]
    public void A_partial_answer_is_open_with_supports_and_an_open_question_and_still_completes()
    {
        var dto = WithAssessments(N4(), OpenItem("E2", GapReason.Partial, "i4", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4"));

        var result = Complete(dto);

        var partial = result.GapAssessments!.Single(r => r.Gap == "E2");
        Assert.Equal((GapOutcome.Open, GapReason.Partial), (partial.Outcome, partial.Reason));
        Assert.Equal(["E3"], partial.SupportingEvidence);
    }

    [Fact]
    public void An_ambiguous_answer_is_open_and_may_point_to_the_open_conflict()
    {
        var dto = WithAssessments(N4(), OpenConflict("E2", GapReason.Ambiguous, "c1", "E1", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4"));

        var result = Complete(dto);

        var ambiguous = result.GapAssessments!.Single(r => r.Gap == "E2");
        Assert.Equal((GapOutcome.Open, GapReason.Ambiguous, "c1"), (ambiguous.Outcome, ambiguous.Reason, ambiguous.OpenConflict));
        Assert.Null(ambiguous.OpenItem);
    }

    [Fact]
    public void The_five_situations_keep_distinct_records_and_distinct_hashes()
    {
        var answered = Complete(WithAssessments(N4(), Answered("E2", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4")));
        var noAnswer = Complete(WithAssessments(N4(), OpenItem("E2", GapReason.NoAnswer, "i4"), OpenItem("E5", GapReason.NoAnswer, "i4")));
        var partial = Complete(WithAssessments(N4(), OpenItem("E2", GapReason.Partial, "i4", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4")));
        var ambiguous = Complete(WithAssessments(N4(), OpenItem("E2", GapReason.Ambiguous, "i4", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4")));
        var conflict = Complete(WithAssessments(N4(), OpenConflict("E2", GapReason.Ambiguous, "c1", "E1", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4")));

        var states = new[] { answered, noAnswer, partial, ambiguous, conflict }.Select(r => r.GapAssessments!.Single(a => a.Gap == "E2")).ToList();
        Assert.Equal(
            [(GapOutcome.AnsweredInSnapshot, GapReason.Answered), (GapOutcome.Open, GapReason.NoAnswer), (GapOutcome.Open, GapReason.Partial), (GapOutcome.Open, GapReason.Ambiguous), (GapOutcome.Open, GapReason.Ambiguous)],
            states.Select(s => (s.Outcome, s.Reason)));
        Assert.Equal(5, new[] { answered, noAnswer, partial, ambiguous, conflict }.Select(r => r.GapAssessmentsHash).Distinct().Count());
        Assert.Equal(5, new[] { answered, noAnswer, partial, ambiguous, conflict }.Select(r => r.Validation!.Completion!.CoverageHash).Distinct().Count());
        Assert.Equal(5, new[] { answered, noAnswer, partial, ambiguous, conflict }.Select(r => r.AiOutputHash).Distinct().Count());
    }

    // ---------------------------------------------------------------- record malformati e riferimenti falsi ⇒ work_output_invalid

    public static TheoryData<string, string> InvalidRecords => new()
    {
        { "record per un'evidenza non Gap", "l'evidenza non è un Gap" },
        { "record fuori dall'input assegnato", "fuori dall'input assegnato" },
        { "record duplicato per lo stesso Gap", "più record per il Gap E2" },
        { "riscontro che è un altro Gap", "è un Gap e non può essere un riscontro" },
        { "riscontro fuori dall'input", "fuori dall'input assegnato" },
        { "il Gap riscontra se stesso", "il Gap non può riscontrare se stesso" },
        { "Answered senza riscontro", "almeno un riscontro" },
        { "Open senza né openItem né openConflict", "esattamente uno fra openItem e openConflict" },
        { "Open con openItem e openConflict", "esattamente uno fra openItem e openConflict" },
        { "openItem non è una domanda aperta", "non è una domanda aperta dell'output" },
        { "openConflict inesistente", "non è un conflitto dell'output" },
        { "Partial senza riscontro", "Partial richiede almeno un riscontro" },
        { "rationale vuota", "rationale" },
    };

    private static AiGapAssessment[] RecordsFor(string kind) => kind switch
    {
        "record per un'evidenza non Gap" => [Answered("E2", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4"), Answered("E3", "E4")],
        "record fuori dall'input assegnato" => [Answered("E2", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4"), Answered("E9", "E3")],
        "record duplicato per lo stesso Gap" => [Answered("E2", "E3"), Answered("E2", "E4"), OpenItem("E5", GapReason.NoAnswer, "i4")],
        "riscontro che è un altro Gap" => [Answered("E2", "E5"), OpenItem("E5", GapReason.NoAnswer, "i4")],
        "riscontro fuori dall'input" => [Answered("E2", "E9"), OpenItem("E5", GapReason.NoAnswer, "i4")],
        "il Gap riscontra se stesso" => [Answered("E2", "E2"), OpenItem("E5", GapReason.NoAnswer, "i4")],
        "Answered senza riscontro" => [Answered("E2"), OpenItem("E5", GapReason.NoAnswer, "i4")],
        "Open senza né openItem né openConflict" => [Answered("E2", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4") with { OpenItem = null }],
        "Open con openItem e openConflict" => [Answered("E2", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4") with { OpenConflict = "c1" }],
        "openItem non è una domanda aperta" => [Answered("E2", "E3"), OpenItem("E5", GapReason.NoAnswer, "i3")],
        "openConflict inesistente" => [Answered("E2", "E3"), OpenConflict("E5", GapReason.NoAnswer, "c9")],
        "Partial senza riscontro" => [Answered("E2", "E3"), OpenItem("E5", GapReason.Partial, "i4")],
        "rationale vuota" => [Answered("E2", "E3") with { Rationale = "  " }, OpenItem("E5", GapReason.NoAnswer, "i4")],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public void A_malformed_record_or_a_false_reference_is_work_output_invalid_never_a_reusable_incomplete(string kind, string fragment)
    {
        var result = Rejected(WithAssessments(N4(), RecordsFor(kind)), fragment);

        Assert.Null(result.CanonicalHash);
        Assert.NotNull(result.AiOutputHash);
    }

    [Theory]
    [InlineData("extra property", """{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"ok","supportingEvidence":["E3"],"openItem":null,"openConflict":null,"confidence":1}""")]
    [InlineData("enum case", """{"gap":"E2","outcome":"answeredInSnapshot","reason":"Answered","rationale":"ok","supportingEvidence":["E3"],"openItem":null,"openConflict":null}""")]
    [InlineData("non canonical alias", """{"gap":"e2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"ok","supportingEvidence":["E3"],"openItem":null,"openConflict":null}""")]
    [InlineData("alias with leading zero", """{"gap":"E02","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"ok","supportingEvidence":["E3"],"openItem":null,"openConflict":null}""")]
    [InlineData("omitted property", """{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"ok","supportingEvidence":["E3"],"openItem":null}""")]
    [InlineData("support as string", """{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"ok","supportingEvidence":"E3","openItem":null,"openConflict":null}""")]
    [InlineData("uuid as support", """{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"ok","supportingEvidence":["00000000-0000-4000-8000-000000000103"],"openItem":null,"openConflict":null}""")]
    public void A_structurally_malformed_assessment_in_the_json_is_work_output_invalid_by_the_strict_decoder(string kind, string record)
    {
        var input = Input();
        var json = JsonNode.Parse(AnalysisAliasContracts.Encode(N4()))!.AsObject();
        var valid = json["gapAssessments"]![1]!.DeepClone();
        json["gapAssessments"] = new JsonArray(JsonNode.Parse(record), valid);

        var result = AnalysisAliasComposer.Compose(json.ToJsonString(), Catalog(input), input);

        Assert.True(result.Outcome == ReconciliationOutcome.Invalid, kind);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
        Assert.Null(result.Output);
    }

    [Fact]
    public void The_alias_2_json_requires_the_assessment_list_and_the_alias_2_contract()
    {
        var input = Input();
        var catalog = Catalog(input);
        var missingList = JsonNode.Parse(AnalysisAliasContracts.Encode(N4()))!.AsObject();
        missingList.Remove("gapAssessments");
        var legacyContract = JsonNode.Parse(AnalysisAliasContracts.Encode(N4()))!.AsObject();
        legacyContract["contract"] = "alias-1";

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, AnalysisAliasComposer.Compose(missingList.ToJsonString(), catalog, input).ErrorCode);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, AnalysisAliasComposer.Compose(legacyContract.ToJsonString(), catalog, input).ErrorCode);
        Assert.Equal(ReconciliationOutcome.Complete, AnalysisAliasComposer.Compose(AnalysisAliasContracts.Encode(N4()), catalog, input).Outcome);
    }

    // ---------------------------------------------------------------- contratti che non si incrociano (alias-1 / alias-2)

    [Fact]
    public void An_alias_2_dto_with_an_alias_1_catalog_and_the_opposite_are_checkpoint_incompatible()
    {
        var input = Input();
        var v1Catalog = Catalog(input, AnalysisAliasContracts.SchemaVersion);
        var v2Catalog = Catalog(input);

        var toV1 = Assert.Throws<DocumentWorkException>(() => AnalysisAliasComposer.Compose(N4(), v1Catalog, input));
        var toV2 = Assert.Throws<DocumentWorkException>(() => AnalysisAliasComposer.Compose(AliasFixtures.ValidDto(), v2Catalog, input));

        Assert.Equal(DocumentWorkException.CheckpointIncompatible, toV1.Code);
        Assert.Equal(DocumentWorkException.CheckpointIncompatible, toV2.Code);
        Assert.NotEqual(v1Catalog.CatalogHash, v2Catalog.CatalogHash);
    }

    [Fact]
    public void The_alias_1_json_with_an_alias_2_catalog_never_reaches_the_alias_1_decoder()
    {
        var input = Input();

        var result = AnalysisAliasComposer.Compose(AnalysisAliasContracts.Encode(AliasFixtures.ValidDto()), Catalog(input), input);

        Assert.Equal((ReconciliationOutcome.Invalid, DocumentWorkException.WorkOutputInvalid), (result.Outcome, result.ErrorCode));
    }

    [Fact]
    public void The_alias_1_composition_is_unchanged_a_gap_evidence_still_needs_a_mapping_and_no_assessment_is_ever_required()
    {
        // v3/v4 storici invariati: nessun gap_not_assessed, nessuna esenzione dalla mappa per i Gap, nessuna coerenza §13.4.
        var input = Input();
        var catalog = Catalog(input, AnalysisAliasContracts.SchemaVersion);
        var v1 = AliasFixtures.ValidDto() with
        {
            Items = AliasFixtures.ValidDto().Items with
            {
                Requirements =
                [
                    AliasFixtures.Item("i1", "Servono 45 pezzi al minuto.", DocumentAnalysisClassification.Declared, "E1"),
                    AliasFixtures.Item("i3", "La scheda tecnica indica 30 pezzi al minuto.", DocumentAnalysisClassification.Documented, "E3"),
                    AliasFixtures.Item("i4", "Il turno dura 8 ore.", DocumentAnalysisClassification.Documented, "E4"),
                    AliasFixtures.Item("i5", "Chi approva la modifica?", DocumentAnalysisClassification.Documented, "E5"),
                ],
                Goals = [],
            },
        };

        var unmappedGap = AnalysisAliasComposer.Compose(v1, catalog, input);

        Assert.Equal(ReconciliationOutcome.ValidIncomplete, unmappedGap.Outcome);
        Assert.Equal(["evidence_not_mapped"], unmappedGap.Validation!.Completion!.Gaps.Select(g => g.Code).Distinct());
        Assert.Null(unmappedGap.GapAssessments);
        Assert.Null(unmappedGap.GapAssessmentsHash);

        var mapped = v1 with { Items = v1.Items with { Requirements = [.. v1.Items.Requirements, AliasFixtures.Item("i6", "Cosa dice la scheda?", DocumentAnalysisClassification.Documented, "E2")] } };
        var complete = AnalysisAliasComposer.Compose(mapped, catalog, input);
        Assert.Equal(ReconciliationOutcome.Complete, complete.Outcome);
        Assert.Equal(AnalysisAliasComposer.CanonicalHash(complete.Output!), complete.CanonicalHash);       // forma di hash alias-1 invariata
    }

    // ---------------------------------------------------------------- §13.4 coerenze verificabili

    private static AiAnalysisOutputV2 OpenFinding(AiAnalysisOutputV2 dto, EvidenceRelationType type, string left, string right) => dto with
    {
        Findings = [new() { Key = "f1", View = ReconcileView.A, Left = left, Right = right, Type = type, Basis = EvidenceRelationBasis.Inferred, Rationale = "Posizioni incompatibili.", Outcome = AiFindingOutcome.Open, Reason = "Serve una scelta." }],
    };

    private static AiAnalysisOutputV2 WithoutConflict(AiAnalysisOutputV2 dto) => dto with
    {
        Conflicts = [],
        Items = dto.Items with { Requirements = [.. dto.Items.Requirements, AliasFixtures.Item("i1", "Servono 45 pezzi al minuto.", DocumentAnalysisClassification.Declared, "E1"), AliasFixtures.Item("i2", "La scheda tecnica indica 30 pezzi al minuto.", DocumentAnalysisClassification.Documented, "E3")] },
    };

    [Theory]
    [InlineData(EvidenceRelationType.Contradicts)]
    [InlineData(EvidenceRelationType.AlternativeTo)]
    public void An_open_incompatibility_finding_without_a_representative_open_conflict_is_rejected(EvidenceRelationType type)
    {
        var dto = WithAssessments(OpenFinding(WithoutConflict(N4()), type, "E1", "E3"), Answered("E2", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4"));

        Rejected(dto, "conflitto");
    }

    [Theory]
    [InlineData(EvidenceRelationType.Contradicts)]
    [InlineData(EvidenceRelationType.AlternativeTo)]
    public void An_open_incompatibility_finding_with_a_conflict_that_cites_only_one_endpoint_is_rejected(EvidenceRelationType type)
    {
        var onlyE1 = N4() with
        {
            Conflicts = [new() { Key = "c1", Description = "Solo un termine.", Alternatives = [AliasFixtures.Item("i1", "Servono 45 pezzi al minuto.", DocumentAnalysisClassification.Declared, "E1"), AliasFixtures.Item("i2", "Servono 45 pezzi al minuto, da verificare.", DocumentAnalysisClassification.Inferred, "E1")] }],
            Items = N4().Items with { Requirements = [.. N4().Items.Requirements, AliasFixtures.Item("i5", "La scheda tecnica indica 30 pezzi al minuto.", DocumentAnalysisClassification.Documented, "E3")] },
        };

        Rejected(OpenFinding(onlyE1, type, "E1", "E3"), "conflitto");
    }

    [Theory]
    [InlineData(EvidenceRelationType.Contradicts)]
    [InlineData(EvidenceRelationType.AlternativeTo)]
    public void An_open_incompatibility_finding_represented_by_an_open_conflict_citing_both_endpoints_is_accepted(EvidenceRelationType type)
    {
        Complete(OpenFinding(N4(), type, "E1", "E3"));
    }

    [Theory]
    [InlineData(EvidenceRelationType.Qualifies)]
    [InlineData(EvidenceRelationType.Confirms)]
    [InlineData(EvidenceRelationType.DependsOn)]
    public void An_open_finding_of_another_type_does_not_need_a_conflict(EvidenceRelationType type)
    {
        // Il finding Open resta rappresentato da una domanda aperta come in v3 (open_candidate_not_represented): qui lo è dal conflitto già presente, e senza conflitto da una domanda.
        var dto = WithAssessments(OpenFinding(WithoutConflict(N4()), type, "E1", "E3"), Answered("E2", "E3"), OpenItem("E5", GapReason.NoAnswer, "i4"));
        dto = WithOpenQuestions(dto, AliasFixtures.Item("i4", "Chi approva la modifica?", DocumentAnalysisClassification.Documented, "E5", "E1", "E3"));

        var result = Compose(dto);

        Assert.NotEqual(ReconciliationOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void A_compared_incompatibility_finding_does_not_require_a_conflict()
    {
        var dto = WithoutConflict(N4()) with
        {
            Findings = [new() { Key = "f1", View = ReconcileView.A, Left = "E1", Right = "E3", Type = EvidenceRelationType.Contradicts, Basis = EvidenceRelationBasis.Inferred, Rationale = "45 contro 30.", Outcome = AiFindingOutcome.Compared, Reason = null }],
        };

        Assert.NotEqual(ReconciliationOutcome.Invalid, Compose(dto).Outcome);
    }

    [Fact]
    public void An_exact_duplicate_between_an_ordinary_item_and_a_conflict_alternative_is_rejected()
    {
        var duplicate = N4() with { Items = N4().Items with { Requirements = [.. N4().Items.Requirements, AliasFixtures.Item("i9", "La scheda tecnica indica 30 pezzi al minuto.", DocumentAnalysisClassification.Documented, "E3")] } };

        var result = Rejected(duplicate, "duplica");

        Assert.Contains(result.Violations, v => v.Contains("i9", StringComparison.Ordinal) && v.Contains("i2", StringComparison.Ordinal));
    }

    [Fact]
    public void Newline_normalisation_alone_does_not_hide_an_exact_duplicate_and_nothing_else_is_normalised()
    {
        // Alternativa con «\n», item ordinario con «\r\n»: stesso testo dopo la sola normalizzazione dei newline ⇒ duplicato esatto.
        var lf = N4() with { Conflicts = [N4().Conflicts[0] with { Alternatives = [N4().Conflicts[0].Alternatives[0], N4().Conflicts[0].Alternatives[1] with { Text = "La scheda tecnica\nindica 30 pezzi al minuto." }] }] };
        lf = lf with { Items = lf.Items with { Requirements = [.. lf.Items.Requirements, AliasFixtures.Item("i9", "La scheda tecnica\r\nindica 30 pezzi al minuto.", DocumentAnalysisClassification.Documented, "E3")] } };
        // Un testo con a capo contro uno senza a capo non coincide: i newline non sono spazi, nessun'altra normalizzazione.
        var flat = N4() with { Items = N4().Items with { Requirements = [.. N4().Items.Requirements, AliasFixtures.Item("i9", "La scheda tecnica\r\nindica 30 pezzi al minuto.", DocumentAnalysisClassification.Documented, "E3")] } };

        Rejected(lf, "duplica");
        Assert.NotEqual(ReconciliationOutcome.Invalid, Compose(flat).Outcome);
    }

    [Theory]
    [InlineData("classification")]
    [InlineData("evidence set")]
    [InlineData("paraphrase")]
    [InlineData("trailing space")]
    [InlineData("case")]
    public void Anything_other_than_an_exact_duplicate_is_not_decided_by_the_server_no_fuzzy_matching(string difference)
    {
        var item = difference switch
        {
            "classification" => AliasFixtures.Item("i9", "La scheda tecnica indica 30 pezzi al minuto.", DocumentAnalysisClassification.Inferred, "E3"),
            "evidence set" => AliasFixtures.Item("i9", "La scheda tecnica indica 30 pezzi al minuto.", DocumentAnalysisClassification.Documented, "E3", "E4"),
            "paraphrase" => AliasFixtures.Item("i9", "Secondo la scheda tecnica la cadenza è di 30 pezzi al minuto.", DocumentAnalysisClassification.Documented, "E3"),
            "trailing space" => AliasFixtures.Item("i9", "La scheda tecnica indica 30 pezzi al minuto. ", DocumentAnalysisClassification.Documented, "E3"),
            _ => AliasFixtures.Item("i9", "la scheda tecnica indica 30 pezzi al minuto.", DocumentAnalysisClassification.Documented, "E3"),
        };
        var dto = N4() with { Items = N4().Items with { Requirements = [.. N4().Items.Requirements, item] } };

        var result = Compose(dto);

        Assert.NotEqual(ReconciliationOutcome.Invalid, result.Outcome);              // il gate semantico, non il server, giudica le parafrasi
    }

    [Fact]
    public void The_exact_duplicate_rule_compares_the_evidence_set_not_its_citation_order()
    {
        var reordered = N4() with
        {
            Conflicts = [N4().Conflicts[0] with { Alternatives = [N4().Conflicts[0].Alternatives[0], AliasFixtures.Item("i2", "Il limite è fissato.", DocumentAnalysisClassification.Documented, "E3", "E4")] }],
            Items = N4().Items with { Requirements = [.. N4().Items.Requirements, AliasFixtures.Item("i9", "Il limite è fissato.", DocumentAnalysisClassification.Documented, "E4", "E3")] },
        };

        Rejected(reordered, "duplica");
    }

    // ---------------------------------------------------------------- hash e identità

    [Fact]
    public void The_assessments_are_covered_by_the_ai_hash_the_canonical_hash_the_coverage_hash_and_their_own_hash()
    {
        var baseline = Complete(N4());
        var reworded = Complete(WithAssessments(N4(), Answered("E2", "E3") with { Rationale = "Diversa motivazione." }, OpenItem("E5", GapReason.NoAnswer, "i4")));

        Assert.NotEqual(baseline.AiOutputHash, reworded.AiOutputHash);
        Assert.NotEqual(baseline.GapAssessmentsHash, reworded.GapAssessmentsHash);
        Assert.NotEqual(baseline.CanonicalHash, reworded.CanonicalHash);
        Assert.NotEqual(baseline.Validation!.Completion!.CoverageHash, reworded.Validation!.Completion!.CoverageHash);
        Assert.Equal(AnalysisAliasContracts.Hash(N4()), baseline.AiOutputHash);
        Assert.Equal(AnalysisAliasComposer.CanonicalHash(baseline.Output!, baseline.GapAssessmentsHash!), baseline.CanonicalHash);
        Assert.NotEqual(AnalysisAliasComposer.CanonicalHash(baseline.Output!), baseline.CanonicalHash);           // forma alias-2 distinta da alias-1
        // L'AnalysisContent composto è lo stesso: le valutazioni non lo toccano.
        Assert.Equal(DocumentAnalysisContentJson.Serialize(baseline.Output!.Analysis), DocumentAnalysisContentJson.Serialize(reworded.Output!.Analysis));
        Assert.Equal(baseline.Output.EvidenceMap.Select(m => (m.ItemId, m.EvidenceIds)), reworded.Output!.EvidenceMap.Select(m => (m.ItemId, m.EvidenceIds)));
    }

    [Fact]
    public void The_assessment_hash_does_not_depend_on_the_emission_order_but_the_ai_hash_does()
    {
        var ordered = Complete(N4());
        var swapped = Complete(WithAssessments(N4(), OpenItem("E5", GapReason.NoAnswer, "i4"), Answered("E2", "E3")));

        Assert.Equal(ordered.GapAssessmentsHash, swapped.GapAssessmentsHash);
        Assert.NotEqual(ordered.AiOutputHash, swapped.AiOutputHash);
    }

    [Fact]
    public void The_coverage_hash_covers_the_obligations_so_an_unassessed_gap_differs_from_an_assessed_one()
    {
        var assessed = Complete(N4());
        var unassessed = Incomplete(WithAssessments(N4(), OpenItem("E5", GapReason.NoAnswer, "i4")), "gap_not_assessed");

        Assert.NotEqual(assessed.Validation!.Completion!.CoverageHash, unassessed.Validation!.Completion!.CoverageHash);
    }

    [Fact]
    public void A_recomposed_canonical_that_differs_from_the_committed_one_is_an_integrity_conflict_also_when_only_an_assessment_changed()
    {
        var committed = Complete(N4());
        var tampered = WithAssessments(N4(), Answered("E2", "E3") with { Rationale = "Riscritta dopo il commit." }, OpenItem("E5", GapReason.NoAnswer, "i4"));

        var replay = Compose(N4(), expectedCanonicalHash: committed.CanonicalHash);
        var diverged = Compose(tampered, expectedCanonicalHash: committed.CanonicalHash);

        Assert.Equal(ReconciliationOutcome.Complete, replay.Outcome);
        Assert.Equal((ReconciliationOutcome.Invalid, DocumentWorkException.IntegrityConflict), (diverged.Outcome, diverged.ErrorCode));
    }

    [Fact]
    public void The_composition_is_deterministic_and_the_json_round_trip_gives_the_same_hashes()
    {
        var first = Complete(N4());
        var input = Input();
        var fromJson = AnalysisAliasComposer.Compose(AnalysisAliasContracts.Canonical(N4()), Catalog(input), input);

        Assert.Equal(ReconciliationOutcome.Complete, fromJson.Outcome);
        Assert.Equal((first.AiOutputHash, first.GapAssessmentsHash, first.CanonicalHash, first.Validation!.Completion!.CoverageHash),
            (fromJson.AiOutputHash, fromJson.GapAssessmentsHash, fromJson.CanonicalHash, fromJson.Validation!.Completion!.CoverageHash));
    }

    // ---------------------------------------------------------------- provenienza

    [Fact]
    public void The_assessment_aliases_resolve_in_the_catalog_to_the_unchanged_original_evidence_version_and_sources()
    {
        var input = Input();
        var catalog = Catalog(input);
        var result = Complete(N4());

        var gap = catalog.Evidence(result.GapAssessments![0].Gap)!;
        var support = catalog.Evidence(result.GapAssessments[0].SupportingEvidence[0])!;

        Assert.Equal((EvidenceKind.Gap, input.Evidence[1].EvidenceId, input.Evidence[1].VersionId, input.Evidence[1].ContentHash), (gap.Kind, gap.EvidenceId, gap.EvidenceVersionId, gap.ContentHash));
        Assert.Equal((EvidenceKind.Requirement, input.Evidence[2].EvidenceId, input.Evidence[2].VersionId), (support.Kind, support.EvidenceId, support.EvidenceVersionId));
        Assert.Equal([AliasFixtures.Sa], catalog.SourcesOf(gap).Select(s => s.SourceId));
        Assert.Equal([AliasFixtures.Sx], catalog.SourcesOf(support).Select(s => s.SourceId));
        Assert.Equal(catalog.CatalogHash, result.CatalogHash);
        Assert.Equal(AnalysisAliasContracts.SchemaVersionV2, catalog.Data.ContractVersion);
        // Il riscontro non entra in analysis.sources ne negli EvidenceRefs dell'item del Gap: nessuna valutazione cambia originale, tipo, versione o fonte.
        Assert.Equal(input.Snapshot.Evidence.Select(e => e.ContentHash), catalog.Data.Evidence.Select(e => e.ContentHash));
    }

    [Fact]
    public void An_assessed_gap_does_not_add_a_requirement_support_or_a_confirmation_to_any_item()
    {
        var withAssessment = Complete(N4());
        var withoutAnswer = Complete(WithAssessments(N4(), OpenItem("E2", GapReason.NoAnswer, "i4"), OpenItem("E5", GapReason.NoAnswer, "i4")));

        // Stesso AnalysisContent e stessa mappa item→evidenze qualunque sia la valutazione del Gap: la relazione Gap→riscontro è distinta da item→evidenza.
        Assert.Equal(DocumentAnalysisContentJson.Serialize(withAssessment.Output!.Analysis), DocumentAnalysisContentJson.Serialize(withoutAnswer.Output!.Analysis));
        Assert.Equal(withAssessment.Output!.EvidenceMap.Select(m => (m.ItemId, m.EvidenceIds)), withoutAnswer.Output!.EvidenceMap.Select(m => (m.ItemId, m.EvidenceIds)));
        Assert.All(withAssessment.Output.Analysis.Requirements, r => Assert.Equal(DocumentAnalysisClassification.Documented, r.Classification));
    }

    // ---------------------------------------------------------------- Compose vs. documento JSON di riferimento

    [Fact]
    public void The_json_schema_the_model_receives_requires_the_assessment_list()
    {
        var schema = AnalysisAliasContracts.JsonSchemaV2();

        Assert.Contains("gapAssessments", schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("alias-2", schema.GetProperty("properties").GetProperty("contract").GetProperty("enum")[0].GetString());
    }

    // ---------------------------------------------------------------- 002 T142: composizione senza duplicati (modello finto: struttura, NON comportamento del DGX)

    /// <summary>La struttura corretta del N4 v5: una sola alternativa 45 e una sola 30, entrambe nel conflitto Open; nessun item ordinario con gli stessi valori; E2 (Gap) Answered con E3.</summary>
    [Fact]
    public void T142_the_correct_structure_has_one_45_and_one_30_alternative_in_the_open_conflict_and_no_ordinary_item_repeats_them()
    {
        var result = Complete(N4());

        var analysis = result.Output!.Analysis;
        var conflict = Assert.Single(analysis.Conflicts);
        Assert.Equal(2, conflict.Alternatives.Count);
        Assert.Single(conflict.Alternatives, a => a.Text.Contains("45", StringComparison.Ordinal));
        Assert.Single(conflict.Alternatives, a => a.Text.Contains("30", StringComparison.Ordinal));
        var ordinary = analysis.Goals.Concat(analysis.Requirements).Concat(analysis.OperationalFlows).Concat(analysis.Constraints).Concat(analysis.Dependencies).Concat(analysis.Assumptions).Concat(analysis.OpenQuestions).Select(i => i.Text);
        Assert.DoesNotContain(ordinary, t => t.Contains("45", StringComparison.Ordinal) || t.Contains("30", StringComparison.Ordinal));
        var gap = Assert.Single(result.GapAssessments!, g => g.Outcome == GapOutcome.AnsweredInSnapshot);
        Assert.Equal("E2", gap.Gap);
    }

    /// <summary>
    /// Caratterizzazione della forma osservata sul DGX (item ordinari 30/45 con testo diverso dalle alternative «come da documento»): il controllo deterministico esistente intercetta solo
    /// il duplicato con testo identico (rifiutato più sopra); la riformulazione NON è un duplicato oggettivo. Non si introduce deduplicazione semantica: la prevenzione spetta al prompt v5.
    /// </summary>
    [Fact]
    public void T142_a_reworded_repetition_is_not_an_objective_duplicate_for_the_deterministic_validator_so_prevention_belongs_to_the_prompt()
    {
        var dto = N4() with
        {
            Items = N4().Items with
            {
                Requirements = [AliasFixtures.Item("i3", "Il turno dura 8 ore.", DocumentAnalysisClassification.Documented, "E4"), AliasFixtures.Item("i6", "Servono 45 pezzi al minuto (da dichiarazione).", DocumentAnalysisClassification.Declared, "E1")],
            },
        };

        var result = Compose(dto);

        Assert.Equal(ReconciliationOutcome.Complete, result.Outcome);   // accettato: nessun falso positivo sulle informazioni distinte, nessuna euristica semantica
    }
}

/// <summary>Contratto del testo del prompt `document-analysis-v4` (single-call v5): v3 resta intatto, v4 aggiunge la valutazione dei Gap senza mai istruire un supporto automatico.</summary>
public sealed class AnalysisAliasPromptV4ContractTests
{
    private static string V4 => DocumentAnalysisWorkflow.InstructionsV4.Replace("\r\n", "\n");

    private static string V3 => DocumentAnalysisWorkflow.InstructionsV3.Replace("\r\n", "\n");

    [Fact]
    public void The_v3_prompt_is_untouched_and_knows_nothing_about_gap_assessments()
    {
        Assert.Contains("ESATTAMENTE queste otto proprietà", V3, StringComparison.Ordinal);
        Assert.Contains("`\"alias-1\"`", V3, StringComparison.Ordinal);
        Assert.DoesNotContain("gapAssessments", V3, StringComparison.Ordinal);
        Assert.DoesNotContain("alias-2", V3, StringComparison.Ordinal);
        Assert.NotEqual(V3, V4);
        Assert.Equal("document-analysis-v4", DocumentAnalysisWorkflow.PromptVersionV4);
        Assert.Equal("document-analysis-v3", DocumentAnalysisWorkflow.PromptVersionV3);
    }

    [Fact]
    public void The_v4_prompt_declares_the_alias_2_contract_and_the_nine_properties_including_the_assessment_list()
    {
        Assert.Contains("ESATTAMENTE queste nove proprietà", V4, StringComparison.Ordinal);
        Assert.Contains("`gapAssessments`", V4, StringComparison.Ordinal);
        Assert.Contains("`\"alias-2\"`", V4, StringComparison.Ordinal);
        Assert.DoesNotContain("`\"alias-1\"`", V4, StringComparison.Ordinal);
        Assert.DoesNotContain("ESATTAMENTE queste otto proprietà", V4, StringComparison.Ordinal);
    }

    [Fact]
    public void The_v4_prompt_requires_one_record_per_gap_even_when_the_list_is_empty_and_states_the_record_shape()
    {
        foreach (var fragment in new[]
        {
            "un record per OGNI evidenza con `tipo=Gap`", "`[]` solo se nessuna evidenza ha `tipo=Gap`", "`AnsweredInSnapshot`", "`Answered`", "`NoAnswer`", "`Partial`", "`Ambiguous`",
            "`supportingEvidence`", "`openItem`", "`openConflict`", "`rationale`",
        })
        {
            Assert.Contains(fragment, V4, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_v4_prompt_keeps_gaps_out_of_supports_and_forbids_closing_a_question_with_another_question_or_by_proximity()
    {
        Assert.Contains("mai un altro Gap", V4, StringComparison.Ordinal);
        Assert.Contains("Non scrivere una domanda aperta che ripete un Gap già riscontrato", V4, StringComparison.Ordinal);
        Assert.Contains("non aggiunge il riscontro agli elementi", V4, StringComparison.Ordinal);
    }

    [Fact]
    public void The_v4_prompt_requires_an_open_conflict_representing_both_endpoints_for_an_open_incompatibility_finding_and_forbids_exact_duplicates()
    {
        Assert.Contains("conflitto che cita entrambe le evidenze", V4, StringComparison.Ordinal);
        Assert.Contains("stesso testo, stessa classificazione e stesse evidenze", V4, StringComparison.Ordinal);
    }

    [Fact]
    public void The_v4_prompt_has_no_copyable_concrete_known_relation_alias_and_keeps_the_label_and_key_rules_of_v3()
    {
        Assert.DoesNotMatch(@"R\d+", V4);
        Assert.Contains("`Relazioni note: nessuna`", V4, StringComparison.Ordinal);
        Assert.Contains("ogni chiave locale è usata UNA SOLA volta in tutto il documento JSON, in qualsiasi sezione", V4, StringComparison.Ordinal);
        Assert.Contains("mai le etichette `S<n>`", V4, StringComparison.Ordinal);
        Assert.Contains("| solo `origine=Document` | `Documented`, `Inferred`, `Assumed` (MAI `Declared`) |", V4, StringComparison.Ordinal);
        Assert.DoesNotContain("certezza", V4, StringComparison.OrdinalIgnoreCase);
    }
}
