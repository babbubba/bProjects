using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace bProjects.App.AI;

/// <summary>Modello esposto dall'endpoint interno (GET /v1/models).</summary>
public sealed record ModelInfo(string Id, string? Status, int? ConfiguredContextTokens);

/// <summary>
/// Client del solo modello interno (DGX Spark, llama.cpp OpenAI-compatibile). Costruisce un unico
/// <see cref="IChatClient"/> MAF sull'endpoint Chat Completions configurato: nessun altro provider,
/// nessun fallback, nessun retry nel client HTTP/SDK (i tentativi sono del dispatcher, contracts §2).
/// </summary>
public sealed class InternalModelClient : IDisposable
{
    private readonly AiOptions _options;
    private readonly HttpClient _http;
    private readonly Uri _serverRoot;

    public InternalModelClient(AiOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        var endpoint = new Uri(options.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        _serverRoot = new Uri(endpoint, "/");

        // Niente redirect né proxy di sistema: la chiave viaggia solo verso l'host configurato e validato.
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            Transport = new HttpClientPipelineTransport(_http),
        };
        ChatClient = new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions)
            .GetChatClient(options.ModelId)
            .AsIChatClient();
    }

    /// <summary>Client MAF/M.E.AI sul modello configurato; base per ChatClientAgent e compattazione.</summary>
    public IChatClient ChatClient { get; }

    /// <summary>
    /// Opzioni di richiesta coerenti con la configurazione: output massimo, ragionamento nascosto spento e,
    /// se è fornito uno schema e la modalità è JsonSchema, <c>response_format json_schema</c> nativo.
    /// In ValidatedTextJson non si invia alcun vincolo: la validazione tipizzata è del backend.
    /// </summary>
    public ChatOptions CreateChatOptions(JsonElement? schema = null, string schemaName = "result", float? temperature = null)
    {
        var options = new ChatOptions
        {
            ModelId = _options.ModelId,
            MaxOutputTokens = _options.MaxOutputTokens,
            Temperature = temperature,
            RawRepresentationFactory = _ => CreateRawOptions(_options.EnableThinking),
        };

        if (schema is { } s && _options.StructuredOutputMode == StructuredOutputMode.JsonSchema)
        {
            options.ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema(s, schemaName);
        }

        return options;
    }

    /// <summary>Modelli esposti dal server con la finestra dichiarata nelle loro impostazioni.</summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(new Uri(_serverRoot, "v1/models"), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        var models = new List<ModelInfo>();
        foreach (var item in json.RootElement.GetProperty("data").EnumerateArray())
        {
            string? status = null;
            int? ctx = null;
            if (item.TryGetProperty("status", out var st))
            {
                status = st.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (st.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                {
                    var list = args.EnumerateArray().Select(a => a.GetString()).ToList();
                    var i = list.IndexOf("--ctx-size");
                    if (i >= 0 && i + 1 < list.Count && int.TryParse(list[i + 1], out var parsed))
                    {
                        ctx = parsed;
                    }
                }
            }

            models.Add(new ModelInfo(item.GetProperty("id").GetString()!, status, ctx));
        }

        return models;
    }

    /// <summary>Finestra effettivamente allocata dal runtime per il modello configurato (GET /props, n_ctx).</summary>
    public async Task<int> GetRuntimeContextTokensAsync(CancellationToken cancellationToken = default)
    {
        var uri = new Uri(_serverRoot, $"props?model={Uri.EscapeDataString(_options.ModelId)}");
        using var response = await _http.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("default_generation_settings").GetProperty("n_ctx").GetInt32();
    }

    /// <summary>Conteggio esatto con il tokenizer del modello (POST /tokenize di llama.cpp).</summary>
    public async Task<int> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new { model = _options.ModelId, content = text });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(new Uri(_serverRoot, "tokenize"), content, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("tokens").GetArrayLength();
    }

    public void Dispose() => _http.Dispose();

#pragma warning disable SCME0001 // Patch del payload: unico modo per passare chat_template_kwargs al server.
    private static ChatCompletionOptions CreateRawOptions(bool enableThinking)
    {
        var raw = new ChatCompletionOptions();
        raw.Patch.Set("$.chat_template_kwargs"u8, BinaryData.FromString($"{{\"enable_thinking\":{(enableThinking ? "true" : "false")}}}"));
        return raw;
    }
#pragma warning restore SCME0001
}
