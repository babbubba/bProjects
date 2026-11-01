using bProjects.App.AI;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.App.Documents;

/// <summary>Job Parse preso in carico: owner e fencing token identificano in modo esclusivo questo tentativo.</summary>
/// <param name="Remaining">Residuo al claim: min(DeadlineAt del job, EffectiveDeadlineAt del padre) meno l'orologio del database.</param>
public sealed record ParseClaim(
    Guid JobId, Guid PreparationId, Guid ProjectId, Guid DocumentVersionId, string LeaseOwner, long FencingToken, int AttemptCount, int MaxAttempts,
    TimeSpan Remaining, string? ConfigJson);

/// <summary>Esito del rilascio di un job Parse dopo un errore.</summary>
public enum ParseRelease
{
    /// <summary>Owner/fencing non più validi: nulla è stato modificato.</summary>
    Rejected,
    Requeued,
    Failed,
}

/// <summary>
/// Claim, lease, backoff e fence dei job Parse sulla stessa macchina a lease dei turni e dei run 001 (owner, LeaseUntil, FencingToken, AttemptCount,
/// DeadlineAt), ma in una corsia CPU separata: non conta nel limite AI globale, non occupa slot DGX e non usa il lock di claim AI.
/// Ogni istante è <c>clock_timestamp()</c> del database; ogni scrittura dell'owner è protetta da owner+fence e dalla vita del padre
/// (preparazione attiva e <c>EffectiveDeadlineAt</c> non passata): una FK da sola non basta. Usa la transazione EF corrente, se presente.
/// </summary>
public sealed class DocumentJobStore(AppDbContext db, IOptions<ExecutionOptions> options)
{
    /// <summary>Serializza i claim dei Parse fra istanze; distinto dal lock di claim AI.</summary>
    private const long ParseClaimLockKey = 0x6250_0029;

    private const string ExhaustedMessage = "Il parsing del documento non è terminato entro i limiti di tentativi o di tempo.";

    /// <summary>Idoneità al claim, valutata dal candidato e ricontrollata nella UPDATE (READ COMMITTED: lock di riga e rinnovi concorrenti).</summary>
    private const string EligibleSql = """
        j."Kind" = 'Parse'
        AND ((j."Status" = 'Queued' AND (j."NextAttemptAt" IS NULL OR j."NextAttemptAt" <= clk.t))
             OR (j."Status" = 'Running' AND j."LeaseUntil" <= clk.t))
        AND j."AttemptCount" < j."MaxAttempts"
        AND j."DeadlineAt" > clk.t
        AND p."EffectiveDeadlineAt" > clk.t
        AND p."Status" IN ('Queued', 'Parsing')
        """;

    private readonly ExecutionOptions _policy = options.Value;

