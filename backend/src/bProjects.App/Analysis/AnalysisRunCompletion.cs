using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Analysis;

/// <summary>Esecuzione di Analysis presa in carico: owner e fencing token del lease del ProcessingRun.</summary>
public sealed record AnalysisRunClaim(Guid RunId, string LeaseOwner, long FencingToken);

public enum AnalysisCommitOutcome
{
    /// <summary>Revisione AI, Completed e CurrentAnalysisId sono stati scritti da questa chiamata.</summary>
    Committed,

    /// <summary>Il run era già Completed (commit precedente riuscito): nulla è stato modificato.</summary>
    AlreadyCompleted,

    /// <summary>Lease/fencing/deadline non più validi: nulla è stato modificato.</summary>
    Rejected,
}

/// <summary>Messaggio originale della conversazione, ammesso dal cutoff.</summary>
public sealed record AnalysisDialogMessage(Guid Id, AnalysisMessageRole Role, long Sequence, string Body);

/// <summary>Input immutabile del run: identità fidate e dialogo fino al cutoff.</summary>
public sealed record AnalysisRunInput(Guid ProjectId, Guid AnalysisId, long SourceCutoffSequence, IReadOnlyList<AnalysisDialogMessage> Dialog)
{
    public AnalysisValidationContext ToValidationContext() => new(
        ProjectId,
        AnalysisId,
        SourceCutoffSequence,
        [.. Dialog.Select(m => new AnalysisMessageRef(m.Id, ProjectId, m.Role, m.Sequence))],
        new HashSet<Guid>());
}

public sealed record AnalysisCommitResult(AnalysisCommitOutcome Outcome, Guid? RevisionId = null, string? Reason = null);

/// <summary>Chi committa atomicamente il risultato dell'analisi (T029). Deve rifiutare se lease/fencing non sono più validi.</summary>
public interface IAnalysisRunStore
{
    /// <summary>Input fidato del run: la conversazione fino al cutoff immutabile, mai oltre.</summary>
    Task<AnalysisRunInput> LoadInputAsync(Guid runId, CancellationToken ct);

    Task<AnalysisCommitResult> CommitAsync(AnalysisRunClaim claim, AnalysisContent content, CancellationToken ct);

    /// <summary>Failed terminale deciso dall'owner. False se owner/fencing non sono più validi.</summary>
    Task<bool> FailAsync(AnalysisRunClaim claim, string errorCode, string errorMessage, CancellationToken ct);

    /// <summary>Proiezione informativa della fase osservata (mai usata per decidere il passo successivo). False se non più owner.</summary>
    Task<bool> RecordStageAsync(AnalysisRunClaim claim, string stage, CancellationToken ct);
}

/// <summary>
/// Scritture del ProcessingRun di Analysis (T029, contracts/ai-workflow.md §8). Il commit è unico e atomico: sotto il
/// lock di riga del run, con owner/fencing/lease/deadline verificati sul tempo del database, inserisce la prima revisione
/// AI Unvalidated, porta il run a Completed con ResultRevisionId e aggiorna Project.CurrentAnalysisId. Un worker vecchio
/// non scrive nulla; un run già Completed è riconciliato senza duplicare la revisione.
/// </summary>
public sealed class AnalysisRunCompletion(AppDbContext db) : IAnalysisRunStore
{
    /// <summary>Fase che esegue il commit (id dell'executor ValidatePersistAnalysis): la proiezione la registra nel commit stesso, senza gara con l'osservatore degli eventi.</summary>
    public const string CommitStage = "ValidatePersistAnalysis";

    public async Task<AnalysisRunInput> LoadInputAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == runId && r.Kind == ProcessingRunKind.Analysis, ct);
        var messages = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == run.ConversationId && m.Sequence <= run.SourceCutoffSequence)
            .OrderBy(m => m.Sequence)
            .ToListAsync(ct);

        return new AnalysisRunInput(
            run.ProjectId,
            run.Id,
            run.SourceCutoffSequence!.Value,
            [.. messages.Select(m => new AnalysisDialogMessage(
                m.Id, m.Role == Conversations.MessageRole.User ? AnalysisMessageRole.User : AnalysisMessageRole.Assistant, m.Sequence, m.Body))]);
    }

    public async Task<AnalysisCommitResult> CommitAsync(AnalysisRunClaim claim, AnalysisContent content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(content);

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var run = (await db.ProcessingRuns
            .FromSql($"SELECT * FROM \"ProcessingRuns\" WHERE \"Id\" = {claim.RunId} FOR UPDATE")
            .AsTracking()
            .ToListAsync(ct)).SingleOrDefault();
        if (run is null || run.Kind != ProcessingRunKind.Analysis)
        {
            return new(AnalysisCommitOutcome.Rejected, Reason: "run_missing");
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
            return run.ResultRevisionId is { } existing
                ? new(AnalysisCommitOutcome.AlreadyCompleted, existing)
                : new(AnalysisCommitOutcome.Rejected, Reason: "completed_without_result");
        }

        if (state != "valid")
        {
            return new(AnalysisCommitOutcome.Rejected, Reason: "lease_lost");
        }

        if (content.AnalysisId != run.Id || content.ProjectId != run.ProjectId || content.SourceCutoffSequence != run.SourceCutoffSequence)
        {
            // Difesa in profondità: la validazione ha già imposto questi valori dall'input fidato.
            return new(AnalysisCommitOutcome.Rejected, Reason: "content_not_bound_to_run");
        }

        var now = (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single();
        var revision = new AnalysisRevision
        {
            Id = Guid.NewGuid(),
            AnalysisId = run.Id,
            Version = 1,
            ParentRevisionId = null,
            Origin = AnalysisRevisionOrigin.Ai,
            CreatedBy = run.RequestedBy,
            CreatedAt = now,
            SchemaVersion = AnalysisRevision.CurrentSchemaVersion,
            ContentJson = AnalysisContentJson.Serialize(content),
            ValidationState = AnalysisValidationState.Unvalidated,
        };
        db.AnalysisRevisions.Add(revision);
        await db.SaveChangesAsync(ct);

        run.Status = ProcessingRunStatus.Completed;
        run.ResultRevisionId = revision.Id;
        run.CompletedAt = now;
        run.UpdatedAt = now;
        run.LastObservedStage = CommitStage;
        run.LeaseOwner = null;
        run.LeaseUntil = null;
        run.ErrorCode = null;
        run.ErrorMessage = null;
        await db.SaveChangesAsync(ct);

        await db.Projects.Where(p => p.Id == run.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.CurrentAnalysisId, run.Id), ct);

        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(AnalysisCommitOutcome.Committed, revision.Id);
    }

    public async Task<bool> FailAsync(AnalysisRunClaim claim, string errorCode, string errorMessage, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ProcessingRuns" SET "Status" = 'Failed', "ErrorCode" = {errorCode}, "ErrorMessage" = {Truncate(errorMessage)},
                "CompletedAt" = clock_timestamp(), "UpdatedAt" = clock_timestamp(), "LeaseOwner" = NULL, "LeaseUntil" = NULL
            WHERE "Id" = {claim.RunId} AND "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner} AND "FencingToken" = {claim.FencingToken}
            """, ct) == 1;

    public async Task<bool> RecordStageAsync(AnalysisRunClaim claim, string stage, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ProcessingRuns" SET "LastObservedStage" = {stage}, "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {claim.RunId} AND "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner} AND "FencingToken" = {claim.FencingToken}
            """, ct) == 1;

    private static string Truncate(string message) => message.Length <= 1_000 ? message : message[..1_000];
}
