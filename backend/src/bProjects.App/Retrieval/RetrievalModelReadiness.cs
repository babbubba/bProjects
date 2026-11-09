using System.Net.Http.Headers;
using System.Text.Json;
using bProjects.App.AI;

namespace bProjects.App.Retrieval;

/// <summary>
/// Stato di prontezza di un modello del recupero (contracts/model-readiness.md §2). Riusa lo schema di <see cref="RuntimeReadiness"/> (T126) per i sei stati
/// comuni — vedi <see cref="RetrievalModelStateExtensions.ToRuntimeReadiness"/> — più i due specifici del contenuto del modello.
/// </summary>
public enum RetrievalModelState
{
    Ready,
    ModelUnloaded,
    ModelLoading,
    Unreachable,
    ModelMissing,

    /// <summary>Il server risponde ma nome/artefatto non sono dimostrabili.</summary>
    Unverified,

    /// <summary>L'artefatto riportato dal runtime non è quello atteso.</summary>
    ArtifactMismatch,

    /// <summary>Embedding: la risposta di prova ha una dimensione diversa da quella attesa.</summary>
    DimensionMismatch,
}

public static class RetrievalModelStateExtensions
{
    /// <summary>Stato equivalente di T126; i due stati specifici (artefatto, dimensione) non hanno corrispondente: il modello non è dimostrato ⇒ <see cref="RuntimeReadiness.Unverified"/>.</summary>
    public static RuntimeReadiness ToRuntimeReadiness(this RetrievalModelState state) => state switch
    {
        RetrievalModelState.Ready => RuntimeReadiness.Ready,
        RetrievalModelState.ModelUnloaded => RuntimeReadiness.ModelUnloaded,
        RetrievalModelState.ModelLoading => RuntimeReadiness.ModelLoading,
        RetrievalModelState.Unreachable => RuntimeReadiness.Unreachable,
        RetrievalModelState.ModelMissing => RuntimeReadiness.ModelMissing,
        _ => RuntimeReadiness.Unverified,
    };

    /// <summary>Solo scaricato, in caricamento e irraggiungibile sono transitori e si attendono (con tetto); il resto è permanente.</summary>
    public static bool IsTransient(this RetrievalModelState state) =>
        state is RetrievalModelState.ModelUnloaded or RetrievalModelState.ModelLoading or RetrievalModelState.Unreachable;
}

/// <summary>
/// Snapshot della readiness: modello, artefatto (osservato dal runtime, null se non riportato), dimensione (osservata dalla risposta di prova, null se non
/// provata), <see cref="Sha256Status"/> e istante della sonda. Nessun valore è dedotto dalla configurazione.
/// </summary>
public sealed record RetrievalModelSnapshot(
    RetrievalModelState State,
    DateTimeOffset? CheckedAt,
    string ModelId,
    string ExpectedArtifact,
    string? Artifact = null,
    int? Dimension = null,
    Sha256Status Sha256Status = Sha256Status.NotVerified)
{
    public bool IsReady => State == RetrievalModelState.Ready;
}

/// <summary>Esito dell'attesa di readiness: ultimo stato noto e se è scaduta la deadline del chiamante prima del tetto (come <see cref="ReadinessWait"/>).</summary>
public sealed record RetrievalReadinessWait(RetrievalModelSnapshot Snapshot, bool DeadlineReached);

/// <summary>
/// Readiness del modello di embedding (003 T008, FR-034, D-5). Sonda a richiesta (<c>/v1/models</c>, <c>/props</c> e UNA richiesta di embedding di prova per
/// la dimensione) con cache a TTL (<see cref="RetrievalOptions.RuntimeCheckSeconds"/>) e una sola sonda in volo; stesse attese e tetto di
/// <see cref="ModelRuntimeMonitor.ReadinessBackoff"/>. Non carica né scarica modelli e non sostituisce mai il modello: nome, artefatto e dimensione sono configurazione
/// fissata e una difformità è «non pronto». Difformità di nome/artefatto/dimensione ⇒ nessuna richiesta di embedding di contenuto (<see cref="EnsureEmbeddingReadyAsync"/>).
/// </summary>
public sealed class RetrievalModelReadiness : IDisposable
{
    private readonly RetrievalOptions _options;
    private readonly EmbeddingClient _embedding;
    private readonly HttpClient _http;
    private readonly Uri _serverRoot;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private RetrievalModelSnapshot _snapshot;
    private DateTimeOffset _validUntil = DateTimeOffset.MinValue;
    private Task<RetrievalModelSnapshot>? _probe;
    private Task<RetrievalModelSnapshot>? _wait;

