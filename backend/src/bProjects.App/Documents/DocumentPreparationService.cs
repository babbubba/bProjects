using System.Text.Json;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Documents;

public enum PreparationRetryKind
{
    /// <summary>Job o progetto inesistente (o di un altro progetto).</summary>
    NotFound,

    /// <summary>Nuova richiesta accodata: Preparation e job Parse Queued collegati al terminale.</summary>
    Created,

    /// <summary>Stessa chiave e stesso input: stessi ID, nulla di nuovo in coda.</summary>
    Replayed,

    /// <summary>Conflitto con codice stabile (idempotency_conflict, job_active, preparation_active, retry_not_allowed, job_superseded).</summary>
    Conflict,
}

public sealed record PreparationRetryResult(
    PreparationRetryKind Kind, string? Code = null, Guid JobId = default, Guid PreparationId = default, int Generation = 0,
    Guid RetryOfJobId = default, Guid RetryOfPreparationId = default, DateTimeOffset RequestedAt = default, DateTimeOffset DeadlineAt = default);

/// <summary>
/// Retry minimo del parsing (T116), servizio unico delle azioni di preparazione (T094 lo estende, senza un secondo percorso di retry).
/// Un job Parse terminale fallito, oppure Completed con preparazione Incomplete, genera una NUOVA Preparation con un nuovo job Parse Queued
/// collegati al precedente (<c>RetryOfId</c>), con t0 e cap di preparazione nuovi e un policy hash congelato adesso. Il vecchio root, il suo
/// errore, i suoi output e le sue scadenze restano immutati; l'originale non viene riscritto. L'idempotenza è la chiave della richiesta,
/// unica per progetto. Tutto avviene in una transazione breve sotto il lock di progetto; il lavoro vero lo esegue il worker del parsing.
/// </summary>
public sealed class DocumentPreparationService(AppDbContext db, DocumentPolicyService policies)
{
    public const int ParseMaxAttempts = 3;

    /// <summary>
    /// Stato terminale ritentabile per il PM: job Failed, o job Completed la cui preparazione è Incomplete. Non dice nulla sulla presenza di una
    /// generazione successiva o di lavoro aperto: lo decide <see cref="RetryParseAsync"/> sotto lock (e <see cref="IsRetryAllowedAsync"/> per le viste).
    /// </summary>
    public static bool IsRetryableTerminal(DocumentJobStatus job, PreparationStatus preparation) =>
        job == DocumentJobStatus.Failed || (job == DocumentJobStatus.Completed && preparation == PreparationStatus.Incomplete);

    /// <summary>Azione «retry» ammessa per la preparazione: ultima generazione della versione, nessun lavoro aperto e job Parse terminale ritentabile.</summary>
    public async Task<bool> IsRetryAllowedAsync(DocumentPreparation preparation, CancellationToken ct)
    {
        if (preparation.ParseJobId is not { } jobId)
        {
            return false;
        }

        var job = await db.DocumentJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => new { j.Status }).SingleOrDefaultAsync(ct);
        if (job is null || !IsRetryableTerminal(job.Status, preparation.Status))
        {
            return false;
        }

