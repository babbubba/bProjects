using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T017 - ConversationalAgent collegato al dispatch: streaming MAF, history dal solo provider PostgreSQL, nessun
/// effetto collaterale (la persistenza definitiva e' di T018) e prompt di dialogo. Modello fake su PostgreSQL reale;
/// la prova con il DGX reale e' in <see cref="ConversationalAgentRealTests"/>.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ConversationalAgentTests : IAsyncLifetime
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

    private ApiHarness App
    {
        get
        {
            TestEnvironment.RequirePostgres();
            return _app!;
        }
    }

    // ---------------------------------------------------------------- prompt (nessun database)

    [Fact]
    public void The_prompt_is_embedded_and_states_the_dialogue_boundaries()
    {
        var prompt = ConversationalAgent.Instructions;

        Assert.Contains("al massimo UNA domanda", prompt);
        Assert.Contains("Non ripetere una domanda", prompt);
        Assert.Contains("Non produci requisiti formali", prompt);
        Assert.Contains("pulsanti", prompt);
        Assert.Contains("Non dichiarare di aver salvato", prompt);
    }

    // ---------------------------------------------------------------- run

    [Fact]
    public async Task Streams_the_reply_in_order_with_the_dialogue_prompt_and_the_trigger_as_only_input()
    {
        var s = await SeedAsync("Voglio un portale clienti");
        var model = new FakeModel(["Capito, ", "un portale ", "per i clienti. ", "Quali utenti lo useranno?"]);
        var deltas = new List<string>();

        var reply = await RunAsync(model, s.Turns[0], (d, _) => { deltas.Add(d); return default; });

        Assert.Equal(["Capito, ", "un portale ", "per i clienti. ", "Quali utenti lo useranno?"], deltas);
        Assert.Equal("Capito, un portale per i clienti. Quali utenti lo useranno?", reply.Text);
        Assert.Equal(ConversationalAgent.Instructions, model.LastOptions!.Instructions);
        Assert.Null(model.LastOptions.Tools);                      // nessuno strumento: non puo' modificare requisiti/bozze
        Assert.Equal(["Voglio un portale clienti"], model.LastRequest.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        Assert.False(reply.Restored.Reused);
    }

    [Fact]
    public async Task Writes_nothing_the_atomic_commit_belongs_to_T018()
    {
        var s = await SeedAsync("Primo messaggio");
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = await db.Conversations.AsNoTracking().SingleAsync(Ct);

        await RunAsync(new FakeModel(["Risposta"]), s.Turns[0]);

        db.ChangeTracker.Clear();
        var after = await db.Conversations.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(1, await db.ConversationMessages.CountAsync(Ct));
        Assert.Equal(before.MafChatSessionState, after.MafChatSessionState);
        Assert.Equal(before.ContextThroughSequence, after.ContextThroughSequence);
        Assert.Equal(before.ChatSessionRevision, after.ChatSessionRevision);
        Assert.Equal(ChatTurnStatus.Queued, (await db.ChatTurns.AsNoTracking().SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task Resumes_history_from_PostgreSQL_and_reuses_the_committed_session_snapshot()
    {
        var s = await SeedAsync("Voglio un portale clienti");
        var first = await RunAsync(new FakeModel(["Chi lo usera?"]), s.Turns[0]);
        await CommitAsync(s.ConversationId, s.Turns[0], first, "Chi lo usera?");
        var second = await PostAsync(s.ProjectId, "Lo useranno gli agenti");
        var model = new FakeModel(["Bene."]);

        var reply = await RunAsync(model, second);

        Assert.True(reply.Restored.Reused);
        Assert.Equal(["Voglio un portale clienti", "Chi lo usera?", "Lo useranno gli agenti"],
            model.LastRequest.Where(m => m.Role != ChatRole.System).Select(m => m.Text));
    }

    [Fact]
    public async Task Reasoning_updates_never_reach_the_reply_or_the_deltas()
    {
        var s = await SeedAsync("Ciao");
        var model = new FakeModel(["Salve."]) { ReasoningFirst = "ragionamento interno" };
        var deltas = new List<string>();

        var reply = await RunAsync(model, s.Turns[0], (d, _) => { deltas.Add(d); return default; });

        Assert.Equal("Salve.", reply.Text);
        Assert.DoesNotContain(deltas, d => d.Contains("ragionamento"));
    }

    [Fact]
    public async Task An_empty_reply_is_an_error_not_an_outcome()
    {
        var s = await SeedAsync("Ciao");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new FakeModel(["  "]), s.Turns[0]));
    }

    [Fact]
    public async Task The_executor_hands_the_reply_to_the_committer_and_propagates_model_errors()
    {
        var s = await SeedAsync("Ciao");
        var committer = new CapturingCommitter();
        var services = new ServiceCollection()
            .AddScoped(_ => App.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>())
            .AddSingleton<IChatTurnCommitter>(committer)
            .BuildServiceProvider();
        var claim = await ClaimAsync(s.Turns[0]);

        var ok = new ConversationalTurnExecutor(services.GetRequiredService<IServiceScopeFactory>(), Agent(new FakeModel(["Salve."])));
        await ok.ExecuteAsync(claim, Ct);
        Assert.Equal("Salve.", committer.Reply!.Text);

        var failing = new ConversationalTurnExecutor(services.GetRequiredService<IServiceScopeFactory>(),
            Agent(new FakeModel([]) { Failure = new HttpRequestException("giu", null, HttpStatusCode.ServiceUnavailable) }));
        await Assert.ThrowsAsync<HttpRequestException>(() => failing.ExecuteAsync(claim, Ct));
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Seed(Guid ProjectId, Guid ConversationId, List<Guid> Turns);

    private static ConversationalAgent Agent(IChatClient model) =>
        new(new AgentFactory(new AiOptions { ContextWindowTokens = 32768 }, model,
            (t, _) => new(t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)),
            new ChatOptions { ModelId = "fake" });

    private async Task<ConversationalReply> RunAsync(
        FakeModel model, Guid turnId, Func<string, CancellationToken, ValueTask>? onDelta = null)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await Agent(model).RunAsync(db, await ClaimAsync(turnId), onDelta, Ct);
    }

    private async Task<ChatTurnClaim> ClaimAsync(Guid turnId)
    {
        using var scope = App.Services.CreateScope();
        var t = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatTurns.AsNoTracking().SingleAsync(x => x.Id == turnId, Ct);
        return new(t.Id, t.ConversationId, t.TriggerMessageId, t.SourceCutoffSequence, "test-owner", 1, 1,
            DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(3));
    }

    private async Task<Seed> SeedAsync(string text)
    {
        using var client = await App.LoginAsync();
        var created = await client.PostAsJsonAsync("/api/projects", new { name = $"Chat {Guid.NewGuid():N}" }, Ct);
        var projectId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var turn = await PostAsync(projectId, text);
        using var scope = App.Services.CreateScope();
        var conversationId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Conversations
            .Where(c => c.ProjectId == projectId).Select(c => c.Id).SingleAsync(Ct);
        return new(projectId, conversationId, [turn]);
    }

    private async Task<Guid> PostAsync(Guid projectId, string text)
    {
        using var client = await App.LoginAsync();
        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("chatTurnId").GetGuid();
    }

    /// <summary>Stand-in del commit di T018, solo per preparare lo snapshot del turno successivo.</summary>
    internal static async Task CommitAsync(AppDbContext db, Guid conversationId, Guid turnId, ConversationalReply reply, string text)
    {
        await using var tx = await db.Database.BeginTransactionAsync(Ct);
        var conversation = await db.Conversations.SingleAsync(c => c.Id == conversationId, Ct);
        var turn = await db.ChatTurns.SingleAsync(t => t.Id == turnId, Ct);
        var response = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Sequence = ++conversation.LastSequence,
            Role = MessageRole.Assistant,
            Body = text,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentId = ConversationalAgent.AgentName,
            ReplyToMessageId = turn.TriggerMessageId,
            ChatTurnId = turnId,
        };
        db.ConversationMessages.Add(response);
        await db.SaveChangesAsync(Ct);
        conversation.MafChatSessionState = await PostgresChatHistoryProvider.CreateSnapshotAsync(
            reply.Agent, reply.Session, db, conversation, turn, new(response.Id, response.Sequence), reply.ChatConfigVersion, Ct);
        conversation.MafVersion = Conversation.CurrentMafVersion;
        conversation.ChatConfigVersion = reply.ChatConfigVersion;
        conversation.ContextThroughSequence = response.Sequence;
        conversation.ChatSessionRevision++;
        await db.SaveChangesAsync(Ct);
        await tx.CommitAsync(Ct);
        db.ChangeTracker.Clear();
    }

    private async Task CommitAsync(Guid conversationId, Guid turnId, ConversationalReply reply, string text)
    {
        using var scope = App.Services.CreateScope();
        await CommitAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), conversationId, turnId, reply, text);
    }

    private sealed class CapturingCommitter : IChatTurnCommitter
    {
        public ConversationalReply? Reply { get; private set; }

        public Task CommitAsync(ChatTurnClaim claim, ConversationalReply reply, CancellationToken ct)
        {
            Reply = reply;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeModel(string[] chunks) : IChatClient
    {
        public List<ChatMessage> LastRequest { get; private set; } = [];

        public ChatOptions? LastOptions { get; private set; }

        public string? ReasoningFirst { get; init; }

        public Exception? Failure { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("La chat usa solo lo streaming.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = messages.ToList();
            LastOptions = options;
            await Task.Yield();
            if (Failure is not null)
            {
                throw Failure;
            }

            if (ReasoningFirst is not null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(ReasoningFirst)]);
            }

            foreach (var chunk in chunks)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

/// <summary>T017 con il modello reale su DGX Spark: uno scambio in streaming e la ripresa della history (MafReal + PostgreSQL).</summary>
[Trait("Category", TestCategories.MafReal)]
public sealed class ConversationalAgentRealTests : IAsyncLifetime
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

    [Fact]
    public async Task Real_model_streams_a_pertinent_reply_with_at_most_one_question_and_resumes_the_history()
    {
        var options = TestEnvironment.RequireDgxOptions();
        TestEnvironment.RequirePostgres();
        using var model = new InternalModelClient(options);
        var agent = new ConversationalAgent(AgentFactory.Create(options, model), model.CreateChatOptions(temperature: 0.2f));

        using var client = await _app!.LoginAsync();
        var created = await client.PostAsJsonAsync("/api/projects", new { name = $"Reale {Guid.NewGuid():N}" }, Ct);
        var projectId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var first = await PostAsync(client, projectId, "Voglio realizzare un portale clienti chiamato Aurora per gestire i ticket di assistenza.");

        var deltas = new List<string>();
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var claim = await ClaimAsync(db, first);
            var reply = await agent.RunAsync(db, claim, (d, _) => { deltas.Add(d); return default; }, Ct);

            Assert.True(deltas.Count > 1, $"attesi piu delta in streaming, ricevuti {deltas.Count}");
            Assert.Equal(reply.Text, string.Concat(deltas));
            Assert.InRange(reply.Text.Count(c => c == '?'), 0, 1);
            Assert.DoesNotContain("<think>", reply.Text, StringComparison.OrdinalIgnoreCase);
            await ConversationalAgentTests.CommitAsync(db, claim.ConversationId, first, reply, reply.Text);
        }

        var second = await PostAsync(client, projectId, "Come si chiama il portale di cui parlavamo? Rispondi solo con il nome.");
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reply = await agent.RunAsync(db, await ClaimAsync(db, second), null, Ct);

            Assert.True(reply.Restored.Reused);
            Assert.Contains("Aurora", reply.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<Guid> PostAsync(HttpClient client, Guid projectId, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("chatTurnId").GetGuid();
    }

    private static async Task<ChatTurnClaim> ClaimAsync(AppDbContext db, Guid turnId)
    {
        var t = await db.ChatTurns.AsNoTracking().SingleAsync(x => x.Id == turnId, Ct);
        return new(t.Id, t.ConversationId, t.TriggerMessageId, t.SourceCutoffSequence, "test-owner", 1, 1,
            DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(3));
    }
}
