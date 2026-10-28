using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static bProjects.IntegrationTests.ConversationalAgentTests;

namespace bProjects.IntegrationTests;

/// <summary>
/// T021 - GET /projects/{p}/events (SSE) su PostgreSQL reale con modello fake: autorizzazione, intestazioni, sequenza e
/// forma degli eventi di un turno completo, delta solo da owner/fence validi, nessun thinking, isolamento per progetto
/// e disconnessione che non cancella il turno.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ProjectEventsTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();
    private ApiHarness? _app;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            _app = await ApiHarness.StartAsync(_database.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    // ---------------------------------------------------------------- autorizzazione e intestazioni

    [Fact]
    public async Task Requires_a_pm_bearer_and_an_existing_project()
    {
        TestEnvironment.RequirePostgres();
        using var pm = await _app!.LoginAsync();
        var projectId = await CreateProject(pm);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Anonymous().GetAsync($"/api/projects/{projectId}/events", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _app.WithoutPmRole().GetAsync($"/api/projects/{projectId}/events", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pm.GetAsync($"/api/projects/{Guid.NewGuid()}/events", Ct)).StatusCode);
    }

    [Fact]
    public async Task Responds_with_an_uncached_event_stream()
    {
        TestEnvironment.RequirePostgres();
        using var pm = await _app!.LoginAsync();
        var projectId = await CreateProject(pm);

        await using var sse = await Sse.OpenAsync(pm, projectId);

        Assert.Equal(HttpStatusCode.OK, sse.Response.StatusCode);
        Assert.Equal("text/event-stream", sse.Response.Content.Headers.ContentType?.MediaType);
        Assert.True(sse.Response.Headers.CacheControl?.NoCache);
    }

    // ---------------------------------------------------------------- turno completo

    [Fact]
    public async Task A_full_turn_emits_state_and_provisional_deltas_in_order_and_only_to_its_project()
    {
        TestEnvironment.RequirePostgres();
        await using var hosted = await StartHosted(new FakeModel(["Capito. ", "Chi lo ", "usera?"]));
        using var pm = await hosted.LoginAsync();
        var projectId = await CreateProject(pm);
        var otherProject = await CreateProject(pm);
        await using var sse = await Sse.OpenAsync(pm, projectId);
        await using var other = await Sse.OpenAsync(pm, otherProject);

        var turnId = await PostMessage(pm, projectId, "Voglio un portale clienti");
        var completed = await sse.WaitFor(e => e.Name == "chat.state" && e.Json.GetProperty("status").GetString() == "Completed");

        var frames = sse.Snapshot();
        var deltas = frames.Where(e => e.Name == "chat.delta").ToList();
        Assert.Equal(["Capito. ", "Chi lo ", "usera?"], deltas.Select(d => d.Json.GetProperty("text").GetString()).ToArray());
        Assert.Equal([0, 1, 2], deltas.Select(d => d.Json.GetProperty("deltaIndex").GetInt32()).ToArray());
        Assert.All(deltas, d =>
        {
            Assert.Equal(turnId, d.Json.GetProperty("chatTurnId").GetGuid());
            Assert.Equal(1, d.Json.GetProperty("attemptCount").GetInt32());
            Assert.Equal(["attemptCount", "chatTurnId", "deltaIndex", "text"], d.Json.EnumerateObject().Select(p => p.Name).Order().ToArray());
        });

        // chat.state: ChatTurnView del contratto; passa per Running e finisce Completed con il messaggio persistito.
        var states = frames.Where(e => e.Name == "chat.state").ToList();
        Assert.All(states, s => Assert.Equal(turnId, s.Json.GetProperty("id").GetGuid()));
        Assert.Contains(states, s => s.Json.GetProperty("status").GetString() == "Running");
        Assert.Same(states[^1].Raw, completed.Raw);
        var resultId = completed.Json.GetProperty("resultMessageId").GetGuid();
        Assert.Equal(JsonValueKind.Null, completed.Json.GetProperty("error").ValueKind);
        foreach (var field in new[] { "triggerMessageId", "sourceCutoffSequence", "requestedAt", "startedAt", "completedAt", "deadlineAt", "attemptCount", "maxAttempts", "nextAttemptAt" })
        {
            Assert.True(completed.Json.TryGetProperty(field, out _), field);
        }

        // Ordine: Running prima dei delta, tutti i delta prima di Completed.
        var order = frames.Select(e => e.Name == "chat.delta" ? "delta" : e.Json.GetProperty("status").GetString()).ToList();
        Assert.True(order.IndexOf("Running") < order.IndexOf("delta"));
        Assert.True(order.LastIndexOf("delta") < order.IndexOf("Completed"));

        // I delta sono provvisori: il messaggio persistito e' quello autorevole.
        var messages = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/messages", Ct);
        var assistant = messages.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Equal(resultId, assistant.GetProperty("id").GetGuid());
        Assert.Equal("Capito. Chi lo usera?", assistant.GetProperty("text").GetString());

        // Nessun processing.state: non esiste ancora alcun ProcessingRun reale.
        Assert.DoesNotContain(frames, e => e.Name == "processing.state");
        // Isolamento: l'altro progetto non riceve nulla.
        await Task.Delay(200, Ct);
        Assert.Empty(other.Snapshot());
    }

    [Fact]
    public async Task Never_exposes_reasoning_text()
    {
        TestEnvironment.RequirePostgres();
        await using var hosted = await StartHosted(new FakeModel(["Risposta visibile."]) { ReasoningFirst = "PENSIERO-SEGRETO" });
        using var pm = await hosted.LoginAsync();
        var projectId = await CreateProject(pm);
        await using var sse = await Sse.OpenAsync(pm, projectId);

        await PostMessage(pm, projectId, "Ciao");
        await sse.WaitFor(e => e.Name == "chat.state" && e.Json.GetProperty("status").GetString() == "Completed");

        var frames = sse.Snapshot();
        Assert.Contains(frames, e => e.Name == "chat.delta");
        Assert.DoesNotContain(frames, e => e.Raw.Contains("PENSIERO-SEGRETO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_turn_is_notified_with_its_redacted_error()
    {
        TestEnvironment.RequirePostgres();
        await using var hosted = await StartHosted(new FakeModel([]) { Failure = new InvalidOperationException("dettaglio-interno") });
        using var pm = await hosted.LoginAsync();
        var projectId = await CreateProject(pm);
        await using var sse = await Sse.OpenAsync(pm, projectId);

        await PostMessage(pm, projectId, "Ciao");
        var failed = await sse.WaitFor(e => e.Name == "chat.state" && e.Json.GetProperty("status").GetString() == "Failed");

        Assert.Equal("model_error", failed.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("dettaglio-interno", failed.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain(sse.Snapshot(), e => e.Name == "chat.delta");
    }

    // ---------------------------------------------------------------- fence e disconnessione

    [Fact]
    public async Task Deltas_stop_when_the_owner_loses_its_fence()
    {
        TestEnvironment.RequirePostgres();
        var model = new HookedModel("Prima. ", "Seconda. ");
        await using var hosted = await StartHosted(model);
        model.BetweenChunks = async () =>
        {
            // Un altro worker ha fatto il reclaim: il fencing token cambia mentre il vecchio owner sta ancora generando.
            await Exec("""UPDATE "ChatTurns" SET "FencingToken" = "FencingToken" + 1""");
        };
        using var pm = await hosted.LoginAsync();
        var projectId = await CreateProject(pm);
        await using var sse = await Sse.OpenAsync(pm, projectId);

        await PostMessage(pm, projectId, "Ciao");
        await sse.WaitFor(e => e.Name == "chat.delta");
        await model.Finished.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await Task.Delay(500, Ct);

        var frames = sse.Snapshot();
        Assert.Equal(["Prima. "], frames.Where(e => e.Name == "chat.delta").Select(d => d.Json.GetProperty("text").GetString()).ToArray());
        Assert.DoesNotContain(frames, e => e.Name == "chat.state" && e.Json.GetProperty("status").GetString() == "Completed");
    }

    [Fact]
    public async Task Disconnecting_the_stream_does_not_cancel_the_turn()
    {
        TestEnvironment.RequirePostgres();
        var model = new HookedModel("Prima. ", "Seconda.");
        await using var hosted = await StartHosted(model);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.BetweenChunks = () => gate.Task;
        using var pm = await hosted.LoginAsync();
        var projectId = await CreateProject(pm);
        var turnId = Guid.Empty;
        var sse = await Sse.OpenAsync(pm, projectId);

        turnId = await PostMessage(pm, projectId, "Ciao");
        await sse.WaitFor(e => e.Name == "chat.delta");
        await sse.DisposeAsync(); // il browser si disconnette a meta' risposta
        gate.SetResult();

        await model.Finished.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var status = "";
        for (var i = 0; i < 100 && status != "Completed"; i++)
        {
            status = (await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/chat-turns/{turnId}", Ct)).GetProperty("status").GetString()!;
            if (status != "Completed")
            {
                await Task.Delay(100, Ct);
            }
        }

        Assert.Equal("Completed", status);
    }

    // ---------------------------------------------------------------- supporto

    private Task<ApiHarness> StartHosted(IChatClient model) =>
        ApiHarness.StartAsync(_database.ConnectionString, prepare: false, configure: services =>
        {
            services.AddSingleton(Agent(model));
            services.AddSingleton<IChatTurnExecutor, ConversationalTurnExecutor>();
        });

    private static async Task<Guid> CreateProject(HttpClient pm)
    {
        var created = await pm.PostAsJsonAsync("/api/projects", new { name = $"Eventi {Guid.NewGuid():N}" }, Ct);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PostMessage(HttpClient pm, Guid projectId, string text)
    {
        var response = await pm.PostAsJsonAsync($"/api/projects/{projectId}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("chatTurnId").GetGuid();
    }

    private async Task Exec(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Modello che esegue un'azione tra un frammento e l'altro e segnala la fine del flusso.</summary>
    private sealed class HookedModel(params string[] chunks) : IChatClient
    {
        public Func<Task>? BetweenChunks { get; set; }

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                for (var i = 0; i < chunks.Length; i++)
                {
                    if (i > 0 && BetweenChunks is not null)
                    {
                        await BetweenChunks();
                    }

                    yield return new ChatResponseUpdate(ChatRole.Assistant, chunks[i]);
                }
            }
            finally
            {
                Finished.TrySetResult();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    internal sealed record SseEvent(string Name, string Raw)
    {
        public JsonElement Json => JsonDocument.Parse(Raw).RootElement;
    }

    /// <summary>Client SSE di prova: legge i frame in background e li accumula; i commenti (keep-alive) sono ignorati.</summary>
    internal sealed class Sse : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentQueue<SseEvent> _events = new();
        private Task _pump = Task.CompletedTask;

        public HttpResponseMessage Response { get; private set; } = null!;

        public static async Task<Sse> OpenAsync(HttpClient client, Guid projectId)
        {
            var sse = new Sse();
            sse.Response = await client.GetAsync($"/api/projects/{projectId}/events", HttpCompletionOption.ResponseHeadersRead, Ct);
            Assert.Equal(HttpStatusCode.OK, sse.Response.StatusCode);
            var stream = await sse.Response.Content.ReadAsStreamAsync(Ct);
            sse._pump = Task.Run(async () =>
            {
                try
                {
                    using var reader = new StreamReader(stream);
                    string? name = null;
                    string? data = null;
                    while (await reader.ReadLineAsync(sse._cts.Token) is { } line)
                    {
                        if (line.StartsWith("event: ", StringComparison.Ordinal))
                        {
                            name = line[7..];
                        }
                        else if (line.StartsWith("data: ", StringComparison.Ordinal))
                        {
                            data = line[6..];
                        }
                        else if (line.Length == 0 && name is not null && data is not null)
                        {
                            sse._events.Enqueue(new SseEvent(name, data));
                            name = data = null;
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
                {
                }
            });
            return sse;
        }

        public List<SseEvent> Snapshot() => [.. _events];

        public async Task<SseEvent> WaitFor(Func<SseEvent, bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (_events.FirstOrDefault(predicate) is { } found)
                {
                    return found;
                }

                await Task.Delay(25, Ct);
            }

            throw new TimeoutException("Evento SSE atteso non ricevuto: " + string.Join(", ", _events.Select(e => e.Name)));
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            Response.Dispose();
            await _pump;
            _cts.Dispose();
        }
    }
}
