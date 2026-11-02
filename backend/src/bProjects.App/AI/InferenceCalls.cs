using System.Text.Json;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.AI;

/// <summary>
/// Riga di telemetria di una richiesta reale al modello (tabella InferenceCalls): solo numeri e metadati, mai testi, prompt o
/// ragionamento. Appartiene a un ChatTurn oppure a un ProcessingRun, con tentativo e fencing token del lease che l'ha eseguita.
/// I valori sono quelli del momento della chiamata (modello, finestra, config): una metrica storica non cambia con la configurazione.
/// Un token assente è null (unavailable), mai stimato.
/// </summary>
public sealed class InferenceCall
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? ChatTurnId { get; set; }
    public Guid? ProcessingRunId { get; set; }

    /// <summary>Lavoro di estrazione del registro delle evidenze (terzo proprietario possibile, esclusivo).</summary>
    public Guid? EvidenceExtractionJobId { get; set; }

    /// <summary>Lavoro documentale (quarto proprietario possibile, esclusivo): estrazione di un'unità di una preparazione.</summary>
    public Guid? DocumentJobId { get; set; }

    public int Attempt { get; set; }
    public long FencingToken { get; set; }
    public AiPhase Phase { get; set; }
    public required string ModelId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public long? DurationMs { get; set; }
    public int? ContextWindowTokens { get; set; }
    public int? MaxOutputTokensRequested { get; set; }
    public int? ServerDefaultNPredict { get; set; }
    public bool ReasoningRequested { get; set; }
    public required string ReasoningEffective { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }
    public int? CachedTokens { get; set; }
    public string? FinishReason { get; set; }
    public InferenceCallState State { get; set; }

    /// <summary>Chiamata che ha prodotto il risultato logico (messaggio assistant, revisione AI o bozza): una sola per turno/run.</summary>
    public bool IsFinal { get; set; }

    /// <summary>JSONB {items:[{key,tokens,messageCount}]}: solo sulla chiamata finale.</summary>
    public string? Composition { get; set; }

    /// <summary>JSONB {messageCount, originalTokens}: storico originale entrato nella compattazione, solo sulla chiamata finale.</summary>
    public string? History { get; set; }

    /// <summary>JSONB {at, tokensBefore, tokensAfter, messagesSummarized}: evento di compattazione del tentativo vincente, se c'è stato.</summary>
    public string? Compaction { get; set; }

    /// <summary>JSONB numerico della configurazione effettiva della chiamata, senza endpoint né credenziali.</summary>
    public required string Config { get; set; }
}

/// <summary>Esecuzione a cui appartengono le chiamate di uno scope: un turno, un run o un lavoro di estrazione, con tentativo e fencing del lease.</summary>
public sealed record InferenceOwner(Guid? ChatTurnId, Guid? ProcessingRunId, int Attempt, long FencingToken, Guid? EvidenceExtractionJobId = null, Guid? DocumentJobId = null);

