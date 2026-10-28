using System.Security.Claims;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using bProjects.App.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.App.Analysis;

public sealed record RunError(string Code, string? Message);

/// <summary>ProcessingRunView del contratto HTTP: stato persistente del run formale e fase osservata, mai un risultato parziale.</summary>
public sealed record ProcessingRunView(
    Guid Id,
    string Kind,
    Guid? AnalysisId,
    long? SourceCutoffSequence,
    Guid? InputRevisionId,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset DeadlineAt,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset? NextAttemptAt,
    string? LastObservedStage,
    Guid? ResultRevisionId,
    Guid? DraftId,
    RunError? Error);

/// <summary>Revisione dell'anteprima con provenienza e conferma (GET .../revisions/{v}).</summary>
public sealed record RevisionView(
    Guid AnalysisId,
    Guid ProjectId,
    Guid RevisionId,
    int Version,
    Guid? ParentRevisionId,
    bool IsCurrent,
    string SchemaVersion,
    JsonElement Content,
    string ValidationState,
    string Origin,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    string? ConfirmedBy,
    DateTimeOffset? ConfirmedAt);

/// <summary>AnalysisView del contratto HTTP: anteprima e revisione corrente (GET .../analyses/{a}).</summary>
public sealed record AnalysisView(
    Guid AnalysisId,
    Guid ProjectId,
    Guid CurrentRevisionId,
    int Version,
    string SchemaVersion,
    JsonElement Content,
    string ValidationState,
    string Origin,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    string? ConfirmedBy,
    DateTimeOffset? ConfirmedAt,
    long SourceCutoffSequence,
    bool HasNewNotes,
    bool CanEdit,
    bool CanConfirm,
    bool CanGenerate);

