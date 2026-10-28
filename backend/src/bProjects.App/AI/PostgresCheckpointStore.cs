using System.Text.Json;
using bProjects.App.Persistence;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace bProjects.App.AI;

/// <summary>
/// Checkpoint nativo MAF di un ProcessingRun (data-model.md «WorkflowCheckpoint»). Il payload è opaco: nessun campo
/// di dominio decide la prossima fase del grafo, e non esiste alcuno stato per executor.
/// </summary>
public sealed class WorkflowCheckpoint
{
    public Guid Id { get; set; }
    public Guid ProcessingRunId { get; set; }

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

    /// <summary>Posizione oldest-first nel run: cresce di uno a ogni scrittura, serializzata sulla riga del run.</summary>
    public long CommitSequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string MafVersion { get; set; }
    public required string WorkflowDefinitionVersion { get; set; }
}

/// <summary>Chi scrive: owner e fencing token correnti del lease del ProcessingRun.</summary>
public sealed record CheckpointWriter(Guid ProcessingRunId, string LeaseOwner, long FencingToken);

/// <summary>Scrittura rifiutata: owner/fencing non più validi, run non Running o lease scaduto. Nulla è stato scritto.</summary>
public sealed class CheckpointFencedException(Guid processingRunId)
    : LeaseLostException($"Scrittura checkpoint rifiutata per il run {processingRunId}: owner o fencing token non più validi.");

/// <summary>
/// <c>ICheckpointStore&lt;JsonElement&gt;</c> su PostgreSQL per <c>CheckpointManager.CreateJson</c>. Lo store è legato a un
/// ProcessingRun; le scritture sono un'unica istruzione atomica protetta da owner + FencingToken del run (come le altre
/// scritture dell'owner), quindi un worker che ha perso il lease non può aggiungere checkpoint. Le letture non richiedono
/// il fence: il resume di un nuovo processo le usa prima di prendere il lease.
/// </summary>
public sealed class PostgresCheckpointStore(AppDbContext db, CheckpointWriter writer) : ICheckpointStore<JsonElement>
{
    public async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string runId, CheckpointInfo? withParent = null)
    {
        await using var command = await CommandAsync(
            """
            SELECT c."CheckpointKey" FROM "WorkflowCheckpoints" c
            WHERE c."ProcessingRunId" = @run AND c."WorkflowSessionId" = @session
              AND (@parent::text IS NULL OR c."ParentCheckpointKey" = @parent)
            ORDER BY c."CommitSequence"
            """,
            ("run", writer.ProcessingRunId), ("session", runId), ("parent", (object?)withParent?.CheckpointId ?? DBNull.Value));

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
        var key = Guid.NewGuid().ToString("N");
        var info = new CheckpointInfo(runId, key);

        // Due istruzioni in una transazione: il blocco di riga sul run (con owner/fence/lease verificati) viene preso per
        // primo, così lo snapshot dell'INSERT successivo (READ COMMITTED) vede già l'ultima CommitSequence dei writer
        // concorrenti. Un'unica istruzione calcolerebbe il massimo su uno snapshot precedente all'attesa sul lock.
        await using var owned = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync() : null;

        await using (var fence = await CommandAsync(
            """
            SELECT 1 FROM "ProcessingRuns"
            WHERE "Id" = @run AND "Status" = 'Running' AND "LeaseOwner" = @owner AND "FencingToken" = @fence
              AND "LeaseUntil" > clock_timestamp()
            FOR UPDATE
            """,
            ("run", writer.ProcessingRunId), ("owner", writer.LeaseOwner), ("fence", writer.FencingToken)))
        {
            if (await fence.ExecuteScalarAsync() is null)
            {
                throw new CheckpointFencedException(writer.ProcessingRunId);
            }
        }

        await using (var command = await CommandAsync(
            """
            INSERT INTO "WorkflowCheckpoints"
                ("Id", "ProcessingRunId", "WorkflowSessionId", "CheckpointKey", "CheckpointInfo", "ParentCheckpointKey", "ParentInfo",
                 "Payload", "CommitSequence", "CreatedAt", "MafVersion", "WorkflowDefinitionVersion")
            SELECT @id, r."Id", @session, @key, @info, @parentKey, @parentInfo, @payload,
                   COALESCE((SELECT max(w."CommitSequence") FROM "WorkflowCheckpoints" w WHERE w."ProcessingRunId" = r."Id"), 0) + 1,
                   clock_timestamp(), r."MafVersion", r."WorkflowDefinitionVersion"
            FROM "ProcessingRuns" r WHERE r."Id" = @run
            """,
            ("id", Guid.NewGuid()), ("run", writer.ProcessingRunId), ("session", runId), ("key", key)))
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
        await using var command = await CommandAsync(
            """
            SELECT "Payload"::text FROM "WorkflowCheckpoints"
            WHERE "ProcessingRunId" = @run AND "WorkflowSessionId" = @session AND "CheckpointKey" = @key
            """,
            ("run", writer.ProcessingRunId), ("session", runId), ("key", key.CheckpointId));

        var payload = await command.ExecuteScalarAsync() as string
            ?? throw new KeyNotFoundException($"Checkpoint {key.CheckpointId} non trovato per il run {writer.ProcessingRunId}.");
        using var document = JsonDocument.Parse(payload);
        // jsonb non conserva l'ordine delle proprietà, ma il polimorfismo di MAF richiede "$type" come prima proprietà.
        return PostgresChatHistoryProvider.WithMetadataFirst(document.RootElement);
    }

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
