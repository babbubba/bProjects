using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

public enum ModelRuntimeStatus
{
    /// <summary>Nessuna verifica riuscita: mai sondato, oppure il server risponde ma non dà informazioni utilizzabili (o il modello non è caricato).</summary>
    Unverified,

    /// <summary>Il server ha risposto alla sonda.</summary>
    Online,

    /// <summary>Il server non è raggiungibile (rete, timeout, 5xx): nessuna capacità è nota.</summary>
    Offline,
}

/// <summary>
/// Stato verificato del runtime del modello. Ogni valore misurato è null se il server non lo ha riportato: non si usa mai
/// la configurazione al suo posto. <c>ContextWindowTokens</c> è la capacità per slot (ctx-size, /props n_ctx), distinta da
/// <c>ServerDefaultNPredict</c> (default del server, null se illimitato o non riportato) e da max_tokens (richiesto dal client).
/// </summary>
public sealed record ModelRuntimeSnapshot(
    ModelRuntimeStatus Status,
    DateTimeOffset? CheckedAt,
    string ModelId,
    int ConfiguredContextWindowTokens,
    int? ContextWindowTokens = null,
    int? ServerDefaultNPredict = null,
    int? SlotCount = null,
    string? ModelStatus = null,
    IReadOnlyList<string>? AvailableModels = null)
{
    /// <summary>Finestra configurata maggiore di quella reale: solo con una misura verificata, mai con un valore assente.</summary>
    public bool WindowMismatch =>
        Status == ModelRuntimeStatus.Online && ContextWindowTokens is { } real && ConfiguredContextWindowTokens > real;

    /// <summary>
    /// Il server ha elencato i propri modelli (GET /v1/models) e <paramref name="modelId"/> non c'è. Un modello elencato ma non
    /// caricato (router mode: unloaded/sleeping) non è mancante: lo carica il server alla richiesta. Senza elenco non si deduce nulla.
    /// </summary>
    public bool IsModelMissing(string? modelId) =>
        AvailableModels is { } listed && !string.IsNullOrEmpty(modelId) && !listed.Contains(modelId, StringComparer.Ordinal);

    /// <summary>
    /// Prontezza del modello per la classificazione (T126). Solo ModelUnloaded, ModelLoading e Unreachable sono transitori e si attendono (con tetto);
    /// modello assente, finestra diversa dalla configurata e runtime non verificabile sono permanenti. Mai dedotta dalla configurazione.
    /// </summary>
    public RuntimeReadiness Readiness
    {
        get
        {
            if (Status == ModelRuntimeStatus.Offline)
            {
                return RuntimeReadiness.Unreachable;
            }

            if (IsModelMissing(ModelId))
            {
                return RuntimeReadiness.ModelMissing;
            }

            if (ModelStatus is { } state && !string.Equals(state, "loaded", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(state, "loading", StringComparison.OrdinalIgnoreCase) ? RuntimeReadiness.ModelLoading : RuntimeReadiness.ModelUnloaded;
            }

            if (Status != ModelRuntimeStatus.Online || ContextWindowTokens is null)
            {
                return RuntimeReadiness.Unverified;
            }

            return WindowMismatch ? RuntimeReadiness.WindowMismatch : RuntimeReadiness.Ready;
        }
    }
}

/// <summary>Stato di prontezza di un modello per il preflight (T126); i motivi nel ClassificationRecord derivano da qui.</summary>
public enum RuntimeReadiness
{
    Ready,

    /// <summary>Disponibile ma non caricato (router: unloaded/sleeping).</summary>
    ModelUnloaded,

    /// <summary>In caricamento.</summary>
    ModelLoading,

    /// <summary>Server non raggiungibile.</summary>
    Unreachable,

    /// <summary>Il server elenca i modelli e quello richiesto non c'è.</summary>
    ModelMissing,

    /// <summary>Finestra reale inferiore a quella configurata.</summary>
    WindowMismatch,

    /// <summary>Il server risponde ma finestra o modello non sono dimostrabili.</summary>
    Unverified,
}

/// <summary>Esito dell'attesa di readiness: ultimo stato noto e se è scaduta la deadline del chiamante prima del tetto.</summary>
public sealed record ReadinessWait(ModelRuntimeSnapshot Snapshot, bool DeadlineReached);

/// <summary>Capacità di conteggio verificate di un modello: tokenizer interrogabile e template applicato esposto dal runtime.</summary>
public sealed record ModelCountingCapabilities(string ModelId, bool TokenizerVerified, bool TemplateExposed);

/// <summary>Il modello richiesto da una fase non è esposto dal server: nessuna richiesta inviata, nessun modello alternativo usato.</summary>
public sealed class ModelNotAvailableException(string modelId, AiPhase phase)
    : InvalidOperationException($"Modello {modelId} (fase {phase}) non esposto dal server.")
{
    public const string ErrorCode = "model_not_available";

    public string Code => ErrorCode;

    public string ModelId { get; } = modelId;

    public AiPhase Phase { get; } = phase;
}

/// <summary>La finestra reale del runtime è inferiore a quella configurata: ogni budget calcolato su di essa sarebbe falso.</summary>
public sealed class ContextWindowMismatchException(int configuredTokens, int runtimeTokens)
    : InvalidOperationException($"Finestra del runtime di {runtimeTokens} token inferiore a quella configurata di {configuredTokens}.")
{
    public const string ErrorCode = "context_window_mismatch";

    public string Code => ErrorCode;

    public int ConfiguredTokens { get; } = configuredTokens;

    public int RuntimeTokens { get; } = runtimeTokens;
}

/// <summary>
/// Sonda a richiesta del runtime del modello (<c>/v1/models</c>, <c>/props</c>, <c>/slots</c> di llama.cpp) con cache a TTL
/// (<c>Ai:RuntimeCheckSeconds</c>) e una sola sonda in volo. Non si avvia da sola né blocca l'avvio: la prima lettura la
/// esegue. Se la sonda fallisce lo stato è Offline senza capacità; nessun valore configurato è presentato come misurato.
/// </summary>
public sealed class ModelRuntimeMonitor
{
    /// <summary>Tetto del tempo complessivo di una sonda (ulteriormente limitato da <c>Ai:RequestTimeoutSeconds</c>).</summary>
    public const int MaxProbeSeconds = 10;

    /// <summary>
    /// Attese fra le sonde della readiness (T126): tetto FISSO di riprove (4) e di tempo (15 s) per modello, indipendente dal numero di richieste.
    /// Un modello non pronto dopo il tetto non si attende oltre: il root fallisce col motivo del caso.
    /// </summary>
    public static readonly TimeSpan[] ReadinessBackoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    private readonly InternalModelClient _client;
    private readonly AiOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, ModelState> _models = new(StringComparer.Ordinal);

    /// <summary>Stato verificato di un modello: snapshot, validità, sonda in volo e capacità di conteggio (tokenizer/template).</summary>
    private sealed class ModelState(ModelRuntimeSnapshot snapshot)
    {
        public ModelRuntimeSnapshot Snapshot { get; set; } = snapshot;

        public DateTimeOffset ValidUntil { get; set; } = DateTimeOffset.MinValue;

        public Task<ModelRuntimeSnapshot>? Probe { get; set; }

        public Task<ModelRuntimeSnapshot>? Readiness { get; set; }

        public Task<ModelCountingCapabilities>? Counting { get; set; }

        public DateTimeOffset CountingValidUntil { get; set; } = DateTimeOffset.MinValue;
    }

    public ModelRuntimeMonitor(InternalModelClient client, AiOptions options, ILogger<ModelRuntimeMonitor> logger, TimeProvider? time = null)
    {
        _client = client;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Ultimo stato noto del modello globale, senza I/O (Unverified se mai sondato).</summary>
    public ModelRuntimeSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return StateOf(_options.ModelId).Snapshot;
            }
        }
    }

    private ModelState StateOf(string modelId)
    {
        if (!_models.TryGetValue(modelId, out var state))
        {
            _models[modelId] = state = new ModelState(Empty(ModelRuntimeStatus.Unverified, null, modelId));
        }

        return state;
    }

    /// <summary>Stato verificato del modello globale: dalla cache finché valido, altrimenti con una sonda condivisa fra i chiamanti. Non lancia per errori del server.</summary>
    public Task<ModelRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        GetSnapshotAsync(_options.ModelId, cancellationToken);

    /// <summary>
    /// Come <see cref="GetSnapshotAsync(CancellationToken)"/> per il modello indicato (override di fase): cache, TTL e sonda in volo
    /// sono per modello, così un modello non è mai giudicato sulla finestra di un altro.
    /// </summary>
    public async Task<ModelRuntimeSnapshot> GetSnapshotAsync(string modelId, CancellationToken cancellationToken = default)
    {
        Task<ModelRuntimeSnapshot> probe;
        lock (_gate)
        {
            var state = StateOf(modelId);
            if (_time.GetUtcNow() < state.ValidUntil)
            {
                return state.Snapshot;
            }

            probe = state.Probe ??= Task.Run(() => ProbeAndStoreAsync(modelId), CancellationToken.None);
        }

        return await probe.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Attende la readiness di un modello transitoriamente non pronto (scaricato, in caricamento, server irraggiungibile) con poche sonde di backoff e tetto
    /// fisso. Un solo giro per modello, condiviso fra i chiamanti concorrenti (nessuna sonda duplicata); non invia mai inferenze né richieste di caricamento:
    /// attende solo ciò che il runtime rende pronto. <paramref name="remaining"/> è il residuo della deadline ORIGINALE del root (dal RequestedAt): il
    /// chiamante smette di attendere a quell'istante, il giro condiviso resta comunque limitato dal tetto.
    /// </summary>
    public async Task<ReadinessWait> WaitUntilReadyAsync(string modelId, TimeSpan remaining, CancellationToken cancellationToken = default)
    {
        Task<ModelRuntimeSnapshot> loop;
        lock (_gate)
        {
            var state = StateOf(modelId);
            loop = state.Readiness ??= Task.Run(() => ReadinessLoopAsync(modelId), CancellationToken.None);
        }

        try
        {
            return new(await loop.WaitAsync(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, _time, cancellationToken), false);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                return new(StateOf(modelId).Snapshot, true);
            }
        }
    }

    private async Task<ModelRuntimeSnapshot> ReadinessLoopAsync(string modelId)
    {
        try
        {
            var snapshot = await GetSnapshotAsync(modelId, CancellationToken.None);
            foreach (var pause in ReadinessBackoff)
            {
                if (snapshot.Readiness is not (RuntimeReadiness.ModelUnloaded or RuntimeReadiness.ModelLoading or RuntimeReadiness.Unreachable))
                {
                    break;
                }

                await Task.Delay(pause, _time);
                snapshot = await RefreshAsync(modelId);
            }

            return snapshot;
        }
        finally
        {
            lock (_gate)
            {
                StateOf(modelId).Readiness = null;
            }
        }
    }

    /// <summary>Nuova sonda ignorando il TTL, condivisa con una sonda già in volo.</summary>
    private Task<ModelRuntimeSnapshot> RefreshAsync(string modelId)
    {
        lock (_gate)
        {
            var state = StateOf(modelId);
            return state.Probe ??= Task.Run(() => ProbeAndStoreAsync(modelId), CancellationToken.None);
        }
    }

    /// <summary>
    /// Capacità di conteggio del modello: tokenizer interrogabile (POST /tokenize con quel modello) e template applicato esposto
    /// (POST /apply-template). Cache per modello con lo stesso TTL della sonda; un fallimento vale «non verificato», mai un default.
    /// </summary>
    public async Task<ModelCountingCapabilities> GetCountingCapabilitiesAsync(string modelId, CancellationToken cancellationToken = default)
    {
        Task<ModelCountingCapabilities> task;
        lock (_gate)
        {
            var state = StateOf(modelId);
            if (state.Counting is null || _time.GetUtcNow() >= state.CountingValidUntil)
            {
                state.Counting = Task.Run(() => ProbeCountingAsync(modelId), CancellationToken.None);
                state.CountingValidUntil = _time.GetUtcNow().AddSeconds(Math.Max(1, _options.RuntimeCheckSeconds));
            }

            task = state.Counting;
        }

        return await task.WaitAsync(cancellationToken);
    }

    private async Task<ModelCountingCapabilities> ProbeCountingAsync(string modelId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(MaxProbeSeconds, Math.Max(1, _options.RequestTimeoutSeconds))));
        var tokenizer = await TryAsync(c => _client.CountTokensAsync(modelId, "probe", c), timeout.Token);
        var template = await TryAsync(c => _client.RenderChatTemplateAsync(modelId, [("user", "probe")], false, c), timeout.Token);
        return new ModelCountingCapabilities(
            modelId, tokenizer is { Outcome: Outcome.Ok, Value: > 0 }, template is { Outcome: Outcome.Ok, Value.Length: > 0 });
    }

    /// <summary>Scarta la validità della cache di tutti i modelli (es. dopo un guasto di rete): la lettura successiva sonda di nuovo.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            foreach (var state in _models.Values)
            {
                state.ValidUntil = DateTimeOffset.MinValue;
                state.Counting = null;
            }
        }
    }

    private async Task<ModelRuntimeSnapshot> ProbeAndStoreAsync(string modelId)
    {
        var fresh = await ProbeAsync(modelId);
        ModelRuntimeSnapshot previous;
        lock (_gate)
        {
            var state = StateOf(modelId);
            previous = state.Snapshot;
            state.Snapshot = fresh;
            state.ValidUntil = _time.GetUtcNow().AddSeconds(Math.Max(1, _options.RuntimeCheckSeconds));
            state.Probe = null;
        }

        LogChange(previous, fresh);
        return fresh;
    }

    private async Task<ModelRuntimeSnapshot> ProbeAsync(string modelId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(MaxProbeSeconds, Math.Max(1, _options.RequestTimeoutSeconds))));
        var ct = timeout.Token;

        // Prima l'elenco: in router mode un modello non caricato non ha ancora una finestra, e interrogare /props per
        // esso potrebbe avviarne il caricamento. Il server è offline solo se non risponde nemmeno qui.
        var models = await TryAsync(c => _client.ListModelsAsync(c), ct);
        if (models.Outcome == Outcome.Unreachable)
        {
            return Empty(ModelRuntimeStatus.Offline, _time.GetUtcNow(), modelId);
        }

        var available = models.Outcome == Outcome.Ok ? models.Value!.Select(m => m.Id).ToList() : null;
        var listed = models.Value?.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.Ordinal));
        if (listed?.Status is { } state && !string.Equals(state, "loaded", StringComparison.OrdinalIgnoreCase))
        {
            return Empty(ModelRuntimeStatus.Unverified, _time.GetUtcNow(), modelId) with { ModelStatus = state, AvailableModels = available };
        }

        var propsTask = TryAsync(c => _client.GetServerPropsAsync(modelId, c), ct);
        var slotsTask = TryAsync(c => _client.GetSlotsAsync(modelId, c), ct);
        var props = await propsTask;
        var slots = await slotsTask;

        if (models.Outcome != Outcome.Ok && props.Outcome != Outcome.Ok)
        {
            // Il server risponde ma né l'elenco né /props sono utilizzabili (non autorizzato, endpoint assente...): non verificabile.
            return Empty(ModelRuntimeStatus.Unverified, _time.GetUtcNow(), modelId);
        }

        // Finestra: /props, altrimenti il più piccolo n_ctx degli slot (stima prudente); mai la configurazione né --ctx-size.
        var slotWindows = slots.Value?.Select(s => s.ContextTokens).OfType<int>().ToList();
        var window = props.Value?.ContextTokensPerSlot ?? (slotWindows is { Count: > 0 } ? slotWindows.Min() : null);

        // -1 (o 0) è «nessun limite» per llama.cpp: non è un numero da mostrare.
        var nPredict = props.Value?.DefaultNPredict ?? listed?.ConfiguredNPredict;
        return new ModelRuntimeSnapshot(
            ModelRuntimeStatus.Online,
            _time.GetUtcNow(),
            modelId,
            _options.ContextWindowTokens,
            window is > 0 ? window : null,
            nPredict is > 0 ? nPredict : null,
            props.Value?.TotalSlots ?? (slots.Value is { Count: > 0 } known ? known.Count : null),
            listed?.Status,
            available);
    }

    private ModelRuntimeSnapshot Empty(ModelRuntimeStatus status, DateTimeOffset? checkedAt, string modelId) =>
        new(status, checkedAt, modelId, _options.ContextWindowTokens);

    private enum Outcome
    {
        Ok,

        /// <summary>Rete, timeout o 5xx: il server non c'è (o sta caricando).</summary>
        Unreachable,

        /// <summary>Il server ha risposto ma l'endpoint è assente, disabilitato, non autorizzato o illeggibile.</summary>
        Rejected,
    }

    private readonly record struct Probe<T>(Outcome Outcome, T? Value);

    private static async Task<Probe<T>> TryAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        try
        {
            return new(Outcome.Ok, await call(ct));
        }
        catch (HttpRequestException e) when (e.StatusCode is null || ((int)e.StatusCode >= 500 && e.StatusCode != System.Net.HttpStatusCode.NotImplemented))
        {
            return new(Outcome.Unreachable, default);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException)
        {
            return new(Outcome.Unreachable, default);
        }
        catch (Exception)
        {
            return new(Outcome.Rejected, default);
        }
    }

    /// <summary>Registra il cambio di stato o di capacità rispetto alla sonda precedente: solo numeri e stati, mai contenuti.</summary>
    private void LogChange(ModelRuntimeSnapshot previous, ModelRuntimeSnapshot fresh)
    {
        if (previous.CheckedAt is not null && previous.Status != fresh.Status)
        {
            _logger.LogWarning("Runtime del modello {Previous} -> {Current}: stato precedente scartato", previous.Status, fresh.Status);
        }
        else if (previous.Status == ModelRuntimeStatus.Online && fresh.Status == ModelRuntimeStatus.Online
            && (previous.ContextWindowTokens != fresh.ContextWindowTokens || previous.ServerDefaultNPredict != fresh.ServerDefaultNPredict
                || previous.ModelId != fresh.ModelId))
        {
            _logger.LogWarning(
                "Runtime del modello cambiato: finestra {PreviousWindow} -> {Window}, n_predict {PreviousNPredict} -> {NPredict}: stato precedente scartato",
                previous.ContextWindowTokens, fresh.ContextWindowTokens, previous.ServerDefaultNPredict, fresh.ServerDefaultNPredict);
        }

        if (fresh.WindowMismatch && !(previous.WindowMismatch && previous.ContextWindowTokens == fresh.ContextWindowTokens))
        {
            _logger.LogWarning(
                "Finestra del runtime {Window} inferiore a quella configurata {Configured}: le chiamate al modello saranno rifiutate",
                fresh.ContextWindowTokens, fresh.ConfiguredContextWindowTokens);
        }
    }
}

