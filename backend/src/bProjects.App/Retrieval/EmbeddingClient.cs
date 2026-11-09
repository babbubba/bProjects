using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace bProjects.App.Retrieval;

/// <summary>
/// Client del solo servizio di embedding interno (003 T007; <c>POST /v1/embeddings</c> OpenAI-compatibile, llama.cpp). Nessuna interfaccia (una sola
/// implementazione), nessun fallback, nessun retry silenzioso (la politica dichiarata è «zero»: i tentativi appartengono al lavoro chiamante, idempotente).
/// Batch fisso di <see cref="BatchSize"/> input; nessun troncamento lato client: un input oltre il limite del modello è <c>embedding_input_too_long</c>.
/// Ogni vettore è verificato: dimensione = <see cref="RetrievalOptions.FixedDimension"/> e norma L2 ≈ 1 entro <see cref="NormTolerance"/>.
/// </summary>
public sealed class EmbeddingClient : IDisposable
{
    /// <summary>Input per richiesta HTTP (R-4).</summary>
    public const int BatchSize = 4;

    /// <summary>Tolleranza dichiarata sulla norma L2 dei vettori (|‖v‖ − 1|): i modelli quantizzati non sono esattamente unitari.</summary>
    public const double NormTolerance = 0.01;

    private readonly RetrievalModelOptions _model;
    private readonly HttpClient _http;
    private readonly Uri _embeddingsUri;
    private readonly TimeSpan _timeout;

    public EmbeddingClient(RetrievalOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _model = options.Embedding;
        var endpoint = new Uri(_model.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        _embeddingsUri = new Uri(endpoint, "embeddings");
        _timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        // Niente redirect né proxy di sistema: la chiave viaggia solo verso l'host configurato e validato.
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;   // il timeout è per richiesta (CancellationTokenSource), così distingue «scaduto» da «annullato dal chiamante»
        if (!string.IsNullOrEmpty(_model.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _model.ApiKey);
        }
    }

    /// <summary>
    /// Embedding degli input nell'ordine dato, in batch da <see cref="BatchSize"/>. Un errore in un batch interrompe l'operazione (nessun risultato parziale).
    /// Lancia <see cref="RetrievalException"/> tipizzata; l'annullamento del chiamante propaga <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var result = new List<float[]>(inputs.Count);
        for (var offset = 0; offset < inputs.Count; offset += BatchSize)
        {
            var batch = inputs.Skip(offset).Take(BatchSize).ToArray();
            foreach (var vector in await RequestAsync(batch, cancellationToken))
            {
                result.Add(Validate(vector));
            }
        }

        return result;
    }

    /// <summary>Embedding di un solo testo (query).</summary>
    public async Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken = default) =>
        (await EmbedAsync([input], cancellationToken))[0];

    /// <summary>
    /// Sola dimensione del vettore di prova (sonda di readiness): nessuna validazione di dimensione né di norma, così una difformità diventa uno stato
    /// (<c>DimensionMismatch</c>) e non un'eccezione. Errori di servizio e risposte illeggibili restano tipizzati.
    /// </summary>
    internal async Task<int> ProbeDimensionAsync(CancellationToken cancellationToken = default) =>
        (await RequestAsync(["probe"], cancellationToken))[0].Length;

    private static float[] Validate(float[] vector)
    {
        if (vector.Length != RetrievalOptions.FixedDimension)
        {
            throw new RetrievalException(
                RetrievalException.EmbeddingDimensionMismatch,
                $"Il servizio di embedding ha restituito vettori di dimensione {vector.Length} invece di {RetrievalOptions.FixedDimension}: verificare modello e artefatto configurati.");
        }

        double sum = 0;
        foreach (var x in vector)
        {
            if (!float.IsFinite(x))
            {
                throw Malformed("il vettore contiene valori non finiti");
            }

            sum += (double)x * x;
        }

        if (Math.Abs(Math.Sqrt(sum) - 1.0) > NormTolerance)
        {
            throw Malformed("il vettore non è normalizzato L2");
        }

        return vector;
    }

    private async Task<float[][]> RequestAsync(string[] batch, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { model = _model.ModelId, input = batch, encoding_format = "float" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _embeddingsUri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                // Richiesta rifiutata per dimensione (llama.cpp: 400 «input is too large» / «exceeds the available context size»): non è un guasto del servizio.
                throw response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge
                    ? new RetrievalException(
                        RetrievalException.EmbeddingInputTooLong,
                        "Un passaggio supera il limite di input del modello di embedding: nessun troncamento è applicato; verificare la dimensione dei blocchi.")
                    : Unavailable($"stato HTTP {(int)response.StatusCode}");
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return Parse(json.RootElement, batch.Length);
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

    /// <summary>Risposta <c>{"data":[{"index":i,"embedding":[...]}]}</c>: esattamente un vettore per input, indici unici 0..n-1; l'ordine è quello degli indici.</summary>
    private static float[][] Parse(JsonElement root, int expected)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw Malformed("campo data assente");
        }

        if (data.GetArrayLength() != expected)
        {
            throw Malformed($"attesi {expected} vettori, ricevuti {data.GetArrayLength()}");
        }

        var vectors = new float[expected][];
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("index", out var index) || !index.TryGetInt32(out var i) || i < 0 || i >= expected || vectors[i] is not null
                || !item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array)
            {
                throw Malformed("voce senza indice valido e unico o senza embedding");
            }

            var vector = new float[embedding.GetArrayLength()];
            var n = 0;
            foreach (var number in embedding.EnumerateArray())
            {
                if (number.ValueKind != JsonValueKind.Number || !number.TryGetSingle(out vector[n]))
                {
                    throw Malformed("componente non numerica");
                }

                n++;
            }

            vectors[i] = vector;
        }

        return vectors;
    }

    private static RetrievalException Unavailable(string cause) =>
        new(RetrievalException.EmbeddingUnavailable, $"Il servizio di embedding non è disponibile ({cause}): riprovare più tardi; nessun servizio alternativo è usato.");

    private static RetrievalException Malformed(string cause) =>
        new(RetrievalException.EmbeddingMalformedResponse, $"Risposta del servizio di embedding non valida ({cause}).");

    public void Dispose() => _http.Dispose();
}