/// <summary>
/// Comando Analizza e letture di run e anteprima (contracts/http-api.md, «Richiesta di analisi formale»).
/// Il comando non invoca alcuna risorsa AI: sotto il lock della conversazione fissa il cutoff, registra il ProcessingRun
/// Queued con la configurazione congelata e risponde; l'esecuzione spetta al dispatcher (<see cref="AiDispatchService"/>).
/// </summary>
public static class AnalysisEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}");
        group.MapPost("/analyses", Analyze);
        group.MapGet("/processing-runs/{runId:guid}", GetRun);
        group.MapGet("/analyses/{analysisId:guid}", GetAnalysis);
        // {version}: numero di versione o ID della revisione; il parsing è nel gestore, così un valore malformato è 404 e non un'assenza di rotta.
        group.MapGet("/analyses/{analysisId:guid}/revisions/{version}", GetRevision);
        group.MapPreviewEndpoints();
        return app;
    }

    private static async Task<IResult> Analyze(
        Guid projectId,
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

        if (!Guid.TryParse(user.FindFirstValue("sub"), out var requestedBy))
        {
            return Results.Unauthorized();
        }

        var policy = execution.Value;
        Guid runId;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Stesso lock del salvataggio dei messaggi PM: il cutoff è l'ultimo messaggio PM committato, senza race con chi scrive.
            var conversationId = (await db.Database
                .SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"Conversations\" WHERE \"ProjectId\" = {projectId} FOR UPDATE")
                .ToListAsync(ct)).Cast<Guid?>().FirstOrDefault();
            if (conversationId is null)
            {
                return Results.NotFound();
            }

            var cutoff = await db.ConversationMessages.AsNoTracking()
                .Where(m => m.ConversationId == conversationId && m.Role == MessageRole.User)
                .MaxAsync(m => (long?)m.Sequence, ct);

            var existing = await db.ProcessingRuns.AsNoTracking()
                .SingleOrDefaultAsync(r => r.ProjectId == projectId && r.Kind == ProcessingRunKind.Analysis && r.RequestKey == requestKey, ct);
            if (existing is not null)
            {
                // Replay (contracts/http-api.md, Idempotenza): il POST non ha corpo, quindi stessa chiave = stessa richiesta
                // e torna sempre il run originale (202 se attivo, 200 se terminale), anche se il cutoff dei messaggi PM è avanzato.
                var replay = ToView(existing);
                return existing.Status is ProcessingRunStatus.Queued or ProcessingRunStatus.Running
                    ? Results.Accepted(LocationOf(projectId, existing.Id), replay)
                    : Results.Ok(replay);
            }

            if (cutoff is null)
            {
                return Problem(StatusCodes.Status422UnprocessableEntity, "Serve almeno un messaggio del project manager", "no_pm_input");
            }

            var active = await db.ProcessingRuns.AsNoTracking()
                .Where(r => r.ProjectId == projectId && (r.Status == ProcessingRunStatus.Queued || r.Status == ProcessingRunStatus.Running))
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(ct);
            if (active is not null)
            {
                return Problem(StatusCodes.Status409Conflict, "Un'elaborazione formale è già attiva", "processing_active", active);
            }

            var now = (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single();
            var run = new ProcessingRun
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Kind = ProcessingRunKind.Analysis,
                ConversationId = conversationId,
                SourceCutoffSequence = cutoff,
                RequestKey = requestKey,
                RequestedBy = requestedBy,
                RequestedAt = now,
                Status = ProcessingRunStatus.Queued,
                DeadlineAt = now + policy.RunDeadline,
                WorkflowDefinitionVersion = ProcessingRun.AnalysisWorkflowVersion,
                MafVersion = Conversation.CurrentMafVersion,
                AgentConfig = FrozenConfig(ai.Value, policy),
                PromptVersions = JsonSerializer.Serialize(new { analysis = ConversationalAgent.ConsolidationPromptVersion }),
                MaxAttempts = policy.MaxAttempts,
                UpdatedAt = now,
            };
            db.ProcessingRuns.Add(run);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            runId = run.Id;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Un altro comando formale ha vinto la gara sull'indice «uno attivo per progetto»: stessa risposta del controllo esplicito.
            var other = await db.ProcessingRuns.AsNoTracking()
                .Where(r => r.ProjectId == projectId && (r.Status == ProcessingRunStatus.Queued || r.Status == ProcessingRunStatus.Running))
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(ct);
            return Problem(StatusCodes.Status409Conflict, "Un'elaborazione formale è già attiva", "processing_active", other);
        }

        await events.PublishStateAsync(runId); // post-commit; legge lo stato corrente, mai uno vecchio
        var created = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == runId, ct);
        return Results.Accepted(LocationOf(projectId, runId), ToView(created));
    }

    private static async Task<IResult> GetRun(Guid projectId, Guid runId, AppDbContext db, CancellationToken ct)
    {
        var run = await db.ProcessingRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.ProjectId == projectId, ct);
        return run is null ? Results.NotFound() : Results.Ok(ToView(run));
    }

    private static async Task<IResult> GetAnalysis(Guid projectId, Guid analysisId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var view = await BuildViewAsync(db, projectId, analysisId, ct);
        if (view is null)
        {
            return Results.NotFound();
        }

        if (view.Value.View is not { } analysis)
        {
            // Nessun JSON riuscito inventato: stato ed errore restano nel ProcessingRun.
            return Problem(StatusCodes.Status404NotFound, "Anteprima non disponibile", "preview_not_available", analysisId);
        }

        http.Response.Headers.ETag = $"\"{analysis.CurrentRevisionId}\"";
        return Results.Ok(analysis);
    }

    /// <summary>
    /// AnalysisView della revisione corrente. Null se l'analisi non esiste nel progetto; View nulla se non ha ancora una revisione.
    /// Modifica/conferma: solo sull'analisi corrente e mai durante una nuova Analysis attiva (un Document attivo non le blocca,
    /// il suo input è congelato); generare richiede inoltre nessun run formale attivo e la revisione confermata.
    /// </summary>
    internal static async Task<(AnalysisView? View, ProcessingRun Run)?> BuildViewAsync(AppDbContext db, Guid projectId, Guid analysisId, CancellationToken ct)
    {
        var run = await db.ProcessingRuns.AsNoTracking().SingleOrDefaultAsync(
            r => r.Id == analysisId && r.ProjectId == projectId && r.Kind == ProcessingRunKind.Analysis, ct);
        if (run is null)
        {
            return null;
        }

        var revision = await db.AnalysisRevisions.AsNoTracking().Where(r => r.AnalysisId == analysisId).OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
        if (revision is null)
        {
            return (null, run);
        }

        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId, ct);
        var active = await db.ProcessingRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId && (r.Status == ProcessingRunStatus.Queued || r.Status == ProcessingRunStatus.Running))
            .Select(r => r.Kind)
            .ToListAsync(ct);
        // Solo i messaggi PM oltre il cutoff contano: una risposta dell'assistente da sola non attiva l'indicatore.
        var hasNewNotes = await db.ConversationMessages.AsNoTracking()
            .AnyAsync(m => m.ConversationId == run.ConversationId && m.Role == MessageRole.User && m.Sequence > run.SourceCutoffSequence, ct);

        var canEdit = project.CurrentAnalysisId == analysisId && !active.Contains(ProcessingRunKind.Analysis);
        var names = await UserNames(db, [revision.CreatedBy, revision.ConfirmedBy], ct);
        return (new AnalysisView(
            analysisId,
            projectId,
            revision.Id,
            revision.Version,
            revision.SchemaVersion,
            ParseContent(revision),
            revision.ValidationState.ToString(),
            revision.Origin.ToString(),
            names.GetValueOrDefault(revision.CreatedBy),
            revision.CreatedAt,
            revision.ConfirmedBy is { } c ? names.GetValueOrDefault(c) : null,
            revision.ConfirmedAt,
            run.SourceCutoffSequence!.Value,
            hasNewNotes,
            canEdit,
            CanConfirm: canEdit && revision.ValidationState == AnalysisValidationState.Unvalidated,
            CanGenerate: canEdit && active.Count == 0 && revision.ValidationState == AnalysisValidationState.PmConfirmed), run);
    }

    private static async Task<IResult> GetRevision(Guid projectId, Guid analysisId, string version, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var owned = await db.ProcessingRuns.AsNoTracking()
            .AnyAsync(r => r.Id == analysisId && r.ProjectId == projectId && r.Kind == ProcessingRunKind.Analysis, ct);
        if (!owned)
        {
            return Results.NotFound();
        }

        var revisions = db.AnalysisRevisions.AsNoTracking().Where(r => r.AnalysisId == analysisId);
        AnalysisRevision? revision;
        if (int.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            revision = await revisions.SingleOrDefaultAsync(r => r.Version == number, ct);
        }
        else if (Guid.TryParse(version, out var revisionId))
        {
            revision = await revisions.SingleOrDefaultAsync(r => r.Id == revisionId, ct);
        }
        else
        {
            return Results.NotFound();
        }

        if (revision is null)
        {
            return Results.NotFound();
        }

        var maxVersion = await revisions.MaxAsync(r => r.Version, ct);
        var names = await UserNames(db, [revision.CreatedBy, revision.ConfirmedBy], ct);
        http.Response.Headers.ETag = $"\"{revision.Id}\"";
        return Results.Ok(new RevisionView(
            analysisId,
            projectId,
            revision.Id,
            revision.Version,
            revision.ParentRevisionId,
            revision.Version == maxVersion,
            revision.SchemaVersion,
            ParseContent(revision),
            revision.ValidationState.ToString(),
            revision.Origin.ToString(),
            names.GetValueOrDefault(revision.CreatedBy),
            revision.CreatedAt,
            revision.ConfirmedBy is { } c ? names.GetValueOrDefault(c) : null,
            revision.ConfirmedAt));
    }

    public static ProcessingRunView ToView(ProcessingRun run) => new(
        run.Id,
        run.Kind.ToString(),
        run.AnalysisId,
        run.SourceCutoffSequence,
        run.InputRevisionId,
        run.Status.ToString(),
        run.RequestedAt,
        run.StartedAt,
        run.CompletedAt,
        run.DeadlineAt,
        run.AttemptCount,
        run.MaxAttempts,
        run.NextAttemptAt,
        run.LastObservedStage,
        run.ResultRevisionId,
        run.DraftId,
        run.ErrorCode is null ? null : new RunError(run.ErrorCode, run.ErrorMessage));

    private static string LocationOf(Guid projectId, Guid runId) => $"/api/projects/{projectId}/processing-runs/{runId}";

    private static JsonElement ParseContent(AnalysisRevision revision)
    {
        using var document = JsonDocument.Parse(revision.ContentJson);
        return document.RootElement.Clone();
    }

    private static async Task<Dictionary<Guid, string?>> UserNames(AppDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName, ct);
    }

    /// <summary>Configurazione risolta del run, congelata alla richiesta: nessun endpoint né credenziale, soltanto parametri.</summary>
    internal static string FrozenConfig(AiOptions ai, ExecutionOptions policy) => JsonSerializer.Serialize(new
    {
        modelId = ai.ModelId,
        structuredOutputMode = ai.StructuredOutputMode.ToString(),
        contextWindowTokens = ai.ContextWindowTokens,
        maxOutputTokens = ai.MaxOutputTokens,
        safetyMarginTokens = ai.SafetyMarginTokens,
        inputBudgetTokens = ai.InputBudgetTokens,
        compactionTriggerTokens = ai.EffectiveCompactionTriggerTokens,
        enableThinking = ai.EnableThinking,
        temperature = 0,
        requestTimeoutSeconds = ai.RequestTimeoutSeconds,
        leaseSeconds = policy.LeaseDurationSeconds,
        renewSeconds = policy.RenewIntervalSeconds,
        maxAttempts = policy.MaxAttempts,
        retryBaseDelaySeconds = policy.RetryBaseDelaySeconds,
        retryMaxDelaySeconds = policy.RetryMaxDelaySeconds,
        runDeadlineSeconds = policy.RunDeadlineSeconds,
    });

    private static IResult Problem(int status, string title, string code, Guid? processingRunId = null) => Results.Problem(
        statusCode: status,
        title: title,
        extensions: new Dictionary<string, object?> { ["code"] = code, ["processingRunId"] = processingRunId });
}

/// <summary>
/// Pubblica processing.state sullo stream del progetto (<see cref="ProjectEventHub"/>) con la ProcessingRunView corrente.
/// Notifiche effimere e best-effort: un errore qui non tocca mai il lavoro durabile; lo stato si legge dal database
/// dopo la transizione, quindi la fase mostrata è soltanto quella osservata davvero (lastObservedStage).
/// </summary>
public sealed class ProcessingRunEvents(ProjectEventHub hub, IServiceScopeFactory scopes, ILogger<ProcessingRunEvents> logger)
{
    public async Task PublishStateAsync(Guid runId)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.ProcessingRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId);
            if (run is not null)
            {
                hub.Publish(run.ProjectId, ProjectEventHub.ProcessingState, AnalysisEndpoints.ToView(run));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Notifica processing.state non pubblicata: {ExceptionType}", ex.GetType().Name);
        }
    }
}
