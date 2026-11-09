using System.Text.Json;

namespace bProjects.App.Documents;

/// <summary>
/// Composizione statica delle unità di estrazione (DocumentJob ExtractUnit) a partire dalle porzioni del parser (002 T051). Funzione pura dei dati
/// persistiti, quindi deterministica: stesse porzioni → stesse unità, senza orologio né Guid.
///
/// Perché statica: la partizione ESAUSTIVA e MISURATA del payload (token, schema, output) è di <c>AI/DocumentPortionPlanner</c> e richiede il
/// runtime verificato; il commit del parsing è CPU, breve e non deve dipendere dal modello. Qui si decide soltanto quali porzioni consecutive
/// formano un'unità (un solo claim, un solo slot AI, poi rilascio e precedenza della chat): un tetto prudente sul testo proprietario
/// (<see cref="TargetOwnedChars"/>). Dentro l'unità il <c>DocumentPortionPlanner</c> suddivide ulteriormente in batch misurati; una porzione che
/// da sola supera il tetto è una unità a sé (il planner la divide o la dichiara irriducibile). Ogni porzione appartiene a una e una sola unità,
/// le unità sono contigue in ordine di porzione e l'unità prende l'ordinale della prima porzione (<c>DocumentEvidenceAgent.UnitOrdinalStride</c>).
/// </summary>
public static class DocumentUnitPlan
{
    /// <summary>Tentativi massimi di un'unità (Plan: unità di estrazione con max 3 tentativi, incluso il primo claim).</summary>
    public const int ExtractMaxAttempts = 3;

    /// <summary>Tetto prudente di testo proprietario per unità (UTF-16): ~1500 token, oltre il budget di output di un batch con i default.</summary>
    public const int TargetOwnedChars = 6_000;

    /// <summary>Raggruppa le porzioni (già in ordine di ordinale) in unità contigue.</summary>
    public static IReadOnlyList<IReadOnlyList<DocumentPortion>> Group(IReadOnlyList<DocumentPortion> portions, int targetOwnedChars = TargetOwnedChars)
    {
        var groups = new List<IReadOnlyList<DocumentPortion>>();
        var current = new List<DocumentPortion>();
        long size = 0;
        foreach (var portion in portions.OrderBy(p => p.Ordinal))
        {
            var owned = OwnedChars(portion);
            if (current.Count > 0 && size + owned > targetOwnedChars)
            {
                groups.Add(current);
                current = [];
                size = 0;
            }

            current.Add(portion);
            size += owned;
        }

        if (current.Count > 0)
        {
            groups.Add(current);
        }

        return groups;
    }

    /// <summary>Caratteri UTF-16 dei range proprietari della porzione (il contesto non conta: non è proprietario).</summary>
    public static long OwnedChars(DocumentPortion portion)
    {
        using var document = JsonDocument.Parse(portion.RangesJson);
        long total = 0;
        foreach (var range in document.RootElement.EnumerateArray())
        {
            total += range.GetProperty("end").GetInt32() - range.GetProperty("start").GetInt32();
        }

        return total;
    }
}
