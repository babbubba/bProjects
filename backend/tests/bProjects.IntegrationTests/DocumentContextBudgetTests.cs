using System.Net;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace bProjects.IntegrationTests;

/// <summary>
/// Budget e preflight del payload effettivo del ramo documentale (T038/T039, contracts/ai-workflow.md §6, plan.md «Budget»).
/// Runtime FINTO (llama.cpp simulato, nessun DGX): due modelli con finestra e tokenizer distinti, template applicato esposto
/// (o no), conteggio per parole. Le chiamate di completamento sono registrate: fuori budget o non verificato, ne parte zero.
/// </summary>
public sealed class DocumentContextBudgetTests
{
    private const string ModelA = "Qwen-A";
    private const string ModelB = "Qwen-B";

    // Schema di 1 token (nessuno spazio) per entrambi i tokenizer finti.
    private const string Schema = """{"type":"object"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Cap configurato 2000, O=100, M=20; fase Extraction sul modello B con O=200. A: n_ctx 1000, 1 token/parola. B: n_ctx 4000, 2 token/parola.</summary>
    private static AiOptions Options(bool documentThinking = false)
    {
        var o = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/",
            ModelId = ModelA,
            ApiKey = "test-key",
            ContextWindowTokens = 2000,
            MaxOutputTokens = 100,
            SafetyMarginTokens = 20,
            RuntimeCheckSeconds = 60,
        };
        o.Phases.Extraction.ModelId = ModelB;
        o.Phases.Extraction.MaxOutputTokens = 200;
        o.Phases.Document.EnableThinking = documentThinking;
        return o;
    }

    private static string Words(int n) => string.Join(' ', Enumerable.Repeat("w", n));

    private static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>llama.cpp simulato. Tokenizer e finestra dipendono dal campo/parametro <c>model</c> della richiesta.</summary>
    private sealed class FakeRuntime : HttpMessageHandler
    {
        public Dictionary<string, int?> Windows { get; } = new() { [ModelA] = 1000, [ModelB] = 4000 };

        public Dictionary<string, int> TokensPerWord { get; } = new() { [ModelA] = 1, [ModelB] = 2 };

        public bool TemplateExposed { get; set; } = true;

        public bool TokenizerAvailable { get; set; } = true;

        public bool Online { get; set; } = true;

        public string[] Listed { get; set; } = [ModelA, ModelB];

        public List<(string Model, string Content)> Tokenized { get; } = [];

        public List<string> Completions { get; } = [];

        public List<string> Hosts { get; } = [];

        public List<string> Paths { get; } = [];

        public bool AllRequestsCarryTheKey { get; private set; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths)
            {
                Hosts.Add(request.RequestUri.Host);
                Paths.Add(path);
                AllRequestsCarryTheKey &= request.Headers.Authorization?.Parameter == "test-key";
            }

            if (!Online)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            switch (path)
            {
                case "/v1/models":
                    return Json("{\"object\":\"list\",\"data\":[" + string.Join(',', Listed.Select(id =>
                        "{\"id\":\"" + id + "\",\"object\":\"model\",\"status\":{\"value\":\"loaded\",\"args\":[\"llama-server\"]}}")) + "]}");
                case "/props":
                {
                    var model = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["model"]!;
                    return Windows.GetValueOrDefault(model) is { } window
                        ? Json("{\"default_generation_settings\":{\"n_ctx\":" + window + ",\"params\":{\"n_predict\":-1}},\"total_slots\":1}")
                        : Json("""{"total_slots":1}""");
                }

                case "/slots":
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                case "/tokenize":
                {
                    if (!TokenizerAvailable)
                    {
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }

                    using var doc = JsonDocument.Parse(body);
                    var model = doc.RootElement.GetProperty("model").GetString()!;
                    var content = doc.RootElement.GetProperty("content").GetString()!;
                    lock (Tokenized)
                    {
                        Tokenized.Add((model, content));
                    }

                    var count = WordCount(content) * TokensPerWord[model];
                    return Json("{\"tokens\":[" + string.Join(',', Enumerable.Range(0, count)) + "]}");
                }

                case "/apply-template":
                {
                    if (!TemplateExposed)
                    {
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }

                    using var doc = JsonDocument.Parse(body);
                    var prompt = string.Join('\n', doc.RootElement.GetProperty("messages").EnumerateArray().Select(m =>
                    {
                        var role = m.GetProperty("role").GetString();
                        return $"[{role}] {m.GetProperty("content").GetString()} [/{role}]";   // 2 token di framing per messaggio
                    }));
                    return Json(JsonSerializer.Serialize(new { prompt }));
                }

                case "/v1/chat/completions":
                    lock (Completions)
                    {
                        Completions.Add(body);
                    }

                    return Json("""{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(AiOptions? options = null, PayloadCalibration? calibration = null, FakeRuntime? runtime = null)
        {
            Options = options ?? DocumentContextBudgetTests.Options();
            Server = runtime ?? new FakeRuntime();
            Client = new InternalModelClient(Options, Server);
            Monitor = new ModelRuntimeMonitor(Client, Options, NullLogger<ModelRuntimeMonitor>.Instance);
            Factory = new AgentFactory(
                Options, Client.ChatClient, (text, ct) => new(Client.CountTokensAsync(text, ct)), null, Monitor, Client, calibration);
        }

        public AiOptions Options { get; }

        public FakeRuntime Server { get; }

        public InternalModelClient Client { get; }

        public ModelRuntimeMonitor Monitor { get; }

        public AgentFactory Factory { get; }

        /// <summary>Opzioni della fase come in produzione (modello, tetto di output, schema nativo) più le istruzioni.</summary>
        public ChatOptions ChatOptions(AiPhase phase, string instructions = "sys", bool schema = true)
        {
            var o = Client.CreateChatOptions(schema ? JsonDocument.Parse(Schema).RootElement.Clone() : null, "result", 0, phase);
            o.Instructions = instructions;
            return o;
        }

        public DocumentPayload Payload(AiPhase phase, int userWords, string instructions = "sys") =>
            new(phase, instructions, [new ChatMessage(ChatRole.User, Words(userWords))], Schema);

        public void Dispose() => Client.Dispose();
    }

