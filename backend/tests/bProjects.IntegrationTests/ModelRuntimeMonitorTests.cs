using System.Net;
using System.Text;
using bProjects.App.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace bProjects.IntegrationTests;

/// <summary>
/// Monitor del runtime del modello e protezione dal mismatch di finestra. Le risposte HTTP sono CANNED: riproducono la forma
/// osservata su llama-server in router mode (sola lettura) e varianti con campi mancanti; nessun valore è dedotto dal codice.
/// </summary>
public sealed class ModelRuntimeMonitorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Forma osservata su llama.cpp (router mode): modello caricato, --ctx-size e --parallel negli argomenti, nessun --n-predict.
    private const string ModelsLoaded = """{"object":"list","data":[{"id":"Qwen3.6-35B","object":"model","owned_by":"llamacpp","status":{"value":"loaded","args":["llama-server","--ctx-size","32768","--parallel","1"]}}]}""";

    private const string ModelsUnloaded = """{"object":"list","data":[{"id":"Qwen3.6-35B","object":"model","owned_by":"llamacpp","status":{"value":"unloaded","args":["llama-server","--ctx-size","32768"]}}]}""";

    // Forma osservata: n_ctx per slot, n_predict = -1 (illimitato), un solo slot.
    private static string Props(int nCtx, int nPredict = -1) =>
        $$$"""{"default_generation_settings":{"id":0,"n_ctx":{{{nCtx}}},"params":{"n_predict":{{{nPredict}}},"max_tokens":-1,"temperature":0.8}},"total_slots":1,"model_alias":"Qwen3.6-35B","endpoint_slots":true,"endpoint_props":false}""";

    private static string Slots(int nCtx) => $$$"""[{"id":0,"n_ctx":{{{nCtx}}},"is_processing":false}]""";

    private static AiOptions Options(int window = 32768, int checkSeconds = 60) => new()
    {
        Endpoint = "http://dgx.internal:8080/v1/",
        ModelId = "Qwen3.6-35B",
        ApiKey = "test-key",
        ContextWindowTokens = window,
        RuntimeCheckSeconds = checkSeconds,
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Server llama.cpp simulato: una risposta canned per percorso, modificabile fra una sonda e l'altra.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Models { get; set; } = () => Json(ModelsLoaded);

        public Func<HttpResponseMessage> PropsResponse { get; set; } = () => Json(Props(32768));

        public Func<HttpResponseMessage> SlotsResponse { get; set; } = () => Json(Slots(32768));

        public Task? Gate { get; set; }

        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Paths)
            {
                Paths.Add(request.RequestUri!.AbsolutePath);
            }

            if (Gate is not null)
            {
                await Gate;
            }

            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/models" => Models(),
                "/props" => PropsResponse(),
                "/slots" => SlotsResponse(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CapturingLogger : ILogger<ModelRuntimeMonitor>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class Rig : IDisposable
    {
        public Rig(AiOptions? options = null, FakeServer? server = null)
        {
            Options = options ?? ModelRuntimeMonitorTests.Options();
            Server = server ?? new FakeServer();
            Client = new InternalModelClient(Options, Server);
            Monitor = new ModelRuntimeMonitor(Client, Options, Log, Time);
        }

        public AiOptions Options { get; }

        public FakeServer Server { get; }

        public InternalModelClient Client { get; }

        public ManualTime Time { get; } = new();

        public CapturingLogger Log { get; } = new();

        public ModelRuntimeMonitor Monitor { get; }

        public void Dispose() => Client.Dispose();
    }

    [Fact]
    public async Task Observed_llama_cpp_router_shape_gives_a_verified_snapshot_with_unlimited_n_predict_as_null()
    {
        using var rig = new Rig();

        var s = await rig.Monitor.GetSnapshotAsync(Ct);

        Assert.Equal(ModelRuntimeStatus.Online, s.Status);
        Assert.Equal(32768, s.ContextWindowTokens);
        Assert.Null(s.ServerDefaultNPredict);   // n_predict = -1 vuol dire nessun limite: non è un numero da mostrare
        Assert.Equal(1, s.SlotCount);
        Assert.Equal("loaded", s.ModelStatus);
        Assert.Equal("Qwen3.6-35B", s.ModelId);
        Assert.Equal(32768, s.ConfiguredContextWindowTokens);
        Assert.NotNull(s.CheckedAt);
        Assert.False(s.WindowMismatch);
        Assert.Equal(["/props", "/slots", "/v1/models"], rig.Server.Paths.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_positive_server_default_n_predict_is_reported_and_kept_distinct_from_the_window()
    {
        using var rig = new Rig();
        rig.Server.PropsResponse = () => Json(Props(32768, nPredict: 2048));

        var s = await rig.Monitor.GetSnapshotAsync(Ct);

        Assert.Equal(2048, s.ServerDefaultNPredict);
        Assert.Equal(32768, s.ContextWindowTokens);
    }

    [Fact]
    public async Task The_n_predict_argument_of_the_model_is_used_only_when_props_does_not_report_one()
    {
        using var rig = new Rig();
        rig.Server.Models = () => Json("""{"data":[{"id":"Qwen3.6-35B","status":{"value":"loaded","args":["--ctx-size","32768","--n-predict","1500"]}}]}""");
        rig.Server.PropsResponse = () => Json("""{"default_generation_settings":{"n_ctx":32768},"total_slots":1}""");

        var s = await rig.Monitor.GetSnapshotAsync(Ct);

        Assert.Equal(1500, s.ServerDefaultNPredict);
    }

    [Fact]
    public async Task Missing_fields_are_unavailable_never_invented_and_a_disabled_slots_endpoint_is_tolerated()
    {
        using var rig = new Rig();
        rig.Server.Models = () => Json("""{"object":"list"}""");                    // nessun elenco
        rig.Server.PropsResponse = () => Json("""{"total_slots":2}""");             // nessun default_generation_settings
        rig.Server.SlotsResponse = () => Json("""{"error":{"message":"not supported"}}""", HttpStatusCode.NotImplemented);

        var s = await rig.Monitor.GetSnapshotAsync(Ct);

        Assert.Equal(ModelRuntimeStatus.Online, s.Status);
        Assert.Null(s.ContextWindowTokens);       // né la configurazione né --ctx-size sostituiscono una misura assente
        Assert.Null(s.ServerDefaultNPredict);
        Assert.Equal(2, s.SlotCount);
        Assert.Null(s.ModelStatus);
        Assert.False(s.WindowMismatch);
    }

    [Fact]
    public async Task The_window_falls_back_to_the_smallest_slot_when_props_has_no_n_ctx()
    {
        using var rig = new Rig();
        rig.Server.PropsResponse = () => Json("""{"default_generation_settings":{"params":{}},"total_slots":2}""");
        rig.Server.SlotsResponse = () => Json("""[{"id":0,"n_ctx":32768},{"id":1,"n_ctx":16384},{"id":2}]""");

        var s = await rig.Monitor.GetSnapshotAsync(Ct);

        Assert.Equal(16384, s.ContextWindowTokens);
        Assert.True(s.WindowMismatch);
    }

    [Fact]
    public async Task Props_with_non_numeric_or_null_fields_do_not_throw()
    {
        using var rig = new Rig();
        rig.Server.PropsResponse = () => Json("""{"default_generation_settings":{"n_ctx":"grande","params":{"n_predict":null}},"total_slots":null}""");
        rig.Server.SlotsResponse = () => Json("""{"not":"an array"}""");

        var s = await rig.Monitor.GetSnapshotAsync(Ct);

        Assert.Equal(ModelRuntimeStatus.Online, s.Status);
        Assert.Null(s.ContextWindowTokens);
        Assert.Null(s.ServerDefaultNPredict);
        Assert.Null(s.SlotCount);   // /slots risponde con una lista non valida: nessuno slot noto, non un conteggio inventato
    }

    [Fact]
    public async Task A_model_that_is_not_loaded_is_unverified_without_probing_props_and_never_a_mismatch()
    {
        using var rig = new Rig();
        rig.Server.Models = () => Json(ModelsUnloaded);

        var s = await rig.Monitor.GetSnapshotAsync(Ct);

        Assert.Equal(ModelRuntimeStatus.Unverified, s.Status);
        Assert.Equal("unloaded", s.ModelStatus);
        Assert.Null(s.ContextWindowTokens);
        Assert.False(s.WindowMismatch);
        Assert.Equal(["/v1/models"], rig.Server.Paths.ToArray());   // /props non viene interrogato: in router mode potrebbe caricare il modello
    }

    [Fact]
    public async Task An_unreachable_server_is_offline_with_no_capacity_and_the_configured_window_is_not_presented_as_measured()
    {
        using var rig = new Rig();
        rig.Server.Models = () => throw new HttpRequestException("connessione rifiutata");
        rig.Server.PropsResponse = () => throw new HttpRequestException("connessione rifiutata");

        var s = await rig.Monitor.GetSnapshotAsync(Ct);

        Assert.Equal(ModelRuntimeStatus.Offline, s.Status);
        Assert.Null(s.ContextWindowTokens);
        Assert.Null(s.ServerDefaultNPredict);
        Assert.Null(s.SlotCount);
        Assert.Equal(32768, s.ConfiguredContextWindowTokens);   // il valore configurato resta etichettato come tale
        Assert.False(s.WindowMismatch);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, ModelRuntimeStatus.Offline)]
    [InlineData(HttpStatusCode.BadGateway, ModelRuntimeStatus.Offline)]
    [InlineData(HttpStatusCode.Unauthorized, ModelRuntimeStatus.Unverified)]
    [InlineData(HttpStatusCode.NotFound, ModelRuntimeStatus.Unverified)]
    public async Task A_server_error_is_offline_while_an_answer_without_usable_data_is_unverified(HttpStatusCode status, ModelRuntimeStatus expected)
    {
        using var rig = new Rig();
        rig.Server.Models = () => new HttpResponseMessage(status);
        rig.Server.PropsResponse = () => new HttpResponseMessage(status);
        rig.Server.SlotsResponse = () => new HttpResponseMessage(status);

        var s = await rig.Monitor.GetSnapshotAsync(Ct);

        Assert.Equal(expected, s.Status);
        Assert.Null(s.ContextWindowTokens);
        Assert.False(s.WindowMismatch);
    }

    [Fact]
    public void Before_the_first_probe_the_state_is_unverified_without_any_request()
    {
        using var rig = new Rig();

        Assert.Equal(ModelRuntimeStatus.Unverified, rig.Monitor.Current.Status);
        Assert.Null(rig.Monitor.Current.CheckedAt);
        Assert.Empty(rig.Server.Paths);
    }

    [Fact]
    public async Task The_snapshot_is_cached_until_the_ttl_and_a_changed_n_ctx_discards_the_previous_state_with_a_content_free_log()
    {
        using var rig = new Rig(Options(checkSeconds: 60));

        var first = await rig.Monitor.GetSnapshotAsync(Ct);
        rig.Time.Advance(TimeSpan.FromSeconds(59));
        var cached = await rig.Monitor.GetSnapshotAsync(Ct);
        Assert.Same(first, cached);
        Assert.Equal(3, rig.Server.Paths.Count);                  // una sola sonda (models, props, slots)

        rig.Server.PropsResponse = () => Json(Props(16384, nPredict: 512));
        rig.Server.SlotsResponse = () => Json(Slots(16384));
        rig.Time.Advance(TimeSpan.FromSeconds(2));                // 61 s: scaduto
        var fresh = await rig.Monitor.GetSnapshotAsync(Ct);

        Assert.Equal(6, rig.Server.Paths.Count);
        Assert.Equal(16384, fresh.ContextWindowTokens);
        Assert.Equal(512, fresh.ServerDefaultNPredict);
        Assert.True(fresh.WindowMismatch);
        Assert.Equal(fresh, rig.Monitor.Current);
        Assert.Contains(rig.Log.Messages, m => m.Contains("32768") && m.Contains("16384") && m.Contains("scartato"));
        Assert.DoesNotContain(rig.Log.Messages, m => m.Contains("test-key") || m.Contains("dgx.internal"));
    }

    [Fact]
    public async Task A_server_that_goes_offline_replaces_the_previous_capacities_instead_of_keeping_them()
    {
        using var rig = new Rig(Options(checkSeconds: 5));
        Assert.Equal(32768, (await rig.Monitor.GetSnapshotAsync(Ct)).ContextWindowTokens);

        rig.Server.Models = () => throw new HttpRequestException("giù");
        rig.Server.PropsResponse = () => throw new HttpRequestException("giù");
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        var s = await rig.Monitor.GetSnapshotAsync(Ct);

        Assert.Equal(ModelRuntimeStatus.Offline, s.Status);
        Assert.Null(s.ContextWindowTokens);
        Assert.Contains(rig.Log.Messages, m => m.Contains("Online") && m.Contains("Offline"));
    }

    [Fact]
    public async Task Concurrent_readers_share_a_single_probe()
    {
        var gate = new TaskCompletionSource();
        using var rig = new Rig(server: new FakeServer { Gate = gate.Task });

        var readers = Enumerable.Range(0, 8).Select(_ => rig.Monitor.GetSnapshotAsync(Ct)).ToArray();
        await Task.Delay(100, Ct);
        gate.SetResult();
        var snapshots = await Task.WhenAll(readers);

        Assert.All(snapshots, s => Assert.Same(snapshots[0], s));
        Assert.Equal(1, rig.Server.Paths.Count(p => p == "/v1/models"));   // models, props e slots una volta sola
    }

    [Fact]
    public async Task A_caller_cancellation_does_not_cancel_the_shared_probe()
    {
        var gate = new TaskCompletionSource();
        using var rig = new Rig(server: new FakeServer { Gate = gate.Task });
        using var cancel = new CancellationTokenSource();

        var impatient = rig.Monitor.GetSnapshotAsync(cancel.Token);
        var patient = rig.Monitor.GetSnapshotAsync(Ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        gate.SetResult();

        Assert.Equal(ModelRuntimeStatus.Online, (await patient).Status);
    }

    // --- Protezione dal mismatch ---

    private sealed class CountingModel : IChatClient
    {
        public int Calls { get; private set; }

        public Exception? Failure { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Failure is null
                ? Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")))
                : Task.FromException<ChatResponse>(Failure);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static ValueTask<int> Words(string text, CancellationToken _) =>
        new(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

    private static void ServerWithWindow(Rig rig, int real)
    {
        rig.Server.PropsResponse = () => Json(Props(real));
        rig.Server.SlotsResponse = () => Json(Slots(real));
    }

    [Fact]
    public async Task A_verified_smaller_window_fails_before_the_model_with_a_permanent_context_window_mismatch()
    {
        using var rig = new Rig();
        ServerWithWindow(rig, 16384);
        var model = new CountingModel();
        var agent = new AgentFactory(rig.Options, model, Words, runtime: rig.Monitor);

        var client = await agent.CreateCompactingAgentAsync("A", "Sei un assistente.", Ct);
        var e = await Assert.ThrowsAsync<ContextWindowMismatchException>(() => client.RunAsync("ciao", cancellationToken: Ct));

        Assert.Equal(0, model.Calls);
        Assert.Equal("context_window_mismatch", e.Code);
        Assert.Equal(32768, e.ConfiguredTokens);
        Assert.Equal(16384, e.RuntimeTokens);
        var failure = ModelFailure.Classify(e);
        Assert.False(failure.IsTransient);
        Assert.Equal("context_window_mismatch", failure.Code);
        Assert.Contains("16384", failure.Detail);
        Assert.Contains("32768", failure.Detail);
        Assert.DoesNotContain("ciao", failure.Detail + e.Message);   // nessun contenuto proprietario
    }

    [Fact]
    public async Task The_mismatch_also_stops_streaming_and_the_document_client_before_the_model()
    {
        using var rig = new Rig();
        ServerWithWindow(rig, 8192);
        var model = new CountingModel();
        var factory = new AgentFactory(rig.Options, model, Words, runtime: rig.Monitor);

        var chat = await factory.CreateCompactingAgentAsync("A", "Sei un assistente.", Ct);
        await Assert.ThrowsAsync<ContextWindowMismatchException>(async () =>
        {
            await foreach (var _ in chat.RunStreamingAsync("ciao", cancellationToken: Ct))
            {
            }
        });
        await Assert.ThrowsAsync<ContextWindowMismatchException>(() => factory.CreateDocumentClient().GetResponseAsync("ciao", cancellationToken: Ct));

        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_mismatch_hit_by_the_summarizer_is_not_masked_as_compaction_failed()
    {
        using var rig = new Rig(new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen3.6-35B", ApiKey = "test-key",
            ContextWindowTokens = 300, MaxOutputTokens = 50, SafetyMarginTokens = 50, CompactionTriggerTokens = 60,
        });
        ServerWithWindow(rig, 200);
        var model = new CountingModel();
        var agent = await new AgentFactory(rig.Options, model, Words, runtime: rig.Monitor).CreateCompactingAgentAsync("A", "Sei un assistente.", Ct);
        var dialog = Enumerable.Range(0, 20)
            .Select(i => new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"m{i} uno due tre quattro cinque sei sette otto nove")).ToList();

        var e = await Assert.ThrowsAsync<ContextWindowMismatchException>(() => agent.RunAsync(dialog, cancellationToken: Ct));

        Assert.Equal("context_window_mismatch", ModelFailure.Classify(e).Code);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task An_equal_or_larger_real_window_lets_the_call_through()
    {
        foreach (var real in new[] { 32768, 65536 })
        {
            using var rig = new Rig();
            ServerWithWindow(rig, real);
            var model = new CountingModel();
            var client = await new AgentFactory(rig.Options, model, Words, runtime: rig.Monitor).CreateCompactingAgentAsync("A", "Sei un assistente.", Ct);

            await client.RunAsync("ciao", cancellationToken: Ct);

            Assert.Equal(1, model.Calls);
            Assert.False(rig.Monitor.Current.WindowMismatch);
        }
    }

    [Fact]
    public async Task An_offline_or_unverified_runtime_never_blocks_the_call_and_the_network_error_stays_transient()
    {
        using var rig = new Rig();
        rig.Server.Models = () => throw new HttpRequestException("giù");
        rig.Server.PropsResponse = () => throw new HttpRequestException("giù");
        var model = new CountingModel { Failure = new HttpRequestException("giù") };
        var client = await new AgentFactory(rig.Options, model, Words, runtime: rig.Monitor).CreateCompactingAgentAsync("A", "Sei un assistente.", Ct);

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.RunAsync("ciao", cancellationToken: Ct));

        Assert.Equal(1, model.Calls);                         // la chiamata procede: è il modello a fallire, non il guard
        Assert.True(ModelFailure.Classify(e).IsTransient);
        Assert.Equal(ModelRuntimeStatus.Offline, rig.Monitor.Current.Status);

        rig.Server.Models = () => new HttpResponseMessage(HttpStatusCode.Unauthorized);       // raggiungibile ma non verificabile
        rig.Server.PropsResponse = () => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        rig.Monitor.Invalidate();
        model.Failure = null;
        await client.RunAsync("ciao", cancellationToken: Ct);
        Assert.Equal(ModelRuntimeStatus.Unverified, rig.Monitor.Current.Status);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task A_transient_model_failure_invalidates_the_cache_so_the_retry_rereads_the_runtime()
    {
        using var rig = new Rig();
        var model = new CountingModel { Failure = new HttpRequestException("riavvio del server") };
        var client = await new AgentFactory(rig.Options, model, Words, runtime: rig.Monitor).CreateCompactingAgentAsync("A", "Sei un assistente.", Ct);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RunAsync("ciao", cancellationToken: Ct));
        Assert.Equal(3, rig.Server.Paths.Count);

        // Il server riparte con una finestra più piccola: il tentativo successivo, ben entro il TTL, lo rileva e non invia nulla.
        ServerWithWindow(rig, 16384);
        model.Failure = null;
        await Assert.ThrowsAsync<ContextWindowMismatchException>(() => client.RunAsync("ciao", cancellationToken: Ct));

        Assert.Equal(6, rig.Server.Paths.Count);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Monitor_reads_use_only_the_configured_host_with_bearer_auth()
    {
        var seen = new List<(Uri Uri, string? Auth)>();
        var server = new FakeServer();
        using var inspecting = new InspectingHandler(server, seen);
        using var client = new InternalModelClient(Options(), inspecting);
        var monitor = new ModelRuntimeMonitor(client, Options(), new CapturingLogger(), new ManualTime());

        await monitor.GetSnapshotAsync(Ct);

        Assert.All(seen, r =>
        {
            Assert.Equal("dgx.internal", r.Uri.Host);
            Assert.Equal("Bearer test-key", r.Auth);
        });
        Assert.Contains(seen, r => r.Uri.PathAndQuery == "/props?model=Qwen3.6-35B");
    }

    /// <summary>Sonda reale in sola lettura (Category=MafReal, Skipped senza DGX): la forma delle risposte è quella attesa e la finestra coincide con /props.</summary>
    [Fact]
    [Trait("Category", TestCategories.MafReal)]
    public async Task The_real_runtime_is_probed_read_only_and_reports_a_window_equal_to_props()
    {
        var options = TestEnvironment.RequireDgxOptions();
        using var client = new InternalModelClient(options);
        var monitor = new ModelRuntimeMonitor(client, options, new CapturingLogger());

        var s = await monitor.GetSnapshotAsync(Ct);

        Assert.NotEqual(ModelRuntimeStatus.Offline, s.Status);
        Assert.NotNull(s.CheckedAt);
        if (s.Status == ModelRuntimeStatus.Online)
        {
            Assert.Equal(await client.GetRuntimeContextTokensAsync(Ct), s.ContextWindowTokens);
            Assert.True(s.ServerDefaultNPredict is null or > 0);
        }
    }

    private sealed class InspectingHandler(HttpMessageHandler inner, List<(Uri, string?)> seen) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (seen)
            {
                seen.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
