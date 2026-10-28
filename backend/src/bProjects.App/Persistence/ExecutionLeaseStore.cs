using bProjects.App.AI;
using bProjects.App.Analysis;
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

/// <summary>Lavoro AI preso in carico: esattamente uno fra un ChatTurn e un ProcessingRun di Analysis.</summary>
public sealed record WorkClaim(ChatTurnClaim? Turn, AnalysisRunClaim? Run);

/// <summary>
/// Claim, lease e transizioni dei ChatTurn e dei ProcessingRun di Analysis su PostgreSQL (contracts/ai-workflow.md §2).
/// Ogni istante e ogni scadenza sono calcolati dal database (<c>clock_timestamp()</c>); ogni scrittura di un owner è
/// protetta da owner + FencingToken nella stessa istruzione, quindi un vecchio worker non può mutare il lavoro.
/// I due tipi condividono limite AI globale e lock di claim; le transizioni hanno la stessa semantica.
/// Usa la transazione EF corrente, se presente, così il commit del risultato può comporsi con la transizione.
/// </summary>
public sealed class ExecutionLeaseStore(AppDbContext db, IOptions<ExecutionOptions> options)
{
    /// <summary>Esecuzioni AI contemporanee ammesse: una sola nell'istanza applicativa iniziale.</summary>
    public const int MaxConcurrentRuns = 1;

    /// <summary>Serializza i claim di tutte le istanze: garantisce il limite AI globale e la FIFO senza race.</summary>
    private const long ClaimLockKey = 0x6250_0014;

    private const string ExhaustedMessage = "Il modello interno non ha risposto entro i limiti di tentativi o di tempo.";

    private readonly ExecutionOptions _policy = options.Value;

    private enum Table
    {
        Turns,
        Runs,
    }

    /// <summary>I nomi di tabella sono costanti interne (mai input): la query resta parametrica per ogni valore.</summary>
    private static string Sql(Table table, string template) => template
        .Replace("{TABLE}", table == Table.Turns ? "\"ChatTurns\"" : "\"ProcessingRuns\"", StringComparison.Ordinal)
        .Replace("{KIND}", table == Table.Turns ? "" : " AND x.\"Kind\" = 'Analysis'", StringComparison.Ordinal)
        .Replace("{UPDATED}", table == Table.Turns ? "" : ",\n                \"UpdatedAt\" = clock_timestamp()", StringComparison.Ordinal);

    /// <summary>Claim dei soli ChatTurn: <see cref="ClaimNextWorkAsync"/> senza i ProcessingRun (il limite AI globale li conta comunque).</summary>
    public async Task<ChatTurnClaim?> ClaimNextAsync(string owner, CancellationToken ct) =>
        (await ClaimNextWorkAsync(owner, turns: true, runs: false, ct))?.Turn;