    [Fact]
    public async Task Each_model_and_phase_resolves_its_own_window_budget_and_fingerprint_from_its_own_probe()
    {
        using var rig = new Rig();

        var document = await rig.Factory.Preflight.ResolveProfileAsync(AiPhase.Document, Ct);
        var extraction = await rig.Factory.Preflight.ResolveProfileAsync(AiPhase.Extraction, Ct);

        // W = min(cap configurato 2000, finestra misurata del PROPRIO modello); B = W - O - M.
        Assert.Equal((ModelA, 1000, 100, 20, 880), (document.ModelId, document.WindowTokens, document.OutputTokens, document.MarginTokens, document.InputBudgetTokens));
        Assert.Equal((ModelB, 2000, 200, 20, 1780), (extraction.ModelId, extraction.WindowTokens, extraction.OutputTokens, extraction.MarginTokens, extraction.InputBudgetTokens));
        Assert.NotEqual(document.Fingerprint, extraction.Fingerprint);
        Assert.True(document.TemplateExposed);

        // Probe/cache/count per modello: ogni modello sondato e contato una volta, nessuna sonda globale riusata per l'altro.
        await rig.Factory.Preflight.ResolveProfileAsync(AiPhase.Document, Ct);
        await rig.Factory.Preflight.ResolveProfileAsync(AiPhase.Extraction, Ct);
        Assert.Equal(2, rig.Server.Paths.Count(p => p == "/props"));
        Assert.Equal([ModelA, ModelB], rig.Server.Tokenized.Where(t => t.Content == "probe").Select(t => t.Model).Order().ToArray());
        Assert.Equal(ModelA, rig.Monitor.Current.ModelId);   // il ramo legacy continua a leggere il modello globale
    }

