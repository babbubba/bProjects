using System.IO.Compression;
using System.Text;

namespace bProjects.App.Documents;

/// <summary>Formati ammessi dalla slice 002 (PDF testuali, DOCX, Markdown, TXT). Nessun altro formato è accettato.</summary>
public enum DocumentFormat
{
    Pdf,
    Docx,
    Markdown,
    Text,
}

/// <summary>Rifiuto sincrono di un upload: stato HTTP e codice stabile, mai percorsi, nomi interni né contenuto del file.</summary>
public sealed class UploadRejectedException(int status, string code, string title) : Exception(title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
}

/// <summary>
/// Validazione del trasporto (T013): nome file, formato effettivo (estensione + firma + tipo dichiarato), rifiuto di macro e
/// documenti protetti, limite di byte espansi per i DOCX (zip bomb). Il formato non si desume mai dal solo nome né dal solo
/// Content-Type del client. I controlli sul contenuto leggono il file già durevole: se falliscono l'originale appena scritto
/// non è referenziato e viene rimosso dal chiamante.
/// </summary>
public static class DocumentUploadValidation
{
    public const int MaxFileNameLength = 255;
    public const int HeadBytes = 8192;
    private const int MaxZipEntries = 20_000;
    private const int MaxContentTypesBytes = 1_048_576;

