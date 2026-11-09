using System.Net;
using System.Text;
using System.Text.Json;

namespace bProjects.IntegrationTests;

/// <summary>
/// Server HTTP FINTO del servizio di embedding (003): nessuna chiamata di rete reale, nessun modello. Stessa tecnica dei test di <c>ModelRuntimeMonitor</c>
/// (un <see cref="HttpMessageHandler"/> con risposte canned per percorso). Per default risponde con vettori L2-normalizzati deterministici di dimensione 1024.
/// </summary>
internal sealed class FakeEmbeddingServer : HttpMessageHandler
{
    public const string ModelsLoaded =
        """{"object":"list","data":[{"id":"bge-m3-Q5","object":"model","owned_by":"llamacpp","status":{"value":"loaded","args":["llama-server","--model","/models/bge-m3-Q5_K_M.gguf","--embeddings"]}}]}""";

    public int Dimension { get; set; } = 1024;

    /// <summary>Se valorizzata sostituisce il corpo della risposta di <c>/v1/embeddings</c>.</summary>
    public Func<string[], HttpResponseMessage>? EmbeddingsOverride { get; set; }

    public Func<HttpResponseMessage> Models { get; set; } = () => Json(ModelsLoaded);

    public Func<HttpResponseMessage> Props { get; set; } = () => new HttpResponseMessage(HttpStatusCode.NotFound);

    public Task? Gate { get; set; }

    public List<string> Paths { get; } = [];

    public List<string[]> EmbeddingInputs { get; } = [];

    public List<string> Authorization { get; } = [];

    public int EmbeddingRequests => EmbeddingInputs.Count;

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Vettore deterministico L2-normalizzato derivato dal testo (hash → direzione).</summary>
    public static float[] VectorOf(string text, int dimension)
    {
        var seed = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var v = new float[dimension];
        double sum = 0;
        for (var i = 0; i < dimension; i++)
        {
            v[i] = (seed[i % seed.Length] - 127.5f) / 127.5f + (i % 7) * 0.01f;
            sum += (double)v[i] * v[i];
        }

        var norm = (float)Math.Sqrt(sum);
        for (var i = 0; i < dimension; i++)
        {
            v[i] /= norm;
        }

        return v;
    }

    public static string EmbeddingsBody(IReadOnlyList<float[]> vectors, IReadOnlyList<int>? indexes = null) =>
        JsonSerializer.Serialize(new
        {
            @object = "list",
            model = "bge-m3-Q5",
            data = vectors.Select((v, i) => new { @object = "embedding", index = indexes?[i] ?? i, embedding = v }).ToArray(),
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Paths)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Authorization.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
        }

        if (Gate is not null)
        {
            await Gate.WaitAsync(cancellationToken);
        }

        switch (request.RequestUri!.AbsolutePath)
        {
            case "/v1/embeddings":
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using (var json = JsonDocument.Parse(body))
                {
                    var inputs = json.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
                    lock (EmbeddingInputs)
                    {
                        EmbeddingInputs.Add(inputs);
                    }

                    return EmbeddingsOverride is { } over
                        ? over(inputs)
                        : Json(EmbeddingsBody(inputs.Select(t => VectorOf(t, Dimension)).ToArray()));
                }

            case "/v1/models":
                return Models();
            case "/props":
                return Props();
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