    public RetrievalModelReadiness(RetrievalOptions options, EmbeddingClient embedding, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        _options = options;
        _embedding = embedding;
        _time = time ?? TimeProvider.System;
        var endpoint = new Uri(options.Embedding.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        _serverRoot = new Uri(endpoint, "/");
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        if (!string.IsNullOrEmpty(options.Embedding.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Embedding.ApiKey);
        }

        _snapshot = Empty(RetrievalModelState.Unverified, null);
    }

    /// <summary>Ultimo stato noto, senza I/O (Unverified se mai sondato).</summary>
    public RetrievalModelSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>Stato verificato dell'embedding: dalla cache finché valido, altrimenti con una sonda condivisa fra i chiamanti. Non lancia per errori del server.</summary>
    public async Task<RetrievalModelSnapshot> GetEmbeddingSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Task<RetrievalModelSnapshot> probe;
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

    /// <summary>
    /// Attende la readiness di un modello transitoriamente non pronto (scaricato, in caricamento, irraggiungibile) con poche sonde e tetto fisso; un solo giro condiviso
    /// fra i chiamanti. Non invia richieste di caricamento. <paramref name="remaining"/> è il residuo della deadline del chiamante.
    /// </summary>
    public async Task<RetrievalReadinessWait> WaitUntilEmbeddingReadyAsync(TimeSpan remaining, CancellationToken cancellationToken = default)
    {
        Task<RetrievalModelSnapshot> loop;
        lock (_gate)
        {
            loop = _wait ??= Task.Run(WaitLoopAsync, CancellationToken.None);
        }

        try
        {
            return new(await loop.WaitAsync(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, _time, cancellationToken), false);
        }
        catch (TimeoutException)
        {
            return new(Current, true);
        }
    }

    /// <summary>Lancia l'errore tipizzato del recupero se l'embedding non è pronto; in caso contrario restituisce lo snapshot (da registrare nel registro e nei rapporti).</summary>
    public async Task<RetrievalModelSnapshot> EnsureEmbeddingReadyAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await GetEmbeddingSnapshotAsync(cancellationToken);
        return snapshot.IsReady ? snapshot : throw ToException(snapshot);
    }

    /// <summary>Mappatura verso gli errori tipizzati (contracts/model-readiness.md §2).</summary>
    public static RetrievalException ToException(RetrievalModelSnapshot snapshot) => snapshot.State switch
    {
        RetrievalModelState.ModelMissing or RetrievalModelState.ArtifactMismatch => new(
            RetrievalException.EmbeddingModelMismatch,
            $"Il modello di embedding {snapshot.ModelId} non corrisponde alla configurazione (nome o artefatto atteso {snapshot.ExpectedArtifact}): nessuna richiesta inviata."),
        RetrievalModelState.DimensionMismatch => new(
            RetrievalException.EmbeddingDimensionMismatch,
            $"Il modello di embedding ha una dimensione dei vettori diversa da {RetrievalOptions.FixedDimension}: nessuna richiesta inviata."),
        _ => new(
            RetrievalException.EmbeddingUnavailable,
            $"Il servizio di embedding non è pronto ({snapshot.State}): riprovare più tardi; nessun servizio alternativo è usato."),
    };

