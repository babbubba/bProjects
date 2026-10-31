using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>L'usage di una risposta del modello come è arrivato sulla rete (solo numeri).</summary>
internal sealed record ReceivedUsage(bool Summarizer, bool Stream, bool IncludeUsage, int? Prompt, int? Completion, int? Total, int? Cached, string? FinishReason);

/// <summary>
/// Registra, dietro il vero <see cref="InternalModelClient"/>, l'usage restituito dal runtime per ogni completamento (in streaming
/// dal chunk con <c>usage</c>): il termine di confronto indipendente per riga salvata e JSON dell'API. Nessun contenuto registrato.
/// </summary>
internal sealed class UsageRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public ConcurrentQueue<ReceivedUsage> Calls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        var summarizer = body["messages"]!.AsArray().Any(m => m!["role"]!.GetValue<string>() == "system"
            && (m["content"] as JsonValue)?.GetValue<string>().StartsWith(AgentFactory.SummarizationPrompt, StringComparison.Ordinal) == true);

        var response = await base.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var media = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        response.Content = new StringContent(text, Encoding.UTF8, media);

        JsonElement? usage = null;
        string? finish = null;
        var documents = media == "text/event-stream"
            ? text.Split('\n').Where(l => l.StartsWith("data: {", StringComparison.Ordinal)).Select(l => l["data: ".Length..])
            : [text];
        foreach (var json in documents)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                usage = u.Clone();
            }

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String)
                    {
                        finish = f.GetString();
                    }
                }
            }
        }

        int? Read(string name) => usage is { } x && x.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        int? cached = usage is { } y && y.TryGetProperty("prompt_tokens_details", out var details) && details.TryGetProperty("cached_tokens", out var c) ? c.GetInt32() : null;
        Calls.Enqueue(new ReceivedUsage(
            summarizer, body["stream"]?.GetValue<bool>() == true, body["stream_options"]?["include_usage"]?.GetValue<bool>() == true,
            Read("prompt_tokens"), Read("completion_tokens"), Read("total_tokens"), cached, finish));
        return response;
    }
}