    public const string PdfMediaType = "application/pdf";
    public const string DocxMediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string MarkdownMediaType = "text/markdown";
    public const string TextMediaType = "text/plain";

    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] OleMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] ElfMagic = [0x7F, 0x45, 0x4C, 0x46];

    private static readonly string[] MacroExtensions = [".docm", ".dotm", ".dotx", ".xlsm", ".pptm", ".doc", ".dot"];

    /// <summary>
    /// Ultimo segmento del nome (separatori di entrambe le piattaforme), senza caratteri di controllo né virgolette, nome mai vuoto.
    /// Il nome è un'etichetta: non è mai usato per costruire un percorso (la chiave di storage è generata dal server).
    /// </summary>
    public static string SanitizeFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new UploadRejectedException(StatusCodes.Status422UnprocessableEntity, "invalid_file_name", "Nome del file mancante");
        }

        var name = raw[(raw.LastIndexOfAny(['/', '\\']) + 1)..];
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (!char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or '?' or '*' or ':' or ';'))
            {
                builder.Append(c);
            }
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.');
        if (sanitized.Length == 0 || sanitized.All(c => c == '.'))
        {
            throw new UploadRejectedException(StatusCodes.Status422UnprocessableEntity, "invalid_file_name", "Nome del file non valido");
        }

        if (sanitized.Length > MaxFileNameLength)
        {
            throw new UploadRejectedException(StatusCodes.Status422UnprocessableEntity, "invalid_file_name", "Nome del file troppo lungo");
        }

        return sanitized;
    }

    public static string MediaTypeOf(DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => PdfMediaType,
        DocumentFormat.Docx => DocxMediaType,
        DocumentFormat.Markdown => MarkdownMediaType,
        _ => TextMediaType,
    };

    /// <summary>
    /// Formato dall'estensione e coerenza col tipo dichiarato. Estensioni macro-enabled o legacy → 415; tipo dichiarato che appartiene a
    /// un altro formato ammesso (es. .txt dichiarato application/pdf) → 415. Tipi ignoti o generici (octet-stream) non decidono nulla.
    /// </summary>
    public static DocumentFormat DetectFormat(string fileName, string? declaredContentType)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (MacroExtensions.Contains(extension))
        {
            throw Unsupported("macro_enabled_not_allowed", "Formato con macro o legacy non ammesso");
        }

        var format = extension switch
        {
            ".pdf" => DocumentFormat.Pdf,
            ".docx" => DocumentFormat.Docx,
            ".md" or ".markdown" => DocumentFormat.Markdown,
            ".txt" => DocumentFormat.Text,
            _ => throw Unsupported("unsupported_format", "Formato non supportato: sono ammessi PDF, DOCX, Markdown e TXT"),
        };

        if (!string.IsNullOrWhiteSpace(declaredContentType))
        {
            var declared = declaredContentType.Split(';')[0].Trim().ToLowerInvariant();
            if (declared.Contains("macroenabled", StringComparison.Ordinal) || declared.StartsWith("application/vnd.ms-word", StringComparison.Ordinal))
            {
                throw Unsupported("macro_enabled_not_allowed", "Formato con macro o legacy non ammesso");
            }

            var declaredFamily = declared switch
            {
                PdfMediaType or "application/x-pdf" => (DocumentFormat?)DocumentFormat.Pdf,
                DocxMediaType => DocumentFormat.Docx,
                _ => null,
            };
            var textLike = declared is TextMediaType or MarkdownMediaType or "text/x-markdown";
            if ((declaredFamily is { } family && family != format) || (textLike && format is DocumentFormat.Pdf or DocumentFormat.Docx))
            {
                throw Unsupported("format_mismatch", "Il tipo dichiarato non corrisponde al formato del file");
            }
        }

        return format;
    }

    /// <summary>Firma dei primi byte: deve corrispondere al formato dichiarato dall'estensione. Un file vuoto è 422.</summary>
    public static void CheckHead(DocumentFormat format, ReadOnlySpan<byte> head)
    {
        if (head.Length == 0)
        {
            throw new UploadRejectedException(StatusCodes.Status422UnprocessableEntity, "empty_file", "Il file è vuoto");
        }

        switch (format)
        {
            case DocumentFormat.Pdf:
                if (head[..Math.Min(head.Length, 1024)].IndexOf(PdfMagic) < 0)
                {
                    throw Unsupported("format_mismatch", "Il contenuto non è un PDF");
                }

                break;
            case DocumentFormat.Docx:
                if (head.StartsWith(OleMagic))
                {
                    // Un pacchetto Office cifrato (o un .doc legacy) è un contenitore OLE, non uno zip OOXML.
                    throw new UploadRejectedException(StatusCodes.Status422UnprocessableEntity, "protected_file", "Il documento è protetto da password");
                }

                if (!head.StartsWith(ZipMagic))
                {
                    throw Unsupported("format_mismatch", "Il contenuto non è un documento DOCX");
                }

                break;
            default:
                CheckText(head);
                break;
        }
    }

    private static void CheckText(ReadOnlySpan<byte> head)
    {
        var utf16 = head.StartsWith(new byte[] { 0xFF, 0xFE }) || head.StartsWith(new byte[] { 0xFE, 0xFF });
        if (head.StartsWith(PdfMagic) || head.StartsWith(ZipMagic) || head.StartsWith(OleMagic) || head.StartsWith(ElfMagic)
            || (!utf16 && head.IndexOf((byte)0) >= 0))
        {
            throw Unsupported("format_mismatch", "Il contenuto non è un file di testo");
        }
    }

    /// <summary>
    /// Controlli sull'originale già durevole: PDF cifrato → 422; DOCX con macro, struttura non OOXML, voci con percorso ostile o byte
    /// espansi oltre il limite (conteggiati leggendo davvero i dati, non fidandosi delle dimensioni dichiarate) → 415/422.
    /// Non estrae nulla su disco: le voci sono solo lette e contate.
    /// </summary>
    public static async Task ValidateStoredAsync(DocumentFormat format, Stream file, long maxExpandedBytes, CancellationToken ct)
    {
        switch (format)
        {
            case DocumentFormat.Pdf:
                await CheckPdfNotEncryptedAsync(file, ct);
                break;
            case DocumentFormat.Docx:
                await Task.Run(() => CheckDocx(file, maxExpandedBytes, ct), ct);
                break;
        }
    }

    private static async Task CheckPdfNotEncryptedAsync(Stream file, CancellationToken ct)
    {
        var needle = "/Encrypt"u8.ToArray();
        // Il dizionario trailer sta in coda (o in testa nei PDF linearizzati): bastano i due estremi.
        foreach (var offset in new[] { 0L, Math.Max(0, file.Length - 65_536) })
        {
            file.Position = offset;
            var buffer = new byte[(int)Math.Min(65_536, file.Length - offset)];
            await file.ReadExactlyAsync(buffer, ct);
            if (buffer.AsSpan().IndexOf(needle) >= 0)
            {
                throw new UploadRejectedException(StatusCodes.Status422UnprocessableEntity, "protected_file", "Il PDF è protetto da password");
            }
        }
    }

    private static void CheckDocx(Stream file, long maxExpandedBytes, CancellationToken ct)
    {
        try
        {
            file.Position = 0;
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaxZipEntries)
            {
                throw Hostile("Archivio con troppe voci");
            }

            var hasContentTypes = false;
            var hasDocument = false;
            long declared = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (name.Length == 0 || name.StartsWith('/') || name.StartsWith('\\') || name.Contains("..", StringComparison.Ordinal)
                    || name.Contains(':') || name.Any(char.IsControl))
                {
                    throw Hostile("Archivio con percorsi non ammessi");
                }

                if (name.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))
                {
                    throw Unsupported("macro_enabled_not_allowed", "Documento con macro non ammesso");
                }

                hasContentTypes |= name == "[Content_Types].xml";
                hasDocument |= name == "word/document.xml";
                declared += entry.Length;
                if (declared > maxExpandedBytes)
                {
                    throw Hostile("Il contenuto espanso supera il limite consentito");
                }
            }

            if (!hasContentTypes || !hasDocument)
            {
                throw Unsupported("format_mismatch", "Il contenuto non è un documento DOCX");
            }

            // Lettura reale: le dimensioni dichiarate nella directory centrale possono mentire.
            long actual = 0;
            var buffer = new byte[81_920];
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                using var stream = entry.Open();
                var isContentTypes = entry.FullName == "[Content_Types].xml";
                using var capture = isContentTypes ? new MemoryStream() : null;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    actual += read;
                    if (actual > maxExpandedBytes)
                    {
                        throw Hostile("Il contenuto espanso supera il limite consentito");
                    }

                    if (capture is not null)
                    {
                        if (capture.Length + read > MaxContentTypesBytes)
                        {
                            throw Hostile("Voce di tipi di contenuto troppo grande");
                        }

                        capture.Write(buffer, 0, read);
                    }
                }

                if (capture is not null && Encoding.UTF8.GetString(capture.ToArray()).Contains("macroEnabled", StringComparison.OrdinalIgnoreCase))
                {
                    throw Unsupported("macro_enabled_not_allowed", "Documento con macro non ammesso");
                }
            }
        }
        catch (InvalidDataException)
        {
            throw Unsupported("format_mismatch", "Il contenuto non è un documento DOCX valido");
        }
    }

    private static UploadRejectedException Unsupported(string code, string title) => new(StatusCodes.Status415UnsupportedMediaType, code, title);

    private static UploadRejectedException Hostile(string title) => new(StatusCodes.Status422UnprocessableEntity, "hostile_archive", title);
}

/// <summary>
/// Stream che rilegge per primi i byte già letti per la verifica della firma e poi prosegue sul sorgente: la firma si controlla prima di
/// creare qualsiasi file, senza bufferizzare l'intero documento.
/// </summary>
internal sealed class HeadReplayStream(byte[] head, int headLength, Stream rest) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position < headLength)
        {
            var n = Math.Min(buffer.Length, headLength - _position);
            head.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        return rest.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < headLength)
        {
            var n = Math.Min(buffer.Length, headLength - _position);
            head.AsSpan(_position, n).CopyTo(buffer.Span);
            _position += n;
            return ValueTask.FromResult(n);
        }

        return rest.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
