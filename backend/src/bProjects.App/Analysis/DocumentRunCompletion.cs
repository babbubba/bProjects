using bProjects.App.AI;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Analysis;

/// <summary>Esecuzione di Document presa in carico: owner e fencing token del lease del ProcessingRun.</summary>
public sealed record DocumentRunClaim(Guid RunId, string LeaseOwner, long FencingToken, int AttemptCount = 0);

public enum DocumentCommitOutcome
{
    /// <summary>Bozza, Completed e LatestDraftId sono stati scritti da questa chiamata.</summary>
    Committed,

    /// <summary>Il run era già Completed (commit precedente riuscito): nulla è stato modificato.</summary>
    AlreadyCompleted,

    /// <summary>Lease/fencing/deadline non più validi o input non coerente: nulla è stato modificato.</summary>
    Rejected,
}

public sealed record DocumentCommitResult(DocumentCommitOutcome Outcome, Guid? DraftId = null, string? Reason = null);

/// <summary>Input immutabile del run Document: la revisione confermata congelata all'avvio, intera.</summary>
public sealed record DocumentRunInput(Guid ProjectId, Guid AnalysisId, Guid RevisionId, int Version, AnalysisContent Content)
{
    public DocumentValidationContext ToValidationContext() => new(new HashSet<Guid>(Content.Sources.Select(s => s.Id)));
}

/// <summary>Chi committa atomicamente il risultato della generazione (T040). Deve rifiutare se lease/fencing non sono più validi.</summary>
public interface IDocumentRunStore
{
    /// <summary>
    /// La revisione identificata da InputRevisionId, intera: deve appartenere all'analisi del run ed essere PmConfirmed.
    /// Mai una revisione più recente, mai una sintesi. Lancia se il run non è un Document o l'input non è coerente.
    /// </summary>
    Task<DocumentRunInput> LoadInputAsync(Guid runId, CancellationToken ct);

    Task<DocumentCommitResult> CommitAsync(DocumentRunClaim claim, string markdown, CancellationToken ct);

    /// <summary>Failed terminale deciso dall'owner. False se owner/fencing non sono più validi.</summary>
    Task<bool> FailAsync(DocumentRunClaim claim, string errorCode, string errorMessage, CancellationToken ct);

    /// <summary>Proiezione informativa della fase osservata (mai usata per decidere il passo successivo). False se non più owner.</summary>
    Task<bool> RecordStageAsync(DocumentRunClaim claim, string stage, CancellationToken ct);
}

/// <summary>
/// Scritture del ProcessingRun di Document (T040, contracts/ai-workflow.md §8). Il commit è unico e atomico: sotto il lock di
/// riga del run, con owner/fencing/lease/deadline verificati sul tempo del database, inserisce la bozza Proposed/NotApproved legata
/// alla revisione di input, porta il run a Completed con DraftId e aggiorna Project.LatestDraftId. Un worker vecchio non scrive nulla;
/// un run già Completed è riconciliato senza duplicare la bozza; un errore non cambia LatestDraftId.
/// </summary>
public sealed class DocumentRunCompletion(AppDbContext db) : IDocumentRunStore
{
    /// <summary>Fase che esegue il commit (id dell'executor ValidatePersistDraft): la proiezione la registra nel commit stesso.</summary>
    public const string CommitStage = "ValidatePersistDraft";

