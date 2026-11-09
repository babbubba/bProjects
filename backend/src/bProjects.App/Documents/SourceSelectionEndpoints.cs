using System.Globalization;
using System.Security.Claims;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Documents;

/// <summary>
/// GET/PUT <c>/api/projects/{p}/source-selection</c> (contracts/http-api.md §Selezione persistente): preferenze di fonte con ETag quotato
/// della revisione e If-Match obbligatorio. La lettura non crea righe né job. Il PUT sostituisce l'intero elenco sotto il lock di progetto:
/// a parità di revisione un solo scrittore vince, l'altro riceve 409 source_selection_conflict con la revisione corrente.
/// </summary>
public static class SourceSelectionEndpoints
{
    public sealed record SelectionItemRequest(Guid? DocumentId, bool? Included, string? Mode, Guid? VersionId);

    public sealed record PutSelectionRequest(IReadOnlyList<SelectionItemRequest>? Items);

    public static IEndpointRouteBuilder MapSourceSelectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/source-selection");
        group.MapGet("", Get);
        group.MapPut("", Put);
        return app;
    }

    private static async Task<IResult> Get(Guid projectId, HttpContext http, AppDbContext db, SourceSelectionService selection, CancellationToken ct)
    {
        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, ct))
        {
            return Results.NotFound();
        }

        return Ok(http, await selection.ViewAsync(projectId, ct));
    }

    private static async Task<IResult> Put(
        Guid projectId, PutSelectionRequest? request, HttpContext http, ClaimsPrincipal user, AppDbContext db, SourceSelectionService selection, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var author))
        {
            return Results.Unauthorized();
        }

        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, ct))
        {
            return Results.NotFound();
        }

        if (!TryIfMatch(http, out var expected))
        {
            return Problem(StatusCodes.Status400BadRequest, "revision_required", "If-Match mancante o non valido");
        }

        if (request?.Items is not { } items)
        {
            return Problem(StatusCodes.Status400BadRequest, "malformed_request", "items è obbligatorio");
        }

        var inputs = new List<SelectionItemInput>(items.Count);
        foreach (var item in items)
        {
            if (item?.DocumentId is not { } documentId || item.Included is not { } included
                || !Enum.TryParse<SourceMode>(item.Mode, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
            {
                return Problem(StatusCodes.Status400BadRequest, "malformed_request", "Ogni item richiede documentId, included e mode (LatestReady o Pinned)");
            }

            inputs.Add(new(documentId, included, mode, item.VersionId));
        }

        switch (await selection.ReplaceAsync(projectId, expected, inputs, author, ct))
        {
            case SelectionUpdate.Applied applied:
                return Ok(http, applied.View);
            case SelectionUpdate.Conflict conflict:
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Le preferenze di fonte sono cambiate",
                    extensions: new Dictionary<string, object?> { ["code"] = "source_selection_conflict", ["currentRevision"] = conflict.CurrentRevision });
            case SelectionUpdate.Invalid invalid:
                return Problem(invalid.Status, invalid.Code, invalid.Title);
            default:
                throw new InvalidOperationException();
        }
    }

    private static IResult Ok(HttpContext http, SourceSelectionView view)
    {
        http.Response.Headers.ETag = $"\"{view.Revision.ToString(CultureInfo.InvariantCulture)}\"";
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(view);
    }

    /// <summary>If-Match con la revisione, quotata come ETag (accettata anche senza virgolette). La revisione iniziale è 0.</summary>
    private static bool TryIfMatch(HttpContext http, out long revision)
    {
        revision = 0;
        if (!http.Request.Headers.TryGetValue("If-Match", out var raw) || raw.Count != 1 || raw[0] is not { } value)
        {
            return false;
        }

        return long.TryParse(value.Trim().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out revision);
    }

    private static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
