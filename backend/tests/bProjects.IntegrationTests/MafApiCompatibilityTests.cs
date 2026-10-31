#pragma warning disable MAAI001 // Compaction: API sperimentale MAF, verificata qui di proposito.

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// T005: le primitive MAF 1.26.0 usate dal piano esistono con le firme attese e funzionano
/// nel processo. Il modello è uno stub locale di IChatClient: il codice MAF è reale, il LLM no
/// (il LLM reale è provato in T006).
/// </summary>
public sealed class MafApiCompatibilityTests
{
    [Fact]
    public async Task Agent_session_serializes_and_streams()
    {
        var agent = new ChatClientAgent(new EchoChatClient("risposta"), instructions: "test", name: "ConversationalAgent");

        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync("ciao", session, cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.Contains("risposta", string.Concat(updates.Select(u => u.Text)));

        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: TestContext.Current.CancellationToken);
        var restored = await agent.DeserializeSessionAsync(serialized, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(restored);
    }

    [Fact]
    public void Native_compaction_and_context_provider_compose_on_chat_client_builder()
    {
        var model = new EchoChatClient("sintesi");
        var strategy = new SummarizationCompactionStrategy(model, CompactionTriggers.TokensExceed(1000));
        var provider = new CompactionProvider(strategy);

        var client = new ChatClientBuilder(model).UseAIContextProviders(provider).Build();
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions { ChatHistoryProvider = null });

        Assert.NotNull(agent.ChatClient);
    }

    [Fact]
    public async Task Workflow_with_agent_executor_checkpoints_and_resumes()
    {
        var agent = new ChatClientAgent(new EchoChatClient("fatto"), instructions: "test", name: "Agent");
        var executor = agent.BindAsExecutor(emitEvents: true);
        var workflow = new WorkflowBuilder(executor).WithOutputFrom(executor).Build();
        var checkpoints = CheckpointManager.CreateJson(new InMemoryJsonCheckpointStore());
        var sessionId = Guid.NewGuid().ToString("N");

        await using (var run = await InProcessExecution.RunStreamingAsync(
            workflow, new List<ChatMessage> { new(ChatRole.User, "ciao") }, checkpoints, sessionId, TestContext.Current.CancellationToken))
        {
            Assert.True(await run.TrySendMessageAsync(new TurnToken(emitEvents: true)));
            await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken))
            {
            }
        }

        var latest = await checkpoints.GetLatestCheckpointAsync(sessionId, TestContext.Current.CancellationToken);
        Assert.NotNull(latest);

        await using var resumed = await InProcessExecution.ResumeStreamingAsync(workflow, latest, checkpoints, TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, resumed.SessionId);
    }

    /// <summary>LLM sostitutivo deterministico: restituisce un testo fisso, anche in streaming.</summary>
    private sealed class EchoChatClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Store JSON in memoria per provare CheckpointManager.CreateJson (lo store PostgreSQL è un task successivo).</summary>
    private sealed class InMemoryJsonCheckpointStore : ICheckpointStore<JsonElement>
    {
        private readonly ConcurrentDictionary<string, List<(CheckpointInfo Info, JsonElement Value)>> _runs = new();

        public ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string runId, CheckpointInfo? withParent = null) =>
            new(_runs.TryGetValue(runId, out var list) ? list.Select(c => c.Info).ToList() : Enumerable.Empty<CheckpointInfo>());

        public ValueTask<CheckpointInfo> CreateCheckpointAsync(string runId, JsonElement value, CheckpointInfo? parent = null)
        {
            var info = new CheckpointInfo(runId, Guid.NewGuid().ToString("N"));
            _runs.GetOrAdd(runId, _ => []).Add((info, value));
            return new(info);
        }

        public ValueTask<JsonElement> RetrieveCheckpointAsync(string runId, CheckpointInfo key) =>
            new(_runs[runId].Single(c => c.Info.Equals(key)).Value);
    }
}
