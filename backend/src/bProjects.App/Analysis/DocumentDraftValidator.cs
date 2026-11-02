using System.Text.Json;
using System.Text.RegularExpressions;

namespace bProjects.App.Analysis;

/// <summary>Input fidato della validazione: le fonti della revisione confermata di input del run (binding di revisione).</summary>
public sealed record DocumentValidationContext(IReadOnlySet<Guid> SourceIds);

public sealed record DocumentValidationResult(bool Accepted, IReadOnlyList<string> Violations)
{
    public static DocumentValidationResult Ok { get; } = new(true, []);
}

/// <summary>Blocco dell'output chiuso della generazione documentale v2: item della revisione, Markdown e fonti citate (una per marcatore).</summary>
public sealed record DraftBlock(IReadOnlyList<Guid> ItemIds, string Markdown, IReadOnlyList<Guid> SourceIds);

/// <summary>Output chiuso <c>{sectionKey,blocks:[{itemIds,markdown,sourceIds}]}</c> (contracts/ai-workflow.md §4.5).</summary>
public sealed record DraftOutput(string SectionKey, IReadOnlyList<DraftBlock> Blocks);

/// <summary>Pacchetto fidato della generazione: item e fonti della revisione confermata 1.1 di input, mai ricavati dall'output del modello.</summary>
public sealed record DocumentDraftPackage(IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> ItemSources, IReadOnlySet<Guid> SourceIds)
{
    /// <summary>Tutti gli item della revisione, alternative dei conflitti comprese, ciascuno con le fonti che lo sostengono (evidenceRefs).</summary>
    public static DocumentDraftPackage From(DocumentAnalysisContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var items = new[]
            {
                content.Goals, content.ContextAndScope.Overview, content.ContextAndScope.Included, content.ContextAndScope.Excluded, content.Requirements,
                content.OperationalFlows, content.Constraints, content.Dependencies, content.Assumptions, content.OpenQuestions,
            }
            .SelectMany(section => section)
            .Concat(content.Conflicts.SelectMany(c => c.Alternatives));
        var map = new Dictionary<Guid, IReadOnlyList<Guid>>();
        foreach (var item in items)
        {
            map[item.Id] = [.. item.EvidenceRefs];
        }

        return new(map, new HashSet<Guid>(content.Sources.Select(s => s.Id)));
    }
}

