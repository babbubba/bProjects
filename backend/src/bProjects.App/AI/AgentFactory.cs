#pragma warning disable MAAI001 // Compaction: API sperimentale MAF 1.26.0, adottata di proposito (research.md §6).

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>
/// Costruisce i client e gli agenti MAF sul solo modello interno. Chat e analisi montano la compattazione nativa
/// (CompactionProvider via UseAIContextProviders con SummarizationCompactionStrategy) seguita dal controllo finale
/// del budget; il DocumentAgent non ha history né provider di riduzione, solo il controllo preventivo (plan.md).
/// </summary>
public sealed class AgentFactory
{
    private readonly AiOptions _options;
    private readonly IChatClient _model;
    private readonly TokenCounter _count;

    public AgentFactory(AiOptions options, IChatClient model, TokenCounter count)
    {
        _options = options;
        _model = model;
        _count = count;
    }

    public static AgentFactory Create(AiOptions options, InternalModelClient client) =>
        new(options, client.ChatClient, async (text, ct) => await client.CountTokensAsync(text, ct));

    /// <summary>
    /// Client per chat e analisi. La soglia della strategia sottrae i token delle istruzioni; la history originale
    /// non è mai modificata: la sintesi vive solo nel contesto della chiamata.
    /// </summary>
    public async Task<IChatClient> CreateCompactingClientAsync(string instructions, CancellationToken cancellationToken = default)
    {
        var budget = ContextBudget.From(_options);
        var instructionTokens = await _count(instructions, cancellationToken);
        var monitor = new CompactionMonitor();

        var strategy = new SummarizationCompactionStrategy(
            new MonitoredSummarizerClient(_model, monitor),
            CompactionTriggers.TokensExceed(budget.MessageTriggerTokens(instructionTokens)));

        return new ChatClientBuilder(_model)
            .UseAIContextProviders(new CompactionProvider(strategy))
            .Use(inner => new ContextBudgetChatClient(inner, budget, _count, monitor))
            .Build();
    }

    /// <summary>Client del DocumentAgent: nessuna compattazione, fallisce con context_overflow prima della chiamata.</summary>
    public IChatClient CreateDocumentClient() =>
        new ChatClientBuilder(_model)
            .Use(inner => new ContextBudgetChatClient(inner, ContextBudget.From(_options), _count, monitor: null))
            .Build();

    /// <summary>Preflight del DocumentAgent: istruzioni e messaggi dentro il budget di input, altrimenti context_overflow prima della chiamata.</summary>
    public ValueTask EnsureDocumentWithinBudgetAsync(IEnumerable<ChatMessage> messages, string instructions, CancellationToken cancellationToken = default) =>
        ContextBudget.From(_options).EnsureWithinAsync(messages, instructions, _count, cancellationToken);

    public async Task<ChatClientAgent> CreateCompactingAgentAsync(
        string name, string instructions, CancellationToken cancellationToken = default) =>
        new(await CreateCompactingClientAsync(instructions, cancellationToken), instructions: instructions, name: name);
}
