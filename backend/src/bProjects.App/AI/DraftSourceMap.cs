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

/// <summary>La mappa persistita di una bozza che la richiede è assente o incoerente con run, revisione, fonti o registro Evidence: la bozza non è valida.</summary>
public sealed class DraftSourceMapInconsistentException(string message) : Exception(message);

/// <summary>
/// Lettura della mappa storica persistita nei WorkOutput Committed del run che ha prodotto la bozza (002 T087; contracts/http-api.md, «Delta T087»). Nessuna ricostruzione: se il
/// run non ha output per unità editoriali (bozza storica o a singola chiamata) la mappa è assente (<c>null</c>); se li ha, ogni unità deve portare la propria mappa legata allo
/// stesso run e alla stessa revisione della bozza, e la mappa deve coincidere con quella ricalcolabile dai blocchi dell'unità, dal pacchetto della revisione e dal registro
/// Evidence. Una mappa mancante o incoerente è <see cref="DraftSourceMapInconsistentException"/>, mai una bozza accettata in silenzio.
/// </summary>
public static class DraftSourceMapReader
{
    public static async Task<IReadOnlyList<DraftSourceMapEntry>?> ReadAsync(AppDbContext db, Draft draft, AnalysisRevision revision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(revision);
        var outputs = await db.WorkOutputs.AsNoTracking()
            .Where(o => o.OwnerKind == WorkOwnerKind.ProcessingRun && o.OwnerId == draft.ProcessingRunId && o.ProjectId == draft.ProjectId
                && o.SchemaVersion == DocumentDraftEditorial.SchemaVersion)
            .ToListAsync(ct);
        if (outputs.Count == 0)
        {
            return null;
        }

        try
        {
            return await ReadUnitsAsync(db, draft, revision, outputs, ct);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new DraftSourceMapInconsistentException("La mappa delle fonti persistita non è nel formato atteso: " + ex.Message);
        }
    }

    private static async Task<IReadOnlyList<DraftSourceMapEntry>> ReadUnitsAsync(AppDbContext db, Draft draft, AnalysisRevision revision, List<WorkOutput> outputs, CancellationToken ct)
    {
        static DraftSourceMapInconsistentException Bad(string reason) => new("La mappa delle fonti della bozza è incoerente: " + reason);

        var manifestHash = await db.WorkManifests.AsNoTracking()
            .Where(m => m.OwnerKind == WorkOwnerKind.ProcessingRun && m.OwnerId == draft.ProcessingRunId).Select(m => m.Hash).SingleOrDefaultAsync(ct);
        var package = DocumentDraftPackage.From(DocumentAnalysisContentJson.Deserialize(revision.ContentJson));
        var ledger = await DraftSourceLedger.LoadAsync(db, revision.AnalysisId, ct);
        var units = new SortedDictionary<int, IReadOnlyList<DraftSourceMapEntry>>();
        var seenItems = new HashSet<Guid>();
        foreach (var output in outputs)
        {
            if (output.PayloadJson is null || output.Status != WorkOutputStatus.Committed || output.ManifestHash != manifestHash
                || output.ValidationVersion != DocumentDraftEditorial.ValidationVersion || CanonicalJson.Sha256Hex(output.PayloadJson) != output.OutputHash)
            {
                throw Bad("un output di unità non è integro o non appartiene al manifesto del run");
            }

            using var payload = JsonDocument.Parse(output.PayloadJson);
            var root = payload.RootElement;
            if (root.GetProperty("revisionId").GetGuid() != draft.AnalysisRevisionId)
            {
                throw Bad("l'output appartiene a un'altra revisione");
            }

            var ordinal = root.GetProperty("ordinal").GetInt32();
            var primaries = root.GetProperty("primaryItemIds").EnumerateArray().Select(e => e.GetGuid()).ToList();
            if (primaries.Any(id => !package.ItemSources.ContainsKey(id)) || primaries.Any(id => !seenItems.Add(id)))
            {
                throw Bad("gli item principali dell'unità non appartengono alla revisione o sono ripetuti");
            }

            if (!root.TryGetProperty("sourceMap", out var stored) || stored.ValueKind != JsonValueKind.Array)
            {
                throw Bad($"l'unità {ordinal} non porta la mappa");
            }

            var entries = Parse(stored);
            using var maps = JsonDocument.Parse(output.MapsJson ?? throw Bad($"l'unità {ordinal} non ha le mappe dell'output"));
            if (!Parse(maps.RootElement.GetProperty("sourceMap")).SequenceEqual(entries))
            {
                throw Bad($"la mappa dell'unità {ordinal} non coincide con le mappe dell'output");
            }

            var blocks = maps.RootElement.GetProperty("blocks").EnumerateArray()
                .Select(b => new DraftBlock(
                    [.. b.GetProperty("itemIds").EnumerateArray().Select(e => e.GetGuid())], string.Empty, [.. b.GetProperty("sourceIds").EnumerateArray().Select(e => e.GetGuid())]))
                .ToList();
            if (blocks.Any(b => b.ItemIds.Any(i => !primaries.Contains(i)) || b.SourceIds.Any(s => !package.SourceIds.Contains(s))))
            {
                throw Bad($"i blocchi dell'unità {ordinal} citano item o fonti estranei");
            }

            if (!DraftSourceMap.Build(blocks, package.ItemSources, ledger).SequenceEqual(entries))
            {
                throw Bad($"la mappa dell'unità {ordinal} non coincide con fonti e registro Evidence della revisione");
            }

            if (!units.TryAdd(ordinal, entries))
            {
                throw Bad($"l'unità {ordinal} è ripetuta");
            }
        }

        if (!units.Keys.SequenceEqual(Enumerable.Range(0, units.Count)))
        {
            throw Bad("le unità non sono consecutive dall'ordinale 0");
        }

        return [.. units.Values.SelectMany(e => e)];
    }

    private static List<DraftSourceMapEntry> Parse(JsonElement array) => [.. array.EnumerateArray().Select(e =>
    {
        Guid? OptionalGuid(string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetGuid();
        var part = e.GetProperty("part");
        return new DraftSourceMapEntry(
            e.GetProperty("itemId").GetGuid(), OptionalGuid("evidenceId"), OptionalGuid("citationGroupId"),
            part.ValueKind == JsonValueKind.Null ? null : part.GetInt32(), e.GetProperty("sourceId").GetGuid());
    })];
}
