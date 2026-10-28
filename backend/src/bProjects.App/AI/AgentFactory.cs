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
    private readonly ChatOptions? _summarizerOptions;

    public AgentFactory(AiOptions options, IChatClient model, TokenCounter count, ChatOptions? summarizerOptions = null)
    {
        _options = options;
        _model = model;
        _count = count;
        _summarizerOptions = summarizerOptions;
    }

    public static AgentFactory Create(AiOptions options, InternalModelClient client) =>
        new(options, client.ChatClient, async (text, ct) => await client.CountTokensAsync(text, ct), client.CreateChatOptions(temperature: 0));

    /// <summary>
    /// Client per chat e analisi. La soglia della strategia sottrae i token delle istruzioni; la history originale
    /// non è mai modificata: la sintesi vive solo nel contesto della chiamata.
    /// </summary>
    public async Task<IChatClient> CreateCompactingClientAsync(string instructions, CancellationToken cancellationToken = default)
    {
        var budget = ContextBudget.From(_options);
        var instructionTokens = await _count(instructions, cancellationToken);
        var monitor = new CompactionMonitor();
        var measured = new MeasuredTokens(_count);
        var messageTrigger = budget.MessageTriggerTokens(instructionTokens);

        // Soglia nativa (CompactionTrigger) sui token reali del modello; la strategia nativa riassume solo i gruppi più
        // vecchi necessari a rientrare e ne preserva almeno l'ultimo (il messaggio a cui rispondere): il minimo di default
        // del framework (8 gruppi) renderebbe incomprimibile una conversazione di pochi messaggi molto lunghi.
        // La strategia smette di riassumere quando il trigger non scatta più, ma la sintesi si aggiunge dopo: si lascia
        // sotto soglia lo spazio della sintesi (il suo tetto di output) perché il contesto finale resti entro il budget.
        var reserve = Math.Max(1, Math.Min(Math.Min(budget.InputBudgetTokens / 6, _options.MaxOutputTokens), messageTrigger / 2));
        var strategy = new SummarizationCompactionStrategy(
            new MonitoredSummarizerClient(_model, monitor, _summarizerOptions, reserve),
            index => measured.Included(index) > messageTrigger,
            minimumPreservedGroups: PreservedGroups,
            summarizationPrompt: $"{SummarizationPrompt} Lunghezza massima: circa {Math.Max(1, reserve / 4)} parole.",
            target: index => measured.Included(index) <= messageTrigger - reserve);

        return new ChatClientBuilder(_model)
            .Use(inner => new MeasuringChatClient(inner, measured))
            .UseAIContextProviders(new CompactionProvider(strategy))
            .Use(inner => new ContextBudgetChatClient(inner, budget, measured.CountAsync, monitor))
            .Build();
    }

    /// <summary>Gruppi finali sempre preservati dalla sintesi: il solo ultimo, cioè il messaggio PM/istruzione del turno.</summary>
    public const int PreservedGroups = 1;

    /// <summary>
    /// Prompt della sintesi nativa: il predefinito del framework chiede di omettere ciò che non è «chiave» e non nomina
    /// negazioni, esclusioni né identificativi di fonte, che invece qui non devono perdersi.
    /// </summary>
    public const string SummarizationPrompt =
        "Sei un riassuntore di conversazioni di raccolta requisiti. Produci una sintesi fedele e compatta dei messaggi forniti. "
        + "Conserva sempre, senza attenuarli né invertirli: fatti, requisiti, vincoli, decisioni e assunzioni; "
        + "negazioni ed esclusioni (ciò che è escluso, fuori perimetro o non richiesto, e ciò che il project manager non sa o non ha deciso); "
        + "conflitti e punti aperti; nomi, numeri, date e termini tecnici; "
        + "gli identificativi di messaggio (id, un UUID di 36 caratteri) e di sequenza, copiati per intero e mai abbreviati, con il ruolo del messaggio d'origine (ruolo=user oppure ruolo=assistant), accanto al fatto a cui si riferiscono "
        + "così che resti riconducibile al messaggio d'origine. "
        + "La sintesi deve essere molto più breve dell'originale (al massimo un quarto della sua lunghezza): raggruppa gli elementi simili o ripetitivi "
        + "in poche frasi, senza elencarli uno per uno, ma non omettere mai negazioni, esclusioni, decisioni, nomi e identificativi. "
        + "Non aggiungere informazioni, interpretazioni o stime. Rispondi solo con la sintesi.";

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
