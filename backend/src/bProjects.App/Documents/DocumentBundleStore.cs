
using bProjects.App.AI;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.App.Documents;

/// <summary>Aggregatore documentale preso in carico: owner e fencing token identificano in modo esclusivo questo tentativo.</summary>
public sealed record AggregationClaim(Guid JobId, Guid PreparationId, Guid ProjectId, string LeaseOwner, long FencingToken, int AttemptCount, int MaxAttempts);

public enum AggregationOutcome
{
    /// <summary>Questa chiamata ha pubblicato versione, Completed, Ready ed eventuale primo bundle corrente, in una sola transazione.</summary>
    Published,

    /// <summary>L'aggregatore era già Completed (replay o pubblicazione di un altro owner): letto lo stesso risultato, nulla di nuovo scritto.</summary>
    AlreadyPublished,

    /// <summary>Autorità assente (owner/fence/lease/scadenze/padre): nulla è stato scritto.</summary>
    Rejected,

    /// <summary>Aggregazione impossibile (copertura, integrità, output): aggregatore Failed e preparazione Incomplete o Failed.</summary>
    Failed,
}

/// <param name="BecameCurrent">Primo bundle completo della versione file: puntatore corrente e promozione automatica tracciata; false per una candidata.</param>
public sealed record AggregationResult(
    AggregationOutcome Outcome, Guid? EvidenceVersionId = null, bool BecameCurrent = false, string? ErrorCode = null, PreparationStatus? PreparationStatus = null);

public enum BundlePromotionOutcome
{
    /// <summary>Preparazione inesistente per questa versione file.</summary>
    NotFound,

    /// <summary>Bundle cambiato: puntatori e revisione si sono mossi insieme, promozione tracciata.</summary>
    Promoted,

    /// <summary>La preparazione è già il bundle corrente: replay, nulla scritto.</summary>
    AlreadyCurrent,

    /// <summary>expectedRevision non è più la revisione dello stato: un altro comando ha vinto.</summary>
    Conflict,

    /// <summary>La preparazione non è Ready con copertura completa e versione Evidence coerente: nessuna promozione dell'incompleto.</summary>
    NotPromotable,
}

/// <param name="State">Stato corrente della versione file dopo la chiamata (null solo se non è mai stato creato).</param>
/// <param name="Promotion">Promozione che ha reso corrente la preparazione (la più recente verso di essa, in caso di replay).</param>
public sealed record BundlePromotionResult(
    BundlePromotionOutcome Outcome, EvidenceDocumentState? State = null, DocumentPromotion? Promotion = null, int Generation = 0);

/// <summary>Notifica best-effort di uno stato definitivo della preparazione, emessa SOLO dopo il commit e solo da chi l'ha prodotto (mai da un owner stale o da un replay).</summary>
public interface IDocumentPreparationEvents
{
    Task PublishStateAsync(Guid projectId, Guid preparationId, PreparationStatus status, CancellationToken ct);
}

/// <summary>
/// Claim e commit dell'unico aggregatore documentale di una Preparation (002 T051; data-model.md §Commit aggregato Evidence F3, §Lifecycle F6).
/// Lavoro deterministico, non AI: ha un advisory lock di claim proprio, non conta nel limite AI globale e non tiene alcun lease mentre le unità
/// lavorano (diventa eleggibile solo quando sono tutte Completed). La pubblicazione è una transazione corta sotto l'ordine di lock progetto →
/// Preparation → aggregatore → DocumentVersion; sotto lock e clock del database controlla owner/fence/lease, scadenze (job e padre) e stato del
/// padre, poi — tutto o nulla — inserisce la versione Evidence (EvidenceLedgerStore), chiude l'aggregatore Completed, rende Ready la preparazione
/// e, se è la prima preparazione completa della versione file, imposta il bundle corrente con promozione automatica. Una candidata (la versione ha
/// già un bundle corrente) non lo sostituisce. Usa la transazione corrente del chiamante solo per il claim; la pubblicazione ne apre una propria.
/// </summary>
public sealed class DocumentBundleStore(AppDbContext db, IOptions<ExecutionOptions> options)
{
    public const string AlgorithmVersion = "document-aggregation-1";

    /// <summary>Serializza i claim degli aggregatori fra istanze; distinto dal lock di claim AI e da quello dei Parse.</summary>
    private const long AggregateClaimLockKey = 0x6250_0051;

