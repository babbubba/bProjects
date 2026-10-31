using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace bProjects.IntegrationTests;

/// <summary>
/// Modello, ragionamento, output e timeout per fase (Qwen3.6-35B-Smart predefinito, Qwen3.8 selezionabile per l'analisi):
/// i parametri sono verificati sul corpo HTTP realmente inviato, attraverso la stessa pipeline usata in produzione.
/// </summary>
public sealed class PhaseModelRoutingTests
{
    private const string Qwen36 = "Qwen3.6-35B-Smart";
    private const string Qwen38 = "Qwen3.8-Flash-Next-Smart";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>I valori di default di infra/compose.yaml (variabili Ai__*), letti dal binder come in produzione.</summary>
    private static AiOptions ComposeDefaults(string? analysisModel = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Endpoint"] = "http://dgx.internal:8080/v1/",
            ["Ai:ModelId"] = Qwen36,
            ["Ai:ApiKey"] = "k",
            ["Ai:ContextWindowTokens"] = "131072",
            ["Ai:MaxOutputTokens"] = "4096",
            ["Ai:EnableThinking"] = "false",
            ["Ai:RequestTimeoutSeconds"] = "150",
            ["Ai:Phases:Chat:ModelId"] = "",
            ["Ai:Phases:Chat:EnableThinking"] = "false",
            ["Ai:Phases:Chat:RequestTimeoutSeconds"] = "",
            ["Ai:Phases:Analysis:ModelId"] = analysisModel ?? "",
            ["Ai:Phases:Analysis:MaxOutputTokens"] = "32768",
            ["Ai:Phases:Analysis:EnableThinking"] = "true",
            ["Ai:Phases:Analysis:RequestTimeoutSeconds"] = "900",
            ["Ai:Phases:Document:ModelId"] = "",
            ["Ai:Phases:Document:EnableThinking"] = "false",
            ["Ai:Phases:Compaction:ModelId"] = "",
            ["Ai:Phases:Compaction:EnableThinking"] = "false",
        }).Build().GetSection(AiOptions.SectionName).Get<AiOptions>()!;

    [Fact]
    public void The_compose_defaults_resolve_qwen36_everywhere_with_thinking_only_in_the_analysis()
    {
        var o = ComposeDefaults();

        Assert.True(new AiOptionsValidator().Validate(null, o).Succeeded);
        Assert.All(Enum.GetValues<AiPhase>(), phase => Assert.Equal(Qwen36, o.ForPhase(phase).ModelId));
        Assert.False(o.ForPhase(AiPhase.Chat).EnableThinking);
        Assert.False(o.ForPhase(AiPhase.Compaction).EnableThinking);
        Assert.False(o.ForPhase(AiPhase.Document).EnableThinking);
        Assert.True(o.ForPhase(AiPhase.Analysis).EnableThinking);
        Assert.Equal(32768, o.ForPhase(AiPhase.Analysis).MaxOutputTokens);
        Assert.Equal(131072 - 32768 - 1024, o.ForPhase(AiPhase.Analysis).InputBudgetTokens);
        Assert.Equal(900, o.ForPhase(AiPhase.Analysis).RequestTimeoutSeconds);
        Assert.Equal(150, o.ForPhase(AiPhase.Chat).RequestTimeoutSeconds);
        Assert.Equal(150, o.ForPhase(AiPhase.Document).RequestTimeoutSeconds);
        Assert.Equal(900, o.MaxRequestTimeoutSeconds);

        Assert.Equal(Qwen38, ComposeDefaults(Qwen38).ForPhase(AiPhase.Analysis).ModelId);
        Assert.Equal(Qwen36, ComposeDefaults(Qwen38).ForPhase(AiPhase.Chat).ModelId);
    }

    [Fact]
    public void The_analysis_timeout_must_fit_the_analysis_deadline_and_the_other_phases_keep_the_common_one()
    {
        var ai = Microsoft.Extensions.Options.Options.Create(ComposeDefaults());

        // Senza deadline dedicata (180 s comuni) i 900 s dell'analisi sono incompatibili: la configurazione è rifiutata.
        var common = new ExecutionOptionsValidator(ai).Validate(null, new ExecutionOptions());
        Assert.True(common.Failed);
        Assert.Contains(common.Failures!, f => f.Contains("AnalysisRunDeadlineSeconds", StringComparison.Ordinal));

        var policy = new ExecutionOptions { AnalysisRunDeadlineSeconds = 1200 };
        Assert.True(new ExecutionOptionsValidator(ai).Validate(null, policy).Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(1200), policy.AnalysisRunDeadline);
        Assert.Equal(TimeSpan.FromSeconds(180), policy.RunDeadline);   // chat e documento invariati

        ai.Value.Phases.Document.RequestTimeoutSeconds = 180;
        Assert.True(new ExecutionOptionsValidator(ai).Validate(null, policy).Failed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Qwen38)]
    public async Task Each_phase_sends_its_model_thinking_flag_and_output_cap_without_contaminating_the_others(string? analysisModel)
    {
        var o = ComposeDefaults(analysisModel);
        var server = new Server();
        using var client = new InternalModelClient(o, server);
        var factory = AgentFactory.Create(o, client);
        var chatOptions = client.CreateChatOptions(temperature: 0.2f, phase: AiPhase.Chat);
        var analysisOptions = client.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0, phase: AiPhase.Analysis);
        var documentOptions = client.CreateChatOptions(temperature: 0.2f, phase: AiPhase.Document);
        var consolidation = new ConversationalAgent(factory, chatOptions);

        // Ordine alternato: le opzioni di una fase non devono trapelare nella richiesta successiva.
        var chat = await ChatAgentAsync(factory, chatOptions);
        await chat.RunAsync([new ChatMessage(ChatRole.User, "ciao")], cancellationToken: Ct);
        var analysis = await consolidation.CreateConsolidationAgentAsync(analysisOptions, Ct);
        await analysis.RunAsync([new ChatMessage(ChatRole.User, "consolida")], cancellationToken: Ct);
        await factory.CreateDocumentClient().GetResponseAsync([new ChatMessage(ChatRole.User, "documento")], documentOptions, Ct);
        await chat.RunAsync([new ChatMessage(ChatRole.User, "di nuovo")], cancellationToken: Ct);

        Assert.Equal(4, server.Completions.Count);
        AssertRequest(server.Completions[0], Qwen36, thinking: false, maxTokens: 4096, structured: false);
        AssertRequest(server.Completions[1], analysisModel ?? Qwen36, thinking: true, maxTokens: 32768, structured: true);
        AssertRequest(server.Completions[2], Qwen36, thinking: false, maxTokens: 4096, structured: false);
        AssertRequest(server.Completions[3], Qwen36, thinking: false, maxTokens: 4096, structured: false);
    }

    [Fact]
    public async Task The_native_compaction_summary_is_sent_without_thinking_even_inside_the_analysis()
    {
        // Finestra piccola: oltre al prompt analysis-v3 la sintesi nativa MAF scatta con pochi messaggi (token = parole, /tokenize simulato).
        var o = ComposeDefaults();
        o.ContextWindowTokens = 6000;
        o.SafetyMarginTokens = 50;
        o.Phases.Analysis.MaxOutputTokens = 100;
        o.Phases.Compaction.MaxOutputTokens = 40;
        var server = new Server();
        using var client = new InternalModelClient(o, server);
        var factory = AgentFactory.Create(o, client);
        var analysisOptions = client.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0, phase: AiPhase.Analysis);
        var agent = await new ConversationalAgent(factory, analysisOptions).CreateConsolidationAgentAsync(analysisOptions, Ct);
        var dialog = Enumerable.Range(0, 80)
            .Select(i => new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"m{i} " + string.Join(' ', Enumerable.Repeat("parola", 60))))
            .ToList();

        await agent.RunAsync(dialog, cancellationToken: Ct);

        var summaries = server.Completions.Where(b => b.Contains("Sei un riassuntore", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(summaries);
        Assert.All(summaries, body => AssertRequest(body, Qwen36, thinking: false, maxTokens: 40, structured: false));
        var final = server.Completions.Last();
        AssertRequest(final, Qwen36, thinking: true, maxTokens: 100, structured: true);
    }

    [Fact]
    public async Task A_phase_model_not_exposed_by_the_server_fails_explicitly_without_any_request_or_fallback()
    {
        var o = ComposeDefaults("Qwen3.8-assente");
        var server = new Server();
        using var client = new InternalModelClient(o, server);
        var runtime = new ModelRuntimeMonitor(client, o, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = AgentFactory.Create(o, client, runtime);
        var analysisOptions = client.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0, phase: AiPhase.Analysis);
        var chatOptions = client.CreateChatOptions(temperature: 0.2f, phase: AiPhase.Chat);

        var analysis = await new ConversationalAgent(factory, chatOptions).CreateConsolidationAgentAsync(analysisOptions, Ct);
        var e = await Assert.ThrowsAsync<ModelNotAvailableException>(() => analysis.RunAsync([new ChatMessage(ChatRole.User, "consolida")], cancellationToken: Ct));

        Assert.Empty(server.Completions);   // nessuna richiesta, quindi nessun altro modello al suo posto
        Assert.Equal("Qwen3.8-assente", e.ModelId);
        Assert.Equal(AiPhase.Analysis, e.Phase);
        var failure = ModelFailure.Classify(e);
        Assert.False(failure.IsTransient);
        Assert.Equal("model_not_available", failure.Code);
        Assert.Contains("Qwen3.8-assente", failure.Detail);

        // La chat usa il modello esposto (anche se non caricato: lo carica il server) e procede.
        var chat = await ChatAgentAsync(factory, chatOptions);
        await chat.RunAsync([new ChatMessage(ChatRole.User, "ciao")], cancellationToken: Ct);
        AssertRequest(Assert.Single(server.Completions), Qwen36, thinking: false, maxTokens: 4096, structured: false);
    }

    [Fact]
    public async Task A_call_longer_than_its_phase_timeout_is_a_transient_model_timeout_and_caller_cancellation_stays_its_own()
    {
        var o = ComposeDefaults();
        o.Phases.Document.RequestTimeoutSeconds = 1;
        var factory = new AgentFactory(o, new HangingModel(), (text, _) => new(text.Length));

        var e = await Assert.ThrowsAsync<TaskCanceledException>(
            () => factory.CreateDocumentClient().GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct));
        Assert.IsType<TimeoutException>(e.InnerException);
        Assert.Contains("Document", e.Message);
        var failure = ModelFailure.Classify(e);
        Assert.True(failure.IsTransient);
        Assert.Equal("model_timeout", failure.Code);

        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        caller.CancelAfter(TimeSpan.FromMilliseconds(100));
        var own = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.CreateDocumentClient().GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: caller.Token));
        Assert.IsNotType<TimeoutException>(own.InnerException);
    }

    /// <summary>Come ConversationalAgent.RunAsync: agente MAF sul client compattante della chat (il provider di contesto richiede un AIAgent).</summary>
    private static async Task<Microsoft.Agents.AI.ChatClientAgent> ChatAgentAsync(AgentFactory factory, ChatOptions chatOptions) =>
        new(await factory.CreateCompactingClientAsync(ConversationalAgent.Instructions, AiPhase.Chat, Ct),
            new Microsoft.Agents.AI.ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    ModelId = chatOptions.ModelId,
                    MaxOutputTokens = chatOptions.MaxOutputTokens,
                    Temperature = chatOptions.Temperature,
                    RawRepresentationFactory = chatOptions.RawRepresentationFactory,
                    Instructions = ConversationalAgent.Instructions,
                },
            });

    private static void AssertRequest(string body, string model, bool thinking, int maxTokens, bool structured)
    {
        var json = JsonNode.Parse(body)!;
        Assert.Equal(model, json["model"]!.GetValue<string>());
        Assert.Equal(thinking, json["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(maxTokens, json["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal(structured, json["response_format"]?["type"]?.GetValue<string>() == "json_schema");
        Assert.Null(json["reasoning_effort"]);
    }

    /// <summary>llama.cpp simulato: /v1/models espone il solo Qwen3.6 (non caricato), /tokenize conta le parole, le completion sono registrate.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public List<string> Completions { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/models" => Json($$$"""{"object":"list","data":[{"id":"{{{Qwen36}}}","object":"model","status":{"value":"unloaded","args":["llama-server","--ctx-size","131072"]}}]}"""),
                "/tokenize" => Json($$"""{"tokens":[{{string.Join(',', Enumerable.Repeat("1", Words(body)))}}]}"""),
                "/v1/chat/completions" => Complete(body),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static int Words(string tokenizeBody) =>
            Math.Max(1, JsonNode.Parse(tokenizeBody)!["content"]!.GetValue<string>().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

        private HttpResponseMessage Complete(string body)
        {
            lock (Completions)
            {
                Completions.Add(body);
            }

            var model = JsonNode.Parse(body)!["model"]!.GetValue<string>();
            return Json($$$"""{"id":"x","object":"chat.completion","created":1,"model":"{{{model}}}","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"SINTESI breve"}}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class HangingModel : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("irraggiungibile");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