    [Fact]
    public async Task The_configured_cap_wins_over_a_larger_runtime_window_and_the_window_is_never_taken_from_configuration()
    {
        var o = Options();
        o.ContextWindowTokens = 600;   // più piccolo dei 1000 misurati: W = 600
        using var rig = new Rig(o);

        var profile = await rig.Factory.Preflight.ResolveProfileAsync(AiPhase.Document, Ct);

        Assert.Equal(600, profile.WindowTokens);
        Assert.Equal(480, profile.InputBudgetTokens);
        Assert.Equal(o.ContextWindowTokens - o.MaxOutputTokens - o.SafetyMarginTokens, ContextBudget.From(o, AiPhase.Document).InputBudgetTokens);   // policy legacy invariata
    }

    [Fact]
    public async Task Reasoning_is_part_of_the_total_output_reserve_and_is_not_subtracted_twice()
    {
        using var plain = new Rig(Options(documentThinking: false));
        using var thinking = new Rig(Options(documentThinking: true));

        var without = await plain.Factory.Preflight.ResolveProfileAsync(AiPhase.Document, Ct);
        var with = await thinking.Factory.Preflight.ResolveProfileAsync(AiPhase.Document, Ct);

        Assert.Equal(880, without.InputBudgetTokens);
        Assert.Equal(880, with.InputBudgetTokens);   // O è già il massimo totale con ragionamento: B = W - O - M in entrambi i casi
        Assert.True(with.Thinking);
        Assert.NotEqual(without.Fingerprint, with.Fingerprint);   // ma il ragionamento fa parte dell'impronta congelata
    }

