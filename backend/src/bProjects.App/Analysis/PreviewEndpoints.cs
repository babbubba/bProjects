using System.Security.Claims;
using System.Text.Json;
using bProjects.App.Persistence;

namespace bProjects.App.Analysis;

/// <summary>
/// PUT preview e POST confirm (contracts/http-api.md, «Modifica e conferma dell'anteprima»). If-Match è l'ID della revisione
/// corrente in forma HTTP quotata; la risposta di successo è l'AnalysisView con il nuovo ETag. La conferma non avvia alcuna
/// generazione e nessuna delle due crea un ProcessingRun.
/// </summary>
public static class PreviewEndpoints
{
    public static IEndpointRouteBuilder MapPreviewEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapPut("/analyses/{analysisId:guid}/preview", Edit);
        group.MapPost("/analyses/{analysisId:guid}/confirm", Confirm);
        return group;
    }

    private static async Task<IResult> Edit(
        Guid projectId, Guid analysisId, HttpContext http, ClaimsPrincipal user, AppDbContext db, PreviewRevisionService service, CancellationToken ct)
    {
        if (!TryIfMatch(http, out var expected))
        {
            return Problem(StatusCodes.Status400BadRequest, "If-Match mancante o non valido", "revision_required");
        }

        if (!Guid.TryParse(user.FindFirstValue("sub"), out var author))
        {
            return Results.Unauthorized();
        }

        string contentJson;
        try
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
            var root = document.RootElement;
            // Il corpo è l'AnalysisContent; è tollerato anche l'inviluppo { "content": ... } dell'AnalysisView.
            if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("schemaVersion", out _)
                && root.TryGetProperty("content", out var inner) && inner.ValueKind == JsonValueKind.Object)
            {
                root = inner;
            }

            contentJson = root.GetRawText();
        }
        catch (JsonException)
        {
            return Problem(StatusCodes.Status400BadRequest, "Corpo JSON non valido", "malformed_request");
        }

        var result = await service.EditAsync(projectId, analysisId, expected, author, contentJson, ct);
        return await Respond(result, projectId, analysisId, http, db, ct);
    }

    private static async Task<IResult> Confirm(
        Guid projectId, Guid analysisId, HttpContext http, ClaimsPrincipal user, AppDbContext db, PreviewRevisionService service, CancellationToken ct)
    {
        if (!TryIfMatch(http, out var expected))
        {
            return Problem(StatusCodes.Status400BadRequest, "If-Match mancante o non valido", "revision_required");
        }

        if (!Guid.TryParse(user.FindFirstValue("sub"), out var confirmer))
        {
            return Results.Unauthorized();
        }

        var result = await service.ConfirmAsync(projectId, analysisId, expected, confirmer, ct);
        return await Respond(result, projectId, analysisId, http, db, ct);
    }

    private static async Task<IResult> Respond(PreviewResult result, Guid projectId, Guid analysisId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        switch (result.Status)
        {
            case PreviewStatus.Ok:
                // La risposta descrive la revisione che il comando ha scritto o confermato, non quella corrente dopo il commit: un PUT concorrente
                // può già aver aggiunto la successiva (mai una conferma mostrata come v2 non confermata).
                var built = await AnalysisEndpoints.BuildViewAsync(db, projectId, analysisId, ct, result.RevisionId);
                if (built?.View is not { } view)
                {
                    return Results.NotFound();
                }

                http.Response.Headers.ETag = $"\"{view.CurrentRevisionId}\"";
                return Results.Ok(view);
            case PreviewStatus.NotFound:
                return Results.NotFound();
            case PreviewStatus.AnalysisNotCurrent:
                return Problem(StatusCodes.Status409Conflict, "Solo l'analisi corrente del progetto è modificabile", "analysis_not_current");
            case PreviewStatus.ProcessingActive:
                return Problem(StatusCodes.Status409Conflict, "Un'analisi formale è già attiva", "processing_active", processingRunId: result.ProcessingRunId);
            case PreviewStatus.RevisionConflict:
                return Problem(StatusCodes.Status409Conflict, "La revisione indicata non è più la corrente", "revision_conflict", currentRevisionId: result.RevisionId);
            default:
                return Problem(
                    StatusCodes.Status422UnprocessableEntity,
                    "Anteprima non valida",
                    AnalysisContentValidator.PreviewInvalid,
                    errors: [.. (result.Violations ?? []).Select(ToFieldError)]);
        }
    }

    /// <summary>I messaggi del validatore hanno la forma «percorso: descrizione»; il percorso è il campo, mai contenuto dell'utente.</summary>
    private static object ToFieldError(string violation)
    {
        var split = violation.IndexOf(": ", StringComparison.Ordinal);
        return split > 0
            ? new { field = violation[..split], message = violation[(split + 2)..] }
            : new { field = "content", message = violation };
    }

    /// <summary>If-Match con l'ID di revisione, quotato come ETag (accettato anche senza virgolette).</summary>
    private static bool TryIfMatch(HttpContext http, out Guid revisionId)
    {
        revisionId = Guid.Empty;
        if (!http.Request.Headers.TryGetValue("If-Match", out var raw) || raw.Count != 1 || raw[0] is not { } value)
        {
            return false;
        }

        return Guid.TryParse(value.Trim().Trim('"'), out revisionId) && revisionId != Guid.Empty;
    }

    private static IResult Problem(
        int status, string title, string code, Guid? processingRunId = null, Guid? currentRevisionId = null, IEnumerable<object>? errors = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (processingRunId is not null)
        {
            extensions["processingRunId"] = processingRunId;
        }

        if (currentRevisionId is not null)
        {
            extensions["currentRevisionId"] = currentRevisionId;
        }

        if (errors is not null)
        {
            extensions["errors"] = errors;
        }

        return Results.Problem(statusCode: status, title: title, extensions: extensions);
    }
}
