#pragma warning disable MAAI001 // Compaction: API sperimentale MAF 1.26.0, adottata di proposito (research.md §6).

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

public enum InferenceCallState
{
    Running,
    Completed,
    Failed,
    Interrupted,
}

/// <summary>Una categoria del contesto realmente inviato, con i token del tokenizer del modello e i messaggi che la compongono.</summary>
public sealed record CompositionItem(string Key, int Tokens, int? MessageCount);

/// <summary>
/// Composizione della richiesta finale: istruzioni e messaggi effettivamente inviati, classificati e contati con il tokenizer reale.
/// Non è un breakdown esatto del prompt: template e token speciali del runtime restano fuori (quota non attribuita).
/// </summary>
public sealed record ContextComposition(IReadOnlyList<CompositionItem> Items)
{
    public const string SystemInstructions = "system_instructions";
    public const string UserMessages = "user_messages";
    public const string AssistantMessages = "assistant_messages";
    public const string DerivedSummary = "derived_summary";
    public const string AnalysisStructured = "analysis_structured";

    public int AttributedTokens => Items.Sum(i => i.Tokens);

    /// <summary>Token dei soli messaggi (istruzioni escluse): il contesto operativo dopo l'eventuale compattazione.</summary>
    public int MessageTokens => Items.Where(i => i.Key != SystemInstructions).Sum(i => i.Tokens);
}

/// <summary>
/// Esito tecnico di una richiesta reale al modello, raccolto in memoria da <see cref="InferenceCaptureChatClient"/>: solo numeri e
/// metadati (mai testi, prompt o ragionamento). I token vengono soltanto dall'<c>usage</c> del runtime: assenti = null, mai stimati.
/// </summary>
public sealed class InferenceCallRecord
{
    public Guid Id { get; } = Guid.NewGuid();

    public required AiPhase Phase { get; init; }

    public required string ModelId { get; set; }

    public required DateTimeOffset StartedAt { get; init; }

    public long? DurationMs { get; private set; }

    /// <summary>ctx-size per slot verificato sul runtime (Online) al momento della chiamata; null se non verificato.</summary>
    public int? ContextWindowTokens { get; init; }

    /// <summary>max_tokens inviato: limite richiesto, che llama.cpp non conferma.</summary>
    public int? MaxOutputTokensRequested { get; init; }

    public int? ServerDefaultNPredict { get; init; }

    public bool ReasoningRequested { get; init; }

    /// <summary>on/off solo se verificabile dalla risposta senza esporne il contenuto, altrimenti unknown.</summary>
    public string ReasoningEffective { get; private set; } = "unknown";

    public int? PromptTokens { get; private set; }

    public int? CompletionTokens { get; private set; }

    public int? TotalTokens { get; private set; }

    public int? CachedTokens { get; private set; }

    public string? FinishReason { get; private set; }

    public InferenceCallState State { get; private set; } = InferenceCallState.Running;

    public ContextComposition? Composition { get; internal set; }

    /// <summary>Configurazione numerica effettiva della chiamata (JSON, senza endpoint né credenziali): la metrica storica non cambia con la config.</summary>
    public required string ConfigJson { get; init; }

