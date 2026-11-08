using System.Globalization;
using System.Text;
using System.Text.Json;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// Pianificatore delle unità di estrazione documentale (002 T049; contracts/ai-workflow.md §2.1 «il planner deve dividere l'unità prima
// della chiamata», §3 «Ogni intervallo informativo ha una unità proprietaria», §4.2 Pack = massimo prefisso contiguo misurato sul payload
// completo). Funzione pura dei dati e del misuratore: stessi input e stessa misura → stesse unità e stesse chiavi (nessun Guid né orologio),
// quindi il replay ricostruisce lo stesso piano. Non chiama il modello e non scrive nulla.

/// <summary>Porzione parser già letta dal database: range proprietari (Primary) e di contesto, con il testo canonico dei blocchi.</summary>
public sealed record PlannedPortion(int Ordinal, IReadOnlyList<DocumentSourceRange> Owned, IReadOnlyList<DocumentSourceRange> Context);

/// <summary>
/// Misura del payload COMPLETO di un'unità (istruzioni, messaggio, schema): <see cref="Tokens"/> e <see cref="Quality"/> vengono dal
/// preflight del runtime; <see cref="OwnedTokens"/> sono i token dei soli intervalli proprietari (base della stima dell'output);
/// <see cref="PayloadHash"/> è l'hash del payload effettivo (inputHash logico del batch).
/// </summary>
public sealed record UnitMeasure(int Tokens, TokenCountQuality Quality, string PayloadHash, int OwnedTokens);

/// <summary>Misura il payload di un insieme ordinato di range. Non lancia per overflow: chi suddivide legge <see cref="PlannerLimits.Fits"/>.</summary>
public delegate ValueTask<UnitMeasure> UnitMeasurer(IReadOnlyList<DocumentSourceRange> ranges, CancellationToken ct);

/// <summary>
/// Limiti dell'unità: budget di input del profilo (W−O−M, già misurato dal runtime) e forma dell'output. Il testo proprietario di un
/// batch è limitato in modo che l'output stimato (<c>OwnedTokens × OutputExpansion</c>) stia nell'output massimo della fase, ragionamento
/// incluso: l'output non si taglia, quindi l'unità si riduce prima (AI §2.1). Il fattore è un parametro congelato nel manifesto, da
/// calibrare sul runtime reale (T055); non è un tetto sul contenuto dei documenti.
/// </summary>
public sealed record PlannerLimits(int InputBudgetTokens, int OutputTokens, double OutputExpansion = PlannerLimits.DefaultOutputExpansion)
{
    public const double DefaultOutputExpansion = 8.0;

    public bool Fits(UnitMeasure m) =>
        m.Quality != TokenCountQuality.Unavailable && m.Tokens <= InputBudgetTokens
        && Math.Ceiling(m.OwnedTokens * OutputExpansion) <= OutputTokens;
}

/// <summary>Batch pianificato: range ordinati (contesto non proprietario prima del testo proprietario di ogni porzione) e relativa misura.</summary>
public sealed record PlannedBatch(int Ordinal, IReadOnlyList<int> PortionOrdinals, IReadOnlyList<DocumentSourceRange> Ranges, UnitMeasure Measure)
{
    public IEnumerable<DocumentSourceRange> Owned => Ranges.Where(r => r.Owned);
}

/// <summary>Partizione esaustiva: tutti i range proprietari di tutte le porzioni compaiono, interi o in sotto-intervalli contigui, in un solo batch.</summary>
public sealed record ExtractionPartition(IReadOnlyList<PlannedBatch> Batches);

public sealed class DocumentPortionPlanner(PlannerLimits limits)
{
    /// <summary>Prefisso delle chiavi dei sotto-intervalli: <c>{rangeKey}~{start}-{end}</c> con offset assoluti nel blocco (deterministici).</summary>
    public const char PieceMarker = '~';

    private sealed record Atom(int PortionOrdinal, string OriginKey, DocumentSourceRange Piece, IReadOnlyList<DocumentSourceRange> Context, UnitMeasure Single);

