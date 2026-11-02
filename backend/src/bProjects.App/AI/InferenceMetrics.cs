using bProjects.App.Documents;
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

/// <summary>
/// Vista «execution» di un root (contracts/http-api.md, D1/F1/F7). Solo misure e metadati: nessun campo afferma il rispetto di un target
/// temporale, perché Completed non certifica il profilo N. QueueMs/ProcessingMs sono null (unavailable) quando gli intervalli non sono osservabili.
/// </summary>
public sealed record ExecutionView(
    string? InitialRegime,
    string? Regime,
    string? ClassificationStatus,
    string? ClassificationVersion,
    string? ClassificationRecordHash,
    string? Reason,
    DateTimeOffset RequestedAt,
    DateTimeOffset? OrdinaryDeadlineAt,
    DateTimeOffset? HardDeadlineAt,
    DateTimeOffset? EffectiveDeadlineAt,
    DateTimeOffset? PromotedAt,
    string? PromotionReason,
    string? PromotionReceiptHash,
    long ElapsedMs,
    long? QueueMs,
    long? ProcessingMs,
    string? Stage,
    int? CompletedUnits,
    int? PlannedUnits);

/// <summary>Chiamate del tentativo vincente di un'unità (o del root stesso): nessuna riga di altri tentativi né dei figli.</summary>
public sealed record UnitMetricsView(
    Guid? JobId, Guid? UnitId, string Phase, string ModelId, int Attempt, int Calls, long? DurationMs,
    TokenValue PromptTokens, TokenValue CompletionTokens, TokenValue TotalTokens);

/// <summary>Diagnostica redatta di un tentativo senza risultato: solo stato e numeri, mai testo di trasporto, prompt, documento o ragionamento.</summary>
public sealed record FailedAttemptView(Guid? JobId, int Attempt, string Phase, string State, string? FinishReason, long? DurationMs);

public sealed record RootMetricsView(
    int Calls,
    IReadOnlyList<string> ModelIds,
    TokenValue PromptTokens,
    TokenValue CompletionTokens,
    TokenValue TotalTokens,
    IReadOnlyList<UnitMetricsView> Units,
    IReadOnlyList<FailedAttemptView> FailedAttempts);

public sealed record RootTelemetryView(ExecutionView Execution, RootMetricsView Metrics);

/// <summary>
/// Telemetria di un root (turno, run o preparazione) letta dal database, sempre filtrata per progetto. La durata totale è autorevole e
/// viene dai timestamp del database (t0 e chiusura, oppure il clock del database per un root in corso). Le metriche aggregano solo le chiamate
/// del tentativo vincente di ciascun proprietario (il root o, per la preparazione, ogni job figlio): le righe dei figli non si sommano mai
/// a quelle del padre. Il tempo di elaborazione è l'unione temporale degli intervalli osservati delle chiamate; basta una chiamata senza
/// durata (crash, interruzione) perché diventi unavailable. La coda non ha intervalli persistiti: resta unavailable, mai zero.
/// </summary>
public static class ExecutionTelemetry
{
    public static async Task<RootTelemetryView?> ForRootAsync(AppDbContext db, Guid projectId, ClassificationOwnerKind kind, Guid rootId, CancellationToken ct)
    {
        var root = await LoadRootAsync(db, projectId, kind, rootId, ct);
        if (root is null)
        {
            return null;
        }

        var isPreparation = kind == ClassificationOwnerKind.DocumentPreparation;
        var jobs = isPreparation
            ? await db.DocumentJobs.AsNoTracking().Where(j => j.ProjectId == projectId && j.PreparationId == rootId)
                .Select(j => new JobRow(j.Id, j.UnitId, j.Kind, j.Status, j.CompletedAt)).ToListAsync(ct)
            : [];
        var calls = await CallsOfAsync(db, projectId, kind, rootId, jobs, ct);

        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var end = root.CompletedAt
            ?? (isPreparation && root.Terminal ? jobs.Max(j => j.CompletedAt) : null)
            ?? now;
        var elapsed = (long)(end - root.RequestedAt).TotalMilliseconds;

        string? promotionHash = null;
        if (!isPreparation)
        {
            var rootKind = kind == ClassificationOwnerKind.ChatTurn ? WorkOwnerKind.ChatTurn : WorkOwnerKind.ProcessingRun;
            promotionHash = await db.RegimeTransitions.AsNoTracking()
                .Where(t => t.ProjectId == projectId && t.RootId == rootId && t.RootKind == rootKind && t.ToRegime == ExecutionRegime.Extended)
                .OrderByDescending(t => t.At).Select(t => t.DerivedPlanHash).FirstOrDefaultAsync(ct);
        }

        var recordHash = await db.ClassificationRecords.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.OwnerKind == kind && r.OwnerId == rootId)
            .OrderByDescending(r => r.Ordinal).Select(r => r.InputHash).FirstOrDefaultAsync(ct);