/// <summary>Scrittura delle righe di un tentativo, sempre nella transazione del chiamante (commit del risultato o rilascio dopo un errore).</summary>
public static class InferenceCallLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Limiti delle colonne (AppDbContext): un valore diagnostico più lungo si tronca, mai un'inferenza riuscita che fallisce.</summary>
    public const int ModelIdMaxLength = 256;
    public const int FinishReasonMaxLength = 32;

    /// <summary>
    /// Aggiunge al contesto EF le righe delle chiamate dello scope (senza SaveChanges). Con <paramref name="result"/> la chiamata
    /// finale del tentativo vincente è marcata IsFinal con composizione, storico ed evento di compattazione. Le chiamate ancora in
    /// corso diventano interrupted. False se non c'era nulla da scrivere.
    /// </summary>
    public static bool Stage(AppDbContext db, InferenceScope? scope, Guid projectId, InferenceOwner owner, bool result)
    {
        if (scope is not { HasUnwritten: true })
        {
            return false;
        }

        var calls = scope.Calls;
        var final = result ? scope.Final : null;
        foreach (var call in calls)
        {
            call.Interrupt();
            var isFinal = ReferenceEquals(call, final);
            db.Add(new InferenceCall
            {
                Id = call.Id,
                ProjectId = projectId,
                ChatTurnId = owner.ChatTurnId,
                ProcessingRunId = owner.ProcessingRunId,
                EvidenceExtractionJobId = owner.EvidenceExtractionJobId,
                DocumentJobId = owner.DocumentJobId,
                Attempt = owner.Attempt,
                FencingToken = owner.FencingToken,
                Phase = call.Phase,
                ModelId = Truncate(call.ModelId, ModelIdMaxLength),
                StartedAt = call.StartedAt,
                DurationMs = call.DurationMs,
                ContextWindowTokens = call.ContextWindowTokens,
                MaxOutputTokensRequested = call.MaxOutputTokensRequested,
                ServerDefaultNPredict = call.ServerDefaultNPredict,
                ReasoningRequested = call.ReasoningRequested,
                ReasoningEffective = call.ReasoningEffective,
                PromptTokens = call.PromptTokens,
                CompletionTokens = call.CompletionTokens,
                TotalTokens = call.TotalTokens,
                CachedTokens = call.CachedTokens,
                FinishReason = Truncate(call.FinishReason, FinishReasonMaxLength),
                State = call.State,
                IsFinal = isFinal,
                Composition = isFinal && call.Composition is { } composition
                    ? JsonSerializer.Serialize(new { items = composition.Items }, Json)
                    : null,
                History = isFinal && scope.History is { } history ? JsonSerializer.Serialize(history, Json) : null,
                Compaction = isFinal ? CompactionOf(scope, calls, call) : null,
                Config = call.ConfigJson,
            });
        }

        return true;
    }

    /// <summary>
    /// Righe di un tentativo fallito o rifiutato dal validatore, nella transazione corrente del chiamante (il progetto è letto dal
    /// database). Nessuna riga è finale: il tentativo non ha prodotto un risultato logico.
    /// </summary>
    public static async Task AppendAttemptAsync(AppDbContext db, InferenceScope? scope, InferenceOwner owner, CancellationToken ct)
    {
        if (scope is not { HasUnwritten: true })
        {
            return;
        }

        var projectId = owner.ChatTurnId is { } turnId
            ? await (from t in db.ChatTurns
                     join c in db.Conversations on t.ConversationId equals c.Id
                     where t.Id == turnId
                     select c.ProjectId).SingleAsync(ct)
            : owner.DocumentJobId is { } documentJobId
                ? await db.DocumentJobs.Where(j => j.Id == documentJobId).Select(j => j.ProjectId).SingleAsync(ct)
            : owner.EvidenceExtractionJobId is { } jobId
                ? await db.EvidenceExtractionJobs.Where(j => j.Id == jobId).Select(j => j.ProjectId).SingleAsync(ct)
                : await db.ProcessingRuns.Where(r => r.Id == owner.ProcessingRunId).Select(r => r.ProjectId).SingleAsync(ct);

        Stage(db, scope, projectId, owner, result: false);
        await db.SaveChangesAsync(ct);
    }

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value, int max) => value is { Length: var length } && length > max ? value[..max] : value;

    /// <summary>
    /// Evento di compattazione del tentativo: token dello storico originale (prima) e dei messaggi inviati nella chiamata finale (dopo),
    /// entrambi dal tokenizer reale; il risparmio solo se coerente (dopo &lt;= prima), altrimenti assente.
    /// </summary>
    private static string? CompactionOf(InferenceScope scope, IReadOnlyList<InferenceCallRecord> calls, InferenceCallRecord final)
    {
        var summaries = calls.Where(c => c.Phase == AiPhase.Compaction && c.State == InferenceCallState.Completed).ToList();
        if (summaries.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(new
        {
            at = summaries[0].StartedAt,
            tokensBefore = scope.History?.OriginalTokens,
            tokensAfter = final.Composition?.MessageTokens,
            messagesSummarized = scope.SummarizedMessages,
        }, Json);
    }
}

/// <summary>Tipo di intervallo di attesa persistito di un root: mutuamente esclusivi fra loro e con l'elaborazione osservata (Plan §Misure).</summary>
public enum ExecutionIntervalKind
{
    Queue,
    Preflight,
    Backoff,
    Recovery,
}

/// <summary>
/// Intervallo di attesa (append-only) di un root documentale, mutuamente esclusivo con gli altri e con l'elaborazione: Preflight (misura, scritto dalla classificazione),
/// Queue (fine preflight o fine backoff -> claim) e Backoff (rilascio con retry -> NextAttemptAt). Recovery non ha ancora un produttore (T102); un intervallo non scritto resta unavailable, mai dedotto.
/// </summary>
public sealed class ExecutionInterval
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public ClassificationOwnerKind RootKind { get; set; }
    public Guid RootId { get; set; }
    public ExecutionIntervalKind Kind { get; set; }
    public int Attempt { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
}

