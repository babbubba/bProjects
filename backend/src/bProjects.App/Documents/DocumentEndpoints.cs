using System.Security.Claims;
using System.Globalization;
using System.Text;
using System.Text.Json;
using bProjects.App.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace bProjects.App.Documents;

/// <summary>
/// Archivio documenti del progetto (contracts/http-api.md §Documenti, T014): upload di nuovi documenti e versioni (multipart in streaming,
/// idempotente), lista a cursore, dettaglio, versione e download degli originali. Ogni identificativo è cercato sotto <c>{projectId}</c>:
/// la risorsa di un altro progetto è 404. Nessuna risposta contiene percorsi di storage, chiavi o URL pubbliche; il download è
/// <c>attachment</c>, <c>no-store</c> e <c>nosniff</c> e solo dopo la verifica di lunghezza e SHA-256.
/// </summary>
public static class ProjectDocumentEndpoints
{
    /// <summary>Margine sul limite di file per intestazioni e campi del multipart nel tetto di richiesta.</summary>
    private const long MultipartAllowance = 1_048_576;
    private const int MaxDisplayNameInput = 1024;

    public static IEndpointRouteBuilder MapProjectDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}");
        group.MapPost("/documents", (Guid projectId, HttpContext http, ClaimsPrincipal user, AppDbContext db, DocumentPolicyService policies, DocumentUploadService uploads, DocumentCatalog catalog, CancellationToken ct) =>
            Upload(projectId, null, http, user, db, policies, uploads, catalog, ct));
        group.MapPost("/documents/{documentId:guid}/versions", (Guid projectId, Guid documentId, HttpContext http, ClaimsPrincipal user, AppDbContext db, DocumentPolicyService policies, DocumentUploadService uploads, DocumentCatalog catalog, CancellationToken ct) =>
            Upload(projectId, documentId, http, user, db, policies, uploads, catalog, ct));
        group.MapGet("/documents", List);
        group.MapGet("/documents/{documentId:guid}", GetDocument);
        group.MapGet("/documents/{documentId:guid}/versions/{version}", GetVersion);
        group.MapGet("/documents/{documentId:guid}/versions/{version}/download", Download);
        group.MapGet("/documents/{documentId:guid}/versions/{version}/preparations/{generation}", GetPreparation);
        group.MapGet("/documents/{documentId:guid}/versions/{version}/preparations/{generation}/blocks", ListBlocks);
        group.MapGet("/documents/{documentId:guid}/versions/{version}/preparations/{generation}/passages/{blockId:guid}", GetPassage);
        group.MapPost("/document-jobs/{jobId:guid}/retry", RetryParse);
        return app;
    }

    // ================================================================ upload

    private static async Task<IResult> Upload(
        Guid projectId, Guid? documentId, HttpContext http, ClaimsPrincipal user, AppDbContext db,
        DocumentPolicyService policies, DocumentUploadService uploads, DocumentCatalog catalog, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var uploadedBy))
        {
            return Results.Unauthorized();
        }

        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, ct)
            || (documentId is { } d && !await db.ProjectDocuments.AsNoTracking().AnyAsync(x => x.Id == d && x.ProjectId == projectId, ct)))
        {
            return Results.NotFound();
        }

        if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var raw) || raw.Count != 1
            || !Guid.TryParse(raw[0], out var requestKey) || requestKey == Guid.Empty)
        {
            return Problem(StatusCodes.Status400BadRequest, "idempotency_key_required", "Idempotency-Key mancante o non valida");
        }

        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(contentType.Boundary).Value is not { Length: > 0 and <= 128 } boundary)
        {
            return Problem(StatusCodes.Status400BadRequest, "malformed_request", "La richiesta deve essere multipart/form-data con un file");
        }

        var (_, policy) = await policies.GetAsync(ct);
        var limits = policy.Limits;
        var bodyCap = limits.MaxFileBytes + MultipartAllowance;
        if (http.Request.ContentLength > bodyCap)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "file_too_large", "Il file supera il limite consentito");
        }

        var bodyLimit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false })
        {
            bodyLimit.MaxRequestBodySize = bodyCap;   // il tetto globale di Kestrel (30 MB) non deve tagliare i 50 MB consentiti
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.UploadTimeoutSeconds));
        var token = timeout.Token;

        UploadOpening? opening = null;
        var done = false;
        try
        {
            var reader = new MultipartReader(boundary, http.Request.Body) { HeadersCountLimit = 8, HeadersLengthLimit = 8192, BodyLengthLimit = bodyCap };
            string? displayName = null;
            string? originalName = null;
            ReceivedFile? received = null;
            (long Length, string Sha256)? replayed = null;

            while (await reader.ReadNextSectionAsync(token) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.DispositionType.Equals("form-data"))
                {
                    return Problem(StatusCodes.Status400BadRequest, "malformed_request", "Parte multipart non valida");
                }

                var field = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                if (field == "file")
                {
                    if (originalName is not null)
                    {
                        return Problem(StatusCodes.Status400BadRequest, "malformed_request", "È ammesso un solo file per richiesta");
                    }

                    var rawName = disposition.FileNameStar.HasValue ? disposition.FileNameStar.Value : disposition.FileName.Value;
                    originalName = DocumentUploadValidation.SanitizeFileName(HeaderUtilities.RemoveQuotes(new StringSegment(rawName)).Value);
                    var format = DocumentUploadValidation.DetectFormat(originalName, section.ContentType);

                    opening = await uploads.OpenAsync(projectId, requestKey, documentId, http.Request.ContentLength, limits, token);
                    switch (opening.Kind)
                    {
                        case UploadOpenKind.InProgress:
                            var receiptId = opening.ReceiptId;
                            opening = null;   // non è nostra: non va annullata
                            return Results.Problem(
                                statusCode: StatusCodes.Status409Conflict, title: "Caricamento ancora in ricezione",
                                extensions: new Dictionary<string, object?> { ["code"] = "upload_in_progress", ["receiptId"] = receiptId });
                        case UploadOpenKind.Conflict:
                            opening = null;
                            return IdempotencyConflict();
                        case UploadOpenKind.QuotaExhausted:
                            opening = null;
                            return Problem(StatusCodes.Status507InsufficientStorage, "quota_exceeded", "Quota dei documenti del progetto esaurita");
                        case UploadOpenKind.Replay:
                            replayed = await DocumentUploadService.HashOnlyAsync(section.Body, limits.MaxFileBytes, token);
                            break;
                        default:
                            received = await uploads.ReceiveAsync(opening, projectId, format, section.Body, limits, token);
                            break;
                    }
                }
                else if (field == "displayName" && documentId is null && displayName is null)
                {
                    displayName = await ReadSmallTextAsync(section.Body, token);
                }
                else
                {
                    return Problem(StatusCodes.Status400BadRequest, "malformed_request", "Campo multipart non ammesso o ripetuto");
                }
            }

            if (originalName is null || opening is null)
            {
                return Problem(StatusCodes.Status400BadRequest, "malformed_request", "File mancante");
            }

            var effectiveName = documentId is not null ? string.Empty : EffectiveDisplayName(displayName, originalName);

            if (opening.Kind == UploadOpenKind.Replay)
            {
                if (replayed is not { } replay || replay.Length == 0
                    || DocumentUploadService.InputHash(documentId, effectiveName, originalName, replay.Sha256) != opening.ReceiptInputHash)
                {
                    return IdempotencyConflict();
                }

                var existing = await ViewOfVersionAsync(catalog, db, projectId, opening.ResultVersionId!.Value, ct);
                return existing is null ? Results.NotFound() : Results.Ok(existing);   // replay: nessun lavoro nuovo
            }

            var commit = await uploads.CommitAsync(opening, projectId, documentId, received!, originalName, effectiveName, uploadedBy, limits, policy.PolicyHash, token);
            switch (commit.Kind)
            {
                case UploadCommitKind.Created:
                    done = true;
                    var view = await ViewOfVersionAsync(catalog, db, projectId, commit.VersionId!.Value, ct);
                    return Results.Created($"/api/projects/{projectId}/documents/{view!.DocumentId}/versions/{view.Version}", view);
                case UploadCommitKind.Conflict:
                    done = true;   // la ricevuta è già Aborted dal servizio
                    return IdempotencyConflict();
                case UploadCommitKind.QuotaExhausted:
                    done = true;
                    return Problem(StatusCodes.Status507InsufficientStorage, "quota_exceeded", "Quota dei documenti del progetto esaurita");
                default:
                    done = true;
                    return Problem(StatusCodes.Status503ServiceUnavailable, "upload_expired", "Il caricamento è scaduto prima di essere acquisito: ripetere con la stessa chiave");
            }
        }
        catch (UploadRejectedException rejected)
        {
            return Problem(rejected.Status, rejected.Code, rejected.Title);
        }
        catch (InvalidDataException)
        {
            return Problem(StatusCodes.Status400BadRequest, "malformed_request", "Multipart non valido");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Problem(StatusCodes.Status408RequestTimeout, "upload_timeout", "Ricezione del file oltre il tempo consentito: nulla è stato acquisito");
        }
        finally
        {
            // Qualunque uscita senza acquisizione rilascia la prenotazione di quota; l'originale eventuale resta per il replay.
            if (opening is { Kind: UploadOpenKind.Proceed } && !done)
            {
                await uploads.AbortAsync(opening.ReceiptId);
            }
        }
    }

    private static async Task<DocumentVersionView?> ViewOfVersionAsync(DocumentCatalog catalog, AppDbContext db, Guid projectId, Guid versionId, CancellationToken ct)
    {
        var documentId = await db.DocumentVersions.AsNoTracking().Where(v => v.Id == versionId && v.ProjectId == projectId)
            .Select(v => (Guid?)v.DocumentId).SingleOrDefaultAsync(ct);
        return documentId is null ? null : await catalog.GetVersionAsync(projectId, documentId.Value, versionId.ToString(), ct);
    }

    private static async Task<string> ReadSmallTextAsync(Stream body, CancellationToken ct)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var buffer = new char[MaxDisplayNameInput + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        if (read > MaxDisplayNameInput)
        {
            throw new UploadRejectedException(StatusCodes.Status422UnprocessableEntity, "invalid_display_name", "Nome del documento troppo lungo");
        }

        return new string(buffer, 0, read);
    }

    /// <summary>Etichetta del documento: quella del PM se presente, altrimenti il nome del file. Non è l'identità e non è unica.</summary>
    private static string EffectiveDisplayName(string? requested, string originalName)
    {
        var cleaned = requested is null ? string.Empty : new string(requested.Where(c => !char.IsControl(c)).ToArray()).Trim();
        var name = cleaned.Length > 0 ? cleaned : originalName;
        if (name.Length > ProjectDocument.MaxDisplayNameLength)
        {
            throw new UploadRejectedException(StatusCodes.Status422UnprocessableEntity, "invalid_display_name", "Nome del documento troppo lungo");
        }

        return name;
    }

    // ================================================================ letture

    private static async Task<IResult> List(Guid projectId, string? cursor, int? limit, AppDbContext db, DocumentCatalog catalog, HttpContext http, CancellationToken ct)
    {
        var size = limit ?? DocumentCatalog.DefaultLimit;
        if (size is < 1 or > DocumentCatalog.MaxLimit)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_limit", $"limit deve essere compreso tra 1 e {DocumentCatalog.MaxLimit}");
        }

        (DateTimeOffset At, Guid Id)? after = null;
        if (cursor is not null)
        {
            if (!DocumentCatalog.TryDecodeCursor(cursor, out var decoded))
            {
                return Problem(StatusCodes.Status400BadRequest, "invalid_cursor", "Cursore non valido");
            }

            after = decoded;
        }

        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, ct))
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await catalog.ListAsync(projectId, after, size, ct));
    }

    private static async Task<IResult> GetDocument(Guid projectId, Guid documentId, DocumentCatalog catalog, HttpContext http, CancellationToken ct)
    {
        var detail = await catalog.GetAsync(projectId, documentId, ct);
        http.Response.Headers.CacheControl = "no-store";
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    private static async Task<IResult> GetVersion(Guid projectId, Guid documentId, string version, DocumentCatalog catalog, HttpContext http, CancellationToken ct)
    {
        var view = await catalog.GetVersionAsync(projectId, documentId, version, ct);
        http.Response.Headers.CacheControl = "no-store";
        return view is null ? Results.NotFound() : Results.Ok(view);
    }

    private static async Task<IResult> Download(
        Guid projectId, Guid documentId, string version, DocumentCatalog catalog, DocumentFileStore store, HttpContext http, ILogger<DocumentFileStore> logger, CancellationToken ct)
    {
        var found = await catalog.FindVersionAsync(projectId, documentId, version, ct);
        if (found is null)
        {
            return Results.NotFound();
        }

        FileStream stream;
        try
        {
            stream = await store.OpenVerifiedAsync(found.StorageKey, found.Sha256, found.ByteLength, ct);
        }
        catch (DocumentStoreException ex)
        {
            // Byte alterati o file mancante: mai servirli. Nessun percorso né nome nel messaggio al client.
            logger.LogError("Download rifiutato per la versione {VersionId}: {Reason}", found.Id, ex.Reason);
            return ex.Reason == DocumentStoreFailure.Unavailable
                ? Problem(StatusCodes.Status503ServiceUnavailable, "storage_unavailable", "Archiviazione temporaneamente non disponibile")
                : Problem(StatusCodes.Status500InternalServerError, "original_unavailable", "L'originale non è verificabile");
        }

        var headers = http.Response.Headers;
        headers.CacheControl = "no-store";
        headers["Pragma"] = "no-cache";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Security-Policy"] = "sandbox";
        return Results.File(stream, found.MediaType, fileDownloadName: DownloadName(found.OriginalName), enableRangeProcessing: false);
    }

    /// <summary>Nome per Content-Disposition: ultimo segmento, senza caratteri di controllo né separatori (difesa oltre il nome già sanificato all'upload).</summary>
    private static string DownloadName(string stored)
    {
        try
        {
            return DocumentUploadValidation.SanitizeFileName(stored);
        }
        catch (UploadRejectedException)
        {
            return "documento";
        }
    }

    // ================================================================ preparazione, blocchi, passaggi (T030)

    private const int DefaultBlockLimit = 50;
    private const int MaxBlockLimit = 100;

    /// <summary>
    /// Retry manuale del parsing (T116): solo POST, mai da GET o da polling. Idempotency-Key UUID obbligatoria; risponde 202 con il job e la
    /// preparazione collegati (replay: gli stessi), 409 con codice stabile se il terminale non e ritentabile, e 404 fuori dal progetto.
    /// </summary>
    private static async Task<IResult> RetryParse(
        Guid projectId, Guid jobId, HttpContext http, ClaimsPrincipal user, AppDbContext db, DocumentPreparationService preparations, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var requestedBy))
        {
            return Results.Unauthorized();
        }

        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, ct))
        {
            return Results.NotFound();
        }

        if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var raw) || raw.Count != 1
            || !Guid.TryParse(raw[0], out var requestKey) || requestKey == Guid.Empty)
        {
            return Problem(StatusCodes.Status400BadRequest, "idempotency_key_required", "Idempotency-Key mancante o non valida");
        }

        var result = await preparations.RetryParseAsync(projectId, jobId, requestKey, requestedBy, ct);
        http.Response.Headers.CacheControl = "no-store";
        return result.Kind switch
        {
            PreparationRetryKind.NotFound => Results.NotFound(),
            PreparationRetryKind.Conflict => Problem(StatusCodes.Status409Conflict, result.Code!, result.Code switch
            {
                "idempotency_conflict" => "La stessa chiave e stata usata per un altra richiesta",
                "job_active" => "Il job e ancora in corso",
                "preparation_active" => "Esiste gia una preparazione attiva per questa versione",
                "job_superseded" => "Una preparazione piu recente ha gia sostituito questo job",
                _ => "Il job non e in uno stato ritentabile",
            }),
            _ => Results.Json(
                new ParseRetryView(result.JobId, result.PreparationId, result.Generation, result.RetryOfJobId, result.RetryOfPreparationId, "Queued", result.RequestedAt, result.DeadlineAt),
                statusCode: StatusCodes.Status202Accepted),
        };
    }

    private static async Task<IResult> GetPreparation(
        Guid projectId, Guid documentId, string version, string generation, DocumentCatalog catalog, AppDbContext db, DocumentPreparationService preparations, HttpContext http, CancellationToken ct)
    {
        var found = await FindPreparationAsync(projectId, documentId, version, generation, catalog, db, ct);
        if (found is null)
        {
            return Results.NotFound();
        }

        var (v, p) = found.Value;
        var artifact = await db.ParsedArtifacts.AsNoTracking().Where(a => a.PreparationId == p.Id).Select(a => new { a.BlockCount }).SingleOrDefaultAsync(ct);
        var issues = await db.PreparationIssues.AsNoTracking().Where(i => i.PreparationId == p.Id).OrderBy(i => i.Code).ThenBy(i => i.Id).ToListAsync(ct);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new PreparationView(
            p.Id, v.Id, v.DocumentId, v.Version, p.Generation, p.Status.ToString(), p.Status == PreparationStatus.Ready,
            // «Parsed»: il parsing è pubblicato e leggibile, ma finché lo stato non è Ready la fonte non è utilizzabile.
            artifact is null ? "NotParsed" : p.Status == PreparationStatus.Ready ? "Ready" : "Parsed",
            artifact?.BlockCount, p.RetryOfId, p.RequestedAt,
            p.ErrorCode is { } code ? new IssuesSummary(code, IssuesSummary.FixedMessage) : null,
            issues.Select(i => new PreparationIssueView(i.Code, i.Severity.ToString(), i.Page, i.BlockId, i.Reason, i.AffectsCompleteness)).ToList(),
            await preparations.IsRetryAllowedAsync(p, ct) ? ["download", "retry"] : ["download"], p.ParseJobId));
    }

    private static async Task<IResult> ListBlocks(
        Guid projectId, Guid documentId, string version, string generation, string? cursor, int? limit, DocumentCatalog catalog, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var size = limit ?? DefaultBlockLimit;
        if (size is < 1 or > MaxBlockLimit)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_limit", $"limit deve essere compreso tra 1 e {MaxBlockLimit}");
        }

        var after = -1;
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 0))
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_cursor", "Cursore non valido");
        }

        var found = await FindPreparationAsync(projectId, documentId, version, generation, catalog, db, ct);
        if (found is null)
        {
            return Results.NotFound();
        }

        var (_, p) = found.Value;
        var artifactId = await db.ParsedArtifacts.AsNoTracking().Where(a => a.PreparationId == p.Id).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(ct);
        if (artifactId is null)
        {
            return Problem(StatusCodes.Status409Conflict, "parsing_not_completed", "Il contenuto del documento non è ancora disponibile");
        }

        var rows = await db.DocumentBlocks.AsNoTracking().Where(b => b.ArtifactId == artifactId && b.Ordinal > after)
            .OrderBy(b => b.Ordinal).Take(size + 1).ToListAsync(ct);
        var more = rows.Count > size;
        var page = rows.Take(size).ToList();
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new BlockPage(
            p.Id, p.Status.ToString(), p.Status == PreparationStatus.Ready, page.Select(BlockOf).ToList(),
            more ? page[^1].Ordinal.ToString(CultureInfo.InvariantCulture) : null));
    }

    private static async Task<IResult> GetPassage(
        Guid projectId, Guid documentId, string version, string generation, Guid blockId, int? start, int? end,
        DocumentCatalog catalog, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var found = await FindPreparationAsync(projectId, documentId, version, generation, catalog, db, ct);
        if (found is null)
        {
            return Results.NotFound();
        }

        var (v, p) = found.Value;
        var block = await db.DocumentBlocks.AsNoTracking()
            .Where(b => b.Id == blockId && b.ProjectId == projectId && db.ParsedArtifacts.Any(a => a.Id == b.ArtifactId && a.PreparationId == p.Id))
            .SingleOrDefaultAsync(ct);
        if (block is null)
        {
            return Results.NotFound();
        }

        QuoteRange? quote = null;
        var text = block.CanonicalText;
        if (start is not null || end is not null)
        {
            // Range [start,end) UTF-16 sul testo canonico: entrambi i limiti, non vuoto, dentro il blocco, mai in mezzo a una coppia surrogata.
            if (start is not { } s || end is not { } e || s < 0 || e <= s || e > text.Length
                || (s > 0 && char.IsLowSurrogate(text[s]) && char.IsHighSurrogate(text[s - 1]))
                || (e < text.Length && char.IsLowSurrogate(text[e]) && char.IsHighSurrogate(text[e - 1])))
            {
                return Problem(StatusCodes.Status422UnprocessableEntity, "invalid_range", "Intervallo non valido per il blocco");
            }

            quote = new QuoteRange(s, e, text[..s], text[s..e], text[e..]);
        }

        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new PassageView(
            BlockOf(block), p.Id, p.Status.ToString(), p.Status == PreparationStatus.Ready, quote,
            new OriginalLink(v.OriginalName, $"/api/projects/{projectId}/documents/{documentId}/versions/{v.Version}/download")));
    }

    private static async Task<(DocumentVersion Version, DocumentPreparation Preparation)?> FindPreparationAsync(
        Guid projectId, Guid documentId, string version, string generation, DocumentCatalog catalog, AppDbContext db, CancellationToken ct)
    {
        var v = await catalog.FindVersionAsync(projectId, documentId, version, ct);
        if (v is null)
        {
            return null;
        }

        // Generazione numerica, id della preparazione oppure «latest» (la più recente, anche non pronta).
        var query = db.DocumentPreparations.AsNoTracking().Where(x => x.ProjectId == projectId && x.DocumentVersionId == v.Id);
        var p = generation == "latest" ? await query.OrderByDescending(x => x.Generation).FirstOrDefaultAsync(ct)
            : int.TryParse(generation, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? await query.SingleOrDefaultAsync(x => x.Generation == number, ct)
            : Guid.TryParse(generation, out var id) ? await query.SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        return p is null ? null : (v, p);
    }

    private static BlockView BlockOf(DocumentBlock b) => new(
        b.Id, b.Ref, b.Ordinal, b.ParentId, b.Type.ToString(), b.CanonicalText,
        JsonSerializer.Deserialize<string[]>(b.HeadingPathJson) ?? [],
        b.Page,
        b.BboxJson is null ? null : new BboxView(JsonSerializer.Deserialize<double[]>(b.BboxJson)!, b.CoordinateSystem),
        b.LineStart is { } ls && b.LineEnd is { } le ? new LineRange(ls, le) : null,
        b.TableCellsJson is null ? null : JsonSerializer.Deserialize<JsonElement>(b.TableCellsJson));

    private static IResult IdempotencyConflict() => Problem(
        StatusCodes.Status409Conflict, "idempotency_conflict", "La chiave di idempotenza è già stata usata con un contenuto diverso");

    private static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}

