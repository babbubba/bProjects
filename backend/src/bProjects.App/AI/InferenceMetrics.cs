using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.AI;

/// <summary>
/// Valore numerico con provenienza esplicita (contracts/http-api.md, «Metriche di inferenza»): measured dal runtime, calculated da
/// valori misurati, unavailable con value null. Nessun valore è mai ricavato da lunghezza / 4.
/// </summary>
public sealed record TokenValue(int? Value, string Source)
{
    public static readonly TokenValue Unavailable = new(null, "unavailable");

    public static TokenValue Measured(int? value) => value is null ? Unavailable : new(value, "measured");

    public static TokenValue Calculated(int? value) => value is null ? Unavailable : new(value, "calculated");
}

public sealed record MaxOutputTokensView(int? Value, bool Confirmed);

public sealed record ReasoningView(bool Requested, string Effective);

public sealed record SummaryCallsView(int SummaryCalls, TokenValue SummaryPromptTokens, TokenValue SummaryCompletionTokens);

/// <summary>InferenceMetrics del contratto: chiamata finale del risultato logico, con il numero di chiamate e le sintesi del tentativo vincente.</summary>
public sealed record InferenceMetricsView(
    string State,
    string Phase,
    string ModelId,
    DateTimeOffset StartedAt,
    long? DurationMs,
    TokenValue ContextWindowTokens,
    MaxOutputTokensView MaxOutputTokens,
    int? ServerDefaultNPredict,
    ReasoningView Reasoning,
    TokenValue PromptTokens,
    TokenValue CompletionTokens,
    TokenValue TotalTokens,
    TokenValue CachedTokens,
    string? FinishReason,
    int Calls,
    SummaryCallsView? Compaction);

/// <summary>
/// Lettura delle metriche persistite, sempre filtrate per progetto. Il risultato logico (messaggio assistant, run di analisi o di
/// documento) espone solo la chiamata finale e le righe del suo tentativo; i tentativi falliti restano righe distinte non esposte.
/// Turni e run senza telemetria (precedenti alla tabella, o ripresi da un checkpoint successivo alla chiamata) danno null.
/// </summary>
public static class InferenceMetrics
{
    public static string PhaseName(AiPhase phase) => phase.ToString().ToLowerInvariant();

    public static async Task<Dictionary<Guid, InferenceMetricsView>> ForTurnsAsync(
        AppDbContext db, Guid projectId, IReadOnlyCollection<Guid> turnIds, CancellationToken ct)
    {
        if (turnIds.Count == 0)
        {
            return [];
        }

        var rows = await db.InferenceCalls.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.ChatTurnId != null && turnIds.Contains(c.ChatTurnId.Value))
            .ToListAsync(ct);
        return rows.GroupBy(r => r.ChatTurnId!.Value)
            .Select(g => (g.Key, View: Build(g.ToList())))
            .Where(x => x.View is not null)
            .ToDictionary(x => x.Key, x => x.View!);
    }

    public static async Task<InferenceMetricsView?> ForRunAsync(AppDbContext db, Guid projectId, Guid runId, CancellationToken ct) =>
        Build(await db.InferenceCalls.AsNoTracking().Where(c => c.ProjectId == projectId && c.ProcessingRunId == runId).ToListAsync(ct));

    /// <summary>Metriche di un turno o run dalle sue righe: null se nessuna chiamata finale.</summary>
    internal static InferenceMetricsView? Build(IReadOnlyCollection<InferenceCall> rows) =>
        rows.FirstOrDefault(r => r.IsFinal) is { } final
            ? View(final, [.. rows.Where(r => r.Attempt == final.Attempt && r.FencingToken == final.FencingToken)])
            : null;

    internal static InferenceMetricsView View(InferenceCall final, IReadOnlyCollection<InferenceCall> attempt)
    {
        var summaries = attempt.Where(r => r.Phase == AiPhase.Compaction).ToList();
        return new InferenceMetricsView(
            final.State.ToString().ToLowerInvariant(),
            PhaseName(final.Phase),
            final.ModelId,
            final.StartedAt,
            final.DurationMs,
            TokenValue.Measured(final.ContextWindowTokens),
            new MaxOutputTokensView(final.MaxOutputTokensRequested, Confirmed: false), // llama.cpp non conferma max_tokens
            final.ServerDefaultNPredict,
            new ReasoningView(final.ReasoningRequested, final.ReasoningEffective),
            TokenValue.Measured(final.PromptTokens),
            TokenValue.Measured(final.CompletionTokens),
            TokenValue.Measured(final.TotalTokens),
            TokenValue.Measured(final.CachedTokens),
            final.FinishReason,
            attempt.Count,
            summaries.Count == 0
                ? null
                : new SummaryCallsView(summaries.Count, Sum(summaries.Select(s => s.PromptTokens)), Sum(summaries.Select(s => s.CompletionTokens))));
    }

    /// <summary>Somma di valori misurati: measured se è uno solo, calculated se più d'uno, unavailable se ne manca anche uno.</summary>
    internal static TokenValue Sum(IEnumerable<int?> values)
    {
        var list = values.ToList();
        if (list.Count == 0 || list.Any(v => v is null))
        {
            return TokenValue.Unavailable;
        }

        return list.Count == 1 ? TokenValue.Measured(list[0]) : TokenValue.Calculated(list.Sum());
    }
}
