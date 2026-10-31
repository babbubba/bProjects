using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace bProjects.App.Projects;

public sealed record ContextRuntimeView(
    string Status,
    DateTimeOffset? CheckedAt,
    string ModelId,
    TokenValue ContextWindowTokens,
    int ConfiguredContextWindowTokens,
    int? ServerDefaultNPredict,
    bool WindowMismatch);

public sealed record PhaseLimitsView(int MaxOutputTokens, bool EnableThinking, int InputBudgetTokens);

public sealed record PhasesLimitsView(PhaseLimitsView Chat, PhaseLimitsView Analysis, PhaseLimitsView Document, PhaseLimitsView Compaction);

public sealed record ContextLimitsView(int SafetyMarginTokens, PhasesLimitsView Phases);

public sealed record LastInferenceView(
    string Kind,
    string RefId,
    DateTimeOffset At,
    InferenceMetricsView Metrics,
    double? UsedPercent,
    int OutputReservedTokens,
    int SafetyMarginTokens,
    TokenValue RemainingTokens,
    string? StaleReason);

public sealed record CompositionItemView(string Key, string Label, TokenValue Tokens, int? MessageCount);

public sealed record CompositionView(TokenValue PromptTokens, IReadOnlyList<CompositionItemView> Items, int AttributedTokens, TokenValue UnattributedTokens);

public sealed record HistoryView(int MessageCount, TokenValue OriginalTokens);

public sealed record CompactionEventView(DateTimeOffset At, TokenValue TokensBefore, TokenValue TokensAfter, TokenValue TokensSaved, int MessagesSummarized);

/// <summary>ContextUsage del contratto HTTP («Metriche di inferenza e monitor del contesto»).</summary>
public sealed record ContextUsageView(
    DateTimeOffset GeneratedAt,
    ContextRuntimeView Runtime,
    ContextLimitsView Limits,
    LastInferenceView? Last,
    CompositionView? Composition,
    HistoryView History,
    CompactionEventView? Compaction);

