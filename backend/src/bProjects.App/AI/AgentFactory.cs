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
    private readonly ModelRuntimeMonitor? _runtime;
    private readonly TokenCounter _count;
    private readonly ChatOptions? _summarizerOptions;
    private readonly DocumentPreflight _preflight;

    /// <summary>
    /// Con <paramref name="runtime"/> ogni chiamata al modello (anche la sintesi) passa dal controllo di coerenza della finestra.
    /// Sotto il controllo, ogni richiesta realmente inviata è registrata con la propria fase (<see cref="InferenceCaptureChatClient"/>).
    /// </summary>
    public AgentFactory(
        AiOptions options, IChatClient model, TokenCounter count, ChatOptions? summarizerOptions = null, ModelRuntimeMonitor? runtime = null,
        IPayloadMeter? meter = null, PayloadCalibration? calibration = null)
    {
        _preflight = new DocumentPreflight(options, runtime, meter, calibration);
        _options = options;
        _model = model;
        _runtime = runtime;
        _count = count;
        _summarizerOptions = summarizerOptions;
    }

    /// <summary>
    /// Modello per una fase: guardia della finestra e del modello esposto (se c'è il monitor) sopra la cattura della telemetria,
    /// sopra il timeout della fase, sopra il client reale.
    /// </summary>
    private IChatClient Model(AiPhase phase, TokenCounter? count)
    {
        var timed = new PhaseTimeoutChatClient(_model, phase, TimeSpan.FromSeconds(_options.ForPhase(phase).RequestTimeoutSeconds));
        var capture = new InferenceCaptureChatClient(timed, phase, _options, _runtime, count);
        return _runtime is null ? capture : new ModelRuntimeGuardChatClient(capture, _runtime, phase);
    }

    /// <summary>
    /// Factory di produzione. <paramref name="calibration"/> (da <c>Documents:Calibration</c>) è la sola origine ammessa di un limite superiore del
    /// payload documentale quando il runtime non espone template o tokenizer; senza, quel ramo resta <c>runtime_unverified</c>.
    /// </summary>
    public static AgentFactory Create(AiOptions options, InternalModelClient client, ModelRuntimeMonitor? runtime = null, PayloadCalibration? calibration = null) =>
        new(options, client.ChatClient, async (text, ct) => await client.CountTokensAsync(text, ct),
            client.CreateChatOptions(temperature: 0, phase: AiPhase.Compaction), runtime, meter: client, calibration: calibration);

    /// <summary>Client per la chat (budget della fase Chat).</summary>
    public Task<IChatClient> CreateCompactingClientAsync(string instructions, CancellationToken cancellationToken = default) =>
        CreateCompactingClientAsync(instructions, AiPhase.Chat, cancellationToken);

    /// <summary>
    /// Client per chat e analisi con il budget della <paramref name="phase"/>. La soglia della strategia sottrae i token delle
    /// istruzioni; la history originale non è mai modificata: la sintesi vive solo nel contesto della chiamata.
    /// </summary>
    public async Task<IChatClient> CreateCompactingClientAsync(string instructions, AiPhase phase, CancellationToken cancellationToken = default)
    {
        var budget = ContextBudget.From(_options, phase);
        var instructionTokens = await _count(instructions, cancellationToken);
        var monitor = new CompactionMonitor();
        var measured = new MeasuredTokens(_count);
        var messageTrigger = budget.MessageTriggerTokens(instructionTokens);

        // Soglia nativa (CompactionTrigger) sui token reali del modello; la strategia nativa riassume solo i gruppi più
        // vecchi necessari a rientrare e ne preserva almeno l'ultimo (il messaggio a cui rispondere): il minimo di default
        // del framework (8 gruppi) renderebbe incomprimibile una conversazione di pochi messaggi molto lunghi.
        // La strategia smette di riassumere quando il trigger non scatta più, ma la sintesi si aggiunge dopo: si lascia
        // sotto soglia lo spazio della sintesi (il suo tetto di output) perché il contesto finale resti entro il budget.
        // Il tetto di output della sintesi è quello della fase Compaction, lo stesso che MonitoredSummarizerClient impone alla richiesta.
        var reserve = SummaryReserve(budget, messageTrigger);
        var strategy = new SummarizationCompactionStrategy(
            new MonitoredSummarizerClient(Model(AiPhase.Compaction, count: null), monitor, _summarizerOptions, reserve),
            index => measured.Included(index) > messageTrigger,
            minimumPreservedGroups: PreservedGroups,
            summarizationPrompt: $"{SummarizationPrompt} Lunghezza massima: circa {Math.Max(1, reserve / 4)} parole.",
            target: index => measured.Included(index) <= messageTrigger - reserve);

        return new ChatClientBuilder(Model(phase, measured.CountAsync))
            .Use(inner => new MeasuringChatClient(inner, measured))
            .UseAIContextProviders(new CompactionProvider(strategy))
            .Use(inner => new ContextBudgetChatClient(inner, budget, measured.CountAsync, monitor))
            .Build();
    }

    /// <summary>Spazio lasciato sotto la soglia per la sintesi: il suo tetto di output, entro un sesto del budget e metà della soglia.</summary>
    private int SummaryReserve(ContextBudget budget, int messageTrigger) =>
        Math.Max(1, Math.Min(Math.Min(budget.InputBudgetTokens / 6, _options.ForPhase(AiPhase.Compaction).MaxOutputTokens), messageTrigger / 2));

    /// <summary>Opzioni del modello interno (budget, tetti di output e fasi) con cui sono costruiti client e agenti.</summary>
    public AiOptions Options => _options;

    /// <summary>Token di un testo con lo stesso contatore delle chiamate (tokenizer del modello).</summary>
    public ValueTask<int> CountAsync(string text, CancellationToken cancellationToken = default) => _count(text, cancellationToken);

    /// <summary>
    /// Prevede, con le stesse regole del client compattante di <paramref name="phase"/>, se una chiamata con queste istruzioni e questi
    /// messaggi (l'ultimo sempre preservato) resta entro il budget: senza compattazione, oppure riassumendo i gruppi più vecchi in una
    /// sola richiesta di sintesi che a sua volta stia nel budget della fase Compaction. False = la chiamata singola non è eseguibile
    /// senza perdere informazioni o fallire (compaction_failed/context_overflow): il chiamante deve suddividere il lavoro.
    /// </summary>
    public async ValueTask<bool> FitsWithCompactionAsync(
        string instructions, IReadOnlyList<int> messageTokens, AiPhase phase, CancellationToken cancellationToken = default)
    {
        var budget = ContextBudget.From(_options, phase);
        var instructionTokens = await _count(instructions, cancellationToken);
        var messageTrigger = budget.MessageTriggerTokens(instructionTokens);
        var total = messageTokens.Sum();
        if (total <= messageTrigger && instructionTokens + total <= budget.InputBudgetTokens)
        {
            return true;
        }

        // Come la strategia nativa: si riassumono i messaggi più vecchi, interi, finché il resto scende sotto soglia meno la riserva.
        var target = messageTrigger - SummaryReserve(budget, messageTrigger);
        var kept = total;
        var summarized = 0;
        for (var i = 0; i < messageTokens.Count - PreservedGroups && kept > target; i++)
        {
            kept -= messageTokens[i];
            summarized += messageTokens[i];
        }

        if (kept > target)
        {
            return false; // l'ultimo messaggio da solo supera la soglia: nessuna sintesi lo fa rientrare
        }

        var summarizer = ContextBudget.From(_options, AiPhase.Compaction);
        return summarized + await _count(SummarizationPrompt, cancellationToken) <= summarizer.InputBudgetTokens;
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

    /// <summary>Preflight documentale per modello/fase (finestra misurata, tokenizer e template del runtime, payload completo).</summary>
    public DocumentPreflight Preflight => _preflight;

    /// <summary>
    /// Client di un agente batch documentale (T039): nessuna history, compattazione né sintesi; ogni chiamata passa dal preflight del
    /// payload effettivo sul profilo verificato del modello/fase (<paramref name="expectedFingerprint"/> = impronta congelata dal
    /// manifest). Runtime, finestra o tokenizer non verificati: <see cref="RuntimeUnverifiedException"/> prima di ogni richiesta.
    /// </summary>
    public IChatClient CreateBatchClient(AiPhase phase, string? expectedFingerprint = null)
    {
        var timed = new PhaseTimeoutChatClient(_model, phase, TimeSpan.FromSeconds(_options.ForPhase(phase).RequestTimeoutSeconds));
        // Composizione per categoria (anche document_sources) sul payload realmente inviato, con il tokenizer del runtime.
        var capture = new InferenceCaptureChatClient(timed, phase, _options, _runtime, _count);
        return new DocumentPayloadChatClient(capture, _preflight, phase, expectedFingerprint);
    }

    /// <summary>
    /// Agente batch: istruzioni della fase, nessun provider di history/compattazione e nessuno strumento. Ogni batch gira in una
    /// sessione nuova (<see cref="RunBatchAsync"/>): tre batch entro budget ma oltre la finestra cumulativa non si accumulano.
    /// </summary>
    public ChatClientAgent CreateBatchAgent(string name, string id, string instructions, ChatOptions chatOptions, AiPhase phase, string? expectedFingerprint = null)
    {
        var options = chatOptions.Clone();
        options.Instructions = instructions;
        return new ChatClientAgent(CreateBatchClient(phase, expectedFingerprint), new ChatClientAgentOptions { Id = id, Name = name, ChatOptions = options });
    }

    /// <summary>Esegue un batch in una sessione nativa nuova e vuota: nessun turno precedente è implicito.</summary>
    public static async Task<AgentResponse> RunBatchAsync(ChatClientAgent agent, IEnumerable<ChatMessage> input, CancellationToken cancellationToken = default)
    {
        var session = await agent.CreateSessionAsync(cancellationToken);
        return await agent.RunAsync(input, session, cancellationToken: cancellationToken);
    }

    /// <summary>Client del DocumentAgent: nessuna compattazione, fallisce con context_overflow prima della chiamata.</summary>
    public IChatClient CreateDocumentClient()
    {
        // Stessa cache per il controllo finale e per la composizione della richiesta: ogni testo è contato una sola volta.
        var measured = new MeasuredTokens(_count);
        return new ChatClientBuilder(Model(AiPhase.Document, measured.CountAsync))
            .Use(inner => new ContextBudgetChatClient(inner, ContextBudget.From(_options, AiPhase.Document), measured.CountAsync, monitor: null))
            .Build();
    }

    /// <summary>Preflight del DocumentAgent: istruzioni e messaggi dentro il budget di input della fase Document, altrimenti context_overflow prima della chiamata.</summary>
    public ValueTask EnsureDocumentWithinBudgetAsync(IEnumerable<ChatMessage> messages, string instructions, CancellationToken cancellationToken = default) =>
        ContextBudget.From(_options, AiPhase.Document).EnsureWithinAsync(messages, instructions, _count, cancellationToken);

    /// <summary>
    /// Client dell'estrattore delle evidenze: come quello del DocumentAgent (nessuna history né compattazione: un solo messaggio costruito
    /// dal server), con il budget, il modello, il timeout e la telemetria della fase Extraction.
    /// </summary>
    public IChatClient CreateExtractionClient()
    {
        var measured = new MeasuredTokens(_count);
        return new ChatClientBuilder(Model(AiPhase.Extraction, measured.CountAsync))
            .Use(inner => new ContextBudgetChatClient(inner, ContextBudget.From(_options, AiPhase.Extraction), measured.CountAsync, monitor: null))
            .Build();
    }

    /// <summary>Vero se istruzioni e messaggi rientrano nel budget di input della fase Extraction (conteggio del tokenizer reale).</summary>
    public async ValueTask<bool> FitsExtractionBudgetAsync(IEnumerable<ChatMessage> messages, string instructions, CancellationToken cancellationToken = default)
    {
        try
        {
            await ContextBudget.From(_options, AiPhase.Extraction).EnsureWithinAsync(messages, instructions, _count, cancellationToken);
            return true;
        }
        catch (ContextOverflowException)
        {
            return false;
        }
    }

    /// <summary>Come <see cref="FitsExtractionBudgetAsync"/>, ma fuori budget lancia <see cref="ContextOverflowException"/> (context_overflow).</summary>
    public ValueTask EnsureExtractionWithinBudgetAsync(IEnumerable<ChatMessage> messages, string instructions, CancellationToken cancellationToken = default) =>
        ContextBudget.From(_options, AiPhase.Extraction).EnsureWithinAsync(messages, instructions, _count, cancellationToken);

    public async Task<ChatClientAgent> CreateCompactingAgentAsync(
        string name, string instructions, CancellationToken cancellationToken = default) =>
        new(await CreateCompactingClientAsync(instructions, cancellationToken), instructions: instructions, name: name);
}

