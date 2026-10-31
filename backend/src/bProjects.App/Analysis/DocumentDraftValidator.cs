using System.Text.Json;
using System.Text.RegularExpressions;

namespace bProjects.App.Analysis;

/// <summary>Input fidato della validazione: le fonti della revisione confermata di input del run (binding di revisione).</summary>
public sealed record DocumentValidationContext(IReadOnlySet<Guid> SourceIds);

public sealed record DocumentValidationResult(bool Accepted, IReadOnlyList<string> Violations)
{
    public static DocumentValidationResult Ok { get; } = new(true, []);
}

/// <summary>
/// Validazione deterministica del Markdown prodotto dal DocumentAgent (contracts/ai-workflow.md §6): formato essenziale,
/// fonti e binding di revisione. Non giudica la qualità semantica, valutata sui criteri SC-003/SC-006. Nessun limite di parole.
/// <list type="bullet">
/// <item>testo non vuoto, non racchiuso in un recinto di codice, non un JSON e senza HTML pericoloso o immagini (nessun fetch remoto);</item>
/// <item>le fonti sono i marcatori <c>[fonte:&lt;id&gt;]</c>: ogni id deve essere una fonte della revisione di input del run, nessun
/// riferimento inventato né malformato; se la revisione ha fonti, il documento ne cita almeno una (i fatti rimandano a fonti).</item>
/// </list>
/// Un output non valido fa fallire il run con <c>invalid_agent_output</c>, senza riparazioni.
/// </summary>
public static partial class DocumentDraftValidator
{
    public const string InvalidAgentOutput = AnalysisContentValidator.InvalidAgentOutput;

    [GeneratedRegex(@"\[fonte:([^\]\s]*)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceMarker();

    [GeneratedRegex(@"</?\s*(script|iframe|object|embed|style|link|meta|base|img|svg|form|input|button|video|audio|source|a)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousHtml();

    // Qualsiasi apertura di immagine Markdown (inline `![a](u)`, per riferimento `![a][r]`, abbreviata `![a]`, con parentesi annidate).
    [GeneratedRegex(@"!\s*\[", RegexOptions.CultureInvariant)]
    private static partial Regex ImageStart();

    // Unica eccezione: `![fonte:<id>]` non seguito da `(` o `[` (un punto esclamativo seguito da un marcatore di fonte).
    [GeneratedRegex(@"\G!\s*\[fonte:[^\]\s]*\](?!\s*[(\[])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExclamationBeforeSourceMarker();

    // Destinazione di un collegamento inline: `](  <dest> "titolo")`.
    [GeneratedRegex(@"\]\s*\(\s*<?([^)]*)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineLinkDestination();

    // Definizione di riferimento: `[etichetta]: destinazione` (anche con la destinazione a capo).
    [GeneratedRegex(@"^[ \t]{0,3}\[(?:[^\]\r\n\\]|\\.)+\]:\s*<?(\S*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceDefinition();

    // Autolink `<schema:...>`.
    [GeneratedRegex(@"<\s*([^<>\s]*:[^<>]*)>", RegexOptions.CultureInvariant)]
    private static partial Regex Autolink();

    [GeneratedRegex(@"^([a-z][a-z0-9+.\-]*):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriScheme();

    /// <summary>
    /// Vero se la destinazione è ammessa: relativa/ancora, oppure http(s). Normalizza prima (entità HTML, percent-encoding,
    /// spazi e caratteri di controllo) così <c>java&amp;#x09;script:</c>, <c>JaVa ScRiPt:</c> e <c>%6Aavascript:</c> non sfuggono.
    /// Le entità numeriche/«colon» non risolvibili senza punto e virgola sono rifiutate a priori.
    /// </summary>
    private static bool IsAllowedDestination(string raw)
    {
        if (raw.Contains("&#", StringComparison.Ordinal)
            || raw.Contains("&colon", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("&tab", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("&newline", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = raw;
        for (var i = 0; i < 3; i++)
        {
            var decoded = System.Net.WebUtility.HtmlDecode(value);
            try
            {
                decoded = Uri.UnescapeDataString(decoded);
            }
            catch (UriFormatException)
            {
            }

            if (decoded == value)
            {
                break;
            }

            value = decoded;
        }

        value = new string(value.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
        var scheme = UriScheme().Match(value);
        return !scheme.Success
            || scheme.Groups[1].Value.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Groups[1].Value.Equals("https", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasForbiddenImage(string text)
    {
        foreach (Match start in ImageStart().Matches(text))
        {
            if (!ExclamationBeforeSourceMarker().IsMatch(text, start.Index))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasForbiddenScheme(string text) =>
        InlineLinkDestination().Matches(text).Any(m => !IsAllowedDestination(m.Groups[1].Value))
        || ReferenceDefinition().Matches(text).Any(m => !IsAllowedDestination(m.Groups[1].Value.TrimEnd('>')))
        || Autolink().Matches(text).Any(m => !IsAllowedDestination(m.Groups[1].Value));

    public static DocumentValidationResult Validate(string? markdown, DocumentValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new(false, ["markdown: il documento è vuoto."]);
        }

        var violations = new List<string>();
        var text = markdown.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) || text.StartsWith("~~~", StringComparison.Ordinal))
        {
            violations.Add("markdown: il documento non deve essere racchiuso in un recinto di codice.");
        }

        if (text.StartsWith('{') && IsJson(text))
        {
            violations.Add("markdown: l'output è JSON, non un documento Markdown.");
        }

        if (DangerousHtml().IsMatch(text))
        {
            violations.Add("markdown: HTML non ammesso (script, frame, immagini, moduli).");
        }

        if (HasForbiddenImage(text))
        {
            violations.Add("markdown: le immagini non sono ammesse (nessun contenuto remoto).");
        }

        if (HasForbiddenScheme(text))
        {
            violations.Add("markdown: collegamento con schema non ammesso.");
        }

        var markers = SourceMarker().Matches(text);
        foreach (Match marker in markers)
        {
            var id = marker.Groups[1].Value;
            if (!Guid.TryParse(id, out var source))
            {
                violations.Add("fonti: marcatore [fonte:...] malformato (atteso un UUID).");
            }
            else if (!context.SourceIds.Contains(source))
            {
                violations.Add($"fonti: la fonte {source} non appartiene alla revisione confermata di input.");
            }
        }

        if (markers.Count == 0 && context.SourceIds.Count > 0)
        {
            violations.Add("fonti: il documento non cita alcuna fonte [fonte:<id>] della revisione confermata.");
        }

        return violations.Count == 0 ? DocumentValidationResult.Ok : new(false, [.. violations.Distinct()]);
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