// ---- viste (T030): solo testo e metadati; mai HTML, chiavi di storage o prompt ---------------------------------------------------------

public sealed record PreparationIssueView(string Code, string Severity, int? Page, Guid? BlockId, string Reason, bool AffectsCompleteness);

/// <summary><c>ProcessingStatus</c> è lo stato reale; <c>IsReady</c> è vero solo per Ready. <c>ContentState</c>: NotParsed, Parsed (blocchi leggibili ma non pronto) o Ready.</summary>
public sealed record PreparationView(
    Guid Id, Guid DocumentVersionId, Guid DocumentId, int Version, int Generation, string ProcessingStatus, bool IsReady, string ContentState,
    int? BlockCount, Guid? RetryOfId, DateTimeOffset RequestedAt, IssuesSummary? IssuesSummary,
    IReadOnlyList<PreparationIssueView> Issues, IReadOnlyList<string> AllowedActions, Guid? ParseJobId);

/// <summary>Esito del retry manuale: job e preparazione nuovi, collegati ai precedenti; mai testo del documento né percorsi.</summary>
public sealed record ParseRetryView(
    Guid JobId, Guid PreparationId, int Generation, Guid RetryOfJobId, Guid RetryOfPreparationId, string Status, DateTimeOffset RequestedAt, DateTimeOffset DeadlineAt);

public sealed record BboxView(double[] Values, string? CoordinateSystem);

public sealed record LineRange(int Start, int End);

public sealed record BlockView(
    Guid Id, string Ref, int Ordinal, Guid? ParentId, string Type, string Text, string[] HeadingPath, int? Page,
    BboxView? Bbox, LineRange? Lines, JsonElement? TableCells);

public sealed record BlockPage(Guid PreparationId, string ProcessingStatus, bool IsReady, IReadOnlyList<BlockView> Items, string? NextCursor);

public sealed record QuoteRange(int Start, int End, string Before, string Text, string After);

public sealed record OriginalLink(string OriginalName, string DownloadUrl);

public sealed record PassageView(BlockView Block, Guid PreparationId, string ProcessingStatus, bool IsReady, QuoteRange? Quote, OriginalLink Original);
