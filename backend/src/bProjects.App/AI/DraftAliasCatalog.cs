using bProjects.App.Analysis;

namespace bProjects.App.AI;

// Catalogo alias della bozza `draft-alias-catalog-1` (contracts/ai-workflow.md §7.11, T127): funzione pura della revisione confermata 1.1.0 congelata
// sul run. Item e alternative dei conflitti sono `I<n>`, le fonti che sostengono almeno un item sono `F<n>`; l'id del conflitto e le fonti della sola
// risoluzione non hanno alias, quindi non sono citabili per costruzione. Gli alias sono etichette di lettura, mai identità persistite.

/// <summary>Item (o alternativa di un conflitto) con il suo alias, la sezione di appartenenza e gli alias delle fonti che lo sostengono.</summary>
public sealed record DraftAliasItem(string Alias, Guid ItemId, string Section, IReadOnlyList<string> SourceAliases);

public sealed record DraftAliasSource(string Alias, Guid SourceId, DocumentAnalysisSourceKind Kind);

public sealed class DraftAliasCatalog
{
    public const string Version = "draft-alias-catalog-1";

    private readonly Dictionary<string, DraftAliasItem> _items;
    private readonly Dictionary<string, DraftAliasSource> _sources;
    private readonly Dictionary<Guid, string> _itemAlias;
    private readonly Dictionary<Guid, string> _sourceAlias;

    private DraftAliasCatalog(IReadOnlyList<DraftAliasItem> items, IReadOnlyList<DraftAliasSource> sources)
    {
        Items = items;
        Sources = sources;
        _items = items.ToDictionary(i => i.Alias, StringComparer.Ordinal);
        _sources = sources.ToDictionary(s => s.Alias, StringComparer.Ordinal);
        _itemAlias = items.ToDictionary(i => i.ItemId, i => i.Alias);
        _sourceAlias = sources.ToDictionary(s => s.SourceId, s => s.Alias);
        CatalogHash = CanonicalJson.Sha256Hex(System.Text.Json.JsonSerializer.Serialize(new
        {
            version = Version,
            items = items.Select(i => new { alias = i.Alias, id = i.ItemId.ToString("D"), section = i.Section, sources = i.SourceAliases }),
            sources = sources.Select(s => new { alias = s.Alias, id = s.SourceId.ToString("D"), kind = s.Kind.ToString() }),
        }));
    }

    public IReadOnlyList<DraftAliasItem> Items { get; }

    public IReadOnlyList<DraftAliasSource> Sources { get; }

    /// <summary>SHA-256 della forma canonica del catalogo (alias, id canonici, sezioni e legami item→fonte).</summary>
    public string CatalogHash { get; }

    /// <summary>
    /// Costruisce il catalogo dalla revisione confermata, nell'ordine di <see cref="DocumentDraftPackage"/> (sezioni e poi alternative dei conflitti); le fonti
    /// sono numerate al primo incontro. Un id di item duplicato o una fonte citata da un item ma assente dalla revisione è un conflitto d'integrità.
    /// </summary>
    public static DraftAliasCatalog Build(DocumentAnalysisContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var sections = new (string Name, IReadOnlyList<DocumentAnalysisItem> Items)[]
        {
            ("goals", content.Goals), ("overview", content.ContextAndScope.Overview), ("included", content.ContextAndScope.Included),
            ("excluded", content.ContextAndScope.Excluded), ("requirements", content.Requirements), ("operationalFlows", content.OperationalFlows),
            ("constraints", content.Constraints), ("dependencies", content.Dependencies), ("assumptions", content.Assumptions), ("openQuestions", content.OpenQuestions),
            ("conflicts", [.. content.Conflicts.SelectMany(c => c.Alternatives)]),
        };
        var known = content.Sources.ToDictionary(s => s.Id);
        var sources = new List<DraftAliasSource>();
        var sourceAlias = new Dictionary<Guid, string>();
        var items = new List<DraftAliasItem>();
        var seen = new HashSet<Guid>();
        foreach (var (name, list) in sections)
        {
            foreach (var item in list)
            {
                if (!seen.Add(item.Id))
                {
                    throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"Catalogo alias della bozza: l'item {item.Id:D} compare più di una volta nella revisione confermata.");
                }

                var aliases = new List<string>();
                foreach (var reference in item.EvidenceRefs.Distinct())
                {
                    if (!known.TryGetValue(reference, out var source))
                    {
                        throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"Catalogo alias della bozza: l'item {item.Id:D} cita la fonte {reference:D}, assente dalla revisione confermata.");
                    }

                    if (!sourceAlias.TryGetValue(reference, out var alias))
                    {
                        alias = "F" + (sources.Count + 1);
                        sourceAlias[reference] = alias;
                        sources.Add(new DraftAliasSource(alias, source.Id, source.Kind));
                    }

                    aliases.Add(alias);
                }

                items.Add(new DraftAliasItem("I" + (items.Count + 1), item.Id, name, aliases));
            }
        }

        return new DraftAliasCatalog(items, sources);
    }

    public DraftAliasItem? Item(string alias) => _items.GetValueOrDefault(alias);

    public DraftAliasSource? Source(string alias) => _sources.GetValueOrDefault(alias);

    public string? AliasOfItem(Guid itemId) => _itemAlias.GetValueOrDefault(itemId);

    public string? AliasOfSource(Guid sourceId) => _sourceAlias.GetValueOrDefault(sourceId);
}
