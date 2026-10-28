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

    [GeneratedRegex(@"</?\s*(script|iframe|object|embed|style|link|meta|base|img|svg|form|input|button|video|audio|source)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousHtml();

    [GeneratedRegex(@"!\[[^\]]*\]\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"\]\(\s*(javascript|data|vbscript):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptLink();

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

        if (MarkdownImage().IsMatch(text))
        {
            violations.Add("markdown: le immagini non sono ammesse (nessun contenuto remoto).");
        }

        if (ScriptLink().IsMatch(text))
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