    internal void ObserveUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        PromptTokens = ToInt(usage.InputTokenCount) ?? PromptTokens;
        CompletionTokens = ToInt(usage.OutputTokenCount) ?? CompletionTokens;
        TotalTokens = ToInt(usage.TotalTokenCount) ?? TotalTokens;
        CachedTokens = ToInt(usage.CachedInputTokenCount) ?? CachedTokens;
        if (usage.ReasoningTokenCount is > 0)
        {
            _reasoningSeen = true;
        }
    }

    private bool _reasoningSeen;
    private bool _thinkTagInText;

    internal void ObserveContents(IEnumerable<AIContent> contents)
    {
        foreach (var content in contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                    _reasoningSeen = true; // solo la presenza: il testo non viene mai letto né conservato
                    break;
                case TextContent text when text.Text?.Contains("<think>", StringComparison.Ordinal) == true:
                    _thinkTagInText = true; // ragionamento non separato dal runtime: non verificabile
                    break;
                case UsageContent usage:
                    ObserveUsage(usage.Details);
                    break;
            }
        }
    }

    internal void ObserveFinish(ChatFinishReason? reason, string? modelId)
    {
        if (reason is { } r)
        {
            FinishReason = r.Value;
        }

        if (string.IsNullOrEmpty(ModelId) && !string.IsNullOrEmpty(modelId))
        {
            ModelId = modelId;
        }
    }

    internal void Complete(Stopwatch clock)
    {
        DurationMs = clock.ElapsedMilliseconds;
        State = InferenceCallState.Completed;
        ReasoningEffective = _reasoningSeen ? "on" : _thinkTagInText ? "unknown" : "off";
    }

    /// <summary>Errore o cancellazione: nessun token attribuito oltre a quelli eventualmente riportati dal runtime.</summary>
    internal void Fail(Exception error, CancellationToken cancellationToken, Stopwatch clock)
    {
        DurationMs = clock.ElapsedMilliseconds;
        State = error is OperationCanceledException && cancellationToken.IsCancellationRequested
            ? InferenceCallState.Interrupted
            : InferenceCallState.Failed;
        ReasoningEffective = _reasoningSeen ? "on" : "unknown";
    }

    /// <summary>Chiamata abbandonata dal chiamante prima della fine (o ancora in corso al momento della scrittura).</summary>
    internal void Interrupt(long? durationMs = null)
    {
        if (State != InferenceCallState.Running)
        {
            return;
        }

        DurationMs ??= durationMs;
        State = InferenceCallState.Interrupted;
        ReasoningEffective = _reasoningSeen ? "on" : "unknown";
    }

    private static int? ToInt(long? value) => value is { } v and >= 0 and <= int.MaxValue ? (int)v : null;
}

/// <summary>Storico originale entrato nella pipeline di compattazione: messaggi e somma dei token misurati, prima di ogni sintesi.</summary>
public sealed record InferenceHistory(int MessageCount, int? OriginalTokens);

/// <summary>
/// Contesto ambientale di un tentativo di esecuzione (turno o run): lo crea il dispatcher per ogni lavoro preso in carico e vi
/// confluiscono tutte le richieste al modello di quel tentativo, sintesi della compattazione comprese (anche dentro il workflow
/// MAF, che eredita l'ExecutionContext). È solo memoria: le righe si scrivono con il commit del risultato o con il percorso di
/// fallimento esistente, sotto lo stesso controllo di lease/fencing.
/// </summary>
public sealed class InferenceScope
{
    private static readonly AsyncLocal<InferenceScope?> Ambient = new();

    private readonly object _gate = new();
    private readonly List<InferenceCallRecord> _calls = [];
    private InferenceHistory? _history;
    private int _summarized;
    private bool _written;

    public static InferenceScope? Current => Ambient.Value;

    /// <summary>Nuovo scope per il flusso corrente; il Dispose ripristina il precedente.</summary>
    public static IDisposable Begin()
    {
        var previous = Ambient.Value;
        Ambient.Value = new InferenceScope();
        return new Restore(previous);
    }

    public IReadOnlyList<InferenceCallRecord> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public InferenceHistory? History
    {
        get
        {
            lock (_gate)
            {
                return _history;
            }
        }
    }

    /// <summary>Messaggi consegnati alle sintesi riuscite di questo tentativo.</summary>
    public int SummarizedMessages => Volatile.Read(ref _summarized);

    /// <summary>True se ci sono chiamate non ancora scritte (le righe di un tentativo si scrivono una sola volta).</summary>
    public bool HasUnwritten
    {
        get
        {
            lock (_gate)
            {
                return !_written && _calls.Count > 0;
            }
        }
    }

    internal void Add(InferenceCallRecord record)
    {
        lock (_gate)
        {
            _calls.Add(record);
        }
    }

