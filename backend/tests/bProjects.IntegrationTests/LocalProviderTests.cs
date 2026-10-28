using System.Net;
using System.Text.Json;
using bProjects.App.AI;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

/// <summary>Comportamento del client senza rete esterna: retry SDK disattivato, classificazione errori, un solo host.</summary>
public sealed class InternalModelClientOfflineTests
{
    private static AiOptions Options() => new() { Endpoint = "http://dgx.internal:8080/v1/", ModelId = "m", ApiKey = "test-key" };

    [Fact]
    public async Task Transient_failure_is_sent_once_and_classified_with_retry_after()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Headers = { RetryAfter = new(TimeSpan.FromSeconds(7)) },
            Content = new StringContent("{}"),
        });
        using var client = new InternalModelClient(Options(), handler);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.ChatClient.GetResponseAsync("ciao", client.CreateChatOptions(), TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests); // nessun retry nel client/SDK: i tentativi sono del dispatcher
        var failure = ModelFailure.Classify(ex);
        Assert.True(failure.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(7), failure.RetryAfter);
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    public async Task Http_status_is_classified_transient_or_permanent(int status, bool transient)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") });
        using var client = new InternalModelClient(Options(), handler);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.ChatClient.GetResponseAsync("ciao", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(transient, ModelFailure.Classify(ex).IsTransient);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Transport_errors_and_timeouts_are_transient_and_unknown_errors_permanent()
    {
        Assert.True(ModelFailure.Classify(new HttpRequestException("down")).IsTransient);
        Assert.True(ModelFailure.Classify(new TaskCanceledException("timeout")).IsTransient);
        Assert.False(ModelFailure.Classify(new InvalidOperationException("bug")).IsTransient);
    }

    [Fact]
    public async Task Requests_target_only_the_configured_endpoint_with_bearer_auth_and_no_thinking()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""", System.Text.Encoding.UTF8, "application/json"),
        });
        using var client = new InternalModelClient(Options(), handler);

        await client.ChatClient.GetResponseAsync("ciao", client.CreateChatOptions(), TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("dgx.internal", request.Uri.Host);
        Assert.Equal("/v1/chat/completions", request.Uri.AbsolutePath);
        Assert.Equal("Bearer test-key", request.Authorization);
        Assert.Contains("\"enable_thinking\":false", request.Body);
    }

    private sealed record Seen(Uri Uri, string? Authorization, string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            return respond(request);
        }
    }
}

/// <summary>Prove contro il server llama.cpp reale su DGX Spark (Category=MafReal, Skipped senza configurazione).</summary>
[Trait("Category", TestCategories.MafReal)]
public sealed class LocalProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Discovery_lists_the_configured_model_with_its_declared_window()
    {
        using var client = new InternalModelClient(TestEnvironment.RequireDgxOptions());

        var models = await client.ListModelsAsync(Ct);

        var model = Assert.Single(models, m => m.Id == TestEnvironment.DgxModel);
        Assert.True(model.ConfiguredContextTokens > 0);
    }

    [Fact]
    public async Task Wrong_key_is_rejected_as_a_permanent_failure()
    {
        var options = TestEnvironment.RequireDgxOptions();
        options.ApiKey = "chiave-errata";
        using var client = new InternalModelClient(options);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.ChatClient.GetResponseAsync("ciao", client.CreateChatOptions(), Ct));

        var failure = ModelFailure.Classify(ex);
        Assert.False(failure.IsTransient);
        Assert.Equal("model_unauthorized", failure.Code);
    }

    [Fact]
    public async Task Chat_completion_returns_text_and_token_usage()
    {
        using var client = new InternalModelClient(TestEnvironment.RequireDgxOptions());

        var response = await client.ChatClient.GetResponseAsync("Rispondi solo con la parola: pronto", client.CreateChatOptions(temperature: 0), Ct);

        Assert.Contains("pronto", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Usage?.InputTokenCount > 0);
        Assert.True(response.Usage?.OutputTokenCount > 0);
        Assert.Equal(TestEnvironment.DgxModel, response.ModelId);
    }

    [Fact]
    public async Task Streaming_delivers_several_incremental_updates()
    {
        using var client = new InternalModelClient(TestEnvironment.RequireDgxOptions());
        var updates = new List<(TimeSpan At, string Text)>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await foreach (var update in client.ChatClient.GetStreamingResponseAsync("Conta da 1 a 10 separando con virgole.", client.CreateChatOptions(temperature: 0), Ct))
        {
            if (update.Text is { Length: > 0 } text)
            {
                updates.Add((clock.Elapsed, text));
            }
        }

        Assert.True(updates.Count > 3, $"attesi più aggiornamenti incrementali, ricevuti {updates.Count}");
        Assert.True(updates[^1].At > updates[0].At, "l'ultimo aggiornamento deve arrivare dopo il primo (streaming, non risposta unica)");
        Assert.Contains("10", string.Concat(updates.Select(u => u.Text)));
    }

    [Fact]
    public async Task Runtime_window_matches_the_configured_budget_and_tokenizer_counts_tokens()
    {
        var options = TestEnvironment.RequireDgxOptions();
        using var client = new InternalModelClient(options);

        var window = await client.GetRuntimeContextTokensAsync(Ct);
        var tokens = await client.CountTokensAsync("Hello bProjects, analisi requisiti", Ct);

        Assert.Equal(options.ContextWindowTokens, window);
        Assert.True(options.InputBudgetTokens is > 0 && options.InputBudgetTokens < window);
        Assert.InRange(tokens, 3, 20);
    }

    private const string ItemSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["goals", "openQuestions"],
          "properties": {
            "goals": { "$ref": "#/$defs/items" },
            "openQuestions": { "$ref": "#/$defs/items" }
          },
          "$defs": {
            "items": { "type": "array", "items": { "$ref": "#/$defs/item" } },
            "item": {
              "type": "object",
              "additionalProperties": false,
              "required": ["id", "text", "classification"],
              "properties": {
                "id": { "type": "string", "format": "uuid" },
                "text": { "type": "string", "minLength": 1 },
                "classification": { "enum": ["Declared", "Inferred", "Assumed"] }
              }
            }
          }
        }
        """;

    private const string Prompt =
        "Dal testo estrai gli obiettivi e le domande aperte. Ogni elemento ha un id UUID nuovo, il testo e la classificazione. " +
        "Testo: 'Vogliamo un portale per gestire le richieste dei clienti. Non è chiaro se serva integrare il gestionale.'";

    [Theory]
    [InlineData(StructuredOutputMode.JsonSchema)]
    [InlineData(StructuredOutputMode.ValidatedTextJson)]
    public async Task Structured_output_passes_the_same_typed_validation_in_both_modes(StructuredOutputMode mode)
    {
        var options = TestEnvironment.RequireDgxOptions();
        options.StructuredOutputMode = mode;
        using var client = new InternalModelClient(options);
        using var schema = JsonDocument.Parse(ItemSchema);

        var prompt = mode == StructuredOutputMode.ValidatedTextJson
            ? Prompt + " Rispondi solo con JSON conforme a questo schema: " + ItemSchema
            : Prompt;
        var response = await client.ChatClient.GetResponseAsync(prompt, client.CreateChatOptions(schema.RootElement.Clone(), "extraction", temperature: 0), Ct);

        var result = ExtractionValidator.Parse(response.Text);
        Assert.NotEmpty(result.Goals);
    }

    [Fact]
    public void Validation_rejects_wrong_shape_regardless_of_mode()
    {
        Assert.Throws<JsonException>(() => ExtractionValidator.Parse("""{"goals":[{"id":"x","text":"a","classification":"Declared"}],"openQuestions":[]}"""));
        Assert.Throws<JsonException>(() => ExtractionValidator.Parse("""{"goals":[],"openQuestions":[],"extra":1}"""));
        Assert.Throws<JsonException>(() => ExtractionValidator.Parse("non json"));
    }

    private sealed record Item(Guid Id, string Text, string Classification);

    private sealed record Extraction(List<Item> Goals, List<Item> OpenQuestions);

    /// <summary>Validazione tipizzata minima (stessa per JsonSchema e ValidatedTextJson).</summary>
    private static class ExtractionValidator
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        };

        public static Extraction Parse(string text)
        {
            var result = JsonSerializer.Deserialize<Extraction>(text, Json) ?? throw new JsonException("vuoto");
            foreach (var item in result.Goals.Concat(result.OpenQuestions))
            {
                if (string.IsNullOrWhiteSpace(item.Text) || item.Classification is not ("Declared" or "Inferred" or "Assumed"))
                {
                    throw new JsonException("elemento non valido");
                }
            }

            return result;
        }
    }
}
