namespace bProjects.App.Retrieval;

/// <summary>Un blocco nella classifica di UN segnale (lessicale o denso): rango 1-based dichiarato dal segnale.</summary>
public sealed record RankedBlock(Guid BlockId, int Rank);

/// <summary>Esito della fusione per un blocco: ranghi per segnale CONSERVATI (null se assente dal segnale), rango RRF 1-based e punteggio (solo ordinamento).</summary>
public sealed record FusedBlock(Guid BlockId, int? LexicalRank, int? DenseRank, int RrfRank, double Score);

/// <summary>
/// Fusione Reciprocal Rank Fusion (003 T014, FR-008, A-2): <c>k = 60</c>, pesi pari, sulle due classifiche complete. Funzione pura e deterministica:
/// il punteggio di un blocco è la somma di <c>1/(k + rango)</c> sui segnali in cui compare, sempre nell'ordine lessicale + denso; a pari punteggio vince
/// il <c>BlockId</c> ascendente. L'ordine con cui le voci arrivano nelle liste non conta (si usa il rango dichiarato, non la posizione). Un blocco ripetuto
/// nella stessa lista conserva il rango migliore. Il punteggio serve solo a ordinare (mai come probabilità).
/// </summary>
public static class Rrf
{
    public const int K = 60;

    public static IReadOnlyList<FusedBlock> Fuse(IEnumerable<RankedBlock> lexical, IEnumerable<RankedBlock> dense)
    {
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(dense);
        var lex = Best(lexical);
        var den = Best(dense);
        var ids = lex.Keys.Union(den.Keys);
        var scored = ids.Select(id =>
        {
            var l = lex.TryGetValue(id, out var lr) ? lr : (int?)null;
            var d = den.TryGetValue(id, out var dr) ? dr : (int?)null;
            var score = (l is null ? 0.0 : 1.0 / (K + l.Value)) + (d is null ? 0.0 : 1.0 / (K + d.Value));
            return (Id: id, Lexical: l, Dense: d, Score: score);
        });

        return [.. scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Id)
            .Select((s, index) => new FusedBlock(s.Id, s.Lexical, s.Dense, index + 1, s.Score))];
    }

    private static Dictionary<Guid, int> Best(IEnumerable<RankedBlock> list)
    {
        var best = new Dictionary<Guid, int>();
        foreach (var item in list)
        {
            if (item.Rank < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(list), "Il rango è 1-based.");
            }

            best[item.BlockId] = best.TryGetValue(item.BlockId, out var current) ? Math.Min(current, item.Rank) : item.Rank;
        }

        return best;
    }
}
