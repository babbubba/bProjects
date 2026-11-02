using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace bProjects.App.AI;

/// <summary>
/// Errore dei dati di lavoro con codice stabile: <see cref="IntegrityConflict"/> (input/output/manifest discordanti sotto la stessa chiave,
/// mai un aggiornamento), <see cref="WorkOutputInvalid"/> (payload mancante, corrotto o fuori progetto), <see cref="CheckpointIncompatible"/>
/// (hash, schema, validazione o runtime di un altro contesto: non si reinferisce per sovrascrivere), <see cref="InvalidInput"/>.
/// </summary>
public sealed class DocumentWorkException(string code, string message) : Exception(message)
{
    public const string IntegrityConflict = "integrity_conflict";
    public const string WorkOutputInvalid = "work_output_invalid";
    public const string CheckpointIncompatible = "checkpoint_incompatible";
    public const string InvalidInput = "invalid_work_input";

    public string Code { get; } = code;
}

/// <summary>Manifesto da pubblicare; l'hash lo calcola lo store sul JSON canonico dei campi.</summary>
public sealed record WorkManifestInput(
    string ConfigHash, string SourceHash, string RuntimeFingerprint, string DefinitionVersion, string AlgorithmVersion, string PolicyHash, string PlanJson);

/// <summary>Batch derivato da pubblicare (BatchId e InputHash sono calcolati dal chiamante secondo AI §5.2: nessun Guid nuovo al replay).</summary>
public sealed record DerivedBatchInput(
    string BatchId, string Stage, string? View, int? Pass, int? Level, int Ordinal, IReadOnlyList<string> ParentOutputHashes,
    string InputIdsJson, int? MeasuredTokens, string TokenProvenance, string InputHash, string SchemaVersion, string PromptVersion);

/// <summary>
/// Output validato da rendere durevole. Esattamente uno fra <see cref="PayloadJson"/> (OutputHash = hash del JSON canonico) e
/// <see cref="StorageKey"/> con <see cref="PayloadByteLength"/> (OutputHash = SHA-256 dei byte, già scritti sullo storage privato).
/// </summary>
public sealed record WorkOutputInput(
    string BatchId, string NodeId, string InputHash, string OutputHash, string? PayloadJson, string? StorageKey, long? PayloadByteLength,
    string CoveredIdsJson, string? MapsJson, string? ExpansionReceiptJson, string SchemaVersion, string ValidationVersion, string? DerivedFromOutputHash);

public sealed record WorkCommitResult(WorkOutput Output, bool Replayed);

/// <summary>Versioni che un output deve avere per essere riusato da questo contesto (oltre a input e manifesto, confrontati dallo store).</summary>
public sealed record WorkLookupExpectation(string SchemaVersion, string ValidationVersion);

public enum WorkLookupKind
{
    Miss,
    Hit,
}

public sealed record WorkLookup(WorkLookupKind Kind, WorkOutput? Output, string? Payload);

/// <summary>Esito del lock dell'owner: progetto e, per i run, le versioni da stampare sui checkpoint.</summary>
internal sealed record OwnerLock(Guid ProjectId, string? MafVersion, string? DefinitionVersion);

