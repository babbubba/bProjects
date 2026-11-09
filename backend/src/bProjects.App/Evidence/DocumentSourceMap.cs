using System.Security.Cryptography;
using System.Text;

namespace bProjects.App.Evidence;

// Manifest server dell'unità di estrazione (contracts/ai-workflow.md §2.1/§3): l'unico elenco di intervalli che il modello può citare.
// Il modello indica la `rangeKey`; documento, versione, preparazione, artefatto, blocco e intervallo sono risolti qui. Le copie di
// contesto (header ripetuti nei chunk) risolvono sempre al blocco originale e non sono mai proprietarie.

/// <summary>
/// Intervallo ammesso: <c>[Start,End)</c> in unità UTF-16 sul <c>CanonicalText</c> immutabile del blocco (<paramref name="BlockText"/>).
/// <paramref name="Owned"/> = l'unità è proprietaria dell'informazione (Primary ammessa); false = solo contesto.
/// </summary>
public sealed record DocumentSourceRange(
    string RangeKey, Guid DocumentId, Guid DocumentVersionId, Guid PreparationId, Guid ArtifactId, Guid BlockId,
    string BlockText, int Start, int End, bool Owned)
{
    public ReadOnlySpan<char> Text => BlockText.AsSpan(Start, End - Start);
}

public sealed class DocumentSourceMap
{
    private readonly Dictionary<string, DocumentSourceRange> _ranges = new(StringComparer.Ordinal);

    public DocumentSourceMap(IEnumerable<DocumentSourceRange> ranges)
    {
        foreach (var range in ranges)
        {
            Verify(range);
            if (!_ranges.TryAdd(range.RangeKey, range))
            {
                throw new ArgumentException($"rangeKey duplicata «{range.RangeKey}».", nameof(ranges));
            }
        }
    }

    public IReadOnlyCollection<DocumentSourceRange> Ranges => _ranges.Values;

    public bool TryGet(string rangeKey, out DocumentSourceRange range) => _ranges.TryGetValue(rangeKey, out range!);

    private static void Verify(DocumentSourceRange range)
    {
        if (string.IsNullOrWhiteSpace(range.RangeKey))
        {
            throw new ArgumentException("rangeKey vuota.", nameof(range));
        }

        if (range.Start < 0 || range.End <= range.Start || range.End > range.BlockText.Length)
        {
            throw new ArgumentException($"Intervallo «{range.RangeKey}» non valido: serve 0 <= start < end <= lunghezza del blocco ({range.BlockText.Length}).", nameof(range));
        }

        // Un intervallo non spezza mai una coppia surrogata: confini sempre su punti di codice interi.
        if (char.IsLowSurrogate(range.BlockText[range.Start]) && range.Start > 0 && char.IsHighSurrogate(range.BlockText[range.Start - 1]))
        {
            throw new ArgumentException($"Intervallo «{range.RangeKey}»: start cade a metà di una coppia surrogata.", nameof(range));
        }

        if (char.IsHighSurrogate(range.BlockText[range.End - 1]) && range.End < range.BlockText.Length && char.IsLowSurrogate(range.BlockText[range.End]))
        {
            throw new ArgumentException($"Intervallo «{range.RangeKey}»: end cade a metà di una coppia surrogata.", nameof(range));
        }
    }
}

/// <summary>UUID derivati in modo deterministico da componenti canoniche: stabili nel replay, mai scelti dal modello.</summary>
public static class DeterministicIds
{
    public static Guid Derive(string purpose, params object[] parts)
    {
        var text = purpose + "\n" + string.Join("\n", parts.Select(p => Convert.ToString(p, System.Globalization.CultureInfo.InvariantCulture)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);   // versione 5 (derivato da nome)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // variante RFC 4122
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>Identifica range + origine immutabile; riutilizzabile in più evidenze.</summary>
    public static Guid DocumentSourceId(Guid artifactId, Guid blockId, int start, int end) => Derive("source:document", artifactId, blockId, start, end);

    public static Guid MessageSourceId(Guid messageId, int start, int end) => Derive("source:message", messageId, start, end);

    /// <summary>Identifica il gruppo di citazione dentro una evidenza dell'unità; ordina le parti, non cambia l'originale.</summary>
    public static Guid CitationGroupId(Guid preparationId, int unitOrdinal, string evidenceKey, string groupKey) =>
        Derive("group", preparationId, unitOrdinal, evidenceKey, groupKey);
}
