using bProjects.App.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.App.Persistence;

/// <summary>Turno preso in carico: owner e fencing token identificano in modo esclusivo questo tentativo.</summary>
public sealed record ChatTurnClaim(
    Guid TurnId,
    Guid ConversationId,
    Guid TriggerMessageId,
    long SourceCutoffSequence,
    string LeaseOwner,
    long FencingToken,
    int AttemptCount,
    DateTimeOffset LeaseUntil,
    DateTimeOffset DeadlineAt);

public enum TurnTransition
{
    /// <summary>Owner/fencing non più validi (lease perso o turno già terminale): nulla è stato modificato.</summary>
    Rejected,
    Requeued,
    Failed,
}

public readonly record struct TurnRelease(TurnTransition Outcome, DateTimeOffset? NextAttemptAt = null, string? ErrorCode = null);

/// <summary>
/// Claim, lease e transizioni dei ChatTurn su PostgreSQL (contracts/ai-workflow.md §2). Ogni istante e ogni
/// scadenza sono calcolati dal database (<c>clock_timestamp()</c>); ogni scrittura di un owner è protetta da
/// owner + FencingToken nella stessa istruzione, quindi un vecchio worker non può mutare il turno.
/// Usa la transazione EF corrente, se presente, così T018 può comporre il commit del risultato.
/// </summary>
public sealed class ExecutionLeaseStore(AppDbContext db, IOptions<ExecutionOptions> options)
{
    /// <summary>Esecuzioni AI contemporanee ammesse: una sola nell'istanza applicativa iniziale.</summary>
    public const int MaxConcurrentRuns = 1;

    /// <summary>Serializza i claim di tutte le istanze: garantisce il limite AI globale e la FIFO senza race.</summary>
    private const long ClaimLockKey = 0x6250_0014;

    private const string ExhaustedMessage = "Il modello interno non ha risposto entro i limiti di tentativi o di tempo.";

    private readonly ExecutionOptions _policy = options.Value;

