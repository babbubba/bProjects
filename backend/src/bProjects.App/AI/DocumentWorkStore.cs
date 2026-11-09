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
public class DocumentWorkException(string code, string message) : Exception(message)
{
    public const string IntegrityConflict = "integrity_conflict";
    public const string WorkOutputInvalid = "work_output_invalid";
    public const string CheckpointIncompatible = "checkpoint_incompatible";
    public const string InvalidInput = "invalid_work_input";
    public const string CallLimitExceeded = "document_limit_exceeded";

    /// <summary>Il payload effettivo del retry informato (T137, AI §13.3) supera il budget di input: nessun invio né taglio; porta la sola diagnostica di misura.</summary>
    public const string ContextBudgetExceeded = "context_budget_exceeded";

    /// <summary>
    /// Motivo tipizzato (prefisso stabile di <c>ErrorMessage</c>, mai un codice a sé) del fail-fast v5 oltre contesto con almeno un Gap (T143, AI §13.6):
    /// la valutazione Gap gerarchica non è supportata e nessuna elaborazione è stata eseguita. L'<c>ErrorCode</c> resta <see cref="CallLimitExceeded"/>.
    /// </summary>
    public const string HierarchicalGapAssessmentUnsupported = "hierarchical_gap_assessment_unsupported";

    /// <summary>
    /// Motivi tipizzati (prefisso stabile di <c>ErrorMessage</c>, mai codici a sé) dello stadio Gap della definizione v6 (D1, AI §13.7): l'intero ledger non entra nel budget di input di una
    /// chiamata (<see cref="GapLedgerExceedsInput"/>) oppure le valutazioni attese non entrano nel budget di output (<see cref="GapAssessmentExceedsOutput"/>). L'<c>ErrorCode</c> resta
    /// <see cref="CallLimitExceeded"/> (stesso codice e azione utente di <see cref="HierarchicalGapAssessmentUnsupported"/>); nessun invio, troncamento o valutazione parziale.
    /// </summary>
    public const string GapLedgerExceedsInput = "gap_ledger_exceeds_input";

    public const string GapAssessmentExceedsOutput = "gap_assessment_exceeds_output";

    /// <summary>Prefisso dell'obbligazione <c>gap_reference_missing:&lt;gap&gt;</c> (stadio Gap): un Gap senza domanda canonica nell'analisi assemblata; il server non la crea. Codice del run: <see cref="CoverageIncomplete"/>.</summary>
    public const string GapReferenceMissing = "gap_reference_missing";

    /// <summary>Fonte originale non verificabile alla costruzione del catalogo alias (range, citazione, campi di provenienza): permanente (AI §7.9).</summary>
    public const string SourceSnapshotInvalid = "source_snapshot_invalid";

    /// <summary>Unità irriducibile fuori budget: la preparazione resta incompleta in modo esplicito (AI §3, §6).</summary>
    public const string DocumentIncomplete = "document_incomplete";

    /// <summary>Non entrano due descriptor nello stesso Pack: la riconciliazione di livello non può avanzare (AI §4.2 punto 4).</summary>
    public const string ReconciliationIrreducible = "reconciliation_irreducible";

    /// <summary>Un'unità non dimostra la copertura attesa di tutti i suoi range proprietari.</summary>
    public const string CoverageIncomplete = "coverage_incomplete";

    /// <summary>Nel round finale emergono collegamenti che richiedono altro lavoro: conservati NotProcessed, nessuna iterazione (AI §4.3).</summary>
    public const string ReconciliationRoundLimit = "reconciliation_round_limit";

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

/// <summary>Esito di una porzione alla chiusura dell'unità: ordinale, hash del risultato e, se nessuna evidenza, la motivazione.</summary>
public sealed record PortionCompletion(int Ordinal, string? NoEvidenceReason);

/// <summary>Versioni che un output deve avere per essere riusato da questo contesto (oltre a input e manifesto, confrontati dallo store).</summary>
public sealed record WorkLookupExpectation(string SchemaVersion, string ValidationVersion);

public enum WorkLookupKind
{
    Miss,
    Hit,
}

public sealed record WorkLookup(WorkLookupKind Kind, WorkOutput? Output, string? Payload);

public enum WorkImportKind
{
    /// <summary>Nessun root precedente o nessun suo output con lo stesso input: si procede col lavoro normale.</summary>
    NoCandidate,

