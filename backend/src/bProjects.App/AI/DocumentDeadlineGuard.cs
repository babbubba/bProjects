using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace bProjects.App.AI;

// Guard dell'autorità temporale corrente (002 T118; Plan §Autorità temporale, §Raccordo dispatcher 001).
// Un'operazione che scrive o prolunga lavoro (claim, rinnovo, rilascio, commit, checkpoint, invio) è ammessa solo se, sotto il clock del
// database e (dove serve) il lock della riga: root/figlio sono attivi, owner/fence/lease coincidono e now < min(DeadlineAt locale,
// EffectiveDeadlineAt del padre). L'autorità si rilegge sempre dalle righe correnti: un vecchio claim o timer non concede né toglie nulla
// e nessuna operazione sposta una scadenza (il solo allungamento è la CAS di promozione, T047). Questo tipo non promuove e non pianifica.

/// <summary>Perché un'operazione non è ammessa. L'ordine dell'enum è l'ordine di valutazione.</summary>
public enum DeadlineDenial
{
    None,

    /// <summary>Riga assente.</summary>
    Missing,

    /// <summary>Root o figlio già in uno stato terminale (Completed/Failed): nessuna autorità.</summary>
    Terminal,

    /// <summary>Non Running (es. Queued senza lease).</summary>
    NotRunning,

    /// <summary>Owner o fencing token non coincidono con quelli correnti.</summary>
    Fenced,
    LeaseExpired,

    /// <summary>Scaduta la deadline del root, oppure quella locale del figlio.</summary>
    DeadlineExceeded,

    /// <summary>Il padre del figlio non è più attivo.</summary>
    ParentNotActive,

    /// <summary>Scaduta EffectiveDeadlineAt del padre del figlio.</summary>
    ParentDeadlineExceeded,
}

/// <summary>Stato del lavoro per un committer: Completed (già concluso), Valid (può scrivere) o Stale (lease perso, fence vecchio, scaduto o assente).</summary>
public enum RootCommitState
{
    Completed,
    Valid,
    Stale,
    Missing,
}

/// <summary>Esito della valutazione: <see cref="Until"/> = min(deadline locale, EffectiveDeadlineAt del padre) quando ammessa; l'istante <see cref="Now"/> è del database.</summary>
public sealed record DeadlineAuthority(
    bool Allowed, DeadlineDenial Denial, DateTimeOffset Now, DateTimeOffset? Until, DateTimeOffset? RootEffectiveDeadlineAt, string? Status = null)
{
    /// <summary>Tempo residuo prima di <see cref="Until"/>; zero se non ammessa.</summary>
    public TimeSpan Remaining => Allowed && Until is { } until && until > Now ? until - Now : TimeSpan.Zero;
}

public static class DocumentDeadlineGuard
{
    /// <summary>Stati della preparazione padre in cui un job Parse può operare.</summary>
    public const string ParseParentStatuses = "'Queued', 'Parsing'";

    /// <summary>Stati della preparazione padre in cui un job di estrazione/aggregazione può operare.</summary>
    public const string WorkParentStatuses = "'Queued', 'Parsing', 'Extracting'";

    private const string Clock = "clock_timestamp()";

    // ------------------------------------------------------------------ frammenti SQL condivisi (nessun input: alias e stati sono costanti interne)

    /// <summary>Scadenza corrente di un ChatTurn/ProcessingRun: il minore fra DeadlineAt (proiezione legacy) ed EffectiveDeadlineAt (autorità documentale, se presente).</summary>
    public static string Until(string alias) =>
        $"LEAST({alias}.\"DeadlineAt\", COALESCE({alias}.\"EffectiveDeadlineAt\", {alias}.\"DeadlineAt\"))";

    /// <summary>Root non scaduto: la scadenza corrente è nel futuro del clock indicato.</summary>
    public static string RootAlive(string alias, string now = Clock) => $"{Until(alias)} > {now}";

    /// <summary>
    /// Come <see cref="RootAlive"/> ma solo per i root con campi del regime (EffectiveDeadlineAt valorizzato): i root della 001 non sono
    /// guardati (il checkpoint 001 non ha mai verificato la deadline).
    /// </summary>
    public static string RegimeRootAlive(string alias, string now = Clock) =>
        $"({alias}.\"EffectiveDeadlineAt\" IS NULL OR ({alias}.\"DeadlineAt\" > {now} AND {alias}.\"EffectiveDeadlineAt\" > {now}))";

    /// <summary>Figlio attivo: deadline locale e quella effettiva del padre nel futuro, padre in uno stato ammesso.</summary>
    public static string ChildAlive(string job, string parent, string parentStatuses = WorkParentStatuses, string now = Clock) =>
        $"{job}.\"DeadlineAt\" > {now} AND {parent}.\"EffectiveDeadlineAt\" > {now} AND {parent}.\"Status\" IN ({parentStatuses})";

    // ------------------------------------------------------------------ valutazione

