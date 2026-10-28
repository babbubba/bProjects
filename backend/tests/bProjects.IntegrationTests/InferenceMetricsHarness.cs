using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace bProjects.IntegrationTests;

/// <summary>Una richiesta di completamento come l'ha vista il server finto: solo forma, nessun contenuto.</summary>
internal sealed record CannedRequest(bool Stream, bool IncludeUsage, bool Summarizer, bool Structured, int Messages);

/// <summary>
/// Server llama.cpp OpenAI-compatibile FINTO (risposte canned, MAI il DGX): riproduce la forma reale osservata su llama.cpp b10703
/// (chunk SSE con finish_reason in un chunk a delta vuoto, poi — solo se richiesto con <c>stream_options.include_usage</c> — un chunk
/// con <c>choices: []</c>, <c>usage</c> con <c>prompt_tokens_details.cached_tokens</c> e <c>timings</c>; JSON non-streaming con usage),
/// più <c>/v1/models</c>, <c>/props</c>, <c>/slots</c> e <c>/tokenize</c> (un token ogni quattro caratteri). Il prompt conta anche
/// un overhead di template per messaggio, come il vero chat template: la quota non attribuita della composizione è positiva.
/// </summary>
internal sealed class CannedLlamaServer : HttpMessageHandler
{
    public const string ModelId = "test-model";

    /// <summary>Token di template per messaggio (ruolo, separatori) e del prompt di generazione: non attribuibili a un testo.</summary>
    public const int OverheadPerMessage = 4;
    public const int GenerationPromptOverhead = 3;

    public int Ctx { get; set; } = 32768;

    /// <summary>Il server non restituisce mai usage (né in JSON né in streaming): i token devono risultare unavailable.</summary>
    public bool OmitUsage { get; set; }

    /// <summary>Token di prompt già in cache riportati dal server (prompt_tokens_details.cached_tokens).</summary>
    public int CachedTokens { get; set; } = 7;

    /// <summary>Testo di ragionamento emesso in reasoning_content (null = nessun ragionamento).</summary>
    public string? Reasoning { get; set; }

    public string ChatReply { get; set; } = "Risposta di prova del modello.";

    /// <summary>Sostituisce l'analisi valida costruita dalla richiesta (es. output non valido).</summary>
    public string? AnalysisOverride { get; set; }

    /// <summary>Server irraggiungibile: ogni richiesta fallisce a livello di trasporto.</summary>
    public bool Offline { get; set; }

    public ConcurrentQueue<Func<HttpResponseMessage>> Faults { get; } = new();

    public ConcurrentQueue<CannedRequest> Requests { get; } = new();

