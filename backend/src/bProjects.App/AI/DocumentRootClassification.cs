using System.Text.Json;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace bProjects.App.AI;

// Classificazione iniziale dei root documentali (002 T124; Plan §Classificazione automatica riproducibile, policy document-regime-v2, B1).
// Il POST di chat, Analizza e Genera persiste soltanto t0 (RequestedAt), snapshot e lo stato provvisorio Pending (Extended, preflight_pending, cap dal t0):
// resta asincrono, non misura nulla e non invia nulla. Il preflight tecnico SENZA LLM gira nel dispatcher esistente, prima del primo claim: finché il
// root è Pending nessuna query di claim lo vede (DocumentDeadlineGuard.NotPending). Una sola transazione persiste ClassificationRecord append-only,
// InitialRegime, Regime, PolicyHash, reason e deadline (sempre dal RequestedAt originale); una deadline già raggiunta termina il root Failed
// deadline_exceeded senza invio. Un root già Classified non si riclassifica mai (guard, sweep e timer governano la sua vita); non c'è scheduler.

/// <summary>Misure tecniche del preflight di un root (nessuna inferenza). I campi nulli sono «non determinabili» e la policy li tratta come tali.</summary>
public sealed record RegimeMeasurement
{
    /// <summary>Input/fonti non validi (regola 1): codice d'errore specifico del root.</summary>
    public string? InvalidReason { get; init; }

    /// <summary>Impronta del runtime verificato (modello, finestra, tokenizer, template); nulla = runtime non identificabile (regola 3).</summary>
    public string? RuntimeFingerprint { get; init; }
    public int? WindowTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? MarginTokens { get; init; }
    public bool ReasoningDisabled { get; init; }

    /// <summary>Tetto di ragionamento R verificato e congelato; non esiste nella configurazione: sempre nullo oggi.</summary>
    public int? ReasoningCapTokens { get; init; }
    public int? PayloadTokens { get; init; }
    public TokenCountQuality TokenQuality { get; init; } = TokenCountQuality.Unavailable;
    public int? PlannedCalls { get; init; }
    public bool? HasDependencies { get; init; }
    public int? ShapeFloorTokens { get; init; }
    public string? ShapeSkeletonHash { get; init; }
    public ClassificationProvenance? Provenance { get; init; }
}

/// <summary>Misura i soli input tecnici di un root (runtime, tokenizer, payload, schema): mai una chiamata al modello. Sostituibile nei test.</summary>
public interface IDocumentRegimeProbe
{
    Task<RegimeMeasurement> MeasureAsync(ClassificationOwnerKind kind, Guid rootId, CancellationToken ct);
}

public enum ClassificationAttempt
{
    /// <summary>Classificato e ancora vivo: il root è claimabile.</summary>
    Classified,

    /// <summary>Il root è terminato Failed (input non valido, runtime non verificato, limite certo o deadline già raggiunta): nessun invio.</summary>
    Failed,

    /// <summary>Non Pending (già classificato da un altro worker, legacy, già terminale): nulla è stato scritto.</summary>
    NotPending,
}

public sealed record ClassificationResult(ClassificationOwnerKind Kind, Guid RootId, ClassificationAttempt Attempt, DocumentRegimeDecision? Decision);

/// <summary>Payload (se determinabile prima del claim) e schema di un root per il preflight; il profilo di runtime si risolve col preflight del workflow.</summary>
public sealed record RegimePayload(AiPhase Phase, DocumentPayload? Payload, string SchemaJson, DocumentPreflight Preflight, int PlannedCalls = 1, bool HasDependencies = false);

