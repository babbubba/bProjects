using System.Net;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>003 T007: client di embedding contro un server HTTP FINTO (nessun DGX): batch fisso, dimensione, normalizzazione, errori tipizzati, timeout e cancellazione.</summary>
public sealed class EmbeddingClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RetrievalOptions Options(int timeoutSeconds = 30) => new()
    {
        Enabled = true,
        TimeoutSeconds = timeoutSeconds,
        Embedding = { Endpoint = "http://dgx.internal:8081/v1/", ApiKey = "secret-from-env" },
    };

    private static async Task<RetrievalException> FailureOf(EmbeddingClient client, params string[] inputs) =>
        await Assert.ThrowsAsync<RetrievalException>(() => client.EmbedAsync(inputs, Ct));

    [Fact]
    public async Task Ten_inputs_are_sent_in_fixed_batches_of_four_in_order_with_verified_vectors()
    {
        var server = new FakeEmbeddingServer();
        using var client = new EmbeddingClient(Options(), server);
        var inputs = Enumerable.Range(0, 10).Select(i => $"passaggio {i}").ToArray();

        var vectors = await client.EmbedAsync(inputs, Ct);

        Assert.Equal([4, 4, 2], server.EmbeddingInputs.Select(b => b.Length).ToArray());
        Assert.Equal(inputs, server.EmbeddingInputs.SelectMany(b => b).ToArray());
        Assert.Equal(10, vectors.Count);
        Assert.All(vectors, v => Assert.Equal(1024, v.Length));
        Assert.Equal(FakeEmbeddingServer.VectorOf("passaggio 7", 1024), vectors[7]);   // l'ordine di ritorno segue quello degli input
        Assert.All(server.Paths, p => Assert.Equal("/v1/embeddings", p));
        Assert.All(server.Authorization, a => Assert.Equal("Bearer secret-from-env", a));
    }

    [Fact]
    public async Task Results_are_reordered_by_the_response_index_not_by_arrival_order()
    {
        var server = new FakeEmbeddingServer
        {
            EmbeddingsOverride = inputs => FakeEmbeddingServer.Json(FakeEmbeddingServer.EmbeddingsBody(
                [FakeEmbeddingServer.VectorOf(inputs[1], 1024), FakeEmbeddingServer.VectorOf(inputs[0], 1024)], [1, 0])),
        };
        using var client = new EmbeddingClient(Options(), server);

        var vectors = await client.EmbedAsync(["uno", "due"], Ct);

        Assert.Equal(FakeEmbeddingServer.VectorOf("uno", 1024), vectors[0]);
        Assert.Equal(FakeEmbeddingServer.VectorOf("due", 1024), vectors[1]);
    }

    [Fact]
    public async Task An_empty_input_list_sends_nothing()
    {
        var server = new FakeEmbeddingServer();
        using var client = new EmbeddingClient(Options(), server);

        Assert.Empty(await client.EmbedAsync([], Ct));
        Assert.Equal(0, server.EmbeddingRequests);
    }

    [Fact]
    public async Task A_vector_of_the_wrong_dimension_is_a_typed_dimension_mismatch_and_no_partial_result()
    {
        var server = new FakeEmbeddingServer { Dimension = 768 };
        using var client = new EmbeddingClient(Options(), server);

        var error = await FailureOf(client, "a");

        Assert.Equal(RetrievalException.EmbeddingDimensionMismatch, error.Code);
        Assert.Contains("768", error.Message);
        Assert.Contains("1024", error.Message);
    }

    [Fact]
    public async Task A_not_normalized_vector_is_malformed_within_the_declared_tolerance()
    {
        var unit = FakeEmbeddingServer.VectorOf("x", 1024);
        var within = unit.Select(x => x * (1 + (float)EmbeddingClient.NormTolerance / 2)).ToArray();
        var outside = unit.Select(x => x * 1.5f).ToArray();

        using var ok = new EmbeddingClient(Options(), new FakeEmbeddingServer { EmbeddingsOverride = _ => FakeEmbeddingServer.Json(FakeEmbeddingServer.EmbeddingsBody([within])) });
        Assert.Single(await ok.EmbedAsync(["x"], Ct));

        using var bad = new EmbeddingClient(Options(), new FakeEmbeddingServer { EmbeddingsOverride = _ => FakeEmbeddingServer.Json(FakeEmbeddingServer.EmbeddingsBody([outside])) });
        Assert.Equal(RetrievalException.EmbeddingMalformedResponse, (await FailureOf(bad, "x")).Code);
    }

    [Theory]
    [InlineData("""{"data":[]}""")]                                                             // meno vettori degli input
    [InlineData("""{"nodata":true}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"data":[{"index":0}]}""")]                                                   // senza embedding
    [InlineData("""{"data":[{"index":3,"embedding":[0.1]}]}""")]                                 // indice fuori range
    [InlineData("""{"data":[{"index":0,"embedding":["a"]}]}""")]                                 // componente non numerica
    [InlineData("not json at all")]
    public async Task A_malformed_response_is_a_typed_error(string body)
    {
        var server = new FakeEmbeddingServer { EmbeddingsOverride = _ => FakeEmbeddingServer.Json(body) };
        using var client = new EmbeddingClient(Options(), server);

        Assert.Equal(RetrievalException.EmbeddingMalformedResponse, (await FailureOf(client, "a")).Code);
    }

    [Fact]
    public async Task Duplicate_response_indexes_are_malformed()
    {
        var v = FakeEmbeddingServer.VectorOf("a", 1024);
        var server = new FakeEmbeddingServer { EmbeddingsOverride = _ => FakeEmbeddingServer.Json(FakeEmbeddingServer.EmbeddingsBody([v, v], [0, 0])) };
        using var client = new EmbeddingClient(Options(), server);

        Assert.Equal(RetrievalException.EmbeddingMalformedResponse, (await FailureOf(client, "a", "b")).Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task An_input_over_the_model_limit_is_a_typed_error_and_is_never_truncated_or_retried(HttpStatusCode status)
    {
        var server = new FakeEmbeddingServer { EmbeddingsOverride = _ => FakeEmbeddingServer.Json("""{"error":{"message":"input is too large to process"}}""", status) };
        using var client = new EmbeddingClient(Options(), server);
        var huge = new string('x', 100_000);

        var error = await FailureOf(client, huge);

        Assert.Equal(RetrievalException.EmbeddingInputTooLong, error.Code);
        Assert.DoesNotContain(huge, error.Message);
        Assert.Equal(1, server.EmbeddingRequests);                        // nessun retry silenzioso
        Assert.Equal(huge, server.EmbeddingInputs[0][0]);                 // l'input è stato inviato integro (nessun troncamento lato client)
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_service_failure_is_embedding_unavailable_without_retry(HttpStatusCode status)
    {
        var server = new FakeEmbeddingServer { EmbeddingsOverride = _ => new HttpResponseMessage(status) };
        using var client = new EmbeddingClient(Options(), server);

        Assert.Equal(RetrievalException.EmbeddingUnavailable, (await FailureOf(client, "a")).Code);
        Assert.Equal(1, server.EmbeddingRequests);
    }

    [Fact]
    public async Task A_network_error_is_embedding_unavailable()
    {
        using var client = new EmbeddingClient(Options(), new ThrowingHandler(new HttpRequestException("connessione rifiutata")));

        var error = await FailureOf(client, "a");

        Assert.Equal(RetrievalException.EmbeddingUnavailable, error.Code);
        Assert.DoesNotContain("rifiutata", error.Message);
    }

    [Fact]
    public async Task A_hung_server_times_out_as_embedding_unavailable()
    {
        var server = new FakeEmbeddingServer { Gate = new TaskCompletionSource().Task };
        using var client = new EmbeddingClient(Options(timeoutSeconds: 1), server);

        var error = await FailureOf(client, "a");

        Assert.Equal(RetrievalException.EmbeddingUnavailable, error.Code);
        Assert.Contains("timeout", error.Message);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_as_cancellation_not_as_a_service_error()
    {
        var server = new FakeEmbeddingServer { Gate = new TaskCompletionSource().Task };
        using var client = new EmbeddingClient(Options(), server);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var pending = client.EmbedAsync(["a"], cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task A_failure_in_a_later_batch_returns_no_partial_vectors()
    {
        var calls = 0;
        var server = new FakeEmbeddingServer
        {
            EmbeddingsOverride = inputs => ++calls == 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : FakeEmbeddingServer.Json(FakeEmbeddingServer.EmbeddingsBody(inputs.Select(t => FakeEmbeddingServer.VectorOf(t, 1024)).ToArray())),
        };
        using var client = new EmbeddingClient(Options(), server);

        var error = await FailureOf(client, Enumerable.Range(0, 6).Select(i => $"t{i}").ToArray());

        Assert.Equal(RetrievalException.EmbeddingUnavailable, error.Code);
        Assert.Equal(2, server.EmbeddingRequests);   // si ferma al primo batch fallito
    }

    [Fact]
    public async Task The_query_overload_returns_a_single_vector()
    {
        using var client = new EmbeddingClient(Options(), new FakeEmbeddingServer());

        Assert.Equal(FakeEmbeddingServer.VectorOf("domanda", 1024), await client.EmbedAsync("domanda", Ct));
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(error);
    }
}