        var units = jobs.Where(j => j.Kind == DocumentJobKind.ExtractUnit).ToList();
        var execution = new ExecutionView(
            root.InitialRegime?.ToString(), root.Regime?.ToString(), root.ClassificationStatus?.ToString(), root.ClassificationVersion, recordHash, root.ClassificationReason,
            root.RequestedAt, root.OrdinaryDeadlineAt, root.HardDeadlineAt, root.EffectiveDeadlineAt, root.PromotedAt, root.PromotedReason, promotionHash,
            elapsed, await QueueMsAsync(db, projectId, kind, rootId, root, ct), ProcessingMs(calls), root.Stage,
            isPreparation ? units.Count(j => j.Status == DocumentJobStatus.Completed) : null,
            isPreparation ? units.Count : null);
        return new RootTelemetryView(execution, Metrics(calls, jobs));
    }

    /// <summary>Lunghezza dell'unione di intervalli [start, end) in millisecondi: nessun doppio conteggio delle sovrapposizioni.</summary>
    public static long UnionLength(IReadOnlyCollection<(long Start, long End)> intervals)
    {
        long total = 0;
        long? from = null;
        long to = 0;
        foreach (var (start, end) in intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start))
        {
            if (from is null)
            {
                (from, to) = (start, end);
            }
            else if (start <= to)
            {
                to = Math.Max(to, end);
            }
            else
            {
                total += to - from.Value;
                (from, to) = (start, end);
            }
        }

        return from is null ? total : total + (to - from.Value);
    }

    private sealed record RootRow(
        DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt, bool Terminal, ExecutionRegime? InitialRegime, ExecutionRegime? Regime, ClassificationStatus? ClassificationStatus,
        string? ClassificationVersion, string? ClassificationReason, DateTimeOffset? OrdinaryDeadlineAt, DateTimeOffset? HardDeadlineAt, DateTimeOffset? EffectiveDeadlineAt,
        DateTimeOffset? PromotedAt, string? PromotedReason, string? Stage, int Attempts);

    private sealed record JobRow(Guid Id, Guid? UnitId, DocumentJobKind Kind, DocumentJobStatus Status, DateTimeOffset? CompletedAt);

    private static async Task<RootRow?> LoadRootAsync(AppDbContext db, Guid projectId, ClassificationOwnerKind kind, Guid rootId, CancellationToken ct) => kind switch
    {
        ClassificationOwnerKind.ChatTurn => await (
            from t in db.ChatTurns.AsNoTracking()
            join c in db.Conversations.AsNoTracking() on t.ConversationId equals c.Id
            where t.Id == rootId && c.ProjectId == projectId
            select new RootRow(t.RequestedAt, t.CompletedAt, t.CompletedAt != null, t.InitialRegime, t.Regime, t.ClassificationStatus, t.ClassificationVersion, t.ClassificationReason,
                t.OrdinaryDeadlineAt, t.HardDeadlineAt, t.EffectiveDeadlineAt, t.PromotedAt, t.PromotedReason, null, t.AttemptCount)).SingleOrDefaultAsync(ct),
        ClassificationOwnerKind.ProcessingRun => await db.ProcessingRuns.AsNoTracking().Where(r => r.Id == rootId && r.ProjectId == projectId)
            .Select(r => new RootRow(r.RequestedAt, r.CompletedAt, r.CompletedAt != null, r.InitialRegime, r.Regime, r.ClassificationStatus, r.ClassificationVersion, r.ClassificationReason,
                r.OrdinaryDeadlineAt, r.HardDeadlineAt, r.EffectiveDeadlineAt, r.PromotedAt, r.PromotedReason, r.LastObservedStage, r.AttemptCount)).SingleOrDefaultAsync(ct),
        _ => await db.DocumentPreparations.AsNoTracking().Where(p => p.Id == rootId && p.ProjectId == projectId)
            .Select(p => new RootRow(p.RequestedAt, null,
                p.Status == PreparationStatus.Ready || p.Status == PreparationStatus.Incomplete || p.Status == PreparationStatus.Failed,
                p.InitialRegime, p.Regime, p.ClassificationStatus, p.ClassificationVersion, p.ClassificationReason, p.OrdinaryDeadlineAt, p.HardDeadlineAt, p.EffectiveDeadlineAt,
                p.PromotedAt, p.PromotedReason, p.Status.ToString(), 0)).SingleOrDefaultAsync(ct),
    };

    private static Task<List<InferenceCall>> CallsOfAsync(
        AppDbContext db, Guid projectId, ClassificationOwnerKind kind, Guid rootId, IReadOnlyList<JobRow> jobs, CancellationToken ct)
    {
        var rows = db.InferenceCalls.AsNoTracking().Where(c => c.ProjectId == projectId);
        if (kind == ClassificationOwnerKind.ChatTurn)
        {
            return rows.Where(c => c.ChatTurnId == rootId).ToListAsync(ct);
        }

        if (kind == ClassificationOwnerKind.ProcessingRun)
        {
            return rows.Where(c => c.ProcessingRunId == rootId).ToListAsync(ct);
        }

        var ids = jobs.Select(j => (Guid?)j.Id).ToList();
        return rows.Where(c => ids.Contains(c.DocumentJobId)).ToListAsync(ct);
    }

    /// <summary>
    /// Somma degli intervalli di coda persistiti. Solo per un root al primo tentativo: dopo un retry il backoff e il recupero non sono
    /// persistiti, quindi il totale non sarebbe completo e resta unavailable (mai un valore parziale presentato come intero).
    /// </summary>
    private static async Task<long?> QueueMsAsync(AppDbContext db, Guid projectId, ClassificationOwnerKind kind, Guid rootId, RootRow root, CancellationToken ct)
    {
        if (kind == ClassificationOwnerKind.DocumentPreparation || root.Attempts > 1)
        {
            return null;
        }

        var intervals = await db.ExecutionIntervals.AsNoTracking()
            .Where(i => i.ProjectId == projectId && i.RootKind == kind && i.RootId == rootId && i.Kind == ExecutionIntervalKind.Queue)
            .Select(i => new { i.StartedAt, i.EndedAt }).ToListAsync(ct);
        return intervals.Count == 0
            ? null
            : UnionLength([.. intervals.Select(i => (i.StartedAt.ToUnixTimeMilliseconds(), i.EndedAt.ToUnixTimeMilliseconds()))]);
    }

    /// <summary>Unione degli intervalli osservati di tutte le chiamate (anche dei tentativi falliti); null se una durata non è nota o non c'è telemetria.</summary>
    private static long? ProcessingMs(IReadOnlyList<InferenceCall> calls)
    {
        if (calls.Count == 0 || calls.Any(c => c.DurationMs is null))
        {
            return null;
        }

        return UnionLength([.. calls.Select(c => (c.StartedAt.ToUnixTimeMilliseconds(), c.StartedAt.ToUnixTimeMilliseconds() + c.DurationMs!.Value))]);
    }

    private static RootMetricsView Metrics(IReadOnlyList<InferenceCall> calls, IReadOnlyList<JobRow> jobs)
    {
        var units = new List<UnitMetricsView>();
        var failed = new List<FailedAttemptView>();
        var winning = new List<InferenceCall>();
        foreach (var owner in calls.GroupBy(c => c.DocumentJobId).OrderBy(g => g.Min(c => c.StartedAt)))
        {
            var final = owner.FirstOrDefault(c => c.IsFinal);
            var won = final is null ? [] : owner.Where(c => c.Attempt == final.Attempt && c.FencingToken == final.FencingToken).ToList();
            winning.AddRange(won);
            if (final is not null)
            {
                units.Add(new UnitMetricsView(
                    owner.Key, jobs.FirstOrDefault(j => j.Id == owner.Key)?.UnitId, InferenceMetrics.PhaseName(final.Phase), final.ModelId, final.Attempt, won.Count,
                    won.Any(c => c.DurationMs is null) ? null : won.Sum(c => c.DurationMs),
                    InferenceMetrics.Sum(won.Select(c => c.PromptTokens)), InferenceMetrics.Sum(won.Select(c => c.CompletionTokens)), InferenceMetrics.Sum(won.Select(c => c.TotalTokens))));
            }

            failed.AddRange(owner.Except(won).OrderBy(c => c.StartedAt)
                .Select(c => new FailedAttemptView(owner.Key, c.Attempt, InferenceMetrics.PhaseName(c.Phase), c.State.ToString().ToLowerInvariant(), c.FinishReason, c.DurationMs)));
        }

        return new RootMetricsView(
            winning.Count,
            [.. winning.Where(c => c.IsFinal).Select(c => c.ModelId).Distinct()],
            InferenceMetrics.Sum(winning.Select(c => c.PromptTokens)),
            InferenceMetrics.Sum(winning.Select(c => c.CompletionTokens)),
            InferenceMetrics.Sum(winning.Select(c => c.TotalTokens)),
            units,
            failed);
    }
}
