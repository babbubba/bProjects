using System.Net;
using System.Text;
using System.Text.Json;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>Server HTTP FINTO del servizio di riordino (003 T021): nessuna rete reale, nessun modello. Risposte canned per percorso.</summary>
internal sealed class FakeRerankServer : HttpMessageHandler
{
    public const string ModelsLoaded =
        """{"object":"list","data":[{"id":"bge-reranker-v2-m3","object":"model","status":{"value":"loaded","args":["llama-server","--model","/models/bge-reranker-v2-m3-Q5_K_M.gguf","--reranking"]}}]}""";

    /// <summary>Punteggio per documento (ricevuti query e testi); default: deterministico, con valori negativi.</summary>
    public Func<string, string[], double[]> Scores { get; set; } = (_, docs) => docs.Select((_, i) => -1.5 + (i * 0.5)).ToArray();

    /// <summary>Se valorizzata sostituisce la risposta di <c>/v1/rerank</c>.</summary>
    public Func<string[], HttpResponseMessage>? RerankOverride { get; set; }

    public Func<HttpResponseMessage> Models { get; set; } = () => Json(ModelsLoaded);

    public List<string> Paths { get; } = [];

    public List<string> Bodies { get; } = [];

    public List<string> Authorization { get; } = [];

    public int RerankRequests => Bodies.Count;

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static string ResultsBody(params (int Index, double Score)[] results) =>
        JsonSerializer.Serialize(new { model = "bge-reranker-v2-m3", results = results.Select(r => new { index = r.Index, relevance_score = r.Score }).ToArray() });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Paths)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Authorization.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
        }

        switch (request.RequestUri!.AbsolutePath)
        {
            case "/v1/rerank":
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                lock (Bodies)
                {
                    Bodies.Add(body);
                }

                using (var json = JsonDocument.Parse(body))
                {
                    var query = json.RootElement.GetProperty("query").GetString()!;
                    var docs = json.RootElement.GetProperty("documents").EnumerateArray().Select(e => e.GetString()!).ToArray();
                    if (RerankOverride is { } over)
                    {
                        return over(docs);
                    }

                    var scores = Scores(query, docs);
                    return Json(ResultsBody(scores.Select((s, i) => (i, s)).ToArray()));
                }

            case "/v1/models":
                return Models();
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}