    private const string ParentFailedMessage = "Un'unità di estrazione o la preparazione non è più valida: l'aggregazione non può completarsi.";
    private const string DeadlineMessage = "L'aggregazione non è terminata entro la scadenza della preparazione.";
    private const string ExhaustedMessage = "L'aggregazione non è terminata entro il numero di tentativi consentito.";

    private static readonly string EligibleSql = $"""
        e."OriginKind" = 'Document'
        AND ((e."Status" = 'Queued' AND (e."NextAttemptAt" IS NULL OR e."NextAttemptAt" <= clk.t))
             OR (e."Status" = 'Running' AND e."LeaseUntil" <= clk.t))
        AND e."AttemptCount" < e."MaxAttempts"
        AND e."DeadlineAt" > clk.t AND p."EffectiveDeadlineAt" > clk.t AND p."Status" = 'Extracting'
        AND NOT EXISTS (SELECT 1 FROM "DocumentJobs" u WHERE u."PreparationId" = e."PreparationId" AND u."Kind" = 'ExtractUnit' AND u."Status" <> 'Completed')
        AND NOT EXISTS (SELECT 1 FROM "DocumentPortions" x WHERE x."PreparationId" = e."PreparationId" AND x."Status" <> 'Completed')
        """;

    private readonly ExecutionOptions _policy = options.Value;

    /// <summary>
    /// Chiude gli aggregatori non più eseguibili (padre non più in Extracting per un'unità fallita, scadenza, tentativi) insieme alla preparazione quando
    /// la causa è la loro, poi prende in carico il prossimo eleggibile: tutte le unità e le porzioni Completed, preparazione in Extracting e scadenze
    /// nel futuro. Nessuno slot AI: il claim non passa dal lock né dal limite dei lavori di inferenza.
    /// </summary>
    public async Task<AggregationClaim?> ClaimNextAsync(string owner, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await using (var lockCommand = await CommandAsync("SELECT pg_advisory_xact_lock(@key)", ct, ("key", AggregateClaimLockKey)))
        {
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        await using (var sweep = await CommandAsync(
            """
            WITH clk AS (SELECT clock_timestamp() AS t),
            dead AS (
                UPDATE "EvidenceExtractionJobs" e SET
                    "Status" = 'Failed',
                    "ErrorCode" = CASE WHEN p."Status" NOT IN ('Queued', 'Parsing', 'Extracting') THEN 'parent_failed'
                                       WHEN LEAST(e."DeadlineAt", p."EffectiveDeadlineAt") <= clk.t THEN 'deadline_exceeded'
                                       ELSE 'attempts_exhausted' END,
                    "ErrorMessage" = CASE WHEN p."Status" NOT IN ('Queued', 'Parsing', 'Extracting') THEN @parentFailed
                                          WHEN LEAST(e."DeadlineAt", p."EffectiveDeadlineAt") <= clk.t THEN @deadline
                                          ELSE @exhausted END,
                    "CompletedAt" = clk.t, "LeaseOwner" = NULL, "LeaseUntil" = NULL, "FencingToken" = e."FencingToken" + 1
                FROM clk, "DocumentPreparations" p
                WHERE p."Id" = e."PreparationId" AND e."OriginKind" = 'Document'
                  AND (e."Status" = 'Queued' OR (e."Status" = 'Running' AND e."LeaseUntil" <= clk.t))
                  AND (p."Status" NOT IN ('Queued', 'Parsing', 'Extracting') OR LEAST(e."DeadlineAt", p."EffectiveDeadlineAt") <= clk.t
                       OR e."AttemptCount" >= e."MaxAttempts")
                RETURNING e."PreparationId", e."ErrorCode", e."ErrorMessage")
            UPDATE "DocumentPreparations" q SET "Status" = 'Failed', "ErrorCode" = dead."ErrorCode", "ErrorMessage" = dead."ErrorMessage"
            FROM dead
            WHERE q."Id" = dead."PreparationId" AND q."Status" IN ('Queued', 'Parsing', 'Extracting') AND dead."ErrorCode" <> 'parent_failed'
            """,
            ct, ("parentFailed", ParentFailedMessage), ("deadline", DeadlineMessage), ("exhausted", ExhaustedMessage)))
        {
            await sweep.ExecuteNonQueryAsync(ct);
        }

        Guid? candidate;
        await using (var select = await CommandAsync(
            $"""
            WITH clk AS (SELECT clock_timestamp() AS t)
            SELECT e."Id" FROM "EvidenceExtractionJobs" e JOIN "DocumentPreparations" p ON p."Id" = e."PreparationId", clk
            WHERE {EligibleSql}
            ORDER BY p."RequestedAt", e."Id"
            LIMIT 1
            """,
            ct))
        {
            candidate = await select.ExecuteScalarAsync(ct) is Guid id ? id : null;
        }

        AggregationClaim? claim = null;
        if (candidate is { } jobId)
        {
            await using var update = await CommandAsync(
                $"""
                WITH clk AS (SELECT clock_timestamp() AS t)
                UPDATE "EvidenceExtractionJobs" e SET
                    "Status" = 'Running',
                    "LeaseOwner" = @owner,
                    "LeaseUntil" = clk.t + make_interval(secs => @lease),
                    "FencingToken" = e."FencingToken" + 1,
                    "AttemptCount" = e."AttemptCount" + 1,
                    "StartedAt" = COALESCE(e."StartedAt", clk.t),
                    "NextAttemptAt" = NULL
                FROM clk, "DocumentPreparations" p
                WHERE e."Id" = @id AND p."Id" = e."PreparationId" AND {EligibleSql}
                RETURNING e."Id", e."PreparationId", e."ProjectId", e."FencingToken", e."AttemptCount", e."MaxAttempts"
                """,
                ct, ("id", jobId), ("owner", owner), ("lease", _policy.LeaseDuration.TotalSeconds));
            await using var reader = await update.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                claim = new AggregationClaim(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), owner, reader.GetInt64(3), reader.GetInt32(4), reader.GetInt32(5));
            }
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return claim;
    }

