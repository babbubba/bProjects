using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using bProjects.App.Identity;

namespace bProjects.App.Documents;

/// <summary>
/// GET/PUT <c>/api/admin/document-limits</c> (contracts/http-api.md): solo ruolo Admin, solo limiti non segreti. If-Match è la revisione
/// corrente quotata come ETag; l'applicazione è atomica (UPDATE condizionato) e i nuovi lavori congelano la policy al momento della richiesta.
/// Nessun endpoint può cambiare modello, percorsi di storage o comando del parser.
/// </summary>
public static class DocumentLimitEndpoints
{
    private const int MaxBodyBytes = 16 * 1024;

    public sealed record PolicyView(long Revision, DateTimeOffset UpdatedAt, Guid? UpdatedBy, string PolicyHash, DocumentLimits Limits);

    public static IEndpointRouteBuilder MapDocumentLimitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/document-limits").RequireAuthorization(AccountBootstrap.AdminPolicy);
        group.MapGet("", Get);
        group.MapPut("", Put);
        return app;
    }

    private static async Task<IResult> Get(HttpContext http, DocumentPolicyService policies, CancellationToken ct)
    {
        var (row, snapshot) = await policies.GetAsync(ct);
        return Ok(http, row.Revision, new PolicyView(row.Revision, row.UpdatedAt, row.UpdatedBy, snapshot.PolicyHash, snapshot.Limits));
    }

    private static async Task<IResult> Put(HttpContext http, ClaimsPrincipal user, DocumentPolicyService policies, CancellationToken ct)
    {
        if (!TryIfMatch(http, out var expected))
        {
            return Problem(StatusCodes.Status400BadRequest, "If-Match mancante o non valido", "revision_required");
        }

        if (!Guid.TryParse(user.FindFirstValue("sub"), out var author))
        {
            return Results.Unauthorized();
        }

        string body;
        using (var reader = new StreamReader(http.Request.Body))
        {
            var buffer = new char[MaxBodyBytes + 1];
            var read = await reader.ReadBlockAsync(buffer, ct);
            if (read > MaxBodyBytes)
            {
                return Problem(StatusCodes.Status413PayloadTooLarge, "Corpo troppo grande", "payload_too_large");
            }

            body = new string(buffer, 0, read);
        }

        DocumentLimits limits;
        try
        {
            limits = DocumentLimits.FromJson(body);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            // Corpo malformato, campo sconosciuto o mancante: l'errore del client non riporta il contenuto.
            return Problem(StatusCodes.Status400BadRequest, "Limiti malformati: campi mancanti, sconosciuti o di tipo errato", "malformed_request");
        }

        switch (await policies.UpdateAsync(limits, expected, author, ct))
        {
            case PolicyUpdateResult.Updated updated:
                var view = new PolicyView(updated.Policy.Revision, updated.UpdatedAt, author, updated.Policy.PolicyHash, updated.Policy.Limits);
                return Ok(http, updated.Policy.Revision, view);
            case PolicyUpdateResult.Conflict conflict:
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Revisione dei limiti non aggiornata",
                    extensions: new Dictionary<string, object?> { ["code"] = "revision_conflict", ["currentRevision"] = conflict.CurrentRevision });
            case PolicyUpdateResult.Invalid invalid:
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity, title: "Limiti non validi",
                    extensions: new Dictionary<string, object?> { ["code"] = "limits_invalid", ["errors"] = invalid.Violations.Select(ToFieldError).ToArray() });
            default:
                throw new InvalidOperationException();
        }
    }

    private static object ToFieldError(string violation)
    {
        var split = violation.IndexOf(": ", StringComparison.Ordinal);
        return new { field = violation[..split], message = violation[(split + 2)..] };
    }

    private static IResult Ok(HttpContext http, long revision, PolicyView view)
    {
        http.Response.Headers.ETag = $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(view);
    }

    /// <summary>If-Match con il numero di revisione, quotato come ETag (accettato anche senza virgolette).</summary>
    private static bool TryIfMatch(HttpContext http, out long revision)
    {
        revision = 0;
        if (!http.Request.Headers.TryGetValue("If-Match", out var raw) || raw.Count != 1 || raw[0] is not { } value)
        {
            return false;
        }

        return long.TryParse(value.Trim().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out revision) && revision > 0;
    }

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
