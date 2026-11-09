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
/// provata o non applicabile al riordino), <see cref="Sha256Status"/> e istante della sonda. Nessun valore è dedotto dalla configurazione.
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
/// Readiness dei modelli del recupero: embedding (003 T008) e riordino (T021) — FR-034, D-5. Sonda a richiesta (<c>/v1/models</c>, <c>/props</c> e UNA richiesta
/// funzionale di prova: embedding per la dimensione, riordino minimo per il pooling <c>rank</c>) con cache a TTL (<see cref="RetrievalOptions.RuntimeCheckSeconds"/>)
/// e una sola sonda in volo PER MODELLO; stesse attese e tetto di <see cref="ModelRuntimeMonitor.ReadinessBackoff"/>. Non carica né scarica modelli e non sostituisce
/// mai il modello: nome, artefatto e dimensione sono configurazione fissata e una difformità è «non pronto». Difformità ⇒ nessuna richiesta di contenuto
/// (<see cref="EnsureEmbeddingReadyAsync"/>, <see cref="EnsureRerankerReadyAsync"/>). I due modelli sono indipendenti: il riordino non pronto non rende non pronto l'embedding.
/// </summary>
public sealed class RetrievalModelReadiness : IDisposable
{
    private readonly RetrievalOptions _options;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Slot _embeddingSlot;
    private readonly Slot _rerankerSlot;

    public RetrievalModelReadiness(RetrievalOptions options, EmbeddingClient embedding, HttpMessageHandler? handler = null, TimeProvider? time = null, RerankClient? reranker = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;

        // La prova funzionale dell'embedding restituisce la dimensione osservata; quella del riordino non ha dimensione (null).
        _embeddingSlot = new Slot(
            options.Embedding,
            async ct => await embedding.ProbeDimensionAsync(ct),
            expectsDimension: true);
        _rerankerSlot = new Slot(
            options.Reranker,
            async ct =>
            {
                await reranker!.ProbeAsync(ct);
                return null;
            },
            expectsDimension: false,
            configured: reranker is not null && !string.IsNullOrWhiteSpace(options.Reranker.Endpoint));

        _embeddingSlot.Snapshot = Empty(_embeddingSlot, RetrievalModelState.Unverified, null);
        _rerankerSlot.Snapshot = Empty(_rerankerSlot, RetrievalModelState.Unverified, null);
    }

    /// <summary>Ultimo stato noto dell'embedding, senza I/O (Unverified se mai sondato).</summary>
    public RetrievalModelSnapshot Current => Read(_embeddingSlot);

    /// <summary>Ultimo stato noto del riordino, senza I/O (Unverified se mai sondato).</summary>
    public RetrievalModelSnapshot CurrentReranker => Read(_rerankerSlot);

    /// <summary>Stato verificato dell'embedding: dalla cache finché valido, altrimenti con una sonda condivisa fra i chiamanti. Non lancia per errori del server.</summary>
    public Task<RetrievalModelSnapshot> GetEmbeddingSnapshotAsync(CancellationToken cancellationToken = default) => GetSnapshotAsync(_embeddingSlot, cancellationToken);

    /// <summary>Stato verificato del riordino (stesse regole dell'embedding). Riordino non configurato ⇒ <see cref="RetrievalModelState.Unverified"/> senza alcuna richiesta.</summary>
    public Task<RetrievalModelSnapshot> GetRerankerSnapshotAsync(CancellationToken cancellationToken = default) => GetSnapshotAsync(_rerankerSlot, cancellationToken);

    /// <summary>
    /// Attende la readiness di un modello transitoriamente non pronto (scaricato, in caricamento, irraggiungibile) con poche sonde e tetto fisso; un solo giro condiviso
    /// fra i chiamanti. Non invia richieste di caricamento. <paramref name="remaining"/> è il residuo della deadline del chiamante.
    /// </summary>
    public Task<RetrievalReadinessWait> WaitUntilEmbeddingReadyAsync(TimeSpan remaining, CancellationToken cancellationToken = default) =>
        WaitUntilReadyAsync(_embeddingSlot, remaining, cancellationToken);

    /// <summary>Come <see cref="WaitUntilEmbeddingReadyAsync"/>, per il riordino.</summary>
    public Task<RetrievalReadinessWait> WaitUntilRerankerReadyAsync(TimeSpan remaining, CancellationToken cancellationToken = default) =>
        WaitUntilReadyAsync(_rerankerSlot, remaining, cancellationToken);

