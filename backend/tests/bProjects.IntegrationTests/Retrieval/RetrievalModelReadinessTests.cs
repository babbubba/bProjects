using System.Net;
using bProjects.App.AI;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003 T008: readiness dell'embedding con server FINTO (tutti gli stati). La prova DGX «scaricato → disponibile» resta legata a 002:T126 e a T019:
/// qui si prova solo lo schema (DET), che non va dichiarato «provato» su infrastruttura reale.
/// </summary>
public sealed class RetrievalModelReadinessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Models(string id = "bge-m3", string status = "loaded", string? artifactArgs = "/models/bge-m3-Q5_K_M.gguf", string? sha = null) =>
        "{\"object\":\"list\",\"data\":[{\"id\":\"" + id + "\",\"object\":\"model\",\"status\":{\"value\":\"" + status + "\",\"args\":[\"llama-server\""
        + (artifactArgs is null ? string.Empty : ",\"--model\",\"" + artifactArgs.Replace("\\", "\\\\") + "\"") + "]}"
        + (sha is null ? string.Empty : ",\"sha256\":\"" + sha + "\"") + "}]}";

    private static RetrievalOptions Options() => new()
    {
        Enabled = true,
        RuntimeCheckSeconds = 60,
        Embedding = { Endpoint = "http://dgx.internal:8081/v1/", ApiKey = "k" },
    };

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(FakeEmbeddingServer? server = null, RetrievalOptions? options = null)
        {
            Server = server ?? new FakeEmbeddingServer();
            Options = options ?? RetrievalModelReadinessTests.Options();
            Embedding = new EmbeddingClient(Options, Server);
            Readiness = new RetrievalModelReadiness(Options, Embedding, Server, Time);
        }

        public FakeEmbeddingServer Server { get; }

        public RetrievalOptions Options { get; }

        public EmbeddingClient Embedding { get; }

        public ManualTime Time { get; } = new();

        public RetrievalModelReadiness Readiness { get; }

        public void Dispose()
        {
            Readiness.Dispose();
            Embedding.Dispose();
        }
    }

    [Fact]
    public async Task A_loaded_model_with_the_expected_artifact_and_dimension_is_ready_and_the_snapshot_carries_the_observed_values()
    {
        using var rig = new Rig();

        var s = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);

        Assert.Equal(RetrievalModelState.Ready, s.State);
        Assert.True(s.IsReady);
        Assert.Equal("bge-m3", s.ModelId);
        Assert.Equal("bge-m3-Q5_K_M.gguf", s.Artifact);
        Assert.Equal(1024, s.Dimension);
        Assert.Equal(Sha256Status.NotVerified, s.Sha256Status);   // nessuno SHA-256 configurato
        Assert.NotNull(s.CheckedAt);
        Assert.Equal(RuntimeReadiness.Ready, s.State.ToRuntimeReadiness());
        Assert.Equal(1, rig.Server.EmbeddingRequests);              // una sola risposta di prova per la dimensione
        Assert.Same(s, (await rig.Readiness.EnsureEmbeddingReadyAsync(Ct)));
    }

    [Fact]
    public async Task The_artifact_is_read_from_props_when_the_models_list_does_not_report_it_and_a_windows_path_is_reduced_to_its_file_name()
    {
        var server = new FakeEmbeddingServer { Models = () => FakeEmbeddingServer.Json(Models(artifactArgs: null)) };
        server.Props = () => FakeEmbeddingServer.Json("""{"model_path":"C:\\models\\bge-m3-Q5_K_M.gguf","total_slots":1}""");
        using var rig = new Rig(server);

        var s = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);

        Assert.Equal(RetrievalModelState.Ready, s.State);
        Assert.Equal("bge-m3-Q5_K_M.gguf", s.Artifact);
        Assert.Contains("/props", server.Paths);
    }

    [Fact]
    public async Task An_artifact_that_cannot_be_demonstrated_is_unverified_and_sends_no_embedding_request()
    {
        var server = new FakeEmbeddingServer { Models = () => FakeEmbeddingServer.Json(Models(artifactArgs: null)) };   // /props risponde 404
        using var rig = new Rig(server);

        var s = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);

        Assert.Equal(RetrievalModelState.Unverified, s.State);
        Assert.Null(s.Artifact);
        Assert.Equal(0, server.EmbeddingRequests);
        Assert.Equal(RuntimeReadiness.Unverified, s.State.ToRuntimeReadiness());
    }

    [Fact]
    public async Task A_different_artifact_is_an_artifact_mismatch_with_zero_embedding_requests_and_a_typed_error()
    {
        var server = new FakeEmbeddingServer { Models = () => FakeEmbeddingServer.Json(Models(artifactArgs: "/models/bge-m3-Q8_0.gguf")) };
        using var rig = new Rig(server);

        var s = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);

        Assert.Equal(RetrievalModelState.ArtifactMismatch, s.State);
        Assert.Equal("bge-m3-Q8_0.gguf", s.Artifact);
        Assert.Equal(0, server.EmbeddingRequests);
        var error = await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureEmbeddingReadyAsync(Ct));
        Assert.Equal(RetrievalException.EmbeddingModelMismatch, error.Code);
        Assert.Equal(0, server.EmbeddingRequests);
    }

    [Fact]
    public async Task A_model_not_listed_by_the_server_is_missing_and_no_other_model_is_used()
    {
        var server = new FakeEmbeddingServer { Models = () => FakeEmbeddingServer.Json(Models(id: "another-embedder")) };
        using var rig = new Rig(server);

        var s = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);

        Assert.Equal(RetrievalModelState.ModelMissing, s.State);
        Assert.Equal(RuntimeReadiness.ModelMissing, s.State.ToRuntimeReadiness());
        Assert.Equal(0, server.EmbeddingRequests);
        Assert.Equal(RetrievalException.EmbeddingModelMismatch, (await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureEmbeddingReadyAsync(Ct))).Code);
    }

    [Theory]
    [InlineData("unloaded", RetrievalModelState.ModelUnloaded, RuntimeReadiness.ModelUnloaded)]
    [InlineData("sleeping", RetrievalModelState.ModelUnloaded, RuntimeReadiness.ModelUnloaded)]
    [InlineData("loading", RetrievalModelState.ModelLoading, RuntimeReadiness.ModelLoading)]
    public async Task A_listed_but_not_loaded_model_is_unloaded_or_loading_and_nothing_is_requested_from_it(string status, RetrievalModelState expected, RuntimeReadiness equivalent)
    {
        var server = new FakeEmbeddingServer { Models = () => FakeEmbeddingServer.Json(Models(status: status)) };
        using var rig = new Rig(server);

        var s = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);

        Assert.Equal(expected, s.State);
        Assert.Equal(equivalent, s.State.ToRuntimeReadiness());
        Assert.True(s.State.IsTransient());
        Assert.Equal(["/v1/models"], server.Paths);   // né /props (potrebbe avviare il caricamento) né embedding
        Assert.Equal(RetrievalException.EmbeddingUnavailable, (await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureEmbeddingReadyAsync(Ct))).Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_server_error_on_the_models_list_is_unreachable(HttpStatusCode status)
    {
        var server = new FakeEmbeddingServer { Models = () => new HttpResponseMessage(status) };
        using var rig = new Rig(server);

        var s = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);

        Assert.Equal(RetrievalModelState.Unreachable, s.State);
        Assert.Equal(RuntimeReadiness.Unreachable, s.State.ToRuntimeReadiness());
        Assert.True(s.State.IsTransient());
        Assert.Equal(0, server.EmbeddingRequests);
    }

    [Fact]
    public async Task A_network_failure_is_unreachable()
    {
        var options = Options();
        using var embedding = new EmbeddingClient(options, new Refusing());
        using var readiness = new RetrievalModelReadiness(options, embedding, new Refusing(), new ManualTime());

        Assert.Equal(RetrievalModelState.Unreachable, (await readiness.GetEmbeddingSnapshotAsync(Ct)).State);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_server_that_answers_but_gives_no_usable_list_is_unverified_never_ready(HttpStatusCode status)
    {
        var server = new FakeEmbeddingServer { Models = () => new HttpResponseMessage(status) };
        using var rig = new Rig(server);

        Assert.Equal(RetrievalModelState.Unverified, (await rig.Readiness.GetEmbeddingSnapshotAsync(Ct)).State);
        Assert.Equal(0, server.EmbeddingRequests);

        var garbled = new FakeEmbeddingServer { Models = () => FakeEmbeddingServer.Json("{\"unexpected\":true}") };
        using var other = new Rig(garbled);
        Assert.Equal(RetrievalModelState.Unverified, (await other.Readiness.GetEmbeddingSnapshotAsync(Ct)).State);
    }

    [Fact]
    public async Task A_trial_vector_of_another_dimension_is_a_dimension_mismatch_with_a_typed_error()
    {
        var server = new FakeEmbeddingServer { Dimension = 768 };
        using var rig = new Rig(server);

        var s = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);

        Assert.Equal(RetrievalModelState.DimensionMismatch, s.State);
        Assert.Equal(768, s.Dimension);
        Assert.False(s.IsReady);
        Assert.Equal(1, server.EmbeddingRequests);   // solo la risposta di prova
        var error = await Assert.ThrowsAsync<RetrievalException>(() => rig.Readiness.EnsureEmbeddingReadyAsync(Ct));
        Assert.Equal(RetrievalException.EmbeddingDimensionMismatch, error.Code);
        Assert.Equal(1, server.EmbeddingRequests);   // nessuna nuova richiesta di embedding dopo la difformità (cache)
    }

    [Fact]
    public async Task The_trial_request_failing_makes_the_model_unreachable_not_ready()
    {
        var server = new FakeEmbeddingServer { EmbeddingsOverride = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };
        using var rig = new Rig(server);

        Assert.Equal(RetrievalModelState.Unreachable, (await rig.Readiness.GetEmbeddingSnapshotAsync(Ct)).State);
    }

    [Fact]
    public async Task The_sha256_is_verified_only_when_configured_and_proved_by_the_runtime()
    {
        var sha = new string('c', 64);

        var proved = new FakeEmbeddingServer { Models = () => FakeEmbeddingServer.Json(Models(sha: sha)) };
        var options = Options();
        options.Embedding.Sha256 = sha;
        using (var rig = new Rig(proved, options))
        {
            Assert.Equal(Sha256Status.Verified, (await rig.Readiness.GetEmbeddingSnapshotAsync(Ct)).Sha256Status);
        }

        // Configurato ma il runtime non lo riporta: nessun falso «Verified» (non è causa di blocco).
        var options2 = Options();
        options2.Embedding.Sha256 = sha;
        using var silent = new Rig(new FakeEmbeddingServer(), options2);
        var s = await silent.Readiness.GetEmbeddingSnapshotAsync(Ct);
        Assert.Equal(RetrievalModelState.Ready, s.State);
        Assert.Equal(Sha256Status.NotVerified, s.Sha256Status);
    }

    [Fact]
    public async Task The_snapshot_is_cached_for_the_ttl_with_a_single_probe_in_flight_and_refreshed_after_it()
    {
        var gate = new TaskCompletionSource();
        var server = new FakeEmbeddingServer { Gate = gate.Task };
        using var rig = new Rig(server);

        var concurrent = Enumerable.Range(0, 5).Select(_ => rig.Readiness.GetEmbeddingSnapshotAsync(Ct)).ToArray();
        gate.SetResult();
        await Task.WhenAll(concurrent);
        var probeRequests = server.Paths.Count;

        Assert.Equal(1, server.Paths.Count(p => p == "/v1/models"));   // una sola sonda condivisa
        Assert.All(concurrent, t => Assert.Same(concurrent[0].Result, t.Result));

        await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);               // dentro il TTL: dalla cache
        Assert.Equal(probeRequests, server.Paths.Count);

        rig.Time.Advance(TimeSpan.FromSeconds(61));                       // scaduto: nuova sonda
        await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);
        Assert.Equal(2, server.Paths.Count(p => p == "/v1/models"));
    }

    [Fact]
    public async Task Waiting_follows_an_unloaded_model_until_it_becomes_ready_without_loading_it()
    {
        var status = "unloaded";
        var server = new FakeEmbeddingServer { Models = () => FakeEmbeddingServer.Json(Models(status: status)) };
        using var rig = new Rig(server);

        var first = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);
        Assert.Equal(RetrievalModelState.ModelUnloaded, first.State);

        status = "loaded";   // il runtime lo rende disponibile da solo
        var waited = await rig.Readiness.WaitUntilEmbeddingReadyAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.False(waited.DeadlineReached);
        Assert.Equal(RetrievalModelState.Ready, waited.Snapshot.State);
        Assert.DoesNotContain(server.Paths, p => p is not ("/v1/models" or "/v1/embeddings"));   // mai richieste di caricamento
    }

    [Fact]
    public async Task Waiting_reports_the_deadline_of_the_caller_with_the_last_known_state()
    {
        var server = new FakeEmbeddingServer { Models = () => FakeEmbeddingServer.Json(Models(status: "loading")) };
        using var rig = new Rig(server);

        var waited = await rig.Readiness.WaitUntilEmbeddingReadyAsync(TimeSpan.Zero, Ct);

        Assert.True(waited.DeadlineReached);
        Assert.NotEqual(RetrievalModelState.Ready, waited.Snapshot.State);
    }

    [Theory]
    [InlineData(RetrievalModelState.ModelMissing, RetrievalException.EmbeddingModelMismatch)]
    [InlineData(RetrievalModelState.ArtifactMismatch, RetrievalException.EmbeddingModelMismatch)]
    [InlineData(RetrievalModelState.DimensionMismatch, RetrievalException.EmbeddingDimensionMismatch)]
    [InlineData(RetrievalModelState.Unreachable, RetrievalException.EmbeddingUnavailable)]
    [InlineData(RetrievalModelState.ModelLoading, RetrievalException.EmbeddingUnavailable)]
    [InlineData(RetrievalModelState.ModelUnloaded, RetrievalException.EmbeddingUnavailable)]
    [InlineData(RetrievalModelState.Unverified, RetrievalException.EmbeddingUnavailable)]
    public void Every_not_ready_state_maps_to_its_typed_error(RetrievalModelState state, string code)
    {
        var snapshot = new RetrievalModelSnapshot(state, null, "bge-m3", "bge-m3-Q5_K_M.gguf");

        Assert.Equal(code, RetrievalModelReadiness.ToException(snapshot).Code);
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connessione rifiutata"));
    }
}