    /// <summary>
    /// Partiziona tutte le porzioni, nell'ordine dato: (1) ogni range proprietario che da solo (con il contesto della sua porzione) non entra
    /// nel budget è diviso ricorsivamente su confini strutturali (paragrafo, riga, frase), mai in mezzo a una frase o a una coppia surrogata;
    /// se non si può dividere ulteriormente l'unità è irriducibile (<see cref="DocumentWorkException.DocumentIncomplete"/>, nessuna sottostringa
    /// silenziosa); (2) i pezzi si impacchettano nel massimo prefisso contiguo il cui payload completo, MISURATO ad ogni tentativo, entra nel
    /// budget (i token non sono additivi). Il contesto replicato non è mai proprietario e non si conta due volte.
    /// </summary>
    public async ValueTask<ExtractionPartition> PartitionAsync(IReadOnlyList<PlannedPortion> portions, UnitMeasurer measure, CancellationToken ct)
    {
        // Via rapida (T082): se l'INTERO insieme dei range proprietari entra nel budget in un solo payload misurato, la partizione è un unico batch. È lo stesso
        // risultato del packing incrementale (che parte da pezzi che entrano da soli e aggiunge finché il payload completo misurato entra: con l'insieme completo
        // dentro il budget nessuna aggiunta può essere rifiutata né nessun pezzo dividersi), ma costa UNA misura invece di 2n-1: ogni misura sono diverse richieste
        // al runtime (apply-template e tokenize), serializzate. Se non entra, o la misura non è utilizzabile, si prosegue con l'algoritmo invariato.
        var everything = new List<Atom>();
        foreach (var portion in portions)
        {
            var portionContext = portion.Context.Select(c => c with { Owned = false }).ToList();
            foreach (var range in portion.Owned)
            {
                everything.Add(new Atom(portion.Ordinal, range.RangeKey, range with { Owned = true }, portionContext, null!));
            }
        }

        if (everything.Count > 1)
        {
            var whole = await measure(Assemble(everything), ct);
            if (whole.Quality != TokenCountQuality.Unavailable && limits.Fits(whole))
            {
                var single = new ExtractionPartition([Close(0, everything, whole)]);
                VerifyExhaustive(portions, single);
                return single;
            }
        }

        var atoms = new List<Atom>();
        foreach (var portion in portions)
        {
            var context = portion.Context.Select(c => c with { Owned = false }).ToList();
            foreach (var range in portion.Owned)
            {
                await AddAtomsAsync(portion.Ordinal, range with { Owned = true }, range.RangeKey, context, measure, atoms, ct);
            }
        }

        var batches = new List<PlannedBatch>();
        var current = new List<Atom>();
        UnitMeasure? currentMeasure = null;
        foreach (var atom in atoms)
        {
            if (current.Count == 0)
            {
                current.Add(atom);
                currentMeasure = atom.Single;
                continue;
            }

            var candidate = Assemble([.. current, atom]);
            var measured = await measure(candidate, ct);
            if (limits.Fits(measured))
            {
                current.Add(atom);
                currentMeasure = measured;
            }
            else
            {
                batches.Add(Close(batches.Count, current, currentMeasure!));
                current = [atom];
                currentMeasure = atom.Single;
            }
        }

        if (current.Count > 0)
        {
            batches.Add(Close(batches.Count, current, currentMeasure!));
        }

        var partition = new ExtractionPartition(batches);
        VerifyExhaustive(portions, partition);
        return partition;
    }

    private async ValueTask AddAtomsAsync(
        int portionOrdinal, DocumentSourceRange piece, string originKey, IReadOnlyList<DocumentSourceRange> context, UnitMeasurer measure,
        List<Atom> atoms, CancellationToken ct)
    {
        var single = await measure(Assemble([new Atom(portionOrdinal, originKey, piece, context, null!)]), ct);
        if (single.Quality == TokenCountQuality.Unavailable)
        {
            throw new RuntimeUnverifiedException("conteggio del payload non disponibile (nessun tokenizer o calibrazione identificata)");
        }

        if (limits.Fits(single))
        {
            atoms.Add(new Atom(portionOrdinal, originKey, piece, context, single));
            return;
        }

        var halves = Split(originKey, piece);
        if (halves is null)
        {
            throw new DocumentWorkException(
                DocumentWorkException.DocumentIncomplete,
                $"L'intervallo {originKey} ({piece.End - piece.Start} caratteri) non entra nel budget dell'unità ({single.Tokens} token, input {limits.InputBudgetTokens}, "
                + $"output stimato {Math.Ceiling(single.OwnedTokens * limits.OutputExpansion):0} su {limits.OutputTokens}) e non ha confini strutturali per dividerlo: unità irriducibile.");
        }

        await AddAtomsAsync(portionOrdinal, halves.Value.Left, originKey, context, measure, atoms, ct);
        await AddAtomsAsync(portionOrdinal, halves.Value.Right, originKey, context, measure, atoms, ct);
    }

