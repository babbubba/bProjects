using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>Una richiesta di completamento arrivata al server del modello, come l'ha vista la rete (nessun contenuto nei log).</summary>
internal sealed record ModelCall(
    bool Summarizer, IReadOnlyList<(string Role, string Text)> Messages, int? PromptTokens, int Status, bool Stream, bool Structured)
{
    public IEnumerable<string> Texts => Messages.Select(m => m.Text);
}

/// <summary>
/// Registra ogni richiesta di completamento dietro il vero <see cref="InternalModelClient"/>: messaggi effettivamente inviati,
/// esito HTTP e token di prompt misurati dal server (<c>usage.prompt_tokens</c>). Con <paramref name="requestUsage"/> aggiunge alle
/// richieste in streaming <c>stream_options.include_usage</c> (solo prova): non cambia i messaggi né il comportamento del modello.
/// </summary>
internal sealed class ModelRecorder(HttpMessageHandler inner, bool requestUsage) : DelegatingHandler(inner)
{
    public ConcurrentQueue<ModelCall> Calls { get; } = new();

    public IReadOnlyList<ModelCall> Summaries => Calls.Where(c => c.Summarizer).ToList();

    public IReadOnlyList<ModelCall> Main => Calls.Where(c => !c.Summarizer).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var root = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        var stream = root["stream"]?.GetValue<bool>() == true;
        if (stream && requestUsage)
        {
            root["stream_options"] = new JsonObject { ["include_usage"] = true };
            request.Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json");
        }

        var messages = root["messages"]!.AsArray().Select(m => (
            m!["role"]!.GetValue<string>(),
            m["content"] is JsonArray parts
                ? string.Concat(parts.Select(p => p?["text"]?.GetValue<string>()))
                : m["content"]?.GetValue<string>() ?? "")).ToList();

        var response = await base.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var media = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        response.Content = new StringContent(text, Encoding.UTF8, media);

        var usage = Regex.Matches(text, "\"prompt_tokens\"\\s*:\\s*(\\d+)");
        Calls.Enqueue(new ModelCall(
            Summarizer: messages.Any(m => m.Item1 == "system" && m.Item2 == AgentFactory.SummarizationPrompt),
            messages,
            usage.Count > 0 ? int.Parse(usage[^1].Groups[1].Value) : null,
            (int)response.StatusCode,
            stream,
            root["response_format"] is not null));
        return response;
    }
}

/// <summary>
/// Server del modello finto (llama.cpp OpenAI-compatibile) per le prove deterministiche: tokenizer di un token ogni quattro
/// caratteri, finestra massima che rifiuta con 400 come il vero runtime, sintesi che riporta intestazioni e frasi chiave del
/// testo ricevuto, analisi valida costruita dal contesto realmente inviato.
/// </summary>
internal sealed class FakeModelServer : HttpMessageHandler
{
    private static readonly Regex Key = new(@"[^.!?\n]*(escluse?|fuori perimetro|Non so|Aurora)[^.!?\n]*[.!?]?", RegexOptions.IgnoreCase);

    /// <summary>Token massimi accettati da una richiesta (n_ctx del runtime); oltre risponde 400 exceed_context_size.</summary>
    public int ContextLimit { get; set; } = 32768;

    public string? SummaryOverride { get; set; }

    /// <summary>Risposta fissa dei turni chat (storico di preparazione); null = risposta che riporta i fatti se il contesto li contiene.</summary>
    public string? ChatReply { get; set; }

    public static int Tokens(string text) => (text.Length + 3) / 4;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        if (request.RequestUri!.AbsolutePath.EndsWith("/tokenize", StringComparison.Ordinal))
        {
            var n = Tokens(body["content"]!.GetValue<string>());
            return Json($$"""{"tokens":[{{string.Join(',', Enumerable.Repeat("1", n))}}]}""");
        }

