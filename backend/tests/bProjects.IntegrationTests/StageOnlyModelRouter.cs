using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace bProjects.IntegrationTests;

/// <summary>
/// Instradamento del modello per la verifica isolata Retrieval → Gap Stage (003). SOLO test; si pone davanti a <c>InternalModelClient</c> al posto del gestore HTTP di rete:
/// <list type="bullet">
/// <item>richiesta di chat dello STADIO GAP (<c>Formato: gap-stage-message-1</c>) → inoltrata al modello a monte (reale in prova DGX), al massimo <see cref="MaxStageCalls"/> volte e solo se il messaggio dichiara
/// <c>searchBasis: retrieval</c>; altrimenti VIOLAZIONE, nulla viene inoltrato;</item>
/// <item>richiesta di chat della GERARCHIA (<c>Tipo di batch: foglia|riduzione|cross|espansione|confronto</c>) → risposta DETERMINISTICA locale, mai inoltrata;</item>
/// <item><c>/v1/models</c>, <c>/props</c>, <c>/slots</c>, <c>/tokenize</c>, <c>/apply-template</c> (non generativi, sola lettura) → inoltrati, contati e cronometrati;</item>
/// <item>qualunque altra richiesta (chat non riconosciuta, altro percorso) → VIOLAZIONE.</item>
/// </list>
/// Una violazione è registrata in <see cref="Violations"/> E fa fallire la richiesta (eccezione): il test deve asserire che l'elenco sia vuoto. Nessun retry, nessuna riparazione.
/// </summary>
internal sealed class StageOnlyModelRouter(HttpMessageHandler upstream, Func<string, string> hierarchy, int maxStageCalls = 1, bool ownsUpstream = false, int maxWarmupCalls = 0) : HttpMessageHandler
{
    public const string StageMarker = "Formato: gap-stage-message-1";
    public const string RetrievalBasisMarker = "searchBasis: retrieval";

    public const string PhaseStage = "stage";

    /// <summary>Richiesta di riscaldamento ammessa (come la campagna T040): carica il modello perche il runtime sia verificabile. Testo esatto, al massimo <c>maxWarmupCalls</c> volte (default 0), con <c>max_tokens</c> ≤ 16.</summary>
    public const string WarmupText = "Rispondi solo: ok";

    public const string PhaseWarmup = "warm-up";
    public const string PhaseHierarchyStub = "hierarchy-stub";

    /// <summary>Una richiesta vista dal router: <see cref="Forwarded"/> = arrivata al modello a monte (DGX in prova reale).</summary>
    public sealed record Call(string Path, string Kind, bool Forwarded, DateTimeOffset At, double Milliseconds, int Status, int? PromptTokens = null, int? CompletionTokens = null, string? FinishReason = null);

    /// <summary>Percorsi non generativi ammessi e metodo ammesso: le letture sono GET, tokenizzazione e template POST (un POST su /props o /slots potrebbe modificare lo stato del server).</summary>
    private static readonly Dictionary<string, HttpMethod> ForwardedPaths = new(StringComparer.Ordinal)
    {
        ["/v1/models"] = HttpMethod.Get, ["/props"] = HttpMethod.Get, ["/slots"] = HttpMethod.Get, ["/tokenize"] = HttpMethod.Post, ["/apply-template"] = HttpMethod.Post,
    };

