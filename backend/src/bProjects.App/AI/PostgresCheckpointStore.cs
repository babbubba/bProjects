using System.Text.Json;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace bProjects.App.AI;

/// <summary>
/// Checkpoint nativo MAF di un owner: ProcessingRun (001), ChatTurn o DocumentJob (002), esattamente uno (data-model.md
/// «WorkflowCheckpoint»). Il payload è opaco: nessun campo di dominio decide la prossima fase del grafo, e non esiste alcuno
/// stato per executor.
/// </summary>
public sealed class WorkflowCheckpoint
{
    public Guid Id { get; set; }
    public Guid? ProcessingRunId { get; set; }
    public Guid? ChatTurnId { get; set; }
    public Guid? DocumentJobId { get; set; }

    /// <summary>Progetto dell'owner: obbligatorio per ChatTurn e DocumentJob (FK composta), assente nelle righe 001 di ProcessingRun.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>Id di sessione del workflow (il «runId» dell'API ICheckpointStore).</summary>
    public required string WorkflowSessionId { get; set; }
    public required string CheckpointKey { get; set; }

    /// <summary>CheckpointInfo serializzato (JSONB).</summary>
    public required string CheckpointInfo { get; set; }
    public string? ParentCheckpointKey { get; set; }

    /// <summary>ParentInfo serializzato (JSONB), assente per il primo checkpoint.</summary>
    public string? ParentInfo { get; set; }

    /// <summary>Checkpoint MAF così come prodotto: JSONB opaco per il backend.</summary>
    public required string Payload { get; set; }

    /// <summary>Posizione oldest-first nell'owner: cresce di uno a ogni scrittura, serializzata sotto il lock della riga dell'owner.</summary>
    public long CommitSequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string MafVersion { get; set; }
    public required string WorkflowDefinitionVersion { get; set; }
}

/// <summary>
/// Chi scrive: owner e fencing token correnti del lease. Per i ProcessingRun definizione e versione MAF si leggono dalla riga del run
/// (comportamento 001); per ChatTurn e DocumentJob le fornisce il writer e valgono anche come compatibilità attesa in lettura.
/// </summary>
public sealed record CheckpointWriter(
    Guid OwnerId, string LeaseOwner, long FencingToken, WorkOwnerKind OwnerKind = WorkOwnerKind.ProcessingRun,
    string? DefinitionVersion = null, string? MafVersion = null)
{
    /// <summary>Alias dell'owner per il ramo storico.</summary>
    public Guid ProcessingRunId => OwnerId;

    public static CheckpointWriter ForDocumentJob(Guid jobId, string leaseOwner, long fence, string definitionVersion, string? mafVersion = null) =>
        new(jobId, leaseOwner, fence, WorkOwnerKind.DocumentJob, definitionVersion, mafVersion ?? Conversation.CurrentMafVersion);

    public static CheckpointWriter ForChatTurn(Guid turnId, string leaseOwner, long fence, string definitionVersion, string? mafVersion = null) =>
        new(turnId, leaseOwner, fence, WorkOwnerKind.ChatTurn, definitionVersion, mafVersion ?? Conversation.CurrentMafVersion);
}

/// <summary>Scrittura rifiutata: owner/fencing non più validi, owner non Running, lease o deadline scaduti. Nulla è stato scritto.</summary>
public sealed class CheckpointFencedException(Guid ownerId)
    : LeaseLostException($"Scrittura rifiutata per l'owner {ownerId}: owner, fencing token o scadenze non più validi.");

/// <summary>Checkpoint scritto da un'altra definizione di workflow o versione MAF: non è autorevole e non si ripristina (checkpoint_incompatible).</summary>
public sealed class CheckpointIncompatibleException(string message) : Exception(message)
{
    public string Code => DocumentWorkException.CheckpointIncompatible;
}

