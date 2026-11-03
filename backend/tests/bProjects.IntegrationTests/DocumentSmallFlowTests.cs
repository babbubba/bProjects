using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T066 (E2E-OFFLINE, backend) — percorso piccolo US1 completo su API reale, PostgreSQL reale, dispatcher reale costruito dalla DI di produzione
/// (<c>AddAiWorkflows</c> + esecutore delle unita di estrazione) e MAF 1.26.0 reale. Sono finti soltanto il parser (HTTP programmabile, manifest
/// deterministici dai byte del corpus T002) e il modello (IChatClient HTTP, nessun DGX). Si prova: upload -> parsing -> estrazione -> Ready ->
/// consultazione -> chat -> Analizza -> modifica/conferma -> Genera -> Markdown e coverage; il percorso REALE della classificazione
/// (POST Pending senza invio -> preflight nel dispatcher -> Classified con regime, ClassificationRecord e deadline dal RequestedAt originale ->
/// esecuzione -> telemetria execution coerente col context-usage); errori e falsi Completed vietati; fonti storiche; vecchio run 001 nello stesso processo.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentSmallFlowTests : ArchiveTestBase
{
    private readonly FlowModel _model;
    private readonly FakeParser _parser = new();

    public DocumentSmallFlowTests() => _model = new FlowModel(Reply);

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 100_000;
        options.ProjectQuotaBytes = 1_000_000;
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        // Dispatcher costruito dal test dalla DI (un solo owner per volta); workflow e unita di estrazione come in produzione.
        // Anche il worker CPU del parsing e guidato dal test (stesso DocumentParseRunner di produzione), cosi ogni passo e deterministico.
        foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType is { } t && (t == typeof(AiDispatchService) || t == typeof(DocumentParseWorker))).ToList())
        {
            services.Remove(hosted);
        }

        services.Configure<ParserOptions>(o =>
        {
            o.BaseUrl = "http://parser.test/";
            o.Secret = "segreto-di-test";
        });
        services.AddHttpClient<ParserClient>().ConfigurePrimaryHttpMessageHandler(() => _parser);
        services.AddSingleton(sp => new InternalModelClient(sp.GetRequiredService<IOptions<AiOptions>>().Value, _model));
        services.AddAiWorkflows();
        services.AddSingleton<IDocumentUnitExecutor>(sp =>
        {
            var model = sp.GetRequiredService<InternalModelClient>();
            var documents = sp.GetRequiredService<IOptions<DocumentOptions>>();
            return new DocumentEvidenceAgent(
                sp.GetRequiredService<IServiceScopeFactory>(),
                AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model, sp.GetRequiredService<ModelRuntimeMonitor>(), documents.Value.Calibration.ToCalibration()),
                model, documents);
        });
    }

    // ================================================================ 1. percorso nominale completo

    [Fact]
    public async Task The_nominal_small_flow_goes_from_upload_to_a_cited_markdown_through_the_real_classification_path()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);

        // --- Upload dei quattro formati (+ due schede in conflitto): nessun upload avvia analisi, chat o chiamate di analisi.
        var files = new[] { "md-basic.md", "txt-negations.txt", "pdf-text.pdf", "docx-basic.docx", "txt-conflict-a.txt", "txt-conflict-b.txt" };
        var documents = new List<Doc>();
        foreach (var name in files)
        {
            documents.Add(await UploadFixture(client, project, name));
        }

        Assert.All(documents, d => Assert.NotEqual("Ready", d.Status));   // l'upload non rende pronto nulla da solo
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p", ("p", project)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ChatTurns\""));
        Assert.Empty(_model.Graphs);

        // --- Parsing, estrazione e aggregazione dal dispatcher: tutte Ready, originali scaricabili e integri.
        await DrainAsync();
        foreach (var d in documents)
        {
            var view = await Json(await client.GetAsync($"/api/projects/{project}/documents/{d.Document}", Ct));
            var version = view.GetProperty("versions")[0];
            Assert.True(version.GetProperty("processingStatus").GetString() == "Ready", d.Name + ": " + version.GetRawText());
            Assert.Equal(d.Sha256, version.GetProperty("sha256").GetString());
            var download = await client.GetByteArrayAsync($"/api/projects/{project}/documents/{d.Document}/versions/1/download", Ct);
            Assert.Equal(d.Bytes, download);
        }

        Assert.All(_model.Graphs, g => Assert.Equal(Graph.Extract, g));                       // solo estrazioni: nessuna chat/analisi avviata
        Assert.Equal(files.Length, _model.Graphs.Count);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p", ("p", project)));

        // --- Consultazione: evidenze documentali, passaggio con range esatto, copertura della preparazione (solo GET, nessun lavoro nuovo).
        var evidences = await DocumentEvidences(client, project);
        Assert.NotEmpty(evidences);
        var conflictA = evidences.Single(e => e.Statement.Contains("30 giorni", StringComparison.Ordinal) && e.Statement.Contains("pagamento", StringComparison.Ordinal));
        var conflictB = evidences.Single(e => e.Statement.Contains("60 giorni", StringComparison.Ordinal));
        Assert.NotEqual(conflictA.Id, conflictB.Id);
        foreach (var source in evidences.SelectMany(e => e.Sources))
        {
            var d = documents.Single(x => x.Document == source.DocumentId);
            var passage = await Json(await client.GetAsync(
                $"/api/projects/{project}/documents/{source.DocumentId}/versions/1/preparations/{source.PreparationId}/passages/{source.BlockId}?start={source.Start}&end={source.End}", Ct));
            Assert.Equal(source.Quote, passage.GetProperty("quote").GetProperty("text").GetString());
            Assert.Equal(d.Name, passage.GetProperty("original").GetProperty("originalName").GetString());
        }

        var coverageGet = await client.GetAsync($"/api/projects/{project}/documents/{documents[0].Document}/versions/1/preparations/latest/coverage", Ct);
        Assert.Equal(HttpStatusCode.OK, coverageGet.StatusCode);
        Assert.DoesNotContain(_model.Graphs, g => g != Graph.Extract);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p", ("p", project)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ChatTurns\""));

        // --- Chat: il POST salva solo Pending (nessuna misura, nessun invio); il preflight avviene nel dispatcher, prima del claim.
        var extractCalls = _model.Graphs.Count;
        var turn = await PostChat(client, project, "Qual e il termine di pagamento?");
        await AssertPending("ChatTurns", turn);
        Assert.Equal(extractCalls, _model.Graphs.Count);
        var requestedAt = await Scalar<DateTime>("SELECT \"RequestedAt\" AT TIME ZONE 'UTC' FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn));

        await Task.Delay(1_200, Ct);                                                          // attesa reale in coda: la deadline resta quella del t0 originale
        var dispatcher = Dispatcher();
        Assert.True(await dispatcher.RunOnceAsync(Ct));                                       // passo 1: SOLO classificazione (nessun invio)
        Assert.Equal(extractCalls, _model.Graphs.Count);
        Assert.Equal("Queued", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        var classified = await Row("ChatTurns", turn);
        Assert.Equal("Classified", classified["ClassificationStatus"]);
        Assert.Equal("Ordinary", classified["InitialRegime"]);                               // payload piccolo e misurato nel tokenizer
        Assert.Equal("Ordinary", classified["Regime"]);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"InferenceCalls\" WHERE \"ChatTurnId\" = @t", ("t", turn)));
        await AssertClassificationRecord("ChatTurns", "ChatTurn", turn, measured: true, expectedRegime: "Ordinary");
        await AssertDeadlinesFromOriginalRequest("ChatTurns", turn, requestedAt, minimumWaitMs: 1_000);

        Assert.True(await dispatcher.RunOnceAsync(Ct));                                       // passo 2: claim ed esecuzione
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal([Graph.DocChat], _model.Graphs.Skip(extractCalls));
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        await AssertStartedAfterClassification("ChatTurns", turn);
        var answer = await Scalar<string>("SELECT \"Body\" FROM \"ConversationMessages\" WHERE \"ChatTurnId\" = @t AND \"Role\" = 'Assistant'", ("t", turn));
        var cited = Regex.Matches(answer, @"\[fonte:([0-9a-f-]{36})\]").Select(m => Guid.Parse(m.Groups[1].Value)).Distinct().ToList();
        Assert.Contains(conflictA.Sources[0].SourceId, cited);
        Assert.All(cited, id => Assert.Contains(evidences, e => e.Sources.Any(s => s.SourceId == id)));   // output citato: ogni marcatore e una fonte documentale reale

        // --- Telemetria: execution nelle viste e nel monitor, coerente con le chiamate registrate.
        var turnView = await Json(await client.GetAsync($"/api/projects/{project}/chat-turns/{turn}", Ct));
        var usage = await Json(await client.GetAsync($"/api/projects/{project}/context-usage", Ct));
        await AssertExecutionTelemetry(turnView.GetProperty("execution"), usage, "chat", turn, expectedRegime: "Ordinary", expectedReason: null, requestedAt, waited: true);

        // --- Analizza (con la chat gia presente: input misto messaggio + documenti): Pending, preflight, Extended workload_unknown, esecuzione.
        var analysis = await StartAnalysis(client, project);
        await AssertPending("ProcessingRuns", analysis);
        var before = _model.Graphs.Count;
        Assert.True(await Dispatcher().RunOnceAsync(Ct));
        Assert.Equal(before, _model.Graphs.Count);                                            // classificazione senza invio
        Assert.Equal(("Extended", (string?)"workload_unknown"), Pair(await Row("ProcessingRuns", analysis), "InitialRegime", "ClassificationReason"));
        await AssertClassificationRecord("ProcessingRuns", "ProcessingRun", analysis, measured: false, expectedRegime: "Extended");
        await DrainAsync();
        var run = await Row("ProcessingRuns", analysis);
        Assert.Equal(("Completed", (string?)null), Pair(run, "Status", "ErrorCode"));
        Assert.Contains(Graph.DocAnalysis, _model.Graphs);
        await AssertStartedAfterClassification("ProcessingRuns", analysis);
        var analysisRunView = await Json(await client.GetAsync($"/api/projects/{project}/processing-runs/{analysis}", Ct));
        var analysisUsage = await Json(await client.GetAsync($"/api/projects/{project}/context-usage", Ct));
        await AssertExecutionTelemetry(analysisRunView.GetProperty("execution"), analysisUsage, "analysis", analysis, expectedRegime: "Extended", expectedReason: "workload_unknown",
            await Scalar<DateTime>("SELECT \"RequestedAt\" AT TIME ZONE 'UTC' FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", analysis)));

        var analysisView = await Json(await client.GetAsync($"/api/projects/{project}/analyses/{analysis}", Ct));
        Assert.Equal("1.1.0", analysisView.GetProperty("schemaVersion").GetString());
        var content = JsonNode.Parse(analysisView.GetProperty("content").GetRawText())!;
        var passageSources = content["sources"]!.AsArray().Where(s => s!["kind"]!.GetValue<string>() == "DocumentPassage").ToList();
        Assert.NotEmpty(passageSources);
        Assert.All(passageSources, s => Assert.Contains(evidences.SelectMany(e => e.Sources), x => x.SourceId.ToString() == s!["id"]!.GetValue<string>() && x.Quote == s["quote"]!.GetValue<string>()));
        Assert.Contains(content["requirements"]!.AsArray(), i => i!["classification"]!.GetValue<string>() == "Documented");
        Assert.All(content["requirements"]!.AsArray(), i => Assert.NotEmpty(i!["evidenceRefs"]!.AsArray()));
        Assert.Equal(DocumentAnalysisWorkflow.DefinitionVersion, await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", analysis)));
        var linked = await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", analysis));
        Assert.True(linked >= passageSources.Count);

        // --- Modifica PM (nuova revisione 1.1) e conferma: lo schema si conserva, le fonti documentali sono immutabili, Documented non diventa Declared.
        var revision1 = analysisView.GetProperty("currentRevisionId").GetGuid();
        var firstRequirement = content["requirements"]!.AsArray().First(i => i!["classification"]!.GetValue<string>() == "Documented")!;
        var editedId = firstRequirement["id"]!.GetValue<string>();
        firstRequirement["text"] = firstRequirement["text"]!.GetValue<string>() + " (verificato dal PM)";
        var edit = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{project}/analyses/{analysis}/preview") { Content = JsonContent.Create(content) };
        edit.Headers.TryAddWithoutValidation("If-Match", $"\"{revision1}\"");
        var edited = await client.SendAsync(edit, Ct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var editedView = await Json(edited);
        var revision2 = editedView.GetProperty("currentRevisionId").GetGuid();
        Assert.NotEqual(revision1, revision2);
        Assert.Equal("1.1.0", editedView.GetProperty("schemaVersion").GetString());
        Assert.Equal("PmEdit", editedView.GetProperty("origin").GetString());
        Assert.False(editedView.GetProperty("canGenerate").GetBoolean());
        var editedItem = editedView.GetProperty("content").GetProperty("requirements").EnumerateArray().Single(i => i.GetProperty("id").GetString() == editedId);
        Assert.Equal("Documented", editedItem.GetProperty("classification").GetString());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"Id\" = @r AND \"Origin\" = 'Ai' AND \"SchemaVersion\" = '1.1.0'", ("r", revision1)));

        var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/confirm");
        confirm.Headers.TryAddWithoutValidation("If-Match", $"\"{revision2}\"");
        var confirmed = await client.SendAsync(confirm, Ct);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var confirmedView = await Json(confirmed);
        Assert.Equal(("PmConfirmed", true), (confirmedView.GetProperty("validationState").GetString(), confirmedView.GetProperty("canGenerate").GetBoolean()));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p AND \"Kind\" = 'Document'", ("p", project)));   // la conferma non avvia la generazione

        // --- Genera: Pending, preflight, esecuzione, Markdown citato.
        var generation = await StartGeneration(client, project, analysis, revision2);
        await AssertPending("ProcessingRuns", generation);
        await DrainAsync();
        var genRow = await Row("ProcessingRuns", generation);
        Assert.Equal(("Completed", (string?)null), Pair(genRow, "Status", "ErrorCode"));
        Assert.Equal("Classified", genRow["ClassificationStatus"]);
        await AssertClassificationRecord("ProcessingRuns", "ProcessingRun", generation, measured: true, expectedRegime: null);
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", generation)));
        var generationUsage = await Json(await client.GetAsync($"/api/projects/{project}/context-usage", Ct));
        await AssertExecutionTelemetry((await Json(await client.GetAsync($"/api/projects/{project}/processing-runs/{generation}", Ct))).GetProperty("execution"), generationUsage, "document", generation,
            expectedRegime: genRow["InitialRegime"]!.ToString()!, expectedReason: null, await Scalar<DateTime>("SELECT \"RequestedAt\" AT TIME ZONE 'UTC' FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", generation)), documentSources: false);
        var draft = await Json(await client.GetAsync($"/api/projects/{project}/draft", Ct));
        Assert.Equal(revision2, draft.GetProperty("analysisRevisionId").GetGuid());
        Assert.Equal("Proposed", draft.GetProperty("status").GetString());
        Assert.Equal("NotApproved", draft.GetProperty("approvalStatus").GetString());
        var markdown = draft.GetProperty("markdown").GetString()!;
        var draftSources = Regex.Matches(markdown, @"\[fonte:([0-9a-f-]{36})\]").Select(m => Guid.Parse(m.Groups[1].Value)).Distinct().ToList();
        Assert.NotEmpty(draftSources);
        var confirmedSources = confirmedView.GetProperty("content").GetProperty("sources").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid(), x => x.GetProperty("kind").GetString());
        Assert.All(draftSources, id => Assert.Contains(id, confirmedSources.Keys));          // ogni fonte citata appartiene alla revisione confermata
        Assert.Contains(draftSources, id => confirmedSources[id] == "DocumentPassage");     // e almeno una e un passaggio documentale storico

        // --- Coverage-2 della revisione confermata e della bozza (deterministica, nessun errore bloccante).
        var coverage = await client.PostAsJsonAsync($"/api/projects/{project}/analyses/{analysis}/coverage-reports", new { revisionId = revision2, draftId = draft.GetProperty("id").GetGuid() }, Ct);
        Assert.Contains(coverage.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Created });
        var report = await Json(coverage);
        Assert.Equal("coverage-2", report.GetProperty("checkerVersion").GetString());
        Assert.Equal(0, report.GetProperty("counts").GetProperty("deterministicErrors").GetInt32());
        var summary = report.GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("blockingFindings").GetInt32());
        Assert.Equal(0, summary.GetProperty("portionsMissing").GetInt32());
        Assert.True(summary.GetProperty("expectedEvidence").GetInt32() > 0);
        Assert.Equal(summary.GetProperty("expectedEvidence").GetInt32(), summary.GetProperty("representedEvidence").GetInt32());   // ogni evidenza documentale e rappresentata

        // --- Nessun falso Completed: un solo workflow per root, una sola chiamata, nessuna chiamata del grafo 001.
        Assert.DoesNotContain(_model.Graphs, g => g is Graph.Chat001 or Graph.Analysis001 or Graph.Draft001);
        Assert.Equal([Graph.DocChat, Graph.DocAnalysis, Graph.DocDraft], _model.Graphs.Where(g => g != Graph.Extract));
    }

    // ================================================================ 2. attesa oltre la deadline ordinaria: mai un invio, mai Completed

    [Fact]
    public async Task A_root_classified_after_its_ordinary_deadline_fails_deadline_exceeded_without_any_model_call()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await UploadFixture(client, project, "txt-conflict-a.txt");
        await DrainAsync();
        var sent = _model.Graphs.Count;
        var turn = await PostChat(client, project, "Qual e il termine di pagamento?");
        // Il root ha atteso nel Pending oltre la deadline ordinaria (180 s): si arretra il t0 con le colonne derivate coerenti.
        await Execute(
            "UPDATE \"ChatTurns\" SET \"RequestedAt\" = \"RequestedAt\" - interval '200 seconds', \"DeadlineAt\" = \"DeadlineAt\" - interval '200 seconds', " +
            "\"HardDeadlineAt\" = \"HardDeadlineAt\" - interval '200 seconds', \"EffectiveDeadlineAt\" = \"EffectiveDeadlineAt\" - interval '200 seconds' WHERE \"Id\" = @t", ("t", turn));

        await DrainAsync();

        var row = await Row("ChatTurns", turn);
        Assert.Equal(("Failed", (string?)"deadline_exceeded"), Pair(row, "Status", "ErrorCode"));
        Assert.True(row["ClassificationStatus"]?.ToString() == "Classified", Describe(row));
        Assert.Equal("Ordinary", row["InitialRegime"]);
        Assert.Equal(sent, _model.Graphs.Count);                                              // nessun invio, nessun falso Completed
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"ChatTurnId\" = @t AND \"Role\" = 'Assistant'", ("t", turn)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"InferenceCalls\" WHERE \"ChatTurnId\" = @t", ("t", turn)));
    }

    // ================================================================ 3. errori di parsing: mai Ready, mai fonti inventate

    [Fact]
    public async Task Parser_errors_never_become_ready_sources_and_the_pm_gets_retry_and_a_guard_on_analysis()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        // Un DOCX che non e un contenitore valido e rifiutato gia dall'acquisizione (415): nessun originale, nessun job.
        var rejected = await Upload(client, project, RealParser.Fixture("docx-corrupt.docx"), "docx-corrupt.docx", DocumentUploadValidation.MediaTypeOf(DocumentFormat.Docx), displayName: "docx-corrupt");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, rejected.StatusCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        var corrupt = await UploadFixture(client, project, "pdf-corrupt.pdf");
        var protectedUpload = await Upload(client, project, RealParser.Fixture("pdf-protected.pdf"), "pdf-protected.pdf", "application/pdf", displayName: "pdf-protected");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, protectedUpload.StatusCode);   // PDF con password: rifiutato dall'acquisizione
        Assert.Equal("protected_file", (await Json(protectedUpload)).GetProperty("code").GetString());
        var mixed = await UploadFixture(client, project, "pdf-mixed.pdf");

        await DrainAsync();

        Assert.Equal("Failed", await StatusOf(client, project, corrupt));
        Assert.Equal("Incomplete", await StatusOf(client, project, mixed));
        Assert.Empty(_model.Graphs);                                                          // parser rifiutato/incompleto: nessuna chiamata al modello
        Assert.Equal(0, await Count("SELECT count(*) FROM \"EvidenceExtractionVersions\" WHERE \"ProjectId\" = @p AND \"OriginKind\" = 'Document'", ("p", project)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProjectDocuments\" WHERE \"ProjectId\" = @p AND \"LatestReadyVersionId\" IS NOT NULL", ("p", project)));
        Assert.Equal(corrupt.Bytes, await client.GetByteArrayAsync($"/api/projects/{project}/documents/{corrupt.Document}/versions/1/download", Ct));   // l'originale resta scaricabile

        // Il PM non puo avviare un'analisi su sole fonti non pronte (ne e disponibile alcuna fonte): nessun run, nessun invio.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var refused = await client.SendAsync(request, Ct);
        var refusedBody = await Json(refused);
        Assert.True(refused.StatusCode == HttpStatusCode.Conflict, $"{(int)refused.StatusCode} {refusedBody}");
        Assert.Equal("sources_not_ready", refusedBody.GetProperty("code").GetString());
        var blocking = refusedBody.GetProperty("blockingSources").EnumerateArray().Select(b => (b.GetProperty("displayName").GetString(), b.GetProperty("status").GetString())).ToList();
        Assert.Contains(("pdf-corrupt.pdf", "Failed"), blocking);
        Assert.Contains(("pdf-mixed.pdf", "Incomplete"), blocking);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p", ("p", project)));

        // Retry esplicito del job di parsing fallito: un solo nuovo job (idempotente per chiave), ancora Failed con lo stesso parser.
        var job = (await Json(await client.GetAsync($"/api/projects/{project}/documents/{corrupt.Document}/versions/1/preparations/latest", Ct))).GetProperty("parseJobId").GetGuid();
        var key = Guid.NewGuid().ToString();
        for (var i = 0; i < 2; i++)
        {
            var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/document-jobs/{job}/retry");
            retry.Headers.Add("Idempotency-Key", key);
            Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(retry, Ct)).StatusCode);
        }

        Assert.Equal(2, await Count("SELECT count(*) FROM \"DocumentPreparations\" p JOIN \"DocumentVersions\" v ON v.\"Id\" = p.\"DocumentVersionId\" WHERE v.\"DocumentId\" = @d", ("d", corrupt.Document)));
        await DrainAsync();
        Assert.Equal("Failed", await StatusOf(client, project, corrupt));
        Assert.Empty(_model.Graphs);
    }

    // ================================================================ 4. output del modello non validi: mai Completed

    [Fact]
    public async Task Invalid_or_uncited_model_outputs_never_complete_chat_analysis_or_generation()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await UploadFixture(client, project, "txt-conflict-a.txt");
        await UploadFixture(client, project, "txt-negations.txt");
        await DrainAsync();

        // Chat: citazione di una fonte inesistente => turno Failed, nessuna risposta dell'assistente.
        _model.Rewrite = (graph, _, reply) => graph == Graph.DocChat ? reply.Replace("[fonte:", $"[fonte:{Guid.NewGuid()}][fonte:") : reply;
        var turn = await PostChat(client, project, "Qual e il termine di pagamento?");
        await DrainAsync();
        Assert.True("Failed" == await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)), Describe(await Row("ChatTurns", turn)) + " graphs=" + string.Join(",", _model.Graphs));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"ChatTurnId\" = @t AND \"Role\" = 'Assistant'", ("t", turn)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ChatTurn' AND \"OwnerId\" = @t AND \"Status\" = 'Committed'", ("t", turn)));

        // Analisi: evidenza sconosciuta nella mappa => run Failed, nessuna revisione, nessuna analisi corrente.
        _model.Rewrite = (graph, _, reply) => graph == Graph.DocAnalysis ? Mutate(reply, n => n["evidenceMap"]![0]!["evidenceIds"]![0] = Guid.NewGuid().ToString()) : reply;
        var unknown = await StartAnalysis(client, project);
        await DrainAsync();
        await AssertNotCompleted(project, unknown, "work_output_invalid");

        // Analisi: citazione alterata (testo inventato) => Failed.
        _model.Rewrite = (graph, _, reply) => graph == Graph.DocAnalysis ? Mutate(reply, n => n["analysis"]!["sources"]![0]!["quote"] = "testo mai scritto nel documento") : reply;
        var invented = await StartAnalysis(client, project);
        await DrainAsync();
        await AssertNotCompleted(project, invented, "work_output_invalid");

        // Analisi: evidenza documentale non rappresentata (copertura incompleta) => Failed, mai una revisione parziale.
        _model.Rewrite = (graph, _, reply) => graph == Graph.DocAnalysis ? Mutate(reply, n =>
        {
            n["analysis"]!["requirements"]!.AsArray().RemoveAt(0);
            n["evidenceMap"]!.AsArray().RemoveAt(0);
        }) : reply;
        var partial = await StartAnalysis(client, project);
        await DrainAsync();
        await AssertNotCompleted(project, partial, "coverage_incomplete");
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" r JOIN \"ProcessingRuns\" x ON x.\"Id\" = r.\"AnalysisId\" WHERE x.\"ProjectId\" = @p", ("p", project)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Projects\" WHERE \"Id\" = @p AND \"CurrentAnalysisId\" IS NOT NULL", ("p", project)));

        // Analisi valida: si prosegue, e una bozza con fonte fuori dagli item => Failed, nessuna Draft.
        _model.Rewrite = null;
        var good = await StartAnalysis(client, project);
        await DrainAsync();
        var goodRow = await Row("ProcessingRuns", good);
        Assert.Equal("Completed", goodRow["Status"]?.ToString());
        var revision = await ConfirmAsync(client, project, good);
        _model.Rewrite = (graph, _, reply) => graph == Graph.DocDraft ? reply.Replace("[fonte:", $"[fonte:{Guid.NewGuid()}][fonte:") : reply;
        var badDraft = await StartGeneration(client, project, good, revision);
        await DrainAsync();
        var badRow = await Row("ProcessingRuns", badDraft);
        Assert.Equal(("Failed", "work_output_invalid"), (badRow["Status"]?.ToString(), badRow["ErrorCode"]?.ToString()));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\" WHERE \"ProjectId\" = @p", ("p", project)));
        Assert.Equal(404, (int)(await client.GetAsync($"/api/projects/{project}/draft", Ct)).StatusCode);

        // Bozza che omette un item della revisione confermata => Failed.
        _model.Rewrite = (graph, _, reply) => graph == Graph.DocDraft ? Mutate(reply, n => n["blocks"]![0]!["itemIds"]!.AsArray().RemoveAt(0)) : reply;
        var omitted = await StartGeneration(client, project, good, revision);
        await DrainAsync();
        var omittedRow = await Row("ProcessingRuns", omitted);
        Assert.Equal(("Failed", "work_output_invalid"), (omittedRow["Status"]?.ToString(), omittedRow["ErrorCode"]?.ToString()));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\" WHERE \"ProjectId\" = @p", ("p", project)));

        // Dopo gli errori, un output valido completa (nessuno stato residuo avvelena il percorso).
        _model.Rewrite = null;
        var finalDraft = await StartGeneration(client, project, good, revision);
        await DrainAsync();
        Assert.Equal("Completed", (await Row("ProcessingRuns", finalDraft))["Status"]?.ToString());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\" WHERE \"ProjectId\" = @p", ("p", project)));
    }

    private async Task AssertNotCompleted(Guid project, Guid run, string errorCode)
    {
        var row = await Row("ProcessingRuns", run);
        Assert.Equal("Failed", row["Status"]?.ToString());
        Assert.Equal(errorCode, row["ErrorCode"]?.ToString());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Projects\" WHERE \"Id\" = @p AND \"CurrentAnalysisId\" = @r", ("p", project), ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
    }

    // ================================================================ 5. fonti storiche + vecchio run 001 nello stesso processo

    [Fact]
    public async Task Historical_document_sources_stay_readable_after_a_new_version_and_a_001_run_coexists_in_the_same_process()
    {
        using var client = await App.LoginAsync();

        // Vecchio run 001 (progetto senza documenti): chat, analisi, conferma, generazione sul grafo 001, schema 1.0.0 invariato.
        var legacy = await NewProject(client);
        var legacyTurn = await PostChat(client, legacy, "Vorrei un portale clienti.");
        await DrainAsync();
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", legacyTurn)));
        var legacyAnalysis = await StartAnalysis(client, legacy);
        await DrainAsync();
        Assert.Equal("Completed", (await Row("ProcessingRuns", legacyAnalysis))["Status"]?.ToString());
        Assert.Equal("project-analysis-v3", await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", legacyAnalysis)));
        var legacyRevision = await ConfirmAsync(client, legacy, legacyAnalysis);
        var legacyGeneration = await StartGeneration(client, legacy, legacyAnalysis, legacyRevision);
        await DrainAsync();
        Assert.Equal("Completed", (await Row("ProcessingRuns", legacyGeneration))["Status"]?.ToString());
        Assert.Equal("1.0.0", await Scalar<string>("SELECT \"SchemaVersion\" FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", legacyRevision)));
        Assert.Equal("<null>", await Text("ProcessingRuns", legacyAnalysis, "ClassificationStatus"));   // root 001: mai classificato
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ClassificationRecords\" WHERE \"ProjectId\" = @p", ("p", legacy)));
        Assert.Equal([Graph.Chat001, Graph.Analysis001, Graph.Draft001], _model.Graphs);

        // Progetto documentale: analisi confermata su v1, poi nuova versione v2 (testo diverso) pronta.
        var project = await NewProject(client);
        var v1 = await UploadFixture(client, project, "txt-conflict-a.txt");
        await DrainAsync();
        var analysis = await StartAnalysis(client, project);
        await DrainAsync();
        Assert.Equal("Completed", (await Row("ProcessingRuns", analysis))["Status"]?.ToString());
        var revision = await ConfirmAsync(client, project, analysis);
        var before = await Json(await client.GetAsync($"/api/projects/{project}/analyses/{analysis}", Ct));
        var beforeContent = before.GetProperty("content").GetRawText();
        var evidencesV1 = await DocumentEvidences(client, project);
        var oldSource = evidencesV1.Single().Sources.Single();

        var v2Bytes = Encoding.UTF8.GetBytes("Scheda A\n\nIl termine di pagamento e' 90 giorni, salvo diverso accordo scritto.\n");
        var upload = await Upload(client, project, v2Bytes, "txt-conflict-a.txt", documentId: v1.Document);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        await DrainAsync();

        Assert.Equal("Ready", await Scalar<string>("SELECT p.\"Status\" FROM \"DocumentPreparations\" p JOIN \"DocumentVersions\" v ON v.\"Id\" = p.\"DocumentVersionId\" WHERE v.\"DocumentId\" = @d AND v.\"Version\" = 2", ("d", v1.Document)));
        Assert.Equal(2, await Count("SELECT count(*) FROM \"DocumentVersions\" WHERE \"DocumentId\" = @d", ("d", v1.Document)));

        // La revisione confermata resta identica byte per byte e cita ancora il passaggio storico della v1, leggibile e con lo stesso testo.
        var after = await Json(await client.GetAsync($"/api/projects/{project}/analyses/{analysis}", Ct));
        Assert.Equal(beforeContent, after.GetProperty("content").GetRawText());
        Assert.Equal(revision, after.GetProperty("currentRevisionId").GetGuid());
        var passage = await Json(await client.GetAsync(
            $"/api/projects/{project}/documents/{oldSource.DocumentId}/versions/1/preparations/{oldSource.PreparationId}/passages/{oldSource.BlockId}?start={oldSource.Start}&end={oldSource.End}", Ct));
        Assert.Equal(oldSource.Quote, passage.GetProperty("quote").GetProperty("text").GetString());
        Assert.Contains("30 giorni", oldSource.Quote);
        Assert.DoesNotContain("90", oldSource.Quote);

        // Il vecchio run 001 e la sua conferma restano intatti nello stesso processo; il documentale non ha usato il grafo 001.
        Assert.Equal("1.0.0", await Scalar<string>("SELECT \"SchemaVersion\" FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", legacyRevision)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\" WHERE \"ProjectId\" = @p", ("p", legacy)));
        Assert.DoesNotContain(_model.Graphs.Skip(3), g => g is Graph.Chat001 or Graph.Analysis001 or Graph.Draft001);

        // Una nuova analisi usa la v2 (nuovo snapshot) senza alterare la precedente.
        var second = await StartAnalysis(client, project);
        await DrainAsync();
        Assert.Equal("Completed", (await Row("ProcessingRuns", second))["Status"]?.ToString());
        var secondContent = (await Json(await client.GetAsync($"/api/projects/{project}/analyses/{second}", Ct))).GetProperty("content").GetRawText();
        Assert.Contains("90 giorni", secondContent);
        Assert.DoesNotContain("90 giorni", beforeContent);
    }

    // ================================================================ supporto: dispatcher e dati

    private AiDispatchService Dispatcher() => ActivatorUtilities.CreateInstance<AiDispatchService>(App.Services);

    private async Task DrainAsync()
    {
        var dispatcher = Dispatcher();
        for (var i = 0; i < 200; i++)
        {
            ParseRunOutcome? parsed;
            using (var scope = App.Services.CreateScope())
            {
                parsed = await scope.ServiceProvider.GetRequiredService<DocumentParseRunner>().RunNextAsync("test-parse", Ct);
            }

            if (!await dispatcher.RunOnceAsync(Ct) && parsed is null)
            {
                return;
            }
        }

        Assert.Fail("Il dispatcher non e andato a riposo.");
    }

    private sealed record Doc(Guid Document, string Name, byte[] Bytes, string Sha256, string Status);

    private async Task<Doc> UploadFixture(HttpClient client, Guid project, string name)
    {
        var bytes = RealParser.Fixture(name);
        var response = await Upload(client, project, bytes, name, DocumentUploadValidation.MediaTypeOf(RealParser.FormatOf(name)), displayName: name);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{name}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        var created = await Json(response);
        return new Doc(created.GetProperty("documentId").GetGuid(), name, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), created.GetProperty("processingStatus").GetString()!);
    }

    private static async Task<string> StatusOf(HttpClient client, Guid project, Doc doc) =>
        (await Json(await client.GetAsync($"/api/projects/{project}/documents/{doc.Document}", Ct))).GetProperty("versions")[0].GetProperty("processingStatus").GetString()!;

    private sealed record EvSource(Guid SourceId, Guid DocumentId, Guid PreparationId, Guid BlockId, int Start, int End, string Quote);

    private sealed record Ev(Guid Id, string Statement, List<EvSource> Sources);

    private static async Task<List<Ev>> DocumentEvidences(HttpClient client, Guid project)
    {
        var page = await Json(await client.GetAsync($"/api/projects/{project}/evidence?originKind=Document&viewVersion=1.1.0&limit=100", Ct));
        return [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("evidence")).Select(e => new Ev(
            e.GetProperty("id").GetGuid(), e.GetProperty("statement").GetString()!,
            [.. e.GetProperty("content").GetProperty("sources").EnumerateArray().Select(s => new EvSource(
                s.GetProperty("sourceId").GetGuid(), s.GetProperty("documentId").GetGuid(), s.GetProperty("preparationId").GetGuid(), s.GetProperty("blockId").GetGuid(),
                s.GetProperty("start").GetInt32(), s.GetProperty("end").GetInt32(), s.GetProperty("quote").GetString()!))]))];
    }

    private async Task<Guid> PostChat(HttpClient client, Guid project, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("chatTurnId").GetGuid();
    }

    private async Task<Guid> StartAnalysis(HttpClient client, Guid project)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> ConfirmAsync(HttpClient client, Guid project, Guid analysis)
    {
        var preview = await Json(await client.GetAsync($"/api/projects/{project}/analyses/{analysis}", Ct));
        var revision = preview.GetProperty("currentRevisionId").GetGuid();
        var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/confirm");
        confirm.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(confirm, Ct)).StatusCode);
        return revision;
    }

    private async Task<Guid> StartGeneration(HttpClient client, Guid project, Guid analysis, Guid revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/document-generations") { Content = JsonContent.Create(new { revisionId = revision }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<List<string>> Strings(string sql, params (string Name, object Value)[] args)
    {
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private async Task<Dictionary<string, object?>> Row(string table, Guid id)
    {
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand($"SELECT * FROM \"{table}\" WHERE \"Id\" = @i", connection);
        command.Parameters.AddWithValue("i", id);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return Enumerable.Range(0, reader.FieldCount).ToDictionary(i => reader.GetName(i), i => reader.IsDBNull(i) ? null : reader.GetValue(i));
    }

    private static string Describe(Dictionary<string, object?> row) => string.Join("; ", row.Where(kv => kv.Value is not null && kv.Key is "Status" or "ErrorCode" or "ErrorMessage" or "ClassificationStatus" or "InitialRegime" or "Regime" or "ClassificationReason" or "AttemptCount").Select(kv => $"{kv.Key}={kv.Value}"));

    private static (string?, string?) Pair(Dictionary<string, object?> row, string first, string second) => (row[first]?.ToString(), row[second]?.ToString());

    private Task<string> Text(string table, Guid id, string column) =>
        Scalar<string>($"SELECT COALESCE(\"{column}\"::text, '<null>') FROM \"{table}\" WHERE \"Id\" = @i", ("i", id));

    // ================================================================ supporto: classificazione e telemetria

    /// <summary>Dopo il POST: solo Pending (Extended, nessun InitialRegime, deadline = cap dal t0), nessun record di classificazione, nessuna inferenza.</summary>
    private async Task AssertPending(string table, Guid id)
    {
        Assert.Equal(("Queued", "Pending", "Extended", "<null>", "preflight_pending"),
            (await Text(table, id, "Status"), await Text(table, id, "ClassificationStatus"), await Text(table, id, "Regime"), await Text(table, id, "InitialRegime"), await Text(table, id, "ClassificationReason")));
        Assert.Equal(DocumentRegimePolicy.ComputePolicyHash(180, 3_600), await Text(table, id, "PolicyHash"));
        Assert.Equal(("0", "0"), (await Text(table, id, "PromotionCount"), await Text(table, id, "CallCount")));
        Assert.True(await Scalar<bool>(
            $"SELECT \"HardDeadlineAt\" = \"RequestedAt\" + interval '3600 seconds' AND \"EffectiveDeadlineAt\" = \"HardDeadlineAt\" AND \"OrdinaryDeadlineAt\" IS NULL FROM \"{table}\" WHERE \"Id\" = @i", ("i", id)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ClassificationRecords\" WHERE \"OwnerId\" = @i", ("i", id)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"InferenceCalls\" WHERE \"ChatTurnId\" = @i OR \"ProcessingRunId\" = @i", ("i", id)));
    }

    private async Task AssertClassificationRecord(string table, string ownerKind, Guid id, bool measured, string? expectedRegime)
    {
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ClassificationRecords\" WHERE \"OwnerKind\" = @k AND \"OwnerId\" = @i", ("k", ownerKind), ("i", id)));
        var record = await Row("ClassificationRecords", await Scalar<Guid>("SELECT \"Id\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = @i", ("i", id)));
        Assert.Equal(1, Convert.ToInt32(record["Ordinal"]));
        Assert.Equal(measured ? "measured" : "unavailable", record["TokenQuality"]);
        Assert.Equal(await Text(table, id, "PolicyHash"), record["PolicyHash"]);
        if (measured)
        {
            Assert.NotNull(record["PayloadTokens"]);
            Assert.NotNull(record["WindowTokens"]);
            Assert.NotNull(record["InputBudgetTokens"]);
            Assert.True(Convert.ToInt32(record["PayloadTokens"]) > 0 && Convert.ToInt32(record["PayloadTokens"]) <= Convert.ToInt32(record["InputBudgetTokens"]));
        }
        else
        {
            Assert.Null(record["PayloadTokens"]);
            Assert.NotEqual("[]", record["UnknownReasonsJson"]?.ToString()?.Replace(" ", ""));
        }

        if (expectedRegime is not null)
        {
            Assert.Equal(expectedRegime, await Text(table, id, "InitialRegime"));
        }
    }

    /// <summary>Le deadline derivano dal RequestedAt ORIGINALE (non dall'istante della classificazione): ordinaria t0+180 s, hard t0+3600 s.</summary>
    private async Task AssertDeadlinesFromOriginalRequest(string table, Guid id, DateTime requestedAtUtc, int minimumWaitMs)
    {
        Assert.True(await Scalar<bool>(
            $"SELECT x.\"OrdinaryDeadlineAt\" = x.\"RequestedAt\" + interval '180 seconds' AND x.\"HardDeadlineAt\" = x.\"RequestedAt\" + interval '3600 seconds' " +
            $"AND x.\"EffectiveDeadlineAt\" = CASE x.\"Regime\" WHEN 'Ordinary' THEN x.\"OrdinaryDeadlineAt\" ELSE x.\"HardDeadlineAt\" END AND x.\"DeadlineAt\" = x.\"EffectiveDeadlineAt\" " +
            $"AND c.\"ObservedAt\" >= x.\"RequestedAt\" + interval '{minimumWaitMs} milliseconds' FROM \"{table}\" x JOIN \"ClassificationRecords\" c ON c.\"OwnerId\" = x.\"Id\" WHERE x.\"Id\" = @i", ("i", id)));
        Assert.Equal(requestedAtUtc, await Scalar<DateTime>($"SELECT \"RequestedAt\" AT TIME ZONE 'UTC' FROM \"{table}\" WHERE \"Id\" = @i", ("i", id)));   // il t0 non e stato riscritto
    }

    private async Task AssertStartedAfterClassification(string table, Guid id) =>
        Assert.True(await Scalar<bool>($"SELECT c.\"ObservedAt\" <= x.\"StartedAt\" FROM \"ClassificationRecords\" c JOIN \"{table}\" x ON x.\"Id\" = c.\"OwnerId\" WHERE c.\"OwnerId\" = @i", ("i", id)));

    /// <summary>
    /// «execution» della vista e del monitor descrive proprio la classificazione persistita e le chiamate registrate: regime, ricevuta (hash del record),
    /// deadline dal t0, intervalli esclusivi (coda + preflight + backoff + elaborazione <= trascorso), composizione con document_sources.
    /// </summary>
    private async Task AssertExecutionTelemetry(JsonElement execution, JsonElement usage, string kind, Guid root, string expectedRegime, string? expectedReason, DateTime requestedAtUtc, bool waited = false, bool documentSources = true)
    {
        Assert.Equal("Classified", execution.GetProperty("classificationStatus").GetString());
        Assert.Equal(expectedRegime, execution.GetProperty("initialRegime").GetString());
        Assert.Equal(expectedRegime, execution.GetProperty("regime").GetString());
        if (expectedReason is not null)
        {
            Assert.Equal(expectedReason, execution.GetProperty("reason").GetString());
        }

        Assert.Equal(requestedAtUtc, execution.GetProperty("requestedAt").GetDateTimeOffset().UtcDateTime);
        var t0 = execution.GetProperty("requestedAt").GetDateTimeOffset();
        Assert.Equal(t0.AddSeconds(3_600), execution.GetProperty("hardDeadlineAt").GetDateTimeOffset());
        if (expectedRegime == "Ordinary")
        {
            Assert.Equal(t0.AddSeconds(180), execution.GetProperty("ordinaryDeadlineAt").GetDateTimeOffset());          // deadline ordinaria dal t0 originale
            Assert.Equal(t0.AddSeconds(180), execution.GetProperty("effectiveDeadlineAt").GetDateTimeOffset());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, execution.GetProperty("ordinaryDeadlineAt").ValueKind);                  // Extended: nessuna deadline ordinaria
            Assert.Equal(t0.AddSeconds(3_600), execution.GetProperty("effectiveDeadlineAt").GetDateTimeOffset());
        }
        var hash = execution.GetProperty("classificationRecordHash").GetString();
        Assert.Matches("^[0-9a-f]{64}$", hash!);
        Assert.Equal("document-regime-v2", execution.GetProperty("classificationVersion").GetString());

        var elapsed = execution.GetProperty("elapsedMs").GetInt64();
        Assert.True(elapsed >= (waited ? 1_000 : 0));                                         // include l'attesa reale in coda
        long Part(string name) => execution.GetProperty(name).ValueKind == JsonValueKind.Null ? 0 : execution.GetProperty(name).GetInt64();
        Assert.NotEqual(JsonValueKind.Null, execution.GetProperty("preflightMs").ValueKind);   // il preflight e davvero avvenuto nel dispatcher
        Assert.NotEqual(JsonValueKind.Null, execution.GetProperty("queueMs").ValueKind);
        Assert.True(execution.GetProperty("queueMs").GetInt64() >= (waited ? 1_000 : 0));     // t0 -> inizio preflight: l'attesa Pending non e persa
        Assert.True(Part("queueMs") + Part("preflightMs") + Part("backoffMs") + Part("processingMs") <= elapsed + 50);
        Assert.Equal(JsonValueKind.Null, execution.GetProperty("recoveryMs").ValueKind);       // nessun produttore (T102): unavailable, mai zero

        // context-usage: la chiamata finale e quella di questo root, con lo stesso execution e una composizione che attribuisce le fonti documentali.
        var last = usage.GetProperty("last");
        Assert.Equal(kind, last.GetProperty("kind").GetString());
        Assert.Equal(hash, last.GetProperty("execution").GetProperty("classificationRecordHash").GetString());
        Assert.Equal(expectedRegime, last.GetProperty("execution").GetProperty("regime").GetString());
        var dbPrompt = await Scalar<int>(kind == "chat" ? "SELECT \"PromptTokens\" FROM \"InferenceCalls\" WHERE \"ChatTurnId\" = @i AND \"IsFinal\"" : "SELECT \"PromptTokens\" FROM \"InferenceCalls\" WHERE \"ProcessingRunId\" = @i AND \"IsFinal\"", ("i", root));
        Assert.True(dbPrompt > 0);
        var items = usage.GetProperty("composition").GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("key").GetString()!, i => i.GetProperty("tokens"));
        if (documentSources)
        {
            Assert.True(items["document_sources"].GetProperty("value").GetInt32() > 0);
        }
        else
        {
            Assert.DoesNotContain("document_sources", items.Keys);                            // la bozza non attribuisce fonti (limite strutturale dichiarato)
        }
    }

    // ================================================================ supporto: manifest e parser finto (HTTP, nessun parser reale)

    private static string Norm(string text) => text.Replace("\r\n", "\n");

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class FakeParser : HttpMessageHandler
    {
        private const string VersionJson = """{"schema":"document-parse-1","configHash":"cfg-hash-1","formats":["pdf","docx","md","txt"]}""";
        private static readonly byte[] MixedSha = SHA256.HashData(RealParser.Fixture("pdf-mixed.pdf"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/version", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, VersionJson);
            }

            var form = (MultipartFormDataContent)request.Content!;
            var fields = new Dictionary<string, string>();
            byte[] file = [];
            foreach (var part in form)
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (name == "file")
                {
                    file = await part.ReadAsByteArrayAsync(cancellationToken);
                }
                else
                {
                    fields[name] = await part.ReadAsStringAsync(cancellationToken);
                }
            }

            var format = fields["declaredFormat"];
            // Input che un parser reale rifiuta (422): contenitore non valido o PDF cifrato.
            var invalid = format switch
            {
                "docx" => file.Length < 2 || file[0] != (byte)'P' || file[1] != (byte)'K',
                "pdf" => file.Length < 4 || Encoding.Latin1.GetString(file, 0, 4) != "%PDF" || Encoding.Latin1.GetString(file).Contains("/Encrypt", StringComparison.Ordinal) || !Encoding.Latin1.GetString(file).Contains("%%EOF", StringComparison.Ordinal),
                _ => false,
            };
            if (invalid)
            {
                return Json(HttpStatusCode.UnprocessableEntity, """{"code":"corrupt_input"}""");
            }

            var incomplete = format == "pdf" && SHA256.HashData(file).AsSpan().SequenceEqual(MixedSha);
            return Json(HttpStatusCode.OK, Manifest(format, fields["originalSha256"], file, fields["configHash"], incomplete));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static string[] Paragraphs(string format, byte[] file) => format switch
        {
            "pdf" => ["Capitolato tecnico", "La penale giornaliera e di 500 euro per ogni giorno di ritardo nella consegna.", "Il collaudo avviene entro 30 giorni dalla consegna del software."],
            "docx" => ["Requisiti", "Il sistema deve esportare i dati dei clienti in formato CSV ogni giorno.", "L'accesso al sistema richiede sempre l'autenticazione dell'operatore."],
            _ => [.. Norm(Encoding.UTF8.GetString(file)).Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(p => p.TrimEnd('\n'))],
        };

        private static string Manifest(string format, string originalSha, byte[] file, string configHash, bool incomplete)
        {
            var paragraphs = Paragraphs(format, file);
            if (incomplete)
            {
                paragraphs = paragraphs[..2];                                                     // la pagina 2 e solo un'immagine: nessun testo estratto
            }

            var textual = format is "md" or "txt";
            var line = 1;
            var blocks = new JsonArray();
            var ranges = new JsonArray();
            for (var i = 0; i < paragraphs.Length; i++)
            {
                var text = paragraphs[i];
                var lineCount = text.Count(c => c == '\n') + 1;
                var locator = textual
                    ? new JsonObject { ["lineStart"] = line, ["lineEnd"] = line + lineCount - 1 }
                    : format == "pdf" ? new JsonObject { ["page"] = i < 2 ? 1 : 2 } : new JsonObject { ["path"] = $"body/p[{i}]" };
                blocks.Add(new JsonObject
                {
                    ["ref"] = $"b{i}", ["order"] = i, ["parentRef"] = null, ["type"] = i == 0 ? "heading" : "paragraph", ["text"] = text, ["headingPath"] = new JsonArray(), ["locator"] = locator,
                });
                ranges.Add(new JsonObject
                {
                    ["ranges"] = new JsonArray(new JsonObject { ["rangeKey"] = $"r{i}", ["blockRef"] = $"b{i}", ["start"] = 0, ["end"] = text.Length, ["ownership"] = "Primary", ["sourceTextSha256"] = Sha(text) }),
                });
                line += lineCount + 1;
            }

            JsonArray Refs(IEnumerable<int> indexes) => new([.. indexes.Select(i => (JsonNode)JsonValue.Create($"b{i}")!)]);
            var inventory = format == "pdf"
                ? new JsonArray(
                    new JsonObject { ["kind"] = "page", ["page"] = 1, ["state"] = "represented", ["blockRefs"] = Refs(Enumerable.Range(0, Math.Min(2, paragraphs.Length))), ["issueRefs"] = new JsonArray() },
                    incomplete
                        ? new JsonObject { ["kind"] = "page", ["page"] = 2, ["state"] = "unprocessed", ["blockRefs"] = new JsonArray(), ["issueRefs"] = new JsonArray("i1") }
                        : new JsonObject { ["kind"] = "page", ["page"] = 2, ["state"] = "represented", ["blockRefs"] = Refs([2]), ["issueRefs"] = new JsonArray() })
                : new JsonArray(new JsonObject { ["kind"] = "paragraph", ["state"] = "represented", ["blockRefs"] = Refs(Enumerable.Range(0, paragraphs.Length)), ["issueRefs"] = new JsonArray() });
            var manifest = new JsonObject
            {
                ["schemaVersion"] = "document-parse-1", ["originalSha256"] = originalSha, ["parserVersion"] = "1.0.0", ["dependencyLockHash"] = "lock", ["modelArtifactsHash"] = "model",
                ["configHash"] = configHash, ["status"] = incomplete ? "Incomplete" : "Complete", ["originFormat"] = format, ["originalByteLength"] = file.Length,
                ["pageCount"] = format == "pdf" ? 2 : null, ["offsetConvention"] = "unicode-codepoint",
                ["document"] = new JsonObject { ["serialization"] = "text-1", ["content"] = string.Join("\n\n", paragraphs) },
                ["blocks"] = blocks, ["inventory"] = inventory, ["chunks"] = ranges, ["issues"] = new JsonArray(),
            };
            if (incomplete)
            {
                manifest["issues"] = new JsonArray(new JsonObject { ["issueRef"] = "i1", ["code"] = "unprocessed_image_page", ["severity"] = "blocking", ["page"] = 2, ["blockRef"] = null, ["reason"] = "Pagina 2 solo immagine, OCR disabilitato", ["affectsCompleteness"] = true });
            }

            return manifest.ToJsonString();
        }
    }

    // ================================================================ supporto: modello finto (HTTP, llama.cpp simulato)

    internal enum Graph { Extract, DocChat, Chat001, DocAnalysis, Analysis001, DocDraft, Draft001, Other }

    private sealed record Req(Graph Graph, string System, string Text);

    private static Graph Classify(string system)
    {
        var s = Norm(system);
        return s.Contains(Norm(DocumentEvidenceAgent.Instructions)) ? Graph.Extract
            : s.Contains(Norm(DocumentChatWorkflow.Instructions)) ? Graph.DocChat
            : s.Contains(Norm(DocumentAnalysisWorkflow.Instructions)) ? Graph.DocAnalysis
            : s.Contains(Norm(DocumentSourceDraftWorkflow.Instructions)) ? Graph.DocDraft
            : s.Contains(Norm(ConversationalAgent.ConsolidationInstructions)) ? Graph.Analysis001
            : s.Contains(Norm(DocumentAgent.Instructions)) ? Graph.Draft001
            : s.Contains(Norm(ConversationalAgent.Instructions)) ? Graph.Chat001
            : Graph.Other;
    }

    private static string Mutate(string reply, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(reply)!;
        change(node);
        return node.ToJsonString();
    }

    private static readonly Regex Range = new(@"<<<INTERVALLO rangeKey=(\S+) ruolo=(Primary|Context)>>>\n(.*?)\n<<<FINE rangeKey=\1>>>", RegexOptions.Singleline | RegexOptions.Compiled);

    private string Reply(Req request)
    {
        var reply = request.Graph switch
        {
            Graph.Extract => ExtractionReply(request.Text),
            Graph.DocChat => ChatReply(request.Text),
            Graph.Chat001 => "Quali utenti userebbero il portale?",
            Graph.DocAnalysis => DocumentAnalysisReply(request.Text),
            Graph.Analysis001 => LegacyAnalysisReply(request.Text),
            Graph.DocDraft => DocumentDraftReply(request.Text),
            Graph.Draft001 => $"# Documento\n\n- Obiettivo [fonte:{Regex.Match(request.Text, "\"sources\"\\s*:\\s*\\[\\s*\\{\\s*\"id\"\\s*:\\s*\"([0-9a-f-]{36})\"").Groups[1].Value}]",
            _ => throw new InvalidOperationException("Richiesta al modello di un grafo non riconosciuto."),
        };
        return _model.Rewrite?.Invoke(request.Graph, request.Text, reply) ?? reply;
    }

    /// <summary>Una evidenza per ogni range Primary con almeno quattro parole (citazione = tutto il range, enunciato = il testo); altrimenti zero evidenze motivate.</summary>
    private static string ExtractionReply(string text)
    {
        var ranges = Range.Matches(text).Select(m => (Key: m.Groups[1].Value, Primary: m.Groups[2].Value == "Primary", Text: m.Groups[3].Value))
            .Where(r => r.Primary && r.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 4).ToList();
        var evidences = ranges.Select((r, i) => new
        {
            key = $"e{i + 1}",
            sources = new[] { new { kind = "DocumentPassage", rangeKey = r.Key, quote = r.Text, occurrence = 1, role = "Primary", citationGroupKey = $"g{i + 1}", part = 0 } },
            kind = "Requirement", kindRationale = (string?)null, statement = r.Text, subject = (string?)null, action = (string?)null, @object = (string?)null,
            scope = (string?)null, purpose = (string?)null, instrument = (string?)null, channel = (string?)null, recipient = (string?)null,
            qualifiers = Array.Empty<object>(), polarity = "Affirmed", certainty = "Declared", relations = Array.Empty<object>(),
        }).ToList();
        return JsonSerializer.Serialize(new { schemaVersion = "evidence-1.1.0", evidences, noEvidenceReason = evidences.Count == 0 ? "Solo titoli o frasi troppo brevi per un requisito." : null });
    }

    /// <summary>Cita le fonti il cui testo risponde alla domanda (contiene una parola chiave del messaggio PM); se nessuna, la prima fonte disponibile.</summary>
    private static string ChatReply(string text)
    {
        var blocks = Regex.Matches(text, @"<<<FONTE fonte:([0-9a-f-]{36}) ruolo=\w+>>>\n(.*?)\n<<<FINE FONTE>>>", RegexOptions.Singleline)
            .Select(m => (Id: m.Groups[1].Value, Quote: m.Groups[2].Value)).ToList();
        var question = text[(text.LastIndexOf("Messaggio del project manager:", StringComparison.Ordinal) + 1)..];
        var keywords = Regex.Matches(question.ToLowerInvariant(), @"\p{L}{6,}").Select(m => m.Value).ToList();
        var cited = blocks.Where(b => keywords.Any(k => b.Quote.Contains(k, StringComparison.OrdinalIgnoreCase))).Select(b => b.Id).Distinct().Take(4).ToList();
        if (cited.Count == 0)
        {
            cited = [.. blocks.Select(b => b.Id).Take(1)];
        }

        return JsonSerializer.Serialize(new { mode = "documented", answer = "Secondo i documenti: " + string.Join(' ', cited.Select(s => $"[fonte:{s}]")) + "." });
    }

    private static string LegacyAnalysisReply(string request)
    {
        string Match(string pattern) => Regex.Match(request, pattern).Groups[1].Value;
        var message = Regex.Match(request, @"\[messaggio id=([0-9a-f-]{36}) sequenza=(\d+)\]");
        var source = Guid.NewGuid().ToString();
        return new JsonObject
        {
            ["schemaVersion"] = "1.0.0", ["analysisId"] = Match(@"analysisId: ([0-9a-f-]{36})"), ["projectId"] = Match(@"projectId: ([0-9a-f-]{36})"),
            ["sourceCutoffSequence"] = long.Parse(Match(@"sourceCutoffSequence: (\d+)")), ["validationState"] = "Unvalidated",
            ["sources"] = new JsonArray(new JsonObject { ["id"] = source, ["kind"] = "Message", ["messageId"] = message.Groups[1].Value, ["messageRole"] = "user", ["revisionId"] = null }),
            ["goals"] = new JsonArray(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["text"] = "Realizzare un portale clienti", ["classification"] = "Declared", ["evidenceRefs"] = new JsonArray(JsonValue.Create(source)) }),
            ["contextAndScope"] = new JsonObject { ["overview"] = new JsonArray(), ["included"] = new JsonArray(), ["excluded"] = new JsonArray() },
            ["requirements"] = new JsonArray(), ["operationalFlows"] = new JsonArray(), ["constraints"] = new JsonArray(), ["dependencies"] = new JsonArray(),
            ["assumptions"] = new JsonArray(), ["openQuestions"] = new JsonArray(), ["conflicts"] = new JsonArray(),
        }.ToJsonString();
    }

    private string DocumentAnalysisReply(string request)
    {
        var run = Guid.Parse(Regex.Match(request, "analysisId: ([0-9a-f-]{36})").Groups[1].Value);
        using var scope = App.Services.CreateScope();
        var input = DocumentAnalysisSources.LoadAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), run, Ct).GetAwaiter().GetResult();
        ReconcileResult Result(ReconcileView view) => new()
        {
            BatchId = input.BatchKey, InputHash = input.InputHash, View = view, Level = 0,
            VisitedEntryIds = [.. input.Snapshot.Evidence.Select(e => ReconciliationIds.EntryId(input.Snapshot.SourceHash, e.EvidenceId, e.ContentHash)).Order(StringComparer.Ordinal)],
            VisitedNodeIds = [], Candidates = [], Dispositions = [], ItemProposals = [], Patches = [], Descriptor = null, NoFindingsReason = "nessun collegamento oltre quelli gia noti",
        };

        static Guid ItemId(Guid evidence) => new(SHA256.HashData(evidence.ToByteArray())[..16]);
        static DocumentAnalysisSource Source(PersistedSource s) => s.Kind == EvidenceSourceKind.Message
            ? new()
            {
                Id = s.SourceId, Kind = DocumentAnalysisSourceKind.Message, MessageId = s.MessageId, MessageRole = AnalysisMessageRole.User, RevisionId = null, DocumentId = null,
                DocumentVersionId = null, PreparationId = null, ArtifactId = null, BlockId = null, Start = null, End = null, Quote = null,
            }
            : new()
            {
                Id = s.SourceId, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = s.DocumentId,
                DocumentVersionId = s.DocumentVersionId, PreparationId = s.PreparationId, ArtifactId = s.ArtifactId, BlockId = s.BlockId, Start = s.Start, End = s.End, Quote = s.Quote,
            };

        var output = new SingleCallOutput
        {
            Results = [Result(ReconcileView.A), Result(ReconcileView.B), Result(ReconcileView.Cross)],
            Analysis = new DocumentAnalysisContent
            {
                SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = input.AnalysisId, ProjectId = input.ProjectId, SourceCutoffSequence = input.Cutoff,
                ValidationState = AnalysisContentState.Unvalidated, Sources = [.. input.Evidence.SelectMany(e => e.Sources.Select(Source))], Goals = [],
                ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
                Requirements =
                [
                    .. input.Evidence.Select(e => new DocumentAnalysisItem
                    {
                        Id = ItemId(e.EvidenceId), Text = e.Statement,
                        Classification = e.Origin == EvidenceOriginKind.Message ? DocumentAnalysisClassification.Declared : DocumentAnalysisClassification.Documented,
                        EvidenceRefs = [.. e.Sources.Select(s => s.SourceId)],
                    }),
                ],
                OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = [],
            },
            EvidenceMap = [.. input.Evidence.Select(e => new EvidenceMapEntry { ItemId = ItemId(e.EvidenceId), EvidenceIds = [e.EvidenceId] })],
        };
        return ReconciliationCodec.Encode(output);
    }

    /// <summary>Un unico blocco con tutti gli item della revisione confermata e le loro fonti, letti dal JSON che il server ha inviato nel messaggio.</summary>
    private static string DocumentDraftReply(string request)
    {
        var start = request.IndexOf("completo:\n", StringComparison.Ordinal) + "completo:\n".Length;
        var end = request.LastIndexOf("\nRispondi ora", StringComparison.Ordinal);
        using var content = JsonDocument.Parse(request[start..end]);
        var root = content.RootElement;
        var items = new List<JsonElement>();
        foreach (var name in new[] { "goals", "requirements", "operationalFlows", "constraints", "dependencies", "assumptions", "openQuestions" })
        {
            items.AddRange(root.GetProperty(name).EnumerateArray());
        }

        foreach (var section in new[] { "overview", "included", "excluded" })
        {
            items.AddRange(root.GetProperty("contextAndScope").GetProperty(section).EnumerateArray());
        }

        foreach (var conflict in root.GetProperty("conflicts").EnumerateArray())
        {
            items.AddRange(conflict.GetProperty("alternatives").EnumerateArray());
        }

        var ids = items.Select(i => i.GetProperty("id").GetString()!).ToList();
        var sources = items.SelectMany(i => i.GetProperty("evidenceRefs").EnumerateArray().Select(r => r.GetString()!)).Distinct().ToList();
        var markdown = "- Requisiti confermati " + string.Join(' ', sources.Select(s => $"[fonte:{s}]"));
        return new JsonObject
        {
            ["sectionKey"] = "documento",
            ["blocks"] = new JsonArray(new JsonObject
            {
                ["itemIds"] = new JsonArray([.. ids.Select(i => (JsonNode)JsonValue.Create(i)!)]),
                ["markdown"] = markdown,
                ["sourceIds"] = new JsonArray([.. sources.Select(i => (JsonNode)JsonValue.Create(i)!)]),
            }),
        }.ToJsonString();
    }

    private sealed class FlowModel(Func<Req, string> reply) : HttpMessageHandler
    {
        private readonly List<Req> _requests = [];

        /// <summary>Alterazione della risposta valida di un grafo (per le prove degli output non validi).</summary>
        public Func<Graph, string, string, string>? Rewrite { get; set; }

        public IReadOnlyList<Graph> Graphs
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests.Select(r => r.Graph)];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/models":
                    return Json("""{"object":"list","data":[{"id":"test-model","object":"model","status":{"value":"loaded","args":["llama-server"]}}]}""");
                case "/props":
                    return Json("{\"default_generation_settings\":{\"n_ctx\":32768,\"params\":{\"n_predict\":-1}},\"total_slots\":1}");
                case "/tokenize":
                {
                    using var doc = JsonDocument.Parse(body);
                    var count = Words(doc.RootElement.GetProperty("content").GetString()!);
                    return Json("{\"tokens\":[" + string.Join(',', Enumerable.Range(0, count)) + "]}");
                }

                case "/apply-template":
                {
                    using var doc = JsonDocument.Parse(body);
                    var prompt = string.Join('\n', doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => $"[{m.GetProperty("role").GetString()}] {TextOf(m.GetProperty("content"))}"));
                    return Json(JsonSerializer.Serialize(new { prompt }));
                }

                case "/v1/chat/completions":
                {
                    using var doc = JsonDocument.Parse(body);
                    var messages = doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => (Role: m.GetProperty("role").GetString()!, Text: TextOf(m.GetProperty("content")))).ToList();
                    var system = string.Join("\n", messages.Where(m => m.Role is "system" or "developer").Select(m => m.Text));
                    var record = new Req(Classify(system), system, string.Join("\n", messages.Where(m => m.Role is not ("system" or "developer")).Select(m => m.Text)));
                    lock (_requests)
                    {
                        _requests.Add(record);
                    }

                    var content = reply(record);
                    var stream = doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
                    return stream ? Sse(content) : Json(JsonSerializer.Serialize(new
                    {
                        id = "x", @object = "chat.completion", created = 1, model = "test-model",
                        choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
                        usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
                    }));
                }

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Sse(string content)
        {
            string Chunk(object delta, string? finish, object? usage = null) =>
                "data: " + JsonSerializer.Serialize(new { id = "x", @object = "chat.completion.chunk", created = 1, model = "test-model", choices = new[] { new { index = 0, delta, finish_reason = finish } }, usage }) + "\n\n";
            var text = Chunk(new { role = "assistant", content }, null) + Chunk(new { }, "stop", new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }) + "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };
        }

        private static string TextOf(JsonElement content) => content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Concat(content.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));

        private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