/// <summary>003 T021: client di riordino e readiness del riordino contro un server HTTP FINTO (nessun DGX): validazione della risposta, errori tipizzati, punteggi solo come ordinamento.</summary>
public sealed class RerankClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RetrievalOptions Options(int timeoutSeconds = 30) => new()
    {
        Enabled = true,
        TimeoutSeconds = timeoutSeconds,
        RuntimeCheckSeconds = 60,
        Embedding = { Endpoint = "http://dgx.internal:8081/v1/", ApiKey = "embedding-key" },
        Reranker = { Endpoint = "http://dgx.internal:8082/v1/", ApiKey = "reranker-key" },
    };

    private static async Task<RetrievalException> FailureOf(RerankClient client, params string[] docs) =>
        await Assert.ThrowsAsync<RetrievalException>(() => client.RerankAsync("query", docs, Ct));

    [Fact]
    public async Task Candidates_are_returned_by_descending_score_including_negative_scores_and_the_request_carries_model_query_and_documents()
    {
        var server = new FakeRerankServer { Scores = (_, _) => [-7.25, 3.5, -0.5] };
        using var client = new RerankClient(Options(), server);

        var ordered = await client.RerankAsync("scadenza contratto", ["a", "b", "c"], Ct);

        Assert.Equal([1, 2, 0], ordered.Select(r => r.Index).ToArray());
        Assert.Equal([3.5, -0.5, -7.25], ordered.Select(r => r.Score).ToArray());   // il punteggio negativo è valido: solo ordinamento
        Assert.Equal("/v1/rerank", Assert.Single(server.Paths));
        Assert.Equal("Bearer reranker-key", Assert.Single(server.Authorization));
        using var body = JsonDocument.Parse(Assert.Single(server.Bodies));
        Assert.Equal("bge-reranker-v2-m3", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("scadenza contratto", body.RootElement.GetProperty("query").GetString());
        Assert.Equal(["a", "b", "c"], body.RootElement.GetProperty("documents").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task Equal_scores_are_ordered_by_ascending_index_and_the_response_arrival_order_is_irrelevant()
    {
        var server = new FakeRerankServer { RerankOverride = _ => FakeRerankServer.Json(FakeRerankServer.ResultsBody((2, 1.0), (0, 1.0), (1, 5.0))) };
        using var client = new RerankClient(Options(), server);

        var ordered = await client.RerankAsync("q", ["a", "b", "c"], Ct);

        Assert.Equal([1, 0, 2], ordered.Select(r => r.Index).ToArray());
    }

    [Fact]
    public async Task An_empty_candidate_list_sends_nothing()
    {
        var server = new FakeRerankServer();
        using var client = new RerankClient(Options(), server);

        Assert.Empty(await client.RerankAsync("q", [], Ct));
        Assert.Equal(0, server.RerankRequests);
    }

    [Theory]
    [InlineData("""{"model":"x","results":[{"index":0,"relevance_score":1.0},{"index":0,"relevance_score":2.0}]}""", "indice duplicato")]
    [InlineData("""{"model":"x","results":[{"index":0,"relevance_score":1.0},{"index":2,"relevance_score":2.0}]}""", "indice fuori dal pool")]
    [InlineData("""{"model":"x","results":[{"index":0,"relevance_score":1.0},{"index":-1,"relevance_score":2.0}]}""", "indice negativo")]
    [InlineData("""{"model":"x","results":[{"index":0,"relevance_score":1.0}]}""", "candidato non coperto")]
    [InlineData("""{"model":"x","results":[{"index":0,"relevance_score":1.0},{"index":1,"relevance_score":2.0},{"index":1,"relevance_score":3.0}]}""", "più risultati dei candidati")]
    [InlineData("""{"model":"x","results":[{"index":0,"relevance_score":1.0},{"index":1}]}""", "punteggio assente")]
    [InlineData("""{"model":"x","results":[{"index":0,"relevance_score":1.0},{"index":1,"relevance_score":"alto"}]}""", "punteggio non numerico")]
    [InlineData("""{"model":"x","results":[{"index":0,"relevance_score":1.0},{"index":"1","relevance_score":2.0}]}""", "indice non numerico")]
    [InlineData("""{"model":"x"}""", "campo results assente")]
    [InlineData("""{"results":{"index":0}}""", "results non è un array")]
    [InlineData("[1,2]", "radice non oggetto")]
    [InlineData("non è json", "JSON illeggibile")]
    public async Task A_malformed_or_incomplete_response_is_a_typed_malformed_error_and_never_a_partial_list(string body, string why)
    {
        var server = new FakeRerankServer { RerankOverride = _ => FakeRerankServer.Json(body) };
        using var client = new RerankClient(Options(), server);

        var error = await FailureOf(client, "a", "b");

        Assert.True(error.Code == RetrievalException.RerankerMalformedResponse, why);
    }

    [Fact]
    public async Task A_service_error_is_reranker_unavailable_and_the_message_never_contains_the_api_key_or_the_texts()
    {
        var server = new FakeRerankServer { RerankOverride = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };
        using var client = new RerankClient(Options(), server);

        var error = await FailureOf(client, "testo riservato del cliente");

        Assert.Equal(RetrievalException.RerankerUnavailable, error.Code);
        Assert.DoesNotContain("reranker-key", error.Message);
        Assert.DoesNotContain("testo riservato", error.Message);
        Assert.Equal(1, server.RerankRequests);   // nessun retry silenzioso
    }

    [Fact]
    public async Task A_network_failure_is_reranker_unavailable()
    {
        using var client = new RerankClient(Options(), new Failing(new HttpRequestException("rifiutata")));

        Assert.Equal(RetrievalException.RerankerUnavailable, (await FailureOf(client, "a")).Code);
    }

    [Fact]
    public async Task A_request_that_exceeds_the_timeout_is_reranker_unavailable()
    {
        using var client = new RerankClient(Options(timeoutSeconds: 1), new Hanging());

        Assert.Equal(RetrievalException.RerankerUnavailable, (await FailureOf(client, "a")).Code);
    }

    [Fact]
    public async Task A_caller_cancellation_propagates_as_cancellation_not_as_a_service_failure()
    {
        using var client = new RerankClient(Options(), new Hanging());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var pending = client.RerankAsync("q", ["a"], cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // ---- readiness del riordino ----

    private sealed class Rig : IDisposable
    {
        public Rig(FakeRerankServer? server = null, bool withReranker = true)
        {
            Server = server ?? new FakeRerankServer();
            var options = Options();
            Embedding = new EmbeddingClient(options, new FakeEmbeddingServer());
            Reranker = withReranker ? new RerankClient(options, Server) : null;
            Readiness = new RetrievalModelReadiness(options, Embedding, Server, new FixedTime(), Reranker);
        }

        public FakeRerankServer Server { get; }

        public EmbeddingClient Embedding { get; }

        public RerankClient? Reranker { get; }

        public RetrievalModelReadiness Readiness { get; }

        public void Dispose()
        {
            Readiness.Dispose();
            Embedding.Dispose();
            Reranker?.Dispose();
        }
    }

    [Fact]
    public async Task A_loaded_reranker_with_the_expected_artifact_and_a_valid_probe_is_ready_without_a_dimension()
    {
        using var rig = new Rig();

        var s = await rig.Readiness.GetRerankerSnapshotAsync(Ct);

        Assert.Equal(RetrievalModelState.Ready, s.State);
        Assert.Equal("bge-reranker-v2-m3", s.ModelId);
        Assert.Equal("bge-reranker-v2-m3-Q5_K_M.gguf", s.Artifact);
        Assert.Null(s.Dimension);
        Assert.Equal(Sha256Status.NotVerified, s.Sha256Status);
        Assert.Equal(1, rig.Server.RerankRequests);                 // UNA sola richiesta di prova
        Assert.All(rig.Server.Authorization, a => Assert.Equal("Bearer reranker-key", a));   // la chiave dell'embedding non viaggia verso il riordino
        Assert.Same(s, await rig.Readiness.EnsureRerankerReadyAsync(Ct));
        Assert.Equal(1, rig.Server.RerankRequests);                 // cache a TTL: nessuna seconda sonda
    }

    [Fact]
    public async Task A_different_artifact_is_a_mismatch_with_no_rerank_request_and_maps_to_reranker_unavailable()
    {
        var server = new FakeRerankServer
        {
            Models = () => FakeRerankServer.Json(FakeRerankServer.ModelsLoaded.Replace("bge-reranker-v2-m3-Q5_K_M.gguf", "altro-modello.gguf")),
        };
        using var rig = new Rig(server);

        var s = await rig.Readiness.GetRerankerSnapshotAsync(Ct);

        Assert.Equal(RetrievalModelState.ArtifactMismatch, s.State);
        Assert.Equal("altro-modello.gguf", s.Artifact);
        Assert.Equal(0, server.RerankRequests);
        var error = await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureRerankerReadyAsync(Ct));
        Assert.Equal(RetrievalException.RerankerUnavailable, error.Code);
    }

    [Fact]
    public async Task A_missing_unloaded_or_unreachable_reranker_is_not_ready_with_the_matching_state_and_no_rerank_request()
    {
        var missing = new FakeRerankServer { Models = () => FakeRerankServer.Json(FakeRerankServer.ModelsLoaded.Replace("\"bge-reranker-v2-m3\"", "\"altro\"")) };
        using (var rig = new Rig(missing))
        {
            Assert.Equal(RetrievalModelState.ModelMissing, (await rig.Readiness.GetRerankerSnapshotAsync(Ct)).State);
            Assert.Equal(0, missing.RerankRequests);
        }

        var unloaded = new FakeRerankServer { Models = () => FakeRerankServer.Json(FakeRerankServer.ModelsLoaded.Replace("\"loaded\"", "\"unloaded\"")) };
        using (var rig = new Rig(unloaded))
        {
            var s = await rig.Readiness.GetRerankerSnapshotAsync(Ct);
            Assert.Equal(RetrievalModelState.ModelUnloaded, s.State);
            Assert.True(s.State.IsTransient());
            Assert.Equal(0, unloaded.RerankRequests);
        }

        var down = new FakeRerankServer { Models = () => new HttpResponseMessage(HttpStatusCode.BadGateway) };
        using (var rig = new Rig(down))
        {
            Assert.Equal(RetrievalModelState.Unreachable, (await rig.Readiness.GetRerankerSnapshotAsync(Ct)).State);
        }
    }

    [Fact]
    public async Task A_malformed_probe_response_makes_the_reranker_unverified_and_an_unavailable_probe_makes_it_unreachable()
    {
        var malformed = new FakeRerankServer { RerankOverride = _ => FakeRerankServer.Json("""{"results":[{"index":0,"relevance_score":1.0}]}""") };
        using (var rig = new Rig(malformed))
        {
            Assert.Equal(RetrievalModelState.Unverified, (await rig.Readiness.GetRerankerSnapshotAsync(Ct)).State);
        }

        var failing = new FakeRerankServer { RerankOverride = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        using (var rig = new Rig(failing))
        {
            Assert.Equal(RetrievalModelState.Unreachable, (await rig.Readiness.GetRerankerSnapshotAsync(Ct)).State);
        }
    }

    [Fact]
    public async Task A_reranker_that_is_not_configured_is_unverified_without_any_request_and_does_not_affect_the_embedding_state()
    {
        using var rig = new Rig(withReranker: false);

        var s = await rig.Readiness.GetRerankerSnapshotAsync(Ct);

        Assert.Equal(RetrievalModelState.Unverified, s.State);
        Assert.Empty(rig.Server.Paths);
        Assert.Equal(RetrievalException.RerankerUnavailable, (await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureRerankerReadyAsync(Ct))).Code);
        Assert.Equal(RetrievalModelState.Unverified, rig.Readiness.Current.State);   // l'embedding non è mai stato sondato: stato iniziale, indipendente
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Failing(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw error;
    }

    private sealed class Hanging : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
