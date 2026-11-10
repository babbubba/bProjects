using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace bProjects.IntegrationTests;

/// <summary>Query del confronto A/B: <see cref="MentionsDocument"/> dichiarato (insieme T027) o stimato con una parola chiave (gold storici: euristica, dichiarata nel rapporto).</summary>
internal sealed record AbCase(string Id, string Query, string Category, bool MentionsDocument, bool MentionDeclared, IReadOnlyList<GoldFinding> Gold);

/// <summary>Esito di una query nei tre ordini: RRF, A (solo testo, <c>retrieval-policy-1</c>) e B (documento + sezione + testo, <c>retrieval-policy-2</c>) sugli STESSI candidati.</summary>
internal sealed record AbQuery(
    string Set, AbCase Case, QueryMetrics Rrf, QueryMetrics A, QueryMetrics B, double RerankMsA, double RerankMsB);

/// <summary>Aggregato di un gruppo di query per i tre ordini.</summary>
internal sealed record AbGroup(string Name, int Queries, AggregateMetrics Rrf, AggregateMetrics A, AggregateMetrics B);

/// <summary>
/// Dataset e calcoli del confronto A/B di T027 (funzioni pure, testate senza servizi). Il dataset <c>T027-dev</c> è un insieme di SVILUPPO scritto dopo aver osservato i
/// fallimenti di M2: non è una validazione indipendente. Query, gold e documenti sono congelati da <c>frozen-manifest.json</c> (SHA-256 su contenuto con a capo LF) PRIMA della misura.
/// </summary>
internal static class AbEvaluation
{
    public const string DevSet = "T027-dev";

    /// <summary>Radice dei dataset del confronto; ogni insieme è una cartella con documenti, <c>cases.json</c> e <c>frozen-manifest.json</c>.</summary>
    public static string SetRoot(string set) => Path.Combine(AppContext.BaseDirectory, "Retrieval", "Evaluation", set);

    public static IReadOnlyList<string> DocumentFiles(string set) =>
        [.. Directory.GetFiles(SetRoot(set)).Select(Path.GetFileName).Where(f => f is not ("cases.json" or "frozen-manifest.json")).Order(StringComparer.Ordinal).Select(f => f!)];

    public static IReadOnlyList<AbCase> LoadCases(string set)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(SetRoot(set), "cases.json")));
        return [.. json.RootElement.GetProperty("scenarios").GetProperty(set).EnumerateArray().Select(c => new AbCase(
            c.GetProperty("id").GetString()!, c.GetProperty("query").GetString()!, c.GetProperty("category").GetString()!,
            c.GetProperty("mentionsDocument").GetBoolean(), true,
            [.. c.GetProperty("gold").EnumerateArray().Select(g => new GoldFinding(g.GetProperty("id").GetString()!, g.GetProperty("file").GetString()!, g.GetProperty("contains").GetString()!))]))];
    }

    /// <summary>Autore e dichiarazione di indipendenza letti dal <c>cases.json</c> (congelato dal manifest): <c>Independent</c> solo se la dichiarazione ha entrambe le conferme a true.</summary>
    public static (string Author, bool Independent) Authorship(string set)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(SetRoot(set), "cases.json")));
        var root = json.RootElement;
        var author = root.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "n/d";
        var independent = root.TryGetProperty("independenceDeclaration", out var d) && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("notSeenPriorFailures", out var f) && f.ValueKind == JsonValueKind.True
            && d.TryGetProperty("notSeenPriorResults", out var r) && r.ValueKind == JsonValueKind.True;
        return (author, independent);
    }

    /// <summary>Insiemi consegnati da un revisore (schema <c>ab-set-1</c>): cartelle di <c>Retrieval/Evaluation</c> il cui <c>cases.json</c> dichiara lo schema.</summary>
    public static IReadOnlyList<string> SchemaSets()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Retrieval", "Evaluation");
        return [.. Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "cases.json")) && File.ReadAllText(Path.Combine(d, "cases.json")).Contains("\"ab-set-1\"", StringComparison.Ordinal)).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal)];
    }

    /// <summary>SHA-256 esadecimale minuscolo del contenuto con a capo normalizzati a LF (il checkout Windows può convertire CRLF).</summary>
    public static string Sha256Lf(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal))));

    /// <summary>Differenze fra il manifest congelato e i file presenti (vuoto = insieme intatto). File aggiunto, mancante o modificato sono tutti scarti.</summary>
    public static IReadOnlyList<string> VerifyFrozen(string set)
    {
        var root = SetRoot(set);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "frozen-manifest.json")));
        var expected = manifest.RootElement.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var actual = Directory.GetFiles(root).Select(Path.GetFileName).Where(f => f != "frozen-manifest.json").Select(f => f!).ToList();
        var problems = new List<string>();
        problems.AddRange(actual.Except(expected.Keys, StringComparer.Ordinal).Select(f => $"file non congelato: {f}"));
        problems.AddRange(expected.Keys.Except(actual, StringComparer.Ordinal).Select(f => $"file congelato mancante: {f}"));
        problems.AddRange(expected.Where(e => actual.Contains(e.Key) && Sha256Lf(Path.Combine(root, e.Key)) != e.Value).Select(e => $"file modificato dopo il congelamento: {e.Key}"));
        return problems;
    }

    private static readonly string[] DocumentKeywords = ["contratto", "allegato", "verbale", "scheda", "capitolato", "documento", "offerta", "addendum"];

    /// <summary>Euristica per i gold storici (senza dichiarazione d'autore): la query contiene una parola che nomina un tipo di documento.</summary>
    public static bool MentionsDocumentHeuristic(string query) =>
        DocumentKeywords.Any(k => query.Contains(k, StringComparison.OrdinalIgnoreCase));

    public static AbGroup Group(string name, IEnumerable<AbQuery> queries)
    {
        var list = queries.ToList();
        return new(name, list.Count, EvaluationMetrics.Aggregate(list.Select(q => q.Rrf)), EvaluationMetrics.Aggregate(list.Select(q => q.A)), EvaluationMetrics.Aggregate(list.Select(q => q.B)));
    }

    /// <summary>Query corrette in A (Hit@1) e perse in B: le regressioni da esaminare una per una.</summary>
    public static IReadOnlyList<AbQuery> Regressions(IEnumerable<AbQuery> queries) => [.. queries.Where(q => q.A.Hit[1] && !q.B.Hit[1])];

    /// <summary>Query mancate in A (Hit@1) e corrette in B.</summary>
    public static IReadOnlyList<AbQuery> Improvements(IEnumerable<AbQuery> queries) => [.. queries.Where(q => !q.A.Hit[1] && q.B.Hit[1])];

    public static IReadOnlyDictionary<string, double> Latency(IReadOnlyCollection<double> values)
    {
        var sorted = values.Order().ToList();
        double Pct(double p) => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
        return new Dictionary<string, double>
        {
            ["n"] = sorted.Count,
            ["p50"] = Math.Round(Pct(0.5), 2),
            ["p95"] = Math.Round(Pct(0.95), 2),
            ["mean"] = Math.Round(sorted.Count == 0 ? 0 : sorted.Average(), 2),
            ["max"] = Math.Round(sorted.Count == 0 ? 0 : sorted[^1], 2),
        };
    }
}