    private static readonly Regex HierarchyBatch = new(@"^Tipo di batch: (foglia|riduzione|cross|espansione|confronto)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly HttpMessageInvoker _upstream = new(upstream, disposeHandler: false);
    private readonly List<Call> _calls = [];
    private readonly List<string> _violations = [];
    private readonly List<string> _stageTexts = [];
    private int _stageCalls;
    private int _warmupCalls;

    public int MaxStageCalls { get; } = maxStageCalls;

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<string> Violations
    {
        get
        {
            lock (_violations)
            {
                return [.. _violations];
            }
        }
    }

    /// <summary>Testo del messaggio dello stadio effettivamente inoltrato (utente), per verificare ciò che il modello ha ricevuto davvero.</summary>
    public IReadOnlyList<string> StageRequestTexts
    {
        get
        {
            lock (_stageTexts)
            {
                return [.. _stageTexts];
            }
        }
    }

    /// <summary>Richieste di chat (generative) arrivate al router, di qualunque tipo; lo stadio e la gerarchia simulata.</summary>
    public int ChatRequests => Calls.Count(c => c.Kind is PhaseStage or PhaseHierarchyStub);

    /// <summary>Richieste generative inoltrate al modello a monte: il contratto dell'esperimento ne prevede UNA per scenario.</summary>
    public int ForwardedWarmups => Calls.Count(c => c.Kind == PhaseWarmup && c.Forwarded);

    public int ForwardedStageCalls => Calls.Count(c => c.Kind == PhaseStage && c.Forwarded);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        if (path == "/v1/chat/completions")
        {
            string text;
            try
            {
                text = ChatText(body);
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw Violation("corpo della richiesta di chat non interpretabile: nulla inoltrato");
            }

            if (text.Contains(StageMarker, StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _stageCalls) > MaxStageCalls)
                {
                    throw Violation($"richiesta generativa dello stadio Gap non prevista (massimo {MaxStageCalls}): nulla inoltrato");
                }

                if (!text.Contains(RetrievalBasisMarker, StringComparison.Ordinal))
                {
                    throw Violation($"la richiesta dello stadio Gap non dichiara «{RetrievalBasisMarker}» (il ledger entra nello stadio: sorgente esaustiva, il recupero non e esercitato): nulla inoltrato");
                }

                lock (_stageTexts)
                {
                    _stageTexts.Add(text);
                }

                return await ForwardAsync(request, PhaseStage, path, cancellationToken);
            }

            if (text.Trim() == WarmupText)
            {
                if (Interlocked.Increment(ref _warmupCalls) > maxWarmupCalls)
                {
                    throw Violation($"richiesta di riscaldamento non prevista (massimo {maxWarmupCalls}): nulla inoltrato");
                }

                if (!CapIsAtMost(body, 16))
                {
                    throw Violation("la richiesta di riscaldamento deve avere un tetto di token (max_tokens o max_completion_tokens) ≤ 16: nulla inoltrato");
                }

                return await ForwardAsync(request, PhaseWarmup, path, cancellationToken);
            }

            if (HierarchyBatch.IsMatch(text))
            {
                var started = DateTimeOffset.UtcNow;
                var content = hierarchy(text);
                Record(new Call(path, PhaseHierarchyStub, false, started, 0, 200));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            id = "stub", @object = "chat.completion", created = 1, model = "hierarchy-stub",
                            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
                            usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
                        }),
                        Encoding.UTF8, "application/json"),
                };
            }

            throw Violation("richiesta di chat non riconosciuta (ne stadio Gap ne batch della gerarchia): nulla inoltrato");
        }

        if (ForwardedPaths.TryGetValue(path, out var method))
        {
            if (request.Method != method)
            {
                throw Violation($"metodo {request.Method} non ammesso su {path} (ammesso {method})");
            }

            return await ForwardAsync(request, path.TrimStart('/'), path, cancellationToken);
        }

        throw Violation($"percorso non ammesso: {path}");
    }

    private async Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request, string kind, string path, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await _upstream.SendAsync(request, ct);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            if (kind == PhaseStage && response.IsSuccessStatusCode)
            {
                // Uso e motivo di fine della risposta dello stadio (corpo bufferizzato: resta leggibile dal client). Solo osservazione.
                await response.Content.LoadIntoBufferAsync(ct);
                var (prompt, completion, finish) = UsageOf(await response.Content.ReadAsStringAsync(ct));
                Record(new Call(path, kind, true, started, elapsed, (int)response.StatusCode, prompt, completion, finish));
            }
            else
            {
                Record(new Call(path, kind, true, started, elapsed, (int)response.StatusCode));
            }

            return response;
        }
        catch
        {
            Record(new Call(path, kind, true, started, watch.Elapsed.TotalMilliseconds, 0));
            throw;
        }
    }

    /// <summary>Tetto di token della richiesta (<c>max_tokens</c> oppure <c>max_completion_tokens</c>, come invia il client) presente e nell'intervallo 1..<paramref name="max"/>.</summary>
    internal static bool CapIsAtMost(string body, int max)
    {
        using var doc = JsonDocument.Parse(body);
        foreach (var name in new[] { "max_completion_tokens", "max_tokens" })
        {
            if (doc.RootElement.TryGetProperty(name, out var limit) && limit.TryGetInt32(out var tokens))
            {
                return tokens > 0 && tokens <= max;
            }
        }

        return false;
    }

    private static (int? Prompt, int? Completion, string? Finish) UsageOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            int? Int(string name) => doc.RootElement.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var n) && n.TryGetInt32(out var value) ? value : null;
            var finish = doc.RootElement.TryGetProperty("choices", out var c) && c.GetArrayLength() > 0 && c[0].TryGetProperty("finish_reason", out var f) ? f.GetString() : null;
            return (Int("prompt_tokens"), Int("completion_tokens"), finish);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return (null, null, null);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && ownsUpstream)
        {
            upstream.Dispose();
        }

        base.Dispose(disposing);
    }

    private InvalidOperationException Violation(string message)
    {
        lock (_violations)
        {
            _violations.Add(message);
        }

        return new InvalidOperationException("Instradamento del modello (fail-closed): " + message);
    }

    private void Record(Call call)
    {
        lock (_calls)
        {
            _calls.Add(call);
        }
    }

    /// <summary>Testo concatenato dei messaggi della richiesta di chat (stringa o parti con <c>text</c>).</summary>
    internal static string ChatText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var parts = new List<string>();
        foreach (var message in doc.RootElement.GetProperty("messages").EnumerateArray())
        {
            if (message.GetProperty("role").GetString() is "system" or "developer")
            {
                continue;   // come i riconoscitori dei test della 003 (IsStage): conta solo il messaggio dell'utente, non le istruzioni
            }

            var content = message.GetProperty("content");
            parts.Add(content.ValueKind == JsonValueKind.String
                ? content.GetString()!
                : string.Concat(content.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : string.Empty)));
        }

        return string.Join('\n', parts);
    }

    // ================================================================ attribuzione per fase (a posteriori, dai tempi)

    public sealed record PhaseLine(string Path, string Phase, int Requests, double TotalMilliseconds, double MaxMilliseconds, int NonSuccess);

    /// <summary>
    /// Richieste inoltrate al modello a monte, per percorso e FASE, attribuite dai tempi: <c>pre-hierarchy</c> (prima della prima risposta simulata), <c>hierarchy</c> (fino all'ultima),
    /// <c>retrieval-and-measure</c> (dopo la gerarchia e prima dello stadio: recupero e misura dei costi), <c>stage</c> (dalla richiesta dello stadio). Funzione pura.
    /// </summary>
    public static IReadOnlyList<PhaseLine> ByPhase(IReadOnlyList<Call> calls)
    {
        var stubs = calls.Where(c => c.Kind == PhaseHierarchyStub).Select(c => c.At).ToList();
        var stage = calls.Where(c => c.Kind == PhaseStage).Select(c => c.At).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        string PhaseOf(Call c)
        {
            if (c.At >= stage)
            {
                return "stage";
            }

            if (stubs.Count == 0 || c.At < stubs.Min())
            {
                return "pre-hierarchy";
            }

            return c.At <= stubs.Max() ? "hierarchy" : "retrieval-and-measure";
        }

        return [.. calls.Where(c => c.Forwarded)
            .GroupBy(c => (c.Path, Phase: PhaseOf(c)))
            .OrderBy(g => g.Key.Path, StringComparer.Ordinal).ThenBy(g => g.Key.Phase, StringComparer.Ordinal)
            .Select(g => new PhaseLine(g.Key.Path, g.Key.Phase, g.Count(), g.Sum(c => c.Milliseconds), g.Max(c => c.Milliseconds), g.Count(c => c.Status is < 200 or >= 300)))];
    }
}

/// <summary>Passa le richieste a un gestore che non possiede: il client di riscaldamento non deve smaltire il router condiviso.</summary>
internal sealed class NonOwningHandler(HttpMessageHandler inner) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _inner = new(inner, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _inner.SendAsync(request, cancellationToken);
}