    /// <summary>
    /// Pubblica l'aggregato della preparazione. Solo l'owner con il lease corrente dell'aggregatore, con preparazione ancora in Extracting e senza
    /// scadenze passate (clock del database, dopo i lock), può scrivere; un job già Completed rilegge lo stesso risultato. Se l'aggregato non è
    /// valido (copertura, integrità) chiude aggregatore e preparazione nella stessa transazione, mai una versione fittizia.
    /// </summary>
    public async Task<AggregationResult> PublishAsync(AggregationClaim claim, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await ProjectLock.AcquireAsync(db, claim.ProjectId, ct);   // ordine uniforme: progetto → Preparation → aggregatore → DocumentVersion

            var documentVersionId = await ScalarAsync<Guid>(
                """SELECT "DocumentVersionId" FROM "DocumentPreparations" WHERE "Id" = @p AND "ProjectId" = @project FOR UPDATE""", ct,
                ("p", claim.PreparationId), ("project", claim.ProjectId));
            if (documentVersionId is null || await ScalarAsync<Guid>(
                    """SELECT "Id" FROM "EvidenceExtractionJobs" WHERE "Id" = @j AND "PreparationId" = @p AND "OriginKind" = 'Document' FOR UPDATE""", ct,
                    ("j", claim.JobId), ("p", claim.PreparationId)) is null)
            {
                return new(AggregationOutcome.Rejected);
            }

            _ = await ScalarAsync<Guid>("""SELECT "Id" FROM "DocumentVersions" WHERE "Id" = @v FOR UPDATE""", ct, ("v", documentVersionId.Value));

            // Autorità: una sola istruzione DOPO i lock, con il clock del database (le righe non cambiano più fino al commit).
            string status;
            Guid? resultVersion;
            bool valid;
            string? modelId;
            await using (var authority = await CommandAsync(
                """
                SELECT e."Status", e."ResultVersionId",
                       (e."Status" = 'Running' AND e."LeaseOwner" = @owner AND e."FencingToken" = @fence AND e."LeaseUntil" > clock_timestamp()
                        AND LEAST(e."DeadlineAt", p."EffectiveDeadlineAt") > clock_timestamp() AND p."Status" = 'Extracting'),
                       e."ExtractorConfig"->>'modelId'
                FROM "EvidenceExtractionJobs" e JOIN "DocumentPreparations" p ON p."Id" = e."PreparationId"
                WHERE e."Id" = @id
                """,
                ct, ("owner", claim.LeaseOwner), ("fence", claim.FencingToken), ("id", claim.JobId)))
            {
                await using var reader = await authority.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                status = reader.GetString(0);
                resultVersion = reader.IsDBNull(1) ? null : reader.GetGuid(1);
                valid = reader.GetBoolean(2);
                modelId = reader.IsDBNull(3) ? null : reader.GetString(3);
            }

            if (status == "Completed" && resultVersion is { } existing)
            {
                return new(AggregationOutcome.AlreadyPublished, existing);
            }

            if (!valid)
            {
                return new(AggregationOutcome.Rejected);
            }

            var ledger = new EvidenceLedgerStore(db);
            DocumentAggregate aggregate;
            try
            {
                aggregate = await ledger.LoadDocumentAggregateAsync(claim.PreparationId, modelId, ct);
            }
            catch (DocumentAggregationException failure)
            {
                var closed = failure.LeavesPreparationIncomplete ? PreparationStatus.Incomplete : PreparationStatus.Failed;
                await using (var fail = await CommandAsync(
                    """
                    WITH job AS (
                        UPDATE "EvidenceExtractionJobs" SET "Status" = 'Failed', "ErrorCode" = @code, "ErrorMessage" = @message,
                            "CompletedAt" = clock_timestamp(), "LeaseOwner" = NULL, "LeaseUntil" = NULL
                        WHERE "Id" = @id AND "Status" = 'Running' AND "LeaseOwner" = @owner AND "FencingToken" = @fence
                        RETURNING "PreparationId")
                    UPDATE "DocumentPreparations" q SET "Status" = @status, "ErrorCode" = @code, "ErrorMessage" = @message
                    FROM job WHERE q."Id" = job."PreparationId" AND q."Status" = 'Extracting'
                    """,
                    ct, ("code", failure.Code), ("message", failure.Message), ("id", claim.JobId), ("owner", claim.LeaseOwner), ("fence", claim.FencingToken),
                    ("status", closed.ToString())))
                {
                    if (await fail.ExecuteNonQueryAsync(ct) != 1)
                    {
                        throw new InvalidOperationException("Chiusura dell'aggregatore non riuscita sotto lock.");
                    }
                }

                await transaction.CommitAsync(ct);
                return new(AggregationOutcome.Failed, null, false, failure.Code, closed);
            }

            var now = await ProjectLock.NowAsync(db, ct);
            var job = await db.EvidenceExtractionJobs.AsNoTracking().SingleAsync(j => j.Id == claim.JobId, ct);
            var version = await ledger.StageDocumentVersionAsync(job, aggregate, now, ct);

            var state = await db.EvidenceDocumentStates.AsTracking().SingleOrDefaultAsync(s => s.DocumentVersionId == documentVersionId, ct);
            if (state is null)
            {
                state = new EvidenceDocumentState { DocumentVersionId = documentVersionId.Value, ProjectId = claim.ProjectId, Revision = 0, UpdatedAt = now };
                db.EvidenceDocumentStates.Add(state);
            }

            // Prima preparazione completa della versione file: bundle corrente (puntatori insieme) e promozione automatica. Altrimenti è una
            // candidata: il puntatore corrente e LatestReadyVersionId non cambiano (l'eventuale promozione è esplicita del PM, T095).
            var becameCurrent = state.CurrentEvidenceVersionId is null;
            if (becameCurrent)
            {
                state.CurrentPreparationId = claim.PreparationId;
                state.CurrentEvidenceVersionId = version.Id;
                state.Revision += 1;
                state.UpdatedAt = now;
                db.DocumentPromotions.Add(new DocumentPromotion
                {
                    Id = Guid.NewGuid(), ProjectId = claim.ProjectId, DocumentVersionId = documentVersionId.Value, FromPreparationId = null,
                    FromEvidenceVersionId = null, ToPreparationId = claim.PreparationId, ToEvidenceVersionId = version.Id, PromotedBy = null,
                    PromotedAt = now, Reason = EvidencePromotion.FirstValidVersionReason,
                });
            }

            await db.SaveChangesAsync(ct);   // versione, evidenze, relazioni, stato e promozione: prima delle righe che le referenziano

            await using (var complete = await CommandAsync(
                """
                WITH job AS (
                    UPDATE "EvidenceExtractionJobs" SET "Status" = 'Completed', "ResultVersionId" = @version, "CompletedAt" = clock_timestamp(),
                        "LeaseOwner" = NULL, "LeaseUntil" = NULL, "ErrorCode" = NULL, "ErrorMessage" = NULL
                    WHERE "Id" = @id AND "Status" = 'Running' AND "LeaseOwner" = @owner AND "FencingToken" = @fence
                    RETURNING "PreparationId")
                UPDATE "DocumentPreparations" q SET "Status" = 'Ready', "CurrentEvidenceVersionId" = @version, "ErrorCode" = NULL, "ErrorMessage" = NULL
                FROM job WHERE q."Id" = job."PreparationId" AND q."Status" = 'Extracting'
                """,
                ct, ("version", version.Id), ("id", claim.JobId), ("owner", claim.LeaseOwner), ("fence", claim.FencingToken)))
            {
                if (await complete.ExecuteNonQueryAsync(ct) != 1)
                {
                    throw new InvalidOperationException("Chiusura dell'aggregatore e della preparazione non riuscita sotto lock.");
                }
            }

            if (becameCurrent)
            {
                // Cache transazionale derivata dai puntatori correnti (mai dal massimo di tutte le preparazioni Ready).
                await RefreshLatestReadyAsync(documentVersionId.Value, ct);
            }

            await transaction.CommitAsync(ct);
            return new(AggregationOutcome.Published, version.Id, becameCurrent, null, PreparationStatus.Ready);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private const int MaxReasonLength = 1_000;

    /// <summary>
    /// Promozione esplicita del PM (T095): rende corrente una preparazione Ready della versione file, con puntatori, Revision e traccia nella stessa
    /// transazione sotto il lock di progetto. Il replay sul bundle già corrente risponde AlreadyCurrent anche con una revisione ormai vecchia; altrimenti
    /// serve una preparazione Ready con versione Evidence propria e nessuna issue di copertura, e expectedRevision uguale alla revisione dello stato
    /// (un solo vincitore fra promozioni concorrenti). Non esiste promozione del solo estratto: il bundle è sempre preparazione + versione Evidence insieme.
    /// Dati storici e vecchio bundle restano immutati (Superseded è una proiezione).
    /// </summary>
    public async Task<BundlePromotionResult> PromoteAsync(
        Guid projectId, Guid documentVersionId, Guid preparationId, int expectedRevision, Guid promotedBy, string? reason, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await ProjectLock.AcquireAsync(db, projectId, ct);   // ordine uniforme: progetto → Preparation → DocumentVersion/stato

            if (await ScalarAsync<Guid>(
                    """SELECT "Id" FROM "DocumentPreparations" WHERE "Id" = @p AND "ProjectId" = @project AND "DocumentVersionId" = @v FOR UPDATE""", ct,
                    ("p", preparationId), ("project", projectId), ("v", documentVersionId)) is null)
            {
                return new(BundlePromotionOutcome.NotFound);
            }

            _ = await ScalarAsync<Guid>("""SELECT "Id" FROM "DocumentVersions" WHERE "Id" = @v FOR UPDATE""", ct, ("v", documentVersionId));
            var preparation = await db.DocumentPreparations.AsNoTracking().SingleAsync(p => p.Id == preparationId, ct);
            var state = await db.EvidenceDocumentStates.AsTracking().SingleOrDefaultAsync(s => s.DocumentVersionId == documentVersionId, ct);

            if (state?.CurrentPreparationId == preparationId)
            {
                var existing = await db.DocumentPromotions.AsNoTracking().Where(p => p.DocumentVersionId == documentVersionId && p.ToPreparationId == preparationId)
                    .OrderByDescending(p => p.PromotedAt).FirstOrDefaultAsync(ct);
                return new(BundlePromotionOutcome.AlreadyCurrent, state, existing, preparation.Generation);
            }

            var promotable = preparation.Status == PreparationStatus.Ready && preparation.CurrentEvidenceVersionId is { } evidenceVersionId
                && await db.EvidenceExtractionVersions.AsNoTracking().AnyAsync(
                    v => v.Id == evidenceVersionId && v.ProjectId == projectId && v.PreparationId == preparationId && v.OriginKind == EvidenceOriginKind.Document, ct)
                && !await db.PreparationIssues.AsNoTracking().AnyAsync(i => i.PreparationId == preparationId && i.AffectsCompleteness, ct);
            if (!promotable)
            {
                return new(BundlePromotionOutcome.NotPromotable, state, null, preparation.Generation);
            }

            if ((state?.Revision ?? 0) != expectedRevision)
            {
                return new(BundlePromotionOutcome.Conflict, state, null, preparation.Generation);
            }

            var now = await ProjectLock.NowAsync(db, ct);
            var trimmed = reason?.Trim();
            var promotion = new DocumentPromotion
            {
                Id = Guid.NewGuid(), ProjectId = projectId, DocumentVersionId = documentVersionId,
                FromPreparationId = state?.CurrentPreparationId, FromEvidenceVersionId = state?.CurrentEvidenceVersionId,
                ToPreparationId = preparationId, ToEvidenceVersionId = preparation.CurrentEvidenceVersionId!.Value, PromotedBy = promotedBy, PromotedAt = now,
                Reason = string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > MaxReasonLength ? trimmed[..MaxReasonLength] : trimmed,
            };
            if (state is null)
            {
                state = new EvidenceDocumentState { DocumentVersionId = documentVersionId, ProjectId = projectId, Revision = 0, UpdatedAt = now };
                db.EvidenceDocumentStates.Add(state);
            }

            state.CurrentPreparationId = promotion.ToPreparationId;   // i due puntatori e la revisione si muovono insieme, o niente
            state.CurrentEvidenceVersionId = promotion.ToEvidenceVersionId;
            state.Revision += 1;
            state.UpdatedAt = now;
            db.DocumentPromotions.Add(promotion);
            await db.SaveChangesAsync(ct);
            await RefreshLatestReadyAsync(documentVersionId, ct);
            await transaction.CommitAsync(ct);
            return new(BundlePromotionOutcome.Promoted, state, promotion, preparation.Generation);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Cache transazionale LatestReadyVersionId del documento: la versione file con numero più alto che ha un bundle corrente completo, derivata dai
    /// puntatori correnti e mai dalla massima generazione Ready. Va chiamata nella transazione che cambia i puntatori.
    /// </summary>
    private async Task RefreshLatestReadyAsync(Guid documentVersionId, CancellationToken ct)
    {
        await using var latest = await CommandAsync(
            """
            UPDATE "ProjectDocuments" d SET "LatestReadyVersionId" = (
                SELECT v."Id" FROM "DocumentVersions" v JOIN "EvidenceDocumentStates" s ON s."DocumentVersionId" = v."Id"
                WHERE v."DocumentId" = d."Id" AND s."CurrentEvidenceVersionId" IS NOT NULL ORDER BY v."Version" DESC LIMIT 1)
            WHERE d."Id" = (SELECT "DocumentId" FROM "DocumentVersions" WHERE "Id" = @v)
            """,
            ct, ("v", documentVersionId));
        await latest.ExecuteNonQueryAsync(ct);
    }

    private async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
        where T : struct
    {
        await using var command = await CommandAsync(sql, ct, parameters);
        return await command.ExecuteScalarAsync(ct) is T value ? value : null;
    }

    private async Task<NpgsqlCommand> CommandAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync(ct);
        var command = new NpgsqlCommand(
            sql,
            (NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}

/// <summary>
/// Un passo dell'aggregazione documentale: prende in carico l'aggregatore eleggibile, pubblica e, solo se QUESTO passo ha prodotto uno stato
/// definitivo, notifica (best effort, dopo il commit). Il GET resta autorevole: un evento perso non cambia nulla.
/// </summary>
public sealed class DocumentAggregationRunner(DocumentBundleStore bundles, ILogger<DocumentAggregationRunner> logger, IDocumentPreparationEvents? events = null)
{
    /// <summary>Null se non c'era nulla di eleggibile.</summary>
    public async Task<AggregationResult?> RunNextAsync(string owner, CancellationToken ct)
    {
        var claim = await bundles.ClaimNextAsync(owner, ct);
        return claim is null ? null : await RunClaimAsync(claim, ct);
    }

    /// <summary>Pubblica per un claim già ottenuto. La notifica spetta solo a chi ha prodotto lo stato (Published o Failed): mai a un replay né a un owner senza autorità.</summary>
    public async Task<AggregationResult> RunClaimAsync(AggregationClaim claim, CancellationToken ct)
    {
        var result = await bundles.PublishAsync(claim, ct);
        if (events is not null && result is { Outcome: AggregationOutcome.Published or AggregationOutcome.Failed, PreparationStatus: { } status })
        {
            try
            {
                await events.PublishStateAsync(claim.ProjectId, claim.PreparationId, status, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Notifica della preparazione {PreparationId} non riuscita: {ExceptionType}", claim.PreparationId, ex.GetType().Name);
            }
        }

        return result;
    }
}