    [Fact]
    public async Task The_whole_payload_is_counted_with_template_framing_and_schema_exactly_at_the_budget_and_one_token_over()
    {
        using var rig = new Rig();
        // Istruzione «sys»: 1 + 2 di framing; utente: N + 2; schema: 1  =>  T = N + 6;  B = 880.
        const int atBudget = 874;

        var exact = await rig.Factory.Preflight.MeasureAsync(rig.Payload(AiPhase.Document, atBudget), Ct);
        Assert.Equal(880, exact.Tokens);
        Assert.Equal(TokenCountQuality.Measured, exact.Quality);
        Assert.True(exact.Fits);

        var over = await rig.Factory.Preflight.MeasureAsync(rig.Payload(AiPhase.Document, atBudget + 1), Ct);
        Assert.Equal(881, over.Tokens);
        Assert.False(over.Fits);

        // Sul client batch: esattamente al budget parte, un token oltre no e il modello non viene chiamato.
        var client = rig.Factory.CreateBatchClient(AiPhase.Document);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, Words(atBudget))], rig.ChatOptions(AiPhase.Document), Ct);
        Assert.Single(rig.Server.Completions);

        var overflow = await Assert.ThrowsAsync<ContextOverflowException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, Words(atBudget + 1))], rig.ChatOptions(AiPhase.Document), Ct));
        Assert.Equal((881, 880), (overflow.Tokens, overflow.BudgetTokens));
        Assert.Single(rig.Server.Completions);   // nessuna chiamata fuori budget
    }

    [Fact]
    public async Task Tokenizers_are_distinct_per_model_and_the_same_text_counts_differently()
    {
        using var rig = new Rig();

        // B conta 2 token per parola, anche per il rendering del template: T = 2 * ((1+2) + (N+2)) + 2 * 1 = 2N + 12; B = 1780.
        var exact = await rig.Factory.Preflight.MeasureAsync(rig.Payload(AiPhase.Extraction, 884), Ct);
        Assert.Equal(1780, exact.Tokens);
        Assert.True(exact.Fits);

        var over = await rig.Factory.Preflight.MeasureAsync(rig.Payload(AiPhase.Extraction, 885), Ct);
        Assert.Equal(1782, over.Tokens);
        Assert.False(over.Fits);

        // Lo stesso identico payload sul modello A (tokenizer da 1 token/parola) pesa la metà.
        var onA = await rig.Factory.Preflight.MeasureAsync(rig.Payload(AiPhase.Document, 884), Ct);
        Assert.Equal(890, onA.Tokens);
        Assert.All(rig.Server.Tokenized.Where(t => t.Content != "probe"), t => Assert.Equal(t.Model == ModelB ? ModelB : ModelA, t.Model));
        Assert.Contains(rig.Server.Tokenized, t => t.Model == ModelB && t.Content.Contains("[system] sys [/system]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_irreducible_single_element_is_reported_and_is_never_sent_truncated()
    {
        using var rig = new Rig();
        var huge = rig.Payload(AiPhase.Document, 5000);

        var measured = await rig.Factory.Preflight.MeasureAsync(huge, Ct);
        Assert.False(measured.Fits);
        Assert.True(measured.Tokens > measured.InputBudgetTokens);

        var client = rig.Factory.CreateBatchClient(AiPhase.Document);
        await Assert.ThrowsAsync<ContextOverflowException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, Words(5000))], rig.ChatOptions(AiPhase.Document), Ct));
        Assert.Empty(rig.Server.Completions);
    }

    [Fact]
    public async Task Without_an_exposed_template_the_count_needs_an_identified_calibration_and_is_labelled_an_upper_bound()
    {
        var runtime = new FakeRuntime { TemplateExposed = false };
        using var uncalibrated = new Rig(runtime: runtime);

        // Nessuna calibrazione: una semplice somma di testi non è un conteggio ammissibile (unavailable), nessun invio.
        var unknown = await uncalibrated.Factory.Preflight.MeasureAsync(uncalibrated.Payload(AiPhase.Document, 10), Ct);
        Assert.Equal(TokenCountQuality.Unavailable, unknown.Quality);
        Assert.False(unknown.Fits);
        var client = uncalibrated.Factory.CreateBatchClient(AiPhase.Document);
        var refused = await Assert.ThrowsAsync<RuntimeUnverifiedException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, Words(10))], uncalibrated.ChatOptions(AiPhase.Document), Ct));
        Assert.Equal("runtime_unverified", refused.Code);
        Assert.Empty(uncalibrated.Server.Completions);

        // Con calibrazione identificata: testi misurati + framing calibrato = limite superiore, dichiarato come tale.
        using var calibrated = new Rig(calibration: new PayloadCalibration("tmpl-qwen-v1", PerMessageFramingTokens: 4, FixedOverheadTokens: 3), runtime: runtime);
        var bound = await calibrated.Factory.Preflight.MeasureAsync(calibrated.Payload(AiPhase.Document, 10), Ct);
        Assert.Equal(TokenCountQuality.CalibratedUpperBound, bound.Quality);
        Assert.Equal(3 + 4 * 2 + 1 + 10 + 1, bound.Tokens);
        Assert.Equal("tmpl-qwen-v1", bound.Profile.CalibrationId);
        Assert.False(bound.Profile.TemplateExposed);
        Assert.True(bound.Fits);

        var exposed = new Rig();
        var exact = await exposed.Factory.Preflight.ResolveProfileAsync(AiPhase.Document, Ct);
        Assert.NotEqual(exact.Fingerprint, bound.Profile.Fingerprint);   // template misurato e calibrato non condividono l'impronta
        exposed.Dispose();
    }

    [Fact]
    public async Task An_unknown_tokenizer_certifies_nothing_unless_a_calibrated_upper_bound_exists()
    {
        var runtime = new FakeRuntime { TokenizerAvailable = false, TemplateExposed = false };
        using var none = new Rig(runtime: runtime);
        Assert.Equal(TokenCountQuality.Unavailable, (await none.Factory.Preflight.MeasureAsync(none.Payload(AiPhase.Document, 10), Ct)).Quality);
        await Assert.ThrowsAsync<RuntimeUnverifiedException>(() => none.Factory.Preflight.EnsureAsync(none.Payload(AiPhase.Document, 10), Ct).AsTask());

        using var framingOnly = new Rig(runtime: runtime, calibration: new PayloadCalibration("c", 4, 3));   // senza minimo caratteri/token
        Assert.Equal(TokenCountQuality.Unavailable, (await framingOnly.Factory.Preflight.MeasureAsync(framingOnly.Payload(AiPhase.Document, 10), Ct)).Quality);

        using var chars = new Rig(runtime: runtime, calibration: new PayloadCalibration("c", 4, 3, MinCharsPerToken: 1.0));
        var bound = await chars.Factory.Preflight.MeasureAsync(chars.Payload(AiPhase.Document, 10), Ct);
        Assert.Equal(TokenCountQuality.CalibratedUpperBound, bound.Quality);
        Assert.True(bound.Tokens >= 10);   // ceil(caratteri / 1.0) non può sottostimare
        Assert.Empty(chars.Server.Completions);
    }

    [Fact]
    public async Task An_unverified_window_offline_server_or_missing_model_blocks_every_send()
    {
        // Finestra non riportata né da /props né da /slots.
        var unknownWindow = new FakeRuntime { Windows = { [ModelA] = null } };
        using (var rig = new Rig(runtime: unknownWindow))
        {
            await Assert.ThrowsAsync<RuntimeUnverifiedException>(
                () => rig.Factory.CreateBatchClient(AiPhase.Document).GetResponseAsync([new ChatMessage(ChatRole.User, "x")], rig.ChatOptions(AiPhase.Document), Ct));
            Assert.Empty(rig.Server.Completions);
        }

        // Server non raggiungibile: nessuna capacità nota; transitorio (un nuovo tentativo può riuscire).
        using (var rig = new Rig(runtime: new FakeRuntime { Online = false }))
        {
            var offline = await Assert.ThrowsAsync<RuntimeUnverifiedException>(() => rig.Factory.Preflight.ResolveProfileAsync(AiPhase.Document, Ct).AsTask());
            Assert.True(offline.Transient);
            Assert.True(ModelFailure.Classify(offline).IsTransient);
            Assert.Equal("runtime_unverified", ModelFailure.Classify(offline).Code);
        }

        // Modello della fase non elencato dal server: nessun fallback su un altro modello.
        using (var rig = new Rig(runtime: new FakeRuntime { Listed = [ModelA] }))
        {
            var missing = await Assert.ThrowsAsync<ModelNotAvailableException>(() => rig.Factory.Preflight.ResolveProfileAsync(AiPhase.Extraction, Ct).AsTask());
            Assert.Equal(ModelB, missing.ModelId);
            Assert.False(ModelFailure.Classify(missing).IsTransient);
        }

        // Senza monitor/contatore (ramo legacy costruito come prima) il preflight documentale non certifica nulla.
        var legacy = new AgentFactory(Options(), new ThrowingModel(), (text, _) => new(WordCount(text)));
        await Assert.ThrowsAsync<RuntimeUnverifiedException>(() => legacy.Preflight.ResolveProfileAsync(AiPhase.Document, Ct).AsTask());
    }

    [Fact]
    public async Task A_frozen_fingerprint_that_no_longer_matches_the_runtime_stops_the_call()
    {
        using var rig = new Rig();
        var frozen = (await rig.Factory.Preflight.ResolveProfileAsync(AiPhase.Document, Ct)).Fingerprint;
        var client = rig.Factory.CreateBatchClient(AiPhase.Document, frozen);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], rig.ChatOptions(AiPhase.Document), Ct);

        // Il server viene riavviato con una finestra diversa: la sonda successiva cambia W e quindi l'impronta.
        rig.Server.Windows[ModelA] = 900;
        rig.Monitor.Invalidate();
        var drift = await Assert.ThrowsAsync<RuntimeFingerprintMismatchException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], rig.ChatOptions(AiPhase.Document), Ct));

        Assert.Equal(frozen, drift.Expected);
        Assert.NotEqual(frozen, drift.Actual);
        Assert.Equal("tokenizer_mismatch", ModelFailure.Classify(drift).Code);
        Assert.Single(rig.Server.Completions);
    }

    [Fact]
    public async Task The_payload_hash_covers_the_complete_payload_and_is_stable_for_the_same_logical_input()
    {
        using var rig = new Rig();
        var p = rig.Payload(AiPhase.Document, 20);

        var first = await rig.Factory.Preflight.MeasureAsync(p, Ct);
        var again = await rig.Factory.Preflight.MeasureAsync(rig.Payload(AiPhase.Document, 20), Ct);
        var otherSchema = await rig.Factory.Preflight.MeasureAsync(p with { ResponseSchemaJson = """{"type":"array"}""" }, Ct);
        var otherInstructions = await rig.Factory.Preflight.MeasureAsync(p with { Instructions = "altro" }, Ct);
        var otherModel = await rig.Factory.Preflight.MeasureAsync(rig.Payload(AiPhase.Extraction, 20), Ct);

        Assert.Equal(first.PayloadHash, again.PayloadHash);
        Assert.Equal(first.Profile.Fingerprint, again.Profile.Fingerprint);
        Assert.NotEqual(first.PayloadHash, otherSchema.PayloadHash);
        Assert.NotEqual(first.PayloadHash, otherInstructions.PayloadHash);
        Assert.NotEqual(first.PayloadHash, otherModel.PayloadHash);
    }

    [Fact]
    public async Task Batch_agents_run_in_fresh_sessions_without_history_so_three_batches_never_accumulate()
    {
        using var rig = new Rig();
        // Tre batch al 60% del budget di input (880): ciascuno entro budget, insieme ben oltre la finestra.
        var agent = rig.Factory.CreateBatchAgent("batch", "batch", "sys", rig.ChatOptions(AiPhase.Document, instructions: "sys"), AiPhase.Document);
        var bodies = new List<string>();
        foreach (var marker in new[] { "ALFA", "BETA", "GAMMA" })
        {
            await AgentFactory.RunBatchAsync(agent, [new ChatMessage(ChatRole.User, marker + " " + Words(520))], Ct);
            bodies.Add(rig.Server.Completions[^1]);
        }

        Assert.Equal(3, rig.Server.Completions.Count);
        Assert.Contains("ALFA", bodies[0]);
        Assert.DoesNotContain("ALFA", bodies[1]);
        Assert.DoesNotContain("ALFA", bodies[2]);
        Assert.DoesNotContain("BETA", bodies[2]);
        Assert.All(bodies, body => Assert.Equal(1, JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray().Count(m => m.GetProperty("role").GetString() == "user")));
    }

    [Fact]
    public async Task Startup_and_archive_do_not_depend_on_the_probe_and_the_credential_only_goes_to_the_internal_host()
    {
        var runtime = new FakeRuntime { Online = false };
        using var rig = new Rig(runtime: runtime);

        // Costruire client, monitor e fabbrica non esegue alcuna richiesta: l'avvio e l'archivio non attendono il DGX.
        Assert.Empty(runtime.Paths);
        Assert.Equal(ModelRuntimeStatus.Unverified, rig.Monitor.Current.Status);
        _ = rig.Factory.CreateBatchClient(AiPhase.Document);
        Assert.Empty(runtime.Paths);

        // Le sonde e i conteggi vanno solo all'endpoint interno configurato, con la chiave Bearer.
        runtime.Online = true;
        await rig.Factory.Preflight.EnsureAsync(rig.Payload(AiPhase.Document, 5), Ct);
        Assert.NotEmpty(runtime.Paths);
        Assert.All(runtime.Hosts, host => Assert.Equal("dgx.internal", host));
        Assert.True(runtime.AllRequestsCarryTheKey);
    }

    private sealed class ThrowingModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("il modello non deve essere chiamato");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("il modello non deve essere chiamato");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