/// <summary>
/// Anello più interno, subito prima del modello: se il runtime è verificato Online con una finestra reale inferiore a quella
/// configurata fallisce con <see cref="ContextWindowMismatchException"/> (permanente) senza inviare nulla; se il server elenca i
/// modelli e quello richiesto dalla fase (<c>ChatOptions.ModelId</c>) non c'è fallisce con <see cref="ModelNotAvailableException"/>
/// (permanente): mai un altro modello al suo posto. Offline o non verificato
/// lascia procedere: l'eventuale errore di rete è classificato transitorio dal dispatcher. Un guasto transitorio invalida la cache,
/// così il tentativo successivo rilegge il runtime (es. riavviato con un ctx-size diverso).
/// </summary>
internal sealed class ModelRuntimeGuardChatClient(IChatClient inner, ModelRuntimeMonitor runtime, AiPhase phase = AiPhase.Chat) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(options, cancellationToken);
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception e) when (IsTransient(e))
        {
            runtime.Invalidate();
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureAsync(options, cancellationToken);
        await using var updates = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await updates.MoveNextAsync())
                {
                    yield break;
                }
            }
            catch (Exception e) when (IsTransient(e))
            {
                runtime.Invalidate();
                throw;
            }

            yield return updates.Current;
        }
    }

    private async ValueTask EnsureAsync(ChatOptions? options, CancellationToken cancellationToken)
    {
        var snapshot = await runtime.GetSnapshotAsync(cancellationToken);
        if (snapshot.WindowMismatch)
        {
            throw new ContextWindowMismatchException(snapshot.ConfiguredContextWindowTokens, snapshot.ContextWindowTokens!.Value);
        }

        if (snapshot.IsModelMissing(options?.ModelId))
        {
            throw new ModelNotAvailableException(options!.ModelId!, phase);
        }
    }

    private static bool IsTransient(Exception e) => e is not OperationCanceledException && ModelFailure.Classify(e).IsTransient;
}