    /// <summary>Lancia l'errore tipizzato del recupero se l'embedding non è pronto; in caso contrario restituisce lo snapshot (da registrare nel registro e nei rapporti).</summary>
    public async Task<RetrievalModelSnapshot> EnsureEmbeddingReadyAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await GetEmbeddingSnapshotAsync(cancellationToken);
        return snapshot.IsReady ? snapshot : throw ToException(snapshot);
    }

    /// <summary>Come <see cref="EnsureEmbeddingReadyAsync"/>, per il riordino: non pronto ⇒ <c>reranker_unavailable</c> (modalità degradata, mai un cambio di modello).</summary>
    public async Task<RetrievalModelSnapshot> EnsureRerankerReadyAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await GetRerankerSnapshotAsync(cancellationToken);
        return snapshot.IsReady ? snapshot : throw ToRerankerException(snapshot);
    }

    /// <summary>Mappatura verso gli errori tipizzati dell'embedding (contracts/model-readiness.md §2).</summary>
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

    /// <summary>Riordino non pronto, per qualsiasi stato (assente, artefatto diverso, non dimostrabile, irraggiungibile) ⇒ <c>reranker_unavailable</c> con la causa nello stato.</summary>
    public static RetrievalException ToRerankerException(RetrievalModelSnapshot snapshot) => new(
        RetrievalException.RerankerUnavailable,
        $"Il servizio di riordino {snapshot.ModelId} non è pronto ({snapshot.State}; artefatto atteso {snapshot.ExpectedArtifact}): ordine diagnostico, nessun servizio alternativo è usato.");

    /// <summary>Scarta la validità della cache dell'embedding: la lettura successiva sonda di nuovo.</summary>
    public void Invalidate() => Invalidate(_embeddingSlot);

    /// <summary>Scarta la validità della cache del riordino.</summary>
    public void InvalidateReranker() => Invalidate(_rerankerSlot);

    private static RetrievalModelSnapshot Read(Slot slot)
    {
        lock (slot.Gate)
        {
            return slot.Snapshot;
        }
    }

    private static void Invalidate(Slot slot)
    {
        lock (slot.Gate)
        {
            slot.ValidUntil = DateTimeOffset.MinValue;
        }
    }

    private async Task<RetrievalModelSnapshot> GetSnapshotAsync(Slot slot, CancellationToken cancellationToken)
    {
        Task<RetrievalModelSnapshot> probe;
        lock (slot.Gate)
        {
            if (_time.GetUtcNow() < slot.ValidUntil)
            {
                return slot.Snapshot;
            }

            probe = slot.Probe ??= Task.Run(() => ProbeAndStoreAsync(slot), CancellationToken.None);
        }

        return await probe.WaitAsync(cancellationToken);
    }

    private async Task<RetrievalReadinessWait> WaitUntilReadyAsync(Slot slot, TimeSpan remaining, CancellationToken cancellationToken)
    {
        Task<RetrievalModelSnapshot> loop;
        lock (slot.Gate)
        {
            loop = slot.Wait ??= Task.Run(() => WaitLoopAsync(slot), CancellationToken.None);
        }

        try
        {
            return new(await loop.WaitAsync(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, _time, cancellationToken), false);
        }
        catch (TimeoutException)
        {
            return new(Read(slot), true);
        }
    }

    private async Task<RetrievalModelSnapshot> WaitLoopAsync(Slot slot)
    {
        try
        {
            var snapshot = await GetSnapshotAsync(slot, CancellationToken.None);
            foreach (var pause in ModelRuntimeMonitor.ReadinessBackoff)
            {
                if (!snapshot.State.IsTransient())
                {
                    break;
                }

                await Task.Delay(pause, _time);
                Invalidate(slot);
                snapshot = await GetSnapshotAsync(slot, CancellationToken.None);
            }

            return snapshot;
        }
        finally
        {
            lock (slot.Gate)
            {
                slot.Wait = null;
            }
        }
    }

    private async Task<RetrievalModelSnapshot> ProbeAndStoreAsync(Slot slot)
    {
        RetrievalModelSnapshot fresh;
        try
        {
            // Servizio non configurato: nessuna richiesta, mai «pronto».
            fresh = slot.Configured ? await ProbeAsync(slot) : Empty(slot, RetrievalModelState.Unverified, _time.GetUtcNow());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            fresh = Empty(slot, RetrievalModelState.Unverified, _time.GetUtcNow());   // risposta incomprensibile: non dimostrabile, mai «pronto»
        }

        lock (slot.Gate)
        {
            slot.Snapshot = fresh;
            slot.ValidUntil = _time.GetUtcNow().AddSeconds(Math.Max(1, _options.RuntimeCheckSeconds));
            slot.Probe = null;
        }

        return fresh;
    }

    private async Task<RetrievalModelSnapshot> ProbeAsync(Slot slot)
    {
        var model = slot.Model;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(ModelRuntimeMonitor.MaxProbeSeconds, Math.Max(1, _options.TimeoutSeconds))));
        var ct = timeout.Token;
        var now = _time.GetUtcNow();

        // 1. Elenco dei modelli: prima di /props, che in router mode potrebbe avviare il caricamento di un modello non caricato.
        ListedModel[]? listed;
        try
        {
            listed = await ListModelsAsync(model, ct);
        }
        catch (Exception e) when (IsUnreachable(e))
        {
            return Empty(slot, RetrievalModelState.Unreachable, now);
        }

        if (listed is null)
        {
            return Empty(slot, RetrievalModelState.Unverified, now);   // il server risponde ma l'elenco non è utilizzabile
        }

        var entry = listed.FirstOrDefault(m => string.Equals(m.Id, model.ModelId, StringComparison.Ordinal));
        if (entry is null)
        {
            return Empty(slot, RetrievalModelState.ModelMissing, now);
        }

        if (entry.Status is { } status && !string.Equals(status, "loaded", StringComparison.OrdinalIgnoreCase))
        {
            return Empty(slot, string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase) ? RetrievalModelState.ModelLoading : RetrievalModelState.ModelUnloaded, now);
        }

        // 2. Artefatto: dai parametri del modello elencato (router mode) o, in alternativa, da /props. Non riportato ⇒ non dimostrabile.
        var artifact = entry.Artifact;
        if (artifact is null)
        {
            try
            {
                artifact = await GetPropsArtifactAsync(model, ct);
            }
            catch (Exception e) when (IsUnreachable(e))
            {
                return Empty(slot, RetrievalModelState.Unreachable, now);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException)
            {
                artifact = null;
            }
        }

        var sha = model.ResolveSha256Status(entry.Sha256);
        if (artifact is null)
        {
            return Empty(slot, RetrievalModelState.Unverified, now) with { Sha256Status = sha };
        }

        if (!string.Equals(artifact, model.Artifact, StringComparison.Ordinal))
        {
            return Empty(slot, RetrievalModelState.ArtifactMismatch, now) with { Artifact = artifact, Sha256Status = sha };
        }

        // 3. Prova funzionale: UNA richiesta minima (embedding: dimensione; riordino: due candidati con risposta validata); nessuna richiesta di contenuto prima di questo punto.
        int? dimension;
        try
        {
            dimension = await slot.FunctionalProbe(ct);
        }
        catch (Exception e) when (e is OperationCanceledException || e is RetrievalException { Code: RetrievalException.EmbeddingUnavailable or RetrievalException.RerankerUnavailable })
        {
            return Empty(slot, RetrievalModelState.Unreachable, now) with { Artifact = artifact, Sha256Status = sha };
        }
        catch (RetrievalException)
        {
            return Empty(slot, RetrievalModelState.Unverified, now) with { Artifact = artifact, Sha256Status = sha };
        }

        return !slot.ExpectsDimension || dimension == model.Dimension
            ? new RetrievalModelSnapshot(RetrievalModelState.Ready, now, model.ModelId, model.Artifact, artifact, dimension, sha)
            : Empty(slot, RetrievalModelState.DimensionMismatch, now) with { Artifact = artifact, Dimension = dimension, Sha256Status = sha };
    }

    private static RetrievalModelSnapshot Empty(Slot slot, RetrievalModelState state, DateTimeOffset? checkedAt) =>
        new(state, checkedAt, slot.Model.ModelId, slot.Model.Artifact);

    private static bool IsUnreachable(Exception e) =>
        e is HttpRequestException { StatusCode: null } or OperationCanceledException or TimeoutException or IOException
        || e is HttpRequestException { StatusCode: { } code } && (int)code >= 500 && code != System.Net.HttpStatusCode.NotImplemented;

    private sealed record ListedModel(string Id, string? Status, string? Artifact, string? Sha256);

    /// <summary>Radice del server del modello (host:porta): l'embedding e il riordino possono stare su server diversi.</summary>
    private static Uri ServerRoot(RetrievalModelOptions model) =>
        new(new Uri(model.Endpoint.TrimEnd('/') + "/", UriKind.Absolute), "/");

    /// <summary>GET autenticato con la chiave del PROPRIO modello: la chiave dell'embedding non viaggia verso il riordino e viceversa.</summary>
    private async Task<HttpResponseMessage> GetAsync(RetrievalModelOptions model, string relative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ServerRoot(model), relative));
        if (!string.IsNullOrEmpty(model.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", model.ApiKey);
        }

        return await _http.SendAsync(request, ct);
    }

    private async Task<ListedModel[]?> ListModelsAsync(RetrievalModelOptions model, CancellationToken ct)
    {
        using var response = await GetAsync(model, "v1/models", ct);
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
    private async Task<string?> GetPropsArtifactAsync(RetrievalModelOptions model, CancellationToken ct)
    {
        using var response = await GetAsync(model, $"props?model={Uri.EscapeDataString(model.ModelId)}", ct);
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

    /// <summary>Stato condiviso di un modello: cache, sonda in volo e attesa in corso (una sola per modello).</summary>
    private sealed class Slot(RetrievalModelOptions model, Func<CancellationToken, Task<int?>> functionalProbe, bool expectsDimension, bool configured = true)
    {
        public RetrievalModelOptions Model { get; } = model;

        public Func<CancellationToken, Task<int?>> FunctionalProbe { get; } = functionalProbe;

        public bool ExpectsDimension { get; } = expectsDimension;

        public bool Configured { get; } = configured;

        public object Gate { get; } = new();

        public RetrievalModelSnapshot Snapshot { get; set; } = null!;

        public DateTimeOffset ValidUntil { get; set; } = DateTimeOffset.MinValue;

        public Task<RetrievalModelSnapshot>? Probe { get; set; }

        public Task<RetrievalModelSnapshot>? Wait { get; set; }
    }
}