/// <summary>Scrittura degli intervalli di attesa, con il clock del database (mai quello del processo).</summary>
public static class ExecutionIntervalStore
{
    /// <summary>
    /// Coda del tentativo appena claimato, sul clock del database. Solo per un root documentale (regime
    /// valorizzato) e solo se esiste l'intervallo che la precede (preflight o backoff).
    /// Idempotente per (root, tipo, tentativo).
    /// </summary>
    public static async Task RecordQueueAsync(AppDbContext db, ClassificationOwnerKind kind, Guid rootId, CancellationToken ct)
    {
        var (from, project) = kind switch
        {
            ClassificationOwnerKind.ChatTurn => ("\"ChatTurns\" x JOIN \"Conversations\" c ON c.\"Id\" = x.\"ConversationId\"", "c.\"ProjectId\""),
            ClassificationOwnerKind.ProcessingRun => ("\"ProcessingRuns\" x", "x.\"ProjectId\""),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "La coda del primo claim riguarda un turno o un run."),
        };
        // from/project/kind sono costanti del codice (mai input): solo l'id del root è un parametro. La coda del tentativo k parte dalla fine dell'intervallo
        // che lo precede: il preflight (k = 1) o il backoff del tentativo k-1 (k > 1). Senza quell'intervallo (per esempio un tentativo raggiunto per recupero
        // del lease) il punto di partenza non è osservabile e la coda non si scrive: resta unavailable.
        var sql = "INSERT INTO \"ExecutionIntervals\" (\"Id\",\"ProjectId\",\"RootKind\",\"RootId\",\"Kind\",\"Attempt\",\"StartedAt\",\"EndedAt\") "
            + "SELECT gen_random_uuid(), " + project + ", '" + kind + "', x.\"Id\", 'Queue', x.\"AttemptCount\", s.\"EndedAt\", GREATEST(clock_timestamp(), s.\"EndedAt\") "
            + "FROM " + from + " JOIN \"ExecutionIntervals\" s ON s.\"RootKind\" = '" + kind + "' AND s.\"RootId\" = x.\"Id\" AND "
            + "((x.\"AttemptCount\" = 1 AND s.\"Kind\" = 'Preflight' AND s.\"Attempt\" = 1) OR (x.\"AttemptCount\" > 1 AND s.\"Kind\" = 'Backoff' AND s.\"Attempt\" = x.\"AttemptCount\" - 1)) "
            + "WHERE x.\"Id\" = {0} AND x.\"InitialRegime\" IS NOT NULL "
            + "ON CONFLICT (\"RootKind\",\"RootId\",\"Kind\",\"Attempt\") DO NOTHING";
        await db.Database.ExecuteSqlRawAsync(sql, [rootId], ct);
    }

    /// <summary>
    /// Backoff del tentativo fallito e rilasciato con retry: da (NextAttemptAt - ritardo) = istante del rilascio a NextAttemptAt, entrambi sul clock del
    /// database. Scritto dal dispatcher DOPO il commit del rilascio, mai dentro la sua transazione: un guasto della telemetria non blocca il lavoro; il backoff
    /// mancante rende non disponibili backoff e coda del tentativo successivo (mai dedotti). Solo root documentali (regime valorizzato); idempotente.
    /// </summary>
    public static async Task RecordBackoffAsync(AppDbContext db, ClassificationOwnerKind kind, Guid rootId, int attempt, DateTimeOffset nextAttemptAt, TimeSpan delay, CancellationToken ct)
    {
        var (from, project) = kind switch
        {
            ClassificationOwnerKind.ChatTurn => ("\"ChatTurns\" x JOIN \"Conversations\" c ON c.\"Id\" = x.\"ConversationId\"", "c.\"ProjectId\""),
            ClassificationOwnerKind.ProcessingRun => ("\"ProcessingRuns\" x", "x.\"ProjectId\""),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "Il backoff riguarda un turno o un run."),
        };
        var sql = "INSERT INTO \"ExecutionIntervals\" (\"Id\",\"ProjectId\",\"RootKind\",\"RootId\",\"Kind\",\"Attempt\",\"StartedAt\",\"EndedAt\") "
            + "SELECT gen_random_uuid(), " + project + ", '" + kind + "', x.\"Id\", 'Backoff', {1}, {2}::timestamptz - make_interval(secs => {3}), {2}::timestamptz "
            + "FROM " + from + " WHERE x.\"Id\" = {0} AND x.\"Status\" = 'Queued' AND x.\"AttemptCount\" = {1} AND x.\"InitialRegime\" IS NOT NULL "
            + "ON CONFLICT (\"RootKind\",\"RootId\",\"Kind\",\"Attempt\") DO NOTHING";
        await db.Database.ExecuteSqlRawAsync(sql, [rootId, attempt, nextAttemptAt, delay.TotalSeconds], ct);
    }
}
