using System.Text.Json;
using bProjects.App.Persistence;
using bProjects.App.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace bProjects.App.AI;

// CAS atomica di promozione Ordinary→Extended (002 T047; Plan §Promozione tecnica e concorrenza; B1).
// Unico motivo: `additional_work`, dimostrato da una ricevuta server verificata contro WorkOutput Committed, obbligazioni ancora reali e un
// batch necessario senza output compatibile. Coda, inferenza lenta, preflight lento, retry della stessa chiamata e attesa del commit non sono
// motivi e non hanno alcun ingresso qui. Questo tipo NON è un timer né uno scheduler (T119), NON sceglie quale lavoro eseguire e NON
// pianifica: riceve una ricevuta già costruita da PrepareBatch e la verifica. Le gare integrate con timer/sweep sono T048.

/// <summary>Ricevuta immutabile di lavoro aggiuntivo (Plan): `{reason, sourceHash, triggerOutputHashes, remainingObligationIds, additionalBatchIds, derivedPlanHash}`.</summary>
public sealed record AdditionalWorkReceipt(
    string Reason,
    string SourceHash,
    IReadOnlyList<string> TriggerOutputHashes,
    IReadOnlyList<string> RemainingObligationIds,
    IReadOnlyList<string> AdditionalBatchIds,
    string DerivedPlanHash)
{
    public const string AdditionalWork = "additional_work";
}

/// <summary>
/// Segnali del root che precedono la promozione nella tabella di priorità del Plan. Sono fatti già accertati dal chiamante (errore permanente,
/// limite certo di chiamate/byte, risultato finale completo e valido): la CAS non li inferisce da tempi o code.
/// </summary>
/// <param name="PermanentError">Errore permanente osservato (schema/citazione invalidi, runtime incompatibile, limite irriducibile): il root fallisce, non si promuove.</param>
/// <param name="LimitExceeded">Il lavoro necessario eccede un limite di chiamate/byte: il root fallisce, non si promuove.</param>
/// <param name="CompleteResultReady">Risultato finale valido con copertura completa e condizioni di commit soddisfatte: il commit precede la promozione.</param>
/// <param name="MaxAiCalls">Limite chiamate del root: CallCount + batch aggiuntivi oltre il massimo è un limite superato (esattamente al massimo è ammesso).</param>
public sealed record PromotionSignals(string? PermanentError = null, string? LimitExceeded = null, bool CompleteResultReady = false, int? MaxAiCalls = null);

public sealed record PromotionRequest(
    WorkOwnerKind RootKind,
    Guid RootId,
    string LeaseOwner,
    long Fence,
    AdditionalWorkReceipt Receipt,
    IReadOnlyList<DerivedBatchInput> Batches,
    PromotionSignals? Signals = null);

public enum PromotionOutcome
{
    /// <summary>Questa chiamata ha eseguito la transizione (un solo vincitore).</summary>
    Promoted,

    /// <summary>Il root è già stato promosso (da questa o da un'altra chiamata): nessuna scrittura, nessuna seconda promozione.</summary>
    AlreadyPromoted,

    /// <summary>Priorità 1: root terminale, si rilegge il risultato.</summary>
    Terminal,

    /// <summary>Priorità 2: now ≥ scadenza corrente. A scadenza esatta prevale l'errore: né promozione né Completed.</summary>
    DeadlineExceeded,

    /// <summary>Priorità 2: errore permanente osservato.</summary>
    PermanentError,

    /// <summary>Priorità 2: lavoro necessario oltre un limite di chiamate/byte.</summary>
    LimitExceeded,

    /// <summary>Priorità 3: il risultato finale valido e completo si committa; nessuna promozione per l'attesa del commit.</summary>
    CompletionPrecedes,

    /// <summary>Owner, fence o lease non più validi (o altro worker): nulla è stato scritto.</summary>
    Fenced,

    /// <summary>Root non promuovibile: già Extended, classificazione non conclusa o root della 001.</summary>
    NotEligible,

    /// <summary>Ricevuta o batch non verificabili: rollback completo, nulla è stato scritto.</summary>
    InvalidReceipt,
}

