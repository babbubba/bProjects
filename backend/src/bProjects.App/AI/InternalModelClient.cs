using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace bProjects.App.AI;

/// <summary>
/// Modello esposto dall'endpoint interno (GET /v1/models). In router mode <c>Status</c> è <c>status.value</c> (loaded/unloaded/...)
/// e gli argomenti di avvio danno <c>--ctx-size</c> e, solo se presente, <c>--n-predict</c>; altrove sono null.
/// </summary>
public sealed record ModelInfo(string Id, string? Status, int? ConfiguredContextTokens, int? ConfiguredNPredict = null);

/// <summary>GET /props: <c>default_generation_settings.n_ctx</c> (per slot), <c>params.n_predict</c> e <c>total_slots</c>; un campo assente è null.</summary>
public sealed record ServerProps(int? ContextTokensPerSlot, int? DefaultNPredict, int? TotalSlots);

/// <summary>Slot di GET /slots: <c>n_ctx</c> se presente.</summary>
public sealed record ServerSlot(int? ContextTokens);

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
    /// Opzioni di richiesta coerenti con la configurazione della fase (<see cref="AiOptions.ForPhase"/>): output massimo,
    /// ragionamento nascosto (spento di default) e, se è fornito uno schema e la modalità è JsonSchema,
    /// <c>response_format json_schema</c> nativo. In ValidatedTextJson non si invia alcun vincolo: la validazione tipizzata è del backend.
    /// Lo schema viaggia così com'è nella rappresentazione grezza: l'adattatore OpenAI di M.E.AI (10.10.1) lo riscriverebbe per lo
    /// strict mode spostando <c>pattern</c> e <c>format</c> nella <c>description</c>, e llama.cpp costruirebbe la grammatica senza
    /// vincolo sugli UUID (osservato sul DGX: id come «src-1» e invalid_agent_output su ogni analisi del progetto a 9 scambi).
    /// </summary>
    public ChatOptions CreateChatOptions(
        JsonElement? schema = null, string schemaName = "result", float? temperature = null, AiPhase phase = AiPhase.Chat)
    {
        var settings = _options.ForPhase(phase);
        var structured = schema is not null && _options.StructuredOutputMode == StructuredOutputMode.JsonSchema;
        var schemaJson = structured ? schema!.Value.GetRawText() : null;
        var options = new ChatOptions
        {
            ModelId = _options.ModelId,
            MaxOutputTokens = settings.MaxOutputTokens,
            Temperature = temperature,
            RawRepresentationFactory = _ => CreateRawOptions(settings.EnableThinking, schemaName, schemaJson),
        };

        if (structured)
        {
            // Resta valorizzato per MAF e per la telemetria; il response_format inviato è quello grezzo, che l'adattatore non sovrascrive.
            options.ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema(schema!.Value, schemaName);
        }

        return options;
    }

    /// <summary>
    /// Le stesse opzioni con un altro schema (es. lo schema del singolo run), sia in <c>ResponseFormat</c> sia nel response_format
    /// grezzo realmente inviato. Senza output strutturato (ValidatedTextJson) le opzioni restano invariate: nessun vincolo.
    /// </summary>
    public static ChatOptions WithJsonSchema(ChatOptions options, JsonElement schema, string schemaName)
    {
        if (options.ResponseFormat is not ChatResponseFormatJson)
        {
            return options;
        }

        var json = schema.GetRawText();
        var inner = options.RawRepresentationFactory;
        var result = options.Clone();
        result.ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema(schema, schemaName);
        result.RawRepresentationFactory = client =>
        {
            var raw = inner?.Invoke(client) as ChatCompletionOptions ?? new ChatCompletionOptions();
            raw.ResponseFormat = OpenAI.Chat.ChatResponseFormat.CreateJsonSchemaFormat(schemaName, BinaryData.FromString(json));
            return raw;
        };
        return result;
    }

    /// <summary>Modelli esposti dal server con la finestra dichiarata nelle loro impostazioni.</summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(new Uri(_serverRoot, "v1/models"), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        var models = new List<ModelInfo>();
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? status = null;
            int? ctx = null;
            int? nPredict = null;
            if (item.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                status = st.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (st.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                {
                    var list = args.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() : null).ToList();
                    ctx = ArgValue(list, "--ctx-size", "-c");
                    nPredict = ArgValue(list, "--n-predict", "--predict", "-n");
                }
            }

            models.Add(new ModelInfo(id.GetString()!, status, ctx, nPredict));
        }

        return models;
    }

    private static int? ArgValue(List<string?> args, params string[] names)
    {
        var i = args.FindIndex(a => a is not null && names.Contains(a));
        return i >= 0 && i + 1 < args.Count && int.TryParse(args[i + 1], out var parsed) ? parsed : null;
    }

    /// <summary>
    /// GET /props?model=: finestra per slot e n_predict di default come riportati dal server. Ogni campo assente o non numerico è
    /// null: nulla è dedotto. Un errore HTTP lancia <see cref="HttpRequestException"/> (con lo StatusCode).
    /// </summary>
    public async Task<ServerProps> GetServerPropsAsync(CancellationToken cancellationToken = default)
    {
        var uri = new Uri(_serverRoot, $"props?model={Uri.EscapeDataString(_options.ModelId)}");
        using var response = await _http.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        var root = json.RootElement;
        var totalSlots = Int(root, "total_slots");
        if (!root.TryGetProperty("default_generation_settings", out var defaults) || defaults.ValueKind != JsonValueKind.Object)
        {
            return new ServerProps(null, null, totalSlots);
        }

        var nPredict = defaults.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? Int(p, "n_predict") : null;
        return new ServerProps(Int(defaults, "n_ctx"), nPredict ?? Int(defaults, "n_predict"), totalSlots);
    }

    /// <summary>GET /slots?model=: <c>n_ctx</c> di ogni slot. Disabilitato/non supportato (404/501...) lancia <see cref="HttpRequestException"/>: il chiamante lo tollera.</summary>
    public async Task<IReadOnlyList<ServerSlot>> GetSlotsAsync(CancellationToken cancellationToken = default)
    {
        var uri = new Uri(_serverRoot, $"slots?model={Uri.EscapeDataString(_options.ModelId)}");
        using var response = await _http.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        return json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object).Select(s => new ServerSlot(Int(s, "n_ctx"))).ToList()
            : [];
    }

    private static int? Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

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
    private static ChatCompletionOptions CreateRawOptions(bool enableThinking, string schemaName, string? schemaJson)
    {
        var raw = new ChatCompletionOptions();
        raw.Patch.Set("$.chat_template_kwargs"u8, BinaryData.FromString($"{{\"enable_thinking\":{(enableThinking ? "true" : "false")}}}"));
        if (schemaJson is not null)
        {
            raw.ResponseFormat = OpenAI.Chat.ChatResponseFormat.CreateJsonSchemaFormat(schemaName, BinaryData.FromString(schemaJson));
        }

        return raw;
    }
#pragma warning restore SCME0001
}
