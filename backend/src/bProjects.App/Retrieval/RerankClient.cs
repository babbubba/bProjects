using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace bProjects.App.Retrieval;

/// <summary>
/// Posizione di un candidato nel riordino: <see cref="Index"/> è la posizione nell'elenco inviato, <see cref="Score"/> il punteggio grezzo del riordino.
/// Il punteggio serve SOLO a ordinare (FR-009): può essere negativo, non è una probabilità, non è persistito come tale e non è mai una soglia.
/// </summary>
public sealed record RerankScore(int Index, double Score);

/// <summary>
/// Client del solo servizio di riordino interno (003 T021; <c>POST /v1/rerank</c>, llama.cpp, pooling <c>rank</c>). Una sola implementazione, nessun fallback,
/// nessun retry silenzioso. Il risultato è validato: esattamente un punteggio finito per candidato, indici unici entro il pool; una risposta incompleta o
/// incoerente è <c>reranker_malformed_response</c>, mai una lista parziale. Nessun troncamento lato client dei testi.
/// </summary>
public sealed class RerankClient : IDisposable
{
    private readonly RetrievalModelOptions _model;
    private readonly HttpClient _http;
    private readonly Uri _rerankUri;
    private readonly TimeSpan _timeout;

    public RerankClient(RetrievalOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _model = options.Reranker;
        var endpoint = new Uri(_model.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        _rerankUri = new Uri(endpoint, "rerank");
        _timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        // Niente redirect né proxy di sistema: la chiave viaggia solo verso l'host configurato e validato.
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;   // timeout per richiesta: distingue «scaduto» da «annullato dal chiamante»
        if (!string.IsNullOrEmpty(_model.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _model.ApiKey);
        }
    }

    /// <summary>
    /// Riordina <paramref name="documents"/> rispetto a <paramref name="query"/>: restituisce un punteggio per ciascun candidato, in ordine decrescente di punteggio
    /// (a parità, per indice crescente: ordine deterministico). Elenco vuoto ⇒ nessuna richiesta. Lancia <see cref="RetrievalException"/> tipizzata;
    /// l'annullamento del chiamante propaga <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
        {
            return [];
        }

        var body = JsonSerializer.Serialize(new { model = _model.ModelId, query, documents });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _rerankUri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable($"stato HTTP {(int)response.StatusCode}");
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return Parse(json.RootElement, documents.Count);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable("timeout");
        }
        catch (HttpRequestException)
        {
            throw Unavailable("rete");
        }
        catch (IOException)
        {
            throw Unavailable("rete");
        }
        catch (JsonException)
        {
            throw Malformed("JSON illeggibile");
        }
    }

    /// <summary>Sonda funzionale della readiness: un riordino minimo (due candidati) con la stessa validazione della risposta.</summary>
    internal async Task ProbeAsync(CancellationToken cancellationToken = default) =>
        await RerankAsync("probe", ["probe a", "probe b"], cancellationToken);

    /// <summary>Risposta <c>{"results":[{"index":i,"relevance_score":s}]}</c>: un elemento per candidato, indici unici in 0..n-1, punteggi finiti.</summary>
    private static List<RerankScore> Parse(JsonElement root, int expected)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            throw Malformed("campo results assente");
        }

        if (results.GetArrayLength() != expected)
        {
            throw Malformed($"attesi {expected} punteggi, ricevuti {results.GetArrayLength()}");
        }

        var seen = new bool[expected];
        var scores = new List<RerankScore>(expected);
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number
                || !index.TryGetInt32(out var i) || i < 0 || i >= expected || seen[i]
                || !item.TryGetProperty("relevance_score", out var score) || score.ValueKind != JsonValueKind.Number
                || !score.TryGetDouble(out var s) || !double.IsFinite(s))
            {
                throw Malformed("voce senza indice valido e unico o senza punteggio numerico");
            }

            seen[i] = true;
            scores.Add(new RerankScore(i, s));
        }

        scores.Sort(static (a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Index.CompareTo(b.Index));
        return scores;
    }

    private static RetrievalException Unavailable(string cause) =>
        new(RetrievalException.RerankerUnavailable, $"Il servizio di riordino non è disponibile ({cause}): riprovare più tardi; nessun servizio alternativo è usato.");

    private static RetrievalException Malformed(string cause) =>
        new(RetrievalException.RerankerMalformedResponse, $"Risposta del servizio di riordino non valida ({cause}).");

    public void Dispose() => _http.Dispose();
}
