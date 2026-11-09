using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace bProjects.App.Documents;

/// <summary>
/// Collegamento al parser interno (contracts/parser.md), sezione <c>Parser</c>. Senza <see cref="BaseUrl"/> il parsing non è configurato:
/// il worker non prende in carico nulla e i job restano Queued (non falliscono per un servizio che non esiste).
/// </summary>
public sealed class ParserOptions
{
    public const string SectionName = "Parser";

    /// <summary>Indirizzo del parser sulla rete privata (mai pubblicato); vuoto = parsing non configurato.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Segreto interno inviato nell'header X-Internal-Secret (protocollo del parser); solo da variabile d'ambiente, mai nel repository né nei log.</summary>
    public string? Secret { get; set; }

    /// <summary>Attesa del worker quando non c'è nulla da fare.</summary>
    public int PollSeconds { get; set; } = 2;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}

/// <summary>Limiti passati al parser: non superiori a quelli del server, già congelati con la richiesta.</summary>
public sealed record ParserLimits(long MaxExpandedBytes, int MaxPages, int MaxBlocks, long MaxOutputBytes, int TimeoutSeconds);

/// <summary>Una conversione: i byte vengono trasmessi, mai un percorso o un URL. Lo stream è consumato dalla chiamata.</summary>
public sealed record ParserRequest(
    Stream Content, DocumentFormat Format, Guid DocumentVersionId, string OriginalSha256, long OriginalByteLength, ParserLimits Limits);

/// <summary>Corpo grezzo del manifest (200): non è ancora fidato, lo valida <see cref="ParsedManifestValidator"/>. <c>ConfigHash</c> è quello pubblicato da /version e inviato con la richiesta.</summary>
public sealed record ParserResponse(byte[] Body, string ConfigHash);

/// <summary>Esito di un errore del parser già classificato dal backend (transitorio o permanente) con codice stabile e senza diagnostica sensibile.</summary>
public readonly record struct ParserFailure(bool IsTransient, string Code, TimeSpan? RetryAfter = null);

public sealed class ParserException(ParserFailure failure) : Exception(failure.Code)
{
    public ParserFailure Failure { get; } = failure;
}

/// <summary>
/// Adapter HTTP sincrono verso il parser: una conversione, nessun retry proprio (retry e tentativi sono del backend), nessun stato.
/// Il tempo è un budget unico: min(timeout del parser, residuo della deadline effettiva) per la conversione, più al massimo 5 s di cleanup
/// per il trasporto e comunque mai oltre il residuo. Il corpo di un errore non entra mai nel messaggio: si legge solo un codice noto.
/// </summary>
public sealed class ParserClient(HttpClient http, IOptions<ParserOptions> options)
{
    public const string SchemaVersion = "document-parse-1";

    /// <summary>Header con cui il parser autentica il backend (parser/service.py).</summary>
    public const string SecretHeader = "X-Internal-Secret";

    /// <summary>Cleanup massimo concesso al trasporto oltre il timeout del subprocess.</summary>
    public static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(5);

    /// <summary>Sotto questo residuo non ha senso avviare una conversione.</summary>
    public static readonly TimeSpan MinimumBudget = TimeSpan.FromSeconds(1);

    private const int MaxErrorBodyBytes = 4_096;

    private static readonly HashSet<string> KnownErrorCodes = ["parse_limit_exceeded", "parse_timeout", "parser_busy", "parser_config_mismatch"];

    private readonly ParserOptions _options = options.Value;

    public static string FormatCode(DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => "pdf",
        DocumentFormat.Docx => "docx",
        DocumentFormat.Markdown => "md",
        _ => "txt",
    };

