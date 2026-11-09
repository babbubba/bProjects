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
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 T147 (DET) — campi derivati dal server per le revisioni dello stadio Gap (AI §13.7): `absenceProven` secondo la tabella a cinque stati, guardia non bloccante «risposta documentale con
/// conflitto aperto», contatori coerenti, campi additivi che non alterano la forma serializzata dei report v5 (byte-identica). Nessun database, nessun LLM: provano regole del server,
/// non la correttezza semantica della valutazione AI.
/// </summary>
public sealed class DocumentGapStageDerivationTests
{
    private static Guid Id(int n) => AliasFixtures.G(7000 + n);

    private static DocumentCoverageEvidence Ev(int n, EvidenceOriginKind origin, EvidenceKind kind = EvidenceKind.Requirement) =>
        new(Id(n), "E" + n, origin, origin == EvidenceOriginKind.Message ? AliasFixtures.M1 : null, kind, EvidencePolarity.Affirmed, $"Evidenza {n}", [], null, null, []);

    private static DocumentAnalysisItem Item(int n, params Guid[] _) => new() { Id = Id(100 + n), Text = $"Elemento {n}", Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [] };

    private static DocumentAnalysisContent Content(IReadOnlyList<DocumentAnalysisItem> questions, IReadOnlyList<DocumentAnalysisConflict> conflicts) => new()
    {
        SchemaVersion = "1.1.0", AnalysisId = AliasFixtures.Run, ProjectId = AliasFixtures.Project, SourceCutoffSequence = 2, ValidationState = AnalysisContentState.Unvalidated, Sources = [],
        Goals = [], ContextAndScope = new() { Overview = [], Included = [], Excluded = [] }, Requirements = [], OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [],
        OpenQuestions = questions, Conflicts = conflicts,
    };

    /// <summary>N4: E1 messaggio PM «45», E2 Gap, E3 scheda «30» (documento), E4 estranea (documento); conflitto Open E1/E3; domanda aperta per E2.</summary>
    private static DocumentCoverageInput N4(
        PortionStatus portion = PortionStatus.Completed, bool conflict = true, EvidenceRelationType? relation = null, int relationFrom = 3, int relationTo = 1, IReadOnlyList<GapAssessmentRecord>? gaps = null)
    {
        var (a1, a2, question) = (Item(1), Item(2), Item(3));
        var conflicts = conflict
            ? new[]
            {
                new DocumentAnalysisConflict { Id = Id(200), Description = "Cadenza discordante", Status = AnalysisConflictStatus.Open, Alternatives = [a1, a2], Resolution = null },
            }
            : [];
        var ai = Content([question], conflicts);
        var links = new List<(Guid, Guid)> { (a1.Id, Id(1)), (a2.Id, Id(3)), (question.Id, Id(2)) };
        var evidence = new[] { Ev(1, EvidenceOriginKind.Message), Ev(2, EvidenceOriginKind.Document, EvidenceKind.Gap), Ev(3, EvidenceOriginKind.Document), Ev(4, EvidenceOriginKind.Document) };
        var relations = relation is { } r ? new[] { new DocumentCoverageRelation(Id(relationFrom), Id(relationTo), r) } : [];
        return new(evidence, links, ai, ai, null, new Dictionary<Guid, string>(), [new DocumentCoveragePortion(Id(300), Id(301), 0, portion, null)], relations, null, gaps, GapStage: true);
    }

    private static (bool AbsenceProven, bool AnsweredWithOpenConflict) Derive(DocumentCoverageInput input, GapOutcome outcome, GapReason reason, params int[] support) =>
        new GapStageDerivation(input).For(outcome, reason, [.. support.Select(Id)]);

    // ---------------------------------------------------------------- assenza provata: tabella a cinque stati