/// <summary>
/// Monitor del contesto di un progetto: stato del runtime dalla cache del <see cref="ModelRuntimeMonitor"/> (attesa breve, mai una
/// sonda bloccante né un 5xx con il modello offline), limiti per fase dalla configurazione corrente e, dalle metriche persistite,
/// l'ultima inferenza finale (chat, analisi o documento) con composizione, storico originale e ultima compattazione. Ogni numero
/// dichiara la propria provenienza; la quota non attribuita è calcolata, mai forzata a zero.
/// </summary>
public static class ContextUsageEndpoint
{
    /// <summary>Attesa massima della sonda del runtime: oltre si risponde con l'ultimo stato noto (la sonda prosegue e aggiorna la cache).</summary>
    public static readonly TimeSpan RuntimeWait = TimeSpan.FromSeconds(2);

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [ContextComposition.SystemInstructions] = "Istruzioni di sistema",
        [ContextComposition.UserMessages] = "Messaggi del PM",
        [ContextComposition.AssistantMessages] = "Risposte dell'assistente",
        [ContextComposition.DerivedSummary] = "Riassunto derivato",
        [ContextComposition.AnalysisStructured] = "Analisi strutturata confermata",
    };

    public static IEndpointRouteBuilder MapContextUsageEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:guid}/context-usage", GetContextUsage);
        return app;
    }

    private static async Task<IResult> GetContextUsage(
        Guid projectId, HttpContext http, AppDbContext db, IOptions<AiOptions> ai, CancellationToken ct)
    {
        var conversationId = await db.Conversations.AsNoTracking().Where(c => c.ProjectId == projectId).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct);
        if (conversationId is null)
        {
            return Results.NotFound();
        }

        // Il monitor (e il client del modello) si risolvono solo per un progetto esistente: un 404 non tocca alcuna risorsa AI.
        var options = ai.Value;
        var snapshot = await RuntimeAsync(http.RequestServices.GetRequiredService<ModelRuntimeMonitor>(), ct);
        var finals = db.InferenceCalls.AsNoTracking().Where(c => c.ProjectId == projectId && c.IsFinal);
        var last = await finals.OrderByDescending(c => c.StartedAt).FirstOrDefaultAsync(ct);
        var withHistory = await finals.Where(c => c.History != null).OrderByDescending(c => c.StartedAt).FirstOrDefaultAsync(ct);
        var withCompaction = await finals.Where(c => c.Compaction != null).OrderByDescending(c => c.StartedAt).FirstOrDefaultAsync(ct);

        return Results.Ok(new ContextUsageView(
            DateTimeOffset.UtcNow,
            new ContextRuntimeView(
                snapshot.Status.ToString().ToLowerInvariant(),
                snapshot.CheckedAt,
                snapshot.ModelId,
                snapshot.Status == ModelRuntimeStatus.Online ? TokenValue.Measured(snapshot.ContextWindowTokens) : TokenValue.Unavailable,
                snapshot.ConfiguredContextWindowTokens,
                snapshot.Status == ModelRuntimeStatus.Online ? snapshot.ServerDefaultNPredict : null,
                snapshot.WindowMismatch),
            new ContextLimitsView(options.SafetyMarginTokens, new PhasesLimitsView(
                Limits(options, AiPhase.Chat), Limits(options, AiPhase.Analysis), Limits(options, AiPhase.Document), Limits(options, AiPhase.Compaction))),
            last is null ? null : await LastAsync(db, projectId, conversationId.Value, last, ct),
            last is null ? null : Composition(last),
            await HistoryAsync(db, conversationId.Value, withHistory, ct),
            withCompaction is null ? null : Compaction(withCompaction)));
    }

    private static PhaseLimitsView Limits(AiOptions options, AiPhase phase)
    {
        var settings = options.ForPhase(phase);
        return new(settings.MaxOutputTokens, settings.EnableThinking, settings.InputBudgetTokens);
    }

    private static async Task<ModelRuntimeSnapshot> RuntimeAsync(ModelRuntimeMonitor runtime, CancellationToken ct)
    {
        try
        {
            return await runtime.GetSnapshotAsync(ct).WaitAsync(RuntimeWait, ct);
        }
        catch (TimeoutException)
        {
            return runtime.Current;
        }
    }

    private static async Task<LastInferenceView> LastAsync(AppDbContext db, Guid projectId, Guid conversationId, InferenceCall final, CancellationToken ct)
    {
        var attempt = await db.InferenceCalls.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Attempt == final.Attempt && c.FencingToken == final.FencingToken
                && (final.ChatTurnId != null ? c.ChatTurnId == final.ChatTurnId : c.ProcessingRunId == final.ProcessingRunId))
            .ToListAsync(ct);

        // Riferimento e cutoff del risultato logico: il messaggio assistant del turno, oppure il run (il documento eredita il cutoff dell'analisi).
        string refId;
        long? cutoff;
        if (final.ChatTurnId is { } turnId)
        {
            var turn = await db.ChatTurns.AsNoTracking().SingleAsync(t => t.Id == turnId, ct);
            refId = (turn.ResultMessageId ?? turn.Id).ToString();
            cutoff = turn.SourceCutoffSequence;
        }
        else
        {
            var run = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == final.ProcessingRunId && r.ProjectId == projectId, ct);
            refId = run.Id.ToString();
            cutoff = run.Kind == ProcessingRunKind.Analysis
                ? run.SourceCutoffSequence
                : await db.ProcessingRuns.AsNoTracking().Where(a => a.Id == run.AnalysisId).Select(a => a.SourceCutoffSequence).SingleOrDefaultAsync(ct);
        }

        var stale = cutoff is { } c && await db.ConversationMessages.AsNoTracking()
            .AnyAsync(m => m.ConversationId == conversationId && m.Role == MessageRole.User && m.Sequence > c, ct);

        var config = ReadConfig(final.Config);
        var reserved = final.MaxOutputTokensRequested ?? config.MaxOutput;
        var margin = config.SafetyMargin;
        var ctx = final.ContextWindowTokens;
        var prompt = final.PromptTokens;
        return new LastInferenceView(
            InferenceMetrics.PhaseName(final.Phase),
            refId,
            final.StartedAt.AddMilliseconds(final.DurationMs ?? 0),
            InferenceMetrics.View(final, attempt),
            prompt is { } p && ctx is > 0 ? Math.Round(p * 100.0 / ctx.Value, 1) : null,
            reserved,
            margin,
            prompt is { } used && ctx is { } window ? TokenValue.Calculated(window - used - reserved - margin) : TokenValue.Unavailable,
            stale ? "newer_messages" : null);
    }

    /// <summary>Composizione della chiamata finale: token per categoria (tokenizer reale) e quota non attribuita = prompt - attribuiti.</summary>
    private static CompositionView? Composition(InferenceCall final)
    {
        if (final.Composition is null)
        {
            return null;
        }

        using var json = JsonDocument.Parse(final.Composition);
        var items = json.RootElement.GetProperty("items").EnumerateArray().Select(i =>
        {
            var key = i.GetProperty("key").GetString()!;
            var count = i.TryGetProperty("messageCount", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : (int?)null;
            return new CompositionItemView(key, Labels.GetValueOrDefault(key, key), TokenValue.Measured(i.GetProperty("tokens").GetInt32()), count);
        }).ToList();
        var attributed = items.Sum(i => i.Tokens.Value ?? 0);
        return new CompositionView(
            TokenValue.Measured(final.PromptTokens),
            items,
            attributed,
            final.PromptTokens is { } prompt ? TokenValue.Calculated(prompt - attributed) : TokenValue.Unavailable);
    }

    /// <summary>
    /// Storico originale come l'ha letto l'ultima inferenza di chat/analisi (messaggi e somma dei token misurati, prima della compattazione).
    /// Senza un'inferenza che lo abbia misurato: il conteggio dei messaggi conservati, token non disponibili.
    /// </summary>
    private static async Task<HistoryView> HistoryAsync(AppDbContext db, Guid conversationId, InferenceCall? final, CancellationToken ct)
    {
        if (final?.History is { } history)
        {
            using var json = JsonDocument.Parse(history);
            var tokens = json.RootElement.TryGetProperty("originalTokens", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : (int?)null;
            return new HistoryView(json.RootElement.GetProperty("messageCount").GetInt32(), TokenValue.Calculated(tokens));
        }

        return new HistoryView(await db.ConversationMessages.AsNoTracking().CountAsync(m => m.ConversationId == conversationId, ct), TokenValue.Unavailable);
    }

    private static CompactionEventView Compaction(InferenceCall final)
    {
        using var json = JsonDocument.Parse(final.Compaction!);
        var root = json.RootElement;
        int? Read(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        var before = Read("tokensBefore");
        var after = Read("tokensAfter");
        return new CompactionEventView(
            root.GetProperty("at").GetDateTimeOffset(),
            TokenValue.Calculated(before),
            TokenValue.Calculated(after),
            // Risparmio solo se coerente: una sintesi più lunga dell'originale non è un risparmio.
            before is { } b && after is { } a && a <= b ? TokenValue.Calculated(b - a) : TokenValue.Unavailable,
            Read("messagesSummarized") ?? 0);
    }

    private static (int MaxOutput, int SafetyMargin) ReadConfig(string config)
    {
        using var json = JsonDocument.Parse(config);
        var root = json.RootElement;
        return (root.GetProperty("phaseMaxOutputTokens").GetInt32(), root.GetProperty("safetyMarginTokens").GetInt32());
    }
}