/// <summary>
/// Autorità di scrittura di un owner: sotto il lock della sua riga, con il clock del database, verifica Running, owner e fencing token
/// del lease, lease non scaduto e, se richiesto, scadenza propria e (per un DocumentJob) preparazione padre ancora attiva con la sua
/// EffectiveDeadlineAt corrente. Un worker con fence vecchio, lease scaduto o padre scaduto riceve <see cref="CheckpointFencedException"/>
/// e non scrive nulla. Va eseguito dentro la transazione del chiamante; la FK da sola non è una condizione di vita del padre.
/// </summary>
internal static class WorkOwnerGuard
{
    public static string Column(WorkOwnerKind kind) => kind switch
    {
        WorkOwnerKind.ProcessingRun => "ProcessingRunId",
        WorkOwnerKind.ChatTurn => "ChatTurnId",
        WorkOwnerKind.DocumentJob => "DocumentJobId",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static async Task<OwnerLock> LockAsync(AppDbContext db, CheckpointWriter writer, bool includeDeadline, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Il lock dell'owner richiede una transazione.");
        }

        var deadline = includeDeadline ? " AND {0}.\"DeadlineAt\" > clock_timestamp()" : "";
        var sql = writer.OwnerKind switch
        {
            WorkOwnerKind.ProcessingRun => $"""
                SELECT r."ProjectId", r."MafVersion", r."WorkflowDefinitionVersion" FROM "ProcessingRuns" r
                WHERE r."Id" = @owner AND r."Status" = 'Running' AND r."LeaseOwner" = @lease AND r."FencingToken" = @fence
                  AND r."LeaseUntil" > clock_timestamp(){string.Format(deadline, "r")}
                FOR UPDATE OF r
                """,
            WorkOwnerKind.ChatTurn => $"""
                SELECT c."ProjectId", NULL::text, NULL::text FROM "ChatTurns" t JOIN "Conversations" c ON c."Id" = t."ConversationId"
                WHERE t."Id" = @owner AND t."Status" = 'Running' AND t."LeaseOwner" = @lease AND t."FencingToken" = @fence
                  AND t."LeaseUntil" > clock_timestamp(){string.Format(deadline, "t")}
                FOR UPDATE OF t
                """,
            _ => $"""
                SELECT j."ProjectId", NULL::text, j."WorkflowDefinitionVersion" FROM "DocumentJobs" j
                JOIN "DocumentPreparations" p ON p."Id" = j."PreparationId" AND p."ProjectId" = j."ProjectId"
                WHERE j."Id" = @owner AND j."Status" = 'Running' AND j."LeaseOwner" = @lease AND j."FencingToken" = @fence
                  AND j."LeaseUntil" > clock_timestamp() AND j."DeadlineAt" > clock_timestamp()
                  AND p."Status" IN ('Queued', 'Parsing', 'Extracting') AND p."EffectiveDeadlineAt" > clock_timestamp()
                FOR UPDATE OF j
                """,
        };

        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            sql, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction.GetDbTransaction());
        command.Parameters.AddWithValue("owner", writer.OwnerId);
        command.Parameters.AddWithValue("lease", writer.LeaseOwner);
        command.Parameters.AddWithValue("fence", writer.FencingToken);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new CheckpointFencedException(writer.OwnerId);
        }

        return new OwnerLock(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <summary>Progetto dell'owner senza lock né condizioni di vita (per validare un blob prima di aprire la transazione di commit).</summary>
    public static async Task<Guid> ProjectOfAsync(AppDbContext db, CheckpointWriter writer, CancellationToken ct)
    {
        var project = writer.OwnerKind switch
        {
            WorkOwnerKind.ProcessingRun => await db.ProcessingRuns.AsNoTracking().Where(r => r.Id == writer.OwnerId).Select(r => (Guid?)r.ProjectId).SingleOrDefaultAsync(ct),
            WorkOwnerKind.ChatTurn => await (from t in db.ChatTurns.AsNoTracking()
                                             join c in db.Conversations.AsNoTracking() on t.ConversationId equals c.Id
                                             where t.Id == writer.OwnerId
                                             select (Guid?)c.ProjectId).SingleOrDefaultAsync(ct),
            _ => await db.DocumentJobs.AsNoTracking().Where(j => j.Id == writer.OwnerId).Select(j => (Guid?)j.ProjectId).SingleOrDefaultAsync(ct),
        };

        return project ?? throw new CheckpointFencedException(writer.OwnerId);
    }
}