public sealed record PromotionResult(
    PromotionOutcome Outcome, string? Reason = null, DateTimeOffset? EffectiveDeadlineAt = null, Guid? TransitionId = null, DateTimeOffset? At = null);

/// <summary>
/// Transizione di regime, append-only (trigger) e unica per root e regime di arrivo: l'unique(rootId, to=Extended) rende impossibile ogni
/// seconda promozione o reset anche se due worker arrivassero contemporaneamente. Il root è polimorfico (ChatTurn o ProcessingRun): nessuna FK.
/// </summary>
public sealed class RegimeTransition
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public WorkOwnerKind RootKind { get; set; }
    public Guid RootId { get; set; }
    public ExecutionRegime FromRegime { get; set; }
    public ExecutionRegime ToRegime { get; set; }

    /// <summary>Clock del database nell'istante della CAS.</summary>
    public DateTimeOffset At { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset OldDeadlineAt { get; set; }
    public DateTimeOffset NewDeadlineAt { get; set; }

    /// <summary>Tempo trascorso dal t0 (RequestedAt) alla transizione, in millisecondi.</summary>
    public long ElapsedMs { get; set; }
    public required string PolicyHash { get; set; }

    /// <summary>Fence del tentativo che ha eseguito la transizione (attributo del tentativo, non del root: non viene azzerato).</summary>
    public long Fence { get; set; }

    /// <summary>Ricevuta additional_work verificata (JSONB) e il suo derivedPlanHash.</summary>
    public required string ReceiptJson { get; set; }
    public required string DerivedPlanHash { get; set; }
}

/// <summary>
/// La sola transizione atomica di promozione. Una transazione corta sotto il lock del root, con il clock del database e il guard T118:
/// precedenza terminale → scadenza/errori/limiti → completamento → promozione. Persiste in modo indivisibile ricevuta, batch derivati,
/// RegimeTransition, PromotionCount 0→1 e DeadlineAt=EffectiveDeadlineAt=HardDeadlineAt; Hard, t0, fence, tentativi e contatori non si
/// azzerano. Possiede la propria transazione (non si compone con una esterna): ogni rifiuto la annulla per intero.
/// </summary>
public sealed class DocumentRegimeTransitions(AppDbContext db, ProjectEventHub? events = null)
{
    /// <summary>
    /// hash tipizzato (SHA-256 su JSON canonico, ordine normativo di AI §4.1) di ricevuta e manifesti dei batch aggiuntivi, escluso il campo
    /// derivedPlanHash stesso (nessuna dipendenza circolare). Insiemi in ordine ordinale; gli array dei genitori conservano l'ordine.
    /// </summary>
    public static string ComputeDerivedPlanHash(AdditionalWorkReceipt receipt, IReadOnlyList<DerivedBatchInput> batches)
    {
        static string[] Sorted(IEnumerable<string> values) => [.. values.OrderBy(v => v, StringComparer.Ordinal)];

        var plan = new
        {
            additionalBatchIds = Sorted(receipt.AdditionalBatchIds),
            batches = batches.OrderBy(b => b.BatchId, StringComparer.Ordinal).Select(b => new
            {
                batchId = b.BatchId, inputHash = b.InputHash, inputIds = JsonDocument.Parse(b.InputIdsJson).RootElement.Clone(), level = b.Level,
                measuredTokens = b.MeasuredTokens, ordinal = b.Ordinal, parentOutputHashes = b.ParentOutputHashes, pass = b.Pass,
                promptVersion = b.PromptVersion, schemaVersion = b.SchemaVersion, stage = b.Stage, tokenProvenance = b.TokenProvenance, view = b.View,
            }).ToArray(),
            reason = receipt.Reason,
            remainingObligationIds = Sorted(receipt.RemainingObligationIds),
            sourceHash = receipt.SourceHash,
            triggerOutputHashes = Sorted(receipt.TriggerOutputHashes),
        };
        return CanonicalJson.Sha256Hex(JsonSerializer.SerializeToElement(plan));
    }