    public async Task<DocumentRunInput> LoadInputAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == runId && r.Kind == ProcessingRunKind.Document, ct);
        var revision = await db.AnalysisRevisions.AsNoTracking().SingleAsync(r => r.Id == run.InputRevisionId, ct);
        if (revision.AnalysisId != run.AnalysisId || revision.ValidationState != AnalysisValidationState.PmConfirmed)
        {
            throw new InvalidOperationException("La revisione di input del run non è una revisione confermata dell'analisi indicata.");
        }

        var content = AnalysisContentJson.Deserialize(revision.ContentJson);
        if (content.ProjectId != run.ProjectId || content.AnalysisId != revision.AnalysisId || content.ValidationState != AnalysisContentState.PmConfirmed)
        {
            throw new InvalidOperationException("Il contenuto della revisione di input non è legato al progetto, all'analisi o alla conferma del run.");
        }

        return new DocumentRunInput(run.ProjectId, revision.AnalysisId, revision.Id, revision.Version, content);
    }

    public async Task<DocumentCommitResult> CommitAsync(DocumentRunClaim claim, string markdown, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new(DocumentCommitOutcome.Rejected, Reason: "markdown_blank");
        }

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var run = (await db.ProcessingRuns
            .FromSql($"SELECT * FROM \"ProcessingRuns\" WHERE \"Id\" = {claim.RunId} FOR UPDATE")
            .AsTracking()
            .ToListAsync(ct)).SingleOrDefault();
        if (run is null || run.Kind != ProcessingRunKind.Document)
        {
            return new(DocumentCommitOutcome.Rejected, Reason: "run_missing");
        }

        var state = (await db.Database.SqlQuery<string>($"""
            SELECT CASE
                WHEN "Status" = 'Completed' THEN 'completed'
                WHEN "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner} AND "FencingToken" = {claim.FencingToken}
                     AND "LeaseUntil" > clock_timestamp() AND "DeadlineAt" > clock_timestamp() THEN 'valid'
                ELSE 'stale' END AS "Value"
            FROM "ProcessingRuns" WHERE "Id" = {claim.RunId}
            """).ToListAsync(ct)).Single();

        if (state == "completed")
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return run.DraftId is { } existing
                ? new(DocumentCommitOutcome.AlreadyCompleted, existing)
                : new(DocumentCommitOutcome.Rejected, Reason: "completed_without_result");
        }

        if (state != "valid")
        {
            return new(DocumentCommitOutcome.Rejected, Reason: "lease_lost");
        }

        // Difesa in profondità: la bozza si lega alla revisione di input del run, che deve essere ancora una revisione confermata dell'analisi.
        var inputRevision = await db.AnalysisRevisions.AsNoTracking().SingleOrDefaultAsync(r => r.Id == run.InputRevisionId, ct);
        if (inputRevision is null || inputRevision.AnalysisId != run.AnalysisId || inputRevision.ValidationState != AnalysisValidationState.PmConfirmed)
        {
            return new(DocumentCommitOutcome.Rejected, Reason: "input_not_confirmed");
        }

        var now = (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single();
        var draft = new Draft
        {
            Id = Guid.NewGuid(),
            ProjectId = run.ProjectId,
            ProcessingRunId = run.Id,
            AnalysisRevisionId = inputRevision.Id,
            Markdown = markdown,
            CreatedAt = now,
            Status = DraftStatus.Proposed,
            ApprovalStatus = DraftApprovalStatus.NotApproved,
        };
        db.Drafts.Add(draft);
        await db.SaveChangesAsync(ct);

        run.Status = ProcessingRunStatus.Completed;
        run.DraftId = draft.Id;
        run.CompletedAt = now;
        run.UpdatedAt = now;
        run.LastObservedStage = CommitStage;
        run.LeaseOwner = null;
        run.LeaseUntil = null;
        run.ErrorCode = null;
        run.ErrorMessage = null;
        // Telemetria del tentativo vincente nella stessa transazione del risultato (stesso controllo di lease/fencing).
        var inference = InferenceScope.Current;
        InferenceCallLog.Stage(db, inference, run.ProjectId, new(null, run.Id, claim.AttemptCount, claim.FencingToken), result: true);
        await db.SaveChangesAsync(ct);

        await db.Projects.Where(p => p.Id == run.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LatestDraftId, draft.Id), ct);

        await transaction.CommitAsync(ct);
        inference?.MarkWritten();
        db.ChangeTracker.Clear();
        return new(DocumentCommitOutcome.Committed, draft.Id);
    }

    /// <summary>Failed deciso dall'owner; le chiamate del tentativo (es. output rifiutato dal validatore) si scrivono nella stessa transazione.</summary>
    public async Task<bool> FailAsync(DocumentRunClaim claim, string errorCode, string errorMessage, CancellationToken ct)
    {
        var inference = InferenceScope.Current;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var failed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ProcessingRuns" SET "Status" = 'Failed', "ErrorCode" = {errorCode}, "ErrorMessage" = {Truncate(errorMessage)},
                "CompletedAt" = clock_timestamp(), "UpdatedAt" = clock_timestamp(), "LeaseOwner" = NULL, "LeaseUntil" = NULL
            WHERE "Id" = {claim.RunId} AND "Kind" = 'Document' AND "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner} AND "FencingToken" = {claim.FencingToken}
            """, ct) == 1;
        if (!failed)
        {
            return false;
        }

        await InferenceCallLog.AppendAttemptAsync(db, inference, new(null, claim.RunId, claim.AttemptCount, claim.FencingToken), ct);
        await transaction.CommitAsync(ct);
        inference?.MarkWritten();
        return true;
    }

    public async Task<bool> RecordStageAsync(DocumentRunClaim claim, string stage, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ProcessingRuns" SET "LastObservedStage" = {stage}, "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {claim.RunId} AND "Kind" = 'Document' AND "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner} AND "FencingToken" = {claim.FencingToken}
            """, ct) == 1;

    private static string Truncate(string message) => message.Length <= 1_000 ? message : message[..1_000];
}