    internal void ObserveHistory(InferenceHistory history)
    {
        lock (_gate)
        {
            _history = history;
        }
    }

    internal void ObserveSummarized(int messages) => Interlocked.Add(ref _summarized, messages);

    /// <summary>Da chiamare solo dopo il commit della transazione che contiene le righe.</summary>
    public void MarkWritten()
    {
        lock (_gate)
        {
            _written = true;
        }
    }

    /// <summary>
    /// Chiamata che ha prodotto il risultato logico: l'ultima completata fuori dalla compattazione. Null se il tentativo non ne ha
    /// (es. ripresa da un checkpoint successivo alla chiamata del modello).
    /// </summary>
    public InferenceCallRecord? Final => Calls.LastOrDefault(c => c.Phase != AiPhase.Compaction && c.State == InferenceCallState.Completed);

    private sealed class Restore(InferenceScope? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>
/// Anello più interno sopra il modello (sotto il controllo della finestra, quindi solo richieste realmente inviate): per ogni
/// richiesta registra nello <see cref="InferenceScope"/> corrente fase, modello, durata, finestra verificata, max_tokens richiesto,
/// ragionamento e l'<c>usage</c> restituito dal runtime (in streaming dall'ultimo chunk, che l'SDK chiede con
/// <c>stream_options.include_usage</c>). Senza scope non fa nulla. Non legge né conserva testi: per la chiamata finale classifica
/// e conta istruzioni e messaggi inviati con il tokenizer del modello (<see cref="ContextComposition"/>).
/// </summary>
internal sealed class InferenceCaptureChatClient(
    IChatClient inner, AiPhase phase, AiOptions options, ModelRuntimeMonitor? runtime, TokenCounter? count) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, CancellationToken cancellationToken = default)
    {
        if (InferenceScope.Current is not { } scope)
        {
            return await base.GetResponseAsync(messages, chatOptions, cancellationToken);
        }

        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var record = Start(scope, chatOptions);
        var clock = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(list, chatOptions, cancellationToken);
        }
        catch (Exception e)
        {
            record.Fail(e, cancellationToken, clock);
            throw;
        }

        record.ObserveUsage(response.Usage);
        foreach (var message in response.Messages)
        {
            record.ObserveContents(message.Contents);
        }

        record.ObserveFinish(response.FinishReason, response.ModelId);
        record.Complete(clock);
        record.Composition = await ComposeAsync(list, chatOptions, cancellationToken);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (InferenceScope.Current is not { } scope)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
            {
                yield return update;
            }

            yield break;
        }

        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var record = Start(scope, chatOptions);
        var clock = Stopwatch.StartNew();
        try
        {
            await using var updates = base.GetStreamingResponseAsync(list, chatOptions, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                try
                {
                    if (!await updates.MoveNextAsync())
                    {
                        break;
                    }
                }
                catch (Exception e)
                {
                    record.Fail(e, cancellationToken, clock);
                    throw;
                }

                var update = updates.Current;
                record.ObserveContents(update.Contents);
                record.ObserveFinish(update.FinishReason, update.ModelId);
                yield return update;
            }

            record.Complete(clock);
            record.Composition = await ComposeAsync(list, chatOptions, cancellationToken);
        }
        finally
        {
            // Il consumatore ha smesso di leggere prima della fine dello stream: la chiamata non è conclusa.
            record.Interrupt(clock.ElapsedMilliseconds);
        }
    }

    private InferenceCallRecord Start(InferenceScope scope, ChatOptions? chatOptions)
    {
        var settings = options.ForPhase(phase);
        var snapshot = runtime?.Current;
        var online = snapshot is { Status: ModelRuntimeStatus.Online };
        var record = new InferenceCallRecord
        {
            Phase = phase,
            ModelId = chatOptions?.ModelId ?? "",
            StartedAt = DateTimeOffset.UtcNow,
            ContextWindowTokens = online ? snapshot!.ContextWindowTokens : null,
            ServerDefaultNPredict = online ? snapshot!.ServerDefaultNPredict : null,
            MaxOutputTokensRequested = chatOptions?.MaxOutputTokens,
            ReasoningRequested = RequestedThinking(chatOptions),
            ConfigJson = JsonSerializer.Serialize(new
            {
                configuredContextWindowTokens = options.ContextWindowTokens,
                phaseMaxOutputTokens = settings.MaxOutputTokens,
                safetyMarginTokens = options.SafetyMarginTokens,
                inputBudgetTokens = settings.InputBudgetTokens,
                compactionTriggerTokens = options.CompactionTriggerTokens > 0 ? options.CompactionTriggerTokens : settings.InputBudgetTokens,
                enableThinking = settings.EnableThinking,
                temperature = chatOptions?.Temperature,
                structuredOutput = chatOptions?.ResponseFormat is ChatResponseFormatJson,
                requestTimeoutSeconds = options.RequestTimeoutSeconds,
            }),
        };
        scope.Add(record);
        return record;
    }