    /// <summary>
    /// Fa fallire i lavori non più eseguibili (deadline o tentativi esauriti) e prende in carico il prossimo lavoro
    /// ammissibile fra ChatTurn e ProcessingRun di Analysis, in ordine di richiesta (a parità, il turno). Per i turni:
    /// il primo non terminale della propria conversazione; per i run: Queued con NextAttemptAt raggiunto oppure Running
    /// con lease scaduto (reclaim). Un solo lavoro può avere un lease valido nell'istanza, qualunque sia il tipo.
    /// </summary>
    public async Task<WorkClaim?> ClaimNextWorkAsync(string owner, bool turns, bool runs, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;

        await using (var lockCommand = await CommandAsync("SELECT pg_advisory_xact_lock(@key)", ct, ("key", ClaimLockKey)))
        {
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        if (turns)
        {
            await SweepAsync(Table.Turns, ct);
        }

        if (runs)
        {
            await SweepAsync(Table.Runs, ct);
        }

        WorkClaim? claim = null;
        if (await RunningCountAsync(ct) < MaxConcurrentRuns)
        {
            var turnCandidate = turns ? await TurnCandidateAsync(ct) : null;
            var runCandidate = runs ? await RunCandidateAsync(ct) : null;
            if (turnCandidate is { } t && (runCandidate is not { } r || t.RequestedAt <= r.RequestedAt))
            {
                claim = await ClaimTurnAsync(t.Id, owner, ct) is { } turn ? new WorkClaim(turn, null) : null;
            }
            else if (runCandidate is { } run)
            {
                claim = await ClaimRunAsync(run.Id, owner, ct) is { } analysis ? new WorkClaim(null, analysis) : null;
            }
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return claim;
    }

    private async Task SweepAsync(Table table, CancellationToken ct)
    {
        await using var sweep = await CommandAsync(
            Sql(table, """
            WITH clk AS (SELECT clock_timestamp() AS t)
            UPDATE {TABLE} x SET
                "Status" = 'Failed',
                "ErrorCode" = CASE WHEN x."DeadlineAt" <= clk.t THEN 'deadline_exceeded' ELSE 'attempts_exhausted' END,
                "ErrorMessage" = @message,
                "CompletedAt" = clk.t,
                "LeaseOwner" = NULL,
                "LeaseUntil" = NULL,
                "FencingToken" = x."FencingToken" + 1{UPDATED}
            FROM clk
            WHERE (x."Status" = 'Queued' OR (x."Status" = 'Running' AND x."LeaseUntil" <= clk.t))
              AND (x."DeadlineAt" <= clk.t OR x."AttemptCount" >= x."MaxAttempts"){KIND}
            """),
            ct,
            ("message", ExhaustedMessage));
        await sweep.ExecuteNonQueryAsync(ct);
    }

    private async Task<long> RunningCountAsync(CancellationToken ct)
    {
        await using var command = await CommandAsync(
            """
            SELECT (SELECT count(*) FROM "ChatTurns" WHERE "Status" = 'Running' AND "LeaseUntil" > clock_timestamp())
                 + (SELECT count(*) FROM "ProcessingRuns" WHERE "Status" = 'Running' AND "LeaseUntil" > clock_timestamp())
            """,
            ct);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<(Guid Id, DateTimeOffset RequestedAt)?> TurnCandidateAsync(CancellationToken ct)
    {
        await using var command = await CommandAsync(
            """
            WITH clk AS (SELECT clock_timestamp() AS t)
            SELECT c."Id", c."RequestedAt"
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
            ORDER BY c."RequestedAt", c."SourceCutoffSequence"
            LIMIT 1
            """,
            ct);
        return await ReadCandidateAsync(command, ct);
    }

    private async Task<(Guid Id, DateTimeOffset RequestedAt)?> RunCandidateAsync(CancellationToken ct)
    {
        await using var command = await CommandAsync(
            """
            WITH clk AS (SELECT clock_timestamp() AS t)
            SELECT r."Id", r."RequestedAt"
            FROM "ProcessingRuns" r, clk
            WHERE r."Kind" = 'Analysis'
              AND ((r."Status" = 'Queued' AND (r."NextAttemptAt" IS NULL OR r."NextAttemptAt" <= clk.t))
                   OR (r."Status" = 'Running' AND r."LeaseUntil" <= clk.t))
              AND r."AttemptCount" < r."MaxAttempts"
              AND r."DeadlineAt" > clk.t
            ORDER BY r."RequestedAt", r."Id"
            LIMIT 1
            """,
            ct);
        return await ReadCandidateAsync(command, ct);
    }

    private static async Task<(Guid, DateTimeOffset)?> ReadCandidateAsync(NpgsqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1)) : null;
    }

    private async Task<ChatTurnClaim?> ClaimTurnAsync(Guid id, string owner, CancellationToken ct)
    {
        await using var command = await CommandAsync(
            """
            WITH clk AS (SELECT clock_timestamp() AS t)
            UPDATE "ChatTurns" t SET
                "Status" = 'Running',
                "LeaseOwner" = @owner,
                "LeaseUntil" = clk.t + make_interval(secs => @lease),
                "FencingToken" = t."FencingToken" + 1,
                "AttemptCount" = t."AttemptCount" + 1,
                "StartedAt" = COALESCE(t."StartedAt", clk.t),
                "NextAttemptAt" = NULL,
                "SessionRevisionAtStart" = (SELECT v."ChatSessionRevision" FROM "Conversations" v WHERE v."Id" = t."ConversationId")
            FROM clk
            WHERE t."Id" = @id
              -- Rivalutazione dopo l'attesa sul lock di riga (READ COMMITTED): solo l'Id verrebbe ricontrollato,
              -- quindi un turno nel frattempo Completed/rinnovato verrebbe riportato a Running. Stessa idoneità del candidato.
              AND ((t."Status" = 'Queued' AND (t."NextAttemptAt" IS NULL OR t."NextAttemptAt" <= clk.t))
                   OR (t."Status" = 'Running' AND t."LeaseUntil" <= clk.t))
              AND t."AttemptCount" < t."MaxAttempts"
              AND t."DeadlineAt" > clk.t
            RETURNING t."Id", t."ConversationId", t."TriggerMessageId", t."SourceCutoffSequence",
                      t."FencingToken", t."AttemptCount", t."LeaseUntil", t."DeadlineAt"
            """,
            ct,
            ("id", id), ("owner", owner), ("lease", _policy.LeaseDuration.TotalSeconds));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ChatTurnClaim(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetInt64(3),
                owner,
                reader.GetInt64(4),
                reader.GetInt32(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7))
            : null;
    }

    private async Task<AnalysisRunClaim?> ClaimRunAsync(Guid id, string owner, CancellationToken ct)
    {
        await using var command = await CommandAsync(
            """
            WITH clk AS (SELECT clock_timestamp() AS t)
            UPDATE "ProcessingRuns" t SET
                "Status" = 'Running',
                "LeaseOwner" = @owner,
                "LeaseUntil" = clk.t + make_interval(secs => @lease),
                "FencingToken" = t."FencingToken" + 1,
                "AttemptCount" = t."AttemptCount" + 1,
                "StartedAt" = COALESCE(t."StartedAt", clk.t),
                "NextAttemptAt" = NULL,
                "UpdatedAt" = clk.t
            FROM clk
            WHERE t."Id" = @id AND t."Kind" = 'Analysis'
              -- Come per i turni: l'idoneità si ricontrolla nella UPDATE, perché un commit concorrente (Completed) o un
              -- rinnovo avvenuti durante l'attesa sul lock di riga non devono essere sovrascritti.
              AND ((t."Status" = 'Queued' AND (t."NextAttemptAt" IS NULL OR t."NextAttemptAt" <= clk.t))
                   OR (t."Status" = 'Running' AND t."LeaseUntil" <= clk.t))
              AND t."AttemptCount" < t."MaxAttempts"
              AND t."DeadlineAt" > clk.t
            RETURNING t."Id", t."FencingToken", t."AttemptCount"
            """,
            ct,
            ("id", id), ("owner", owner), ("lease", _policy.LeaseDuration.TotalSeconds));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new AnalysisRunClaim(reader.GetGuid(0), owner, reader.GetInt64(1), reader.GetInt32(2)) : null;
    }

    /// <summary>Estende LeaseUntil dell'owner corrente; DeadlineAt non si sposta. False se owner/fencing non sono più validi o la deadline è passata.</summary>
    public Task<bool> RenewAsync(ChatTurnClaim claim, CancellationToken ct) =>
        RenewAsync(Table.Turns, claim.TurnId, claim.LeaseOwner, claim.FencingToken, ct);

    /// <summary>Come <see cref="RenewAsync(ChatTurnClaim, CancellationToken)"/> per un ProcessingRun di Analysis.</summary>
    public Task<bool> RenewAsync(AnalysisRunClaim claim, CancellationToken ct) =>
        RenewAsync(Table.Runs, claim.RunId, claim.LeaseOwner, claim.FencingToken, ct);

    private async Task<bool> RenewAsync(Table table, Guid id, string owner, long fence, CancellationToken ct)
    {
        await using var command = await CommandAsync(
            Sql(table, """
            UPDATE {TABLE} SET "LeaseUntil" = clock_timestamp() + make_interval(secs => @lease){UPDATED}
            WHERE "Id" = @id AND "Status" = 'Running' AND "LeaseOwner" = @owner AND "FencingToken" = @fence
              AND "LeaseUntil" > clock_timestamp() AND "DeadlineAt" > clock_timestamp()
            """),
            ct,
            ("lease", _policy.LeaseDuration.TotalSeconds), ("id", id), ("owner", owner), ("fence", fence));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Esito di un errore osservato dall'owner. Transitorio con tentativi e tempo residui: Queued con NextAttemptAt
    /// (backoff, o Retry-After se maggiore). Altrimenti Failed terminale: permanente con <paramref name="permanentCode"/>,
    /// transitorio con attempts_exhausted/deadline_exceeded.
    /// </summary>
    public Task<TurnRelease> ReleaseAfterFailureAsync(
        ChatTurnClaim claim, ModelFailure failure, string permanentCode, string permanentMessage, CancellationToken ct) =>
        ReleaseAfterFailureAsync(Table.Turns, claim.TurnId, claim.LeaseOwner, claim.FencingToken, claim.AttemptCount, failure, permanentCode, permanentMessage, ct);

    /// <summary>Come <see cref="ReleaseAfterFailureAsync(ChatTurnClaim, ModelFailure, string, string, CancellationToken)"/> per un ProcessingRun di Analysis.</summary>
    public Task<TurnRelease> ReleaseAfterFailureAsync(
        AnalysisRunClaim claim, ModelFailure failure, string permanentCode, string permanentMessage, CancellationToken ct) =>
        ReleaseAfterFailureAsync(Table.Runs, claim.RunId, claim.LeaseOwner, claim.FencingToken, claim.AttemptCount, failure, permanentCode, permanentMessage, ct);

    private async Task<TurnRelease> ReleaseAfterFailureAsync(
        Table table, Guid id, string owner, long fence, int attemptCount, ModelFailure failure, string permanentCode, string permanentMessage, CancellationToken ct)
    {
        var delay = _policy.BackoffAfterAttempt(attemptCount);
        if (failure.RetryAfter is { } retryAfter && retryAfter > delay)
        {
            delay = retryAfter;
        }

        await using var command = await CommandAsync(
            Sql(table, """
            WITH clk AS (SELECT clock_timestamp() AS t)
            UPDATE {TABLE} x SET
                "Status" = CASE WHEN d.retry THEN 'Queued' ELSE 'Failed' END,
                "LeaseOwner" = NULL,
                "LeaseUntil" = NULL,
                "NextAttemptAt" = CASE WHEN d.retry THEN d.next ELSE x."NextAttemptAt" END,
                "CompletedAt" = CASE WHEN d.retry THEN NULL ELSE d.now END,
                "ErrorCode" = CASE WHEN d.retry THEN NULL ELSE d.code END,
                "ErrorMessage" = CASE WHEN d.retry THEN NULL WHEN @transient THEN @exhausted ELSE @message END{UPDATED}
            FROM (
                SELECT t."Id", clk.t AS now, clk.t + make_interval(secs => @delay) AS next,
                       (@transient AND t."AttemptCount" < t."MaxAttempts"
                            AND clk.t + make_interval(secs => @delay) < t."DeadlineAt") AS retry,
                       CASE WHEN NOT @transient THEN @code
                            WHEN clk.t + make_interval(secs => @delay) >= t."DeadlineAt" THEN 'deadline_exceeded'
                            ELSE 'attempts_exhausted' END AS code
                FROM {TABLE} t, clk
                WHERE t."Id" = @id AND t."Status" = 'Running' AND t."LeaseOwner" = @owner AND t."FencingToken" = @fence
            ) d
            WHERE x."Id" = d."Id" AND x."Status" = 'Running' AND x."LeaseOwner" = @owner AND x."FencingToken" = @fence
            RETURNING x."Status", x."NextAttemptAt", x."ErrorCode"
            """),
            ct,
            ("transient", failure.IsTransient), ("delay", delay.TotalSeconds), ("code", permanentCode), ("message", permanentMessage),
            ("exhausted", ExhaustedMessage), ("id", id), ("owner", owner), ("fence", fence));

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
