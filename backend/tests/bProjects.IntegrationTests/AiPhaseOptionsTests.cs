using System.Net;
using bProjects.App.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace bProjects.IntegrationTests;

/// <summary>Impostazioni per fase (<c>Ai:Phases</c>): ereditarietà, retrocompatibilità, budget, validazione e opzioni di richiesta.</summary>
public sealed class AiPhaseOptionsTests
{
    private static readonly AiPhase[] AllPhases = Enum.GetValues<AiPhase>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AiOptions Valid() => new() { Endpoint = "http://192.168.253.18:8080/v1/", ModelId = "m", ApiKey = "k" };

    [Fact]
    public void Without_overrides_every_phase_equals_the_current_global_behaviour()
    {
        var o = Valid();

        foreach (var phase in AllPhases)
        {
            var s = o.ForPhase(phase);
            Assert.Equal(o.MaxOutputTokens, s.MaxOutputTokens);
            Assert.Equal(o.EnableThinking, s.EnableThinking);
            Assert.Equal(o.InputBudgetTokens, s.InputBudgetTokens);
            Assert.Equal(32768 - 4096 - 1024, s.InputBudgetTokens);
            Assert.Equal(phase, s.Phase);
        }

        Assert.True(new AiOptionsValidator().Validate(null, o).Succeeded);
    }

    [Fact]
    public void A_phase_override_changes_only_that_phase_and_its_input_budget()
    {
        var o = Valid();
        o.EnableThinking = false;
        o.Phases.Analysis.MaxOutputTokens = 8192;
        o.Phases.Document.EnableThinking = true;

        Assert.Equal(8192, o.ForPhase(AiPhase.Analysis).MaxOutputTokens);
        Assert.Equal(32768 - 8192 - 1024, o.ForPhase(AiPhase.Analysis).InputBudgetTokens);
        Assert.False(o.ForPhase(AiPhase.Analysis).EnableThinking);   // l'altro campo eredita
        Assert.True(o.ForPhase(AiPhase.Document).EnableThinking);
        Assert.Equal(4096, o.ForPhase(AiPhase.Document).MaxOutputTokens);
        Assert.Equal(o.InputBudgetTokens, o.ForPhase(AiPhase.Chat).InputBudgetTokens);
        Assert.Equal(o.InputBudgetTokens, o.ForPhase(AiPhase.Compaction).InputBudgetTokens);
    }

    [Fact]
    public void Global_values_are_inherited_when_they_change_and_an_explicit_false_overrides_a_global_true()
    {
        var o = Valid();
        o.MaxOutputTokens = 2000;
        o.EnableThinking = true;
        o.Phases.Chat.EnableThinking = false;

        Assert.Equal(2000, o.ForPhase(AiPhase.Chat).MaxOutputTokens);
        Assert.False(o.ForPhase(AiPhase.Chat).EnableThinking);
        Assert.True(o.ForPhase(AiPhase.Analysis).EnableThinking);
    }