    public static int Tokens(string text) => (text.Length + 3) / 4;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline)
        {
            throw new HttpRequestException("server finto offline");
        }

        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get)
        {
            return path switch
            {
                "/v1/models" => Json($$$"""{"data":[{"id":"{{{ModelId}}}","status":{"value":"loaded","args":["--ctx-size","{{{Ctx}}}"]}}]}"""),
                "/props" => Json($$$"""{"default_generation_settings":{"n_ctx":{{{Ctx}}},"params":{"n_predict":-1}},"total_slots":1}"""),
                "/slots" => Json($$$"""[{"n_ctx":{{{Ctx}}}}]"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        if (path.EndsWith("/tokenize", StringComparison.Ordinal))
        {
            return Json($$"""{"tokens":[{{string.Join(',', Enumerable.Repeat("1", Tokens(body["content"]!.GetValue<string>())))}}]}""");
        }

        var messages = body["messages"]!.AsArray().Select(m => (Role: m!["role"]!.GetValue<string>(), Text: Text(m["content"]))).ToList();
        var stream = body["stream"]?.GetValue<bool>() == true;
        var includeUsage = body["stream_options"]?["include_usage"]?.GetValue<bool>() == true;
        var summarizer = messages.Any(m => m.Role == "system" && m.Text.StartsWith(AgentFactory.SummarizationPrompt, StringComparison.Ordinal));
        var structured = body["response_format"] is not null;
        Requests.Enqueue(new CannedRequest(stream, includeUsage, summarizer, structured, messages.Count));

        if (Faults.TryDequeue(out var fault))
        {
            return fault();
        }

        var user = string.Join("\n", messages.Where(m => m.Role is not ("system" or "developer")).Select(m => m.Text));
        var reply =
            summarizer ? "Sintesi fedele: il portale si chiama Aurora."
            : structured ? AnalysisOverride ?? WorkflowRecoveryTests.AnalysisReply(user)
            : messages.Any(m => m.Role == "system" && m.Text == DocumentAgent.Instructions) ? WorkflowRecoveryTests.DocumentReply(user)
            : ChatReply;

        var prompt = messages.Sum(m => Tokens(m.Text) + OverheadPerMessage) + GenerationPromptOverhead;
        var completion = Tokens(reply) + (Reasoning is null ? 0 : Tokens(Reasoning));
        var usage = new { completion_tokens = completion, prompt_tokens = prompt, total_tokens = prompt + completion, prompt_tokens_details = new { cached_tokens = CachedTokens } };
        var timings = new { cache_n = CachedTokens, prompt_n = prompt - CachedTokens, predicted_n = completion };
        return stream ? Stream(reply, includeUsage && !OmitUsage ? usage : null, timings) : Json(Completion(reply, OmitUsage ? null : usage, timings));
    }

    private static string Text(JsonNode? content) => content is JsonArray parts
        ? string.Concat(parts.Select(p => p?["text"]?.GetValue<string>()))
        : content?.GetValue<string>() ?? "";

    private string Completion(string text, object? usage, object timings)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = text };
        if (Reasoning is not null)
        {
            message["reasoning_content"] = Reasoning;
        }

        var root = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "stop", ["index"] = 0, ["message"] = message }),
            ["created"] = 1,
            ["model"] = ModelId,
            ["system_fingerprint"] = "canned",
            ["object"] = "chat.completion",
            ["id"] = "chatcmpl-canned",
            ["timings"] = JsonSerializer.SerializeToNode(timings),
        };
        if (usage is not null)
        {
            root["usage"] = JsonSerializer.SerializeToNode(usage);
        }

        return root.ToJsonString();
    }

    private HttpResponseMessage Stream(string text, object? usage, object timings)
    {
        static string Chunk(object choices, object? usage = null, object? timings = null)
        {
            var node = new JsonObject
            {
                ["choices"] = JsonSerializer.SerializeToNode(choices),
                ["created"] = 1,
                ["id"] = "chatcmpl-canned",
                ["model"] = ModelId,
                ["system_fingerprint"] = "canned",
                ["object"] = "chat.completion.chunk",
            };
            if (usage is not null)
            {
                node["usage"] = JsonSerializer.SerializeToNode(usage);
            }

            if (timings is not null)
            {
                node["timings"] = JsonSerializer.SerializeToNode(timings);
            }

            return "data: " + node.ToJsonString() + "\n\n";
        }

        var sse = new StringBuilder(Chunk(new[] { new { finish_reason = (string?)null, index = 0, delta = new { role = "assistant", content = (string?)null } } }));
        if (Reasoning is not null)
        {
            sse.Append(Chunk(new[] { new { finish_reason = (string?)null, index = 0, delta = new { reasoning_content = Reasoning } } }));
        }

        foreach (var piece in text.Chunk(16).Select(c => new string(c)))
        {
            sse.Append(Chunk(new[] { new { finish_reason = (string?)null, index = 0, delta = new { content = piece } } }));
        }

        // Come llama.cpp: senza include_usage le timings arrivano sul chunk di fine, con include_usage sul chunk dell'usage.
        sse.Append(Chunk(new[] { new { finish_reason = "stop", index = 0, delta = new { } } }, timings: usage is null ? timings : null));
        if (usage is not null)
        {
            sse.Append(Chunk(Array.Empty<object>(), usage, timings));
        }

        sse.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    public static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new StringContent("""{"error":{"message":"guasto canned","type":"server_error"}}""", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>
/// Applicazione reale su PostgreSQL con gli stessi cablaggi di Program.cs (ConversationalAgent, workflow di analisi e documento,
/// monitor del runtime) e il vero <see cref="InternalModelClient"/> verso un server del modello passato dal test (canned o DGX).
/// </summary>
internal sealed class MetricsHost : IAsyncDisposable
{
    private readonly InternalModelClient _model;

    private MetricsHost(ApiHarness app, InternalModelClient model, AiOptions options)
    {
        App = app;
        _model = model;
        Options = options;
    }

    public ApiHarness App { get; }

    public AiOptions Options { get; }

    public InternalModelClient Model => _model;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static AiOptions CannedOptions(Action<AiOptions>? change = null)
    {
        var options = new AiOptions { Endpoint = "http://dgx.test/v1/", ApiKey = "chiave-di-prova", ModelId = CannedLlamaServer.ModelId };
        change?.Invoke(options);
        return options;
    }

    public static async Task<MetricsHost> StartAsync(
        string connectionString, HttpMessageHandler server, AiOptions options, bool chat = true, bool formal = true, bool prepare = true)
    {
        var model = new InternalModelClient(options, server);
        var monitor = new ModelRuntimeMonitor(model, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = AgentFactory.Create(options, model, monitor);
        var agent = new ConversationalAgent(factory, model.CreateChatOptions(temperature: 0.2f, phase: AiPhase.Chat));
        var app = await ApiHarness.StartAsync(connectionString, prepare, services =>
        {
            services.AddSingleton(model);
            services.AddSingleton(monitor);
            services.AddSingleton(agent);
            // Limiti mostrati dal monitor del contesto coerenti con quelli del client (l'endpoint resta quello della configurazione di test).
            services.PostConfigure<AiOptions>(o =>
            {
                o.ModelId = options.ModelId;
                o.ContextWindowTokens = options.ContextWindowTokens;
                o.MaxOutputTokens = options.MaxOutputTokens;
                o.SafetyMarginTokens = options.SafetyMarginTokens;
                o.CompactionTriggerTokens = options.CompactionTriggerTokens;
                o.EnableThinking = options.EnableThinking;
                o.Phases = options.Phases;
            });
            if (chat)
            {
                services.AddSingleton<IChatTurnExecutor, ConversationalTurnExecutor>();
            }

            if (formal)
            {
                var document = new DocumentAgent(factory, model.CreateChatOptions(temperature: 0.2f, phase: AiPhase.Document));
                services.AddSingleton(sp => new AnalysisWorkflow(
                    sp.GetRequiredService<IServiceScopeFactory>(), agent,
                    model.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0, phase: AiPhase.Analysis)));
                services.AddSingleton(document);
                services.AddSingleton(sp => new DocumentWorkflow(sp.GetRequiredService<IServiceScopeFactory>(), document));
            }
        });
        return new MetricsHost(app, model, options);
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        _model.Dispose();
    }

    public async Task<(HttpClient Pm, Guid Project)> NewProjectAsync()
    {
        var pm = await App.LoginAsync();
        var created = await pm.PostAsJsonAsync("/api/projects", new { name = $"Metriche {Guid.NewGuid():N}" }, Ct);
        created.EnsureSuccessStatusCode();
        return (pm, (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
    }

    /// <summary>Invia una nota PM e attende lo stato terminale del suo turno.</summary>
    public static async Task<JsonElement> ChatAsync(HttpClient pm, Guid project, string text) =>
        await CompactionHost.WaitTurnAsync(pm, project, await CompactionHost.PostAsync(pm, project, text));

    public static async Task<JsonElement> AssistantAsync(HttpClient pm, Guid project, Guid turnId)
    {
        var page = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/messages?limit=100", Ct);
        return page.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant" && m.GetProperty("chatTurnId").GetGuid() == turnId).Clone();
    }

    public static async Task<JsonElement> ContextUsageAsync(HttpClient pm, Guid project)
    {
        var response = await pm.GetAsync($"/api/projects/{project}/context-usage", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();
    }

    /// <summary>Analizza, conferma la revisione AI e genera il documento: restituisce i due run conclusi.</summary>
    public static async Task<(JsonElement Analysis, JsonElement Document)> AnalyzeAndGenerateAsync(HttpClient pm, Guid project)
    {
        var analysis = await CompactionHost.WaitRunAsync(pm, project, await CompactionHost.AnalyzeAsync(pm, project));
        Assert.True(analysis.GetProperty("status").GetString() == "Completed", CompactionHost.ErrorOf(analysis));
        var analysisId = analysis.GetProperty("id").GetGuid();
        var revision = analysis.GetProperty("resultRevisionId").GetGuid();

        using var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysisId}/confirm");
        confirm.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        (await pm.SendAsync(confirm, Ct)).EnsureSuccessStatusCode();

        using var generate = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysisId}/document-generations")
        {
            Content = JsonContent.Create(new { revisionId = revision }),
        };
        generate.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var accepted = await pm.SendAsync(generate, Ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var document = await CompactionHost.WaitRunAsync(pm, project, (await accepted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
        return (analysis, document);
    }

    public async Task<List<InferenceCall>> CallsAsync(Guid? turnId = null, Guid? runId = null)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.InferenceCalls.AsNoTracking()
            .Where(c => (turnId == null || c.ChatTurnId == turnId) && (runId == null || c.ProcessingRunId == runId))
            .OrderBy(c => c.Attempt).ThenBy(c => c.StartedAt)
            .ToListAsync(Ct);
    }

    /// <summary>Asserisce che un TokenValue JSON abbia valore e provenienza attesi.</summary>
    public static void AssertToken(JsonElement token, int? value, string source)
    {
        Assert.Equal(source, token.GetProperty("source").GetString());
        if (value is null)
        {
            Assert.Equal(JsonValueKind.Null, token.GetProperty("value").ValueKind);
        }
        else
        {
            Assert.Equal(value, token.GetProperty("value").GetInt32());
        }
    }
}
