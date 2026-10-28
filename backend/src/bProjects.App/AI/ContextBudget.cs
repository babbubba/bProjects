using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>Conta i token di un testo con il tokenizer/stimatore calibrato sul modello (es. POST /tokenize).</summary>
public delegate ValueTask<int> TokenCounter(string text, CancellationToken cancellationToken);

/// <summary>Il contesto finale (istruzioni + payload) resta fuori dal budget di input anche dopo la compattazione.</summary>
public sealed class ContextOverflowException(int tokens, int budgetTokens)
    : InvalidOperationException($"Contesto di {tokens} token oltre il budget di input di {budgetTokens}.")
{
    public const string ErrorCode = "context_overflow";

    public string Code => ErrorCode;

    public int Tokens { get; } = tokens;

    public int BudgetTokens { get; } = budgetTokens;
}

/// <summary>La sintesi nativa è fallita in modo non transitorio o ha prodotto un risultato non valido.</summary>
public sealed class CompactionFailedException(string reason, Exception? inner = null)
    : InvalidOperationException($"Compattazione non riuscita: {reason}.", inner)
{
    public const string ErrorCode = "compaction_failed";

    public string Code => ErrorCode;
}

/// <summary>
/// Budget in token del modello (contracts/ai-workflow.md §7): input = finestra - output - margine, con soglia di
/// attivazione della compattazione fra 1 e il budget. Le istruzioni contano come qualunque altro payload.
/// </summary>
public readonly record struct ContextBudget(int InputBudgetTokens, int TriggerTokens)
{
    public static ContextBudget From(AiOptions options) => new(options.InputBudgetTokens, options.EffectiveCompactionTriggerTokens);

    /// <summary>Soglia per i soli messaggi, tolti i token delle istruzioni già riservati nell'input.</summary>
    public int MessageTriggerTokens(int instructionTokens) => Math.Max(1, TriggerTokens - instructionTokens);

    /// <summary>Conteggio finale di istruzioni e messaggi; <see cref="ContextOverflowException"/> se oltre il budget.</summary>
    public async ValueTask EnsureWithinAsync(
        IEnumerable<ChatMessage> messages, string? instructions, TokenCounter count, CancellationToken cancellationToken)
    {
        var total = 0;
        if (!string.IsNullOrEmpty(instructions))
        {
            total += await count(instructions, cancellationToken);
        }

        foreach (var message in messages)
        {
            var text = message.Text;
            if (text.Length > 0)
            {
                total += await count(text, cancellationToken);
            }
        }

        if (total > InputBudgetTokens)
        {
            throw new ContextOverflowException(total, InputBudgetTokens);
        }
    }
}

/// <summary>
/// Raccoglie l'esito del summarizer nativo. SummarizationCompactionStrategy assorbe gli errori del proprio client
/// e prosegue senza compattare: qui li si rende espliciti prima della chiamata al modello.
/// </summary>
public sealed class CompactionMonitor
{
    private Exception? _failure;

    internal void Record(Exception failure) => _failure = failure;

    /// <summary>Errori transitori (rete, 5xx) tornano così come sono: il dispatcher li ritenta; gli altri diventano compaction_failed.</summary>
    internal void ThrowIfFailed()
    {
        var failure = Interlocked.Exchange(ref _failure, null);
        if (failure is null)
        {
            return;
        }

        if (failure is CompactionFailedException || ModelFailure.Classify(failure).IsTransient)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        throw new CompactionFailedException("errore del modello di sintesi", failure);
    }
}

/// <summary>Client usato dalla SummarizationCompactionStrategy: registra errori e sintesi vuote.</summary>
internal sealed class MonitoredSummarizerClient(IChatClient inner, CompactionMonitor monitor) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            if (string.IsNullOrWhiteSpace(response.Text))
            {
                var empty = new CompactionFailedException("sintesi vuota");
                monitor.Record(empty);
                throw empty;
            }

            return response;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            monitor.Record(e);
            throw;
        }
    }
}

/// <summary>
/// Ultimo anello prima del modello: dopo la compattazione conta istruzioni e payload effettivi e fallisce con
/// <see cref="ContextOverflowException"/> invece di inviare un contesto fuori budget (nessun taglio nascosto).
/// </summary>
internal sealed class ContextBudgetChatClient(IChatClient inner, ContextBudget budget, TokenCounter count, CompactionMonitor? monitor)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = await CheckAsync(messages, options, cancellationToken);
        return await base.GetResponseAsync(list, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = await CheckAsync(messages, options, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
        {
            yield return update;
        }
    }

    private async ValueTask<List<ChatMessage>> CheckAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        monitor?.ThrowIfFailed();
        var list = messages.ToList();
        await budget.EnsureWithinAsync(list, options?.Instructions, count, ct);
        return list;
    }
}