/// <summary>Campi provvisori Pending applicati dai POST documentali (nessuna misura, nessuna decisione).</summary>
public static class DocumentRootRegime
{
    /// <summary>
    /// Root documentale appena richiesto: Extended provvisorio con cap = t0 + ExtendedDeadlineSeconds (t0 = <paramref name="now"/>, lo stesso RequestedAt),
    /// InitialRegime non ancora definitivo (nullo), nessuna inferenza ammessa. DeadlineAt è la proiezione di EffectiveDeadlineAt (CHECK).
    /// </summary>
    public static void Pending(ChatTurn turn, DateTimeOffset now, DocumentLimits limits)
    {
        var hard = Hard(now, limits);
        turn.ClassificationStatus = ClassificationStatus.Pending;
        turn.Regime = ExecutionRegime.Extended;
        turn.InitialRegime = null;
        turn.ClassificationVersion = DocumentRegimePolicy.Version;
        turn.ClassificationReason = DocumentRegimePolicy.PendingReason;
        turn.PolicyHash = PolicyHashOf(limits);
        turn.PromotionCount = 0;
        turn.CallCount = 0;
        turn.OrdinaryDeadlineAt = null;
        turn.HardDeadlineAt = hard;
        turn.EffectiveDeadlineAt = hard;
        turn.DeadlineAt = hard;
    }

    public static void Pending(ProcessingRun run, DateTimeOffset now, DocumentLimits limits)
    {
        var hard = Hard(now, limits);
        run.ClassificationStatus = ClassificationStatus.Pending;
        run.Regime = ExecutionRegime.Extended;
        run.InitialRegime = null;
        run.ClassificationVersion = DocumentRegimePolicy.Version;
        run.ClassificationReason = DocumentRegimePolicy.PendingReason;
        run.PolicyHash = PolicyHashOf(limits);
        run.PromotionCount = 0;
        run.CallCount = 0;
        run.OrdinaryDeadlineAt = null;
        run.HardDeadlineAt = hard;
        run.EffectiveDeadlineAt = hard;
        run.DeadlineAt = hard;
    }

    private static DateTimeOffset Hard(DateTimeOffset now, DocumentLimits limits) => now.AddSeconds(limits.ExtendedDeadlineSeconds);

    private static string PolicyHashOf(DocumentLimits limits) => DocumentRegimePolicy.ComputePolicyHash(limits.OrdinaryDeadlineSeconds, limits.ExtendedDeadlineSeconds);
}

/// <summary>
/// Preflight di classificazione nel dispatcher (T124): sceglie il prossimo root Pending, lo misura senza LLM e persiste l'esito in una sola transazione
/// sotto il lock della riga del root. Due worker possono misurare lo stesso root, ma uno solo scrive (gli altri trovano il root non più Pending).
/// </summary>
public sealed class DocumentRootClassifier(IServiceScopeFactory scopes, IDocumentRegimeProbe probe)
{
    private readonly IServiceScopeFactory _scopes = scopes;
    private readonly IDocumentRegimeProbe _probe = probe;

    /// <summary>
    /// Input della policy dalle misure del preflight e dai tempi del root. <paramref name="requestedAt"/> è il t0 ORIGINALE (mai l'istante di classificazione):
    /// da lì derivano Ordinary (t0+180 s) ed Extended (cap); <paramref name="now"/> è il clock del database, usato solo per le regole 2 e per il controllo atomico
    /// della deadline. Misure non determinabili restano nulle (la policy le tratta come unknown).
    /// </summary>
    public static DocumentRegimeInput BuildInput(
        RegimeMeasurement measurement, DateTimeOffset requestedAt, DateTimeOffset hardDeadlineAt, DateTimeOffset now, DocumentLimits limits) =>
        new()
        {
            InputValid = measurement.InvalidReason is null,
            InvalidReason = measurement.InvalidReason,
            RootTerminal = false,
            RequestedAt = requestedAt,
            Now = now,
            HardDeadlineAt = hardDeadlineAt,
            ClassifiedEffectiveDeadlineAt = null,
            OrdinaryDeadlineSeconds = limits.OrdinaryDeadlineSeconds,
            RuntimeFingerprint = measurement.RuntimeFingerprint,
            WindowTokens = measurement.WindowTokens,
            OutputTokens = measurement.OutputTokens,
            MarginTokens = measurement.MarginTokens,
            IsPreparation = false,
            IrreducibleInput = false,
            CallLimitExceeded = measurement.PlannedCalls is { } planned && planned > limits.MaxAiCallsPerRun,
            PayloadTokens = measurement.PayloadTokens,
            TokenQuality = measurement.TokenQuality,
            PlannedCalls = measurement.PlannedCalls,
            HasDependencies = measurement.HasDependencies,
            ReasoningDisabled = measurement.ReasoningDisabled,
            ReasoningCapTokens = measurement.ReasoningCapTokens,
            ShapeFloorTokens = measurement.ShapeFloorTokens,
            ShapeSkeletonHash = measurement.ShapeSkeletonHash,
            Provenance = measurement.Provenance,
        };

