using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Analysis;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.AI;

// Mappa storica delle fonti di una bozza (002 T086; contracts/ai-workflow.md §4.5 «Raccordo minimo W1»): per ogni fonte citata da un blocco, a quale item e a quale
// gruppo di citazione (evidenza, citationGroupId, part) appartiene. Il renderer ricostruisce i gruppi da QUESTA mappa persistita nel WorkOutput del run e non deduce un
// gruppo unico dalla sequenza globale dei sourceId: due gruppi possono condividere lo stesso header/sourceId e restano due voci distinte. Una fonte che il registro
// Evidence non conosce (PmRevision, registro assente) è una voce con i campi di gruppo null, mai inventati. Funzione pura dei blocchi e del registro.

/// <summary>Fonte di un'evidenza con il gruppo di citazione e la parte (ordine delle parti del gruppo).</summary>
public sealed record LedgerSource(Guid SourceId, Guid CitationGroupId, int Part);

/// <summary>Registro minimo letto dal ledger Evidence: evidenze di ogni item (mappa item→evidenze dell'analisi) e fonti persistite di ogni evidenza.</summary>
public sealed class DraftSourceLedger(IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> itemEvidence, IReadOnlyDictionary<Guid, IReadOnlyList<LedgerSource>> evidenceSources)
{
    public static DraftSourceLedger Empty { get; } = new(new Dictionary<Guid, IReadOnlyList<Guid>>(), new Dictionary<Guid, IReadOnlyList<LedgerSource>>());

    public IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> ItemEvidence { get; } = itemEvidence;

    public IReadOnlyDictionary<Guid, IReadOnlyList<LedgerSource>> EvidenceSources { get; } = evidenceSources;

    /// <summary>
    /// Legge dal database la mappa item→evidenze dell'analisi (AnalysisEvidenceLinks) e le fonti persistite delle evidenze. Lettura lenient del JSON delle evidenze: una riga
    /// non interpretabile non contribuisce (voce con campi null), non fa fallire la generazione. Nessuna scrittura.
    /// </summary>
    public static async Task<DraftSourceLedger> LoadAsync(AppDbContext db, Guid analysisId, CancellationToken ct)
    {
        var links = await db.AnalysisEvidenceLinks.AsNoTracking().Where(l => l.RunId == analysisId).Select(l => new { l.ItemId, l.EvidenceId }).ToListAsync(ct);
        if (links.Count == 0)
        {
            return Empty;
        }

        var evidenceIds = links.Select(l => l.EvidenceId).Distinct().ToList();
        var contents = await db.Evidences.AsNoTracking().Where(e => evidenceIds.Contains(e.Id)).Select(e => new { e.Id, e.Content }).ToListAsync(ct);
        var sources = new Dictionary<Guid, IReadOnlyList<LedgerSource>>();
        foreach (var row in contents)
        {
            sources[row.Id] = ParseSources(row.Content);
        }

        var itemEvidence = links.GroupBy(l => l.ItemId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)[.. g.Select(l => l.EvidenceId).Distinct().OrderBy(id => id.ToString("D"), StringComparer.Ordinal)]);
        return new DraftSourceLedger(itemEvidence, sources);
    }

    private static List<LedgerSource> ParseSources(string content)
    {
        var list = new List<LedgerSource>();
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return list;
            }

            foreach (var property in document.RootElement.EnumerateObject().Where(p => p.NameEquals("sources") && p.Value.ValueKind == JsonValueKind.Array))
            {
                foreach (var source in property.Value.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object))
                {
                    if (Guid.TryParse(Text(source, "sourceId"), out var sourceId) && Guid.TryParse(Text(source, "citationGroupId"), out var group)
                        && source.TryGetProperty("part", out var part) && part.ValueKind == JsonValueKind.Number && part.TryGetInt32(out var number))
                    {
                        list.Add(new LedgerSource(sourceId, group, number));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Riga non interpretabile: nessuna fonte dal registro.
        }

        return list;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Voce della mappa: la fonte citata per un item, con evidenza e gruppo (null se il registro non li conosce).</summary>
public sealed record DraftSourceMapEntry(Guid ItemId, Guid? EvidenceId, Guid? CitationGroupId, int? Part, Guid SourceId);

public static class DraftSourceMap
{
    /// <summary>
    /// Una voce per ogni (item del blocco, fonte del blocco che sostiene l'item, evidenza dell'item che porta quella fonte), nell'ordine di blocco, item e marcatore. Una fonte
    /// che non sostiene l'item non si mappa mai; una fonte senza evidenza nel registro è una sola voce con i campi di gruppo null.
    /// </summary>
    public static IReadOnlyList<DraftSourceMapEntry> Build(
        IReadOnlyList<DraftBlock> blocks, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> itemSources, DraftSourceLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(itemSources);
        ArgumentNullException.ThrowIfNull(ledger);
        var entries = new List<DraftSourceMapEntry>();
        foreach (var block in blocks)
        {
            foreach (var itemId in block.ItemIds.Where(itemSources.ContainsKey))
            {
                var supporting = itemSources[itemId];
                var evidences = ledger.ItemEvidence.GetValueOrDefault(itemId) ?? [];
                foreach (var sourceId in block.SourceIds.Where(supporting.Contains))
                {
                    var found = evidences
                        .SelectMany(evidence => (ledger.EvidenceSources.GetValueOrDefault(evidence) ?? []).Where(s => s.SourceId == sourceId).OrderBy(s => s.Part).Select(s => (evidence, s)))
                        .Select(x => new DraftSourceMapEntry(itemId, x.evidence, x.s.CitationGroupId, x.s.Part, sourceId))
                        .ToList();
                    entries.AddRange(found.Count > 0 ? found : [new DraftSourceMapEntry(itemId, null, null, null, sourceId)]);
                }
            }
        }

        return entries;
    }

    /// <summary>Forma JSON della mappa persistita nel WorkOutput (campi di gruppo null quando il registro non li conosce).</summary>
    public static JsonArray ToJson(IEnumerable<DraftSourceMapEntry> entries) => new([.. entries.Select(e => (JsonNode)new JsonObject
    {
        ["itemId"] = e.ItemId.ToString("D"),
        ["evidenceId"] = e.EvidenceId?.ToString("D"),
        ["citationGroupId"] = e.CitationGroupId?.ToString("D"),
        ["part"] = e.Part,
        ["sourceId"] = e.SourceId.ToString("D"),
    })]);
}