    private sealed record RootRow(
        Guid ProjectId, string Status, string? LeaseOwner, long Fence, DateTimeOffset? LeaseUntil, DateTimeOffset DeadlineAt, DateTimeOffset? EffectiveDeadlineAt,
        DateTimeOffset? HardDeadlineAt, DateTimeOffset RequestedAt, string? Regime, string? InitialRegime, string? ClassificationStatus, int? PromotionCount,
        string? PolicyHash, int? CallCount, DateTimeOffset Now);

    public async Task<PromotionResult> PromoteAsync(PromotionRequest request, CancellationToken ct)
    {
        if (request.RootKind is not (WorkOwnerKind.ChatTurn or WorkOwnerKind.ProcessingRun))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "La promozione riguarda un ChatTurn o un ProcessingRun: la preparazione nasce Extended.");
        }

        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("La promozione possiede la propria transazione: non va composta con una esterna.");
        }

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await DecideAndPromoteAsync(request, ct);
        if (result.Outcome == PromotionOutcome.Promoted)
        {
            await transaction.CommitAsync(ct);
            await PublishRegimeChangedAsync(result);
        }
        else
        {
            await transaction.RollbackAsync(CancellationToken.None);   // qualunque rifiuto lascia il database invariato (anche i batch già inseriti)
        }

        return result;
    }

    /// <summary>Evento best-effort dopo il commit: un errore di lettura o di pubblicazione non annulla né altera la promozione già durevole (il GET resta la fonte).</summary>
    private async Task PublishRegimeChangedAsync(PromotionResult result)
    {
        if (events is null || result.TransitionId is not { } id)
        {
            return;
        }

        try
        {
            var transition = await db.RegimeTransitions.AsNoTracking().SingleAsync(t => t.Id == id, CancellationToken.None);
            events.PublishRegimeChanged(transition);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nessun log del contenuto: la transizione è già persistita e leggibile via GET.
        }
    }

    private async Task<PromotionResult> DecideAndPromoteAsync(PromotionRequest request, CancellationToken ct)
    {
        var table = request.RootKind == WorkOwnerKind.ChatTurn ? "ChatTurns" : "ProcessingRuns";
        var root = await LockRootAsync(table, request.RootId, ct);
        if (root is null)
        {
            return new(PromotionOutcome.Fenced, "root_missing");
        }

        // Priorità 1 — root terminale: si rilegge il risultato, nessuna transizione.
        if (root.Status is "Completed" or "Failed" or "Cancelled")
        {
            return new(PromotionOutcome.Terminal, root.Status);
        }

        // Priorità 2 — scadenza corrente (il minore fra la proiezione legacy e l'EffectiveDeadlineAt): a scadenza esatta prevale l'errore.
        var until = root.EffectiveDeadlineAt is { } effective && effective < root.DeadlineAt ? effective : root.DeadlineAt;
        if (until <= root.Now)
        {
            return new(PromotionOutcome.DeadlineExceeded, "deadline_exceeded", until, At: root.Now);
        }

        // Guard di owner/fence/lease (T118): chi non è il possessore corrente non decide né scrive nulla.
        if (root.Status != "Running" || root.LeaseOwner != request.LeaseOwner || root.Fence != request.Fence || root.LeaseUntil is not { } lease || lease <= root.Now)
        {
            return new(PromotionOutcome.Fenced, "owner_fence_or_lease");
        }

        // Priorità 2 — errore permanente o lavoro oltre un limite di chiamate/byte.
        var signals = request.Signals ?? new PromotionSignals();
        if (signals.PermanentError is { } permanent)
        {
            return new(PromotionOutcome.PermanentError, permanent);
        }

        var requiredCalls = request.Receipt.AdditionalBatchIds.Count;
        if (signals.LimitExceeded is { } limit
            || (signals.MaxAiCalls is { } max && (long)(root.CallCount ?? 0) + requiredCalls > max))
        {
            return new(PromotionOutcome.LimitExceeded, signals.LimitExceeded ?? "call_limit");
        }

        // Priorità 3 — un risultato finale valido e completo si committa: nessuna promozione per l'attesa del commit.
        if (signals.CompleteResultReady)
        {
            return new(PromotionOutcome.CompletionPrecedes, "complete_result_ready");
        }

        // Idoneità: solo un root documentale classificato Ordinary, mai promosso.
        if (root.PromotionCount == 1)
        {
            return new(PromotionOutcome.AlreadyPromoted, "already_promoted", until);
        }

        if (root.EffectiveDeadlineAt is null || root.HardDeadlineAt is not { } hard || root.ClassificationStatus != nameof(ClassificationStatus.Classified)
            || root.Regime != nameof(ExecutionRegime.Ordinary) || root.PromotionCount != 0 || root.PolicyHash is null)
        {
            return new(PromotionOutcome.NotEligible, root.EffectiveDeadlineAt is null ? "legacy_root" : root.Regime == nameof(ExecutionRegime.Extended) ? "already_extended" : "not_classified");
        }

        // Priorità 4 — ricevuta additional_work valida. Ogni verifica è sullo stato corrente sotto lock; nulla si fida di un booleano AI.
        try
        {
            if (await VerifyReceiptAsync(request, root, ct) is { } invalid)
            {
                return new(PromotionOutcome.InvalidReceipt, invalid);
            }

            // Batch già determinabili: stessa transazione, stesso lock del root (riusa lo store dei dati di lavoro e le sue unique/idempotenze).
            var writer = new CheckpointWriter(request.RootId, request.LeaseOwner, request.Fence, request.RootKind);
            await new DocumentWorkStore(db, writer).PublishBatchesAsync(request.Batches, ct);
        }
        catch (DocumentWorkException ex)
        {
            return new(PromotionOutcome.InvalidReceipt, ex.Code);
        }
        catch (CheckpointFencedException)
        {
            return new(PromotionOutcome.Fenced, "owner_fence_or_lease");
        }

        // CAS: l'UPDATE è condizionato a Ordinary / PromotionCount=0 / non terminale / non scaduto, quindi un solo vincitore anche senza il lock.
        var promoted = await ApplyAsync(request, root, table, hard, ct);
        return promoted;
    }

    private async Task<RootRow?> LockRootAsync(string table, Guid id, CancellationToken ct)
    {
        // Prima il lock di riga (sezione critica del root), poi la lettura con lo stesso clock del database.
        await using (var lockCommand = Command($"SELECT 1 FROM \"{table}\" WHERE \"Id\" = @id FOR UPDATE", ("id", id)))
        {
            if (await lockCommand.ExecuteScalarAsync(ct) is null)
            {
                return null;
            }
        }

        var project = table == "ChatTurns" ? "c.\"ProjectId\"" : "x.\"ProjectId\"";
        var from = table == "ChatTurns" ? "\"ChatTurns\" x JOIN \"Conversations\" c ON c.\"Id\" = x.\"ConversationId\"" : "\"ProcessingRuns\" x";
        await using var command = Command(
            $"""
            SELECT {project}, x."Status", x."LeaseOwner", x."FencingToken", x."LeaseUntil", x."DeadlineAt", x."EffectiveDeadlineAt",
                   x."HardDeadlineAt", x."RequestedAt", x."Regime", x."InitialRegime", x."ClassificationStatus", x."PromotionCount", x."PolicyHash", x."CallCount",
                   clock_timestamp()
            FROM {from} WHERE x."Id" = @id
            """,
            ("id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        static DateTimeOffset? Ts(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
        static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new RootRow(
            reader.GetGuid(0), reader.GetString(1), Str(reader, 2), reader.GetInt64(3), Ts(reader, 4), reader.GetFieldValue<DateTimeOffset>(5), Ts(reader, 6),
            Ts(reader, 7), reader.GetFieldValue<DateTimeOffset>(8), Str(reader, 9), Str(reader, 10), Str(reader, 11), reader.IsDBNull(12) ? null : reader.GetInt32(12),
            Str(reader, 13), reader.IsDBNull(14) ? null : reader.GetInt32(14), reader.GetFieldValue<DateTimeOffset>(15));
    }

    /// <summary>Verifica la ricevuta contro lo stato corrente; ritorna il motivo del rifiuto, o null se è valida.</summary>
    private async Task<string?> VerifyReceiptAsync(PromotionRequest request, RootRow root, CancellationToken ct)
    {
        var receipt = request.Receipt;
        if (receipt.Reason != AdditionalWorkReceipt.AdditionalWork)
        {
            return "reason_not_additional_work";
        }

        var kind = request.RootKind.ToString();

        // Il piano congelato: manifesto dell'owner, stessa sorgente e stessa policy del root.
        await using (var manifest = Command(
            "SELECT \"SourceHash\", \"PolicyHash\" FROM \"WorkManifests\" WHERE \"OwnerKind\" = @kind AND \"OwnerId\" = @id", ("kind", kind), ("id", request.RootId)))
        {
            await using var reader = await manifest.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return "manifest_missing";
            }

            if (reader.GetString(0) != receipt.SourceHash)
            {
                return "source_hash_mismatch";
            }

            if (reader.GetString(1) != root.PolicyHash)
            {
                return "policy_hash_mismatch";
            }
        }

        var triggers = receipt.TriggerOutputHashes.Distinct(StringComparer.Ordinal).ToArray();
        var obligations = receipt.RemainingObligationIds.Distinct(StringComparer.Ordinal).ToArray();
        var batchIds = receipt.AdditionalBatchIds.Distinct(StringComparer.Ordinal).ToArray();
        if (triggers.Length == 0 || obligations.Length == 0 || batchIds.Length == 0
            || triggers.Length != receipt.TriggerOutputHashes.Count || obligations.Length != receipt.RemainingObligationIds.Count || batchIds.Length != receipt.AdditionalBatchIds.Count
            || obligations.Any(string.IsNullOrWhiteSpace))
        {
            return "receipt_incomplete";
        }

        // Il piano derivato coincide con i batch forniti: né mancanti né in più, e hash ricalcolato dal server.
        if (!request.Batches.Select(b => b.BatchId).Order(StringComparer.Ordinal).SequenceEqual(batchIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return "batches_not_in_receipt";
        }

        if (ComputeDerivedPlanHash(receipt, request.Batches) != receipt.DerivedPlanHash)
        {
            return "derived_plan_hash_mismatch";
        }

        // Almeno un WorkOutput Committed valido del root: ogni hash di innesco deve esserlo.
        if (await ScalarAsync<long>(
            "SELECT count(DISTINCT \"OutputHash\") FROM \"WorkOutputs\" WHERE \"OwnerKind\" = @kind AND \"OwnerId\" = @id AND \"Status\" = 'Committed' AND \"OutputHash\" = ANY(@hashes)",
            ct, ("kind", kind), ("id", request.RootId), ("hashes", triggers)) != triggers.Length)
        {
            return "trigger_output_not_committed";
        }

        // Obbligazioni ancora reali: nessuna è già coperta da un output Committed.
        if (await ScalarAsync<long>(
            "SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = @kind AND \"OwnerId\" = @id AND \"Status\" = 'Committed' AND \"CoveredIdsJson\" ?| @ids",
            ct, ("kind", kind), ("id", request.RootId), ("ids", obligations)) > 0)
        {
            return "obligation_already_covered";
        }

        // Genitori dei batch aggiuntivi: tutti output Committed del root (il batch dipende da lavoro realmente concluso).
        var parents = request.Batches.SelectMany(b => b.ParentOutputHashes).Distinct(StringComparer.Ordinal).ToArray();
        if (parents.Length > 0 && await ScalarAsync<long>(
            "SELECT count(DISTINCT \"OutputHash\") FROM \"WorkOutputs\" WHERE \"OwnerKind\" = @kind AND \"OwnerId\" = @id AND \"Status\" = 'Committed' AND \"OutputHash\" = ANY(@hashes)",
            ct, ("kind", kind), ("id", request.RootId), ("hashes", parents)) != parents.Length)
        {
            return "parent_output_not_committed";
        }

        // Nessun output compatibile già Committed (cache): per lo stesso batch, oppure con lo stesso input. Output riutilizzabili non promuovono.
        var inputHashes = request.Batches.Select(b => b.InputHash).Distinct(StringComparer.Ordinal).ToArray();
        if (await ScalarAsync<long>(
            "SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = @kind AND \"OwnerId\" = @id AND \"Status\" = 'Committed' AND (\"BatchId\" = ANY(@batches) OR \"InputHash\" = ANY(@inputs))",
            ct, ("kind", kind), ("id", request.RootId), ("batches", batchIds), ("inputs", inputHashes)) > 0)
        {
            return "batch_has_compatible_output";
        }

        return null;
    }

    private async Task<PromotionResult> ApplyAsync(PromotionRequest request, RootRow root, string table, DateTimeOffset hard, CancellationToken ct)
    {
        var elapsedMs = (long)Math.Floor((root.Now - root.RequestedAt).TotalMilliseconds);
        var oldDeadline = root.EffectiveDeadlineAt!.Value;
        var kind = request.RootKind.ToString();

        // Un'unica istruzione: DeadlineAt=EffectiveDeadlineAt=HardDeadlineAt (CHECK di uguaglianza), Regime, PromotionCount 0→1 e ricevuta temporale.
        // Hard, RequestedAt, fence, AttemptCount, CallCount e OrdinaryDeadlineAt non compaiono: non si azzerano.
        await using (var update = Command(
            $"""
            UPDATE "{table}" SET "Regime" = 'Extended', "PromotionCount" = 1, "PromotedAt" = @now, "PromotedReason" = 'additional_work',
                "DeadlineAt" = "HardDeadlineAt", "EffectiveDeadlineAt" = "HardDeadlineAt"{(table == "ProcessingRuns" ? ", \"UpdatedAt\" = @now" : "")}
            WHERE "Id" = @id AND "Status" = 'Running' AND "Regime" = 'Ordinary' AND "PromotionCount" = 0 AND "ClassificationStatus" = 'Classified'
              AND "LeaseOwner" = @owner AND "FencingToken" = @fence AND LEAST("DeadlineAt", "EffectiveDeadlineAt") > clock_timestamp()
            """,
            ("now", root.Now), ("id", request.RootId), ("owner", request.LeaseOwner), ("fence", request.Fence)))
        {
            if (await update.ExecuteNonQueryAsync(ct) != 1)
            {
                return new(PromotionOutcome.AlreadyPromoted, "cas_lost");
            }
        }

        var transitionId = Guid.NewGuid();
        var receiptJson = JsonSerializer.Serialize(new
        {
            reason = request.Receipt.Reason, sourceHash = request.Receipt.SourceHash, triggerOutputHashes = request.Receipt.TriggerOutputHashes,
            remainingObligationIds = request.Receipt.RemainingObligationIds, additionalBatchIds = request.Receipt.AdditionalBatchIds,
            derivedPlanHash = request.Receipt.DerivedPlanHash,
        });
        await using (var insert = Command(
            """
            INSERT INTO "RegimeTransitions" ("Id","ProjectId","RootKind","RootId","FromRegime","ToRegime","At","Reason","OldDeadlineAt","NewDeadlineAt","ElapsedMs","PolicyHash","Fence","ReceiptJson","DerivedPlanHash")
            VALUES (@tid,@project,@kind,@id,'Ordinary','Extended',@now,'additional_work',@old,@new,@elapsed,@policy,@fence,@receipt,@plan)
            """,
            ("tid", transitionId), ("project", root.ProjectId), ("kind", kind), ("id", request.RootId), ("now", root.Now), ("old", oldDeadline), ("new", hard),
            ("elapsed", elapsedMs), ("policy", root.PolicyHash!), ("fence", request.Fence), ("plan", request.Receipt.DerivedPlanHash)))
        {
            insert.Parameters.Add(new NpgsqlParameter("receipt", NpgsqlDbType.Jsonb) { Value = receiptJson });
            await insert.ExecuteNonQueryAsync(ct);
        }

        return new(PromotionOutcome.Promoted, "additional_work", hard, transitionId, root.Now);
    }

    private async Task<T> ScalarAsync<T>(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, parameters);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync(ct))!, typeof(T));
    }

    private NpgsqlCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(
            sql, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