    public async Task<ParserResponse> ConvertAsync(ParserRequest request, TimeSpan remaining, CancellationToken ct)
    {
        if (!_options.IsConfigured)
        {
            throw new ParserException(new(false, "parser_not_configured"));
        }

        var parseTimeout = TimeSpan.FromSeconds(Math.Min(request.Limits.TimeoutSeconds, Math.Floor(remaining.TotalSeconds)));
        if (remaining < MinimumBudget || parseTimeout < MinimumBudget)
        {
            throw new ParserException(new(false, "deadline_exceeded"));
        }

        var transport = TimeSpan.FromTicks(Math.Min((parseTimeout + CleanupGrace).Ticks, remaining.Ticks));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(transport);
        try
        {
            var configHash = await ReadVersionAsync(request.Format, budget.Token);
            using var content = BuildContent(request, configHash, (int)parseTimeout.TotalSeconds);
            using var message = Message(HttpMethod.Post, "convert");
            message.Content = content;
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new ParserException(await ClassifyAsync(response, budget.Token));
            }

            return new(await ReadBoundedAsync(response, request.Limits.MaxOutputBytes, budget.Token), configHash);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Budget esaurito: il subprocess non ha risposto entro il tempo. Ritentabile solo se tentativi e residuo lo consentono.
            throw new ParserException(new(true, "parse_timeout"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ParserException(new(true, "parser_unreachable"));
        }
    }

    private HttpRequestMessage Message(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, new Uri(new Uri(_options.BaseUrl!.TrimEnd('/') + "/"), path));
        if (!string.IsNullOrEmpty(_options.Secret))
        {
            message.Headers.Add(SecretHeader, _options.Secret);
        }

        return message;
    }

    /// <summary>GET /version: schema, configHash e formati ammessi. Una versione incompatibile blocca la conversione (permanente).</summary>
    private async Task<string> ReadVersionAsync(DocumentFormat format, CancellationToken ct)
    {
        using var message = Message(HttpMethod.Get, "version");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ParserException(await ClassifyAsync(response, ct));
        }

        var body = await ReadBoundedAsync(response, MaxErrorBodyBytes * 16, ct);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.String && schema.GetString() == SchemaVersion
                && root.TryGetProperty("configHash", out var config) && config.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(config.GetString())
                && root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array
                && formats.EnumerateArray().Any(f => f.ValueKind == JsonValueKind.String && f.GetString() == FormatCode(format)))
            {
                return config.GetString()!;
            }
        }
        catch (JsonException)
        {
            // trattato come versione incompatibile
        }

        throw new ParserException(new(false, "parser_incompatible"));
    }

    private static MultipartFormDataContent BuildContent(ParserRequest request, string configHash, int timeoutSeconds)
    {
        var limits = request.Limits;
        var form = new MultipartFormDataContent();
        var file = new StreamContent(request.Content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "document");   // nome fisso: il nome originale non lascia il backend
        void Field(string name, string value) => form.Add(new StringContent(value), name);
        Field("declaredFormat", FormatCode(request.Format));
        Field("documentVersionId", request.DocumentVersionId.ToString("D"));
        Field("originalSha256", request.OriginalSha256);
        Field("configHash", configHash);
        Field("maxExpandedBytes", limits.MaxExpandedBytes.ToString(CultureInfo.InvariantCulture));
        Field("maxPages", limits.MaxPages.ToString(CultureInfo.InvariantCulture));
        Field("maxBlocks", limits.MaxBlocks.ToString(CultureInfo.InvariantCulture));
        Field("maxOutputBytes", limits.MaxOutputBytes.ToString(CultureInfo.InvariantCulture));
        Field("timeoutSeconds", timeoutSeconds.ToString(CultureInfo.InvariantCulture));
        return form;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            throw new ParserException(new(false, "parse_output_too_large"));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new ParserException(new(false, "parse_output_too_large"));   // mai un JSON troncato presentato come valido
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Classificazione del contratto: transitori 429/503/timeout, permanenti config/formato/input/limite. Si legge solo un codice noto.</summary>
    private static async Task<ParserFailure> ClassifyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var code = await ReadErrorCodeAsync(response, ct);
        var retryAfter = response.Headers.RetryAfter is { } header ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null) : null;
        return status switch
        {
            429 => new(true, "parser_busy", retryAfter),
            409 => new(false, "parser_config_mismatch"),
            415 => new(false, "parser_unsupported_format"),
            422 when code == "parse_limit_exceeded" => new(false, "parse_limit_exceeded"),
            422 when code == "parse_timeout" => new(true, "parse_timeout"),
            422 => new(false, "parse_input_invalid"),
            401 or 403 => new(false, "parser_unauthorized"),
            >= 500 => new(true, "parser_unavailable", retryAfter),
            _ => new(false, "parser_error"),
        };
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await ReadBoundedAsync(response, MaxErrorBodyBytes, ct);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                && code.GetString() is { } text && KnownErrorCodes.Contains(text)
                    ? text
                    : null;
        }
        catch (Exception ex) when (ex is JsonException or ParserException or HttpRequestException or IOException)
        {
            return null;
        }
    }
}