        var messages = body["messages"]!.AsArray().Select(m => (Role: m!["role"]!.GetValue<string>(), Text: Text(m["content"]))).ToList();
        var prompt = messages.Sum(m => Tokens(m.Text));
        if (prompt > ContextLimit)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        error = new
                        {
                            code = 400,
                            message = $"request ({prompt} tokens) exceeds the available context size ({ContextLimit} tokens)",
                            type = "exceed_context_size_error",
                            n_prompt_tokens = prompt,
                            n_ctx = ContextLimit,
                        },
                    }),
                    Encoding.UTF8, "application/json"),
            };
        }

        var user = string.Join("\n", messages.Where(m => m.Role is not ("system" or "developer")).Select(m => m.Text));
        var summarizer = messages.Any(m => m.Role == "system" && m.Text == AgentFactory.SummarizationPrompt);
        var reply =
            summarizer ? SummaryOverride ?? Summary(user)
            : body["response_format"] is not null ? WorkflowRecoveryTests.AnalysisReply(user)
            : ChatReply ?? (Regex.IsMatch(user, "SMS") ? "Esclusi dal primo rilascio: SMS e fatturazione. Il portale si chiama Aurora." : "Risposta.");

        return body["stream"]?.GetValue<bool>() == true ? Stream(reply, prompt) : Json(Completion(reply, prompt));
    }

    private static string Text(JsonNode? content) => content is JsonArray parts
        ? string.Concat(parts.Select(p => p?["text"]?.GetValue<string>()))
        : content?.GetValue<string>() ?? "";

    /// <summary>Sintesi fedele del finto: intestazioni di fonte e frasi con negazioni/esclusioni/nomi, in ordine.</summary>
    private static string Summary(string user)
    {
        var lines = new List<string>();
        foreach (var header in Regex.Matches(user, @"\[messaggio id=[0-9a-f-]{36} sequenza=\d+\]").Select(m => m.Value))
        {
            lines.Add(header);
        }

        lines.AddRange(Key.Matches(user).Select(m => m.Value.Trim()).Distinct());
        return string.Join('\n', lines);
    }

    private static string Completion(string text, int prompt) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-test",
        @object = "chat.completion",
        created = 1,
        model = "fake-model",
        choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } },
        usage = new { prompt_tokens = prompt, completion_tokens = Tokens(text), total_tokens = prompt + Tokens(text) },
    });

    private static HttpResponseMessage Stream(string text, int prompt)
    {
        string Chunk(object delta, string? finish) => "data: " + JsonSerializer.Serialize(new
        {
            id = "chatcmpl-test",
            @object = "chat.completion.chunk",
            created = 1,
            model = "fake-model",
            choices = new[] { new { index = 0, delta, finish_reason = finish } },
        }) + "\n\n";

        var usage = "data: " + JsonSerializer.Serialize(new
        {
            id = "chatcmpl-test",
            @object = "chat.completion.chunk",
            created = 1,
            model = "fake-model",
            choices = Array.Empty<object>(),
            usage = new { prompt_tokens = prompt, completion_tokens = Tokens(text), total_tokens = prompt + Tokens(text) },
        }) + "\n\n";
        var sse = Chunk(new { role = "assistant", content = text }, null) + Chunk(new { }, "stop") + usage + "data: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>Testi di prova ripetibili e distinti: un nucleo di fatti (negazioni, esclusioni, nome) più note di riempimento.</summary>
internal static class CompactionDialogs
{
    public const string KeyFacts =
        "Il portale clienti si chiama Aurora. Le notifiche via SMS sono escluse dal primo rilascio. "
        + "La fatturazione resta fuori perimetro. Non so ancora quanti clienti useranno il portale.";

    public const string Question =
        "\nDomanda finale, senza fare altre domande: elenca in una frase cosa e' escluso dal primo rilascio e come si chiama il portale.";

    private static readonly string[] Screens = ["elenco ticket", "dettaglio ticket", "profilo cliente", "allegati", "cronologia", "ricerca", "report", "impostazioni", "notifiche", "accessi"];
    private static readonly string[] Fields = ["priorita", "stato", "assegnatario", "data di apertura", "categoria", "canale", "sede", "referente"];
    private static readonly string[] Orders = ["per data", "per priorita", "per cliente", "per stato", "alfabetico"];
    private static readonly string[] Filters = ["stato aperto", "assegnatario", "periodo", "categoria", "sede"];
    private static readonly string[] Depts = ["assistenza", "vendite", "amministrazione", "qualita", "logistica"];
    private static readonly string[] Checks = ["i campi obbligatori", "la coerenza dei dati", "i permessi di accesso", "l'esportazione", "le scadenze"];

    /// <summary>Messaggio PM di <paramref name="chars"/> caratteri; il primo contiene i fatti da preservare.</summary>
    public static string Message(int index, int chars, bool question = false)
    {
        var text = new StringBuilder(index == 0 ? KeyFacts + " " : "");
        for (var i = 0; text.Length < chars; i++)
        {
            text.Append($"Nota {index}.{i}: la schermata \"{Screens[(index * 7 + i) % Screens.Length]}\" mostra {Fields[(index + i * 3) % Fields.Length]} "
                + $"con ordinamento {Orders[(index * 5 + i) % Orders.Length]} e filtro per {Filters[(index + i * 2) % Filters.Length]}; "
                + $"il reparto {Depts[(index * 3 + i) % Depts.Length]} verifica {Checks[(index + i) % Checks.Length]} entro il {1 + (index * 3 + i) % 28} del mese. ");
        }

        var result = text.ToString(0, Math.Min(chars, text.Length));
        return question ? result + Question : result;
    }
}

/// <summary>
/// Applicazione ospitata su PostgreSQL con il modello (reale o finto) dietro il vero <see cref="InternalModelClient"/> e
/// <see cref="ModelRecorder"/>, il vero <see cref="AgentFactory"/> (compattazione nativa), ConversationalAgent e, a richiesta,
/// esecutore chat e workflow di Analysis nativo: gli stessi cablaggi di Program.cs.
/// </summary>
internal sealed class CompactionHost : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(240);

    private readonly ApiHarness _app;
    private readonly InternalModelClient _model;

    private CompactionHost(ApiHarness app, InternalModelClient model, ModelRecorder recorder, AiOptions options)
    {
        _app = app;
        _model = model;
        Recorder = recorder;
        Options = options;
    }

    public ModelRecorder Recorder { get; }

    public AiOptions Options { get; }

    public InternalModelClient Model => _model;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<CompactionHost> StartAsync(
        string connectionString, AiOptions options, HttpMessageHandler server, bool chat, bool analysis, bool prepare, bool requestUsage = false)
    {
        var recorder = new ModelRecorder(server, requestUsage);
        var model = new InternalModelClient(options, recorder);
        var agent = new ConversationalAgent(AgentFactory.Create(options, model), model.CreateChatOptions(temperature: 0.2f));
        var app = await ApiHarness.StartAsync(connectionString, prepare, services =>
        {
            services.AddSingleton(agent);
            if (chat)
            {
                services.AddSingleton<IChatTurnExecutor, ConversationalTurnExecutor>();
            }

            if (analysis)
            {
                services.AddSingleton(sp => new AnalysisWorkflow(
                    sp.GetRequiredService<IServiceScopeFactory>(), agent, model.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0)));
            }
        });
        return new CompactionHost(app, model, recorder, options);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _model.Dispose();
    }

    public async Task<(HttpClient Pm, Guid Project)> NewProjectAsync()
    {
        var pm = await _app.LoginAsync();
        var created = await pm.PostAsJsonAsync("/api/projects", new { name = $"Compattazione {Guid.NewGuid():N}" }, Ct);
        created.EnsureSuccessStatusCode();
        return (pm, (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
    }

    public async Task<HttpClient> LoginAsync() => await _app.LoginAsync();

    public static async Task<Guid> PostAsync(HttpClient pm, Guid project, string text)
    {
        var response = await pm.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("chatTurnId").GetGuid();
    }

    public static async Task<Guid> AnalyzeAsync(HttpClient pm, Guid project)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var accepted = await pm.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        return (await accepted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    /// <summary>Stato terminale del turno o del run (Completed/Failed) letto dalle API pubbliche.</summary>
    public static async Task<JsonElement> WaitTerminalAsync(HttpClient pm, string url)
    {
        var deadline = DateTime.UtcNow + Patience;
        JsonElement item = default;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300, Ct);
            item = (await pm.GetFromJsonAsync<JsonElement>(url, Ct)).Clone();
            if (item.GetProperty("status").GetString() is "Completed" or "Failed")
            {
                break;
            }
        }

        return item;
    }

    public static Task<JsonElement> WaitTurnAsync(HttpClient pm, Guid project, Guid turn) =>
        WaitTerminalAsync(pm, $"/api/projects/{project}/chat-turns/{turn}");

    public static Task<JsonElement> WaitRunAsync(HttpClient pm, Guid project, Guid run) =>
        WaitTerminalAsync(pm, $"/api/projects/{project}/processing-runs/{run}");

    public static string ErrorOf(JsonElement terminal) =>
        terminal.GetProperty("error").ValueKind == JsonValueKind.Null
            ? "nessun errore"
            : $"{terminal.GetProperty("error").GetProperty("code").GetString()}: {terminal.GetProperty("error").GetProperty("message").GetString()}";

    public static async Task<List<(Guid Id, long Sequence, string Role, string Body)>> OriginalsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""SELECT "Id", "Sequence", "Role", "Body" FROM "ConversationMessages" ORDER BY "Sequence" """, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var list = new List<(Guid, long, string, string)>();
        while (await reader.ReadAsync(Ct))
        {
            list.Add((reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
        }

        return list;
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Token del contesto realmente inviato, riconteggiati con il tokenizer del modello (testi di tutti i messaggi, istruzioni incluse).</summary>
    public async Task<int> CountSentAsync(ModelCall call)
    {
        var total = 0;
        foreach (var text in call.Texts.Where(t => t.Length > 0))
        {
            total += await _model.CountTokensAsync(text, Ct);
        }

        return total;
    }
}