    [Fact]
    public void Absence_is_proven_only_for_no_answer_over_a_complete_extraction_and_otherwise_it_is_labelled_not_proven()
    {
        Assert.True(Derive(N4(), GapOutcome.Open, GapReason.NoAnswer).AbsenceProven);                                  // ledger completo + estrazione completa
        Assert.False(Derive(N4(PortionStatus.Pending), GapOutcome.Open, GapReason.NoAnswer).AbsenceProven);           // estrazione incompleta ⇒ «assenza non provata»
        Assert.False(Derive(N4(PortionStatus.Failed), GapOutcome.Open, GapReason.NoAnswer).AbsenceProven);
        // Insufficiente/ambigua e documentata: mai «assenza provata».
        Assert.False(Derive(N4(), GapOutcome.Open, GapReason.Partial, 3).AbsenceProven);
        Assert.False(Derive(N4(), GapOutcome.Open, GapReason.Ambiguous, 1, 3).AbsenceProven);
        Assert.False(Derive(N4(), GapOutcome.AnsweredInSnapshot, GapReason.Answered, 4).AbsenceProven);
    }

    [Fact]
    public void An_answered_gap_whose_support_takes_part_in_an_open_conflict_is_flagged_without_changing_the_outcome()
    {
        // N4: E3 (documento) è alternativa del conflitto Open E1/E3.
        Assert.True(Derive(N4(), GapOutcome.AnsweredInSnapshot, GapReason.Answered, 3).AnsweredWithOpenConflict);
        Assert.True(Derive(N4(), GapOutcome.AnsweredInSnapshot, GapReason.Answered, 4, 3).AnsweredWithOpenConflict);   // basta uno dei supporti
        Assert.False(Derive(N4(), GapOutcome.AnsweredInSnapshot, GapReason.Answered, 4).AnsweredWithOpenConflict);      // supporto estraneo al conflitto
        Assert.False(Derive(N4(conflict: false), GapOutcome.AnsweredInSnapshot, GapReason.Answered, 3).AnsweredWithOpenConflict);   // nessun conflitto, nessuna relazione
        // La guardia vale solo per le risposte documentate: un Open non è mai «risposta con conflitto».
        Assert.False(Derive(N4(), GapOutcome.Open, GapReason.Ambiguous, 1, 3).AnsweredWithOpenConflict);
    }

    [Fact]
    public void A_known_contradiction_or_alternative_relation_counts_only_against_an_evidence_of_a_different_origin()
    {
        foreach (var type in new[] { EvidenceRelationType.Contradicts, EvidenceRelationType.AlternativeTo })
        {
            // E3 (Document) contro E1 (Message), senza conflitto canonico: segnalata, in entrambe le direzioni della relazione.
            Assert.True(Derive(N4(conflict: false, relation: type, relationFrom: 3, relationTo: 1), GapOutcome.AnsweredInSnapshot, GapReason.Answered, 3).AnsweredWithOpenConflict);
            Assert.True(Derive(N4(conflict: false, relation: type, relationFrom: 1, relationTo: 3), GapOutcome.AnsweredInSnapshot, GapReason.Answered, 3).AnsweredWithOpenConflict);
            // Due documenti (stessa origine): non è un conflitto Message contro Document.
            Assert.False(Derive(N4(conflict: false, relation: type, relationFrom: 3, relationTo: 4), GapOutcome.AnsweredInSnapshot, GapReason.Answered, 3).AnsweredWithOpenConflict);
        }

        // Una relazione di altro tipo non segnala nulla (nessun matching lessicale né semantico).
        Assert.False(Derive(N4(conflict: false, relation: EvidenceRelationType.Confirms, relationFrom: 3, relationTo: 1), GapOutcome.AnsweredInSnapshot, GapReason.Answered, 3).AnsweredWithOpenConflict);
    }

    // ---------------------------------------------------------------- contatori e forma additiva

    private static GapAssessmentRecord Record(
        int gap, GapOutcome outcome, GapReason reason, bool stage, bool absence = false, bool conflict = false, Guid? openItem = null) => new(
        Id(gap), Guid.NewGuid(), new string('c', 64), outcome, reason, "Valutazione AI nello snapshot.", outcome == GapOutcome.AnsweredInSnapshot ? [Id(3)] : [], openItem, null, new string('b', 64),
        stage ? "ledger_exhaustive" : null, stage ? true : null, stage ? absence : null, stage ? conflict : null);

