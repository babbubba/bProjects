using System.Net;
using System.Text.Json;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>
/// Router llama.cpp FINTO (nessuna rete, nessun modello) con DUE modelli (embedding e riordino) il cui stato è <c>loaded</c>, <c>sleeping</c> o <c>loading</c>: una richiesta ordinaria a un modello
/// <c>sleeping</c> lo carica (se <c>…LoadsOnRequest</c>), come osservato sul DGX dallo smoke test. Registra l'ordine di tutte le chiamate.
/// </summary>
internal sealed class FakeRouter : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string EmbeddingStatus { get; set; } = "sleeping";

    public string RerankerStatus { get; set; } = "loaded";

    public bool EmbeddingListed { get; set; } = true;

    public string EmbeddingArtifact { get; set; } = "bge-m3-Q5_K_M.gguf";

    /// <summary>Artefatto riportato DOPO il caricamento (null = invariato): simula un modello diverso da quello atteso che appare solo all'attivazione.</summary>
    public string? EmbeddingArtifactAfterLoad { get; set; }

    public string RerankerArtifact { get; set; } = "bge-reranker-v2-m3-Q5_K_M.gguf";

    public int Dimension { get; set; } = 1024;

    public bool EmbeddingLoadsOnRequest { get; set; } = true;

    public bool RerankerLoadsOnRequest { get; set; } = true;

    public HttpStatusCode? EmbeddingsFailure { get; set; }

    /// <summary>Se valorizzata, la richiesta di embedding lancia questa eccezione NON tipizzata (guasto imprevisto del trasporto).</summary>
    public Exception? EmbeddingsThrow { get; set; }

    public HttpStatusCode? RerankFailure { get; set; }

    /// <summary>Se valorizzato, ogni richiesta di embedding attende questo task prima di rispondere (simula il caricamento in corso).</summary>
    public Task? EmbeddingHold { get; set; }

    public List<string> Calls { get; } = [];

    public int EmbeddingRequests => Count("POST /v1/embeddings");

    public int RerankRequests => Count("POST /v1/rerank");

    /// <summary>Si completa alla PRIMA richiesta di embedding ricevuta.</summary>
    public Task FirstEmbeddingArrival => _arrived.Task;

    public void Sleep() => EmbeddingStatus = "sleeping";

    private int Count(string call)
    {
        lock (_gate)
        {
            return Calls.Count(c => c == call);
        }
    }

    private static string Entry(string id, string status, string artifact) =>
        "{\"id\":\"" + id + "\",\"object\":\"model\",\"status\":{\"value\":\"" + status + "\",\"args\":[\"llama-server\",\"--model\",\"/models/" + artifact + "\"]}}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        lock (_gate)
        {
            Calls.Add($"{request.Method} {path}");
            if (path == "/v1/embeddings")
            {
                _arrived.TrySetResult();
            }
        }

        switch (path)
        {
            case "/v1/models":
                var entries = new List<string>();
                if (EmbeddingListed)
                {
                    entries.Add(Entry("bge-m3-Q5", EmbeddingStatus, EmbeddingArtifact));
                }

                entries.Add(Entry("bge-reranker-v2-m3", RerankerStatus, RerankerArtifact));
                return FakeEmbeddingServer.Json("{\"object\":\"list\",\"data\":[" + string.Join(',', entries) + "]}");

            case "/v1/embeddings":
                using (var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                {
                    var inputs = json.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
                    if (EmbeddingHold is { } hold)
                    {
                        await hold.WaitAsync(cancellationToken);
                    }

                    if (EmbeddingsThrow is { } boom)
                    {
                        throw boom;
                    }

                    if (EmbeddingsFailure is { } failure)
                    {
                        return new HttpResponseMessage(failure);
                    }

                    if (EmbeddingStatus == "sleeping" && EmbeddingLoadsOnRequest)
                    {
                        EmbeddingStatus = "loaded";
                        EmbeddingArtifact = EmbeddingArtifactAfterLoad ?? EmbeddingArtifact;
                    }

                    return FakeEmbeddingServer.Json(FakeEmbeddingServer.EmbeddingsBody(inputs.Select(t => FakeEmbeddingServer.VectorOf(t, Dimension)).ToArray()));
                }

            case "/v1/rerank":
                using (var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                {
                    var docs = json.RootElement.GetProperty("documents").GetArrayLength();
                    if (RerankFailure is { } failure)
                    {
                        return new HttpResponseMessage(failure);
                    }

                    if (RerankerStatus == "sleeping" && RerankerLoadsOnRequest)
                    {
                        RerankerStatus = "loaded";
                    }

                    return FakeEmbeddingServer.Json(FakeRerankServer.ResultsBody(Enumerable.Range(0, docs).Select(i => (i, (double)i)).ToArray()));
                }

            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}

/// <summary>
/// 003 — readiness con attivazione on-demand (eccezione di D-3 alla regola «nessun caricamento forzato»): SOLO un modello configurato e identificato in stato <c>ModelUnloaded</c> riceve UNA richiesta
/// funzionale minima, nel percorso che ne ha bisogno; poi cache invalidata e riverifica di identità, artefatto e dimensione. Router finto, nessun DGX.
/// </summary>
public sealed class RetrievalModelActivationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(FakeRouter router)
        {
            Router = router;
            var options = new RetrievalOptions
            {
                Enabled = true,
                RuntimeCheckSeconds = 60,
                Embedding = { Endpoint = "http://dgx.internal:8081/v1/", ApiKey = "chiave-di-prova" },
                Reranker = { Endpoint = "http://dgx.internal:8081/v1/", ApiKey = "chiave-di-prova" },
            };
            Embedding = new EmbeddingClient(options, router);
            Reranker = new RerankClient(options, router);
            Readiness = new RetrievalModelReadiness(options, Embedding, router, Time, Reranker);
        }

        public FakeRouter Router { get; }

        public EmbeddingClient Embedding { get; }

        public RerankClient Reranker { get; }

        public ManualTime Time { get; } = new();

        public RetrievalModelReadiness Readiness { get; }

        public void Dispose()
        {
            Readiness.Dispose();
            Reranker.Dispose();
            Embedding.Dispose();
        }
    }

    [Fact]
    public async Task Cold_start_a_sleeping_embedding_gets_one_ordinary_request_then_is_reverified_and_ready()
    {
        var router = new FakeRouter();
        using var rig = new Rig(router);

        var snapshot = await rig.Readiness.EnsureEmbeddingReadyAsync(Ct);

        Assert.Equal(RetrievalModelState.Ready, snapshot.State);
        Assert.Equal("bge-m3-Q5_K_M.gguf", snapshot.Artifact);
        Assert.Equal(1024, snapshot.Dimension);
        // Elenco (scaricato) → UNA richiesta minima di attivazione → elenco → sonda funzionale di verifica. Nessun endpoint di caricamento.
        Assert.Equal(["GET /v1/models", "POST /v1/embeddings", "GET /v1/models", "POST /v1/embeddings"], router.Calls);
        var activation = rig.Readiness.LastEmbeddingActivation;
        Assert.NotNull(activation);
        Assert.Equal(RetrievalModelState.ModelUnloaded, activation.StateBefore);
        Assert.Equal(RetrievalModelState.Ready, activation.StateAfter);
        Assert.Equal("Attivato", activation.Result);
        Assert.Null(activation.ErrorCode);
        Assert.Null(rig.Readiness.LastRerankerActivation);
    }

    [Fact]
    public async Task A_model_that_is_already_loaded_receives_no_activation_request()
    {
        var router = new FakeRouter { EmbeddingStatus = "loaded" };
        using var rig = new Rig(router);

        var snapshot = await rig.Readiness.EnsureEmbeddingReadyAsync(Ct);

        Assert.True(snapshot.IsReady);
        Assert.Equal(1, router.EmbeddingRequests);   // la sola sonda funzionale ordinaria della readiness
        Assert.Null(rig.Readiness.LastEmbeddingActivation);
    }

    [Fact]
    public async Task The_passive_query_never_wakes_a_model_and_reports_it_as_unloaded()
    {
        var router = new FakeRouter();
        using var rig = new Rig(router);

        var snapshot = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);
        var wait = await rig.Readiness.WaitUntilEmbeddingReadyAsync(TimeSpan.Zero, Ct);

        Assert.Equal(RetrievalModelState.ModelUnloaded, snapshot.State);
        Assert.False(wait.Snapshot.IsReady);
        Assert.Equal(0, router.EmbeddingRequests);
        Assert.Null(rig.Readiness.LastEmbeddingActivation);
        Assert.Equal(RetrievalModelState.ModelUnloaded, rig.Readiness.Current.State);
    }

    [Fact]
    public async Task An_activation_request_that_fails_ends_in_a_typed_error_with_the_cause_and_is_not_repeated_within_the_cache_ttl()
    {
        var router = new FakeRouter { EmbeddingsFailure = HttpStatusCode.InternalServerError };
        using var rig = new Rig(router);

        var first = await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureEmbeddingReadyAsync(Ct));
        var second = await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureEmbeddingReadyAsync(Ct));

        Assert.Equal(RetrievalException.EmbeddingUnavailable, first.Code);
        Assert.Equal(RetrievalException.EmbeddingUnavailable, second.Code);
        Assert.Contains("Attivazione on-demand tentata: RichiestaFallita (embedding_unavailable)", first.Message);
        Assert.Equal(1, router.EmbeddingRequests);   // un solo tentativo: niente retry né martellamento
        Assert.Equal("RichiestaFallita", rig.Readiness.LastEmbeddingActivation!.Result);
        Assert.DoesNotContain("chiave-di-prova", first.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dgx.internal", first.Message, StringComparison.Ordinal);

        rig.Time.Advance(TimeSpan.FromSeconds(61));   // scaduta la cache: un NUOVO tentativo è lecito, sempre uno solo
        router.EmbeddingsFailure = null;
        Assert.True((await rig.Readiness.EnsureEmbeddingReadyAsync(Ct)).IsReady);
    }

    [Fact]
    public async Task A_request_that_completes_without_loading_the_model_leaves_it_unready_and_the_error_says_so()
    {
        var router = new FakeRouter { EmbeddingLoadsOnRequest = false };
        using var rig = new Rig(router);

        var error = await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureEmbeddingReadyAsync(Ct));

        Assert.Equal(RetrievalException.EmbeddingUnavailable, error.Code);
        Assert.Contains("RichiestaCompletata", error.Message);
        Assert.Contains("ModelUnloaded", error.Message);
        Assert.Equal(1, router.EmbeddingRequests);
    }

    [Fact]
    public async Task A_wrong_artifact_that_shows_up_after_activation_is_a_model_mismatch_and_no_other_model_is_used()
    {
        var router = new FakeRouter { EmbeddingArtifactAfterLoad = "bge-m3-Q8_0.gguf" };
        using var rig = new Rig(router);

        var error = await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureEmbeddingReadyAsync(Ct));

        Assert.Equal(RetrievalException.EmbeddingModelMismatch, error.Code);
        Assert.Equal(RetrievalModelState.ArtifactMismatch, rig.Readiness.LastEmbeddingActivation!.StateAfter);
        Assert.Equal(1, router.EmbeddingRequests);   // la sola attivazione: nessuna richiesta di contenuto dopo la difformità
    }

    [Theory]
    [InlineData("missing", RetrievalException.EmbeddingModelMismatch)]
    [InlineData("artifact", RetrievalException.EmbeddingModelMismatch)]
    [InlineData("dimension", RetrievalException.EmbeddingDimensionMismatch)]
    [InlineData("loading", RetrievalException.EmbeddingUnavailable)]
    public async Task Missing_wrong_artifact_wrong_dimension_and_loading_states_never_trigger_an_activation(string scenario, string expectedCode)
    {
        var router = new FakeRouter { EmbeddingStatus = "loaded" };
        switch (scenario)
        {
            case "missing":
                router.EmbeddingListed = false;
                break;
            case "artifact":
                router.EmbeddingArtifact = "bge-m3-Q8_0.gguf";
                break;
            case "dimension":
                router.Dimension = 768;
                break;
            case "loading":
                router.EmbeddingStatus = "loading";
                break;
        }

        using var rig = new Rig(router);

        var error = await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureEmbeddingReadyAsync(Ct));

        Assert.Equal(expectedCode, error.Code);
        Assert.Null(rig.Readiness.LastEmbeddingActivation);
        Assert.DoesNotContain("Attivazione on-demand", error.Message);
        Assert.True(router.EmbeddingRequests <= 1);   // al più la sonda funzionale ordinaria (dimensione), mai una richiesta di attivazione
    }

    [Fact]
    public async Task Concurrent_callers_share_one_activation_per_model()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new FakeRouter { EmbeddingHold = hold.Task };
        using var rig = new Rig(router);

        var callers = Enumerable.Range(0, 8).Select(_ => rig.Readiness.EnsureEmbeddingReadyAsync(Ct)).ToArray();
        await router.FirstEmbeddingArrival.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(100, Ct);   // lascia a tutti il tempo di arrivare allo stesso volo
        Assert.Equal(1, router.EmbeddingRequests);

        hold.SetResult();
        var snapshots = await Task.WhenAll(callers);

        Assert.All(snapshots, s => Assert.True(s.IsReady));
        Assert.Equal(2, router.EmbeddingRequests);   // 1 attivazione + 1 sonda di verifica, indipendentemente dal numero di chiamanti
    }

    [Fact]
    public async Task A_cancelled_caller_does_not_abort_the_shared_activation_and_the_next_call_finds_the_model_ready()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new FakeRouter { EmbeddingHold = hold.Task };
        using var rig = new Rig(router);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var caller = rig.Readiness.EnsureEmbeddingReadyAsync(cancel.Token);
        await router.FirstEmbeddingArrival.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller);

        hold.SetResult();
        var next = await rig.Readiness.EnsureEmbeddingReadyAsync(Ct);

        Assert.True(next.IsReady);
        Assert.Equal(2, router.EmbeddingRequests);   // nessuna seconda attivazione: la prima è proseguita in condivisione
    }

    [Fact]
    public async Task A_model_that_went_back_to_sleep_is_activated_again_after_the_cache_expires()
    {
        var router = new FakeRouter();
        using var rig = new Rig(router);
        Assert.True((await rig.Readiness.EnsureEmbeddingReadyAsync(Ct)).IsReady);
        Assert.Equal(2, router.EmbeddingRequests);

        router.Sleep();
        Assert.True((await rig.Readiness.EnsureEmbeddingReadyAsync(Ct)).IsReady);   // cache ancora valida: nessuna nuova richiesta
        Assert.Equal(2, router.EmbeddingRequests);

        rig.Time.Advance(TimeSpan.FromSeconds(61));
        var again = await rig.Readiness.EnsureEmbeddingReadyAsync(Ct);

        Assert.True(again.IsReady);
        Assert.Equal(4, router.EmbeddingRequests);   // di nuovo attivazione + verifica
        Assert.Equal("Attivato", rig.Readiness.LastEmbeddingActivation!.Result);
    }

    [Fact]
    public async Task An_unexpected_untyped_failure_of_the_activation_request_stays_typed_and_never_leaves_the_slot_stuck()
    {
        var router = new FakeRouter { EmbeddingsThrow = new InvalidOperationException("guasto imprevisto") };
        using var rig = new Rig(router);

        var error = await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureEmbeddingReadyAsync(Ct));

        Assert.Equal(RetrievalException.EmbeddingUnavailable, error.Code);
        Assert.Contains("imprevisto", error.Message);
        Assert.DoesNotContain("guasto imprevisto", error.Message, StringComparison.Ordinal);   // solo il codice, non il testo dell'eccezione grezza

        router.EmbeddingsThrow = null;
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.True((await rig.Readiness.EnsureEmbeddingReadyAsync(Ct)).IsReady);   // lo slot si è liberato: nuovo tentativo lecito
    }

    [Fact]
    public async Task An_old_successful_activation_is_not_blamed_when_the_model_later_becomes_missing()
    {
        var router = new FakeRouter();
        using var rig = new Rig(router);
        Assert.True((await rig.Readiness.EnsureEmbeddingReadyAsync(Ct)).IsReady);

        router.EmbeddingListed = false;
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        var error = await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureEmbeddingReadyAsync(Ct));

        Assert.Equal(RetrievalException.EmbeddingModelMismatch, error.Code);
        Assert.DoesNotContain("Attivazione on-demand", error.Message);
    }

    [Fact]
    public async Task The_reranker_follows_the_same_policy_with_an_independent_slot()
    {
        var router = new FakeRouter { EmbeddingStatus = "loaded", RerankerStatus = "sleeping" };
        using var rig = new Rig(router);

        var snapshot = await rig.Readiness.EnsureRerankerReadyAsync(Ct);

        Assert.True(snapshot.IsReady);
        Assert.Equal(2, router.RerankRequests);        // attivazione + verifica
        Assert.Equal(0, router.EmbeddingRequests);     // l'embedding non è toccato
        Assert.Equal("Attivato", rig.Readiness.LastRerankerActivation!.Result);
        Assert.Null(rig.Readiness.LastEmbeddingActivation);
    }

    [Fact]
    public async Task A_reranker_whose_activation_fails_is_a_typed_degraded_error_and_the_embedding_is_unaffected()
    {
        var router = new FakeRouter { EmbeddingStatus = "loaded", RerankerStatus = "sleeping", RerankFailure = HttpStatusCode.ServiceUnavailable };
        using var rig = new Rig(router);

        var error = await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureRerankerReadyAsync(Ct));

        Assert.Equal(RetrievalException.RerankerUnavailable, error.Code);
        Assert.Contains("Attivazione on-demand tentata", error.Message);
        Assert.Equal(1, router.RerankRequests);
        Assert.True((await rig.Readiness.EnsureEmbeddingReadyAsync(Ct)).IsReady);
    }
}