    /// <summary>
    /// Classifica il più vecchio root Pending dei tipi che questo dispatcher sa eseguire. Null se non ce n'è. Un risultato <see cref="ClassificationAttempt.NotPending"/>
    /// (un altro worker ha vinto) è comunque un passo: il chiamante riprova il giro, il root non è più Pending.
    /// </summary>
    public async Task<ClassificationResult?> ClassifyNextAsync(bool turns, bool analysisRuns, bool documentRuns, CancellationToken ct)
    {
        (ClassificationOwnerKind Kind, Guid Id, DateTimeOffset RequestedAt)? next = null;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (turns)
            {
                var turn = await db.ChatTurns.AsNoTracking()
                    .Where(t => t.Status == ChatTurnStatus.Queued && t.ClassificationStatus == ClassificationStatus.Pending)
                    .OrderBy(t => t.RequestedAt).Select(t => new { t.Id, t.RequestedAt }).FirstOrDefaultAsync(ct);
                if (turn is not null)
                {
                    next = (ClassificationOwnerKind.ChatTurn, turn.Id, turn.RequestedAt);
                }
            }

            if (analysisRuns || documentRuns)
            {
                var run = await db.ProcessingRuns.AsNoTracking()
                    .Where(r => r.Status == ProcessingRunStatus.Queued && r.ClassificationStatus == ClassificationStatus.Pending
                        && ((analysisRuns && r.Kind == ProcessingRunKind.Analysis) || (documentRuns && r.Kind == ProcessingRunKind.Document)))
                    .OrderBy(r => r.RequestedAt).Select(r => new { r.Id, r.RequestedAt }).FirstOrDefaultAsync(ct);
                if (run is not null && (next is null || run.RequestedAt < next.Value.RequestedAt))
                {
                    next = (ClassificationOwnerKind.ProcessingRun, run.Id, run.RequestedAt);
                }
            }
        }