    /// <summary>
    /// Fa fallire i Parse non più eseguibili (deadline propria o del padre, tentativi esauriti) insieme alla loro preparazione, poi prende in carico il
    /// prossimo Parse eleggibile in ordine di richiesta: Queued con NextAttemptAt raggiunto oppure Running con lease scaduto (reclaim).
    /// </summary>
    public async Task<ParseClaim?> ClaimNextParseAsync(string owner, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await using (var lockCommand = await CommandAsync("SELECT pg_advisory_xact_lock(@key)", ct, ("key", ParseClaimLockKey)))
        {
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        await using (var sweep = await CommandAsync(
            """
            WITH clk AS (SELECT clock_timestamp() AS t),
            dead AS (
                UPDATE "DocumentJobs" j SET
                    "Status" = 'Failed',
                    "ErrorCode" = CASE WHEN p."EffectiveDeadlineAt" <= clk.t THEN 'parent_deadline_exceeded'
                                       WHEN j."DeadlineAt" <= clk.t THEN 'deadline_exceeded'
                                       ELSE 'attempts_exhausted' END,
                    "ErrorMessage" = @message,
                    "CompletedAt" = clk.t,
                    "LeaseOwner" = NULL,
                    "LeaseUntil" = NULL,
                    "FencingToken" = j."FencingToken" + 1
                FROM clk, "DocumentPreparations" p
                WHERE p."Id" = j."PreparationId" AND j."Kind" = 'Parse'
                  AND (j."Status" = 'Queued' OR (j."Status" = 'Running' AND j."LeaseUntil" <= clk.t))
                  AND (p."EffectiveDeadlineAt" <= clk.t OR j."DeadlineAt" <= clk.t OR j."AttemptCount" >= j."MaxAttempts")
                RETURNING j."PreparationId", j."ErrorCode", j."ErrorMessage")
            UPDATE "DocumentPreparations" q SET "Status" = 'Failed', "ErrorCode" = dead."ErrorCode", "ErrorMessage" = dead."ErrorMessage"
            FROM dead
            WHERE q."Id" = dead."PreparationId" AND q."Status" IN ('Queued', 'Parsing')
            """,
            ct, ("message", ExhaustedMessage)))
        {
            await sweep.ExecuteNonQueryAsync(ct);
        }

        Guid? candidate;
        await using (var select = await CommandAsync(
            $"""
            WITH clk AS (SELECT clock_timestamp() AS t)
            SELECT j."Id" FROM "DocumentJobs" j JOIN "DocumentPreparations" p ON p."Id" = j."PreparationId", clk
            WHERE {EligibleSql}
            ORDER BY j."RequestedAt", j."Id"
            LIMIT 1
            """,
            ct))
        {
            candidate = await select.ExecuteScalarAsync(ct) is Guid id ? id : null;
        }

        ParseClaim? claim = null;
        if (candidate is { } jobId)
        {
            await using var update = await CommandAsync(
                $"""
                WITH clk AS (SELECT clock_timestamp() AS t)
                UPDATE "DocumentJobs" j SET
                    "Status" = 'Running',
                    "LeaseOwner" = @owner,
                    "LeaseUntil" = clk.t + make_interval(secs => @lease),
                    "FencingToken" = j."FencingToken" + 1,
                    "AttemptCount" = j."AttemptCount" + 1,
                    "StartedAt" = COALESCE(j."StartedAt", clk.t),
                    "NextAttemptAt" = NULL
                FROM clk, "DocumentPreparations" p
                WHERE j."Id" = @id AND p."Id" = j."PreparationId" AND {EligibleSql}
                RETURNING j."Id", j."PreparationId", j."ProjectId", p."DocumentVersionId", j."FencingToken", j."AttemptCount", j."MaxAttempts",
                          extract(epoch FROM (LEAST(j."DeadlineAt", p."EffectiveDeadlineAt") - clk.t))::float8, j."ConfigJson"::text
                """,
                ct, ("id", jobId), ("owner", owner), ("lease", _policy.LeaseDuration.TotalSeconds));
            await using (var reader = await update.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    claim = new ParseClaim(
                        reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), owner, reader.GetInt64(4), reader.GetInt32(5), reader.GetInt32(6),
                        TimeSpan.FromSeconds(reader.GetDouble(7)), reader.IsDBNull(8) ? null : reader.GetString(8));
                }
            }

            if (claim is not null)
            {
                // La preparazione entra in Parsing alla prima presa in carico; ai reclaim è già Parsing.
                await using var parsing = await CommandAsync(
                    "UPDATE \"DocumentPreparations\" SET \"Status\" = 'Parsing' WHERE \"Id\" = @id AND \"Status\" = 'Queued'", ct, ("id", claim.PreparationId));
                await parsing.ExecuteNonQueryAsync(ct);
            }
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return claim;
    }

