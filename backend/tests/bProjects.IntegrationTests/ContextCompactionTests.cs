using bProjects.App.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// T016 - compattazione nativa MAF (CompactionProvider + SummarizationCompactionStrategy) e controllo finale del
/// budget su un ChatClientAgent vero. Il modello e' uno stub che registra cosa riceve; i token sono parole.
/// La prova con modello reale a soglia ridotta e' T047.
/// </summary>
public sealed class ContextCompactionTests
{
    private const string Instructions = "Sei un assistente.";    // 3 parole

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Finestra 300 - output 50 - margine 50 = budget 200; soglia di attivazione 60.
    private static AiOptions Options(int trigger = 60) => new()
    {
        ContextWindowTokens = 300,
        MaxOutputTokens = 50,
        SafetyMarginTokens = 50,
        CompactionTriggerTokens = trigger,
    };

    private static ValueTask<int> Words(string text, CancellationToken _) =>
        new(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

    private static string Words10(string tag) => $"{tag} uno due tre quattro cinque sei sette otto nove";

    private static List<ChatMessage> Dialog(int count, Func<int, string> body)
    {
        var list = new List<ChatMessage>();
        for (var i = 0; i < count; i++)
        {
            list.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, body(i)));
        }

        return list;
    }

    private static async Task<ChatClientAgent> AgentAsync(ScriptedModel model, int trigger = 60) =>
        await new AgentFactory(Options(trigger), model, Words).CreateCompactingAgentAsync("ConversationalAgent", Instructions, Ct);

    [Fact]
    public async Task Below_the_trigger_nothing_is_summarized_and_the_model_sees_the_whole_context()
    {
        var model = new ScriptedModel();
        var agent = await AgentAsync(model);
        var messages = Dialog(4, i => Words10($"m{i}"));    // 40 parole + istruzioni < 60

        await agent.RunAsync(messages, cancellationToken: Ct);

        Assert.Equal(0, model.SummarizerCalls);
        Assert.Equal(["m0", "m1", "m2", "m3"], model.LastChatCall.Select(m => m.Text.Split(' ')[0]).ToArray());
    }

    [Fact]
    public async Task Above_the_trigger_the_native_strategy_summarizes_older_messages_within_budget()
    {
        var model = new ScriptedModel();
        var agent = await AgentAsync(model);
        var messages = Dialog(20, i => Words10($"m{i}"));   // 200 parole: oltre soglia 60

        await agent.RunAsync(messages, cancellationToken: Ct);

        Assert.True(model.SummarizerCalls >= 1);
        var sent = model.LastChatCall;
        Assert.Contains(sent, m => m.Text.Contains(ScriptedModel.SummaryText));
        Assert.Contains(sent, m => m.Text.StartsWith("m19 ", StringComparison.Ordinal));      // ultimi gruppi intatti
        Assert.DoesNotContain(sent, m => m.Text.StartsWith("m0 ", StringComparison.Ordinal)); // sostituiti dalla sintesi
        var total = (await Task.WhenAll(sent.Select(m => Words(m.Text, Ct).AsTask()))).Sum() + 3;
        Assert.True(total <= Options().InputBudgetTokens);
        // La lista di input dell'utente (gli "originali") non e' stata alterata.
        Assert.Equal(20, messages.Count);
        Assert.StartsWith("m0 ", messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Still_over_budget_after_compaction_fails_with_context_overflow_without_calling_the_model()
    {
        var model = new ScriptedModel();
        var agent = await AgentAsync(model, trigger: 200);
        // 9 messaggi da 30 parole = 270 > 200; nulla e' sintetizzabile (sotto il minimo preservato) ne' tagliabile.
        var messages = Dialog(9, i => string.Join(' ', Enumerable.Repeat($"w{i}", 30)));

        var e = await Assert.ThrowsAsync<ContextOverflowException>(() => agent.RunAsync(messages, cancellationToken: Ct));

        Assert.Equal("context_overflow", e.Code);
        Assert.True(e.Tokens > e.BudgetTokens);
        Assert.Equal(0, model.ChatCalls);
        var failure = ModelFailure.Classify(e);
        Assert.False(failure.IsTransient);
        Assert.Equal("context_overflow", failure.Code);
    }

    [Fact]
    public async Task Instructions_count_toward_the_input_budget()
    {
        var model = new ScriptedModel();
        var factory = new AgentFactory(Options(trigger: 200), model, Words);
        var bigInstructions = string.Join(' ', Enumerable.Repeat("istruzione", 190));
        var agent = await factory.CreateCompactingAgentAsync("A", bigInstructions, Ct);

        // 190 di istruzioni + 15 di messaggio > 200, anche se il messaggio da solo e' minuscolo.
        await Assert.ThrowsAsync<ContextOverflowException>(
            () => agent.RunAsync(Dialog(1, _ => string.Join(' ', Enumerable.Repeat("x", 15))), cancellationToken: Ct));
        Assert.Equal(0, model.ChatCalls);
    }

    [Fact]
    public async Task A_non_transient_summarizer_error_is_compaction_failed()
    {
        var model = new ScriptedModel { SummarizerFailure = new InvalidOperationException("boom") };
        var agent = await AgentAsync(model);

        var e = await Assert.ThrowsAsync<CompactionFailedException>(
            () => agent.RunAsync(Dialog(20, i => Words10($"m{i}")), cancellationToken: Ct));

        Assert.Equal("compaction_failed", e.Code);
        Assert.Equal(0, model.ChatCalls);
        Assert.Equal("compaction_failed", ModelFailure.Classify(e).Code);
        Assert.False(ModelFailure.Classify(e).IsTransient);
    }

    [Fact]
    public async Task An_empty_summary_is_compaction_failed()
    {
        var model = new ScriptedModel { SummaryOverride = "   " };
        var agent = await AgentAsync(model);

        await Assert.ThrowsAsync<CompactionFailedException>(
            () => agent.RunAsync(Dialog(20, i => Words10($"m{i}")), cancellationToken: Ct));
        Assert.Equal(0, model.ChatCalls);
    }

    [Fact]
    public async Task A_transient_summarizer_error_stays_transient_so_the_dispatcher_can_retry()
    {
        var model = new ScriptedModel { SummarizerFailure = new HttpRequestException("rete") };
        var agent = await AgentAsync(model);

        var e = await Assert.ThrowsAnyAsync<Exception>(
            () => agent.RunAsync(Dialog(20, i => Words10($"m{i}")), cancellationToken: Ct));

        Assert.IsNotType<CompactionFailedException>(e);
        Assert.True(ModelFailure.Classify(e).IsTransient);
        Assert.Equal(0, model.ChatCalls);
    }

    [Fact]
    public async Task A_stale_failure_does_not_poison_the_next_run()
    {
        var model = new ScriptedModel { SummarizerFailure = new InvalidOperationException("boom") };
        var agent = await AgentAsync(model);
        await Assert.ThrowsAnyAsync<Exception>(() => agent.RunAsync(Dialog(20, i => Words10($"m{i}")), cancellationToken: Ct));

        model.SummarizerFailure = null;
        await agent.RunAsync(Dialog(4, i => Words10($"m{i}")), cancellationToken: Ct);

        Assert.Equal(1, model.ChatCalls);
    }

    [Fact]
    public async Task Document_client_has_no_compaction_and_fails_before_the_call_when_the_snapshot_does_not_fit()
    {
        var model = new ScriptedModel();
        var client = new AgentFactory(Options(), model, Words).CreateDocumentClient();
        var agent = new ChatClientAgent(client, instructions: Instructions, name: "DocumentAgent");

        await agent.RunAsync(Dialog(1, _ => string.Join(' ', Enumerable.Repeat("fatto", 150))), cancellationToken: Ct);
        Assert.Equal(1, model.ChatCalls);

        await Assert.ThrowsAsync<ContextOverflowException>(
            () => agent.RunAsync(Dialog(1, _ => string.Join(' ', Enumerable.Repeat("fatto", 250))), cancellationToken: Ct));
        Assert.Equal(1, model.ChatCalls);       // nessuna riduzione ne' summarizer: nessuna seconda chiamata
        Assert.Equal(0, model.SummarizerCalls);
    }

    /// <summary>Modello scriptato: distingue la chiamata di sintesi (prompt di summarization) da quella principale.</summary>
    private sealed class ScriptedModel : IChatClient
    {
        public const string SummaryText = "SINTESI-DERIVATA";

        public int SummarizerCalls { get; private set; }

        public int ChatCalls { get; private set; }

        public Exception? SummarizerFailure { get; set; }

        public string? SummaryOverride { get; set; }

        public List<ChatMessage> LastChatCall { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            if (list.Any(m => m.Role == ChatRole.System && m.Text.Contains("summar", StringComparison.OrdinalIgnoreCase)))
            {
                SummarizerCalls++;
                if (SummarizerFailure is not null)
                {
                    throw SummarizerFailure;
                }

                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, SummaryOverride ?? SummaryText)));
            }

            ChatCalls++;
            LastChatCall = list;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