    [Fact]
    public void The_summary_adds_the_three_derived_counters_only_for_the_stage_and_keeps_the_five_invariants()
    {
        var question = Id(103);
        var records = new[]
        {
            Record(2, GapOutcome.AnsweredInSnapshot, GapReason.Answered, stage: true, conflict: true),
            Record(5, GapOutcome.Open, GapReason.NoAnswer, stage: true, absence: true, openItem: question),
            Record(6, GapOutcome.Open, GapReason.NoAnswer, stage: true, absence: false, openItem: question),
            Record(7, GapOutcome.Open, GapReason.Partial, stage: true, openItem: question),
        };
        var input = N4(gaps: records) with
        {
            Evidence = [.. N4().Evidence, Ev(5, EvidenceOriginKind.Document, EvidenceKind.Gap), Ev(6, EvidenceOriginKind.Document, EvidenceKind.Gap), Ev(7, EvidenceOriginKind.Document, EvidenceKind.Gap)],
        };

        var summary = DocumentCoverage.SummarizeGaps(input, DocumentCoverage.Check(input));

        Assert.Equal((4, 4, 1, 3, 0), (summary.ExpectedGaps, summary.AssessedGaps, summary.AnsweredGaps, summary.OpenGaps, summary.UnassessedGaps));
        Assert.Equal(summary.ExpectedGaps, summary.AssessedGaps + summary.UnassessedGaps);
        Assert.Equal(summary.AssessedGaps, summary.AnsweredGaps + summary.OpenGaps);
        Assert.Equal((1, 1, 1), (summary.AbsenceProvenGaps, summary.AbsenceUnprovenGaps, summary.AnsweredWithOpenConflictGaps));
        Assert.Equal(2, summary.AbsenceProvenGaps + summary.AbsenceUnprovenGaps);   // somma = Gap NoAnswer

        // Revisioni v5 (GapStage falso): i tre campi non esistono.
        var v5 = DocumentCoverage.SummarizeGaps(input with { GapStage = false, Gaps = [Record(2, GapOutcome.AnsweredInSnapshot, GapReason.Answered, stage: false)] }, []);
        Assert.Null(v5.AbsenceProvenGaps);
        Assert.Null(v5.AbsenceUnprovenGaps);
        Assert.Null(v5.AnsweredWithOpenConflictGaps);
    }