    /// <summary>Estende LeaseUntil dell'owner corrente; le deadline non si spostano. False se owner/fence/padre non sono più validi.</summary>
    public async Task<bool> RenewAsync(ParseClaim claim, CancellationToken ct)
    {
        await using var command = await CommandAsync(
            """
            UPDATE "DocumentJobs" j SET "LeaseUntil" = clock_timestamp() + make_interval(secs => @lease)
            FROM "DocumentPreparations" p
            WHERE j."Id" = @id AND p."Id" = j."PreparationId"
              AND j."Status" = 'Running' AND j."LeaseOwner" = @owner AND j."FencingToken" = @fence
              AND j."LeaseUntil" > clock_timestamp() AND j."DeadlineAt" > clock_timestamp()
              AND p."EffectiveDeadlineAt" > clock_timestamp() AND p."Status" IN ('Queued', 'Parsing')
            """,
            ct, ("lease", _policy.LeaseDuration.TotalSeconds), ("id", claim.JobId), ("owner", claim.LeaseOwner), ("fence", claim.FencingToken));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Esito di un errore osservato dall'owner. Transitorio con tentativi e tempo residui (job e padre): Queued con NextAttemptAt (backoff 2/4 s,
    /// o Retry-After se maggiore), stessa deadline e stessa configurazione. Altrimenti Failed terminale e preparazione Failed con lo stesso codice,
    /// nella stessa istruzione: permanente con il codice dato, transitorio con attempts_exhausted, deadline_exceeded o parent_deadline_exceeded.
    /// </summary>
    public async Task<ParseRelease> ReleaseAfterFailureAsync(ParseClaim claim, ParserFailure failure, CancellationToken ct)
    {
        var delay = _policy.BackoffAfterAttempt(claim.AttemptCount);
        if (failure.RetryAfter is { } retryAfter && retryAfter > delay)
        {
            delay = retryAfter;
        }

        await using var command = await CommandAsync(
            """
            WITH clk AS (SELECT clock_timestamp() AS t),
            upd AS (
                UPDATE "DocumentJobs" x SET
                    "Status" = CASE WHEN d.retry THEN 'Queued' ELSE 'Failed' END,
                    "LeaseOwner" = NULL,
                    "LeaseUntil" = NULL,
                    "NextAttemptAt" = CASE WHEN d.retry THEN d.next ELSE x."NextAttemptAt" END,
                    "CompletedAt" = CASE WHEN d.retry THEN NULL ELSE d.now END,
                    "ErrorCode" = CASE WHEN d.retry THEN NULL ELSE d.code END,
                    "ErrorMessage" = CASE WHEN d.retry THEN NULL WHEN @transient THEN @exhausted ELSE @message END
                FROM (
                    SELECT t."Id", clk.t AS now, clk.t + make_interval(secs => @delay) AS next,
                           (@transient AND t."AttemptCount" < t."MaxAttempts"
                                AND clk.t + make_interval(secs => @delay) < LEAST(t."DeadlineAt", p."EffectiveDeadlineAt")) AS retry,
                           CASE WHEN NOT @transient THEN @code
                                WHEN clk.t >= p."EffectiveDeadlineAt" THEN 'parent_deadline_exceeded'
                                WHEN clk.t + make_interval(secs => @delay) >= LEAST(t."DeadlineAt", p."EffectiveDeadlineAt") THEN 'deadline_exceeded'
                                ELSE 'attempts_exhausted' END AS code
                    FROM "DocumentJobs" t JOIN "DocumentPreparations" p ON p."Id" = t."PreparationId", clk
                    WHERE t."Id" = @id AND t."Status" = 'Running' AND t."LeaseOwner" = @owner AND t."FencingToken" = @fence
                ) d
                WHERE x."Id" = d."Id" AND x."Status" = 'Running' AND x."LeaseOwner" = @owner AND x."FencingToken" = @fence
                RETURNING x."PreparationId", x."Status", x."NextAttemptAt", x."ErrorCode", x."ErrorMessage"),
            prep AS (
                UPDATE "DocumentPreparations" q SET "Status" = 'Failed', "ErrorCode" = upd."ErrorCode", "ErrorMessage" = upd."ErrorMessage"
                FROM upd
                WHERE q."Id" = upd."PreparationId" AND upd."Status" = 'Failed' AND q."Status" IN ('Queued', 'Parsing')
                RETURNING q."Id")
            SELECT "Status" FROM upd
            """,
            ct,
            ("transient", failure.IsTransient), ("delay", delay.TotalSeconds), ("code", failure.Code), ("message", $"Parsing non riuscito ({failure.Code})."),
            ("exhausted", ExhaustedMessage), ("id", claim.JobId), ("owner", claim.LeaseOwner), ("fence", claim.FencingToken));
        var status = await command.ExecuteScalarAsync(ct) as string;
        return status switch
        {
            "Queued" => ParseRelease.Requeued,
            "Failed" => ParseRelease.Failed,
            _ => ParseRelease.Rejected,
        };
    }

    /// <summary>
    /// Sotto la transazione del chiamante: blocca preparazione e job e conferma che l'owner ha ancora l'autorità (Running, owner, fence, lease e
    /// deadline del job non scaduti, padre attivo e con deadline effettiva non passata). False = il risultato è tardivo o stale e non va pubblicato.
    /// </summary>
    public async Task<bool> LockForCommitAsync(ParseClaim claim, CancellationToken ct)
    {
        await using var command = await CommandAsync(
            """
            SELECT 1 FROM "DocumentPreparations" p JOIN "DocumentJobs" j ON j."PreparationId" = p."Id"
            WHERE j."Id" = @id AND j."Status" = 'Running' AND j."LeaseOwner" = @owner AND j."FencingToken" = @fence
              AND j."LeaseUntil" > clock_timestamp() AND j."DeadlineAt" > clock_timestamp()
              AND p."EffectiveDeadlineAt" > clock_timestamp() AND p."Status" IN ('Queued', 'Parsing')
            FOR UPDATE OF p, j
            """,
            ct, ("id", claim.JobId), ("owner", claim.LeaseOwner), ("fence", claim.FencingToken));
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Chiude il job Completed con l'hash del risultato e aggiorna la preparazione; da chiamare dopo <see cref="LockForCommitAsync"/> nella stessa transazione.</summary>
    public async Task CompleteParseAsync(
        ParseClaim claim, string resultHash, PreparationStatus preparationStatus, string parserVersion, string configHash, string manifestHash, CancellationToken ct)
    {
        await using (var job = await CommandAsync(
            """
            UPDATE "DocumentJobs" SET "Status" = 'Completed', "ResultHash" = @hash, "CompletedAt" = clock_timestamp(), "LeaseOwner" = NULL, "LeaseUntil" = NULL
            WHERE "Id" = @id
            """,
            ct, ("hash", resultHash), ("id", claim.JobId)))
        {
            await job.ExecuteNonQueryAsync(ct);
        }

        await using var preparation = await CommandAsync(
            """
            UPDATE "DocumentPreparations" SET "Status" = @status, "ParserVersion" = @parser, "ConfigHash" = @config, "ManifestHash" = @manifest
            WHERE "Id" = @id
            """,
            ct, ("status", preparationStatus.ToString()), ("parser", parserVersion), ("config", configHash), ("manifest", manifestHash), ("id", claim.PreparationId));
        await preparation.ExecuteNonQueryAsync(ct);
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