    /// <summary>
    /// Fa fallire i turni non più eseguibili (deadline o tentativi esauriti) e prende in carico il prossimo turno
    /// ammissibile: il primo non terminale della propria conversazione, Queued con NextAttemptAt raggiunto oppure
    /// Running con lease scaduto (reclaim), quando nessun lease valido esiste nell'istanza.
    /// </summary>
    public async Task<ChatTurnClaim?> ClaimNextAsync(string owner, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;

        await using (var lockCommand = await CommandAsync("SELECT pg_advisory_xact_lock(@key)", ct, ("key", ClaimLockKey)))
        {
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        await using (var sweep = await CommandAsync(
            """
            WITH clk AS (SELECT clock_timestamp() AS t)
            UPDATE "ChatTurns" x SET
                "Status" = 'Failed',
                "ErrorCode" = CASE WHEN x."DeadlineAt" <= clk.t THEN 'deadline_exceeded' ELSE 'attempts_exhausted' END,
                "ErrorMessage" = @message,
                "CompletedAt" = clk.t,
                "LeaseOwner" = NULL,
                "LeaseUntil" = NULL,
                "FencingToken" = x."FencingToken" + 1
            FROM clk
            WHERE (x."Status" = 'Queued' OR (x."Status" = 'Running' AND x."LeaseUntil" <= clk.t))
              AND (x."DeadlineAt" <= clk.t OR x."AttemptCount" >= x."MaxAttempts")
            """,
            ct,
            ("message", ExhaustedMessage)))
        {
            await sweep.ExecuteNonQueryAsync(ct);
        }

        ChatTurnClaim? claim = null;
        await using (var command = await CommandAsync(
            """
            WITH clk AS (SELECT clock_timestamp() AS t),
            candidate AS (
                SELECT c."Id"
                FROM "ChatTurns" c, clk
                WHERE ((c."Status" = 'Queued' AND (c."NextAttemptAt" IS NULL OR c."NextAttemptAt" <= clk.t))
                       OR (c."Status" = 'Running' AND c."LeaseUntil" <= clk.t))
                  AND c."AttemptCount" < c."MaxAttempts"
                  AND c."DeadlineAt" > clk.t
                  AND NOT EXISTS (
                        SELECT 1 FROM "ChatTurns" p
                        WHERE p."ConversationId" = c."ConversationId"
                          AND p."Status" IN ('Queued', 'Running')
                          AND p."SourceCutoffSequence" < c."SourceCutoffSequence")
                  AND (SELECT count(*) FROM "ChatTurns" r WHERE r."Status" = 'Running' AND r."LeaseUntil" > clk.t) < @maxRunning
                ORDER BY c."RequestedAt", c."SourceCutoffSequence"
                LIMIT 1
            )
            UPDATE "ChatTurns" t SET
                "Status" = 'Running',
                "LeaseOwner" = @owner,
                "LeaseUntil" = clk.t + make_interval(secs => @lease),
                "FencingToken" = t."FencingToken" + 1,
                "AttemptCount" = t."AttemptCount" + 1,
                "StartedAt" = COALESCE(t."StartedAt", clk.t),
                "NextAttemptAt" = NULL,
                "SessionRevisionAtStart" = (SELECT v."ChatSessionRevision" FROM "Conversations" v WHERE v."Id" = t."ConversationId")
            FROM candidate, clk
            WHERE t."Id" = candidate."Id"
            RETURNING t."Id", t."ConversationId", t."TriggerMessageId", t."SourceCutoffSequence",
                      t."FencingToken", t."AttemptCount", t."LeaseUntil", t."DeadlineAt"
            """,
            ct,
            ("owner", owner),
            ("lease", _policy.LeaseDuration.TotalSeconds),
            ("maxRunning", MaxConcurrentRuns)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                claim = new ChatTurnClaim(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetInt64(3),
                    owner,
                    reader.GetInt64(4),
                    reader.GetInt32(5),
                    reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetFieldValue<DateTimeOffset>(7));
            }
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return claim;
    }

    /// <summary>Estende LeaseUntil dell'owner corrente; DeadlineAt non si sposta. False se owner/fencing non sono più validi o la deadline è passata.</summary>
    public async Task<bool> RenewAsync(ChatTurnClaim claim, CancellationToken ct)
    {
        await using var command = await CommandAsync(
            """
            UPDATE "ChatTurns" SET "LeaseUntil" = clock_timestamp() + make_interval(secs => @lease)
            WHERE "Id" = @id AND "Status" = 'Running' AND "LeaseOwner" = @owner AND "FencingToken" = @fence
              AND "LeaseUntil" > clock_timestamp() AND "DeadlineAt" > clock_timestamp()
            """,
            ct,
            ("lease", _policy.LeaseDuration.TotalSeconds), ("id", claim.TurnId), ("owner", claim.LeaseOwner), ("fence", claim.FencingToken));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Esito di un errore osservato dall'owner. Transitorio con tentativi e tempo residui: Queued con NextAttemptAt
    /// (backoff, o Retry-After se maggiore). Altrimenti Failed terminale: permanente con <paramref name="permanentCode"/>,
    /// transitorio con attempts_exhausted/deadline_exceeded.
    /// </summary>
    public async Task<TurnRelease> ReleaseAfterFailureAsync(
        ChatTurnClaim claim, ModelFailure failure, string permanentCode, string permanentMessage, CancellationToken ct)
    {
        var delay = _policy.BackoffAfterAttempt(claim.AttemptCount);
        if (failure.RetryAfter is { } retryAfter && retryAfter > delay)
        {
            delay = retryAfter;
        }

        await using var command = await CommandAsync(
            """
            WITH clk AS (SELECT clock_timestamp() AS t)
            UPDATE "ChatTurns" x SET
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
                            AND clk.t + make_interval(secs => @delay) < t."DeadlineAt") AS retry,
                       CASE WHEN NOT @transient THEN @code
                            WHEN clk.t + make_interval(secs => @delay) >= t."DeadlineAt" THEN 'deadline_exceeded'
                            ELSE 'attempts_exhausted' END AS code
                FROM "ChatTurns" t, clk
                WHERE t."Id" = @id AND t."Status" = 'Running' AND t."LeaseOwner" = @owner AND t."FencingToken" = @fence
            ) d
            WHERE x."Id" = d."Id" AND x."Status" = 'Running' AND x."LeaseOwner" = @owner AND x."FencingToken" = @fence
            RETURNING x."Status", x."NextAttemptAt", x."ErrorCode"
            """,
            ct,
            ("transient", failure.IsTransient), ("delay", delay.TotalSeconds), ("code", permanentCode), ("message", permanentMessage),
            ("exhausted", ExhaustedMessage), ("id", claim.TurnId), ("owner", claim.LeaseOwner), ("fence", claim.FencingToken));

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new TurnRelease(TurnTransition.Rejected);
        }

        return reader.GetString(0) == "Queued"
            ? new TurnRelease(TurnTransition.Requeued, reader.GetFieldValue<DateTimeOffset>(1))
            : new TurnRelease(TurnTransition.Failed, ErrorCode: reader.GetString(2));
    }

    /// <summary>Failed terminale deciso dall'owner (errore permanente). False se owner/fencing non sono più validi.</summary>
    public async Task<bool> FailAsync(ChatTurnClaim claim, string errorCode, string errorMessage, CancellationToken ct)
    {
        await using var command = await CommandAsync(
            """
            UPDATE "ChatTurns" SET "Status" = 'Failed', "ErrorCode" = @code, "ErrorMessage" = @message,
                "CompletedAt" = clock_timestamp(), "LeaseOwner" = NULL, "LeaseUntil" = NULL
            WHERE "Id" = @id AND "Status" = 'Running' AND "LeaseOwner" = @owner AND "FencingToken" = @fence
            """,
            ct,
            ("code", errorCode), ("message", errorMessage), ("id", claim.TurnId), ("owner", claim.LeaseOwner), ("fence", claim.FencingToken));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Transizione protetta verso Completed. Non crea la risposta: <paramref name="resultMessageId"/> è il messaggio
    /// assistant già scritto da chi compone il commit (T018) nella stessa transazione. False se owner/fencing non
    /// sono più validi: il chiamante deve annullare la transazione.
    /// </summary>
    public async Task<bool> CompleteAsync(ChatTurnClaim claim, Guid resultMessageId, CancellationToken ct)
    {
        await using var command = await CommandAsync(
            """
            UPDATE "ChatTurns" SET "Status" = 'Completed', "ResultMessageId" = @result,
                "CompletedAt" = clock_timestamp(), "LeaseOwner" = NULL, "LeaseUntil" = NULL
            WHERE "Id" = @id AND "Status" = 'Running' AND "LeaseOwner" = @owner AND "FencingToken" = @fence
              AND "LeaseUntil" > clock_timestamp() AND "DeadlineAt" > clock_timestamp()
            """,
            ct,
            ("result", resultMessageId), ("id", claim.TurnId), ("owner", claim.LeaseOwner), ("fence", claim.FencingToken));
        return await command.ExecuteNonQueryAsync(ct) == 1;
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
