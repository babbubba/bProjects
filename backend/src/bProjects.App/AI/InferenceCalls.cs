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

/// <summary>Esecuzione a cui appartengono le chiamate di uno scope: un turno oppure un run, con tentativo e fencing del lease.</summary>
public sealed record InferenceOwner(Guid? ChatTurnId, Guid? ProcessingRunId, int Attempt, long FencingToken);

/// <summary>Scrittura delle righe di un tentativo, sempre nella transazione del chiamante (commit del risultato o rilascio dopo un errore).</summary>
public static class InferenceCallLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
                Attempt = owner.Attempt,
                FencingToken = owner.FencingToken,
                Phase = call.Phase,
                ModelId = call.ModelId,
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
                FinishReason = call.FinishReason,
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
            : await db.ProcessingRuns.Where(r => r.Id == owner.ProcessingRunId).Select(r => r.ProjectId).SingleAsync(ct);

        Stage(db, scope, projectId, owner, result: false);
        await db.SaveChangesAsync(ct);
    }

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