#pragma warning disable SCME0001 // Lettura della Patch: il flag enable_thinking viaggia solo in chat_template_kwargs.
    /// <summary>enable_thinking effettivamente inviato (chat_template_kwargs prodotti da <c>RawRepresentationFactory</c>); false se assente.</summary>
    private bool RequestedThinking(ChatOptions? chatOptions)
    {
        if (chatOptions?.RawRepresentationFactory?.Invoke(this) is not OpenAI.Chat.ChatCompletionOptions raw
            || !raw.Patch.TryGetJson("$.chat_template_kwargs"u8, out var json))
        {
            return false;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("enable_thinking", out var value)
            && value.ValueKind == JsonValueKind.True;
    }
#pragma warning restore SCME0001

    /// <summary>
    /// Composizione della richiesta inviata (solo fuori dalla compattazione). Un guasto del tokenizer non tocca l'inferenza già
    /// riuscita: la composizione resta assente.
    /// </summary>
    private async Task<ContextComposition?> ComposeAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? chatOptions, CancellationToken ct)
    {
        if (count is null || phase == AiPhase.Compaction)
        {
            return null;
        }

        try
        {
            var totals = new Dictionary<string, (int Tokens, int Messages)>();
            if (!string.IsNullOrWhiteSpace(chatOptions?.Instructions))
            {
                totals[ContextComposition.SystemInstructions] = (await count(chatOptions.Instructions, ct), 0);
            }

            foreach (var message in messages)
            {
                var text = message.Text;
                if (text.Length == 0 || Classify(message) is not { } key)
                {
                    continue; // contenuti non testuali: restano nella quota non attribuita
                }

                var tokens = await count(text, ct);
                var current = totals.GetValueOrDefault(key);
                totals[key] = (current.Tokens + tokens, current.Messages + 1);
            }

            return new ContextComposition([.. totals.Select(t => new CompositionItem(
                t.Key, t.Value.Tokens, t.Key == ContextComposition.SystemInstructions ? null : t.Value.Messages))]);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Categoria di un messaggio inviato. La sintesi della compattazione nativa è l'assistant marcato da MAF con
    /// <see cref="CompactionMessageGroup.SummaryPropertyKey"/>; nel DocumentAgent l'unico messaggio utente è la revisione confermata
    /// (JSON AnalysisContent completo con gli id fidati del server).
    /// </summary>
    private string? Classify(ChatMessage message)
    {
        if (message.AdditionalProperties?.TryGetValue(CompactionMessageGroup.SummaryPropertyKey, out var summary) == true
            && summary is true or JsonElement { ValueKind: JsonValueKind.True })
        {
            return ContextComposition.DerivedSummary;
        }

        if (message.Role == ChatRole.System)
        {
            return ContextComposition.SystemInstructions;
        }

        if (message.Role == ChatRole.User)
        {
            return phase == AiPhase.Document ? ContextComposition.AnalysisStructured : ContextComposition.UserMessages;
        }

        return message.Role == ChatRole.Assistant ? ContextComposition.AssistantMessages : null;
    }
}