/// <summary>
/// Metriche di inferenza contro il DGX Spark reale (llama.cpp) e PostgreSQL reale: per la stessa richiesta confronta l'usage ricevuto
/// sulla rete, la riga salvata in InferenceCalls e il JSON delle API (messaggio e monitor del contesto). Registra solo numeri.
/// Servono BPROJECTS_TEST_POSTGRES e BPROJECTS_TEST_DGX_ENDPOINT/API_KEY/MODEL; senza, Skipped.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
public sealed class InferenceMetricsMafTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task Real_streaming_chat_usage_matches_the_saved_row_and_the_api()
    {
        var options = TestEnvironment.RequireDgxOptions();
        TestEnvironment.RequirePostgres();
        var recorder = new UsageRecorder(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        await using var host = await MetricsHost.StartAsync(_database.ConnectionString, recorder, options, formal: false);
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        var props = await host.Model.GetServerPropsAsync(TestContext.Current.CancellationToken);

        var turn = await MetricsHost.ChatAsync(pm, project, "Il portale clienti si chiama Aurora e serve agli agenti per le pratiche. Rispondi con una sola frase breve.");
        Assert.True(turn.GetProperty("status").GetString() == "Completed", CompactionHost.ErrorOf(turn));

        var received = Assert.Single(recorder.Calls);
        Assert.True(received.Stream && received.IncludeUsage);
        Assert.NotNull(received.Prompt); // llama.cpp restituisce l'usage nell'ultimo chunk quando richiesto
        var row = Assert.Single(await host.CallsAsync(turnId: turn.GetProperty("id").GetGuid()));
        Assert.Equal((received.Prompt, received.Completion, received.Total, received.Cached, received.FinishReason),
            (row.PromptTokens, row.CompletionTokens, row.TotalTokens, row.CachedTokens, row.FinishReason));
        Assert.Equal(props.ContextTokensPerSlot, row.ContextWindowTokens);
        Assert.Equal(options.ModelId, row.ModelId);

        var metrics = (await MetricsHost.AssistantAsync(pm, project, row.ChatTurnId!.Value)).GetProperty("metrics");
        MetricsHost.AssertToken(metrics.GetProperty("promptTokens"), received.Prompt, "measured");
        MetricsHost.AssertToken(metrics.GetProperty("completionTokens"), received.Completion, "measured");
        MetricsHost.AssertToken(metrics.GetProperty("totalTokens"), received.Total, "measured");
        MetricsHost.AssertToken(metrics.GetProperty("cachedTokens"), received.Cached, "measured");
        MetricsHost.AssertToken(metrics.GetProperty("contextWindowTokens"), props.ContextTokensPerSlot, "measured");

        var usage = await MetricsHost.ContextUsageAsync(pm, project);
        var composition = usage.GetProperty("composition");
        Assert.Equal(received.Prompt, composition.GetProperty("promptTokens").GetProperty("value").GetInt32());
        var unattributed = composition.GetProperty("unattributedTokens").GetProperty("value").GetInt32();
        Assert.Equal(received.Prompt - composition.GetProperty("attributedTokens").GetInt32(), unattributed);
        Assert.True(unattributed > 0, $"quota non attribuita {unattributed}: il template del runtime aggiunge token");

        Report($"DGX chat streaming: prompt={received.Prompt} completion={received.Completion} total={received.Total} cached={received.Cached} "
            + $"finish={received.FinishReason} ctx={row.ContextWindowTokens} durataMs={row.DurationMs} reasoning={metrics.GetProperty("reasoning").GetRawText()}");
        Report("metrics API: " + metrics.GetRawText());
        Report("composition: " + string.Join(", ", composition.GetProperty("items").EnumerateArray().Select(i =>
            $"{i.GetProperty("key").GetString()}={i.GetProperty("tokens").GetProperty("value")}")) + $", attribuiti={composition.GetProperty("attributedTokens")}, non attribuiti={unattributed}");
        Report($"last: usedPercent={usage.GetProperty("last").GetProperty("usedPercent")} remaining={usage.GetProperty("last").GetProperty("remainingTokens").GetRawText()} runtime={usage.GetProperty("runtime").GetRawText()}");
    }

    [Fact]
    public async Task Real_compaction_summary_usage_is_recorded_and_aggregated()
    {
        var services = TestEnvironment.RequireDgxOptions();
        TestEnvironment.RequirePostgres();
        var options = new AiOptions { Endpoint = services.Endpoint, ApiKey = services.ApiKey, ModelId = services.ModelId, CompactionTriggerTokens = 3000 };
        var recorder = new UsageRecorder(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        await using var host = await MetricsHost.StartAsync(_database.ConnectionString, recorder, options, formal: false);
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;

        var first = await MetricsHost.ChatAsync(pm, project, CompactionDialogs.Message(0, 8000));
        Assert.True(first.GetProperty("status").GetString() == "Completed", CompactionHost.ErrorOf(first));
        var turn = await MetricsHost.ChatAsync(pm, project, CompactionDialogs.Message(1, 8000, question: true));
        Assert.True(turn.GetProperty("status").GetString() == "Completed", CompactionHost.ErrorOf(turn));

        var summary = Assert.Single(recorder.Calls, c => c.Summarizer);
        var rows = await host.CallsAsync(turnId: turn.GetProperty("id").GetGuid());
        var summaryRow = Assert.Single(rows, r => r.Phase == AiPhase.Compaction);
        Assert.Equal((summary.Prompt, summary.Completion), (summaryRow.PromptTokens, summaryRow.CompletionTokens));
        var final = Assert.Single(rows, r => r.IsFinal);
        var main = recorder.Calls.Last(c => !c.Summarizer);
        Assert.Equal(main.Prompt, final.PromptTokens);

        var metrics = (await MetricsHost.AssistantAsync(pm, project, final.ChatTurnId!.Value)).GetProperty("metrics");
        Assert.Equal(2, metrics.GetProperty("calls").GetInt32());
        MetricsHost.AssertToken(metrics.GetProperty("compaction").GetProperty("summaryPromptTokens"), summary.Prompt, "measured");
        var usage = await MetricsHost.ContextUsageAsync(pm, project);
        var keys = usage.GetProperty("composition").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.Contains("derived_summary", keys);

        Report($"DGX compattazione: sintesi prompt={summary.Prompt} completion={summary.Completion}; finale prompt={main.Prompt} completion={main.Completion} cached={main.Cached}");
        Report("compaction: " + usage.GetProperty("compaction").GetRawText());
        Report("composition: " + usage.GetProperty("composition").GetRawText());
        Report("history: " + usage.GetProperty("history").GetRawText());
    }

    /// <summary>Solo numeri e durate (mai contenuti): nel log diagnostico e nell'output del test.</summary>
    private static void Report(string line)
    {
        TestContext.Current.SendDiagnosticMessage(line);
        TestContext.Current.TestOutputHelper?.WriteLine(line);
    }
}
