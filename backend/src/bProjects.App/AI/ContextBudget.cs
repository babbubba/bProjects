#pragma warning disable MAAI001 // Compaction: API sperimentale MAF 1.26.0, adottata di proposito (research.md §6).

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI.Compaction;
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
/// Il budget è quello della fase (<see cref="AiOptions.ForPhase"/>): il tetto di output di fase riduce l'input.
/// </summary>
public readonly record struct ContextBudget(int InputBudgetTokens, int TriggerTokens)
{
    public static ContextBudget From(AiOptions options, AiPhase phase = AiPhase.Chat)
    {
        var input = options.ForPhase(phase).InputBudgetTokens;
        return new(input, options.CompactionTriggerTokens > 0 ? options.CompactionTriggerTokens : input);
    }

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

        if (failure is CompactionFailedException or ContextWindowMismatchException || ModelFailure.Classify(failure).IsTransient)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        throw new CompactionFailedException("errore del modello di sintesi", failure);
    }
}

/// <summary>Client usato dalla SummarizationCompactionStrategy: registra errori e sintesi vuote.</summary>
internal sealed class MonitoredSummarizerClient(
    IChatClient inner, CompactionMonitor monitor, ChatOptions? defaults, int maxSummaryTokens) : DelegatingChatClient(inner)
{
    public const string Request = "Produci ora la sintesi dei messaggi precedenti.";

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // La strategia nativa non passa opzioni: senza quelle dell'applicazione il runtime lascia attivo il ragionamento
            // nascosto e l'output senza tetto (sintesi lentissime). Il tetto di output è lo spazio riservato alla sintesi.
            var sent = options ?? defaults?.Clone() ?? new ChatOptions();
            sent.MaxOutputTokens = Math.Min(sent.MaxOutputTokens ?? maxSummaryTokens, maxSummaryTokens);

            // La richiesta di sintesi è sempre l'ultimo messaggio: i gruppi da riassumere possono finire con un assistant (il runtime lo
            // proseguirebbe) o con una nota utente a cui il modello risponderebbe imitando lo storico invece di riassumere.
            var list = messages.ToList();
            list.Add(new ChatMessage(ChatRole.User, Request));

            var response = await base.GetResponseAsync(list, sent, cancellationToken);
            if (string.IsNullOrWhiteSpace(response.Text))
            {
                var empty = new CompactionFailedException("sintesi vuota");
                monitor.Record(empty);
                throw empty;
            }

            if (response.FinishReason == ChatFinishReason.Length)
            {
                var cut = new CompactionFailedException("sintesi troncata dal limite di output");
                monitor.Record(cut);
                throw cut;
            }

            // Messaggi originali consegnati a una sintesi riuscita (il prompt di sistema della strategia non è un messaggio riassunto).
            InferenceScope.Current?.ObserveSummarized(messages.Count(m => m.Role != ChatRole.System));
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
/// Token reali (tokenizer del modello) dei testi di una chiamata. CompactionProvider costruisce il proprio indice senza
/// tokenizer e stima i token come byte UTF-8 / 4, che per il modello reale non è calibrato: la soglia di attivazione
/// legge invece questi conteggi, misurati in anticipo da <see cref="MeasuringChatClient"/>, così trigger e controllo finale
/// parlano la stessa unità. Un testo mai misurato (la sintesi derivata) ricade sulla stima nativa; il controllo finale resta
/// sempre sul conteggio reale.
/// </summary>
internal sealed class MeasuredTokens(TokenCounter count)
{
    private readonly ConcurrentDictionary<string, int> _byText = new(StringComparer.Ordinal);

    /// <summary>Conteggio con cache: ogni testo viene misurato una sola volta per client.</summary>
    public async ValueTask<int> CountAsync(string text, CancellationToken cancellationToken)
    {
        if (_byText.TryGetValue(text, out var known))
        {
            return known;
        }

        var tokens = await count(text, cancellationToken);
        _byText[text] = tokens;
        return tokens;
    }

    /// <summary>
    /// Misura i messaggi in ingresso e, se c'è uno <see cref="InferenceScope"/>, vi registra lo storico originale (sintesi derivate
    /// escluse): numero di messaggi e somma dei loro token reali, prima di qualunque compattazione.
    /// </summary>
    public async ValueTask MeasureAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var originals = 0;
        var tokens = 0;
        foreach (var message in messages)
        {
            var text = message.Text;
            var measured = text.Length > 0 ? await CountAsync(text, cancellationToken) : 0;
            if (message.AdditionalProperties?.ContainsKey(CompactionMessageGroup.SummaryPropertyKey) != true)
            {
                originals++;
                tokens += measured;
            }
        }

        InferenceScope.Current?.ObserveHistory(new InferenceHistory(originals, tokens));
    }

    /// <summary>Token dei messaggi non esclusi dall'indice di compattazione.</summary>
    public int Included(CompactionMessageIndex index) =>
        index.Groups.Where(g => !g.IsExcluded).Sum(g => g.Messages.Sum(Of));

    private int Of(ChatMessage message)
    {
        var text = message.Text;
        if (text.Length == 0)
        {
            return 0;
        }

        return _byText.TryGetValue(text, out var tokens) ? tokens : Encoding.UTF8.GetByteCount(text) / 4;
    }
}

