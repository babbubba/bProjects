using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T056 (PG) — contratto della selezione documentale fissata al POST (contracts/http-api.md §Selezione persistente e snapshot; data-model
/// SourceSnapshot/ChatTurn/ProcessingRun): selezione congelata sotto lock alla richiesta, readiness di Analizza e chat (409
/// sources_not_ready, 422 no_source_input, messaggio persistito con fonti non pronte), solo documenti pronti con cutoff 0, replay prima del
/// ricalcolo delle fonti, cutoff PM separato dallo snapshot documentale, nessuna fonte successiva o esclusa nello snapshot, snapshot di
/// generazione ereditato dalla revisione (mai quello corrente) e revisioni/bozze confermate immutate. Le righe Ready sono scritte con le
/// stesse entità dell'aggregatore (T051): nessun parser, nessun modello, nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentSourceSnapshotTests : ArchiveTestBase
{
    // T141 (AI §13.1/§13.6): le NUOVE richieste di Analizza congelano la v5 (single-call v5 + gerarchia v4 + reconcileGapPolicy); v2, v3 e v4 restano storiche (run già richiesti).
    private const string AnalysisDocumentsCurrent = "project-analysis-documents-v6";
    // T127: le NUOVE richieste di Genera congelano la v3 con alias; la v2 resta storica (run già richiesti).
    private const string DocumentDocumentsV3 = "project-document-documents-v3";

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;
        options.ProjectQuotaBytes = 50_000;
    }

    // ================================================================ supporto

    private static object Item(Guid document, bool included = true, string mode = "LatestReady", Guid? versionId = null) =>
        new { documentId = document, included, mode, versionId };

    private async Task<HttpResponseMessage> Put(HttpClient client, Guid project, params object[] items)
    {
        var revision = (await Json(await client.GetAsync($"/api/projects/{project}/source-selection", Ct))).GetProperty("revision").GetInt64();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{project}/source-selection") { Content = JsonContent.Create(new { items }) };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        return await client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> Analyze(HttpClient client, Guid project, Guid? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> AnalyzeAccepted(HttpClient client, Guid project, Guid? key = null)
    {
        var response = await Analyze(client, project, key);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await Json(response);
    }

    private static Task<HttpResponseMessage> Message(HttpClient client, Guid project, string text = "Vorrei un portale per i clienti") =>
        client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);

    private sealed record Doc(Guid Document, Guid Version);

    private async Task<Doc> NewDocument(HttpClient client, Guid project, string name, bool ready, Guid? documentId = null)
    {
        var created = await Json(await Upload(client, project, Text(name), name + ".txt", documentId: documentId, displayName: documentId is null ? name : null));
        var doc = new Doc(created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid());
        if (ready)
        {
            await MakeReady(doc);
        }

        return doc;
    }

    /// <summary>Bundle corrente completo: job, versione Evidence, stato corrente, preparazione Ready e cache LatestReady (come fa l'aggregatore T051).</summary>
    private async Task<(Guid Preparation, Guid Evidence)> MakeReady(Doc doc)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == doc.Version, Ct);
        var now = DateTimeOffset.UtcNow;
        var job = Guid.NewGuid();
        var evidence = Guid.NewGuid();
        db.EvidenceExtractionJobs.Add(new EvidenceExtractionJob
        {
            Id = job, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Reason = ExtractionJobReason.Initial,
            Status = ExtractionJobStatus.Queued, RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
        });
        db.EvidenceExtractionVersions.Add(new EvidenceExtractionVersion
        {
            Id = evidence, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, ManifestHash = new string('1', 64),
            Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = 0, NoEvidenceReason = "Documento di prova senza requisiti.", CreatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = doc.Version, ProjectId = preparation.ProjectId, CurrentPreparationId = preparation.Id, CurrentEvidenceVersionId = evidence, Revision = 1, UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        preparation.Status = PreparationStatus.Ready;
        preparation.CurrentEvidenceVersionId = evidence;
        await db.SaveChangesAsync(Ct);
        await db.ProjectDocuments.Where(d => d.Id == doc.Document).ExecuteUpdateAsync(x => x.SetProperty(d => d.LatestReadyVersionId, doc.Version), Ct);
        return (preparation.Id, evidence);
    }

    private async Task<Guid> SnapshotOf(string table, Guid id) =>
        await Scalar<Guid>($"SELECT \"SourceSnapshotId\" FROM \"{table}\" WHERE \"Id\" = @i", ("i", id));

    // ================================================================ readiness di Analizza

    [Fact]
    public async Task Analyze_with_only_ready_documents_and_no_pm_message_freezes_the_selection_with_cutoff_zero_and_the_documents_v3_definition()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewDocument(client, project, "regole", ready: true);
        var (preparation, evidence) = await GetReady(doc);

        var response = await Analyze(client, project);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = await Json(response);
        Assert.Equal(0, run.GetProperty("sourceCutoffSequence").GetInt64());
        var runId = run.GetProperty("id").GetGuid();
        var snapshot = await SnapshotOf("ProcessingRuns", runId);
        Assert.Equal(1, await Count(
            "SELECT count(*) FROM \"SourceSnapshotItems\" WHERE \"SnapshotId\" = @s AND \"Availability\" = 'Ready' AND \"Included\" AND \"DocumentVersionId\" = @v AND \"PreparationId\" = @p AND \"EvidenceVersionId\" = @e",
            ("s", snapshot), ("v", doc.Version), ("p", preparation), ("e", evidence)));
        // Persistita la definizione documentale v6 (D1, AI §13.7) con TUTTE le chiavi di versione: single-call (prompt document-analysis-v6, schema alias-2, algoritmo single-v3, validazione v3, policy di stima dell'output),
        // gerarchia v4 invariata (reconcile-v3, reconcile-alias-1, global-reconcile-v1, catalogo) e le sei chiavi dello stadio Gap al posto della policy Gap temporanea; nessun run 001 convertito, nessuna chiave in più o in meno.
        Assert.Equal(AnalysisDocumentsCurrent, await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", runId)));
        var prompts = JsonDocument.Parse(await Scalar<string>("SELECT \"PromptVersions\"::text FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", runId))).RootElement;
        var frozen = prompts.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={p.Value.GetString()}").ToList();
        Assert.Equal(
        [
            "algorithm=analysis-single-v3", "aliasCatalog=alias-catalog-1", "analysis=document-analysis-v6", "analysisAiSchema=document-analysis-alias-2", "analysisMessageFormat=message-format-2", "analysisOutputPolicy=single-call-output-estimate-1", "analysisSchema=1.1.0",
            "canonIds=canon-ids-1", "gapAiSchema=gap-stage-alias-1", "gapEstimate=gap-stage-estimate-1", "gapMessageFormat=gap-stage-message-1", "gapPolicy=gap-stage-ledger-1", "gapPrompt=document-gap-v1", "gapStage=gap-stage-1",
            "reconcile=document-reconcile-v3", "reconcileAiSchema=reconcile-alias-1", "reconcileAlgorithm=global-reconcile-v1",
            "reconcileCatalog=reconcile-alias-catalog-1", "validation=document-analysis-validation-v3",
        ], frozen);
    }

    private async Task<(Guid Preparation, Guid Evidence)> GetReady(Doc doc) => (
        await Scalar<Guid>("SELECT \"Id\" FROM \"DocumentPreparations\" WHERE \"DocumentVersionId\" = @v", ("v", doc.Version)),
        await Scalar<Guid>("SELECT \"CurrentEvidenceVersionId\" FROM \"DocumentPreparations\" WHERE \"DocumentVersionId\" = @v", ("v", doc.Version)));

    [Fact]
    public async Task Analyze_with_ready_documents_and_pm_messages_keeps_the_pm_cutoff_separate_from_the_documents_snapshot()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", ready: true);
        await Message(client, project, "primo");
        await Message(client, project, "secondo");

        var run = await AnalyzeAccepted(client, project);

        Assert.Equal(2, run.GetProperty("sourceCutoffSequence").GetInt64());   // solo il dialogo PM definisce il cutoff
        Assert.Equal(AnalysisDocumentsCurrent, await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run.GetProperty("id").GetGuid())));
        // Il gate Evidence PM resta: un lavoro di estrazione per ciascun messaggio PM entro il cutoff, nessuno per il documento.
        Assert.Equal(2, await Count("SELECT count(*) FROM \"EvidenceExtractionJobs\" WHERE \"OriginKind\" = 'Message'"));
    }

    [Fact]
    public async Task A_project_without_ready_sources_still_records_the_snapshot_but_the_run_stays_on_the_001_definition()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewDocument(client, project, "escluso", ready: true);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, project, Item(doc.Document, included: false))).StatusCode);
        await Message(client, project);

        var run = await AnalyzeAccepted(client, project);

        var runId = run.GetProperty("id").GetGuid();
        Assert.Equal(ProcessingRun.AnalysisWorkflowVersion, await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", runId)));
        var snapshot = await SnapshotOf("ProcessingRuns", runId);
        // L'esclusione è registrata anche se il documento ha un bundle Ready: nessuna evidenza fuori selezione nello snapshot.
        Assert.Equal(1, await Count("SELECT count(*) FROM \"SourceSnapshotItems\" WHERE \"SnapshotId\" = @s AND \"Availability\" = 'Excluded' AND \"PreparationId\" IS NULL AND \"EvidenceVersionId\" IS NULL AND \"DocumentVersionId\" IS NULL", ("s", snapshot)));
    }

    [Fact]
    public async Task Analyze_with_no_pm_message_and_no_ready_document_is_422_no_source_input_without_run_or_snapshot()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewDocument(client, project, "escluso", ready: true);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, project, Item(doc.Document, included: false))).StatusCode);

        var response = await Analyze(client, project);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("no_source_input", (await Json(response)).GetProperty("code").GetString());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"SourceSnapshots\""));
    }

    [Fact]
    public async Task Analyze_blocked_by_one_unready_source_lists_only_that_source_even_with_a_ready_one_and_creates_nothing()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "pronto", ready: true);
        var pending = await NewDocument(client, project, "attesa", ready: false);
        await Message(client, project);
        var snapshotsBefore = await Count("SELECT count(*) FROM \"SourceSnapshots\"");   // quello del turno di chat, non di Analizza

        var response = await Analyze(client, project);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await Json(response);
        Assert.Equal("sources_not_ready", problem.GetProperty("code").GetString());
        var blocking = Assert.Single(problem.GetProperty("blockingSources").EnumerateArray());
        Assert.Equal(pending.Document, blocking.GetProperty("documentId").GetGuid());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\""));
        Assert.Equal(snapshotsBefore, await Count("SELECT count(*) FROM \"SourceSnapshots\""));
    }

    [Fact]
    public async Task Replay_returns_the_original_run_before_recomputing_sources_even_if_a_document_became_blocking()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "pronto", ready: true);
        var key = Guid.NewGuid();
        var first = await AnalyzeAccepted(client, project, key);
        var runId = first.GetProperty("id").GetGuid();
        var snapshot = await SnapshotOf("ProcessingRuns", runId);

        await NewDocument(client, project, "arrivato-dopo", ready: false);   // ora Analizza con una chiave nuova sarebbe 409
        var replay = await Analyze(client, project, key);

        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(runId, (await Json(replay)).GetProperty("id").GetGuid());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"SourceSnapshots\""));
        Assert.Equal(snapshot, await SnapshotOf("ProcessingRuns", runId));
    }

    [Fact]
    public async Task Documents_uploaded_promoted_or_excluded_after_the_post_never_enter_the_frozen_snapshot()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var first = await NewDocument(client, project, "regole", ready: true);
        var runId = (await AnalyzeAccepted(client, project)).GetProperty("id").GetGuid();
        var snapshot = await SnapshotOf("ProcessingRuns", runId);
        var hash = await Scalar<string>("SELECT \"Hash\" FROM \"SourceSnapshots\" WHERE \"Id\" = @s", ("s", snapshot));
        const string rows = "SELECT count(*) FROM \"SourceSnapshotItems\" WHERE \"SnapshotId\" = @s";
        var before = await Count(rows, ("s", snapshot));

        var later = await NewDocument(client, project, "nuovo", ready: true);                                     // documento successivo, subito Ready
        var v2 = await NewDocument(client, project, "regole", ready: true, documentId: first.Document);          // nuova versione promossa
        Assert.Equal(HttpStatusCode.OK, (await Put(client, project, Item(later.Document, included: false))).StatusCode);   // esclusione successiva

        Assert.Equal(before, await Count(rows, ("s", snapshot)));
        Assert.Equal(1, await Count($"{rows} AND \"DocumentVersionId\" = @v AND \"Availability\" = 'Ready'", ("s", snapshot), ("v", first.Version)));
        Assert.Equal(0, await Count($"{rows} AND (\"DocumentId\" = @d OR \"DocumentVersionId\" = @v2)", ("s", snapshot), ("d", later.Document), ("v2", v2.Version)));
        Assert.Equal(hash, await Scalar<string>("SELECT \"Hash\" FROM \"SourceSnapshots\" WHERE \"Id\" = @s", ("s", snapshot)));
        Assert.Equal(snapshot, await SnapshotOf("ProcessingRuns", runId));
    }

    // ================================================================ chat

    [Fact]
    public async Task A_chat_message_freezes_ready_and_unready_sources_without_waiting_and_stays_saved()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var ready = await NewDocument(client, project, "pronto", ready: true);
        var pending = await NewDocument(client, project, "attesa", ready: false);

        var posted = await Message(client, project);

        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);   // salvataggio immediato, nessun 409
        var turn = (await Json(posted)).GetProperty("chatTurnId").GetGuid();
        var snapshot = await SnapshotOf("ChatTurns", turn);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"SourceSnapshotItems\" WHERE \"SnapshotId\" = @s AND \"Availability\" = 'Ready' AND \"DocumentId\" = @d AND \"EvidenceVersionId\" IS NOT NULL", ("s", snapshot), ("d", ready.Document)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"SourceSnapshotItems\" WHERE \"SnapshotId\" = @s AND \"Availability\" = 'Unavailable' AND \"DocumentId\" = @d AND \"EvidenceVersionId\" IS NULL", ("s", snapshot), ("d", pending.Document)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'User'"));

        // Il documento in attesa diventa pronto dopo: il turno non allarga mai lo snapshot (retry compreso).
        await MakeReady(pending);
        Assert.Equal(snapshot, await SnapshotOf("ChatTurns", turn));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"SourceSnapshotItems\" WHERE \"SnapshotId\" = @s AND \"Availability\" = 'Unavailable'", ("s", snapshot)));
    }

    // ================================================================ generazione: snapshot ereditato

    private async Task<(Guid Analysis, Guid Revision)> ConfirmedAnalysis(Guid project, Guid runId)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ProcessingRuns.SingleAsync(r => r.Id == runId, Ct);
        var now = DateTimeOffset.UtcNow;
        var revision = new AnalysisRevision
        {
            Id = Guid.NewGuid(), AnalysisId = run.Id, Version = 1, Origin = AnalysisRevisionOrigin.Ai, CreatedBy = run.RequestedBy, CreatedAt = now,
            SchemaVersion = AnalysisRevision.CurrentSchemaVersion, ContentJson = "{\"seed\":true}", ValidationState = AnalysisValidationState.PmConfirmed,
            ConfirmedBy = run.RequestedBy, ConfirmedAt = now,
        };
        db.AnalysisRevisions.Add(revision);
        run.Status = ProcessingRunStatus.Completed;
        run.ResultRevisionId = revision.Id;
        run.CompletedAt = now;
        await db.SaveChangesAsync(Ct);
        await db.Projects.Where(p => p.Id == project).ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentAnalysisId, run.Id), Ct);
        return (run.Id, revision.Id);
    }

    private static Task<HttpResponseMessage> Generate(HttpClient client, Guid project, Guid analysis, Guid revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/document-generations") { Content = JsonContent.Create(new { revisionId = revision }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Generation_inherits_the_revision_snapshot_never_the_current_selection_and_leaves_confirmed_revisions_untouched()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var original = await NewDocument(client, project, "regole", ready: true);
        var analysisRun = (await AnalyzeAccepted(client, project)).GetProperty("id").GetGuid();
        var analysisSnapshot = await SnapshotOf("ProcessingRuns", analysisRun);
        var (analysis, revision) = await ConfirmedAnalysis(project, analysisRun);
        var revisionBefore = await Scalar<string>("SELECT md5(to_jsonb(r)::text) FROM \"AnalysisRevisions\" r WHERE \"Id\" = @i", ("i", revision));

        // La selezione corrente cambia dopo la conferma: esclusione dell'originale e nuovo documento pronto.
        Assert.Equal(HttpStatusCode.OK, (await Put(client, project, Item(original.Document, included: false))).StatusCode);
        await NewDocument(client, project, "nuovo", ready: true);

        var response = await Generate(client, project, analysis, revision);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var runId = (await Json(response)).GetProperty("id").GetGuid();
        Assert.Equal(analysisSnapshot, await SnapshotOf("ProcessingRuns", runId));   // ereditato, non ricalcolato
        Assert.Equal(1, await Count("SELECT count(*) FROM \"SourceSnapshots\""));      // nessuno snapshot corrente creato dalla generazione
        Assert.Equal(DocumentDocumentsV3, await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", runId)));
        Assert.Equal(revisionBefore, await Scalar<string>("SELECT md5(to_jsonb(r)::text) FROM \"AnalysisRevisions\" r WHERE \"Id\" = @i", ("i", revision)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task Generation_from_a_legacy_analysis_has_no_snapshot_and_keeps_the_001_document_definition()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await Message(client, project);
        var analysisRun = (await AnalyzeAccepted(client, project)).GetProperty("id").GetGuid();
        var (analysis, revision) = await ConfirmedAnalysis(project, analysisRun);
        await NewDocument(client, project, "dopo", ready: true);   // un documento successivo non trasforma una run 001

        var response = await Generate(client, project, analysis, revision);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var runId = (await Json(response)).GetProperty("id").GetGuid();
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Id\" = @r AND \"SourceSnapshotId\" IS NOT NULL", ("r", runId)));
        Assert.Equal(ProcessingRun.DocumentWorkflowVersion, await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", runId)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"SourceSnapshots\""));
    }

    // ================================================================ T097: consultazione storica e hasSourceChanges

    private async Task<JsonElement> GetJson(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    [Fact]
    public async Task Chat_and_run_views_expose_the_frozen_snapshot_and_report_source_changes_without_changing_the_manifest()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var ready = await NewDocument(client, project, "pronto", ready: true);
        var (preparation, evidence) = (await Scalar<Guid>("SELECT \"Id\" FROM \"DocumentPreparations\" WHERE \"DocumentVersionId\" = @v", ("v", ready.Version)),
            await Scalar<Guid>("SELECT \"CurrentEvidenceVersionId\" FROM \"EvidenceDocumentStates\" WHERE \"DocumentVersionId\" = @v", ("v", ready.Version)));
        var turn = (await Json(await Message(client, project))).GetProperty("chatTurnId").GetGuid();
        var run = (await AnalyzeAccepted(client, project)).GetProperty("id").GetGuid();
        var turnSnapshot = await SnapshotOf("ChatTurns", turn);
        var runSnapshot = await SnapshotOf("ProcessingRuns", run);

        var turnView = await GetJson(client, $"/api/projects/{project}/chat-turns/{turn}");
        var runView = await GetJson(client, $"/api/projects/{project}/processing-runs/{run}");
        Assert.Equal(turnSnapshot, turnView.GetProperty("sourceSnapshotId").GetGuid());
        Assert.False(turnView.GetProperty("hasSourceChanges").GetBoolean());
        Assert.Equal(runSnapshot, runView.GetProperty("sourceSnapshotId").GetGuid());
        Assert.False(runView.GetProperty("hasSourceChanges").GetBoolean());

        // Una v2 pronta dopo la richiesta cambia la risoluzione attuale, non lo snapshot: l'avviso è acceso, il manifest storico no.
        var second = await NewDocument(client, project, "pronto-v2", ready: true, documentId: ready.Document);
        Assert.Equal(second.Document, ready.Document);
        Assert.True((await GetJson(client, $"/api/projects/{project}/chat-turns/{turn}")).GetProperty("hasSourceChanges").GetBoolean());
        var changedRun = await GetJson(client, $"/api/projects/{project}/processing-runs/{run}");
        Assert.True(changedRun.GetProperty("hasSourceChanges").GetBoolean());
        Assert.Equal(runSnapshot, changedRun.GetProperty("sourceSnapshotId").GetGuid());

        var manifest = await GetJson(client, $"/api/projects/{project}/source-snapshots/{runSnapshot}");
        Assert.Equal(runSnapshot, manifest.GetProperty("id").GetGuid());
        var item = Assert.Single(manifest.GetProperty("items").EnumerateArray());
        Assert.Equal(ready.Document, item.GetProperty("documentId").GetGuid());
        Assert.Equal(ready.Version, item.GetProperty("documentVersionId").GetGuid());
        Assert.Equal(1, item.GetProperty("version").GetInt32());
        Assert.Equal(preparation, item.GetProperty("preparationId").GetGuid());
        Assert.Equal(evidence, item.GetProperty("evidenceVersionId").GetGuid());
        Assert.Equal("Ready", item.GetProperty("availability").GetString());

        // Solo nel progetto: id ignoto o di un altro progetto è 404.
        var other = await NewProject(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{other}/source-snapshots/{runSnapshot}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{project}/source-snapshots/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Source_changes_never_alter_the_confirmed_revision_validation_state_or_can_generate()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var ready = await NewDocument(client, project, "pronto", ready: true);
        var run = (await AnalyzeAccepted(client, project)).GetProperty("id").GetGuid();
        using (var scope = App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var runRow = await db.ProcessingRuns.SingleAsync(r => r.Id == run, Ct);
            var revision = new AnalysisRevision
            {
                Id = Guid.NewGuid(), AnalysisId = run, Version = 1, Origin = AnalysisRevisionOrigin.Ai, CreatedBy = runRow.RequestedBy, CreatedAt = DateTimeOffset.UtcNow,
                SchemaVersion = "1.1.0", ContentJson = "{}", ValidationState = AnalysisValidationState.PmConfirmed, ConfirmedBy = runRow.RequestedBy, ConfirmedAt = DateTimeOffset.UtcNow,
            };
            db.AnalysisRevisions.Add(revision);
            runRow.Status = ProcessingRunStatus.Completed;
            runRow.ResultRevisionId = revision.Id;
            runRow.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);
            await db.Projects.Where(p => p.Id == project).ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentAnalysisId, run), Ct);
        }

        var before = await GetJson(client, $"/api/projects/{project}/analyses/{run}");
        Assert.False(before.GetProperty("hasSourceChanges").GetBoolean());
        Assert.Equal(await SnapshotOf("ProcessingRuns", run), before.GetProperty("sourceSnapshotId").GetGuid());

        await NewDocument(client, project, "pronto-v2", ready: true, documentId: ready.Document);
        await NewDocument(client, project, "nuovo-documento", ready: false);

        var after = await GetJson(client, $"/api/projects/{project}/analyses/{run}");
        Assert.True(after.GetProperty("hasSourceChanges").GetBoolean());
        Assert.Equal(before.GetProperty("validationState").GetString(), after.GetProperty("validationState").GetString());
        Assert.Equal(before.GetProperty("canGenerate").GetBoolean(), after.GetProperty("canGenerate").GetBoolean());
        Assert.Equal(before.GetProperty("canConfirm").GetBoolean(), after.GetProperty("canConfirm").GetBoolean());

        // La bozza eredita lo snapshot dell'analisi: il suo avviso segue la stessa risoluzione, senza toccare la conferma.
        var revisionId = Guid.Parse(after.GetProperty("currentRevisionId").GetString()!);
        var generateRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{run}/document-generations") { Content = JsonContent.Create(new { revisionId }) };
        generateRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var generate = await client.SendAsync(generateRequest, Ct);
        Assert.Equal(HttpStatusCode.Accepted, generate.StatusCode);
        var draftRun = (await Json(generate)).GetProperty("id").GetGuid();
        using (var scope = App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var draft = new Draft { Id = Guid.NewGuid(), ProjectId = project, ProcessingRunId = draftRun, AnalysisRevisionId = revisionId, Markdown = "# Bozza", CreatedAt = DateTimeOffset.UtcNow };
            db.Drafts.Add(draft);
            await db.SaveChangesAsync(Ct);
            await db.ProcessingRuns.Where(r => r.Id == draftRun).ExecuteUpdateAsync(x => x.SetProperty(r => r.Status, ProcessingRunStatus.Completed).SetProperty(r => r.DraftId, draft.Id).SetProperty(r => r.CompletedAt, DateTimeOffset.UtcNow), Ct);
            await db.Projects.Where(p => p.Id == project).ExecuteUpdateAsync(x => x.SetProperty(p => p.LatestDraftId, draft.Id), Ct);
        }

        var draftView = await GetJson(client, $"/api/projects/{project}/draft");
        Assert.Equal(await SnapshotOf("ProcessingRuns", draftRun), draftView.GetProperty("sourceSnapshotId").GetGuid());
        Assert.Equal(await SnapshotOf("ProcessingRuns", run), draftView.GetProperty("sourceSnapshotId").GetGuid());
        Assert.True(draftView.GetProperty("hasSourceChanges").GetBoolean());
    }

    [Fact]
    public async Task Legacy_records_without_a_snapshot_expose_a_null_snapshot_and_no_source_change_flag()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var turn = (await Json(await Message(client, project))).GetProperty("chatTurnId").GetGuid();

        var view = await GetJson(client, $"/api/projects/{project}/chat-turns/{turn}");

        Assert.Equal(System.Text.Json.JsonValueKind.Null, view.GetProperty("sourceSnapshotId").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, view.GetProperty("hasSourceChanges").ValueKind);
    }
}