        return next is { } found ? await ClassifyAsync(found.Kind, found.Id, ct) : null;
    }

    private sealed record RootRow(Guid ProjectId, string Status, string? ClassificationStatus, DateTimeOffset RequestedAt, DateTimeOffset? HardDeadlineAt, DateTimeOffset Now);

    /// <summary>
    /// Classifica UN root Pending. Idempotente: un root non Pending (già classificato, legacy, terminale) non viene letto oltre né riclassificato, e la sonda non è
    /// invocata. La misura avviene fuori transazione (può chiamare il runtime); la decisione e la scrittura sotto il lock della riga, con il clock del database.
    /// </summary>
    public async Task<ClassificationResult> ClassifyAsync(ClassificationOwnerKind kind, Guid rootId, CancellationToken ct)
    {
        if (kind == ClassificationOwnerKind.DocumentPreparation)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "La preparazione nasce Extended (regola 4): non passa dal preflight dei comandi.");
        }

        ClassificationResult NotPending() => new(kind, rootId, ClassificationAttempt.NotPending, null);

        DocumentLimits limits;
        RootRow? first;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            first = await ReadAsync(db, kind, rootId, lockRow: false, ct);
            if (first is not { Status: "Queued", ClassificationStatus: "Pending", HardDeadlineAt: not null })
            {
                return NotPending();
            }

            limits = (await scope.ServiceProvider.GetRequiredService<DocumentPolicyService>().GetAsync(ct)).Snapshot.Limits;
        }

        // Il cap è congelato alla richiesta: la policy attuale deve coincidere con quella fotografata nel PolicyHash del root, altrimenti il root non si
        // reinterpreta (config_invalid). Il cap Extended è quello del root (hard - t0), mai ricalcolato.
        var extendedSeconds = (int)Math.Round((first.HardDeadlineAt!.Value - first.RequestedAt).TotalSeconds);
        var expectedPolicy = DocumentRegimePolicy.ComputePolicyHash(limits.OrdinaryDeadlineSeconds, extendedSeconds);
        var frozenPolicy = await FrozenPolicyAsync(kind, rootId, ct);
        var measurement = !string.Equals(frozenPolicy, expectedPolicy, StringComparison.Ordinal)
            ? new RegimeMeasurement { InvalidReason = "config_invalid" }
            : await MeasureAsync(kind, rootId, ct);

        using var writeScope = _scopes.CreateScope();
        var write = writeScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await write.Database.BeginTransactionAsync(ct);
        var row = await ReadAsync(write, kind, rootId, lockRow: true, ct);
        if (row is not { Status: "Queued", ClassificationStatus: "Pending", HardDeadlineAt: not null })
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return NotPending();
        }

        var input = BuildInput(measurement, row.RequestedAt, row.HardDeadlineAt.Value, row.Now, limits);
        var decision = DocumentRegimePolicy.Classify(input, extendedSeconds);
        var ordinal = (await write.ClassificationRecords.Where(r => r.OwnerKind == kind && r.OwnerId == rootId).MaxAsync(r => (int?)r.Ordinal, ct) ?? 0) + 1;
        write.ClassificationRecords.Add(DocumentRegimePolicy.ToRecord(kind, rootId, row.ProjectId, ordinal, input, decision));
        await write.SaveChangesAsync(ct);
        var failed = await ApplyAsync(write, kind, rootId, decision, ct);
        await transaction.CommitAsync(ct);
        return new(kind, rootId, failed ? ClassificationAttempt.Failed : ClassificationAttempt.Classified, decision);
    }

    private async Task<RegimeMeasurement> MeasureAsync(ClassificationOwnerKind kind, Guid rootId, CancellationToken ct)
    {
        try
        {
            return await _probe.MeasureAsync(kind, rootId, ct);
        }
        catch (DocumentWorkException ex)
        {
            return new RegimeMeasurement { InvalidReason = ex.Code };
        }
    }

    private async Task<string?> FrozenPolicyAsync(ClassificationOwnerKind kind, Guid rootId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return kind == ClassificationOwnerKind.ChatTurn
            ? await db.ChatTurns.AsNoTracking().Where(t => t.Id == rootId).Select(t => t.PolicyHash).SingleOrDefaultAsync(ct)
            : await db.ProcessingRuns.AsNoTracking().Where(r => r.Id == rootId).Select(r => r.PolicyHash).SingleOrDefaultAsync(ct);
    }

    private static async Task<RootRow?> ReadAsync(AppDbContext db, ClassificationOwnerKind kind, Guid rootId, bool lockRow, CancellationToken ct)
    {
        var (from, project, alias) = kind == ClassificationOwnerKind.ChatTurn
            ? ("\"ChatTurns\" x JOIN \"Conversations\" c ON c.\"Id\" = x.\"ConversationId\"", "c.\"ProjectId\"", "x")
            : ("\"ProcessingRuns\" x", "x.\"ProjectId\"", "x");
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT {project}, x."Status", x."ClassificationStatus", x."RequestedAt", x."HardDeadlineAt", clock_timestamp()
            FROM {from} WHERE x."Id" = @id{(lockRow ? $" FOR UPDATE OF {alias}" : "")}
            """,
            (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.AddWithValue("id", rootId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new RootRow(
                reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5))
            : null;
    }

    private static string MessageOf(string? code) => code switch
    {
        "runtime_unverified" => "Il runtime del modello non è verificato (finestra, tokenizer o template non identificabili): nessuna richiesta è stata inviata.",
        "config_invalid" => "La configurazione del budget o della policy non è valida: nessuna richiesta è stata inviata.",
        "deadline_exceeded" => "Il lavoro non è terminato entro la scadenza del comando.",
        "document_limit_exceeded" or "coverage_incomplete" => "Il lavoro supera i limiti consentiti: nessuna richiesta è stata inviata.",
        _ => "La richiesta non può essere elaborata: nessuna richiesta è stata inviata.",
    };

    /// <summary>
    /// Persiste lo stato del root nella stessa transazione del record. Ammesso: Classified con InitialRegime=Regime e deadline dal t0 originale (DeadlineAt =
    /// EffectiveDeadlineAt); deadline già raggiunta (Ordinary oltre t0+180 s) o esito Reject/Fail: terminale Failed senza invio (Reject/Fail non assegnano un
    /// regime: Rejected). Il budget (CallCount, PromotionCount) e il fence non si toccano se non per chiudere il root. Ritorna true se il root è terminato.
    /// </summary>
    private static async Task<bool> ApplyAsync(AppDbContext db, ClassificationOwnerKind kind, Guid rootId, DocumentRegimeDecision decision, CancellationToken ct)
    {
        var admitted = decision.Outcome is ClassificationOutcome.AdmitOrdinary or ClassificationOutcome.AdmitExtended && decision.Regime is not null;
        var terminal = !admitted || decision.FailDeadlineExceeded;
        var table = kind == ClassificationOwnerKind.ChatTurn ? "ChatTurns" : "ProcessingRuns";
        var set = new List<string>
        {
            "\"ClassificationStatus\" = @status", "\"ClassificationVersion\" = @version", "\"ClassificationReason\" = @reason", "\"PolicyHash\" = @policy",
        };
        var parameters = new List<NpgsqlParameter>
        {
            new("id", rootId),
            new("status", admitted ? nameof(ClassificationStatus.Classified) : nameof(ClassificationStatus.Rejected)),
            new("version", DocumentRegimePolicy.Version),
            new("reason", decision.Reason),
            new("policy", decision.PolicyHash),
        };

        if (admitted)
        {
            var regime = decision.Regime!.Value.ToString();
            set.AddRange(["\"InitialRegime\" = @regime", "\"Regime\" = @regime", "\"OrdinaryDeadlineAt\" = @ordinary", "\"EffectiveDeadlineAt\" = @effective", "\"DeadlineAt\" = @effective"]);
            parameters.Add(new("regime", regime));
            parameters.Add(new("ordinary", NpgsqlDbType.TimestampTz) { Value = decision.OrdinaryDeadlineAt is { } o ? o.ToUniversalTime() : DBNull.Value });
            parameters.Add(new("effective", NpgsqlDbType.TimestampTz) { Value = decision.EffectiveDeadlineAt!.Value.ToUniversalTime() });
        }

        if (terminal)
        {
            var code = decision.ErrorCode ?? "deadline_exceeded";
            set.AddRange(
            [
                "\"Status\" = 'Failed'", "\"ErrorCode\" = @error", "\"ErrorMessage\" = @message", "\"CompletedAt\" = clock_timestamp()", "\"LeaseOwner\" = NULL",
                "\"LeaseUntil\" = NULL", "\"FencingToken\" = \"FencingToken\" + 1",
            ]);
            if (kind == ClassificationOwnerKind.ProcessingRun)
            {
                set.Add("\"UpdatedAt\" = clock_timestamp()");
            }

            parameters.Add(new("error", code));
            parameters.Add(new("message", MessageOf(code)));
        }

        var sql = $"UPDATE \"{table}\" SET {string.Join(", ", set)} WHERE \"Id\" = @id AND \"Status\" = 'Queued' AND \"ClassificationStatus\" = 'Pending'";
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            sql, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.AddRange(parameters.ToArray());
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException("Il root non era più Pending sotto lock: classificazione non applicata.");
        }

        return terminal;
    }
}

/// <summary>Misura i soli input tecnici di un payload col preflight del workflow (runtime, tokenizer, template): nessuna chiamata al modello.</summary>
public static class RegimeMeasurer
{
    public static async Task<RegimeMeasurement> MeasureAsync(RegimePayload payload, CancellationToken ct)
    {
        try
        {
            PayloadPreflight? measure = null;
            DocumentRuntimeProfile profile;
            if (payload.Payload is { } composed)
            {
                measure = await payload.Preflight.MeasureAsync(composed, ct);
                profile = measure.Profile;
            }
            else
            {
                profile = await payload.Preflight.ResolveProfileAsync(payload.Phase, ct);
            }

            // Soglia strutturale prudenziale (shapeFloorTokens): skeleton canonico dello schema col STESSO tokenizer; senza tokenizer verificato o skeleton
            // determinabile resta sconosciuta (regola 6), mai stimata.
            var skeleton = ShapeSkeletonBuilder.Build(payload.SchemaJson);
            var floor = skeleton is null ? null : await payload.Preflight.CountVerifiedTextAsync(profile, skeleton.Json, ct);
            var counted = measure is { Quality: not TokenCountQuality.Unavailable };
            return new RegimeMeasurement
            {
                RuntimeFingerprint = profile.Fingerprint,
                WindowTokens = profile.WindowTokens,
                OutputTokens = profile.OutputTokens,
                MarginTokens = profile.MarginTokens,
                ReasoningDisabled = !profile.Thinking,
                ReasoningCapTokens = null,
                PayloadTokens = counted ? measure!.Tokens : null,
                TokenQuality = counted ? measure!.Quality : TokenCountQuality.Unavailable,
                PlannedCalls = payload.PlannedCalls,
                HasDependencies = payload.HasDependencies,
                ShapeFloorTokens = floor,
                ShapeSkeletonHash = floor is null ? null : skeleton!.Hash,
                Provenance = new("runtime", "config", "config", measure?.Quality switch
                {
                    TokenCountQuality.Measured => "tokenizer",
                    TokenCountQuality.CalibratedUpperBound => $"calibration:{profile.CalibrationId}",
                    _ => "unavailable",
                }),
            };
        }
        catch (Exception ex) when (ex is RuntimeUnverifiedException or ModelNotAvailableException or HttpRequestException)
        {
            // Finestra, modello, tokenizer o budget non dimostrati: la policy (regola 3) fallisce il root runtime_unverified prima di ogni inferenza.
            return new RegimeMeasurement { PlannedCalls = payload.PlannedCalls, HasDependencies = payload.HasDependencies };
        }
    }
}

/// <summary>Sonda di produzione: compone il payload del root col workflow documentale che lo eseguirà e lo misura (T124). Nessuna inferenza.</summary>
public sealed class WorkflowRegimeProbe(
    IServiceScopeFactory scopes, DocumentChatWorkflow chat, DocumentAnalysisWorkflow analysis, DocumentSourceDraftWorkflow draft) : IDocumentRegimeProbe
{
    public async Task<RegimeMeasurement> MeasureAsync(ClassificationOwnerKind kind, Guid rootId, CancellationToken ct)
    {
        RegimePayload payload;
        try
        {
            payload = kind switch
            {
                ClassificationOwnerKind.ChatTurn => await chat.BuildRegimePayloadAsync(rootId, ct),
                ClassificationOwnerKind.ProcessingRun => await KindOfAsync(rootId, ct) == ProcessingRunKind.Analysis
                    ? analysis.BuildRegimePayload()
                    : await draft.BuildRegimePayloadAsync(rootId, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }
        catch (DocumentWorkException ex)
        {
            return new RegimeMeasurement { InvalidReason = ex.Code };
        }
        catch (JsonException)
        {
            return new RegimeMeasurement { InvalidReason = DocumentWorkException.IntegrityConflict };
        }

        return await RegimeMeasurer.MeasureAsync(payload, ct);
    }

    private async Task<ProcessingRunKind> KindOfAsync(Guid runId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProcessingRuns.AsNoTracking()
            .Where(r => r.Id == runId).Select(r => r.Kind).SingleAsync(ct);
    }
}