    private static PlannedBatch Close(int ordinal, List<Atom> atoms, UnitMeasure measure) =>
        new(ordinal, [.. atoms.Select(a => a.PortionOrdinal).Distinct()], Assemble(atoms), measure);

    /// <summary>
    /// Range del payload: per ogni porzione (in ordine) prima il contesto non ancora presente e non proprietario nel batch, poi i suoi pezzi
    /// proprietari. Un range di contesto che nello stesso batch è anche proprietario compare una sola volta, come proprietario.
    /// </summary>
    private static List<DocumentSourceRange> Assemble(IReadOnlyList<Atom> atoms)
    {
        var ownedKeys = atoms.Select(a => a.Piece.RangeKey).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ranges = new List<DocumentSourceRange>();
        int? portion = null;
        foreach (var atom in atoms)
        {
            if (portion != atom.PortionOrdinal)
            {
                portion = atom.PortionOrdinal;
                foreach (var context in atom.Context)
                {
                    if (!ownedKeys.Contains(context.RangeKey) && seen.Add(context.RangeKey))
                    {
                        ranges.Add(context);
                    }
                }
            }

            if (seen.Add(atom.Piece.RangeKey))
            {
                ranges.Add(atom.Piece);
            }
        }

        return ranges;
    }

    // ------------------------------------------------------------------ divisione strutturale

    /// <summary>
    /// Divide un range in due sotto-intervalli contigui sul confine strutturale più grosso disponibile (paragrafo, poi riga, poi frase),
    /// scegliendo quello più vicino alla metà. Null se non ci sono confini: l'unità è irriducibile.
    /// </summary>
    public static (DocumentSourceRange Left, DocumentSourceRange Right)? Split(string originKey, DocumentSourceRange range)
    {
        var text = range.Text.ToString();
        var cuts = Cuts(text);
        if (cuts.Count == 0)
        {
            return null;
        }

        var middle = text.Length / 2;
        var cut = cuts.OrderBy(c => Math.Abs(c - middle)).ThenBy(c => c).First();
        return (Piece(originKey, range, range.Start, range.Start + cut), Piece(originKey, range, range.Start + cut, range.End));
    }

    private static DocumentSourceRange Piece(string originKey, DocumentSourceRange range, int start, int end) =>
        range with { RangeKey = string.Create(CultureInfo.InvariantCulture, $"{originKey}{PieceMarker}{start}-{end}"), Start = start, End = end };

    /// <summary>
    /// Punti di taglio validi del livello strutturale più grosso presente. Un taglio cade dopo uno spazio bianco (che resta al pezzo
    /// precedente) e lascia testo non vuoto da entrambe le parti: nessun pezzo di soli spazi e nessuna coppia surrogata spezzata.
    /// </summary>
    private static List<int> Cuts(string text)
    {
        foreach (var boundary in new Func<string, IEnumerable<int>>[] { Paragraphs, Lines, Sentences })
        {
            var valid = boundary(text).Where(c => IsValidCut(text, c)).Distinct().ToList();
            if (valid.Count > 0)
            {
                return valid;
            }
        }

        return [];
    }

    private static bool IsValidCut(string text, int cut) =>
        cut > 0 && cut < text.Length && !string.IsNullOrWhiteSpace(text[..cut]) && !string.IsNullOrWhiteSpace(text[cut..])
        && !char.IsWhiteSpace(text[cut]) && char.IsWhiteSpace(text[cut - 1]);

