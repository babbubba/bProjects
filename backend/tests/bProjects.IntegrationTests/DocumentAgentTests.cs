using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// T039 — DocumentAgent MAF (contracts/ai-workflow.md §6-§7) su un ChatClientAgent vero con un IChatClient finto: l'intero JSON
/// confermato più le istruzioni in un solo messaggio utente, nessuna history né compattazione, preflight dei token con errore
/// esplicito oltre il budget e nessuna chiamata al modello in quel caso. I token sono parole. Nessuna chiamata al DGX
/// (la prova con il modello reale è T043).
/// </summary>
public sealed class DocumentAgentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Finestra 600 - output 50 - margine 50 = budget 500; soglia di compattazione (se ci fosse) 60.
    private static AiOptions Options(int window = 600, int trigger = 60) => new()
    {
        ContextWindowTokens = window,
        MaxOutputTokens = 50,
        SafetyMarginTokens = 50,
        CompactionTriggerTokens = trigger,
    };

    private static ValueTask<int> Words(string text, CancellationToken _) =>
        new(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

    private static DocumentInput Input(AnalysisContent? content = null)
    {
        var world = AnalysisFixtures.World();
        content ??= AnalysisContentJson.Deserialize(AnalysisFixtures.NominalAi(world).ToJsonString());
        return new DocumentInput(content.ProjectId, content.AnalysisId, Guid.NewGuid(), 1, content);
    }

    private static DocumentAgent Agent(FakeModel model, AiOptions? options = null) =>
        new(new AgentFactory(options ?? Options(window: 32768, trigger: 0), model, Words), new ChatOptions { ModelId = "fake", Temperature = 0.2f });

    [Fact]
    public void The_prompt_is_an_embedded_resource_with_adaptive_length_sources_and_no_approval_and_no_word_targets()
    {
        var prompt = DocumentAgent.Instructions;

        Assert.False(string.IsNullOrWhiteSpace(prompt));
        Assert.Contains("[fonte:<id>]", prompt);
        Assert.Contains("senza obiettivi di parole o di pagine", prompt);
        Assert.Contains("Open", prompt); // i conflitti aperti restano aperti
        Assert.Contains("Assumed", prompt);
        Assert.Contains("approvato", prompt);
        // nessuna soglia numerica di lunghezza (contracts/ai-workflow.md §6): niente «N parole/pagine/righe/caratteri»
        Assert.DoesNotMatch(new Regex(@"\d+\s*(–|-|a)?\s*\d*\s*(parole|pagine|righe|caratteri|paragrafi)", RegexOptions.IgnoreCase), prompt);
        Assert.Equal("DocumentAgent_document", DocumentAgent.HostExecutorId);
    }

    [Fact]
    public void The_single_user_message_carries_the_whole_confirmed_json_and_the_trusted_identities_with_readable_accents()
    {
        var input = Input();

        var messages = DocumentAgent.BuildMessages(input);

        var message = Assert.Single(messages);
        Assert.Equal(ChatRole.User, message.Role);
        var text = message.Text;
        Assert.Contains($"analysisId: {input.AnalysisId}", text);
        Assert.Contains($"projectId: {input.ProjectId}", text);
        Assert.Contains($"revisionId: {input.RevisionId} (versione 1)", text);
        var embedded = text[text.IndexOf('{', StringComparison.Ordinal)..(text.LastIndexOf('}') + 1)];
        Assert.Equal(AnalysisContentJson.Serialize(input.Content), AnalysisContentJson.Serialize(AnalysisContentJson.Deserialize(embedded))); // l'intero contenuto, senza perdite
        Assert.DoesNotContain("\\u00", embedded); // niente lettere accentate in forma di escape

        var accented = input.Content with
        {
            Goals = [.. input.Content.Goals, new AnalysisItem { Id = Guid.NewGuid(), Text = "Realizzare un'unità già operativa", Classification = AnalysisClassification.Assumed, EvidenceRefs = [] }],
        };
        Assert.Contains("un'unità già operativa", DocumentAgent.BuildMessages(Input(accented))[0].Text);
    }

    [Fact]
    public async Task Running_the_agent_sends_one_user_message_and_the_instructions_without_history_tools_or_structured_format()
    {
        var model = new FakeModel("# Documento\n\nTesto.");
        var agent = Agent(model).CreateAgent();
        var messages = DocumentAgent.BuildMessages(Input());

        var first = await agent.RunAsync(messages, cancellationToken: Ct);
        var second = await agent.RunAsync(messages, cancellationToken: Ct); // nuova sessione: nulla del primo passaggio

        Assert.Equal("# Documento\n\nTesto.", first.Text);
        Assert.Equal("# Documento\n\nTesto.", second.Text);
        Assert.Equal(2, model.Calls);
        foreach (var request in model.Requests)
        {
            var user = Assert.Single(request, m => m.Role == ChatRole.User);
            Assert.Equal(messages[0].Text, user.Text);
            Assert.DoesNotContain(request, m => m.Role == ChatRole.Assistant); // nessuna history
        }

        var options = model.Options[0];
        Assert.Equal(DocumentAgent.Instructions, options.Instructions ?? string.Join("\n", model.Requests[0].Where(m => m.Role == ChatRole.System).Select(m => m.Text)));
        Assert.True(options.Tools is null or { Count: 0 }); // nessuno strumento: non può modificare nulla
        Assert.Null(options.ResponseFormat as ChatResponseFormatJson); // Markdown libero
        Assert.Equal("fake", options.ModelId);
        Assert.Equal(0.2f, options.Temperature);
        Assert.Equal(DocumentAgent.AgentId, agent.Id);
        Assert.Equal(DocumentAgent.AgentName, agent.Name);
    }

    [Fact]
    public async Task A_confirmed_content_above_the_compaction_trigger_is_never_summarized_and_reaches_the_model_whole()
    {
        // Il contenuto supera di molto la soglia di compattazione (60) ma sta nel budget (2.900): un CompactionProvider lo avrebbe
        // riassunto, il DocumentAgent no. Il modello riceve esattamente lo stesso messaggio, con una sola chiamata.
        var content = Input();
        var long_ = content.Content with
        {
            Requirements =
            [
                .. Enumerable.Range(0, 12).Select(i => new AnalysisItem
                {
                    Id = Guid.NewGuid(),
                    Text = $"Requisito {i} del portale con più parole per superare la soglia di compattazione prevista",
                    Classification = AnalysisClassification.Assumed,
                    EvidenceRefs = [],
                }),
            ],
        };
        var input = Input(long_);
        var messages = DocumentAgent.BuildMessages(input);
        var tokens = (await Task.WhenAll(messages.Select(m => Words(m.Text, Ct).AsTask()))).Sum() + (await Words(DocumentAgent.Instructions, Ct));
        Assert.InRange(tokens, 61, 2_900);
        var model = new FakeModel("# Documento");

        await Agent(model, Options(window: 3_000)).CreateAgent().RunAsync(messages, cancellationToken: Ct);

        Assert.Equal(1, model.Calls); // nessuna chiamata di sintesi
        Assert.Equal(messages[0].Text, Assert.Single(model.Requests[0], m => m.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task A_confirmed_content_over_the_budget_fails_with_context_overflow_in_the_preflight_and_never_calls_the_model()
    {
        var model = new FakeModel("# Documento");
        var agent = Agent(model, Options(window: 600));
        var huge = Input().Content with
        {
            Requirements =
            [
                new AnalysisItem { Id = Guid.NewGuid(), Text = string.Join(' ', Enumerable.Repeat("parola", 800)), Classification = AnalysisClassification.Assumed, EvidenceRefs = [] },
            ],
        };
        var messages = DocumentAgent.BuildMessages(Input(huge));

        var preflight = await Assert.ThrowsAsync<ContextOverflowException>(async () => await agent.PreflightAsync(messages, Ct));
        var run = await Assert.ThrowsAsync<ContextOverflowException>(() => agent.CreateAgent().RunAsync(messages, cancellationToken: Ct)); // anche il client lo rifiuta

        Assert.Equal("context_overflow", preflight.Code);
        Assert.True(preflight.Tokens > preflight.BudgetTokens);
        Assert.Equal(preflight.BudgetTokens, run.BudgetTokens);
        Assert.False(ModelFailure.Classify(preflight).IsTransient); // errore permanente, non ritentato
        Assert.Equal(0, model.Calls); // nessuna chiamata, nessun documento parziale
    }

    [Fact]
    public async Task The_instructions_count_toward_the_budget_so_a_content_that_alone_would_fit_can_still_overflow()
    {
        var model = new FakeModel("# Documento");
        var instructionTokens = await Words(DocumentAgent.Instructions, Ct);
        var messages = DocumentAgent.BuildMessages(Input());
        var contentTokens = (await Task.WhenAll(messages.Select(m => Words(m.Text, Ct).AsTask()))).Sum();
        // finestra tale che il solo contenuto sta nel budget, ma contenuto + istruzioni no
        var window = contentTokens + 100 + (instructionTokens / 2);
        var agent = Agent(model, Options(window: window));

        await Assert.ThrowsAsync<ContextOverflowException>(async () => await agent.PreflightAsync(messages, Ct));
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_content_within_the_budget_passes_the_preflight()
    {
        var agent = Agent(new FakeModel("# Documento"));

        await agent.PreflightAsync(DocumentAgent.BuildMessages(Input()), Ct); // non lancia
    }

    /// <summary>LLM finto: risponde con un testo fisso (anche in streaming) e registra richieste e opzioni.</summary>
    private sealed class FakeModel(string reply) : IChatClient
    {
        public int Calls { get; private set; }

        public List<List<ChatMessage>> Requests { get; } = [];

        public List<ChatOptions> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Record(messages, options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Record(messages, options);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        }

        private void Record(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            Calls++;
            Requests.Add([.. messages]);
            Options.Add(options ?? new ChatOptions());
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