/// <summary>
/// Timeout della singola chiamata della fase (<c>Ai:Phases:{fase}:RequestTimeoutSeconds</c>): il client HTTP condiviso ha il tetto
/// della fase più lunga. Lo scadere è un <see cref="TaskCanceledException"/> con causa <see cref="TimeoutException"/>, come il
/// timeout di HttpClient, quindi il dispatcher lo classifica model_timeout (transitorio); la cancellazione del chiamante resta sua.
/// </summary>
internal sealed class PhaseTimeoutChatClient(IChatClient inner, AiPhase phase, TimeSpan timeout) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var cts = Start(cancellationToken);
        try
        {
            return await base.GetResponseAsync(messages, options, cts.Token);
        }
        catch (OperationCanceledException e) when (Expired(cts, cancellationToken))
        {
            throw Timeout(e);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var cts = Start(cancellationToken);
        await using var updates = base.GetStreamingResponseAsync(messages, options, cts.Token).GetAsyncEnumerator(cts.Token);
        while (true)
        {
            try
            {
                if (!await updates.MoveNextAsync())
                {
                    yield break;
                }
            }
            catch (OperationCanceledException e) when (Expired(cts, cancellationToken))
            {
                throw Timeout(e);
            }

            yield return updates.Current;
        }
    }

    private CancellationTokenSource Start(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
        {
            cts.CancelAfter(timeout);
        }

        return cts;
    }

    private static bool Expired(CancellationTokenSource cts, CancellationToken caller) => cts.IsCancellationRequested && !caller.IsCancellationRequested;

    private TaskCanceledException Timeout(OperationCanceledException e) =>
        new($"Nessuna risposta del modello entro {timeout.TotalSeconds:0} s (fase {phase}).", new TimeoutException(e.Message, e));
}
