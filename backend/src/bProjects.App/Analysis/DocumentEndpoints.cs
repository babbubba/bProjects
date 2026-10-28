using System.Security.Claims;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.App.Analysis;

/// <summary>DraftView del contratto HTTP: bozza Markdown proposta, mai approvata, con la revisione usata e gli indicatori di obsolescenza.</summary>
public sealed record DraftView(
    Guid Id,
    Guid ProjectId,
    Guid ProcessingRunId,
    Guid AnalysisId,
    Guid AnalysisRevisionId,
    int AnalysisVersion,
    string Markdown,
    DateTimeOffset CreatedAt,
    string Status,
    string ApprovalStatus,
    bool HasNewNotes,
    bool HasNewRevision,
    string Label,
    InferenceMetricsView? Metrics = null);

/// <summary>
/// Generazione esplicita del documento e lettura dell'ultima bozza (contracts/http-api.md, «Generazione esplicita del documento»).
/// Il comando non invoca alcuna risorsa AI: sotto lo stesso lock della conversazione di Analizza, messaggi PM, modifica e conferma
/// controlla analisi corrente, revisione corrente PmConfirmed e assenza di un altro run formale attivo, poi registra il
/// ProcessingRun Document Queued con InputRevisionId congelato; l'esecuzione spetta allo stesso dispatcher dell'Analysis.
/// </summary>
public static class DocumentEndpoints
{
    /// <summary>Etichetta mostrata con ogni bozza: l'AI propone, una persona deve revisionare.</summary>
    public const string DraftLabel = "Bozza AI — da revisionare";

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}");
        group.MapPost("/analyses/{analysisId:guid}/document-generations", Generate);
        group.MapGet("/draft", GetDraft);
        return app;
    }

    private static async Task<IResult> Generate(
        Guid projectId,
        Guid analysisId,
        HttpContext http,
        ClaimsPrincipal user,
        AppDbContext db,
        IOptions<ExecutionOptions> execution,
        IOptions<AiOptions> ai,
        ProcessingRunEvents events,
        CancellationToken ct)
    {
        if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var raw) || raw.Count != 1
            || !Guid.TryParse(raw[0], out var requestKey) || requestKey == Guid.Empty)
        {
            return Problem(StatusCodes.Status400BadRequest, "Idempotency-Key mancante o non valida", "idempotency_key_required");
        }

        var revision = await ReadRevisionIdAsync(http, ct);
        if (revision.Problem is { } bad)
        {
            return bad;
        }

        var revisionId = revision.Id;
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var requestedBy))
        {
            return Results.Unauthorized();
        }

        var policy = execution.Value;
        Guid runId;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Stesso lock del salvataggio dei messaggi PM, di Analizza e di modifica/conferma: i comandi formali e le guardie
            // sulla revisione corrente si serializzano, e ogni predicato è letto dopo il lock.
            var conversationId = (await db.Database
                .SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"Conversations\" WHERE \"ProjectId\" = {projectId} FOR UPDATE")
                .ToListAsync(ct)).Cast<Guid?>().FirstOrDefault();
            if (conversationId is null)
            {
                return Results.NotFound();
            }

            var existing = await db.ProcessingRuns.AsNoTracking()
                .SingleOrDefaultAsync(r => r.ProjectId == projectId && r.Kind == ProcessingRunKind.Document && r.RequestKey == requestKey, ct);
            if (existing is not null)
            {
                // Replay (contracts/http-api.md, Idempotenza): stessa chiave e stesso input = stesso run (202 se attivo, 200 se terminale),
                // anche se nel frattempo la revisione non è più la corrente; input differente = conflitto.
                if (existing.AnalysisId != analysisId || existing.InputRevisionId != revisionId)
                {
                    return Problem(StatusCodes.Status409Conflict, "La stessa Idempotency-Key è già stata usata con un input diverso", "idempotency_conflict", existing.Id);
                }

                var replay = await AnalysisEndpoints.ViewAsync(db, existing, ct);
                return existing.Status is ProcessingRunStatus.Queued or ProcessingRunStatus.Running
                    ? Results.Accepted(LocationOf(projectId, existing.Id), replay)
                    : Results.Ok(replay);
            }

            var analysisExists = await db.ProcessingRuns.AsNoTracking()
                .AnyAsync(r => r.Id == analysisId && r.ProjectId == projectId && r.Kind == ProcessingRunKind.Analysis, ct);
            if (!analysisExists)
            {
                return Results.NotFound();
            }

            // Il run formale attivo si legge PRIMA dell'analisi corrente: il commit di una Analysis (revisione AI, Completed e CurrentAnalysisId nella
            // stessa transazione) non prende il lock della conversazione e in READ COMMITTED può avvenire fra due letture. In questo ordine o si vede
            // l'Analysis ancora attiva (processing_active) o la si vede completata e allora CurrentAnalysisId è già quello nuovo (analysis_not_current).
            // Nessun altro comando può attivare un run senza il lock: una lettura anticipata può solo essere prudente, mai ottimista.
            var active = await ActiveRunAsync(db, projectId, ct);
            var currentAnalysisId = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.CurrentAnalysisId).SingleAsync(ct);
            if (currentAnalysisId != analysisId)
            {
                return Problem(StatusCodes.Status409Conflict, "Solo l'analisi corrente del progetto può generare il documento", "analysis_not_current");
            }

            var current = await db.AnalysisRevisions.AsNoTracking()
                .Where(r => r.AnalysisId == analysisId)
                .OrderByDescending(r => r.Version)
                .Select(r => new { r.Id, r.ValidationState })
                .FirstOrDefaultAsync(ct);
            if (current is null)
            {
                return Problem(StatusCodes.Status404NotFound, "Anteprima non disponibile", "preview_not_available", analysisId);
            }

            if (current.Id != revisionId)
            {
                return Problem(StatusCodes.Status409Conflict, "La revisione indicata non è più la corrente", "revision_conflict", currentRevisionId: current.Id);
            }

            if (current.ValidationState != AnalysisValidationState.PmConfirmed)
            {
                return Problem(StatusCodes.Status409Conflict, "La revisione corrente non è stata confermata dal PM", "analysis_not_confirmed");
            }

            if (active is not null)
            {
                return Problem(StatusCodes.Status409Conflict, "Un'elaborazione formale è già attiva", "processing_active", active);
            }

            var now = (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single();
            var run = new ProcessingRun
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Kind = ProcessingRunKind.Document,
                AnalysisId = analysisId,
                InputRevisionId = revisionId,
                RequestKey = requestKey,
                RequestedBy = requestedBy,
                RequestedAt = now,
                Status = ProcessingRunStatus.Queued,
                DeadlineAt = now + policy.RunDeadline,
                WorkflowDefinitionVersion = ProcessingRun.DocumentWorkflowVersion,
                MafVersion = Conversation.CurrentMafVersion,
                AgentConfig = AnalysisEndpoints.FrozenConfig(ai.Value, policy, AiPhase.Document),
                PromptVersions = JsonSerializer.Serialize(new { document = DocumentAgent.PromptVersion }),
                MaxAttempts = policy.MaxAttempts,
                UpdatedAt = now,
            };
            db.ProcessingRuns.Add(run);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            runId = run.Id;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_ProcessingRuns_OneActivePerProject" })
        {
            // Un altro comando formale ha vinto la gara sull'indice «uno attivo per progetto»: stessa risposta del controllo esplicito.
            return Problem(StatusCodes.Status409Conflict, "Un'elaborazione formale è già attiva", "processing_active", await ActiveRunAsync(db, projectId, ct));
        }

        await events.PublishStateAsync(runId); // post-commit; legge lo stato corrente, mai uno vecchio
        var created = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == runId, ct);
        return Results.Accepted(LocationOf(projectId, runId), AnalysisEndpoints.ToView(created));
    }

    /// <summary>
    /// Ultima bozza riuscita (Project.LatestDraftId), con revisione e versione usate. HasNewNotes: messaggi PM oltre il cutoff
    /// dell'analisi da cui nasce; HasNewRevision: l'ultima revisione dell'analisi corrente non è quella della bozza.
    /// </summary>
    private static async Task<IResult> GetDraft(Guid projectId, AppDbContext db, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
        {
            return Results.NotFound();
        }

        if (project.LatestDraftId is not { } draftId)
        {
            return Problem(StatusCodes.Status404NotFound, "Nessuna bozza disponibile", "draft_not_available");
        }

        var draft = await db.Drafts.AsNoTracking().SingleAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        var source = await db.AnalysisRevisions.AsNoTracking().SingleAsync(r => r.Id == draft.AnalysisRevisionId, ct);
        var analysis = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == source.AnalysisId, ct);

        // Solo i messaggi PM contano: una risposta dell'assistente oltre il cutoff non rende la bozza «vecchia».
        var hasNewNotes = await db.ConversationMessages.AsNoTracking()
            .AnyAsync(m => m.ConversationId == analysis.ConversationId && m.Role == MessageRole.User && m.Sequence > analysis.SourceCutoffSequence, ct);
        var latestRevisionId = project.CurrentAnalysisId is { } currentAnalysis
            ? await db.AnalysisRevisions.AsNoTracking()
                .Where(r => r.AnalysisId == currentAnalysis)
                .OrderByDescending(r => r.Version)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(ct)
            : null;

        return Results.Ok(new DraftView(
            draft.Id,
            draft.ProjectId,
            draft.ProcessingRunId,
            source.AnalysisId,
            source.Id,
            source.Version,
            draft.Markdown,
            draft.CreatedAt,
            draft.Status.ToString(),
            draft.ApprovalStatus.ToString(),
            hasNewNotes,
            HasNewRevision: latestRevisionId != source.Id,
            DraftLabel,
            // Metriche del run Document che ha generato la bozza, rilette dopo il reload insieme al Markdown.
            await InferenceMetrics.ForRunAsync(db, projectId, draft.ProcessingRunId, ct)));
    }

    private static async Task<Guid?> ActiveRunAsync(AppDbContext db, Guid projectId, CancellationToken ct) =>
        await db.ProcessingRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId && (r.Status == ProcessingRunStatus.Queued || r.Status == ProcessingRunStatus.Running))
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>Corpo {"revisionId": uuid}: assente, vuoto o non UUID è 400 revision_required; JSON non valido è 400 malformed_request.</summary>
    private static async Task<(Guid Id, IResult? Problem)> ReadRevisionIdAsync(HttpContext http, CancellationToken ct)
    {
        string body;
        using (var reader = new StreamReader(http.Request.Body, System.Text.Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(ct);
        }

        IResult Required() => Problem(StatusCodes.Status400BadRequest, "revisionId mancante o non valido", "revision_required");
        if (string.IsNullOrWhiteSpace(body))
        {
            return (Guid.Empty, Required());
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("revisionId", out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var id)
                && id != Guid.Empty)
            {
                return (id, null);
            }

            return (Guid.Empty, Required());
        }
        catch (JsonException)
        {
            return (Guid.Empty, Problem(StatusCodes.Status400BadRequest, "Corpo JSON non valido", "malformed_request"));
        }
    }

    private static string LocationOf(Guid projectId, Guid runId) => $"/api/projects/{projectId}/processing-runs/{runId}";

    private static IResult Problem(int status, string title, string code, Guid? processingRunId = null, Guid? currentRevisionId = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (processingRunId is not null)
        {
            extensions["processingRunId"] = processingRunId;
        }

        if (currentRevisionId is not null)
        {
            extensions["currentRevisionId"] = currentRevisionId;
        }

        return Results.Problem(statusCode: status, title: title, extensions: extensions);
    }
}
