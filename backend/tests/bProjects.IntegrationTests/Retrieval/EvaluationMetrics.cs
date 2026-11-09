namespace bProjects.IntegrationTests;

/// <summary>Citazione di una evidenza su un blocco: intervallo UTF-16 <c>[Start,End)</c> e testo citato.</summary>
internal sealed record Citation(Guid EvidenceId, Guid BlockId, int Start, int End, string Quote);

/// <summary>Metriche di una query: posizioni (1-based, fra elementi DISTINTI) di ciascun riscontro richiesto; null = assente dalla classifica.</summary>
internal sealed record QueryMetrics(
    string Id, int Required, IReadOnlyList<int?> Positions, int? FirstRank,
    IReadOnlyDictionary<int, double> Recall, IReadOnlyDictionary<int, bool> Hit, IReadOnlyDictionary<int, bool> Complete);

/// <summary>Metriche aggregate su un insieme di query con almeno un riscontro richiesto.</summary>
internal sealed record AggregateMetrics(
    int Queries, IReadOnlyDictionary<int, double> Recall, IReadOnlyDictionary<int, double> Hit, IReadOnlyDictionary<int, double> Complete,
    double? FirstRankMean, int NeverFound);

/// <summary>
/// Metriche di valutazione del recupero (003 T017, FR-026/FR-027/FR-030, contracts/evaluation.md §3): funzioni pure e deterministiche, riscritte in C# (nessuno script
/// Python nel prodotto). Definizioni identiche a quelle del laboratorio, verificate sui suoi <c>results/*</c> (vedi <c>EvaluationMetricsTests</c>):
/// <list type="bullet">
/// <item><b>Posizione</b> di un riscontro = rango 1-based nella classifica degli elementi DISTINTI (un elemento ripetuto non sposta quelli che seguono).</item>
/// <item><b>Recall@k</b> di una query = (riscontri distinti entro k) / (riscontri richiesti): una query multi-riscontro richiede TUTTI i riscontri annotati, quindi un solo
/// lato di un conflitto vale 0,5, non 1. Aggregato = media sulle query. <b>Complete@k</b> = quota di query con TUTTI i riscontri entro k.</item>
/// <item><b>Hit@k</b> = almeno un riscontro entro k; aggregato = quota di query.</item>
/// <item><b>Primo riscontro</b> = posizione minima; media aggregata sulle sole query che lo trovano; <c>NeverFound</c> = query che non lo trovano affatto.</item>
/// </list>
/// Nessuna soglia è decisa qui: i numeri sono descrittivi e il loro significato dipende dalla modalità (simulata/reale) dichiarata nel rapporto.
/// </summary>
internal static class EvaluationMetrics
{
    public static readonly IReadOnlyList<int> Ks = [1, 5, 10, 20, 50];

    /// <summary>Profondità a cui si valuta se entrambi i lati di un conflitto sono offerti (Recall@10, SC-002).</summary>
    public const int ConflictK = 10;

    /// <summary>Posizione di ciascun riscontro richiesto (distinto, nell'ordine dato) nella classifica; null se assente.</summary>
    public static IReadOnlyList<int?> Positions<T>(IEnumerable<T> ranking, IReadOnlyList<T> required)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(ranking);
        ArgumentNullException.ThrowIfNull(required);
        var position = new Dictionary<T, int>();
        foreach (var item in ranking)
        {
            position.TryAdd(item, position.Count + 1);
        }

        return [.. required.Distinct().Select(r => position.TryGetValue(r, out var p) ? (int?)p : null)];
    }

    public static QueryMetrics ForQuery<T>(string id, IEnumerable<T> ranking, IReadOnlyList<T> required)
        where T : notnull => ForQuery(id, Positions(ranking, required));

    public static QueryMetrics ForQuery(string id, IReadOnlyList<int?> positions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(positions);
        if (positions.Count == 0)
        {
            throw new ArgumentException("Una query senza riscontri richiesti non ha metriche.", nameof(positions));
        }

        if (positions.Any(p => p is < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(positions), "Le posizioni sono 1-based.");
        }

        var found = positions.Where(p => p is not null).Select(p => p!.Value).ToList();
        return new(
            id, positions.Count, positions, found.Count == 0 ? null : found.Min(),
            Ks.ToDictionary(k => k, k => (double)found.Count(p => p <= k) / positions.Count),
            Ks.ToDictionary(k => k, k => found.Any(p => p <= k)),
            Ks.ToDictionary(k => k, k => found.Count(p => p <= k) == positions.Count));
    }

    public static AggregateMetrics Aggregate(IEnumerable<QueryMetrics> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var all = queries.Where(q => q.Required > 0).ToList();
        if (all.Count == 0)
        {
            return new(0, Ks.ToDictionary(k => k, _ => 0.0), Ks.ToDictionary(k => k, _ => 0.0), Ks.ToDictionary(k => k, _ => 0.0), null, 0);
        }

        var firsts = all.Where(q => q.FirstRank is not null).Select(q => (double)q.FirstRank!.Value).ToList();
        return new(
            all.Count,
            Ks.ToDictionary(k => k, k => all.Average(q => q.Recall[k])),
            Ks.ToDictionary(k => k, k => all.Count(q => q.Hit[k]) / (double)all.Count),
            Ks.ToDictionary(k => k, k => all.Count(q => q.Complete[k]) / (double)all.Count),
            firsts.Count == 0 ? null : firsts.Average(),
            all.Count - firsts.Count);
    }

    /// <summary>
    /// Citazioni invalide: blocco assente dal perimetro, intervallo fuori dal testo, oppure testo citato diverso da quello del blocco in quell'intervallo. Una
    /// citazione valida è sempre verificabile sul blocco da cui proviene (FR-013).
    /// </summary>
    public static int InvalidCitations(IEnumerable<Citation> citations, IReadOnlyDictionary<Guid, string> blockTexts)
    {
        ArgumentNullException.ThrowIfNull(citations);
        ArgumentNullException.ThrowIfNull(blockTexts);
        return citations.Count(c => !IsValid(c, blockTexts));
    }

    private static bool IsValid(Citation c, IReadOnlyDictionary<Guid, string> blockTexts) =>
        blockTexts.TryGetValue(c.BlockId, out var text)
        && c.Start >= 0 && c.End >= c.Start && c.End <= text.Length
        && c.End - c.Start == c.Quote.Length
        && string.CompareOrdinal(text, c.Start, c.Quote, 0, c.Quote.Length) == 0;

    /// <summary>Conflitti persi: almeno un lato non è fra gli elementi offerti. Un conflitto con un solo lato presente NON è recuperato (FR-006).</summary>
    public static IReadOnlyList<string> LostConflicts<T>(IEnumerable<(string Id, IReadOnlyList<T> Sides)> conflicts, IReadOnlySet<T> available)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        ArgumentNullException.ThrowIfNull(available);
        return [.. conflicts.Where(c => c.Sides.Any(s => !available.Contains(s))).Select(c => c.Id)];
    }

    /// <summary>Elementi in testa alla classifica (distinti, al più <paramref name="k"/>).</summary>
    public static IReadOnlyList<T> Top<T>(IEnumerable<T> ranking, int k)
        where T : notnull => [.. ranking.Distinct().Take(k)];
}