    /// <summary>
    /// Valuta l'autorità dell'owner sotto il clock del database. Con <paramref name="lockRow"/> blocca la riga (FOR UPDATE) nella transazione
    /// corrente, così la decisione e la scrittura successiva sono un'unica sezione critica. Non scrive nulla.
    /// </summary>
    public static async Task<DeadlineAuthority> EvaluateAsync(
        AppDbContext db, WorkOwnerKind kind, Guid id, string leaseOwner, long fence, bool lockRow, CancellationToken ct)
    {
        if (lockRow && db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Il lock della riga richiede una transazione.");
        }

        var lockSql = lockRow ? " FOR UPDATE OF x" : "";
        var sql = kind switch
        {
            WorkOwnerKind.DocumentJob => $"""
                SELECT x."Status", x."LeaseOwner", x."FencingToken", x."LeaseUntil", x."DeadlineAt", p."Status", p."EffectiveDeadlineAt", clock_timestamp()
                FROM "DocumentJobs" x JOIN "DocumentPreparations" p ON p."Id" = x."PreparationId"
                WHERE x."Id" = @id{lockSql}
                """,
            _ => $"""
                SELECT x."Status", x."LeaseOwner", x."FencingToken", x."LeaseUntil", x."DeadlineAt", NULL::text, x."EffectiveDeadlineAt", clock_timestamp()
                FROM "{(kind == WorkOwnerKind.ChatTurn ? "ChatTurns" : "ProcessingRuns")}" x
                WHERE x."Id" = @id{lockSql}
                """,
        };

        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            sql, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new(false, DeadlineDenial.Missing, DateTimeOffset.UtcNow, null, null);
        }

        var status = reader.GetString(0);
        var owner = reader.IsDBNull(1) ? null : reader.GetString(1);
        var token = reader.GetInt64(2);
        var leaseUntil = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3);
        var deadline = reader.GetFieldValue<DateTimeOffset>(4);
        var parentStatus = reader.IsDBNull(5) ? null : reader.GetString(5);
        var effective = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6);
        var now = reader.GetFieldValue<DateTimeOffset>(7);

        DeadlineAuthority Deny(DeadlineDenial denial) => new(false, denial, now, null, effective ?? deadline, status);

        if (status is "Completed" or "Failed" or "Cancelled")
        {
            return Deny(DeadlineDenial.Terminal);
        }

        if (status != "Running")
        {
            return Deny(DeadlineDenial.NotRunning);
        }

        if (owner != leaseOwner || token != fence)
        {
            return Deny(DeadlineDenial.Fenced);
        }

        if (leaseUntil is not { } lease || lease <= now)
        {
            return Deny(DeadlineDenial.LeaseExpired);
        }

        if (kind == WorkOwnerKind.DocumentJob)
        {
            if (parentStatus is not ("Queued" or "Parsing" or "Extracting"))
            {
                return Deny(DeadlineDenial.ParentNotActive);
            }

            if (effective is not { } parentUntil || parentUntil <= now)
            {
                return Deny(DeadlineDenial.ParentDeadlineExceeded);
            }

            return deadline <= now
                ? Deny(DeadlineDenial.DeadlineExceeded)
                : new(true, DeadlineDenial.None, now, deadline < parentUntil ? deadline : parentUntil, parentUntil, status);
        }

        // Root: DeadlineAt è la proiezione legacy; per i root documentali vale anche EffectiveDeadlineAt (il minore prevale).
        var until = effective is { } e && e < deadline ? e : deadline;
        return until <= now
            ? Deny(DeadlineDenial.DeadlineExceeded)
            : new(true, DeadlineDenial.None, now, until, effective ?? deadline, status);
    }

    /// <summary>
    /// Stato di un ChatTurn (<paramref name="turn"/>) o ProcessingRun per un committer, sotto la transazione corrente (riga bloccata, se c'è):
    /// Completed (già concluso), Valid (Running con owner, fence e lease validi e scadenza corrente nel futuro) o Stale.
    /// </summary>
    public static async Task<RootCommitState> CommitStateAsync(AppDbContext db, bool turn, Guid id, string leaseOwner, long fence, CancellationToken ct)
    {
        // Il committer chiama sempre dentro la propria transazione (riga bloccata fino al commit); fuori da una transazione è una semplice lettura.
        var lockSql = db.Database.CurrentTransaction is null ? "" : " FOR UPDATE OF x";
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT CASE
                WHEN x."Status" = 'Completed' THEN 'completed'
                WHEN x."Status" = 'Running' AND x."LeaseOwner" = @owner AND x."FencingToken" = @fence
                     AND x."LeaseUntil" > clock_timestamp() AND {RootAlive("x")} THEN 'valid'
                ELSE 'stale' END
            FROM "{(turn ? "ChatTurns" : "ProcessingRuns")}" x WHERE x."Id" = @id{lockSql}
            """,
            (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.AddWithValue("owner", leaseOwner);
        command.Parameters.AddWithValue("fence", fence);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(ct) switch
        {
            "completed" => RootCommitState.Completed,
            "valid" => RootCommitState.Valid,
            "stale" => RootCommitState.Stale,
            _ => RootCommitState.Missing,
        };
    }

    /// <summary>
    /// Timeout di una chiamata: il minore fra il timeout configurato e ogni residuo (tentativo, root, locale). Null se uno qualunque è
    /// esaurito o non positivo: nessun invio alla scadenza esatta, e mai un nuovo budget globale.
    /// </summary>
    public static TimeSpan? CallTimeout(TimeSpan configured, params TimeSpan[] residuals)
    {
        var timeout = configured;
        foreach (var residual in residuals)
        {
            if (residual <= TimeSpan.Zero)
            {
                return null;
            }

            timeout = residual < timeout ? residual : timeout;
        }

        return timeout > TimeSpan.Zero ? timeout : null;
    }
}