public sealed record DraftValidation(bool Accepted, IReadOnlyList<string> Violations, DraftOutput? Output, string? Markdown);

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

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionKey();

    private static readonly HashSet<string> OutputProperties = ["sectionKey", "blocks"];
    private static readonly HashSet<string> BlockProperties = ["itemIds", "markdown", "sourceIds"];

    /// <summary>
    /// Valida l'output chiuso <c>{sectionKey,blocks:[{itemIds,markdown,sourceIds}]}</c> della generazione documentale v2 contro il pacchetto fidato
    /// della revisione confermata: JSON stretto (nessun recinto, prosa, proprietà extra/duplicate, UUID solo minuscoli), ogni item del pacchetto
    /// rappresentato in almeno un blocco, itemIds solo del pacchetto, marcatori <c>[fonte:UUID]</c> atomici uguali a sourceIds, fonti ammesse
    /// solo se sostengono un item del blocco, ogni item con fonti citato con una delle proprie in un blocco che lo contiene, Markdown di ogni
    /// blocco senza HTML/immagini/schemi pericolosi. Nessuna riparazione: l'output o è accettato per intero o non esiste.
    /// </summary>
    public static DraftValidation ValidateBlocks(string? responseText, DocumentDraftPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var violations = new List<string>();
        var output = Decode(responseText, violations);
        if (output is null)
        {
            return Rejected(violations);
        }

        if (!SectionKey().IsMatch(output.SectionKey))
        {
            violations.Add("struttura: sectionKey non valida (minuscole, cifre, '-' e '_', al massimo 64 caratteri).");
        }

        if (output.Blocks.Count == 0)
        {
            violations.Add("struttura: blocks è vuoto.");
        }

        var context = new DocumentValidationContext(package.SourceIds);
        var represented = new HashSet<Guid>();
        var cited = new HashSet<Guid>(); // item con almeno una propria fonte citata in un blocco che lo contiene
        for (var i = 0; i < output.Blocks.Count; i++)
        {
            var block = output.Blocks[i];
            if (block.ItemIds.Count == 0)
            {
                violations.Add($"itemIds: blocks[{i}] non ha item.");
            }

            if (block.ItemIds.Distinct().Count() != block.ItemIds.Count)
            {
                violations.Add($"itemIds: blocks[{i}] ripete un item.");
            }

            var allowed = new HashSet<Guid>();
            foreach (var id in block.ItemIds)
            {
                if (package.ItemSources.TryGetValue(id, out var refs))
                {
                    represented.Add(id);
                    allowed.UnionWith(refs);
                }
                else
                {
                    violations.Add($"itemIds: blocks[{i}] cita l'item {id}, che non appartiene alla revisione confermata.");
                }
            }

            violations.AddRange(Validate(block.Markdown, context, requireSource: false).Violations);

            var markers = SourceMarker().Matches(block.Markdown)
                .Select(m => Guid.TryParse(m.Groups[1].Value, out var g) ? g : (Guid?)null).OfType<Guid>().ToHashSet();
            var declared = block.SourceIds.ToHashSet();
            if (declared.Count != block.SourceIds.Count)
            {
                violations.Add($"fonti: blocks[{i}].sourceIds ripete una fonte.");
            }

            if (!markers.SetEquals(declared))
            {
                violations.Add($"fonti: blocks[{i}].sourceIds non coincide con i marcatori [fonte:...] del Markdown.");
            }

            foreach (var source in declared.Where(s => !package.SourceIds.Contains(s)))
            {
                violations.Add($"fonti: blocks[{i}] dichiara la fonte {source}, che non appartiene alla revisione confermata.");
            }

            foreach (var source in declared.Where(s => package.SourceIds.Contains(s) && !allowed.Contains(s)))
            {
                violations.Add($"fonti: blocks[{i}] cita la fonte {source}, che non sostiene nessuno dei suoi item.");
            }

            foreach (var id in block.ItemIds.Where(id => package.ItemSources.TryGetValue(id, out var refs) && refs.Any(declared.Contains)))
            {
                cited.Add(id);
            }
        }

        foreach (var (id, refs) in package.ItemSources)
        {
            if (!represented.Contains(id))
            {
                violations.Add($"itemIds: l'item {id} della revisione confermata non è rappresentato in nessun blocco.");
            }
            else if (refs.Count > 0 && !cited.Contains(id))
            {
                violations.Add($"fonti: l'item {id} ha fonti ma nessun blocco che lo contiene ne cita una.");
            }
        }

        return violations.Count > 0
            ? Rejected(violations)
            : new(true, [], output, string.Join("\n\n", output.Blocks.Select(b => b.Markdown.Trim())));
    }

    private static DraftValidation Rejected(IEnumerable<string> violations) => new(false, [.. violations.Distinct(StringComparer.Ordinal)], null, null);

    private static DraftOutput? Decode(string? text, List<string> violations)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            violations.Add("struttura: l'output è vuoto.");
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            if (!IsClosedObject(root, OutputProperties, "output", violations))
            {
                return null;
            }

            if (root.GetProperty("sectionKey").ValueKind != JsonValueKind.String || root.GetProperty("blocks").ValueKind != JsonValueKind.Array)
            {
                violations.Add("struttura: sectionKey deve essere una stringa e blocks un array.");
                return null;
            }

            var blocks = new List<DraftBlock>();
            var index = 0;
            foreach (var element in root.GetProperty("blocks").EnumerateArray())
            {
                if (!IsClosedObject(element, BlockProperties, $"blocks[{index}]", violations))
                {
                    return null;
                }

                var items = Uuids(element.GetProperty("itemIds"), $"blocks[{index}].itemIds", violations);
                var sources = Uuids(element.GetProperty("sourceIds"), $"blocks[{index}].sourceIds", violations);
                if (element.GetProperty("markdown").ValueKind != JsonValueKind.String || items is null || sources is null)
                {
                    violations.Add($"struttura: blocks[{index}] non rispetta il tipo atteso (markdown stringa, itemIds e sourceIds array di UUID).");
                    return null;
                }

                blocks.Add(new(items, element.GetProperty("markdown").GetString()!, sources));
                index++;
            }

            return new(root.GetProperty("sectionKey").GetString()!, blocks);
        }
        catch (JsonException)
        {
            violations.Add("struttura: l'output non è un JSON valido (nessuna riparazione, nessun recinto di codice).");
            return null;
        }
    }

    private static bool IsClosedObject(JsonElement element, HashSet<string> expected, string where, List<string> violations)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            violations.Add($"struttura: {where} deve essere un oggetto.");
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ok = true;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                violations.Add($"struttura: {where} ha la proprietà {property.Name} duplicata.");
                ok = false;
            }
            else if (!expected.Contains(property.Name))
            {
                violations.Add($"struttura: {where} ha la proprietà non ammessa {property.Name}.");
                ok = false;
            }
        }

        foreach (var missing in expected.Where(e => !seen.Contains(e)))
        {
            violations.Add($"struttura: {where} non ha la proprietà {missing}.");
            ok = false;
        }

        return ok;
    }

    /// <summary>UUID in forma «D» minuscola: nessun'altra forma è accettata (identificatori canonici).</summary>
    private static List<Guid>? Uuids(JsonElement element, string where, List<string> violations)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<Guid>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParseExact(item.GetString(), "D", out var id) || id.ToString("D") != item.GetString())
            {
                violations.Add($"struttura: {where} contiene un identificatore che non è un UUID minuscolo.");
                return null;
            }

            list.Add(id);
        }

        return list;
    }

    public static DocumentValidationResult Validate(string? markdown, DocumentValidationContext context, bool requireSource = true)
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

        if (requireSource && markers.Count == 0 && context.SourceIds.Count > 0)
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