    /// <summary>Dopo una riga vuota (due a capo con eventuali spazi in mezzo) e tutto lo spazio bianco che segue.</summary>
    private static IEnumerable<int> Paragraphs(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            var j = i + 1;
            while (j < text.Length && text[j] is ' ' or '\t' or '\r')
            {
                j++;
            }

            if (j < text.Length && text[j] == '\n')
            {
                var end = j;
                while (end < text.Length && char.IsWhiteSpace(text[end]))
                {
                    end++;
                }

                yield return end;
                i = end - 1;
            }
        }
    }

    /// <summary>Dopo ogni a capo e lo spazio bianco che segue.</summary>
    private static IEnumerable<int> Lines(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                var end = i + 1;
                while (end < text.Length && char.IsWhiteSpace(text[end]))
                {
                    end++;
                }

                yield return end;
                i = end - 1;
            }
        }
    }

    /// <summary>Dopo una punteggiatura di fine frase seguita da spazio, e lo spazio bianco che segue.</summary>
    private static IEnumerable<int> Sentences(string text)
    {
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?' or ';' && char.IsWhiteSpace(text[i + 1]))
            {
                var end = i + 1;
                while (end < text.Length && char.IsWhiteSpace(text[end]))
                {
                    end++;
                }

                yield return end;
                i = end - 1;
            }
        }
    }

    // ------------------------------------------------------------------ copertura

    /// <summary>
    /// Verifica che ogni range proprietario di ogni porzione sia coperto esattamente una volta: i pezzi di un range, in ordine di batch, sono
    /// contigui da Start a End e nessun pezzo proprietario è estraneo. Un errore qui è un difetto del planner o dei dati (range proprietari
    /// duplicati), mai una porzione omessa in silenzio.
    /// </summary>
    public static void VerifyExhaustive(IReadOnlyList<PlannedPortion> portions, ExtractionPartition partition)
    {
        var pieces = partition.Batches.SelectMany(b => b.Owned).ToList();
        var expected = portions.SelectMany(p => p.Owned).ToList();
        var byOrigin = pieces.GroupBy(p => OriginOf(p.RangeKey), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (byOrigin.Count != expected.Count || expected.Select(e => e.RangeKey).Distinct(StringComparer.Ordinal).Count() != expected.Count)
        {
            throw new DocumentWorkException(DocumentWorkException.InvalidInput, "I range proprietari delle porzioni non sono univoci o la partizione non li copre tutti.");
        }

        foreach (var range in expected)
        {
            if (!byOrigin.TryGetValue(range.RangeKey, out var parts))
            {
                throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"Il range proprietario {range.RangeKey} non è coperto da alcun batch.");
            }

            var position = range.Start;
            foreach (var part in parts)
            {
                if (part.BlockId != range.BlockId || part.Start != position || part.End <= part.Start)
                {
                    throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"Il range proprietario {range.RangeKey} non è coperto in modo contiguo.");
                }

                position = part.End;
            }

            if (position != range.End)
            {
                throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"Il range proprietario {range.RangeKey} non è coperto fino alla fine.");
            }
        }
    }

    /// <summary>Chiave del range originale da cui deriva una chiave di sotto-intervallo.</summary>
    public static string OriginOf(string rangeKey)
    {
        var at = rangeKey.IndexOf(PieceMarker, StringComparison.Ordinal);
        return at < 0 ? rangeKey : rangeKey[..at];
    }

    /// <summary>Descrizione canonica e deterministica del batch (chiavi e offset, mai testo): parte del manifesto e dell'identità.</summary>
    public static string DescribeInputs(PlannedBatch batch)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("ordinal", batch.Ordinal);
            writer.WriteStartArray("portions");
            foreach (var portion in batch.PortionOrdinals)
            {
                writer.WriteNumberValue(portion);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("ranges");
            foreach (var range in batch.Ranges)
            {
                writer.WriteStartObject();
                writer.WriteString("rangeKey", range.RangeKey);
                writer.WriteString("blockId", range.BlockId);
                writer.WriteNumber("start", range.Start);
                writer.WriteNumber("end", range.End);
                writer.WriteBoolean("owned", range.Owned);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