    [Fact]
    public void Configuration_binding_reads_phases_and_treats_empty_strings_as_inherit()
    {
        // Come le variabili d'ambiente di Compose: Ai__Phases__Chat__MaxOutputTokens="" per «eredita».
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:MaxOutputTokens"] = "4096",
            ["Ai:EnableThinking"] = "false",
            ["Ai:RuntimeCheckSeconds"] = "30",
            ["Ai:Phases:Chat:MaxOutputTokens"] = "",
            ["Ai:Phases:Chat:EnableThinking"] = "",
            ["Ai:Phases:Analysis:MaxOutputTokens"] = "6144",
            ["Ai:Phases:Analysis:EnableThinking"] = "true",
            ["Ai:Phases:Compaction:MaxOutputTokens"] = "1024",
        }).Build();

        var o = configuration.GetSection(AiOptions.SectionName).Get<AiOptions>()!;

        Assert.Null(o.Phases.Chat.MaxOutputTokens);
        Assert.Null(o.Phases.Chat.EnableThinking);
        Assert.Equal(6144, o.Phases.Analysis.MaxOutputTokens);
        Assert.True(o.Phases.Analysis.EnableThinking);
        Assert.Equal(1024, o.ForPhase(AiPhase.Compaction).MaxOutputTokens);
        Assert.Null(o.Phases.Document.MaxOutputTokens);
        Assert.Equal(30, o.RuntimeCheckSeconds);
        Assert.Equal(60, new AiOptions().RuntimeCheckSeconds);
    }

    [Fact]
    public void The_context_budget_follows_the_phase_output_cap()
    {
        var o = Valid();
        o.Phases.Document.MaxOutputTokens = 8192;
        o.CompactionTriggerTokens = 10000;

        Assert.Equal(27648, ContextBudget.From(o).InputBudgetTokens);
        Assert.Equal(27648, ContextBudget.From(o, AiPhase.Analysis).InputBudgetTokens);
        Assert.Equal(32768 - 8192 - 1024, ContextBudget.From(o, AiPhase.Document).InputBudgetTokens);
        Assert.Equal(10000, ContextBudget.From(o, AiPhase.Chat).TriggerTokens);

        o.CompactionTriggerTokens = 0;
        Assert.Equal(ContextBudget.From(o, AiPhase.Document).InputBudgetTokens, ContextBudget.From(o, AiPhase.Document).TriggerTokens);
    }

    [Fact]
    public async Task The_summary_output_cap_is_the_compaction_phase_cap_and_the_document_preflight_uses_the_document_budget()
    {
        var o = new AiOptions { ContextWindowTokens = 300, MaxOutputTokens = 50, SafetyMarginTokens = 50, CompactionTriggerTokens = 60 };
        o.Phases.Compaction.MaxOutputTokens = 10;
        o.Phases.Document.MaxOutputTokens = 150; // budget di input del documento: 300 - 150 - 50 = 100
        var model = new ScriptedModel();
        var factory = new AgentFactory(o, model, Words);

        var agent = await factory.CreateCompactingAgentAsync("A", "Sei un assistente.", Ct);
        var dialog = Enumerable.Range(0, 20).Select(i => new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"m{i} uno due tre quattro cinque sei sette otto nove")).ToList();
        await agent.RunAsync(dialog, cancellationToken: Ct);

        Assert.True(model.SummarizerOptions.Count >= 1);
        Assert.All(model.SummarizerOptions, options => Assert.Equal(10, options?.MaxOutputTokens));

        var overflow = await Assert.ThrowsAsync<ContextOverflowException>(
            () => factory.EnsureDocumentWithinBudgetAsync([new ChatMessage(ChatRole.User, string.Join(' ', Enumerable.Repeat("w", 120)))], "istruzioni", Ct).AsTask());
        Assert.Equal(100, overflow.BudgetTokens);
    }

    [Fact]
    public async Task Chat_options_carry_the_phase_output_cap_and_thinking_flag_on_the_wire()
    {
        var o = new AiOptions { Endpoint = "http://dgx.internal:8080/v1/", ModelId = "m", ApiKey = "k" };
        o.Phases.Document.MaxOutputTokens = 3000;
        o.Phases.Document.EnableThinking = true;
        var bodies = new List<string>();
        var handler = new RecordingHandler(bodies);
        using var client = new InternalModelClient(o, handler);

        var chat = client.CreateChatOptions();
        var document = client.CreateChatOptions(phase: AiPhase.Document);
        var compaction = client.CreateChatOptions(temperature: 0, phase: AiPhase.Compaction);

        Assert.Equal(4096, chat.MaxOutputTokens);
        Assert.Equal(3000, document.MaxOutputTokens);
        Assert.Equal(4096, compaction.MaxOutputTokens);

        await client.ChatClient.GetResponseAsync("ciao", document, Ct);
        await client.ChatClient.GetResponseAsync("ciao", chat, Ct);
        Assert.Contains("\"enable_thinking\":true", bodies[0]);
        Assert.Contains("\"enable_thinking\":false", bodies[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_phase_output_cap_is_rejected(int value)
    {
        var o = Valid();
        o.Phases.Chat.MaxOutputTokens = value;

        Assert.False(new AiOptionsValidator().Validate(null, o).Succeeded);
    }

    [Fact]
    public void A_phase_whose_input_budget_is_not_positive_is_rejected_naming_the_phase()
    {
        var o = Valid();
        o.Phases.Document.MaxOutputTokens = 32768;

        var result = new AiOptionsValidator().Validate(null, o);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Ai:Phases:Document", StringComparison.Ordinal));
    }

    [Fact]
    public void The_compaction_trigger_must_fit_the_tightest_chat_or_analysis_budget()
    {
        var o = Valid();
        o.Phases.Analysis.MaxOutputTokens = 16384;   // budget analisi: 32768 - 16384 - 1024 = 15360
        o.CompactionTriggerTokens = 15360;
        Assert.True(new AiOptionsValidator().Validate(null, o).Succeeded);

        o.CompactionTriggerTokens = 15361;
        Assert.True(new AiOptionsValidator().Validate(null, o).Failed);

        // Il documento non compatta: il suo budget più stretto non vincola la soglia.
        o.Phases.Analysis.MaxOutputTokens = null;
        o.Phases.Document.MaxOutputTokens = 16384;
        o.CompactionTriggerTokens = 20000;
        Assert.True(new AiOptionsValidator().Validate(null, o).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_non_positive_runtime_check_interval_is_rejected(int seconds)
    {
        var o = Valid();
        o.RuntimeCheckSeconds = seconds;

        Assert.False(new AiOptionsValidator().Validate(null, o).Succeeded);
    }

    private static ValueTask<int> Words(string text, CancellationToken _) =>
        new(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

    private sealed class RecordingHandler(List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>Modello scriptato: la chiamata di sintesi si riconosce dal prompt di summarization e ne registra le opzioni.</summary>
    private sealed class ScriptedModel : IChatClient
    {
        public List<ChatOptions?> SummarizerOptions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var summary = messages.Any(m => m.Role == ChatRole.System && m.Text.StartsWith(AgentFactory.SummarizationPrompt, StringComparison.Ordinal));
            if (summary)
            {
                SummarizerOptions.Add(options);
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, summary ? "SINTESI" : "ok")));
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
