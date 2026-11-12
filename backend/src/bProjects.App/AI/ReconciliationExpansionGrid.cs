namespace bProjects.App.AI;

/// <summary>
/// Appartenenza di una evidenza ai due gruppi di un candidato di gruppo: solo al primo (S1 = membri di L), solo al secondo (S2 = membri di R) o a entrambi.
/// </summary>
public enum ExpansionTag
{
    /// <summary>Presente solo in S1.</summary>
    Left,

    /// <summary>Presente solo in S2.</summary>
    Right,

    /// <summary>Presente sia in S1 sia in S2.</summary>
    Both,
}

/// <summary>Una cella della griglia: un frammento di S1 per un frammento di S2 (ciascuno ordinato per stringa «D» ordinale).</summary>
public sealed record ExpansionCell(IReadOnlyList<Guid> Left, IReadOnlyList<Guid> Right);

/// <summary>
/// Un blocco di espansione: l'unione ordinata e deduplicata di una cella, con i due frammenti da cui deriva.
/// <see cref="Ordinal"/> parte da 0 nell'ordine di input delle celle.
/// </summary>
public sealed record ExpansionBlock(int Ordinal, IReadOnlyList<Guid> Members, IReadOnlyList<Guid> Left, IReadOnlyList<Guid> Right);

/// <summary>Esito esatto della verifica di copertura: coppie rilevanti richieste, coppie coperte e le prime mancanti.</summary>
public sealed record GridCoverage(long RequiredPairs, long CoveredPairs, IReadOnlyList<(Guid A, Guid B)> FirstMissing)
{
    /// <summary>Vero quando ogni coppia rilevante compare insieme in almeno un blocco.</summary>
    public bool Complete => RequiredPairs == CoveredPairs;
}

/// <summary>
/// Griglia deterministica di blocchi per l'espansione di un candidato di gruppo. Dati S1 e S2 (che possono sovrapporsi), una coppia è «rilevante»
/// se è formata da due id DISTINTI con uno in S1 e l'altro in S2 (equivale a tutte le coppie dell'unione tranne quelle interamente in solo-S1
/// o interamente in solo-S2). Suddividendo S1 e S2 in frammenti contigui e prendendo il prodotto dei frammenti si ottengono celle tali che
/// ogni coppia rilevante sta insieme in almeno un blocco. Tutto l'ordinamento usa <c>Guid.ToString("D")</c> con confronto ordinale (mai
/// <c>Guid.CompareTo</c>, mai cultura); solo aritmetica intera, nessuna casualità, nessuna dipendenza dall'ordine di iterazione di
/// dizionari o insiemi.
/// </summary>
public static class ReconciliationExpansionGrid
{
    /// <summary>Numero massimo di coppie mancanti riportate da <see cref="Verify"/>.</summary>
    private const int MaxMissingReported = 8;

    /// <summary>Confronto ordinale tra le rappresentazioni «D» di due identificatori.</summary>
    public static int Compare(Guid a, Guid b) => StringComparer.Ordinal.Compare(a.ToString("D"), b.ToString("D"));

    /// <summary>Ordina gli id per stringa «D» ordinale; rifiuta i duplicati con <see cref="ArgumentException"/>.</summary>
    public static IReadOnlyList<Guid> Ordered(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var keyed = ids.Select(id => (Id: id, Key: id.ToString("D"))).ToList();
        keyed.Sort((x, y) => StringComparer.Ordinal.Compare(x.Key, y.Key));
        for (var i = 1; i < keyed.Count; i++)
        {
            if (keyed[i].Key == keyed[i - 1].Key)
            {
                throw new ArgumentException($"Identificatore duplicato: {keyed[i].Key}.", nameof(ids));
            }
        }

        return keyed.ConvertAll(k => k.Id);
    }

    /// <summary>
    /// Appartenenza di ogni evidenza ai gruppi INTERI (non ai frammenti): solo S1 → <see cref="ExpansionTag.Left"/>, solo S2 → <see cref="ExpansionTag.Right"/>,
    /// in entrambi → <see cref="ExpansionTag.Both"/>. I duplicati dentro un singolo gruppo sono rifiutati.
    /// </summary>
    public static IReadOnlyDictionary<Guid, ExpansionTag> Tags(IReadOnlyList<Guid> s1, IReadOnlyList<Guid> s2)
    {
        ArgumentNullException.ThrowIfNull(s1);
        ArgumentNullException.ThrowIfNull(s2);
        var first = Ordered(s1);
        var second = Ordered(s2);
        var inFirst = new HashSet<Guid>(first);
        var inSecond = new HashSet<Guid>(second);
        var tags = new Dictionary<Guid, ExpansionTag>(first.Count + second.Count);
        foreach (var id in first)
        {
            tags[id] = inSecond.Contains(id) ? ExpansionTag.Both : ExpansionTag.Left;
        }

        foreach (var id in second)
        {
            if (!inFirst.Contains(id))
            {
                tags[id] = ExpansionTag.Right;
            }
        }

        return tags;
    }

