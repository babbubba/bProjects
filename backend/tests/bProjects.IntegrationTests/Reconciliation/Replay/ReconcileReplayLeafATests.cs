using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-022 (M2, T170) — replay PER BATCH delle foglie A reali dei Run 4 e 5 (corpus sintetico T040) con il costruttore di fixture (RH-006), il compositore, la proiezione e l'assembly
/// di produzione. (a) v10: l'output archiviato, composto così com'è, lascia il Gap SENZA domanda canonica (Run 5 024: resa come requirement; Run 4 022: nessun item cita E1), quindi la precondizione dello stadio
/// Gap (<c>GapStageReferences.From</c>) darebbe <c>MissingGaps</c> non vuoto. (b) v11: le stesse VOCI proiettate danno un item per voce e una domanda per ogni Gap: <c>MissingGaps</c> vuoto (condizione
/// necessaria, NON sufficiente per la qualità della documentazione finale). (c) rischio R-D2b: con un candidato Gap↔evidenza della gerarchia (Run 5 027: E255–E4; Run 4 029: E4–E42; Run 4 032: E4–E254) a
/// disposizione <c>Open</c>, l'assembly v11 con l'eccezione per i Gap lascia <c>MissingGaps</c> vuoto, mentre la D2b senza eccezione darebbe [E4] (caratterizzazione del difetto, simulata nel test). Un candidato che
/// collega una Gap a un'evidenza potenzialmente risolutiva non dimostra una contraddizione fra requisiti: la risposta al Gap è valutata dallo stadio Gap. Nessun database, nessuna inferenza, nessun output trasformato.
/// </summary>
public sealed class ReconcileReplayLeafATests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string V11LeafDto() => ReconcileContractMatrixTests.Dto(descriptor: "gruppo").ToJsonString();

    private static ReconcileComposeOptions V11Options(ReplayBatch batch) => new(ReconcileAliasContracts.StageContract, batch.Evidence);

    /// <summary>Analisi minima dalle proposte di UNA foglia (sezioni dichiarate): serve solo a calcolare i riferimenti dello stadio Gap, come fa il piano sull'analisi assemblata.</summary>
    private static (GapStageReferences References, IReadOnlyList<EvidenceMapEntry> Map, DocumentAnalysisContent Analysis) MinimalAnalysis(
        ReplayBatch batch, IReadOnlyList<ItemProposal> proposals, IReadOnlyDictionary<string, AiSection> sections)
    {
        DocumentAnalysisItem ItemOf(ItemProposal p) => new()
        {
            Id = AnalysisAliasComposer.CanonicalId("item", batch.Input.AnalysisId, batch.Snapshot.SourceHash, p.LocalKey), Text = p.Text, Classification = p.Classification, EvidenceRefs = p.SourceIds,
        };

        var questions = proposals.Where(p => sections[p.LocalKey] == AiSection.OpenQuestions).Select(ItemOf).ToList();
        var others = proposals.Where(p => sections[p.LocalKey] != AiSection.OpenQuestions).Select(ItemOf).ToList();
        var analysis = new DocumentAnalysisContent
        {
            SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = batch.Input.AnalysisId, ProjectId = batch.Input.ProjectId, SourceCutoffSequence = 1,
            ValidationState = AnalysisContentState.Unvalidated, Sources = [], Goals = [], ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = others, OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = questions, Conflicts = [],
        };
        var map = proposals.Select(p => new EvidenceMapEntry { ItemId = ItemOf(p).Id, EvidenceIds = p.EvidenceIds }).ToList();
        return (GapStageReferences.From(analysis, map, batch.RunCatalog), map, analysis);
    }

    private static ReplayBatch LeafA(int run, int call)
    {
        var fixture = ReplayFixtures.Load(run, call);
        Assert.Equal(("leaf", "A"), (fixture.Stage, fixture.View));
        return ReplayFixtureBuilder.Build(fixture);
    }

    // ================================================================ (a) v10: il Gap resta senza domanda canonica

    [Fact]
    public void A_run_5_call_024_composed_as_archived_renders_the_gap_E4_as_a_requirement_and_no_open_question_cites_it()
    {
        var batch = LeafA(5, 24);
        var composition = batch.Compose();

        Assert.True(composition.IsValid, string.Join(" | ", composition.Violations));
        var gap = batch.Evidence.Values.Single(e => e.Kind == EvidenceKind.Gap);
        Assert.Equal("E4", batch.RunCatalog.EvidenceById(gap.EvidenceId)!.Alias);
        var citing = composition.Result!.ItemProposals.Where(p => p.EvidenceIds.Contains(gap.EvidenceId)).ToList();
        Assert.NotEmpty(citing);
        Assert.All(citing, p => Assert.NotEqual(AiSection.OpenQuestions, composition.Sections[p.LocalKey]));    // il Gap è reso in una sezione strutturale (I-4)
        Assert.Equal(AiSection.Requirements, composition.Sections[citing[0].LocalKey]);

        var (references, _, _) = MinimalAnalysis(batch, composition.Result.ItemProposals, composition.Sections);
        Assert.Equal(["E4"], references.MissingGaps);                                                          // precondizione dello stadio Gap non soddisfatta: gap_reference_missing
    }

    [Fact]
    public void A_run_4_call_022_composed_as_archived_leaves_the_gap_E1_without_any_item_that_cites_it()
    {
        var batch = LeafA(4, 22);
        var composition = batch.Compose();

        Assert.True(composition.IsValid, string.Join(" | ", composition.Violations));
        var gap = batch.Evidence.Values.Single(e => e.Kind == EvidenceKind.Gap);
        Assert.Equal("E1", batch.RunCatalog.EvidenceById(gap.EvidenceId)!.Alias);
        Assert.DoesNotContain(composition.Result!.ItemProposals, p => p.EvidenceIds.Contains(gap.EvidenceId));   // nessun item cita E1: voce non coperta dal modello

        var (references, _, _) = MinimalAnalysis(batch, composition.Result.ItemProposals, composition.Sections);
        Assert.Equal(["E1"], references.MissingGaps);
        Assert.True(composition.Result.ItemProposals.SelectMany(p => p.EvidenceIds).Distinct().Count() < batch.Request.Entries.Count);   // e almeno una voce non è mappata (evidence_not_mapped a fine gerarchia)
    }

    // ================================================================ (b) v11: le stesse voci proiettate

    [Theory]
    [InlineData(5, 24)]
    [InlineData(4, 22)]
    public void B_the_same_entries_projected_give_one_item_per_entry_and_one_open_question_per_gap_so_no_gap_is_missing(int run, int call)
    {
        var batch = LeafA(run, call);

        var (proposals, sections) = ReconcileLeafProjection.Project(batch.Request, batch.Evidence);

        Assert.Equal(batch.Request.Entries.Count, proposals.Count);                                           // un item per voce
        Assert.Equal(batch.Request.Entries.Select(e => e.EvidenceId), proposals.Select(p => Assert.Single(p.EvidenceIds)));
        foreach (var gap in batch.Evidence.Values.Where(e => e.Kind == EvidenceKind.Gap))
        {
            var question = Assert.Single(proposals, p => p.EvidenceIds.Contains(gap.EvidenceId));
            Assert.Equal(AiSection.OpenQuestions, sections[question.LocalKey]);                               // una domanda per ogni Gap
            Assert.Equal(gap.Statement, question.Text);                                                       // con il testo dell'enunciato, letterale
        }

        var (references, _, _) = MinimalAnalysis(batch, proposals, sections);
        Assert.Empty(references.MissingGaps);

        // Lo stesso esito dal compositore v11 con gli `items` vuoti del modello (nessun output archiviato è trasformato: la v11 non legge gli item della v10).
        var composed = ReconcileAliasComposer.Compose(V11LeafDto(), batch.Catalog, batch.Request, batch.Snapshot, batch.Base, null, V11Options(batch));
        Assert.True(composed.IsValid, string.Join(" | ", composed.Violations));
        Assert.Equal(ReconciliationCodec.Canonical(proposals), ReconciliationCodec.Canonical(composed.Result!.ItemProposals));
        Assert.Equal(batch.Request.Entries.Count, composed.Result.ItemProposals.SelectMany(p => p.EvidenceIds).Distinct().Count());   // nessuna voce non coperta
    }

    // ================================================================ (c) rischio R-D2b: Gap↔evidenza a disposizione Open

    /// <summary>Candidato E<paramref name="left"/>–E<paramref name="right"/> della fixture, ricostruito dal batch; Open con motivo, nessun candidato nel confronto (v11).</summary>
    private static async Task<(V11Rig Rig, AssembledV11 Assembled)> OpenCandidateAsync(ReplayBatch batch, int left, int right, ReconcileProposedType type)
    {
        const string Reason = "Alternative integrali da sottoporre al PM: il termine non è stabilito in modo univoco.";
        var rig = await V11Rig.CreateAsync(batch.Input, (_, _) => V11Rig.CompareOpen(Reason), Ct);
        var focus = M2Ledger.Pair(batch.Input, left, right, type);
        rig.Engine.Add(focus, ReconcileView.A);
        await rig.Engine.ProcessAsync(focus.CandidateId, Ct);
        Assert.Equal(CandidateState.Open, rig.Engine.Disposition(focus.CandidateId).State);
        return (rig, rig.Assemble());
    }

    private static string GapAlias(ReplayBatch batch, int number) => batch.RunCatalog.EvidenceById(batch.Input.Evidence[number - 1].EvidenceId)!.Alias;

    [Fact]
    public async Task C_run_5_call_027_the_candidate_E255_E4_proposed_by_the_model_with_an_open_disposition_keeps_the_gap_question_with_the_exception_and_loses_it_without()
    {
        var fixture = ReplayFixtures.Load(5, 27);
        var batch = ReplayFixtureBuilder.Build(fixture);
        // Il candidato è quello che l'output archiviato propone (composto, non trasformato): E255 contro il Gap E4.
        var composition = batch.Compose();
        Assert.True(composition.IsValid, string.Join(" | ", composition.Violations));
        var proposed = composition.Result!.Candidates.Single(c => Guid.Parse(c.Left.Id) == batch.Input.Evidence[254].EvidenceId && Guid.Parse(c.Right.Id) == batch.Input.Evidence[3].EvidenceId);
        Assert.Equal(ReconcileProposedType.Contradicts, proposed.ProposedType);

        var (rig, assembled) = await OpenCandidateAsync(batch, 255, 4, proposed.ProposedType);
        var gap = batch.Input.Evidence[3];
        Assert.Equal(EvidenceKind.Gap, gap.Kind);

        // Con l'eccezione per i Gap (R-D2b-1): la domanda canonica di E4 resta, nessun Gap mancante.
        Assert.Empty(rig.References(assembled).MissingGaps);
        Assert.Contains(assembled.Analysis.OpenQuestions, q => q.Text == gap.Statement);
        var conflict = Assert.Single(assembled.Analysis.Conflicts);
        Assert.Equal((AnalysisConflictStatus.Open, null), (conflict.Status, conflict.Resolution));              // nessuna scelta automatica: la risposta al Gap è dello stadio Gap

        // Senza l'eccezione (D2b come scritta in origine, simulata): E4 sparisce da openQuestions ⇒ MissingGaps = [E4] (gap_reference_missing).
        var (naiveAnalysis, naiveMap) = ReconcileD2bAssemblyTests.NaiveD2b(rig, rig.Assemble(openEndpoints: null));
        Assert.Equal(["E4"], GapStageReferences.From(naiveAnalysis, naiveMap, rig.RunCatalog).MissingGaps);
    }

    [Theory]
    [InlineData(29, 4, 42, ReconcileProposedType.DependsOn)]
    [InlineData(32, 4, 254, ReconcileProposedType.DependsOn)]
    public async Task C_run_4_the_gap_E4_as_an_endpoint_of_the_archived_comparison_candidate_keeps_its_question_with_the_exception_and_loses_it_without(int call, int left, int right, ReconcileProposedType type)
    {
        var fixture = ReplayFixtures.Load(4, call);
        var batch = ReplayFixtureBuilder.Build(fixture);
        Assert.Equal(("compare", $"E{left}", $"E{right}"), (fixture.Stage, fixture.Focus!.Left, fixture.Focus.Right));
        Assert.Equal(type.ToString(), fixture.Focus.Type);

        var (rig, assembled) = await OpenCandidateAsync(batch, left, right, type);
        var gap = batch.Input.Evidence[left - 1];
        Assert.Equal(EvidenceKind.Gap, gap.Kind);

        Assert.Empty(rig.References(assembled).MissingGaps);
        Assert.Equal(1, assembled.Analysis.OpenQuestions.Count(q => q.Text == gap.Statement));
        Assert.Single(assembled.Analysis.Conflicts, c => c.Alternatives.Any(a => a.Text == gap.Statement));

        // L'avviso strutturale D3 per il Gap estremo (finding atteso e non bloccante, D2b-3): una volta per l'alternativa identica alla domanda.
        var warning = Assert.Single(rig.Findings(assembled), f => f.Code == "item_repeats_conflict_alternative");
        Assert.Equal((FindingCategory.SemanticRisk, false, GapAlias(batch, left)), (warning.Category, warning.Blocking, warning.Alias));

        var (naiveAnalysis, naiveMap) = ReconcileD2bAssemblyTests.NaiveD2b(rig, rig.Assemble(openEndpoints: null));
        Assert.Equal([$"E{left}"], GapStageReferences.From(naiveAnalysis, naiveMap, rig.RunCatalog).MissingGaps);
    }
}