    [Fact]
    public void The_additive_fields_never_change_the_serialized_shape_of_v5_reports()
    {
        var v5Record = Record(2, GapOutcome.AnsweredInSnapshot, GapReason.Answered, stage: false);
        var v5Json = EvidenceJson.Serialize(v5Record);
        foreach (var key in new[] { "searchBasis", "retrievalComplete", "absenceProven", "answeredWithOpenConflict" })
        {
            Assert.DoesNotContain(key, v5Json, StringComparison.Ordinal);
        }

        var v5Summary = DocumentCoverage.SummarizeGaps(N4(gaps: [v5Record]) with { GapStage = false }, []);
        var summaryJson = EvidenceJson.Serialize(v5Summary);
        foreach (var key in new[] { "absenceProvenGaps", "absenceUnprovenGaps", "answeredWithOpenConflictGaps" })
        {
            Assert.DoesNotContain(key, summaryJson, StringComparison.Ordinal);
        }

        // Un report v5 già persistito (senza i campi nuovi) si rilegge e si riscrive identico.
        Assert.Equal(v5Json, EvidenceJson.Serialize(EvidenceJson.Deserialize<GapAssessmentRecord>(v5Json)));
        Assert.Equal(summaryJson, EvidenceJson.Serialize(EvidenceJson.Deserialize<DocumentCoverageSummary3>(summaryJson)));

        // Le revisioni v6 li portano tutti.
        var stageJson = EvidenceJson.Serialize(Record(2, GapOutcome.Open, GapReason.NoAnswer, stage: true, absence: true));
        Assert.Contains("\"searchBasis\":\"ledger_exhaustive\"", stageJson, StringComparison.Ordinal);
        Assert.Contains("\"retrievalComplete\":true", stageJson, StringComparison.Ordinal);
        Assert.Contains("\"absenceProven\":true", stageJson, StringComparison.Ordinal);
        Assert.Contains("\"answeredWithOpenConflict\":false", stageJson, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pm_edit_that_removes_the_open_reference_is_a_distinct_pm_assessment_and_the_ai_record_is_never_rewritten()
    {
        var question = Id(103);
        var record = Record(2, GapOutcome.Open, GapReason.Partial, stage: true, openItem: question);
        var input = N4(gaps: [record]);
        var edited = input with { Target = Content([], []) };   // il PM ha rimosso domanda e conflitto

        var findings = DocumentCoverage.Check(edited);

        Assert.Contains(findings, f => f.Code == "gap_open_reference_removed_by_pm" && f.Category == FindingCategory.PmAssessment && f.EvidenceId == Id(2));
        Assert.Equal(record, edited.Gaps![0]);                                                       // la valutazione AI originaria non cambia
        Assert.Equal(DocumentCoverage.SummarizeGaps(input, []).AssessedGaps, DocumentCoverage.SummarizeGaps(edited, []).AssessedGaps);
    }
}

/// <summary>
/// 002 T147 (PG, modello FINTO) — assembly e coverage-3 estesa di una revisione dello stadio Gap: le valutazioni si aggiungono senza toccare item, conflitti, mappa evidenze ed EvidenceRefs; il report è
/// selezionato per algoritmo (`gap-stage-1` ⇒ coverage-3), porta i campi derivati e rifiuta payload manomessi; la gerarchia senza stadio resta coverage-2.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentGapStageAssemblyTests : ReconcileRig
{
    private async Task<(Guid Run, AnalysisRunClaim Claim, Guid Project)> ArrangeAsync(HttpClient client, int gaps = 2, Guid? project = null, bool createDocuments = true)
    {
        var p = project ?? await NewProject(client);
        if (createDocuments)
        {
            for (var n = 1; n <= DocumentGapStageWorkflowTests.Documents; n++)
            {
                await NewDocument(client, p, $"doc{n}", $"Requisito {n}: " + string.Join(' ', Enumerable.Range(0, DocumentGapStageWorkflowTests.Words).Select(i => $"w{n}x{i}")), n == 1 ? "aaa" : $"mmm{n}",
                    kind: n <= gaps ? EvidenceKind.Gap : EvidenceKind.Requirement);
            }
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{p}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = (await Json(response)).GetProperty("id").GetGuid();
        await Execute(
            "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = @v::jsonb WHERE \"Id\" = @r", ("d", AnalysisV6Fixture.Definition), ("v", AnalysisV6Fixture.Json()), ("r", run));
        return (run, await ClaimAsync(run), p);
    }

    private static string Url(Guid project, Guid run) => $"/api/projects/{project}/analyses/{run}/coverage-reports";

    private async Task<string> ContentOf(Guid run) => await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r AND \"Origin\" = 'Ai'", ("r", run));

    private static string Normalized(string json) => Regex.Replace(json, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "ID");

    [Fact]
    public async Task Coverage_3_of_a_stage_run_carries_the_assessments_the_derived_fields_and_the_server_made_counters()
    {
        using var client = await App.LoginAsync();
        var (run, claim, project) = await ArrangeAsync(client);
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(claim, new DocumentChatWorkflowTests.FakeChatModel { Responder = DocumentGapStageWorkflowTests.Respond })).Status);

        var created = await client.PostAsJsonAsync(Url(project, run), new { }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var report = await Json(created);
        Assert.Equal("coverage-3", report.GetProperty("checkerVersion").GetString());
        var summary = report.GetProperty("summary");
        Assert.Equal((2, 2, 1, 1, 0), (summary.GetProperty("expectedGaps").GetInt32(), summary.GetProperty("assessedGaps").GetInt32(), summary.GetProperty("answeredGaps").GetInt32(),
            summary.GetProperty("openGaps").GetInt32(), summary.GetProperty("unassessedGaps").GetInt32()));
        Assert.Equal((1, 0, 0), (summary.GetProperty("absenceProvenGaps").GetInt32(), summary.GetProperty("absenceUnprovenGaps").GetInt32(), summary.GetProperty("answeredWithOpenConflictGaps").GetInt32()));

        var records = report.GetProperty("gapAssessments").EnumerateArray().ToList();
        Assert.Equal(2, records.Count);
        Assert.All(records, r =>
        {
            Assert.Equal("ledger_exhaustive", r.GetProperty("searchBasis").GetString());
            Assert.True(r.GetProperty("retrievalComplete").GetBoolean());
        });
        var open = Assert.Single(records, r => r.GetProperty("outcome").GetString() == "Open");
        Assert.Equal("NoAnswer", open.GetProperty("reason").GetString());
        Assert.True(open.GetProperty("absenceProven").GetBoolean());
        var answered = Assert.Single(records, r => r.GetProperty("outcome").GetString() == "AnsweredInSnapshot");
        Assert.False(answered.GetProperty("answeredWithOpenConflict").GetBoolean());
        Assert.Single(answered.GetProperty("supportingEvidenceIds").EnumerateArray());

        // I punti aperti sono gli id canonici della revisione AI originaria (nessun elemento creato dal modello o dal server).
        var questionIds = JsonDocument.Parse(await ContentOf(run)).RootElement.GetProperty("openQuestions").EnumerateArray().Select(q => q.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(open.GetProperty("openItemId").GetGuid(), questionIds);
        Assert.Equal(JsonValueKind.Null, open.GetProperty("openConflictId").ValueKind);

        // Idempotenza per (analisi, revisione, bozza, checker): una seconda richiesta restituisce lo stesso report.
        var again = await client.PostAsJsonAsync(Url(project, run), new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(report.GetProperty("id").GetGuid(), (await Json(again)).GetProperty("id").GetGuid());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"EvidenceCoverageReports\" WHERE \"AnalysisId\" = @a AND \"CheckerVersion\" = 'coverage-3'", ("a", run)));
    }

    [Fact]
    public async Task An_incomplete_extraction_leaves_the_absence_not_proven()
    {
        using var client = await App.LoginAsync();
        var (run, claim, project) = await ArrangeAsync(client);
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(claim, new DocumentChatWorkflowTests.FakeChatModel { Responder = DocumentGapStageWorkflowTests.Respond })).Status);
        using (var scope = App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var preparation = db.DocumentPreparations.First(p => p.ProjectId == project);
            db.DocumentPortions.Add(new DocumentPortion
            {
                Id = Guid.NewGuid(), ProjectId = project, PreparationId = preparation.Id, Ordinal = 0, RangesJson = "[]", ContextRangesJson = "[]", TextHash = new string('a', 64), Status = PortionStatus.Pending,
            });
            await db.SaveChangesAsync(Ct);
        }