    /// <summary>Scarta la validità della cache: la lettura successiva sonda di nuovo.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _validUntil = DateTimeOffset.MinValue;
        }
    }

    private async Task<RetrievalModelSnapshot> WaitLoopAsync()
    {
        try
        {
            var snapshot = await GetEmbeddingSnapshotAsync(CancellationToken.None);
            foreach (var pause in ModelRuntimeMonitor.ReadinessBackoff)
            {
                if (!snapshot.State.IsTransient())
                {
                    break;
                }

                await Task.Delay(pause, _time);
                Invalidate();
                snapshot = await GetEmbeddingSnapshotAsync(CancellationToken.None);
            }

            return snapshot;
        }
        finally
        {
            lock (_gate)
            {
                _wait = null;
            }
        }
    }

    private async Task<RetrievalModelSnapshot> ProbeAndStoreAsync()
    {
        RetrievalModelSnapshot fresh;
        try
        {
            fresh = await ProbeAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            fresh = Empty(RetrievalModelState.Unverified, _time.GetUtcNow());   // risposta incomprensibile: non dimostrabile, mai «pronto»
        }

        lock (_gate)
        {
            _snapshot = fresh;
            _validUntil = _time.GetUtcNow().AddSeconds(Math.Max(1, _options.RuntimeCheckSeconds));
            _probe = null;
        }

        return fresh;
    }

    private async Task<RetrievalModelSnapshot> ProbeAsync()
    {
        var model = _options.Embedding;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(ModelRuntimeMonitor.MaxProbeSeconds, Math.Max(1, _options.TimeoutSeconds))));
        var ct = timeout.Token;
        var now = _time.GetUtcNow();

        // 1. Elenco dei modelli: prima di /props, che in router mode potrebbe avviare il caricamento di un modello non caricato.
        ListedModel[]? listed;
        try
        {
            listed = await ListModelsAsync(ct);
        }
        catch (Exception e) when (IsUnreachable(e))
        {
            return Empty(RetrievalModelState.Unreachable, now);
        }

        if (listed is null)
        {
            return Empty(RetrievalModelState.Unverified, now);   // il server risponde ma l'elenco non è utilizzabile
        }

        var entry = listed.FirstOrDefault(m => string.Equals(m.Id, model.ModelId, StringComparison.Ordinal));
        if (entry is null)
        {
            return Empty(RetrievalModelState.ModelMissing, now);
        }

        if (entry.Status is { } status && !string.Equals(status, "loaded", StringComparison.OrdinalIgnoreCase))
        {
            return Empty(string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase) ? RetrievalModelState.ModelLoading : RetrievalModelState.ModelUnloaded, now);
        }

        // 2. Artefatto: dai parametri del modello elencato (router mode) o, in alternativa, da /props. Non riportato ⇒ non dimostrabile.
        var artifact = entry.Artifact;
        if (artifact is null)
        {
            try
            {
                artifact = await GetPropsArtifactAsync(model.ModelId, ct);
            }
            catch (Exception e) when (IsUnreachable(e))
            {
                return Empty(RetrievalModelState.Unreachable, now);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException)
            {
                artifact = null;
            }
        }

        var sha = model.ResolveSha256Status(entry.Sha256);
        if (artifact is null)
        {
            return Empty(RetrievalModelState.Unverified, now) with { Sha256Status = sha };
        }

        if (!string.Equals(artifact, model.Artifact, StringComparison.Ordinal))
        {
            return Empty(RetrievalModelState.ArtifactMismatch, now) with { Artifact = artifact, Sha256Status = sha };
        }

        // 3. Dimensione: UNA risposta di prova; nessuna richiesta di contenuto è mai inviata prima di questo punto.
        int dimension;
        try
        {
            dimension = await _embedding.ProbeDimensionAsync(ct);
        }
        catch (Exception e) when (e is OperationCanceledException || e is RetrievalException { Code: RetrievalException.EmbeddingUnavailable })
        {
            return Empty(RetrievalModelState.Unreachable, now) with { Artifact = artifact, Sha256Status = sha };
        }
        catch (RetrievalException)
        {
            return Empty(RetrievalModelState.Unverified, now) with { Artifact = artifact, Sha256Status = sha };
        }

        return dimension == model.Dimension
            ? new RetrievalModelSnapshot(RetrievalModelState.Ready, now, model.ModelId, model.Artifact, artifact, dimension, sha)
            : Empty(RetrievalModelState.DimensionMismatch, now) with { Artifact = artifact, Dimension = dimension, Sha256Status = sha };
    }

    private RetrievalModelSnapshot Empty(RetrievalModelState state, DateTimeOffset? checkedAt) =>
        new(state, checkedAt, _options.Embedding.ModelId, _options.Embedding.Artifact);

    private static bool IsUnreachable(Exception e) =>
        e is HttpRequestException { StatusCode: null } or OperationCanceledException or TimeoutException or IOException
        || e is HttpRequestException { StatusCode: { } code } && (int)code >= 500 && code != System.Net.HttpStatusCode.NotImplemented;

    private sealed record ListedModel(string Id, string? Status, string? Artifact, string? Sha256);

    private async Task<ListedModel[]?> ListModelsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(_serverRoot, "v1/models"), ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var models = new List<ListedModel>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? status = null;
            string? artifact = null;
            if (item.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                status = st.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (st.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                {
                    var list = args.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() : null).ToList();
                    var at = list.FindIndex(a => a is "--model" or "-m");
                    artifact = at >= 0 && at + 1 < list.Count ? FileName(list[at + 1]) : null;
                }
            }

            var sha = item.TryGetProperty("sha256", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            models.Add(new ListedModel(id.GetString()!, status, artifact, sha));
        }

        return [.. models];
    }

    /// <summary><c>GET /props?model=</c>: <c>model_path</c> del server (basename). Assente ⇒ null.</summary>
    private async Task<string?> GetPropsArtifactAsync(string modelId, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(_serverRoot, $"props?model={Uri.EscapeDataString(modelId)}"), ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("model_path", out var path) && path.ValueKind == JsonValueKind.String
            ? FileName(path.GetString())
            : null;
    }

    /// <summary>Nome del file di un percorso (separatori Windows o POSIX), vuoto ⇒ null.</summary>
    private static string? FileName(string? path)
    {
        var name = path?.Replace('\\', '/').Split('/').LastOrDefault();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public void Dispose() => _http.Dispose();
}