    /// <summary>Partizione contigua di una sequenza già ordinata secondo le dimensioni date (tutte &gt; 0, somma uguale al numero di elementi).</summary>
    public static IReadOnlyList<IReadOnlyList<Guid>> Chunk(IReadOnlyList<Guid> ordered, IReadOnlyList<int> sizes)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(sizes);
        long total = 0;
        foreach (var size in sizes)
        {
            if (size <= 0)
            {
                throw new ArgumentException("Ogni dimensione di frammento deve essere maggiore di zero.", nameof(sizes));
            }

            total += size;
        }

        if (total != ordered.Count)
        {
            throw new ArgumentException($"La somma delle dimensioni ({total}) deve essere uguale al numero di elementi ({ordered.Count}).", nameof(sizes));
        }

        var chunks = new List<IReadOnlyList<Guid>>(sizes.Count);
        var offset = 0;
        foreach (var size in sizes)
        {
            var part = new Guid[size];
            for (var i = 0; i < size; i++)
            {
                part[i] = ordered[offset + i];
            }

            chunks.Add(part);
            offset += size;
        }

        return chunks;
    }

    /// <summary>Prodotto dei frammenti di S1 per quelli di S2, in ordine lessicografico (i, j).</summary>
    public static IReadOnlyList<ExpansionCell> Cells(IReadOnlyList<IReadOnlyList<Guid>> leftChunks, IReadOnlyList<IReadOnlyList<Guid>> rightChunks)
    {
        ArgumentNullException.ThrowIfNull(leftChunks);
        ArgumentNullException.ThrowIfNull(rightChunks);
        var cells = new List<ExpansionCell>(leftChunks.Count * rightChunks.Count);
        foreach (var left in leftChunks)
        {
            foreach (var right in rightChunks)
            {
                cells.Add(new ExpansionCell(left, right));
            }
        }

        return cells;
    }

    /// <summary>Unione ordinata (stringa «D» ordinale) e deduplicata dei due lati di una cella.</summary>
    public static IReadOnlyList<Guid> UnionOf(ExpansionCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        var distinct = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var id in cell.Left)
        {
            distinct[id.ToString("D")] = id;
        }

        foreach (var id in cell.Right)
        {
            distinct[id.ToString("D")] = id;
        }

        var keys = new List<string>(distinct.Keys);
        keys.Sort(StringComparer.Ordinal);
        return keys.ConvertAll(k => distinct[k]);
    }

    /// <summary>
    /// Vero se i membri (distinti) contengono almeno una coppia rilevante: servono almeno due membri distinti, uno in S1 e uno in S2.
    /// Un singolo membro <see cref="ExpansionTag.Both"/> da solo NON è rilevante; {Both, Left} lo è; {Left, Left} e {Right, Right} no.
    /// </summary>
    public static bool IsRelevant(IReadOnlyList<Guid> members, IReadOnlyDictionary<Guid, ExpansionTag> tags)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(tags);
        var inFirst = 0;
        var inSecond = 0;
        var both = 0;
        foreach (var id in members)
        {
            switch (tags[id])
            {
                case ExpansionTag.Left:
                    inFirst++;
                    break;
                case ExpansionTag.Right:
                    inSecond++;
                    break;
                default:
                    inFirst++;
                    inSecond++;
                    both++;
                    break;
            }
        }

        if (inFirst == 0 || inSecond == 0)
        {
            return false;
        }

        // L'unico caso con S1 e S2 non vuoti ma senza due id distinti: un solo membro, in entrambi i gruppi.
        return !(inFirst == 1 && inSecond == 1 && both == 1);
    }

    /// <summary>
    /// Divide una cella in due celle che coprono esattamente lo stesso prodotto Left × Right: dimezza (per numero, la metà sinistra è il soffitto)
    /// il lato con più membri, a parità il lato Left. Restituisce <c>null</c> quando entrambi i lati hanno al massimo un membro.
    /// </summary>
    public static IReadOnlyList<ExpansionCell>? Split(ExpansionCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        var nl = cell.Left.Count;
        var nr = cell.Right.Count;
        if (nl <= 1 && nr <= 1)
        {
            return null;
        }

        if (nl >= nr)
        {
            var head = (nl + 1) / 2;
            return
            [
                new ExpansionCell(Slice(cell.Left, 0, head), cell.Right),
                new ExpansionCell(Slice(cell.Left, head, nl - head), cell.Right),
            ];
        }

        var half = (nr + 1) / 2;
        return
        [
            new ExpansionCell(cell.Left, Slice(cell.Right, 0, half)),
            new ExpansionCell(cell.Left, Slice(cell.Right, half, nr - half)),
        ];
    }

    private static Guid[] Slice(IReadOnlyList<Guid> source, int start, int count)
    {
        var part = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            part[i] = source[start + i];
        }

        return part;
    }

    /// <summary>
    /// Trasforma le celle in blocchi: scarta le celle senza coppie rilevanti e quelle il cui insieme di membri coincide con una cella precedente
    /// (si tiene la prima: deduplica simmetrica), poi assegna <see cref="ExpansionBlock.Ordinal"/> da 0 nell'ordine di input.
    /// </summary>
    public static IReadOnlyList<ExpansionBlock> Blocks(IReadOnlyList<ExpansionCell> cells, IReadOnlyDictionary<Guid, ExpansionTag> tags)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(tags);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var blocks = new List<ExpansionBlock>();
        foreach (var cell in cells)
        {
            var members = UnionOf(cell);
            if (!IsRelevant(members, tags))
            {
                continue;
            }

            var key = string.Join('|', members.Select(m => m.ToString("D")));
            if (!seen.Add(key))
            {
                continue;
            }

            blocks.Add(new ExpansionBlock(blocks.Count, members, cell.Left, cell.Right));
        }

        return blocks;
    }

    /// <summary>
    /// Verifica ESATTA: enumera ogni coppia rilevante dell'unione di S1 e S2 e controlla che almeno un blocco contenga entrambi gli id
    /// (bitmap sugli indici di coppia dell'unione; costo O(somma |membri|²)). Riporta fino a 8 coppie mancanti, in ordine «D» ordinale.
    /// </summary>
    public static GridCoverage Verify(IReadOnlyList<Guid> s1, IReadOnlyList<Guid> s2, IReadOnlyList<ExpansionBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(s1);
        ArgumentNullException.ThrowIfNull(s2);
        ArgumentNullException.ThrowIfNull(blocks);
        var tags = Tags(s1, s2);
        var union = Ordered(tags.Keys);
        var n = (long)union.Count;
        var index = new Dictionary<Guid, int>(union.Count);
        for (var i = 0; i < union.Count; i++)
        {
            index[union[i]] = i;
        }

        var totalPairs = n * (n - 1) / 2;
        var bits = new long[(totalPairs + 63) / 64];

        foreach (var block in blocks)
        {
            var ids = new List<int>(block.Members.Count);
            foreach (var member in block.Members)
            {
                if (index.TryGetValue(member, out var position))
                {
                    ids.Add(position);
                }
            }

            ids.Sort();
            for (var a = 0; a < ids.Count; a++)
            {
                for (var b = a + 1; b < ids.Count; b++)
                {
                    if (ids[a] == ids[b])
                    {
                        continue;
                    }

                    var bit = PairIndex(n, ids[a], ids[b]);
                    bits[bit >> 6] |= 1L << (int)(bit & 63);
                }
            }
        }

        long required = 0;
        long covered = 0;
        var missing = new List<(Guid A, Guid B)>();
        var tagAt = new ExpansionTag[union.Count];   // tag allineati all'unione: nessun lookup nel ciclo interno O(n²)
        for (var i = 0; i < union.Count; i++)
        {
            tagAt[i] = tags[union[i]];
        }

        for (var i = 0; i < union.Count; i++)
        {
            var ti = tagAt[i];
            for (var j = i + 1; j < union.Count; j++)
            {
                var tj = tagAt[j];
                if ((ti == ExpansionTag.Left && tj == ExpansionTag.Left) || (ti == ExpansionTag.Right && tj == ExpansionTag.Right))
                {
                    continue;
                }

                required++;
                var bit = PairIndex(n, i, j);
                if ((bits[bit >> 6] & (1L << (int)(bit & 63))) != 0)
                {
                    covered++;
                }
                else if (missing.Count < MaxMissingReported)
                {
                    missing.Add((union[i], union[j]));
                }
            }
        }

        return new GridCoverage(required, covered, missing);
    }

    private static long PairIndex(long n, long i, long j) => i * n - i * (i + 1) / 2 + (j - i - 1);

    /// <summary>Rango per l'ordinamento interno a un messaggio: solo-S1 (0), poi in entrambi (1), poi solo-S2 (2).</summary>
    public static int OrderTag(ExpansionTag tag) => tag switch
    {
        ExpansionTag.Left => 0,
        ExpansionTag.Both => 1,
        ExpansionTag.Right => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, null),
    };

    /// <summary>Ordine delle voci in un messaggio: prima solo-S1, poi in entrambi, poi solo-S2, ciascun gruppo per stringa «D» ordinale.</summary>
    public static IReadOnlyList<Guid> MessageOrder(IReadOnlyList<Guid> members, IReadOnlyDictionary<Guid, ExpansionTag> tags)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(tags);
        var list = members.Select(m => (Id: m, Key: m.ToString("D"), Rank: OrderTag(tags[m]))).ToList();
        list.Sort((x, y) => x.Rank != y.Rank ? x.Rank.CompareTo(y.Rank) : StringComparer.Ordinal.Compare(x.Key, y.Key));
        return list.ConvertAll(x => x.Id);
    }
}
