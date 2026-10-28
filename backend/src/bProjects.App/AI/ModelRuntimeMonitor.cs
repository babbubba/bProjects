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
    string? ModelStatus = null)
{
    /// <summary>Finestra configurata maggiore di quella reale: solo con una misura verificata, mai con un valore assente.</summary>
    public bool WindowMismatch =>
        Status == ModelRuntimeStatus.Online && ContextWindowTokens is { } real && ConfiguredContextWindowTokens > real;
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

    private readonly InternalModelClient _client;
    private readonly AiOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ModelRuntimeSnapshot _snapshot;
    private DateTimeOffset _validUntil = DateTimeOffset.MinValue;
    private Task<ModelRuntimeSnapshot>? _probe;

    public ModelRuntimeMonitor(InternalModelClient client, AiOptions options, ILogger<ModelRuntimeMonitor> logger, TimeProvider? time = null)
    {
        _client = client;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _snapshot = Empty(ModelRuntimeStatus.Unverified, null);
    }

    /// <summary>Ultimo stato noto, senza I/O (Unverified se mai sondato).</summary>
    public ModelRuntimeSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>Stato verificato: dalla cache finché valido, altrimenti con una sonda condivisa fra i chiamanti. Non lancia per errori del server.</summary>
    public async Task<ModelRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Task<ModelRuntimeSnapshot> probe;
        lock (_gate)
        {
            if (_time.GetUtcNow() < _validUntil)
            {
                return _snapshot;
            }

            probe = _probe ??= Task.Run(ProbeAndStoreAsync, CancellationToken.None);
        }

        return await probe.WaitAsync(cancellationToken);
    }

    /// <summary>Scarta la validità della cache (es. dopo un guasto di rete): la lettura successiva sonda di nuovo.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _validUntil = DateTimeOffset.MinValue;
        }
    }

    private async Task<ModelRuntimeSnapshot> ProbeAndStoreAsync()
    {
        var fresh = await ProbeAsync();
        ModelRuntimeSnapshot previous;
        lock (_gate)
        {
            previous = _snapshot;
            _snapshot = fresh;
            _validUntil = _time.GetUtcNow().AddSeconds(Math.Max(1, _options.RuntimeCheckSeconds));
            _probe = null;
        }

        LogChange(previous, fresh);
        return fresh;
    }

    private async Task<ModelRuntimeSnapshot> ProbeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(MaxProbeSeconds, Math.Max(1, _options.RequestTimeoutSeconds))));
        var ct = timeout.Token;

        // Prima l'elenco: in router mode un modello non caricato non ha ancora una finestra, e interrogare /props per
        // esso potrebbe avviarne il caricamento. Il server è offline solo se non risponde nemmeno qui.
        var models = await TryAsync(c => _client.ListModelsAsync(c), ct);
        if (models.Outcome == Outcome.Unreachable)
        {
            return Empty(ModelRuntimeStatus.Offline, _time.GetUtcNow());
        }

        var listed = models.Value?.FirstOrDefault(m => string.Equals(m.Id, _options.ModelId, StringComparison.Ordinal));
        if (listed?.Status is { } state && !string.Equals(state, "loaded", StringComparison.OrdinalIgnoreCase))
        {
            return Empty(ModelRuntimeStatus.Unverified, _time.GetUtcNow()) with { ModelStatus = state };
        }

        var propsTask = TryAsync(c => _client.GetServerPropsAsync(c), ct);
        var slotsTask = TryAsync(c => _client.GetSlotsAsync(c), ct);
        var props = await propsTask;
        var slots = await slotsTask;

        if (models.Outcome != Outcome.Ok && props.Outcome != Outcome.Ok)
        {
            // Il server risponde ma né l'elenco né /props sono utilizzabili (non autorizzato, endpoint assente...): non verificabile.
            return Empty(ModelRuntimeStatus.Unverified, _time.GetUtcNow());
        }

        // Finestra: /props, altrimenti il più piccolo n_ctx degli slot (stima prudente); mai la configurazione né --ctx-size.
        var slotWindows = slots.Value?.Select(s => s.ContextTokens).OfType<int>().ToList();
        var window = props.Value?.ContextTokensPerSlot ?? (slotWindows is { Count: > 0 } ? slotWindows.Min() : null);

        // -1 (o 0) è «nessun limite» per llama.cpp: non è un numero da mostrare.
        var nPredict = props.Value?.DefaultNPredict ?? listed?.ConfiguredNPredict;
        return new ModelRuntimeSnapshot(
            ModelRuntimeStatus.Online,
            _time.GetUtcNow(),
            _options.ModelId,
            _options.ContextWindowTokens,
            window is > 0 ? window : null,
            nPredict is > 0 ? nPredict : null,
            props.Value?.TotalSlots ?? (slots.Value is { Count: > 0 } known ? known.Count : null),
            listed?.Status);
    }

    private ModelRuntimeSnapshot Empty(ModelRuntimeStatus status, DateTimeOffset? checkedAt) =>
        new(status, checkedAt, _options.ModelId, _options.ContextWindowTokens);

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
/// configurata fallisce con <see cref="ContextWindowMismatchException"/> (permanente) senza inviare nulla. Offline o non verificato
/// lascia procedere: l'eventuale errore di rete è classificato transitorio dal dispatcher. Un guasto transitorio invalida la cache,
/// così il tentativo successivo rilegge il runtime (es. riavviato con un ctx-size diverso).
/// </summary>
internal sealed class ModelRuntimeGuardChatClient(IChatClient inner, ModelRuntimeMonitor runtime) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
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
        await EnsureAsync(cancellationToken);
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

    private async ValueTask EnsureAsync(CancellationToken cancellationToken)
    {
        var snapshot = await runtime.GetSnapshotAsync(cancellationToken);
        if (snapshot.WindowMismatch)
        {
            throw new ContextWindowMismatchException(snapshot.ConfiguredContextWindowTokens, snapshot.ContextWindowTokens!.Value);
        }
    }

    private static bool IsTransient(Exception e) => e is not OperationCanceledException && ModelFailure.Classify(e).IsTransient;
}
