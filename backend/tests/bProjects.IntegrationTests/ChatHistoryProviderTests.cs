using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T015 — history e restore delle sessioni su PostgreSQL reale con un ChatClientAgent MAF vero. Il modello è
/// uno stub che registra i messaggi ricevuti: serve a provare cosa il provider fa arrivare al modello.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ChatHistoryProviderTests : IAsyncLifetime
{
    private const string Config = "chat-v1";
    private const string Marker = "marker-sintesi";

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

    // ---------------------------------------------------------------- esempio normativo U1/U2/A1

    [Fact]
    public async Task T1_sees_only_the_trigger_as_input_and_the_provider_writes_nothing()
    {
        var s = await SeedAsync("U1", "U2");
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var run = await RunTurnAsync(db, s.ConversationId, s.Turns[0]);

        Assert.False(run.Restored.Reused);
        Assert.Equal("no_snapshot", run.Restored.RebuildReason);
        Assert.Equal(["U1"], run.ModelSaw);                  // trigger una sola volta, nessun dato successivo (U2)
        Assert.Equal(2, await db.ConversationMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task T2_with_cutoff_2_rebuilds_U1_U2_from_originals_without_A1_even_when_the_snapshot_watermark_is_3()
    {
        var s = await SeedAsync("U1", "U2");
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var t1 = await RunTurnAsync(db, s.ConversationId, s.Turns[0]);
        await CommitResponseAsync(db, s.ConversationId, s.Turns[0], t1, "A1");   // A1 ottiene seq=3
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(3, conversation.ContextThroughSequence);
        Assert.Equal(["U1", "U2", "A1"], await BodiesAsync(db));

        var t2 = await RunTurnAsync(db, s.ConversationId, s.Turns[1]);

        Assert.False(t2.Restored.Reused);
        Assert.Equal("watermark_beyond_cutoff", t2.Restored.RebuildReason);
        Assert.Equal(["U1", "U2"], t2.ModelSaw);             // U1 come history, U2 trigger, mai A1
        Assert.False(HasMarker(t2));                          // nessuna sintesi/stato oltre il cutoff
    }

    [Fact]
    public async Task A_compatible_snapshot_is_reused_when_its_references_are_an_ordered_prefix()
    {
        var s = await SeedAsync("U1");
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var t1 = await RunTurnAsync(db, s.ConversationId, s.Turns[0]);
        await CommitResponseAsync(db, s.ConversationId, s.Turns[0], t1, "A1");   // U1(1) A1(2)
        var second = await PostAsync(s.ProjectId, "U2");                          // U2(3), cutoff 3

        var t2 = await RunTurnAsync(db, s.ConversationId, second);

        Assert.True(t2.Restored.Reused);
        Assert.Null(t2.Restored.RebuildReason);
        Assert.Equal(["U1", "A1", "U2"], t2.ModelSaw);       // history da PostgreSQL, trigger una sola volta
        Assert.True(HasMarker(t2));
    }

    // ---------------------------------------------------------------- sessione incompatibile -> nuova dagli originali

    public static TheoryData<string> IncompatibleSnapshots =>
        ["maf_version", "other_conversation", "wrong_reference", "extra_reference", "wrong_watermark", "unreadable", "empty_references"];

    [Theory]
    [MemberData(nameof(IncompatibleSnapshots))]
    public async Task An_incompatible_snapshot_is_never_deserialized_and_a_fresh_session_is_built_from_originals(string defect)
    {
        var s = await SeedAsync("U1");
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var t1 = await RunTurnAsync(db, s.ConversationId, s.Turns[0]);
        await CommitResponseAsync(db, s.ConversationId, s.Turns[0], t1, "A1");
        var second = await PostAsync(s.ProjectId, "U2");
        await CorruptSnapshotAsync(db, defect);

        var t2 = await RunTurnAsync(db, s.ConversationId, second);

        Assert.False(t2.Restored.Reused);
        Assert.NotNull(t2.Restored.RebuildReason);
        Assert.Equal(["U1", "A1", "U2"], t2.ModelSaw);       // ricostruita dagli originali ammessi
        Assert.False(HasMarker(t2));
    }

    [Fact]
    public async Task A_change_of_the_resolved_chat_configuration_forces_a_rebuild()
    {
        var s = await SeedAsync("U1");
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var t1 = await RunTurnAsync(db, s.ConversationId, s.Turns[0]);
        await CommitResponseAsync(db, s.ConversationId, s.Turns[0], t1, "A1");
        var second = await PostAsync(s.ProjectId, "U2");

        var t2 = await RunTurnAsync(db, s.ConversationId, second, configVersion: "chat-v2");

        Assert.False(t2.Restored.Reused);
        Assert.Equal("version_or_config", t2.Restored.RebuildReason);
        Assert.False(HasMarker(t2));
    }

    [Fact]
    public async Task An_intermediate_reference_that_breaks_the_ordered_prefix_forces_a_rebuild()
    {
        var s = await SeedAsync("U1");
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var t1 = await RunTurnAsync(db, s.ConversationId, s.Turns[0]);
        await CommitResponseAsync(db, s.ConversationId, s.Turns[0], t1, "A1");
        var conversation = await db.Conversations.SingleAsync(Ct);
        var envelope = JsonSerializer.Deserialize<ChatSessionEnvelope>(conversation.MafChatSessionState!)!;
        envelope.Messages.Insert(1, new(Guid.NewGuid(), 1)); // un riferimento in più tra U1 e A1: la history non lo contiene
        conversation.MafChatSessionState = JsonSerializer.Serialize(envelope);
        await db.SaveChangesAsync(Ct);
        var second = await PostAsync(s.ProjectId, "U2");

        var t2 = await RunTurnAsync(db, s.ConversationId, second);

        Assert.False(t2.Restored.Reused);
        Assert.Equal("not_a_prefix", t2.Restored.RebuildReason);
        Assert.Equal(["U1", "A1", "U2"], t2.ModelSaw);
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Seed(Guid ProjectId, Guid ConversationId, List<Guid> Turns);

    private sealed record TurnRun(RestoredChatSession Restored, List<string> ModelSaw, ChatClientAgent Agent, AgentSession Session);

    private async Task<Seed> SeedAsync(params string[] userTexts)
    {
        using var client = await App.LoginAsync();
        var created = await client.PostAsJsonAsync("/api/projects", new { name = $"History {Guid.NewGuid():N}" }, Ct);
        var projectId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var turns = new List<Guid>();
        foreach (var text in userTexts)
        {
            turns.Add(await PostAsync(projectId, text));
        }

        using var scope = App.Services.CreateScope();
        var conversationId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Conversations
            .Where(c => c.ProjectId == projectId).Select(c => c.Id).SingleAsync(Ct);
        return new(projectId, conversationId, turns);
    }

    private async Task<Guid> PostAsync(Guid projectId, string text)
    {
        using var client = await App.LoginAsync();
        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("chatTurnId").GetGuid();
    }

    /// <summary>Esegue il turno come lo farà T017: restore, agente con il provider PostgreSQL, trigger come unico input.</summary>
    private static async Task<TurnRun> RunTurnAsync(AppDbContext db, Guid conversationId, Guid turnId, string configVersion = Config)
    {
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId, Ct);
        var turn = await db.ChatTurns.AsNoTracking().SingleAsync(t => t.Id == turnId, Ct);
        var trigger = await db.ConversationMessages.AsNoTracking().SingleAsync(m => m.Id == turn.TriggerMessageId, Ct);
        var model = new RecordingChatClient();
        var provider = new PostgresChatHistoryProvider(db, conversationId, turn.SourceCutoffSequence, turn.TriggerMessageId);
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions { ChatHistoryProvider = provider });

        var restored = await PostgresChatHistoryProvider.RestoreAsync(agent, db, conversation, turn, configVersion, Ct);
        await agent.RunAsync(trigger.Body, restored.Session, cancellationToken: Ct);

        return new(restored, model.LastRequest.Select(m => m.Text).ToList(), agent, restored.Session);
    }

    /// <summary>Come il commit di T018: assistant append-only, snapshot, watermark e revisione nella stessa transazione.</summary>
    private static async Task CommitResponseAsync(AppDbContext db, Guid conversationId, Guid turnId, TurnRun run, string text)
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
            AgentId = "test",
            ReplyToMessageId = turn.TriggerMessageId,
            ChatTurnId = turnId,
        };
        db.ConversationMessages.Add(response);
        await db.SaveChangesAsync(Ct);

        run.Session.StateBag.SetValue(Marker, "x"); // dato derivato che non deve mai attraversare un cutoff
        conversation.MafChatSessionState = await PostgresChatHistoryProvider.CreateSnapshotAsync(
            run.Agent, run.Session, db, conversation, turn, new(response.Id, response.Sequence), Config, Ct);
        conversation.MafVersion = Conversation.CurrentMafVersion;
        conversation.ChatConfigVersion = Config;
        conversation.ContextThroughSequence = response.Sequence;
        conversation.ChatSessionRevision++;
        await db.SaveChangesAsync(Ct);
        await tx.CommitAsync(Ct);
        db.ChangeTracker.Clear();
    }

    private static async Task CorruptSnapshotAsync(AppDbContext db, string defect)
    {
        var conversation = await db.Conversations.SingleAsync(Ct);
        var envelope = JsonSerializer.Deserialize<ChatSessionEnvelope>(conversation.MafChatSessionState!)!;
        switch (defect)
        {
            case "maf_version": conversation.MafVersion = "0.0.1"; break;
            case "other_conversation": envelope = envelope with { ConversationId = Guid.NewGuid() }; break;
            case "wrong_reference": envelope.Messages[0] = envelope.Messages[0] with { Id = Guid.NewGuid() }; break;
            case "extra_reference": envelope.Messages.Add(new(Guid.NewGuid(), 3)); conversation.ContextThroughSequence = 3; break;
            case "wrong_watermark": conversation.ContextThroughSequence = 1; break;
            case "empty_references": envelope.Messages.Clear(); break;
            case "unreadable": conversation.MafChatSessionState = "{\"nope\":1}"; break; // JSON valido per jsonb, forma errata
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        if (defect != "unreadable")
        {
            conversation.MafChatSessionState = JsonSerializer.Serialize(envelope);
        }

        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();
    }

    private static Task<List<string>> BodiesAsync(AppDbContext db) =>
        db.ConversationMessages.AsNoTracking().OrderBy(m => m.Sequence).Select(m => m.Body).ToListAsync(Ct);

    private static bool HasMarker(TurnRun run) => run.Session.StateBag.TryGetValue<string>(Marker, out var value) && value is not null;

    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatMessage> LastRequest { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastRequest = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "risposta")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = messages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "risposta");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