/// <summary>Anello più esterno: misura i token reali dei messaggi prima che li veda il provider di compattazione.</summary>
internal sealed class MeasuringChatClient(IChatClient inner, MeasuredTokens measured) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        await measured.MeasureAsync(list, cancellationToken);
        return await base.GetResponseAsync(list, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        await measured.MeasureAsync(list, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
        {
            yield return update;
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

/// <summary>Come il runtime misura i token di un modello (contracts/ai-workflow.md §6): tokenizer e template sono per modello.</summary>
public interface IPayloadMeter
{
    /// <summary>Token del testo col tokenizer del modello indicato (POST /tokenize).</summary>
    Task<int> CountTokensAsync(string modelId, string text, CancellationToken cancellationToken);

    /// <summary>Prompt reso dal template del modello (POST /apply-template); lancia se il runtime non lo espone.</summary>
    Task<string> RenderChatTemplateAsync(
        string modelId, IReadOnlyList<(string Role, string Text)> messages, bool enableThinking, CancellationToken cancellationToken);
}

/// <summary>Qualità del conteggio del payload: misurato dal runtime, limite superiore con calibrazione identificata, oppure non disponibile.</summary>
public enum TokenCountQuality
{
    Measured,
    CalibratedUpperBound,
    Unavailable,
}

/// <summary>
/// Calibrazione identificata (template/runtime) che permette un limite superiore quando il runtime non espone il template applicato
/// o il tokenizer: framing per messaggio e fisso del template, e (senza tokenizer) un minimo di caratteri per token. Una semplice
/// stima senza calibrazione non è ammessa: il conteggio resta <see cref="TokenCountQuality.Unavailable"/>.
/// </summary>
public sealed record PayloadCalibration(string Id, int PerMessageFramingTokens, int FixedOverheadTokens, double? MinCharsPerToken = null);

/// <summary>Finestra, tokenizer o template del modello non verificabili (o conteggio non disponibile): nessuna chiamata documentale inviata.</summary>
public sealed class RuntimeUnverifiedException(string reason, bool transient = false)
    : InvalidOperationException($"Runtime del modello non verificato: {reason}.")
{
    public const string ErrorCode = "runtime_unverified";

    public string Code => ErrorCode;

    /// <summary>Server non raggiungibile: il tentativo successivo può riuscire. Finestra/tokenizer sconosciuti restano permanenti.</summary>
    public bool Transient { get; } = transient;
}

/// <summary>Il runtime non coincide più con l'impronta congelata dal manifest (modello, finestra, tokenizer, template): niente invio.</summary>
public sealed class RuntimeFingerprintMismatchException(string expected, string actual)
    : InvalidOperationException("L'impronta del runtime è cambiata rispetto a quella congelata.")
{
    public const string ErrorCode = "tokenizer_mismatch";

    public string Code => ErrorCode;

    public string Expected { get; } = expected;

    public string Actual { get; } = actual;
}

/// <summary>
/// Profilo del runtime documentale per modello e fase: W = min(tetto configurato, finestra misurata), O = output massimo totale
/// (ragionamento incluso: non si sottrae due volte), M = margine, B = W - O - M. L'impronta (modello, fase, W/O/M, ragionamento,
/// tokenizer e modalità del template) è ciò che il manifest congela.
/// </summary>
public sealed record DocumentRuntimeProfile(
    AiPhase Phase, string ModelId, int WindowTokens, int OutputTokens, int MarginTokens, bool Thinking,
    bool TemplateExposed, string? CalibrationId, string Fingerprint)
{
    public int InputBudgetTokens => WindowTokens - OutputTokens - MarginTokens;
}

/// <summary>Payload effettivo di una chiamata: istruzioni, messaggi, schema della risposta e strumenti (framing e template sono del runtime).</summary>
public sealed record DocumentPayload(
    AiPhase Phase, string Instructions, IReadOnlyList<ChatMessage> Messages, string? ResponseSchemaJson = null, string? ToolsJson = null);

/// <summary>Esito del conteggio del payload completo: token, qualità, budget e hash del payload (inputHash logico) con l'impronta del runtime.</summary>
public sealed record PayloadPreflight(DocumentRuntimeProfile Profile, int Tokens, TokenCountQuality Quality, string PayloadHash)
{
    public int InputBudgetTokens => Profile.InputBudgetTokens;

    /// <summary>Uguaglianza al budget ammessa; un conteggio non disponibile non è mai «entro budget».</summary>
    public bool Fits => Quality != TokenCountQuality.Unavailable && Tokens <= InputBudgetTokens;
}

/// <summary>
/// Preflight del ramo documentale (T039): risolve il profilo per modello/fase dal monitor (finestra misurata, tokenizer, template),
/// conta il payload effettivo completo e rifiuta l'invio se il runtime non è verificato o il payload eccede il budget. Il ramo
/// legacy (<see cref="ContextBudget.From"/>) resta invariato: usa la finestra configurata.
/// </summary>
public sealed class DocumentPreflight(AiOptions options, ModelRuntimeMonitor? runtime, IPayloadMeter? meter, PayloadCalibration? calibration = null)
{
    private static readonly System.Text.Json.JsonSerializerOptions Canonical = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Profilo verificato della fase; <see cref="RuntimeUnverifiedException"/> se finestra, modello, tokenizer o budget non sono dimostrati.</summary>
    public async ValueTask<DocumentRuntimeProfile> ResolveProfileAsync(AiPhase phase, CancellationToken cancellationToken)
    {
        if (runtime is null || meter is null)
        {
            throw new RuntimeUnverifiedException("monitor o contatore dei token non configurati");
        }

        var settings = options.ForPhase(phase);
        var snapshot = await runtime.GetSnapshotAsync(settings.ModelId, cancellationToken);
        if (snapshot.Status == ModelRuntimeStatus.Offline)
        {
            throw new RuntimeUnverifiedException("server non raggiungibile", transient: true);
        }

        if (snapshot.IsModelMissing(settings.ModelId))
        {
            throw new ModelNotAvailableException(settings.ModelId, phase);
        }

        if (snapshot.Status != ModelRuntimeStatus.Online || snapshot.ContextWindowTokens is not { } measured)
        {
            throw new RuntimeUnverifiedException($"finestra del modello {settings.ModelId} non verificata");
        }

        var window = Math.Min(options.ContextWindowTokens, measured);
        var output = settings.MaxOutputTokens;
        var margin = options.SafetyMarginTokens;
        if (output <= 0 || margin < 0 || window - output - margin <= 0)
        {
            throw new RuntimeUnverifiedException($"budget di input non positivo (W={window}, O={output}, M={margin})");
        }

        var caps = await runtime.GetCountingCapabilitiesAsync(settings.ModelId, cancellationToken);
        var exact = caps.TemplateExposed && caps.TokenizerVerified;
        var mode = exact ? "template" : calibration is { } c ? $"calibration:{c.Id}" : "none";
        var fingerprint = Hash($"{settings.ModelId}|{phase}|{window}|{output}|{margin}|{settings.EnableThinking}|{caps.TokenizerVerified}|{mode}");
        return new DocumentRuntimeProfile(
            phase, settings.ModelId, window, output, margin, settings.EnableThinking, exact, exact ? null : calibration?.Id, fingerprint);
    }

    /// <summary>
    /// Conta il payload completo (template, schema e strumenti compresi) senza lanciare per overflow: chi suddivide il lavoro legge
    /// <see cref="PayloadPreflight.Fits"/>. Con <paramref name="expectedFingerprint"/> il runtime deve coincidere con quello congelato.
    /// </summary>
    public async ValueTask<PayloadPreflight> MeasureAsync(DocumentPayload payload, CancellationToken cancellationToken, string? expectedFingerprint = null)
    {
        var profile = await ResolveProfileAsync(payload.Phase, cancellationToken);
        if (expectedFingerprint is not null && !string.Equals(expectedFingerprint, profile.Fingerprint, StringComparison.Ordinal))
        {
            throw new RuntimeFingerprintMismatchException(expectedFingerprint, profile.Fingerprint);
        }

        var (tokens, quality) = await CountAsync(profile, payload, cancellationToken);
        return new PayloadPreflight(profile, tokens, quality, PayloadHash(profile, payload));
    }

    /// <summary>Come <see cref="MeasureAsync"/>, ma lancia <see cref="RuntimeUnverifiedException"/> (conteggio non disponibile) o <see cref="ContextOverflowException"/> prima di qualunque invio.</summary>
    public async ValueTask<PayloadPreflight> EnsureAsync(DocumentPayload payload, CancellationToken cancellationToken, string? expectedFingerprint = null)
    {
        var result = await MeasureAsync(payload, cancellationToken, expectedFingerprint);
        if (result.Quality == TokenCountQuality.Unavailable)
        {
            throw new RuntimeUnverifiedException("conteggio del payload non disponibile (nessun tokenizer o calibrazione identificata)");
        }

        if (!result.Fits)
        {
            throw new ContextOverflowException(result.Tokens, result.InputBudgetTokens);
        }

        return result;
    }

    private async ValueTask<(int Tokens, TokenCountQuality Quality)> CountAsync(DocumentRuntimeProfile profile, DocumentPayload payload, CancellationToken ct)
    {
        var messages = new List<(string Role, string Text)>();
        if (!string.IsNullOrEmpty(payload.Instructions))
        {
            messages.Add(("system", payload.Instructions));
        }

        messages.AddRange(payload.Messages.Select(m => (m.Role.Value, m.Text)));
        var extras = new[] { payload.ResponseSchemaJson, payload.ToolsJson }.Where(e => !string.IsNullOrEmpty(e)).Select(e => e!).ToList();
        var caps = await runtime!.GetCountingCapabilitiesAsync(profile.ModelId, ct);
        try
        {
            if (caps.TokenizerVerified && profile.TemplateExposed)
            {
                var rendered = await meter!.RenderChatTemplateAsync(profile.ModelId, messages, profile.Thinking, ct);
                var total = await meter.CountTokensAsync(profile.ModelId, rendered, ct);
                foreach (var extra in extras)
                {
                    total += await meter.CountTokensAsync(profile.ModelId, extra, ct);
                }

                return (total, TokenCountQuality.Measured);
            }

            if (calibration is null)
            {
                return (0, TokenCountQuality.Unavailable);
            }

            var bound = calibration.FixedOverheadTokens + calibration.PerMessageFramingTokens * messages.Count;
            foreach (var text in messages.Select(m => m.Text).Concat(extras))
            {
                if (caps.TokenizerVerified)
                {
                    bound += await meter!.CountTokensAsync(profile.ModelId, text, ct);
                }
                else if (calibration.MinCharsPerToken is { } min and > 0)
                {
                    bound += (int)Math.Ceiling(text.Length / min);
                }
                else
                {
                    return (0, TokenCountQuality.Unavailable);
                }
            }

            return (bound, TokenCountQuality.CalibratedUpperBound);
        }
        catch (HttpRequestException)
        {
            return (0, TokenCountQuality.Unavailable);
        }
    }

    /// <summary>Hash del payload completo (istruzioni, messaggi, schema, strumenti, modello e opzioni): stesso input logico, stesso hash.</summary>
    private static string PayloadHash(DocumentRuntimeProfile profile, DocumentPayload payload) =>
        Hash(System.Text.Json.JsonSerializer.Serialize(new
        {
            profile.ModelId,
            profile.Phase,
            profile.OutputTokens,
            profile.Thinking,
            payload.Instructions,
            Messages = payload.Messages.Select(m => new { Role = m.Role.Value, m.Text }),
            payload.ResponseSchemaJson,
            payload.ToolsJson,
        }, Canonical));

    private static string Hash(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>
/// Ultimo anello degli agenti batch documentali: ricostruisce il payload effettivo di OGNI chiamata (istruzioni, messaggi, schema,
/// strumenti) e lo sottopone al preflight del profilo di modello/fase, con l'impronta congelata se fornita. Nessuna history né
/// compattazione: fuori budget o runtime non verificato non parte alcuna richiesta.
/// </summary>
internal sealed class DocumentPayloadChatClient(IChatClient inner, DocumentPreflight preflight, AiPhase phase, string? expectedFingerprint)
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
        var list = messages.ToList();
        var system = list.Where(m => m.Role == ChatRole.System).Select(m => m.Text);
        var instructions = string.Join("\n", new[] { options?.Instructions }.Concat(system).Where(t => !string.IsNullOrEmpty(t)));
        var schema = options?.ResponseFormat is ChatResponseFormatJson { Schema: { } element } ? element.GetRawText() : null;
        var tools = options?.Tools is { Count: > 0 } t
            ? string.Join("\n", t.Select(tool => $"{tool.Name}:{tool.Description}"))
            : null;
        var payload = new DocumentPayload(phase, instructions, list.Where(m => m.Role != ChatRole.System).ToList(), schema, tools);
        await preflight.EnsureAsync(payload, ct, expectedFingerprint);
        return list;
    }
}
