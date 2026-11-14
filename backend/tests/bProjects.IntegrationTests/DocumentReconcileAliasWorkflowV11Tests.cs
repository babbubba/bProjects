using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-021 (M2, T169; PostgreSQL e MAF 1.26.0 reali, modello FINTO, nessun DGX) — la gerarchia della definizione v11 completa end-to-end con lo stadio Gap: le foglie A non producono item
/// (li compone il server), ogni Gap ha la sua domanda canonica con il testo dell'enunciato, il Gap estremo di un conflitto <c>Open</c> con una evidenza non Gap resta in <c>openQuestions</c> (D2b-1) mentre
/// l'evidenza non Gap compare solo come alternativa, i messaggi di espansione e confronto portano gli item già proposti SENZA testo (<c>items-ref-1</c>), e la ripresa in un processo nuovo (a metà gerarchia e
/// prima dello stadio Gap) riusa gli output Committed senza reinferire. Il run v11 si ottiene congelando esplicitamente la definizione: nessuna richiesta nuova la congela (default invariato, v6). Prova strutturale,
/// non di conservazione semantica: il modello finto è scritto dal test. Servono BPROJECTS_TEST_POSTGRES; senza, Skipped.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentReconcileAliasWorkflowV11Tests : ReconcileRig
{
    private const int Documents = 8;
    private const int Words = 15;   // come i test dello stadio Gap: il ledger intero entra nel budget dello stadio, la gerarchia è comunque instradata

    private static string Statement(int n) => $"Requisito {n}: " + string.Join(' ', Enumerable.Range(0, Words).Select(i => $"w{n}x{i}"));

    private static readonly Guid GapDoc = Guid.Parse("00000000-0000-4000-8000-000000000001");
    private static readonly Guid LastDoc = Guid.Parse("00000000-0000-4000-8000-000000000002");

    // ================================================================ modello finto v11

    private static JsonObject Link(string left, string right) => new()
    {
        ["key"] = "f1", ["left"] = left, ["right"] = right, ["type"] = "Contradicts", ["basis"] = "Inferred", ["rationale"] = "il primo documento chiede ciò che l'ultimo sembra stabilire",
    };

    /// <summary>
    /// Risposta della gerarchia v11: foglie A senza item (li compone il server), le altre foglie e la riduzione con solo il descriptor, l'espansione che riapre i documenti 1 (Gap) e 8 nello stesso batch trova il
    /// collegamento, il confronto è sempre <c>Open</c> senza candidati (D4). Usa solo alias ricevuti in quel messaggio.
    /// </summary>
    private static string Hierarchy(DocumentChatWorkflowTests.ChatRequest r)
    {
        var p = DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText);
        var one = p.Voci.Select(v => v.Alias).FirstOrDefault(a => p.NumberOf(a) == 1);
        var last = p.Voci.Select(v => v.Alias).FirstOrDefault(a => p.NumberOf(a) == Documents);
        var obj = ReconcileContractMatrixTests.Dto(
            noFindings: "nessun collegamento emerso", descriptor: p.Stage is "leaf" or "reduce" ? $"gruppo {p.Stage} {p.View} {string.Join('-', p.Voci.Select(v => v.Alias).Concat(p.Nodes))}" : null);
        if ((p is { Stage: "leaf", View: "B" } || p.Stage == "expand") && one is not null && last is not null)
        {
            obj["candidates"] = new JsonArray(Link(one, last));
            obj["noFindings"] = null;
        }
        else if (p.Stage == "compare")
        {
            obj["focus"] = JsonNode.Parse("""{"outcome":"Open","reason":"il primo documento chiede ciò che l'ultimo sembra stabilire: alternative integrali da sottoporre al PM"}""");
            obj["noFindings"] = null;
        }

        return obj.ToJsonString();
    }

    /// <summary>Stadio Gap: ogni Gap è Open/NoAnswer sulla propria domanda canonica (offerta dal server come <c>i&lt;n&gt;</c>).</summary>
    private static string Stage(DocumentChatWorkflowTests.ChatRequest r)
    {
        var view = DocumentGapStageWorkflowTests.ParseStage(r.AllText);
        var records = view.Gaps.Select(g => (JsonNode)new JsonObject
        {
            ["gap"] = g.Alias, ["outcome"] = "Open", ["reason"] = "NoAnswer", ["rationale"] = "Nessuna evidenza risponde.", ["supportingEvidence"] = new JsonArray(),
            ["openItem"] = g.Question, ["openConflict"] = null,
        });
        return new JsonObject { ["contract"] = "gap-stage-1", ["gapAssessments"] = new JsonArray([.. records]) }.ToJsonString();
    }

    private static string Respond(DocumentChatWorkflowTests.ChatRequest r) => DocumentGapStageWorkflowTests.IsStage(r) ? Stage(r) : Hierarchy(r);

    // ================================================================ scenario

    /// <summary>
    /// Otto documenti: 1 = Gap (estremo del conflitto), 2 = requisito, 3 = requisito <c>ToClarify</c>, 4 = decisione, 5 = assunzione ipotizzata, 6 = secondo Gap, 7 = requisito, 8 = requisito (l'altro estremo).
    /// I documenti 1 e 8 condividono la chiave di scope e i loro id di evidenza ordinano per primi: l'espansione li riapre nello stesso batch.
    /// </summary>
    private async Task<(Guid Run, AnalysisRunClaim Claim, Guid Project, HttpClient Client)> ArrangeAsync()
    {
        var client = await App.LoginAsync();
        var project = await NewProject(client);
        for (var n = 1; n <= Documents; n++)
        {
            var subject = n == 1 ? "aaa" : n == Documents ? "zzz" : $"mmm{n}";
            Guid? id = n == 1 ? GapDoc : n == Documents ? LastDoc : null;
            var kind = n switch { 1 or 6 => EvidenceKind.Gap, 4 => EvidenceKind.Decision, 5 => EvidenceKind.Assumption, _ => EvidenceKind.Requirement };
            var doc = await NewDocument(client, project, $"doc{n}", Statement(n), subject, n is 1 or Documents ? "regola" : null, id, kind);
            _ = doc;
        }

        await Execute(
            """
            UPDATE "Evidences" SET "Certainty" = 'ToClarify' WHERE "Statement" LIKE 'Requisito 3:%' AND "ProjectId" = @p;
            UPDATE "Evidences" SET "Certainty" = 'Hypothesized' WHERE "Statement" LIKE 'Requisito 5:%' AND "ProjectId" = @p;
            """,
            ("p", project));

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = (await Json(response)).GetProperty("id").GetGuid();

        // Default invariato: la richiesta nativa congela la v6; il run v11 si ottiene solo con il congelamento esplicito della definizione (come in RH-010).
        Assert.Equal(SourceSnapshotStore.AnalysisDefinitionV6, await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        await Execute(
            "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = CAST(@p AS jsonb) WHERE \"Id\" = @r",
            ("d", DocumentAnalysisWorkflow.DefinitionVersionV11), ("p", SourceSnapshotStore.AnalysisPromptVersionsV11()), ("r", run));
        return (run, await ClaimAsync(run), project, client);
    }

    private static DocumentChatWorkflowTests.FakeChatModel Healthy() => new() { Responder = Respond };

    private Task<long> Committed(Guid run, string? schema = null) => Count(
        "SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'" + (schema is null ? "" : " AND \"SchemaVersion\" = '" + schema + "'"), ("r", run));

    private async Task<DocumentAnalysisContent> ContentAsync(Guid run) =>
        DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));

    // ================================================================ la v11 completa end-to-end con lo stadio Gap

    [Fact]
    public async Task A_v11_hierarchy_with_a_gap_endpoint_of_an_open_conflict_completes_with_the_projected_items_and_the_gap_stage()
    {
        var (run, claim, project, client) = await ArrangeAsync();
        using var _ = client;
        var model = Healthy();

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Equal(DocumentAnalysisWorkflow.DefinitionVersionV11, await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));

        // Instradamento: gerarchia (prima foglia A) e stadio Gap come ultima chiamata, istruzioni v6 e schemi per stadio.
        var hierarchy = model.Requests.Where(r => !DocumentGapStageWorkflowTests.IsStage(r)).ToList();
        Assert.Matches(new Regex(@"^Tipo di batch: foglia\n", RegexOptions.Multiline), hierarchy[0].AllText);
        Assert.Contains("Vista: A", hierarchy[0].AllText, StringComparison.Ordinal);
        Assert.Single(model.Requests, DocumentGapStageWorkflowTests.IsStage);
        Assert.True(DocumentGapStageWorkflowTests.IsStage(model.Requests[^1]));
        Assert.All(hierarchy, r => Assert.Equal(ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV6).Replace("\r\n", "\n"), r.System.Replace("\r\n", "\n")));
        Assert.Contains(hierarchy, r => DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText).Stage == "compare");   // il candidato proposto dalla foglia B è confrontato (Open)
        Assert.Equal("gap", await Scalar<string>("SELECT \"Stage\" FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r ORDER BY \"Ordinal\" DESC LIMIT 1", ("r", run)));

        // I messaggi di espansione e confronto portano gli item già proposti senza testo (items-ref-1): righe `[i<n>] (E<n>)`, mai `[i<n>] (E<n>) <testo>`.
        var candidateStage = hierarchy.Where(r => DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText).Stage is "expand" or "compare").ToList();
        Assert.NotEmpty(candidateStage);
        Assert.All(candidateStage, r =>
        {
            Assert.Matches(new Regex(@"^\[i[1-9][0-9]*\] \(E[1-9][0-9]*(, E[1-9][0-9]*)*\)$", RegexOptions.Multiline), r.AllText);
            Assert.DoesNotMatch(new Regex(@"^\[i[1-9][0-9]*\] \(E[1-9][0-9]*(, E[1-9][0-9]*)*\) .+$", RegexOptions.Multiline), r.AllText);
        });

        // Gli item li ha composti il server: nessuna risposta del modello conteneva item (le foglie A sono solo descriptor).
        var committedAi = await Scalar<string>("SELECT string_agg(\"PayloadJson\"->'ai'->>'items', '|') FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"SchemaVersion\" = 'reconcile-alias-1'", ("r", run));
        Assert.True(committedAi.Split('|').All(s => s == "[]"), committedAi);

        var content = await ContentAsync(run);

        // Un item openQuestions per ogni Gap, con il testo uguale all'enunciato; il Gap estremo del conflitto Open conserva la sua domanda canonica (D2b-1).
        foreach (var gap in new[] { 1, 6 })
        {
            Assert.Single(content.OpenQuestions, q => q.Text == Statement(gap));
        }

        // `ToClarify` resta un elemento da chiarire (D2b-2); la decisione è fra i requisiti, l'ipotesi fra le assunzioni; l'unico estremo non Gap del conflitto Open non è una sezione, è solo un'alternativa.
        Assert.Contains(content.OpenQuestions, q => q.Text == Statement(3));
        Assert.DoesNotContain(content.Requirements, i => i.Text == Statement(3));
        Assert.Contains(content.Requirements, i => i.Text == Statement(4));
        Assert.Contains(content.Assumptions, i => i.Text == Statement(5));
        Assert.Contains(content.Requirements, i => i.Text == Statement(2));
        Assert.DoesNotContain(content.Requirements, i => i.Text == Statement(Documents));
        var conflict = Assert.Single(content.Conflicts);
        Assert.Equal((AnalysisConflictStatus.Open, null), (conflict.Status, conflict.Resolution));
        Assert.Equal(new[] { Statement(1), Statement(Documents) }.Order(StringComparer.Ordinal), conflict.Alternatives.Select(a => a.Text).Order(StringComparer.Ordinal));

        // Stadio Gap: UN record finale per Gap, sulla propria domanda canonica; nessuna valutazione sintetizzata dal server.
        var payload = JsonDocument.Parse(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1'", ("r", run)));
        Assert.Equal("Complete", payload.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("gapAssessments").GetArrayLength());
        Assert.All(payload.RootElement.GetProperty("gapAssessments").EnumerateArray(), a => Assert.NotNull(a.GetProperty("openItem").GetString()));

        // EvidenceCoverage sull'analisi risultante (endpoint di produzione): nessuna evidenza non mappata, nessun finding bloccante; per il Gap estremo l'avviso strutturale D3 è ATTESO e non bloccante
        // (D2b-3, comportamento transitorio); per l'evidenza non Gap dell'altro estremo non c'è alcun avviso.
        var report = await Json(await client.PostAsJsonAsync($"/api/projects/{project}/analyses/{run}/coverage-reports", new { }, Ct));
        var findings = report.GetProperty("findings").EnumerateArray().ToList();
        Assert.DoesNotContain(findings, f => f.GetProperty("code").GetString() == "evidence_not_mapped");
        // I documenti di ReconcileRig sono sintetici: il blocco citato non è materializzato in DocumentBlocks, quindi il controllo del range delle fonti (`source_range_invalid`) segnala OGNI evidenza documentale,
        // qualunque sia la definizione (artefatto della fixture, identico per la v10). Ogni altro finding bloccante sarebbe un difetto della v11.
        Assert.All(findings.Where(f => f.GetProperty("blocking").GetBoolean()), f => Assert.Equal("source_range_invalid", f.GetProperty("code").GetString()));
        var repeats = findings.Where(f => f.GetProperty("code").GetString() == "item_repeats_conflict_alternative").ToList();
        var warning = Assert.Single(repeats);
        Assert.Equal("SemanticRisk", warning.GetProperty("category").GetString());
        Assert.Equal(Statement(1), content.OpenQuestions.Single(q => q.Id == warning.GetProperty("itemId").GetGuid()).Text);   // l'avviso riguarda la domanda del Gap, mai l'evidenza non Gap
        Assert.Equal(findings.Count(f => f.GetProperty("blocking").GetBoolean()), report.GetProperty("summary").GetProperty("blockingFindings").GetInt32());
        Assert.Equal(Documents, findings.Count(f => f.GetProperty("code").GetString() == "source_range_invalid"));   // uno per documento: solo l'artefatto della fixture, nessun altro bloccante
    }

    // ================================================================ ripresa in un processo nuovo

    [Fact]
    public async Task A_crash_in_the_middle_of_the_hierarchy_resumes_with_the_committed_outputs_unchanged_and_the_same_assembled_analysis_on_replay()
    {
        var (run, claim, _, client) = await ArrangeAsync();
        using var _ = client;
        var seen = 0;
        var failing = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Interlocked.Increment(ref seen) == 4 ? throw new InvalidOperationException("rete caduta") : Respond(r) };

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, failing));
        Assert.Equal(3, await Committed(run));
        var hashesBefore = await Scalar<string>("SELECT string_agg(\"PayloadJson\"->>'canonicalHash', ',' ORDER BY \"BatchId\") FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run));

        var healthy = Healthy();
        var resumed = await RunAsync(claim, healthy);

        Assert.True(resumed.Status == ProcessingRunStatus.Completed, await Message(run));
        var all = healthy.Requests.Count + 3;
        Assert.Equal(all, await Committed(run));
        Assert.Equal(all, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r", ("r", run)));   // nessun WorkOutput né batch duplicato
        Assert.Empty(healthy.Requests.Select(r => r.AllText).Intersect(failing.Requests.Take(3).Select(r => r.AllText)));                                   // nessun batch Committed è reinviato
        var hashesAfter = await Scalar<string>("SELECT string_agg(\"PayloadJson\"->>'canonicalHash', ',' ORDER BY \"BatchId\") FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run));
        Assert.True(hashesBefore.Split(',').All(h => hashesAfter.Split(',').Contains(h)), "gli hash canonici già committati devono restare identici dopo la ripresa");

        var content = await ContentAsync(run);
        Assert.Single(content.OpenQuestions, q => q.Text == Statement(1));
        Assert.Single(content.Conflicts);

        var again = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => throw new InvalidOperationException("nessuna inferenza al replay") };
        var replay = await RunAsync(claim, again);
        Assert.Equal(resumed.ResultRevisionId, replay.ResultRevisionId);
        Assert.Empty(again.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(all, await Committed(run));
    }

    [Fact]
    public async Task A_crash_during_the_gap_stage_keeps_the_whole_hierarchy_and_the_takeover_commits_the_same_analysis_once_with_no_inference_for_the_committed_batches()
    {
        var (run, claim, _, client) = await ArrangeAsync();
        using var _ = client;
        var failing = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => DocumentGapStageWorkflowTests.IsStage(r) ? throw new InvalidOperationException("rete caduta durante lo stadio") : Hierarchy(r) };

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, failing));
        var hierarchyBatches = failing.Requests.Count(r => !DocumentGapStageWorkflowTests.IsStage(r));
        Assert.Equal(hierarchyBatches, await Committed(run, "reconcile-alias-1"));
        Assert.Equal(0, await Committed(run, "gap-stage-alias-1"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));

        var healthy = Healthy();
        var resumed = await RunAsync(claim, healthy);

        Assert.True(resumed.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Single(healthy.Requests);                                                    // solo lo stadio Gap: nessuna inferenza per i batch della gerarchia già Committed
        Assert.True(DocumentGapStageWorkflowTests.IsStage(healthy.Requests[0]));
        Assert.Equal(hierarchyBatches, await Committed(run, "reconcile-alias-1"));
        Assert.Equal(1, await Committed(run, "gap-stage-alias-1"));
        var content = await ContentAsync(run);
        Assert.Single(content.OpenQuestions, q => q.Text == Statement(1));                   // il Gap estremo del conflitto Open conserva la domanda dopo la ripresa
        Assert.Single(content.Conflicts);

        var again = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => throw new InvalidOperationException("nessuna inferenza al replay") };
        var replay = await RunAsync(claim, again);
        Assert.Equal(resumed.ResultRevisionId, replay.ResultRevisionId);
        Assert.Empty(again.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }
}