    /// <summary>Output compatibile importato nell'owner corrente (DerivedFromOutputHash valorizzato).</summary>
    Imported,

    /// <summary>L'owner aveva già l'output per lo stesso input: nulla scritto.</summary>
    Replayed,

    /// <summary>Esiste un candidato ma una componente differisce: non riusato né sovrascritto.</summary>
    Incompatible,

    /// <summary>Il payload porta dati del run d'origine non rimappabili in modo deterministico: non importato.</summary>
    NotRemappable,
}

public sealed record WorkImportResult(WorkImportKind Kind, WorkOutput? Output, Guid? SourceOwnerId, string? Reason);

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

    /// <summary>
    /// Blocca l'owner e ne verifica l'autorità (T118, <see cref="DocumentDeadlineGuard"/>). Con <paramref name="includeDeadline"/> vale anche la
    /// scadenza corrente; con <paramref name="regimeRootsOnly"/> i root senza campi del regime (ramo 001) non sono guardati sulla deadline.
    /// </summary>
    public static async Task<OwnerLock> LockAsync(
        AppDbContext db, CheckpointWriter writer, bool includeDeadline, CancellationToken ct = default, bool regimeRootsOnly = false)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Il lock dell'owner richiede una transazione.");
        }

        string Deadline(string alias) => !includeDeadline ? ""
            : " AND " + (regimeRootsOnly ? DocumentDeadlineGuard.RegimeRootAlive(alias) : DocumentDeadlineGuard.RootAlive(alias));
        // Il lock di riga sta in una CTE MATERIALIZED e il clock nel filtro esterno. Con un solo livello PostgreSQL valuta clock_timestamp() mentre
        // scorre la riga, cioè PRIMA di attendere il lock, e non lo rivaluta se la riga non è cambiata nell'attesa; una sottoquery semplice non
        // basta, perché il planner vi spinge dentro anche i predicati volatili. La CTE materializzata è una barriera: prima si prende il lock,
        // poi owner/fence/lease/scadenza si leggono con il clock successivo (T048).
        var sql = writer.OwnerKind switch
        {
            WorkOwnerKind.ProcessingRun => $"""
                WITH l AS MATERIALIZED (SELECT x.* FROM "ProcessingRuns" x WHERE x."Id" = @owner FOR UPDATE OF x)
                SELECT r."ProjectId", r."MafVersion", r."WorkflowDefinitionVersion" FROM l r
                WHERE r."Status" = 'Running' AND r."LeaseOwner" = @lease AND r."FencingToken" = @fence
                  AND r."LeaseUntil" > clock_timestamp(){Deadline("r")}
                """,
            WorkOwnerKind.ChatTurn => $"""
                WITH l AS MATERIALIZED (SELECT x.* FROM "ChatTurns" x WHERE x."Id" = @owner FOR UPDATE OF x)
                SELECT c."ProjectId", NULL::text, NULL::text FROM l t JOIN "Conversations" c ON c."Id" = t."ConversationId"
                WHERE t."Status" = 'Running' AND t."LeaseOwner" = @lease AND t."FencingToken" = @fence
                  AND t."LeaseUntil" > clock_timestamp(){Deadline("t")}
                """,
            _ => $"""
                WITH l AS MATERIALIZED (SELECT x.* FROM "DocumentJobs" x WHERE x."Id" = @owner FOR UPDATE OF x)
                SELECT j."ProjectId", NULL::text, j."WorkflowDefinitionVersion" FROM l j
                JOIN "DocumentPreparations" p ON p."Id" = j."PreparationId" AND p."ProjectId" = j."ProjectId"
                WHERE j."Status" = 'Running' AND j."LeaseOwner" = @lease AND j."FencingToken" = @fence
                  AND j."LeaseUntil" > clock_timestamp() AND {DocumentDeadlineGuard.ChildAlive("j", "p")}
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

    /// <summary>
    /// Riuso cross-root dopo un retry PM (T099; AI §5.3, data-model «RetryOfId non implica compatibilità»). Per il batch GIÀ pubblicato di questo owner cerca,
    /// nei root precedenti della sua catena di retry (RetryOfId, stesso progetto e tipo), un output Committed con lo stesso inputHash e importa solo se TUTTO
    /// coincide esattamente: hash del manifesto (fonti/cutoff, parser, config, runtime, definizione, algoritmo, policy e budget, piano), identità del batch
    /// (stage/vista/passo/livello/ordinale, genitori, id d'ingresso, schema, prompt) e schema/validazione attesi, con payload rilegg­ibile e hash valido.
    /// L'importazione è una nuova riga nell'owner corrente con <c>DerivedFromOutputHash</c> = hash dell'output di origine, nello stesso commit fenced degli altri
    /// output (stale worker, lease o padre scaduti: nulla scritto). Non invia nulla all'AI, non tocca contatori né deadline e non modifica mai il root d'origine;
    /// un output già presente nell'owner non si sovrascrive. Il solo id run rimappabile è quello del root d'origine, sostituito in modo deterministico dal nuovo;
    /// un payload su storage che lo contiene non è rimappabile e non si importa. Incompatibilità o assenza non sono errori: il chiamante esegue il lavoro normale.
    /// </summary>
    public async Task<WorkImportResult> TryImportAsync(string batchId, WorkLookupExpectation expected, CancellationToken ct)
    {
        var batch = await db.Set<DerivedBatch>().AsNoTracking()
            .SingleOrDefaultAsync(b => b.OwnerKind == writer.OwnerKind && b.OwnerId == writer.OwnerId && b.BatchId == batchId, ct)
            ?? throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Il batch da importare non è stato pubblicato.");
        var sources = await RetryLineageAsync(ct);
        if (sources.Count == 0)
        {
            return new WorkImportResult(WorkImportKind.NoCandidate, null, null, null);
        }

        var project = await WorkOwnerGuard.ProjectOfAsync(db, writer, ct);
        var manifest = await RequireManifestAsync(ct);
        var candidates = await (from b in db.Set<DerivedBatch>().AsNoTracking()
                                join o in db.Set<WorkOutput>().AsNoTracking()
                                    on new { b.OwnerKind, b.OwnerId, b.BatchId } equals new { o.OwnerKind, o.OwnerId, o.BatchId }
                                where b.OwnerKind == writer.OwnerKind && b.ProjectId == project && sources.Contains(b.OwnerId) && b.InputHash == batch.InputHash
                                select new { Batch = b, Output = o }).ToListAsync(ct);
        string? lastReason = null;
        foreach (var candidate in candidates.OrderBy(c => sources.IndexOf(c.Batch.OwnerId)))
        {
            var sourceManifest = await db.Set<WorkManifest>().AsNoTracking()
                .SingleOrDefaultAsync(m => m.OwnerKind == writer.OwnerKind && m.OwnerId == candidate.Batch.OwnerId, ct);
            var mismatch = ImportMismatch(batch, manifest, candidate.Batch, sourceManifest, candidate.Output, expected);
            if (mismatch is not null)
            {
                lastReason = mismatch;
                continue;
            }

            string payload;
            try
            {
                payload = await VerifyPayloadAsync(candidate.Output.ProjectId, candidate.Output.PayloadJson, candidate.Output.StorageKey, candidate.Output.PayloadByteLength, candidate.Output.OutputHash, ct);
            }
            catch (DocumentWorkException)
            {
                lastReason = "payload d'origine assente o non valido";
                continue;
            }

            var remapped = Remap(candidate.Output, payload, candidate.Batch.OwnerId, writer.OwnerId);
            if (remapped is null)
            {
                lastReason = "payload legato al run d'origine non rimappabile";
                return new WorkImportResult(WorkImportKind.NotRemappable, null, candidate.Batch.OwnerId, lastReason);
            }

            return await InTransactionAsync(async owner =>
            {
                int inserted;
                await using (var insert = Command(
                    """
                    INSERT INTO "WorkOutputs" ("Id","ProjectId","OwnerKind","OwnerId","BatchId","NodeId","InputHash","OutputHash","PayloadJson","StorageKey","PayloadByteLength",
                        "CoveredIdsJson","MapsJson","ExpansionReceiptJson","SchemaVersion","ManifestHash","Status","ValidationVersion","CommittedAt","CommittedFence","DerivedFromOutputHash")
                    VALUES (@id,@project,@kind,@owner,@batch,@node,@input,@output,@payload,@key,@length,@covered,@maps,@receipt,@schema,@manifest,'Committed',@validation,clock_timestamp(),@fence,@derived)
                    ON CONFLICT ("OwnerKind","OwnerId","BatchId") DO NOTHING
                    """))
                {
                    var source = candidate.Output;
                    insert.Parameters.AddWithValue("id", Guid.NewGuid());
                    insert.Parameters.AddWithValue("project", owner.ProjectId);
                    insert.Parameters.AddWithValue("kind", writer.OwnerKind.ToString());
                    insert.Parameters.AddWithValue("owner", writer.OwnerId);
                    insert.Parameters.AddWithValue("batch", batch.BatchId);
                    insert.Parameters.AddWithValue("node", source.NodeId);
                    insert.Parameters.AddWithValue("input", batch.InputHash);
                    insert.Parameters.AddWithValue("output", remapped.OutputHash);
                    Json(insert, "payload", remapped.PayloadJson);
                    insert.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Varchar) { Value = (object?)source.StorageKey ?? DBNull.Value });
                    insert.Parameters.Add(new NpgsqlParameter("length", NpgsqlDbType.Bigint) { Value = (object?)source.PayloadByteLength ?? DBNull.Value });
                    Json(insert, "covered", remapped.CoveredIdsJson);
                    Json(insert, "maps", remapped.MapsJson);
                    Json(insert, "receipt", remapped.ExpansionReceiptJson);
                    insert.Parameters.AddWithValue("schema", source.SchemaVersion);
                    insert.Parameters.AddWithValue("manifest", manifest.Hash);
                    insert.Parameters.AddWithValue("validation", source.ValidationVersion);
                    insert.Parameters.AddWithValue("fence", writer.FencingToken);
                    insert.Parameters.AddWithValue("derived", source.OutputHash);
                    inserted = await insert.ExecuteNonQueryAsync(ct);
                }

                var stored = await db.Set<WorkOutput>().AsNoTracking()
                    .SingleAsync(o => o.OwnerKind == writer.OwnerKind && o.OwnerId == writer.OwnerId && o.BatchId == batch.BatchId, ct);
                if (inserted == 1)
                {
                    return new WorkImportResult(WorkImportKind.Imported, stored, candidate.Batch.OwnerId, null);
                }

                // Già presente nell'owner: mai sovrascritto. Utilizzabile solo se è lo stesso risultato logico per lo stesso input.
                return stored.InputHash == batch.InputHash && stored.Status == WorkOutputStatus.Committed
                    ? new WorkImportResult(WorkImportKind.Replayed, stored, candidate.Batch.OwnerId, null)
                    : new WorkImportResult(WorkImportKind.Incompatible, null, candidate.Batch.OwnerId, "output già presente nell'owner con altro input");
            }, ct);
        }

        return candidates.Count == 0
            ? new WorkImportResult(WorkImportKind.NoCandidate, null, null, null)
            : new WorkImportResult(WorkImportKind.Incompatible, null, null, lastReason);
    }

    private static string? ImportMismatch(
        DerivedBatch batch, WorkManifest manifest, DerivedBatch sourceBatch, WorkManifest? sourceManifest, WorkOutput sourceOutput, WorkLookupExpectation expected)
    {
        if (sourceManifest is null || sourceManifest.Hash != manifest.Hash)
        {
            return "manifesto diverso (fonti, parser, config, runtime, algoritmo, policy o budget)";
        }

        if (!SameBatch(sourceBatch, new DerivedBatchInput(
                sourceBatch.BatchId, batch.Stage, batch.View, batch.Pass, batch.Level, batch.Ordinal, JsonSerializer.Deserialize<string[]>(batch.ParentOutputHashes)!,
                batch.InputIdsJson, batch.MeasuredTokens, batch.TokenProvenance, batch.InputHash, batch.SchemaVersion, batch.PromptVersion)))
        {
            return "identità del batch diversa (genitori, stage, schema o prompt)";
        }

        return sourceOutput.Status != WorkOutputStatus.Committed || sourceOutput.InputHash != batch.InputHash || sourceOutput.ManifestHash != sourceManifest.Hash
            || sourceOutput.SchemaVersion != expected.SchemaVersion || sourceOutput.ValidationVersion != expected.ValidationVersion
            ? "schema o validazione diversi"
            : null;
    }

    private sealed record RemappedOutput(string OutputHash, string? PayloadJson, string CoveredIdsJson, string? MapsJson, string? ExpansionReceiptJson);

    /// <summary>Sostituzione deterministica del solo id del root d'origine; null se è presente in un payload su storage (non riscrivibile).</summary>
    private static RemappedOutput? Remap(WorkOutput source, string payload, Guid from, Guid to)
    {
        string? Swap(string? text)
        {
            if (text is null)
            {
                return null;
            }

            foreach (var format in new[] { "D", "N" })
            {
                text = text.Replace(from.ToString(format), to.ToString(format), StringComparison.OrdinalIgnoreCase);
            }

            return text;
        }

        var carries = Swap(payload) != payload;
        if (carries && source.StorageKey is not null)
        {
            return null;
        }

        var inline = source.PayloadJson is null ? null : Swap(source.PayloadJson);
        return new RemappedOutput(
            inline is not null && carries ? CanonicalJson.Sha256Hex(inline) : source.OutputHash,
            inline, Swap(source.CoveredIdsJson)!, Swap(source.MapsJson), Swap(source.ExpansionReceiptJson));
    }

    /// <summary>Root precedenti nella catena RetryOfId dell'owner, dal più vicino (per un job documentale: i job delle preparazioni antenate).</summary>
    private async Task<List<Guid>> RetryLineageAsync(CancellationToken ct)
    {
        var sql = writer.OwnerKind switch
        {
            WorkOwnerKind.ProcessingRun => LineageSql("ProcessingRuns", "r"),
            WorkOwnerKind.ChatTurn => LineageSql("ChatTurns", "r"),
            _ => """
                WITH RECURSIVE chain AS (
                    SELECT p."RetryOfId" AS id, 1 AS depth FROM "DocumentJobs" j JOIN "DocumentPreparations" p ON p."Id" = j."PreparationId" WHERE j."Id" = @owner
                    UNION ALL
                    SELECT p."RetryOfId", c.depth + 1 FROM chain c JOIN "DocumentPreparations" p ON p."Id" = c.id WHERE c.id IS NOT NULL AND c.depth < 32)
                SELECT j."Id" FROM chain c JOIN "DocumentJobs" j ON j."PreparationId" = c.id WHERE c.id IS NOT NULL ORDER BY c.depth, j."RequestedAt", j."Id"
                """,
        };

        var ids = new List<Guid>();
        await db.Database.OpenConnectionAsync(ct);
        await using var command = Command(sql);
        command.Parameters.AddWithValue("owner", writer.OwnerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;

        static string LineageSql(string table, string alias) => $"""
            WITH RECURSIVE chain AS (
                SELECT {alias}."RetryOfId" AS id, 1 AS depth FROM "{table}" {alias} WHERE {alias}."Id" = @owner
                UNION ALL
                SELECT {alias}."RetryOfId", c.depth + 1 FROM chain c JOIN "{table}" {alias} ON {alias}."Id" = c.id WHERE c.id IS NOT NULL AND c.depth < 32)
            SELECT id FROM chain WHERE id IS NOT NULL ORDER BY depth
            """;
    }

    /// <summary>
    /// Consuma, una sola volta per owner, l'autorizzazione al retry guidato della bozza (T129, AI §9): pubblica il batch-marcatore sotto il lock fenced dell'owner
    /// (Running, owner, fence, lease e deadline validi). Vero solo per chi ha inserito la riga; falso se esiste già (retry già consumato: restart, recovery o altro
    /// worker non ne autorizzano un secondo). Il marcatore non è un output e non è riusabile come tale: porta solo conteggi/hash non sensibili in <c>InputIdsJson</c>.
    /// </summary>
    public Task<bool> TryClaimGuidedRetryAsync(DerivedBatchInput marker, CancellationToken ct) =>
        InTransactionAsync(async owner =>
        {
            await RequireManifestAsync(ct);
            return await InsertBatchRowAsync(owner, marker, ct) == 1;
        }, ct);

    /// <summary>
    /// Chiude un'unità di estrazione (DocumentJob ExtractUnit) sotto il fence corrente: job Completed con l'hash del risultato, rilascio del lease,
    /// porzioni Completed con risultato e, se l'unità non ha prodotto evidenze, la motivazione; le chiamate del tentativo si scrivono nella stessa
    /// transazione. NON pubblica Evidence né rende Ready la preparazione: l'unico proprietario del commit aggregato è l'aggregatore (T051).
    /// Replay su un job già Completed: il lock fenced rifiuta (nessun secondo stato finale).
    /// </summary>
    public async Task CompleteUnitAsync(string resultHash, IReadOnlyList<PortionCompletion> portions, int attempt, CancellationToken ct)
    {
        if (writer.OwnerKind != WorkOwnerKind.DocumentJob)
        {
            throw new InvalidOperationException("Il completamento di un'unità appartiene a un DocumentJob.");
        }

        RequireHash(resultHash, "resultHash dell'unità");
        var inference = InferenceScope.Current;
        await InTransactionAsync<object?>(async owner =>
        {
            int updated;
            await using (var job = Command(
                """
                UPDATE "DocumentJobs" SET "Status" = 'Completed', "ResultHash" = @hash, "CompletedAt" = clock_timestamp(), "LeaseOwner" = NULL, "LeaseUntil" = NULL
                WHERE "Id" = @id AND "Kind" = 'ExtractUnit' AND "Status" = 'Running' AND "LeaseOwner" = @lease AND "FencingToken" = @fence
                """))
            {
                job.Parameters.AddWithValue("hash", resultHash);
                job.Parameters.AddWithValue("id", writer.OwnerId);
                job.Parameters.AddWithValue("lease", writer.LeaseOwner);
                job.Parameters.AddWithValue("fence", writer.FencingToken);
                updated = await job.ExecuteNonQueryAsync(ct);
            }

            if (updated != 1)
            {
                throw new CheckpointFencedException(writer.OwnerId);
            }

            foreach (var portion in portions)
            {
                await using var command = Command(
                    """
                    UPDATE "DocumentPortions" SET "Status" = 'Completed', "JobId" = @id, "ResultHash" = @hash, "NoEvidenceReason" = @reason
                    WHERE "PreparationId" = (SELECT "PreparationId" FROM "DocumentJobs" WHERE "Id" = @id) AND "Ordinal" = @ordinal AND "Status" = 'Pending'
                    """);
                command.Parameters.AddWithValue("id", writer.OwnerId);
                command.Parameters.AddWithValue("hash", resultHash);
                command.Parameters.AddWithValue("ordinal", portion.Ordinal);
                command.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Varchar)
                {
                    Value = portion.NoEvidenceReason is { } reason ? (reason.Length <= 1_000 ? reason : reason[..1_000]) : DBNull.Value,
                });
                if (await command.ExecuteNonQueryAsync(ct) != 1)
                {
                    throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"La porzione {portion.Ordinal} non è in attesa o non appartiene alla preparazione dell'unità.");
                }
            }

            InferenceCallLog.Stage(db, inference, owner.ProjectId, new InferenceOwner(null, null, attempt, writer.FencingToken, null, writer.OwnerId), result: false);
            await db.SaveChangesAsync(ct);
            return null;
        }, ct);
        inference?.MarkWritten();
    }

    // ------------------------------------------------------------------ interni


    private async Task<IReadOnlyList<DerivedBatch>> InsertBatchesAsync(OwnerLock owner, IReadOnlyList<DerivedBatchInput> batches, CancellationToken ct)
    {
        var result = new List<DerivedBatch>(batches.Count);
        foreach (var batch in batches)
        {
            var inserted = await InsertBatchRowAsync(owner, batch, ct);
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

    private async Task<int> InsertBatchRowAsync(OwnerLock owner, DerivedBatchInput batch, CancellationToken ct)
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
        return inserted;
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
