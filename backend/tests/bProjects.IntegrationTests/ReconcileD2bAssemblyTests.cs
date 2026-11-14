using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-021 (M2, T169), test (b)1–(b)12 della D2b — scritti PRIMA di modificare l'assembly. Regola (decisioni dell'autore del 2026-11-14): nell'assembly v11 un item proiettato la cui UNICA
/// evidenza è estremo di un conflitto <c>Open</c> e che non è bersaglio di patch accettate non entra nelle sezioni (l'evidenza resta mappata tramite l'alternativa), con le SOLE ECCEZIONI della domanda
/// canonica di un Gap (D2b-1) e dell'elemento <c>ToClarify</c> (D2b-2, decisione definitiva dell'autore): restano in <c>openQuestions</c>. Un'<c>Alternative</c> estremo Open è rappresentata soltanto come
/// alternativa del conflitto (salvo sia anche <c>ToClarify</c>); un'<c>Alternative</c> <c>Hypothesized</c>, proiettata in <c>assumptions</c>, è esclusa come ogni assunzione contestata. Nessun test usa la copertura come criterio di qualità: <c>MissingGaps</c> vuoto e assenza di
/// <c>evidence_not_mapped</c> sono condizioni NECESSARIE, non sufficienti, per la documentazione finale (livelli 7–8). Vincolo fondamentale: un candidato che collega un Gap a un'evidenza potenzialmente
/// risolutiva NON dimostra una contraddizione fra requisiti; la risposta al Gap è valutata dallo stadio Gap: l'assembly non risolve nulla (conflitto sempre <c>Open</c>, <c>Resolution</c> nulla).
/// Offline: motore reale con modello finto e assembly per riflessione, nessun database, nessuna inferenza.
/// </summary>
public sealed class ReconcileD2bAssemblyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Reason = "5 ordini nel documento contro 8 dichiarati: alternative integrali da sottoporre al PM";
    private const string RepeatsAlternative = "item_repeats_conflict_alternative";

    private static Task<(V11Rig Rig, AssembledV11 Assembled)> OpenAsync(Func<V11Rig, ReconcileBatchRequest, string>? dto, params (int A, int B)[] pairs) => OpenAsync(null, dto, pairs);

    /// <summary>Come sopra su un ledger personalizzato (le evidenze di <paramref name="specs"/> al posto di quelle standard).</summary>
    private static async Task<(V11Rig Rig, AssembledV11 Assembled)> OpenAsync(IReadOnlyList<M2Evidence>? specs, Func<V11Rig, ReconcileBatchRequest, string>? dto, params (int A, int B)[] pairs)
    {
        var input = M2Ledger.Input(specs);
        var rig = await V11Rig.CreateAsync(input, dto ?? ((_, _) => V11Rig.CompareOpen(Reason)), Ct);
        foreach (var (a, b) in pairs)
        {
            var focus = M2Ledger.Pair(input, a, b);
            rig.Engine.Add(focus, ReconcileView.A);
            await rig.Engine.ProcessAsync(focus.CandidateId, Ct);
        }

        return (rig, rig.Assemble());
    }

    private static Guid IdOf(V11Rig rig, int n) => rig.Input.Evidence[n - 1].EvidenceId;

    private static bool Cites(AssembledV11 a, Guid itemId, Guid evidenceId) => a.Map.Any(m => m.ItemId == itemId && m.EvidenceIds.Contains(evidenceId));

    /// <summary>Domanda canonica (item di <c>openQuestions</c>) che cita l'evidenza; più di una è un errore del test (la domanda deve restare UNA).</summary>
    private static DocumentAnalysisItem? QuestionOf(V11Rig rig, AssembledV11 a, int n) => a.Analysis.OpenQuestions.SingleOrDefault(q => Cites(a, q.Id, IdOf(rig, n)));

    /// <summary>Item di una sezione (qualsiasi, openQuestions compresa) che cita l'evidenza.</summary>
    private static List<(string Section, DocumentAnalysisItem Item)> SectionsOf(V11Rig rig, AssembledV11 a, int n) =>
        [.. V11Rig.SectionItems(a.Analysis).Where(t => Cites(a, t.Item.Id, IdOf(rig, n)))];

    private static List<(string Section, DocumentAnalysisItem Item)> StructuralOf(V11Rig rig, AssembledV11 a, int n) =>
        [.. SectionsOf(rig, a, n).Where(t => t.Section != "openQuestions")];

    private static string Signature(DocumentAnalysisItem i) => $"{i.Id:D}|{i.Text}|{i.Classification}|{string.Join(',', i.EvidenceRefs.Select(r => r.ToString("D")))}";

    private static IReadOnlyList<string> OpenConflictAlternatives(AssembledV11 a) =>
        [.. a.Analysis.Conflicts.Where(c => c.Description == Reason).SelectMany(c => c.Alternatives.Select(Signature)).Order(StringComparer.Ordinal)];

    // ================================================================ (b)1 Gap del PM estremo di un conflitto Open con un'evidenza non Gap

    [Fact]
    public async Task B1_a_pm_gap_that_is_an_endpoint_of_an_open_conflict_keeps_its_question_and_the_conflict_keeps_both_alternatives()
    {
        var (rig, a) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.LimitFive));
        var gap = rig.Input.Evidence[M2Ledger.GapPm - 1];
        var limit = rig.Input.Evidence[M2Ledger.LimitFive - 1];

        var question = Assert.IsType<DocumentAnalysisItem>(QuestionOf(rig, a, M2Ledger.GapPm));
        Assert.Equal(gap.Statement, question.Text);                                      // la domanda canonica è l'enunciato del Gap, non una riscrittura
        var references = rig.References(a);
        Assert.Empty(references.MissingGaps);
        Assert.Equal("i" + (a.Analysis.OpenQuestions.ToList().FindIndex(q => q.Id == question.Id) + 1), references.GapQuestion["E1"]);

        var conflict = Assert.Single(a.Analysis.Conflicts);
        Assert.Equal((AnalysisConflictStatus.Open, null), (conflict.Status, conflict.Resolution));
        Assert.Equal(Reason, conflict.Description);
        Assert.Equal(new[] { gap.Statement, limit.Statement }.Order(StringComparer.Ordinal), conflict.Alternatives.Select(x => x.Text).Order(StringComparer.Ordinal));

        // L'evidenza NON Gap (unica evidenza del suo item, estremo Open) non è nelle sezioni: resta visibile come alternativa.
        Assert.Empty(SectionsOf(rig, a, M2Ledger.LimitFive));
        Assert.Contains(conflict.Alternatives, x => x.Text == limit.Statement);

        // Vincolo: nessuna scelta automatica, nessun requisito acquisito da una coppia Gap–evidenza.
        Assert.DoesNotContain(a.Analysis.Requirements, r => r.Text == limit.Statement || r.Text == gap.Statement);
    }

    // ================================================================ (b)2 Gap contro Gap

    [Fact]
    public async Task B2_gap_against_gap_keeps_both_questions()
    {
        var (rig, a) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.GapDoc));

        var q1 = Assert.IsType<DocumentAnalysisItem>(QuestionOf(rig, a, M2Ledger.GapPm));
        var q2 = Assert.IsType<DocumentAnalysisItem>(QuestionOf(rig, a, M2Ledger.GapDoc));
        Assert.NotEqual(q1.Id, q2.Id);
        Assert.Empty(rig.References(a).MissingGaps);
        Assert.Equal(["E1", "E2"], rig.References(a).GapQuestion.Keys.Order(StringComparer.Ordinal));
        var conflict = Assert.Single(a.Analysis.Conflicts);
        Assert.Equal(2, conflict.Alternatives.Count);
        Assert.Equal((AnalysisConflictStatus.Open, null), (conflict.Status, conflict.Resolution));
    }

    // ================================================================ (b)3 Gap estremo di due conflitti Open

    [Fact]
    public async Task B3_a_gap_that_is_an_endpoint_of_two_open_conflicts_keeps_one_question_once_and_both_conflicts()
    {
        var (rig, a) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.LimitFive), (M2Ledger.GapPm, M2Ledger.LimitEight));

        var question = Assert.IsType<DocumentAnalysisItem>(QuestionOf(rig, a, M2Ledger.GapPm));   // SingleOrDefault: una sola volta
        Assert.Equal(1, a.Analysis.OpenQuestions.Count(q => q.Text == rig.Input.Evidence[M2Ledger.GapPm - 1].Statement));
        Assert.Equal(2, a.Analysis.Conflicts.Count);
        Assert.Empty(rig.References(a).MissingGaps);
        Assert.Equal(2, rig.References(a).Conflicts.Count);
        // Gli item dei due requisiti non Gap (unica evidenza, estremi Open) sono fuori dalle sezioni ma visibili come alternative.
        Assert.Empty(SectionsOf(rig, a, M2Ledger.LimitFive));
        Assert.Empty(SectionsOf(rig, a, M2Ledger.LimitEight));
        Assert.Equal(2, a.Analysis.Conflicts.Count(c => c.Alternatives.Any(x => x.Text == rig.Input.Evidence[M2Ledger.GapPm - 1].Statement)));
        Assert.NotNull(question);
    }

    // ================================================================ (b)4 Gap estremo Open e bersaglio di patch accettate: Applied, Duplicate, PreservedAsConflict

    public enum PatchCase { Applied, Duplicate, PreservedAsConflict }

    private static Func<V11Rig, ReconcileBatchRequest, string> WithPatches(PatchCase kind, int targetEvidence) => (rig, request) =>
    {
        var target = rig.HandleOf(targetEvidence, request);
        JsonObject[] patches = kind switch
        {
            PatchCase.Applied => [V11Rig.AddRelation("p1", target, "E1", "E3", "Confirms", "stesso tema")],
            PatchCase.Duplicate => [V11Rig.AddRelation("p1", target, "E1", "E3", "Confirms", "motivo A"), V11Rig.AddRelation("p2", target, "E1", "E3", "Confirms", "motivo B")],
            _ => [V11Rig.ProposeAlternative("p1", target, "Il limite è 6 ordini", "E1", "E3"), V11Rig.ProposeAlternative("p2", target, "Il limite è 7 ordini", "E1", "E3")],
        };
        return V11Rig.CompareOpen(Reason, patches);
    };

    [Theory]
    [InlineData(PatchCase.Applied, RunPatchDispositionKind.Applied)]
    [InlineData(PatchCase.Duplicate, RunPatchDispositionKind.Duplicate)]
    [InlineData(PatchCase.PreservedAsConflict, RunPatchDispositionKind.PreservedAsConflict)]
    public async Task B4_a_gap_that_is_an_open_endpoint_and_the_target_of_an_accepted_patch_keeps_its_question_and_no_patch_alters_the_question_or_the_alternatives(PatchCase kind, RunPatchDispositionKind expected)
    {
        var (baselineRig, baseline) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.LimitFive));
        var (rig, a) = await OpenAsync(WithPatches(kind, M2Ledger.GapPm), (M2Ledger.GapPm, M2Ledger.LimitFive));

        Assert.Contains(a.Patches, d => d.Kind == expected);                              // lo scenario esercita davvero la disposizione dichiarata
        Assert.All(a.Patches, d => Assert.True(d.Kind is RunPatchDispositionKind.Applied or RunPatchDispositionKind.Duplicate or RunPatchDispositionKind.PreservedAsConflict, d.Reason));
        var question = Assert.IsType<DocumentAnalysisItem>(QuestionOf(rig, a, M2Ledger.GapPm));
        Assert.Equal(Signature(Assert.IsType<DocumentAnalysisItem>(QuestionOf(baselineRig, baseline, M2Ledger.GapPm))), Signature(question));   // la domanda è quella senza patch
        Assert.Empty(rig.References(a).MissingGaps);
        Assert.Equal(OpenConflictAlternatives(baseline), OpenConflictAlternatives(a));    // le alternative del conflitto Open sono invariate
        Assert.Contains(a.Analysis.Conflicts, c => c.Description == Reason && c.Status == AnalysisConflictStatus.Open && c.Resolution is null);
    }

    // ================================================================ (b)5 evidenza non Gap, unica dell'item, estremo Open: l'item è escluso

    [Fact]
    public async Task B5_a_non_gap_item_whose_only_evidence_is_an_open_endpoint_is_excluded_and_the_evidence_stays_mapped_through_the_alternative()
    {
        var (rig, a) = await OpenAsync(null, (M2Ledger.LimitFive, M2Ledger.LimitEight));

        Assert.Empty(SectionsOf(rig, a, M2Ledger.LimitFive));
        Assert.Empty(SectionsOf(rig, a, M2Ledger.LimitEight));
        var conflict = Assert.Single(a.Analysis.Conflicts);
        Assert.Equal(rig.Input.Evidence.Select(e => e.Statement).Where(s => s.StartsWith("Il limite giornaliero", StringComparison.Ordinal)).Order(StringComparer.Ordinal), conflict.Alternatives.Select(x => x.Text).Order(StringComparer.Ordinal));
        // Evidenza ancora mappata (tramite le alternative): nessun evidence_not_mapped, nessun avviso di ripetizione, nessuna violazione del contenuto.
        foreach (var n in new[] { M2Ledger.LimitFive, M2Ledger.LimitEight })
        {
            Assert.Contains(a.Map, m => m.EvidenceIds.Contains(IdOf(rig, n)) && conflict.Alternatives.Any(x => x.Id == m.ItemId));
        }

        var findings = rig.Findings(a);
        Assert.DoesNotContain(findings, f => f.Code == "evidence_not_mapped");
        Assert.DoesNotContain(findings, f => f.Code == RepeatsAlternative);
        Assert.Empty(rig.ContentViolations(a));
        Assert.Empty(rig.References(a).MissingGaps);
    }

    [Fact]
    public async Task B5b_a_non_gap_item_that_is_the_target_of_an_accepted_patch_is_not_excluded()
    {
        // L'item di E3 è bersaglio di una patch Applied: resta nelle sezioni (la regola esclude solo gli item NON bersaglio di patch accettate); quello di E4, no, è escluso.
        var dto = (V11Rig rig, ReconcileBatchRequest request) => V11Rig.CompareOpen(Reason, V11Rig.AddRelation("p1", rig.HandleOf(M2Ledger.LimitFive, request), "E3", "E4", "Confirms", "stesso tema"));
        var (rig, a) = await OpenAsync(dto, (M2Ledger.LimitFive, M2Ledger.LimitEight));

        Assert.Contains(a.Patches, d => d.Kind == RunPatchDispositionKind.Applied);
        Assert.Single(StructuralOf(rig, a, M2Ledger.LimitFive));
        Assert.Empty(SectionsOf(rig, a, M2Ledger.LimitEight));
        Assert.Single(a.Analysis.Conflicts);
    }

    // ================================================================ (b)6 item di openQuestions non Gap: ToClarify, Alternative (D2b-2); ipotesi (assumptions)

    [Fact]
    public async Task B6_a_to_clarify_evidence_that_is_an_open_endpoint_stays_an_open_question_and_is_never_turned_into_an_acquired_requirement()
    {
        var (rig, a) = await OpenAsync(null, (M2Ledger.ToClarify, M2Ledger.LimitFive));
        var toClarify = rig.Input.Evidence[M2Ledger.ToClarify - 1];

        var question = Assert.IsType<DocumentAnalysisItem>(QuestionOf(rig, a, M2Ledger.ToClarify));   // D2b-2: non eliminata da openQuestions
        Assert.Equal(toClarify.Statement, question.Text);
        Assert.Empty(StructuralOf(rig, a, M2Ledger.ToClarify));                                       // e mai un requisito acquisito
        Assert.DoesNotContain(a.Analysis.Requirements, r => r.Text == toClarify.Statement);
        var conflict = Assert.Single(a.Analysis.Conflicts);
        Assert.Contains(conflict.Alternatives, x => x.Text == toClarify.Statement);                   // non considerata risolta perché compare fra le alternative
        Assert.Equal((AnalysisConflictStatus.Open, null), (conflict.Status, conflict.Resolution));
        Assert.Empty(SectionsOf(rig, a, M2Ledger.LimitFive));                                         // l'evidenza non Gap resta soggetta alla regola D2b
        Assert.Empty(rig.References(a).MissingGaps);
    }

    [Fact]
    public async Task B6b_an_alternative_evidence_that_is_an_open_endpoint_is_represented_only_as_the_conflict_alternative()
    {
        // D2b-2 definitiva (autore, 2026-11-14): un'Alternative estremo di un conflitto Open può essere rappresentata soltanto come alternativa del conflitto, salvo debba essere conservata anche perché
        // ToClarify o Gap. Nessuna domanda in openQuestions, nessuna sezione strutturale; l'evidenza resta mappata tramite l'alternativa.
        var (rig, a) = await OpenAsync(null, (M2Ledger.Alternative, M2Ledger.LimitFive));
        var alternative = rig.Input.Evidence[M2Ledger.Alternative - 1];

        Assert.Empty(SectionsOf(rig, a, M2Ledger.Alternative));                                          // né openQuestions né una sezione strutturale
        Assert.Null(QuestionOf(rig, a, M2Ledger.Alternative));
        var conflict = Assert.Single(a.Analysis.Conflicts);
        Assert.Contains(conflict.Alternatives, x => x.Text == alternative.Statement);
        Assert.Equal((AnalysisConflictStatus.Open, null), (conflict.Status, conflict.Resolution));
        Assert.Contains(a.Map, m => m.EvidenceIds.Contains(alternative.EvidenceId) && conflict.Alternatives.Any(x => x.Id == m.ItemId));
        var findings = rig.Findings(a);
        Assert.DoesNotContain(findings, f => f.Code == "evidence_not_mapped");
        Assert.DoesNotContain(findings, f => f.Code == RepeatsAlternative);                              // nessuna ripetizione: l'evidenza compare una sola volta, come alternativa
        Assert.Empty(rig.ContentViolations(a));
    }

    [Fact]
    public async Task B6e_an_alternative_evidence_that_is_also_to_clarify_keeps_its_open_question_because_of_the_to_clarify_nature()
    {
        var specs = M2Ledger.Specs.ToList();
        specs[M2Ledger.Alternative - 1] = new(EvidenceOriginKind.Document, EvidenceKind.Alternative, EvidenceCertainty.ToClarify, "In alternativa gli ordini arrivano dal portale web, da chiarire con il cliente.");
        var (rig, a) = await OpenAsync(specs, null, (M2Ledger.Alternative, M2Ledger.LimitFive));
        var evidence = rig.Input.Evidence[M2Ledger.Alternative - 1];

        var question = Assert.IsType<DocumentAnalysisItem>(QuestionOf(rig, a, M2Ledger.Alternative));
        Assert.Equal(evidence.Statement, question.Text);
        Assert.Empty(StructuralOf(rig, a, M2Ledger.Alternative));
        Assert.Contains(Assert.Single(a.Analysis.Conflicts).Alternatives, x => x.Text == evidence.Statement);
    }

    [Fact]
    public async Task B6f_an_alternative_evidence_that_is_hypothesized_is_projected_in_assumptions_and_when_contested_it_is_excluded_from_that_section()
    {
        var specs = M2Ledger.Specs.ToList();
        specs[M2Ledger.Alternative - 1] = new(EvidenceOriginKind.Document, EvidenceKind.Alternative, EvidenceCertainty.Hypothesized, "Si ipotizza che in alternativa gli ordini arrivino dal portale web.");
        var evidence = M2Ledger.EvidenceOf(M2Ledger.Alternative, specs[M2Ledger.Alternative - 1]);

        // Senza conflitto: l'item è nelle assunzioni (la certezza prevale sul tipo Alternative).
        var (restRig, rest) = await OpenAsync(specs, null);
        Assert.Equal(["assumptions"], SectionsOf(restRig, rest, M2Ledger.Alternative).Select(t => t.Section));

        // Estremo contestato: escluso dalla sezione ordinaria (D2b), mai in openQuestions, resta come alternativa del conflitto.
        var (rig, a) = await OpenAsync(specs, null, (M2Ledger.Alternative, M2Ledger.LimitFive));
        Assert.Empty(SectionsOf(rig, a, M2Ledger.Alternative));
        Assert.Null(QuestionOf(rig, a, M2Ledger.Alternative));
        Assert.Contains(Assert.Single(a.Analysis.Conflicts).Alternatives, x => x.Text == evidence.Statement);
        Assert.DoesNotContain(rig.Findings(a), f => f.Code == "evidence_not_mapped");
    }

    [Fact]
    public async Task B6c_a_hypothesis_that_is_an_open_endpoint_is_excluded_from_assumptions_and_stays_visible_as_an_alternative_never_as_an_acquired_assumption()
    {
        var (rig, a) = await OpenAsync(null, (M2Ledger.Hypothesis, M2Ledger.LimitFive));
        var hypothesis = rig.Input.Evidence[M2Ledger.Hypothesis - 1];

        Assert.Empty(SectionsOf(rig, a, M2Ledger.Hypothesis));
        var conflict = Assert.Single(a.Analysis.Conflicts);
        Assert.Contains(conflict.Alternatives, x => x.Text == hypothesis.Statement);
        Assert.DoesNotContain(a.Analysis.Assumptions, i => i.Text == hypothesis.Statement);
        Assert.DoesNotContain(rig.Findings(a), f => f.Code == "evidence_not_mapped");
    }

    [Fact]
    public async Task B6d_the_open_question_references_are_deterministic_at_every_replay()
    {
        var (rigA, first) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.LimitFive), (M2Ledger.ToClarify, M2Ledger.LimitEight));
        var (rigB, second) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.LimitFive), (M2Ledger.ToClarify, M2Ledger.LimitEight));

        Assert.Equal(first.Analysis.OpenQuestions.Select(q => q.Id), second.Analysis.OpenQuestions.Select(q => q.Id));
        Assert.Equal(rigA.References(first).Hash(), rigB.References(second).Hash());
        Assert.Equal(DocumentAnalysisContentJson.Serialize(first.Analysis), DocumentAnalysisContentJson.Serialize(second.Analysis));
    }

    // ================================================================ (b)7 negativo di caratterizzazione: D2b SENZA l'eccezione

    /// <summary>
    /// D2b come scritta in origine (plan §3, senza eccezione): dall'assembly senza politica (nessun item escluso) toglie dalle sezioni OGNI item la cui unica evidenza è estremo di un conflitto Open, Gap compresi, e
    /// dalla mappa le sue voci. Simulazione di TEST: nessun codice di produzione la implementa mai.
    /// </summary>
    internal static (DocumentAnalysisContent Analysis, IReadOnlyList<EvidenceMapEntry> Map) NaiveD2b(V11Rig rig, AssembledV11 unfiltered)
    {
        var open = rig.Engine.Result().Dispositions.Where(d => d.State == CandidateState.Open).Select(d => rig.Engine.Candidate(d.CandidateId))
            .SelectMany(c => new[] { c.Left, c.Right }).Select(e => Guid.Parse(e.Id)).ToHashSet();
        var dropped = unfiltered.Analysis.OpenQuestions.Concat(unfiltered.Analysis.Requirements).Concat(unfiltered.Analysis.Assumptions)
            .Where(i => unfiltered.Map.Where(m => m.ItemId == i.Id).SelectMany(m => m.EvidenceIds).Distinct().ToList() is [var only] && open.Contains(only)).Select(i => i.Id).ToHashSet();
        IReadOnlyList<DocumentAnalysisItem> Keep(IReadOnlyList<DocumentAnalysisItem> items) => [.. items.Where(i => !dropped.Contains(i.Id))];
        var a = unfiltered.Analysis;
        var naive = new DocumentAnalysisContent
        {
            SchemaVersion = a.SchemaVersion, AnalysisId = a.AnalysisId, ProjectId = a.ProjectId, SourceCutoffSequence = a.SourceCutoffSequence, ValidationState = a.ValidationState, Sources = a.Sources,
            Goals = Keep(a.Goals), ContextAndScope = new DocumentAnalysisContextAndScope { Overview = Keep(a.ContextAndScope.Overview), Included = Keep(a.ContextAndScope.Included), Excluded = Keep(a.ContextAndScope.Excluded) },
            Requirements = Keep(a.Requirements), OperationalFlows = Keep(a.OperationalFlows), Constraints = Keep(a.Constraints), Dependencies = Keep(a.Dependencies), Assumptions = Keep(a.Assumptions),
            OpenQuestions = Keep(a.OpenQuestions), Conflicts = a.Conflicts,
        };
        return (naive, [.. unfiltered.Map.Where(m => !dropped.Contains(m.ItemId))]);
    }

    [Fact]
    public async Task B7_negative_d2b_applied_without_the_gap_exception_makes_the_gap_question_disappear_and_the_gap_stage_precondition_fails()
    {
        var (rig, withException) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.LimitFive));
        var unfiltered = rig.Assemble(openEndpoints: null);               // assembly senza politica: tutti gli item proiettati restano
        var (naiveAnalysis, naiveMap) = NaiveD2b(rig, unfiltered);

        // Il difetto che l'eccezione evita: senza la domanda canonica il Gap non è offerto allo stadio Gap ⇒ `gap_reference_missing` (coverage_incomplete, DocumentReconcilePlan: «gap_reference_missing»).
        var naive = GapStageReferences.From(naiveAnalysis, naiveMap, rig.RunCatalog);
        Assert.Equal(["E1"], naive.MissingGaps);
        Assert.False(naive.GapQuestion.ContainsKey("E1"));
        Assert.Null(naiveAnalysis.OpenQuestions.FirstOrDefault(q => q.Text == rig.Input.Evidence[M2Ledger.GapPm - 1].Statement));

        // Con la regola D2b implementata (eccezione R-D2b-1) lo stesso scenario non ha Gap mancanti.
        Assert.Empty(rig.References(withException).MissingGaps);
        Assert.True(rig.References(withException).GapQuestion.ContainsKey("E1"));
    }

    // ================================================================ (b)8 D3: l'avviso strutturale per un Gap estremo è atteso e non bloccante

    [Fact]
    public async Task B8_for_a_gap_endpoint_the_structural_repetition_warning_is_always_produced_once_per_identical_alternative_and_is_never_blocking()
    {
        var (rig, a) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.LimitFive));
        var question = Assert.IsType<DocumentAnalysisItem>(QuestionOf(rig, a, M2Ledger.GapPm));

        var findings = rig.Findings(a);
        var warning = Assert.Single(findings, f => f.Code == RepeatsAlternative);                 // FINDING ATTESO (D2b-3, transitorio): prodotto nella v11, registrato in «Evidenze M2»
        Assert.Equal(FindingCategory.SemanticRisk, warning.Category);
        Assert.False(warning.Blocking);
        Assert.Equal(question.Id, warning.ItemId);
        Assert.Equal("openQuestions", warning.Section);
        Assert.Contains(a.Analysis.Conflicts.Single().Alternatives, x => x.Id.ToString() == warning.Details);

        // Nessun altro finding bloccante, nessuna evidenza non mappata, la `Coherence` di alias-2 non si applica alla gerarchia (il contenuto non è rifiutato).
        Assert.DoesNotContain(findings, f => f.Blocking);
        Assert.DoesNotContain(findings, f => f.Code == "evidence_not_mapped");
        Assert.Empty(rig.ContentViolations(a));
        var summary = DocumentCoverage.Summarize(rig.CoverageInput(a), findings);
        Assert.Equal(0, summary.BlockingFindings);
        Assert.True(summary.SemanticRisks >= 1);

        // Due conflitti con lo stesso Gap ⇒ due avvisi (uno per ciascuna alternativa identica): ripetizione strutturale documentata, mai una deduplicazione silenziosa.
        var (rig2, two) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.LimitFive), (M2Ledger.GapPm, M2Ledger.LimitEight));
        var twoFindings = rig2.Findings(two);
        Assert.Equal(2, twoFindings.Count(f => f.Code == RepeatsAlternative));
        Assert.DoesNotContain(twoFindings, f => f.Blocking);
    }

    // ================================================================ (b)9 provenienza conservata

    [Fact]
    public async Task B9_the_gap_question_keeps_the_sources_of_the_entry_and_nothing_is_added_or_lost_compared_with_the_v10_path()
    {
        var (rig, a) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.LimitFive));
        var (_, v10Like) = (rig, rig.Assemble(openEndpoints: null));                               // stesso candidato, percorso v10 (nessun item escluso)
        var gap = rig.Input.Evidence[M2Ledger.GapPm - 1];

        var question = Assert.IsType<DocumentAnalysisItem>(QuestionOf(rig, a, M2Ledger.GapPm));
        Assert.Equal(gap.Sources.Select(s => s.SourceId).Order(), question.EvidenceRefs.Order());   // le fonti della voce nello snapshot
        var leaf = rig.Calls.First(c => c.Batch.Stage == "leaf" && c.View == ReconcileView.A);
        var entry = leaf.Entries.Single(e => e.EvidenceId == gap.EvidenceId);
        Assert.Equal(ReconciliationIds.SortedIds(entry.SourceIds.Distinct()).Select(Guid.Parse), question.EvidenceRefs);   // stesso ordine di SortedSourceIds
        var mapEntry = Assert.Single(a.Map, m => m.ItemId == question.Id);
        Assert.Equal([gap.EvidenceId], mapEntry.EvidenceIds);                                       // una voce di EvidenceMap con quella sola evidenza

        var position = leaf.Entries.ToList().FindIndex(e => e.EvidenceId == gap.EvidenceId) + 1;
        var expectedId = AnalysisAliasComposer.CanonicalId("item", rig.Input.AnalysisId, rig.Input.Snapshot.SourceHash, ReconcileAliasComposer.LocalKey(leaf, "p" + position));
        Assert.Equal(expectedId, question.Id);                                                      // id canonico del server, mai del modello, identico a ogni replay

        // Le alternative portano le stesse fonti dell'enunciato; l'insieme item/conflitti/mappa dell'evidenza Gap coincide con il percorso v10.
        var conflict = Assert.Single(a.Analysis.Conflicts);
        Assert.Equal(gap.Sources.Select(s => s.SourceId).Order(), conflict.Alternatives.Single(x => x.Text == gap.Statement).EvidenceRefs.Order());
        Assert.Equal(Signature(question), Signature(Assert.IsType<DocumentAnalysisItem>(QuestionOf(rig, v10Like, M2Ledger.GapPm))));
        Assert.Equal(OpenConflictAlternatives(v10Like), OpenConflictAlternatives(a));
        Assert.Equal(v10Like.Analysis.Conflicts.Select(c => c.Id), a.Analysis.Conflicts.Select(c => c.Id));
        Assert.Equal(v10Like.Analysis.Sources.Select(s => s.Id).Order(), a.Analysis.Sources.Select(s => s.Id).Order());   // nessuna fonte aggiunta o persa
    }

    // ================================================================ (b)10 nessuna decisione contestata presentata come acquisita

    [Fact]
    public async Task B10_a_contested_decision_is_never_presented_as_acquired_and_the_conflict_stays_open_with_no_automatic_choice()
    {
        var (rig, a) = await OpenAsync(null, (M2Ledger.Decision, M2Ledger.GapPm));
        var decision = rig.Input.Evidence[M2Ledger.Decision - 1];

        // (i) la Decision estremo di un conflitto Open con un Gap non è in alcuna sezione strutturale (né requirements): solo come alternativa; il Gap resta in openQuestions.
        Assert.Empty(StructuralOf(rig, a, M2Ledger.Decision));
        Assert.DoesNotContain(a.Analysis.Requirements, r => r.Text == decision.Statement);
        Assert.NotNull(QuestionOf(rig, a, M2Ledger.GapPm));
        var conflict = Assert.Single(a.Analysis.Conflicts);
        Assert.Contains(conflict.Alternatives, x => x.Text == decision.Statement);

        // (ii) per ogni conflitto Open nessuna alternativa coincide con un item di una sezione STRUTTURALE (la sola ripetizione ammessa è quella del Gap in openQuestions, D2b-3).
        var structural = V11Rig.SectionItems(a.Analysis).Where(t => t.Section != "openQuestions").Select(t => t.Item.Text).ToHashSet(StringComparer.Ordinal);
        foreach (var c in a.Analysis.Conflicts.Where(c => c.Status == AnalysisConflictStatus.Open))
        {
            Assert.DoesNotContain(c.Alternatives, x => structural.Contains(x.Text));
        }

        // (iii) il conflitto resta Open, Resolution nulla: nessuna scelta automatica, nessuna risposta al Gap anticipata dall'assembly (appartiene allo stadio Gap).
        Assert.Equal((AnalysisConflictStatus.Open, null), (conflict.Status, conflict.Resolution));
        Assert.Equal(Reason, conflict.Description);
    }

    // ================================================================ (b)11 compatibilità con il contratto dello stadio Gap

    private static string StageResponse(string e1, string e2) =>
        JsonSerializer.Serialize(new
        {
            contract = "gap-stage-1",
            gapAssessments = new[] { e1, e2 }.Select((json, i) => JsonNode.Parse(json)).ToArray(),
        });

    private static string Open(string gap, string? item, string? conflict) => JsonSerializer.Serialize(new
    {
        gap, outcome = "Open", reason = "NoAnswer", rationale = "Nessuna evidenza risponde.", supportingEvidence = Array.Empty<string>(), openItem = item, openConflict = conflict,
    });

    [Fact]
    public async Task B11_the_gap_question_and_the_open_conflict_are_offered_to_the_gap_stage_with_stable_references_and_the_stage_assembly_changes_nothing()
    {
        var (rig, a) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.LimitFive));
        var (rig2, again) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.LimitFive));
        var references = rig.References(a);

        // Riferimenti: la domanda del Gap come i<n>, il conflitto come c<m>; l'hash dei riferimenti è identico a ogni replay.
        var gapKey = references.GapQuestion["E1"];
        var docGapKey = references.GapQuestion["E2"];
        Assert.Matches("^i[1-9][0-9]*$", gapKey);
        var conflictKey = Assert.Single(references.Conflicts).Key;
        Assert.Equal("c1", conflictKey);
        Assert.Equal(references.Hash(), rig2.References(again).Hash());
        Assert.Empty(references.MissingGaps);

        var before = DocumentAnalysisContentJson.Serialize(a.Analysis);
        var mapBefore = a.Map.Select(m => (m.ItemId, string.Join(',', m.EvidenceIds))).ToList();

        // Un record con openItem=i<n> e uno con openConflict=c<m>: l'assembly dello stadio li risolve senza coverage_incomplete né work_output_invalid.
        var byItem = rig.VerifyGapStage(a, StageResponse(Open("E1", gapKey, null), Open("E2", docGapKey, null)));
        var byConflict = rig.VerifyGapStage(a, StageResponse(Open("E1", null, conflictKey), Open("E2", docGapKey, null)));

        Assert.Equal(a.Analysis.OpenQuestions.Single(q => Cites(a, q.Id, IdOf(rig, M2Ledger.GapPm))).Id, byItem.Single(r => r.Gap == "E1").OpenItem);
        Assert.Null(byItem.Single(r => r.Gap == "E1").OpenConflict);
        Assert.Equal(a.Analysis.Conflicts.Single().Id, byConflict.Single(r => r.Gap == "E1").OpenConflict);
        Assert.Null(byConflict.Single(r => r.Gap == "E1").OpenItem);

        // Item, conflitti, EvidenceMap ed EvidenceRefs restano invariati dopo lo stadio Gap.
        Assert.Equal(before, DocumentAnalysisContentJson.Serialize(a.Analysis));
        Assert.Equal(mapBefore, a.Map.Select(m => (m.ItemId, string.Join(',', m.EvidenceIds))).ToList());
    }

    // ================================================================ (b)12 determinismo (la ripresa in un processo nuovo è nel test PostgreSQL)

    [Fact]
    public async Task B12_two_runs_of_the_same_hierarchy_give_the_same_outcome_hashes_and_the_same_assembled_analysis()
    {
        var (rigA, first) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.LimitFive));
        var (rigB, second) = await OpenAsync(null, (M2Ledger.GapPm, M2Ledger.LimitFive));

        Assert.Equal(rigA.Engine.Result().Outcomes.Select(o => o.OutputHash), rigB.Engine.Result().Outcomes.Select(o => o.OutputHash));
        Assert.Equal(rigA.Hierarchy.Outcomes.Select(o => o.OutputHash), rigB.Hierarchy.Outcomes.Select(o => o.OutputHash));
        Assert.Equal(DocumentAnalysisContentJson.Serialize(first.Analysis), DocumentAnalysisContentJson.Serialize(second.Analysis));
        Assert.Equal(first.Map.Select(m => (m.ItemId, string.Join(',', m.EvidenceIds))), second.Map.Select(m => (m.ItemId, string.Join(',', m.EvidenceIds))));
        Assert.Single(first.Analysis.Conflicts);                                                    // nessun duplicato fra le due ripetizioni
        Assert.Equal(1, first.Analysis.OpenQuestions.Count(q => q.Text == rigA.Input.Evidence[M2Ledger.GapPm - 1].Statement));
    }

    // ================================================================ regole di contenuto sugli item proiettati (senza conflitti)

    [Fact]
    public async Task B0_the_projected_items_pass_the_content_rules_once_assembled_and_every_evidence_has_one_item()
    {
        var rig = await V11Rig.CreateAsync(M2Ledger.Input(), (_, _) => throw new InvalidOperationException("nessun candidato"), Ct);

        var assembled = rig.Assemble();

        Assert.Empty(rig.ContentViolations(assembled));
        var items = V11Rig.SectionItems(assembled.Analysis).ToList();
        Assert.Equal(rig.Input.Evidence.Count, items.Count);                                       // nessun conflitto: un item per evidenza
        Assert.Equal(rig.Input.Evidence.Count, assembled.Map.Select(m => m.EvidenceIds.Single()).Distinct().Count());
        Assert.Empty(assembled.Analysis.Conflicts);
        foreach (var (section, item) in items)
        {
            var evidence = rig.Evidence[assembled.Map.Single(m => m.ItemId == item.Id).EvidenceIds.Single()];
            Assert.Equal(evidence.Statement, item.Text);                                           // il testo di ogni item è l'enunciato della sua evidenza
            var (expectedSection, expectedClassification) = ReconcileLeafProjection.Rule(evidence.Kind, evidence.Origin, evidence.Certainty);
            Assert.Equal(SectionName(expectedSection), section);
            Assert.Equal(expectedClassification, item.Classification);
            if (section is not ("openQuestions" or "assumptions"))
            {
                Assert.False(evidence.Kind is EvidenceKind.Gap or EvidenceKind.Alternative or EvidenceKind.Assumption, $"{section}: {evidence.Kind}");
                Assert.False(evidence.Certainty is EvidenceCertainty.ToClarify or EvidenceCertainty.Hypothesized, $"{section}: {evidence.Certainty}");
            }
        }

        // Nessuna evidenza non mappata e nessuna Gap senza domanda canonica (condizioni necessarie, NON sufficienti per la qualità della documentazione finale).
        Assert.DoesNotContain(rig.Findings(assembled), f => f.Code == "evidence_not_mapped");
        Assert.Empty(rig.References(assembled).MissingGaps);
    }

    private static string SectionName(AiSection section) => section switch
    {
        AiSection.Goals => "goals",
        AiSection.ScopeOverview => "scopeOverview",
        AiSection.ScopeIncluded => "scopeIncluded",
        AiSection.ScopeExcluded => "scopeExcluded",
        AiSection.Requirements => "requirements",
        AiSection.Flows => "flows",
        AiSection.Constraints => "constraints",
        AiSection.Dependencies => "dependencies",
        AiSection.Assumptions => "assumptions",
        AiSection.OpenQuestions => "openQuestions",
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    // ================================================================ D2b-3: la ripetizione attesa dell'avviso D3 resta distinguibile da una ripetizione indesiderata

    /// <summary>
    /// Ripetizione ATTESA (D2b-3, transitoria): l'avviso D3 riguarda la domanda canonica di un Gap o l'elemento ToClarify conservato in <c>openQuestions</c>, mai un item di una sezione strutturale. Qualunque
    /// altro avviso D3 (item ordinario che ripete un'alternativa) è una ripetizione indesiderata e non rientra in questo criterio.
    /// </summary>
    private static bool IsExpectedRepetition(V11Rig rig, DocumentCoverageFinding f) =>
        f.Code == RepeatsAlternative && f.Section == "openQuestions" && f.EvidenceId is { } id && rig.Evidence[id] is { Kind: EvidenceKind.Gap } or { Certainty: EvidenceCertainty.ToClarify };

    [Fact]
    public async Task B14_every_structural_repetition_warning_of_the_v11_concerns_a_gap_or_to_clarify_question_and_none_a_structural_item_while_the_v10_path_would_produce_the_unwanted_kind()
    {
        var (rig, a) = await OpenAsync(
            null, (M2Ledger.GapPm, M2Ledger.LimitFive), (M2Ledger.ToClarify, M2Ledger.LimitEight), (M2Ledger.Decision, M2Ledger.Alternative), (M2Ledger.Hypothesis, M2Ledger.Inferred));

        var repeats = rig.Findings(a).Where(f => f.Code == RepeatsAlternative).ToList();

        Assert.NotEmpty(repeats);
        Assert.All(repeats, f =>
        {
            Assert.True(IsExpectedRepetition(rig, f), $"avviso D3 indesiderato: sezione {f.Section}, evidenza {f.Alias}");
            Assert.Equal(FindingCategory.SemanticRisk, f.Category);
            Assert.False(f.Blocking);
        });
        // Esattamente quelli attesi: uno per il Gap, uno per ToClarify; nessuno per Decision, Alternative, Hypothesized né Inferred (rappresentati una sola volta, come alternativa).
        Assert.Equal(["E1", "E6"], repeats.Select(f => f.Alias).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(rig.Findings(a), f => f.Code == "evidence_not_mapped");

        // Controllo negativo: senza la politica v11 (assembly come nella v10) l'item ordinario che ripete l'alternativa è un avviso di sezione STRUTTURALE, quindi distinguibile e non «atteso».
        var legacy = rig.Findings(rig.Assemble(openEndpoints: null)).Where(f => f.Code == RepeatsAlternative).ToList();
        Assert.Contains(legacy, f => f.Section is not "openQuestions" && !IsExpectedRepetition(rig, f));
    }

    // ================================================================ classificazione delle alternative dei conflitti (rischio semantico)

    /// <summary>Alternativa del conflitto (qualunque conflitto Open) che porta l'enunciato dell'evidenza.</summary>
    private static DocumentAnalysisItem AlternativeOf(AssembledV11 a, string statement) =>
        a.Analysis.Conflicts.SelectMany(c => c.Alternatives).First(x => x.Text == statement);

    [Fact]
    public async Task B13_the_alternatives_of_contested_hypotheses_and_inferences_keep_the_classification_of_the_projection_table_in_the_v11_and_the_v10_path_is_unchanged()
    {
        // Rischio rilevato dalla revisione: l'alternativa di un'evidenza Hypothesized/Inferred veniva classificata per ORIGINE (Documented/Declared), quindi un'ipotesi o una deduzione contestata rientrava
        // come fatto. Contratto approvato (ai-workflow §1): «ipotesi e deduzioni restano Assumed/Inferred anche se documentate».
        var specs = M2Ledger.Specs.ToList();
        specs[M2Ledger.Inferred - 1] = new(EvidenceOriginKind.Message, EvidenceKind.Requirement, EvidenceCertainty.Inferred, "Il PM sembra preferire il canale EDI.");
        var (rig, a) = await OpenAsync(
            specs, null, (M2Ledger.Hypothesis, M2Ledger.LimitFive), (M2Ledger.Inferred, M2Ledger.LimitEight), (M2Ledger.GapPm, M2Ledger.Decision), (M2Ledger.ToClarify, M2Ledger.PmRequirement));

        // v11: l'alternativa di OGNI estremo Open porta la classificazione della tabella leaf-projection-1 (la stessa che avrebbe l'item proiettato).
        var checkedEvidence = 0;
        foreach (var n in new[] { M2Ledger.Hypothesis, M2Ledger.LimitFive, M2Ledger.Inferred, M2Ledger.LimitEight, M2Ledger.GapPm, M2Ledger.Decision, M2Ledger.ToClarify, M2Ledger.PmRequirement })
        {
            var evidence = rig.Input.Evidence[n - 1];
            var expected = ReconcileLeafProjection.Rule(evidence.Kind, evidence.Origin, evidence.Certainty).Classification;
            Assert.True(expected == AlternativeOf(a, evidence.Statement).Classification, $"E{n} ({evidence.Kind}/{evidence.Certainty}): alternativa {AlternativeOf(a, evidence.Statement).Classification}, attesa {expected}");
            checkedEvidence++;
        }

        Assert.Equal(8, checkedEvidence);
        Assert.Equal(DocumentAnalysisClassification.Assumed, AlternativeOf(a, rig.Input.Evidence[M2Ledger.Hypothesis - 1].Statement).Classification);   // ipotesi: mai Documented
        Assert.Equal(DocumentAnalysisClassification.Inferred, AlternativeOf(a, rig.Input.Evidence[M2Ledger.Inferred - 1].Statement).Classification);     // deduzione del PM: mai Declared
        Assert.Empty(rig.ContentViolations(a));                                                                                                           // regole di contenuto rispettate (Assumed ammesso nelle alternative)
        Assert.DoesNotContain(rig.Findings(a), f => f.Code == "evidence_not_mapped");

        // Caso invariato: per Gap e ToClarify (che restano anche domande) la classificazione è per origine, come l'item proiettato: la ripetizione D3 attesa resta identica a prima.
        Assert.Equal(DocumentAnalysisClassification.Declared, AlternativeOf(a, rig.Input.Evidence[M2Ledger.GapPm - 1].Statement).Classification);
        Assert.Equal(DocumentAnalysisClassification.Documented, AlternativeOf(a, rig.Input.Evidence[M2Ledger.ToClarify - 1].Statement).Classification);

        // Percorso senza la politica v11 (assembly come nelle definizioni v6–v10): classificazione PER ORIGINE, invariata (caratterizzazione: non è la v11 e non si modifica).
        var legacy = rig.Assemble(openEndpoints: null);
        Assert.Equal(DocumentAnalysisClassification.Documented, AlternativeOf(legacy, rig.Input.Evidence[M2Ledger.Hypothesis - 1].Statement).Classification);
        Assert.Equal(DocumentAnalysisClassification.Declared, AlternativeOf(legacy, rig.Input.Evidence[M2Ledger.Inferred - 1].Statement).Classification);
    }
}
