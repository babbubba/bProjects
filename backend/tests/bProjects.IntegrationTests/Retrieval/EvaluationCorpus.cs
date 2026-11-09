using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace bProjects.IntegrationTests;

/// <summary>Blocco prodotto dal parser finto: testo e percorso dei titoli.</summary>
internal sealed record FakeBlock(string Text, string[] Heading);

/// <summary>Riscontro annotato di una query: identificativo del gold, file e testo letterale che deve comparire in UN blocco.</summary>
internal sealed record GoldFinding(string Id, string File, string Contains);

internal sealed record EvalCase(string Id, string Query, IReadOnlyList<GoldFinding> Gold);

/// <summary>Conflitto del gold: lati come identificativi di riscontro (uno per lato), dal <c>gold.json</c> congelato.</summary>
internal sealed record EvalConflict(string Id, IReadOnlyList<string> SideFindingIds);

/// <summary>Scenario del laboratorio: file documentali e file «Message» (dichiarazioni del PM, sempre offerte allo stadio, mai indicizzate).</summary>
internal sealed record EvalScenario(string Name, string Directory, string[] DocumentFiles, string[] MessageFiles, string? GoldFile);

/// <summary>
/// Caricamento dei corpora e delle query del laboratorio per l'harness (003 T018). Parser FINTO e deterministico, nessun Docling: TXT e Markdown a paragrafi (righe vuote),
/// con percorso dei titoli per il Markdown; DOCX a paragrafi <c>w:p</c> del <c>document.xml</c>. I blocchi sono paragrafi (~3,1k per corpus, come la variante «line» del laboratorio; non
/// i ~460 blocchi Docling). I gold sono i file congelati di <c>Retrieval/Gold</c>, mai modificati; le query T082/T083 vengono dalla copia byte-identica di
/// <c>retrieval-cases.json</c> in <c>Retrieval/Evaluation</c> (il manifest dei gold congelati contiene solo le 12 query N4).
/// </summary>
internal static class EvaluationCorpus
{
    public const string QueryAuthor = "singolo";

    public static readonly IReadOnlyList<EvalScenario> Scenarios =
    [
        new("N4-fixtures", "N4-fixtures", ["txt-conflict-a.txt", "txt-conflict-b.txt", "txt-negations.txt", "md-basic.md"], ["pm-message.txt"], null),
        new("T082-single", "T082-single", ["capitolato-aurora.txt"], [], "gold.json"),
        new("T083-multi", "T083-multi", ["contratto-quadro-zefiro.txt", "allegato-tecnico-zefiro.md", "verbale-collaudo-zefiro.docx"], ["pm-message.txt"], "gold.json"),
    ];

    public static string EvaluationRoot => Path.Combine(AppContext.BaseDirectory, "Retrieval", "Evaluation");

    public static string CasesPath => Path.Combine(EvaluationRoot, "retrieval-cases.json");

    public static IReadOnlyList<EvalCase> LoadCases(string scenario)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(CasesPath));
        return [.. json.RootElement.GetProperty("scenarios").GetProperty(scenario).EnumerateArray().Select(c => new EvalCase(
            c.GetProperty("id").GetString()!, c.GetProperty("query").GetString()!,
            [.. c.GetProperty("gold").EnumerateArray().Select(g => new GoldFinding(g.GetProperty("id").GetString()!, g.GetProperty("file").GetString()!, g.GetProperty("contains").GetString()!))]))];
    }

    /// <summary>Conflitti del <c>gold.json</c> congelato: ogni lato è risolto nell'UNICO riscontro il cui <c>Quote</c> lo contiene (lato non risolvibile o ambiguo ⇒ errore).</summary>
    public static IReadOnlyList<EvalConflict> LoadConflicts(EvalScenario scenario)
    {
        if (scenario.GoldFile is null)
        {
            return [];
        }

        using var json = JsonDocument.Parse(File.ReadAllBytes(RetrievalGold.PathOf(scenario.Directory, scenario.GoldFile)));
        var facts = json.RootElement.GetProperty("Gold").EnumerateArray()
            .Select(g => (Id: g.GetProperty("Id").GetString()!, Quotes: g.GetProperty("Quote").EnumerateArray().Select(q => q.GetString()!).ToList()))
            .ToList();
        return [.. json.RootElement.GetProperty("Conflicts").EnumerateArray().Select(c =>
        {
            var sides = c.GetProperty("Sides").EnumerateArray().Select(side =>
            {
                var text = side.EnumerateArray().Select(s => s.GetString()!).Single();
                var matches = facts.Where(f => f.Quotes.Any(q => q.Contains(text, StringComparison.OrdinalIgnoreCase))).Select(f => f.Id).ToList();
                return matches.Count == 1 ? matches[0] : throw new InvalidOperationException($"Lato di conflitto «{text}» non risolvibile in un solo riscontro ({matches.Count}).");
            }).ToList();
            return new EvalConflict(c.GetProperty("Id").GetString()!, sides);
        })];
    }

    public static IReadOnlyList<FakeBlock> Parse(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => ParseDocx(path),
        ".md" => ParseMarkdown(File.ReadAllText(path, Encoding.UTF8)),
        _ => [.. Paragraphs(File.ReadAllText(path, Encoding.UTF8)).Select(p => new FakeBlock(p, []))],
    };

    private static IEnumerable<string> Paragraphs(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0);

    private static List<FakeBlock> ParseMarkdown(string text)
    {
        var blocks = new List<FakeBlock>();
        var path = new List<string>();
        foreach (var paragraph in Paragraphs(text))
        {
            if (paragraph.StartsWith('#'))
            {
                var level = paragraph.TakeWhile(c => c == '#').Count();
                var title = paragraph[level..].Trim();
                while (path.Count >= level)
                {
                    path.RemoveAt(path.Count - 1);
                }

                path.Add(title);
                blocks.Add(new FakeBlock(title, [.. path]));
            }
            else
            {
                blocks.Add(new FakeBlock(paragraph, [.. path]));
            }
        }

        return blocks;
    }

    private static List<FakeBlock> ParseDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("word/document.xml")!.Open();
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var document = XDocument.Load(stream);
        return [.. document.Descendants(w + "p")
            .Select(p => string.Concat(p.Descendants().Select(e => e.Name == w + "t" ? e.Value : e.Name == w + "tab" ? "\t" : string.Empty)).Trim())
            .Where(t => t.Length > 0)
            .Select(t => new FakeBlock(t, []))];
    }

    /// <summary>Identificativo di blocco deterministico (stesso corpus ⇒ stessi BlockId ⇒ stesse parità risolte nello stesso modo): rende riproducibili ranghi e rapporto.</summary>
    public static Guid BlockId(string scenario, string file, int ordinal) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"retrieval-eval/{scenario}/{file}/{ordinal}"))[..16]);

    public static string Sha256Of(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