        var report = await Json(await client.PostAsJsonAsync(Url(project, run), new { }, Ct));

        var summary = report.GetProperty("summary");
        Assert.Equal((0, 1), (summary.GetProperty("absenceProvenGaps").GetInt32(), summary.GetProperty("absenceUnprovenGaps").GetInt32()));
        Assert.False(Assert.Single(report.GetProperty("gapAssessments").EnumerateArray(), r => r.GetProperty("reason").GetString() == "NoAnswer").GetProperty("absenceProven").GetBoolean());
        Assert.Equal(1, summary.GetProperty("portionsMissing").GetInt32());
    }

    [Fact]
    public async Task The_assessments_never_alter_items_conflicts_the_evidence_map_or_the_evidence_refs()
    {
        using var client = await App.LoginAsync();
        var (runA, claimA, project) = await ArrangeAsync(client);
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(claimA, new DocumentChatWorkflowTests.FakeChatModel { Responder = DocumentGapStageWorkflowTests.Respond })).Status);

        // Stessi documenti, valutazioni DIVERSE (entrambi i Gap Open/NoAnswer sulla propria domanda): la revisione AI è la stessa analisi.
        var (runB, claimB, _) = await ArrangeAsync(client, project: project, createDocuments: false);
        var allOpen = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => DocumentGapStageWorkflowTests.IsStage(r)
                ? DocumentGapStageWorkflowTests.Stage(r, (v, _) => [.. v.Gaps.Select(g => JsonNode.Parse(
                    $$"""{"gap":"{{g.Alias}}","outcome":"Open","reason":"Ambiguous","rationale":"Da chiarire.","supportingEvidence":["{{v.Candidates[0]}}"],"openItem":"{{g.Question}}","openConflict":null}""")!.AsObject())])
                : DocumentGapStageWorkflowTests.Hierarchy(r),
        };
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(claimB, allOpen)).Status);

        Assert.Equal(Normalized(await ContentOf(runA)), Normalized(await ContentOf(runB)));
        var linksA = await Scalar<string>("SELECT coalesce(string_agg(c::text, ',' ORDER BY c), '') FROM (SELECT count(*) AS c FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r GROUP BY \"ItemId\") t", ("r", runA));
        var linksB = await Scalar<string>("SELECT coalesce(string_agg(c::text, ',' ORDER BY c), '') FROM (SELECT count(*) AS c FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r GROUP BY \"ItemId\") t", ("r", runB));
        Assert.Equal(linksA, linksB);
        Assert.NotEqual(
            await Scalar<string>("SELECT \"OutputHash\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1'", ("r", runA)),
            await Scalar<string>("SELECT \"OutputHash\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1'", ("r", runB)));

        var reportB = await Json(await client.PostAsJsonAsync(Url(project, runB), new { }, Ct));
        Assert.Equal((2, 2, 0, 2, 0), (reportB.GetProperty("summary").GetProperty("expectedGaps").GetInt32(), reportB.GetProperty("summary").GetProperty("assessedGaps").GetInt32(),
            reportB.GetProperty("summary").GetProperty("answeredGaps").GetInt32(), reportB.GetProperty("summary").GetProperty("openGaps").GetInt32(), reportB.GetProperty("summary").GetProperty("unassessedGaps").GetInt32()));
    }

    [Fact]
    public async Task A_tampered_stage_payload_prevents_the_report_and_the_hierarchy_without_a_stage_keeps_coverage_2()
    {
        using var client = await App.LoginAsync();
        var (run, claim, project) = await ArrangeAsync(client);
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(claim, new DocumentChatWorkflowTests.FakeChatModel { Responder = DocumentGapStageWorkflowTests.Respond })).Status);

        // Manomissione: il payload non coincide più con OutputHash ⇒ nessun report (mai una lista vuota apparentemente valida).
        await Execute(
            "ALTER TABLE \"WorkOutputs\" DISABLE TRIGGER USER; UPDATE \"WorkOutputs\" SET \"PayloadJson\" = jsonb_set(\"PayloadJson\"::jsonb, '{outcome}', '\"Complete \"') WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1'; ALTER TABLE \"WorkOutputs\" ENABLE TRIGGER USER;",
            ("r", run));
        var response = await client.PostAsJsonAsync(Url(project, run), new { }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"EvidenceCoverageReports\" WHERE \"AnalysisId\" = @a", ("a", run)));

        // Gerarchia SENZA stadio (v6 senza Gap, caso b2): coverage-2, nessuna valutazione sintetizzata dal server.
        var (plainRun, plainClaim, plainProject) = await ArrangeAsync(client, gaps: 0);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = DocumentGapStageWorkflowTests.Respond };
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(plainClaim, model)).Status);
        Assert.DoesNotContain(model.Requests, DocumentGapStageWorkflowTests.IsStage);
        var plain = await Json(await client.PostAsJsonAsync(Url(plainProject, plainRun), new { }, Ct));
        Assert.Equal("coverage-2", plain.GetProperty("checkerVersion").GetString());
        Assert.False(plain.TryGetProperty("gapAssessments", out _));
        Assert.False(plain.GetProperty("summary").TryGetProperty("expectedGaps", out _));
    }
}