        return !await db.DocumentPreparations.AsNoTracking().AnyAsync(
            p => p.DocumentVersionId == preparation.DocumentVersionId && (p.Generation > preparation.Generation
                || p.Status == PreparationStatus.Queued || p.Status == PreparationStatus.Parsing || p.Status == PreparationStatus.Extracting), ct);
    }

    public async Task<PreparationRetryResult> RetryParseAsync(Guid projectId, Guid jobId, Guid requestKey, Guid requestedBy, CancellationToken ct)
    {
        var (_, policy) = await policies.GetAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProjectLock.AcquireAsync(db, projectId, ct);

        var target = await db.DocumentJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId && j.ProjectId == projectId && j.Kind == DocumentJobKind.Parse, ct);
        if (target is null)
        {
            return new(PreparationRetryKind.NotFound);
        }

        // Replay prima delle regole di stato: dopo la creazione il terminale non è più «l'ultimo», ma la stessa chiave deve rispondere con gli stessi ID.
        var existing = await db.DocumentJobs.AsNoTracking().SingleOrDefaultAsync(j => j.ProjectId == projectId && j.RequestKey == requestKey, ct);
        if (existing is not null)
        {
            if (existing.RetryOfId != jobId || existing.Kind != DocumentJobKind.Parse)
            {
                return new(PreparationRetryKind.Conflict, "idempotency_conflict");   // stessa chiave, input diverso
            }

            return await ViewAsync(PreparationRetryKind.Replayed, existing, ct);
        }

        var previous = await db.DocumentPreparations.AsNoTracking().SingleAsync(p => p.Id == target.PreparationId && p.ProjectId == projectId, ct);
        if (target.Status is DocumentJobStatus.Queued or DocumentJobStatus.Running)
        {
            return new(PreparationRetryKind.Conflict, "job_active");
        }

        if (!IsRetryableTerminal(target.Status, previous.Status))
        {
            return new(PreparationRetryKind.Conflict, "retry_not_allowed");   // Completed con preparazione non incompleta: nulla da ritentare
        }

        var siblings = await db.DocumentPreparations.AsNoTracking().Where(p => p.DocumentVersionId == previous.DocumentVersionId)
            .Select(p => new { p.Generation, p.Status }).ToListAsync(ct);
        if (siblings.Any(p => p.Status is PreparationStatus.Queued or PreparationStatus.Parsing or PreparationStatus.Extracting))
        {
            return new(PreparationRetryKind.Conflict, "preparation_active");
        }

        if (siblings.Any(p => p.Generation > previous.Generation))
        {
            return new(PreparationRetryKind.Conflict, "job_superseded");   // il terminale è già stato superato da un'altra generazione
        }

        var now = await ProjectLock.NowAsync(db, ct);   // nuovo t0 esplicito: il cap non è quello del vecchio root
        var hardDeadline = now.AddSeconds(policy.Limits.PreparationDeadlineSeconds);
        var preparation = new DocumentPreparation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            DocumentVersionId = previous.DocumentVersionId,
            Generation = siblings.Max(p => p.Generation) + 1,
            Status = PreparationStatus.Queued,
            RetryOfId = previous.Id,
            RequestedAt = now,
            PolicyHash = policy.PolicyHash,
            HardDeadlineAt = hardDeadline,
            EffectiveDeadlineAt = hardDeadline,
            CreatedAt = now,
        };
        db.DocumentPreparations.Add(preparation);
        await db.SaveChangesAsync(ct);

        var limits = policy.Limits;
        var job = new DocumentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            PreparationId = preparation.Id,
            Kind = DocumentJobKind.Parse,
            Status = DocumentJobStatus.Queued,
            RequestKey = requestKey,
            RetryOfId = target.Id,
            RequestedBy = requestedBy,
            RequestedAt = now,
            DeadlineAt = hardDeadline,
            MaxAttempts = ParseMaxAttempts,
            ConfigJson = JsonSerializer.Serialize(new
            {
                policyHash = policy.PolicyHash,
                limits.ParseTimeoutSeconds,
                limits.MaxPages,
                limits.MaxBlocks,
                limits.MaxExpandedBytes,
                limits.MaxDerivedBytes,
            }),
        };
        db.DocumentJobs.Add(job);
        await db.SaveChangesAsync(ct);
        preparation.ParseJobId = job.Id;   // FK circolare: due passi nella stessa transazione, come all'acquisizione
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(PreparationRetryKind.Created, null, job.Id, preparation.Id, preparation.Generation, target.Id, previous.Id, now, hardDeadline);
    }

    private async Task<PreparationRetryResult> ViewAsync(PreparationRetryKind kind, DocumentJob job, CancellationToken ct)
    {
        var preparation = await db.DocumentPreparations.AsNoTracking().SingleAsync(p => p.Id == job.PreparationId, ct);
        return new(kind, null, job.Id, preparation.Id, preparation.Generation, job.RetryOfId!.Value, preparation.RetryOfId!.Value, preparation.RequestedAt, job.DeadlineAt);
    }
}