/// <summary>
/// Dati di lavoro durevoli del workflow documentale (T041; contracts/ai-workflow.md §5). Ogni scrittura è una transazione corta sotto il
/// lock fenced dell'owner (<see cref="WorkOwnerGuard"/>): output, mappe, figli già determinabili o ricevuta di espansione sono nello stesso
/// commit, mai su una risposta in memoria o parziale. Un payload su storage è riletto e validato per lunghezza e SHA-256 prima del
/// riferimento DB. Le unique rendono i replay idempotenti; la stessa chiave con input, output o genitori diversi è
/// <see cref="DocumentWorkException.IntegrityConflict"/>, mai un aggiornamento. Il checkpoint nativo MAF è una scrittura separata
/// (<see cref="PostgresCheckpointStore"/>): nessuna transazione distribuita finta, e nessun exactly-once dell'inferenza.
/// </summary>
public sealed partial class DocumentWorkStore(AppDbContext db, CheckpointWriter writer, DocumentFileStore? blobs = null)
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    /// <summary>Pubblica il manifesto dell'owner; con lo stesso contenuto è un replay, con altro contenuto un conflitto d'integrità.</summary>
    public Task<WorkManifest> PublishManifestAsync(WorkManifestInput input, CancellationToken ct) =>
        InTransactionAsync(async owner =>
        {
            foreach (var hash in new[] { input.ConfigHash, input.SourceHash, input.PolicyHash })
            {
                RequireHash(hash, "hash del manifesto");
            }

            var plan = JsonDocument.Parse(input.PlanJson).RootElement.Clone();
            var hashOfManifest = CanonicalJson.Sha256Hex(JsonSerializer.SerializeToElement(new
            {
                algorithmVersion = input.AlgorithmVersion, configHash = input.ConfigHash, definitionVersion = input.DefinitionVersion, plan,
                policyHash = input.PolicyHash, runtimeFingerprint = input.RuntimeFingerprint, sourceHash = input.SourceHash,
            }));

            await using (var insert = Command(
                """
                INSERT INTO "WorkManifests" ("Id","ProjectId","OwnerKind","OwnerId","ConfigHash","SourceHash","RuntimeFingerprint","DefinitionVersion","AlgorithmVersion","PolicyHash","PlanJson","Hash","CreatedAt")
                VALUES (@id,@project,@kind,@owner,@config,@source,@runtime,@definition,@algorithm,@policy,@plan,@hash,clock_timestamp())
                ON CONFLICT ("OwnerKind","OwnerId") DO NOTHING
                """))
            {
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("project", owner.ProjectId);
                insert.Parameters.AddWithValue("kind", writer.OwnerKind.ToString());
                insert.Parameters.AddWithValue("owner", writer.OwnerId);
                insert.Parameters.AddWithValue("config", input.ConfigHash);
                insert.Parameters.AddWithValue("source", input.SourceHash);
                insert.Parameters.AddWithValue("runtime", input.RuntimeFingerprint);
                insert.Parameters.AddWithValue("definition", input.DefinitionVersion);
                insert.Parameters.AddWithValue("algorithm", input.AlgorithmVersion);
                insert.Parameters.AddWithValue("policy", input.PolicyHash);
                Json(insert, "plan", input.PlanJson);
                insert.Parameters.AddWithValue("hash", hashOfManifest);
                await insert.ExecuteNonQueryAsync(ct);
            }

            var stored = await db.Set<WorkManifest>().AsNoTracking().SingleAsync(m => m.OwnerKind == writer.OwnerKind && m.OwnerId == writer.OwnerId, ct);
            return stored.Hash == hashOfManifest
                ? stored
                : throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Il manifesto dell'owner esiste già con contenuto diverso.");
        }, ct);

    /// <summary>Pubblica batch derivati (tutti o nessuno); i già presenti identici sono ignorati, uno diverso è un conflitto.</summary>
    public Task<IReadOnlyList<DerivedBatch>> PublishBatchesAsync(IReadOnlyList<DerivedBatchInput> batches, CancellationToken ct) =>
        InTransactionAsync(async owner =>
        {
            await RequireManifestAsync(ct);
            return await InsertBatchesAsync(owner, batches, ct);
        }, ct);

    /// <summary>
    /// Rende durevole l'output di un batch con i figli già determinabili, in un'unica transazione fenced. Con lo stesso batch già
    /// committato e lo stesso input/output è un replay (stesso output, anche sotto un fence più nuovo); altrimenti conflitto.
    /// </summary>
    public async Task<WorkCommitResult> CommitOutputAsync(WorkOutputInput output, IReadOnlyList<DerivedBatchInput>? children, CancellationToken ct)
    {
        RequireHash(output.InputHash, "inputHash");
        RequireHash(output.OutputHash, "outputHash");
        if (output.DerivedFromOutputHash is not null)
        {
            RequireHash(output.DerivedFromOutputHash, "derivedFromOutputHash");
        }

        if ((output.PayloadJson is null) == (output.StorageKey is null) || (output.StorageKey is null) != (output.PayloadByteLength is null))
        {
            throw new DocumentWorkException(DocumentWorkException.InvalidInput, "L'output ha un payload inline oppure una chiave di storage con la lunghezza, non entrambi né nessuno.");
        }

        // Payload e hash controllati prima di qualunque riferimento DB: nessun output Committed su un blob assente o corrotto.
        var project = await WorkOwnerGuard.ProjectOfAsync(db, writer, ct);
        await VerifyPayloadAsync(project, output.PayloadJson, output.StorageKey, output.PayloadByteLength, output.OutputHash, ct);

        return await InTransactionAsync(async owner =>
        {
            var manifest = await RequireManifestAsync(ct);
            if (children is { Count: > 0 })
            {
                await InsertBatchesAsync(owner, children, ct);
            }

            var batch = await db.Set<DerivedBatch>().AsNoTracking()
                .SingleOrDefaultAsync(b => b.OwnerKind == writer.OwnerKind && b.OwnerId == writer.OwnerId && b.BatchId == output.BatchId, ct)
                ?? throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Il batch dell'output non è stato pubblicato.");
            if (batch.InputHash != output.InputHash)
            {
                throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "L'inputHash dell'output è diverso da quello del batch.");
            }

            int inserted;
            await using (var insert = Command(
                """
                INSERT INTO "WorkOutputs" ("Id","ProjectId","OwnerKind","OwnerId","BatchId","NodeId","InputHash","OutputHash","PayloadJson","StorageKey","PayloadByteLength",
                    "CoveredIdsJson","MapsJson","ExpansionReceiptJson","SchemaVersion","ManifestHash","Status","ValidationVersion","CommittedAt","CommittedFence","DerivedFromOutputHash")
                VALUES (@id,@project,@kind,@owner,@batch,@node,@input,@output,@payload,@key,@length,@covered,@maps,@receipt,@schema,@manifest,'Committed',@validation,clock_timestamp(),@fence,@derived)
                ON CONFLICT ("OwnerKind","OwnerId","BatchId") DO NOTHING
                """))
            {
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("project", owner.ProjectId);
                insert.Parameters.AddWithValue("kind", writer.OwnerKind.ToString());
                insert.Parameters.AddWithValue("owner", writer.OwnerId);
                insert.Parameters.AddWithValue("batch", output.BatchId);
                insert.Parameters.AddWithValue("node", output.NodeId);
                insert.Parameters.AddWithValue("input", output.InputHash);
                insert.Parameters.AddWithValue("output", output.OutputHash);
                Json(insert, "payload", output.PayloadJson);
                insert.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Varchar) { Value = (object?)output.StorageKey ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("length", NpgsqlDbType.Bigint) { Value = (object?)output.PayloadByteLength ?? DBNull.Value });
                Json(insert, "covered", output.CoveredIdsJson);
                Json(insert, "maps", output.MapsJson);
                Json(insert, "receipt", output.ExpansionReceiptJson);
                insert.Parameters.AddWithValue("schema", output.SchemaVersion);
                insert.Parameters.AddWithValue("manifest", manifest.Hash);
                insert.Parameters.AddWithValue("validation", output.ValidationVersion);
                insert.Parameters.AddWithValue("fence", writer.FencingToken);
                insert.Parameters.Add(new NpgsqlParameter("derived", NpgsqlDbType.Varchar) { Value = (object?)output.DerivedFromOutputHash ?? DBNull.Value });
                inserted = await insert.ExecuteNonQueryAsync(ct);
            }

            var stored = await db.Set<WorkOutput>().AsNoTracking()
                .SingleAsync(o => o.OwnerKind == writer.OwnerKind && o.OwnerId == writer.OwnerId && o.BatchId == output.BatchId, ct);
            if (inserted == 1)
            {
                return new WorkCommitResult(stored, Replayed: false);
            }

            // Già committato: identico = replay (l'output originale e il suo fence restano); discordante = errore, mai update.
            if (stored.InputHash != output.InputHash || stored.OutputHash != output.OutputHash)
            {
                throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Il batch è già committato con input o output diversi.");
            }

            return new WorkCommitResult(stored, Replayed: true);
        }, ct);
    }

    /// <summary>
    /// Lookup di un output già committato: Miss se non c'è; Hit se input, manifesto, schema e validazione coincidono e il payload è
    /// integro (riletto e riconvalidato); altrimenti eccezione (<c>checkpoint_incompatible</c> / <c>work_output_invalid</c>), perché un
    /// risultato esistente non si sovrascrive con una nuova generazione. Lettura senza fence, come il resume di un nuovo processo.
    /// </summary>
    public async Task<WorkLookup> LookupAsync(string batchId, string inputHash, WorkLookupExpectation expected, CancellationToken ct)
    {
        var output = await db.Set<WorkOutput>().AsNoTracking()
            .SingleOrDefaultAsync(o => o.OwnerKind == writer.OwnerKind && o.OwnerId == writer.OwnerId && o.BatchId == batchId, ct);
        if (output is null)
        {
            return new WorkLookup(WorkLookupKind.Miss, null, null);
        }

        var manifestHash = await db.Set<WorkManifest>().AsNoTracking()
            .Where(m => m.OwnerKind == writer.OwnerKind && m.OwnerId == writer.OwnerId).Select(m => m.Hash).SingleOrDefaultAsync(ct);
        if (output.InputHash != inputHash || output.ManifestHash != manifestHash || output.SchemaVersion != expected.SchemaVersion
            || output.ValidationVersion != expected.ValidationVersion || output.Status != WorkOutputStatus.Committed)
        {
            throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "L'output committato non è compatibile con input, manifesto, schema o validazione richiesti.");
        }

        var payload = await VerifyPayloadAsync(output.ProjectId, output.PayloadJson, output.StorageKey, output.PayloadByteLength, output.OutputHash, ct);
        return new WorkLookup(WorkLookupKind.Hit, output, payload);
    }

    // ------------------------------------------------------------------ interni

    private async Task<IReadOnlyList<DerivedBatch>> InsertBatchesAsync(OwnerLock owner, IReadOnlyList<DerivedBatchInput> batches, CancellationToken ct)
    {
        var result = new List<DerivedBatch>(batches.Count);
        foreach (var batch in batches)
        {
            RequireHash(batch.InputHash, "inputHash del batch");
            foreach (var parent in batch.ParentOutputHashes)
            {
                RequireHash(parent, "hash genitore");
            }

            var parents = JsonSerializer.Serialize(batch.ParentOutputHashes);
            int inserted;
            await using (var insert = Command(
                """
                INSERT INTO "DerivedBatches" ("Id","ProjectId","OwnerKind","OwnerId","BatchId","Stage","View","Pass","Level","Ordinal","ParentOutputHashes","InputIdsJson",
                    "MeasuredTokens","TokenProvenance","InputHash","SchemaVersion","PromptVersion","CreatedAt")
                VALUES (@id,@project,@kind,@owner,@batch,@stage,@view,@pass,@level,@ordinal,@parents,@inputs,@tokens,@provenance,@input,@schema,@prompt,clock_timestamp())
                ON CONFLICT ("OwnerKind","OwnerId","BatchId") DO NOTHING
                """))
            {
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("project", owner.ProjectId);
                insert.Parameters.AddWithValue("kind", writer.OwnerKind.ToString());
                insert.Parameters.AddWithValue("owner", writer.OwnerId);
                insert.Parameters.AddWithValue("batch", batch.BatchId);
                insert.Parameters.AddWithValue("stage", batch.Stage);
                insert.Parameters.Add(new NpgsqlParameter("view", NpgsqlDbType.Varchar) { Value = (object?)batch.View ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("pass", NpgsqlDbType.Integer) { Value = (object?)batch.Pass ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("level", NpgsqlDbType.Integer) { Value = (object?)batch.Level ?? DBNull.Value });
                insert.Parameters.AddWithValue("ordinal", batch.Ordinal);
                Json(insert, "parents", parents);
                Json(insert, "inputs", batch.InputIdsJson);
                insert.Parameters.Add(new NpgsqlParameter("tokens", NpgsqlDbType.Integer) { Value = (object?)batch.MeasuredTokens ?? DBNull.Value });
                insert.Parameters.AddWithValue("provenance", batch.TokenProvenance);
                insert.Parameters.AddWithValue("input", batch.InputHash);
                insert.Parameters.AddWithValue("schema", batch.SchemaVersion);
                insert.Parameters.AddWithValue("prompt", batch.PromptVersion);
                inserted = await insert.ExecuteNonQueryAsync(ct);
            }

            var stored = await db.Set<DerivedBatch>().AsNoTracking()
                .SingleAsync(b => b.OwnerKind == writer.OwnerKind && b.OwnerId == writer.OwnerId && b.BatchId == batch.BatchId, ct);
            if (inserted == 0 && !SameBatch(stored, batch))
            {
                throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Il batch esiste già con input, genitori o parametri diversi.");
            }

            result.Add(stored);
        }

        return result;
    }

    private static bool SameBatch(DerivedBatch stored, DerivedBatchInput batch) =>
        stored.InputHash == batch.InputHash && stored.Stage == batch.Stage && stored.View == batch.View && stored.Pass == batch.Pass && stored.Level == batch.Level
        && stored.Ordinal == batch.Ordinal && stored.SchemaVersion == batch.SchemaVersion && stored.PromptVersion == batch.PromptVersion
        && stored.TokenProvenance == batch.TokenProvenance && stored.MeasuredTokens == batch.MeasuredTokens
        && JsonSerializer.Deserialize<string[]>(stored.ParentOutputHashes)!.SequenceEqual(batch.ParentOutputHashes)   // l'ordine dei genitori è parte dell'identità
        && JsonElement.DeepEquals(JsonDocument.Parse(stored.InputIdsJson).RootElement, JsonDocument.Parse(batch.InputIdsJson).RootElement);

    private async Task<WorkManifest> RequireManifestAsync(CancellationToken ct) =>
        await db.Set<WorkManifest>().AsNoTracking().SingleOrDefaultAsync(m => m.OwnerKind == writer.OwnerKind && m.OwnerId == writer.OwnerId, ct)
            ?? throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Il manifesto dell'owner non è stato pubblicato.");

    /// <summary>Convalida il payload: inline = hash del JSON canonico; su storage = chiave del progetto e byte riletti con lunghezza e SHA-256. Ritorna il testo.</summary>
    private async Task<string> VerifyPayloadAsync(Guid projectId, string? inline, string? storageKey, long? length, string outputHash, CancellationToken ct)
    {
        if (storageKey is null)
        {
            if (inline is null || CanonicalJson.Sha256Hex(inline) != outputHash)
            {
                throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Il payload inline non corrisponde all'hash dell'output.");
            }

            return inline;
        }

        if (blobs is null || length is null || !storageKey.StartsWith($"{projectId:N}/", StringComparison.Ordinal))
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Il payload su storage non è verificabile per questo progetto.");
        }

        try
        {
            await using var stream = await blobs.OpenVerifiedAsync(storageKey, outputHash, length.Value, ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
        catch (Exception ex) when (ex is DocumentStoreException or IOException or UnauthorizedAccessException)
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Il payload su storage è assente o non corrisponde all'hash registrato.");
        }
    }

    private async Task<T> InTransactionAsync<T>(Func<OwnerLock, Task<T>> action, CancellationToken ct)
    {
        await using var owned = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var owner = await WorkOwnerGuard.LockAsync(db, writer, includeDeadline: true, ct);
        var result = await action(owner);
        if (owned is not null)
        {
            await owned.CommitAsync(ct);
        }

        return result;
    }

    private static void RequireHash(string value, string what)
    {
        if (!HashPattern().IsMatch(value))
        {
            throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"{what}: atteso SHA-256 esadecimale minuscolo.");
        }
    }

    private NpgsqlCommand Command(string sql) => new(
        sql, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());

    private static void Json(NpgsqlCommand command, string name, string? json) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = (object?)json ?? DBNull.Value });
}