/// <summary>
/// <c>ICheckpointStore&lt;JsonElement&gt;</c> su PostgreSQL per <c>CheckpointManager.CreateJson</c>. Lo store è legato a un owner
/// (ProcessingRun, ChatTurn o DocumentJob); le scritture sono protette da owner + FencingToken (come le altre scritture dell'owner)
/// sotto il lock della sua riga, che serializza anche la CommitSequence: un worker che ha perso il lease non può aggiungere checkpoint.
/// Per un DocumentJob valgono anche la scadenza del job e quella corrente del padre. Le letture non richiedono il fence (il resume di
/// un nuovo processo le usa prima di prendere il lease), ma rifiutano un checkpoint di altra definizione/versione MAF.
/// </summary>
public sealed class PostgresCheckpointStore(AppDbContext db, CheckpointWriter writer) : ICheckpointStore<JsonElement>
{
    public async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string runId, CheckpointInfo? withParent = null)
    {
        using var span = DocumentTiming.Span("checkpoint.index");
        await using var command = await CommandAsync(
            $"""
            SELECT c."CheckpointKey" FROM "WorkflowCheckpoints" c
            WHERE c."{OwnerColumn}" = @run AND c."WorkflowSessionId" = @session
              AND (@parent::text IS NULL OR c."ParentCheckpointKey" = @parent)
            ORDER BY c."CommitSequence"
            """,
            ("run", writer.OwnerId), ("session", runId), ("parent", (object?)withParent?.CheckpointId ?? DBNull.Value));

        var index = new List<CheckpointInfo>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            index.Add(new CheckpointInfo(runId, reader.GetString(0)));
        }

        return index;
    }

    public async ValueTask<CheckpointInfo> CreateCheckpointAsync(string runId, JsonElement value, CheckpointInfo? parent = null)
    {
        using var span = DocumentTiming.Span("checkpoint.create");
        var key = Guid.NewGuid().ToString("N");
        var info = new CheckpointInfo(runId, key);

        // Due istruzioni in una transazione: il blocco di riga sull'owner (con owner/fence/lease verificati) viene preso per
        // primo, così lo snapshot dell'INSERT successivo (READ COMMITTED) vede già l'ultima CommitSequence dei writer
        // concorrenti. Un'unica istruzione calcolerebbe il massimo su uno snapshot precedente all'attesa sul lock.
        await using var owned = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync() : null;

        // Un DocumentJob e i root con campi del regime (ChatTurn/ProcessingRun documentali) non scrivono oltre la scadenza corrente (T118);
        // i ProcessingRun della 001, senza EffectiveDeadlineAt, mantengono il comportamento storico.
        var owner = await WorkOwnerGuard.LockAsync(db, writer, includeDeadline: true, regimeRootsOnly: writer.OwnerKind != WorkOwnerKind.DocumentJob);
        var definition = writer.DefinitionVersion ?? owner.DefinitionVersion
            ?? throw new InvalidOperationException("Versione di definizione del workflow assente per il checkpoint.");
        var maf = writer.MafVersion ?? owner.MafVersion ?? Conversation.CurrentMafVersion;

        await using (var command = await CommandAsync(
            $"""
            INSERT INTO "WorkflowCheckpoints"
                ("Id", "{OwnerColumn}", "ProjectId", "WorkflowSessionId", "CheckpointKey", "CheckpointInfo", "ParentCheckpointKey", "ParentInfo",
                 "Payload", "CommitSequence", "CreatedAt", "MafVersion", "WorkflowDefinitionVersion")
            VALUES (@id, @run, @project, @session, @key, @info, @parentKey, @parentInfo, @payload,
                    COALESCE((SELECT max(w."CommitSequence") FROM "WorkflowCheckpoints" w WHERE w."{OwnerColumn}" = @run), 0) + 1,
                    clock_timestamp(), @maf, @definition)
            """,
            ("id", Guid.NewGuid()), ("run", writer.OwnerId),
            ("project", writer.OwnerKind == WorkOwnerKind.ProcessingRun ? (object)DBNull.Value : owner.ProjectId),   // le righe 001 restano senza progetto
            ("session", runId), ("key", key), ("maf", maf), ("definition", definition)))
        {
            Json(command, "info", JsonSerializer.Serialize(info));
            command.Parameters.Add(new NpgsqlParameter("parentKey", NpgsqlDbType.Varchar) { Value = (object?)parent?.CheckpointId ?? DBNull.Value });
            Json(command, "parentInfo", parent is null ? null : JsonSerializer.Serialize(parent));
            Json(command, "payload", value.GetRawText());
            await command.ExecuteNonQueryAsync();
        }

        if (owned is not null)
        {
            await owned.CommitAsync();
        }

        return info;
    }

    public async ValueTask<JsonElement> RetrieveCheckpointAsync(string runId, CheckpointInfo key)
    {
        using var span = DocumentTiming.Span("checkpoint.retrieve");
        await using var command = await CommandAsync(
            $"""
            SELECT "Payload"::text, "MafVersion", "WorkflowDefinitionVersion" FROM "WorkflowCheckpoints"
            WHERE "{OwnerColumn}" = @run AND "WorkflowSessionId" = @session AND "CheckpointKey" = @key
            """,
            ("run", writer.OwnerId), ("session", runId), ("key", key.CheckpointId));

        string payload;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
            {
                throw new KeyNotFoundException($"Checkpoint {key.CheckpointId} non trovato per {writer.OwnerKind} {writer.OwnerId}.");
            }

            payload = reader.GetString(0);
            if ((writer.MafVersion is { } maf && maf != reader.GetString(1))
                || (writer.DefinitionVersion is { } definition && definition != reader.GetString(2)))
            {
                // Il checkpoint può restare indietro rispetto al dominio, ma non è autorevole su un'altra definizione: nessun resume.
                throw new CheckpointIncompatibleException($"Il checkpoint {key.CheckpointId} è stato scritto da un'altra definizione di workflow o versione MAF.");
            }
        }

        using var document = JsonDocument.Parse(payload);
        // jsonb non conserva l'ordine delle proprietà, ma il polimorfismo di MAF richiede "$type" come prima proprietà.
        return PostgresChatHistoryProvider.WithMetadataFirst(document.RootElement);
    }

    private string OwnerColumn => WorkOwnerGuard.Column(writer.OwnerKind);

    private static void Json(NpgsqlCommand command, string name, string? json) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = (object?)json ?? DBNull.Value });

    private async Task<NpgsqlCommand> CommandAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync();
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
