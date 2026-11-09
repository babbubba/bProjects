using System.Text.Json;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// Catalogo alias immutabile `alias-catalog-1` (contracts/ai-workflow.md §7.3, T125): funzione pura dello snapshot congelato del run.

/// <summary>Legame evidenza→fonte: ruolo, gruppo di citazione e parte appartengono alla citazione dell'evidenza, la posizione alla fonte.</summary>
public sealed record AliasSourceLink(string SourceAlias, EvidenceSourceRole Role, Guid CitationGroupId, int Part);

public sealed record AliasEvidenceEntry
{
    public required string Alias { get; init; }
    public required Guid EvidenceId { get; init; }
    public required Guid EvidenceVersionId { get; init; }
    public required string ContentHash { get; init; }
    public required string EntryId { get; init; }
    public required EvidenceOriginKind Origin { get; init; }
    public required Guid? DocumentId { get; init; }
    public required Guid? DocumentVersionId { get; init; }
    public required Guid? PreparationId { get; init; }
    public required EvidenceKind Kind { get; init; }
    public required EvidencePolarity Polarity { get; init; }
    public required EvidenceCertainty Certainty { get; init; }
    public required IReadOnlyList<AliasSourceLink> Sources { get; init; }
}

public sealed record AliasSourceEntry
{
    public required string Alias { get; init; }
    public required Guid SourceId { get; init; }
    public required EvidenceSourceKind Provenance { get; init; }
    public required Guid? MessageId { get; init; }
    public required long? Sequence { get; init; }
    public required Guid? DocumentId { get; init; }
    public required Guid? DocumentVersionId { get; init; }
    public required Guid? PreparationId { get; init; }
    public required Guid? ArtifactId { get; init; }
    public required Guid? BlockId { get; init; }
    public required int Start { get; init; }
    public required int End { get; init; }
    public required string Quote { get; init; }
}

public sealed record AliasRelationEntry
{
    public required string Alias { get; init; }
    public required Guid RelationId { get; init; }
    public required string FromAlias { get; init; }
    public required string ToAlias { get; init; }
    public required Guid FromEvidenceId { get; init; }
    public required Guid ToEvidenceId { get; init; }
    public required EvidenceRelationType Type { get; init; }
    public required EvidenceRelationBasis Basis { get; init; }
}

/// <summary>Dati serializzabili del catalogo (payload JSONB del WorkOutput).</summary>
public sealed record AnalysisAliasCatalogData
{
    public required string CatalogVersion { get; init; }
    public required string ContractVersion { get; init; }
    public required string CanonIdsVersion { get; init; }
    public required Guid RunId { get; init; }
    public required Guid ProjectId { get; init; }
    public required string SourceHash { get; init; }
    public required IReadOnlyList<AliasEvidenceEntry> Evidence { get; init; }
    public required IReadOnlyList<AliasSourceEntry> Sources { get; init; }
    public required IReadOnlyList<AliasRelationEntry> Relations { get; init; }
}

/// <summary>
/// Catalogo alias immutabile (`alias-catalog-1`): funzione pura dello snapshot congelato del run. Ogni alias è risolvibile solo qui: nessuna
/// normalizzazione, nessuna ricerca fuzzy. Gli alias sono etichette di lettura, mai identità persistite.
/// </summary>
public sealed class AnalysisAliasCatalog
{
    public const string Version = "alias-catalog-1";
    public const string CanonIdsVersion = "canon-ids-1";

    private readonly Dictionary<string, AliasEvidenceEntry> _evidence;
    private readonly Dictionary<Guid, AliasEvidenceEntry> _evidenceById;
    private readonly Dictionary<string, AliasSourceEntry> _sources;
    private readonly Dictionary<Guid, AliasSourceEntry> _sourceById;
    private readonly Dictionary<string, AliasRelationEntry> _relations;
    private readonly string _canonical;

    private AnalysisAliasCatalog(AnalysisAliasCatalogData data)
    {
        Data = data;
        _evidence = Index(data.Evidence, e => e.Alias, 'E');
        _evidenceById = Index(data.Evidence, e => e.EvidenceId);
        _sources = Index(data.Sources, s => s.Alias, 'S');
        _sourceById = Index(data.Sources, s => s.SourceId);
        _relations = Index(data.Relations, r => r.Alias, 'R');
        foreach (var link in data.Evidence.SelectMany(e => e.Sources))
        {
            if (!_sources.ContainsKey(link.SourceAlias))
            {
                throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, $"Catalogo alias incoerente: la fonte {link.SourceAlias} non esiste.");
            }
        }

        foreach (var relation in data.Relations)
        {
            if (!_evidence.ContainsKey(relation.FromAlias) || !_evidence.ContainsKey(relation.ToAlias))
            {
                throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, $"Catalogo alias incoerente: gli endpoint di {relation.Alias} non esistono.");
            }
        }

        _canonical = ReconciliationCodec.Canonical(data);
        CatalogHash = CanonicalJson.HashText(_canonical);
        RunIndependentHash = CanonicalJson.HashText(ReconciliationCodec.Canonical(data with { RunId = Guid.Empty }));
    }

    public AnalysisAliasCatalogData Data { get; }

    /// <summary>SHA-256 della forma canonica RFC 8785 del catalogo (comprende l'id del run).</summary>
    public string CatalogHash { get; }

    /// <summary>Hash del catalogo senza l'id del run: uguale fra un root e il suo retry PM (stesse fonti e stesse evidenze, solo il root cambia).</summary>
    public string RunIndependentHash { get; }

    /// <summary>
    /// Costruisce il catalogo dallo snapshot congelato. L'ordine delle evidenze è quello di lettura già stabilito dal server (messaggi PM per sequenza e
    /// ordinale, poi documenti nell'ordine dello snapshot e per ordinale); le fonti sono numerate al primo incontro, per gruppo di citazione e parte.
    /// Una fonte originale non verificabile è <see cref="DocumentWorkException.SourceSnapshotInvalid"/>.
    /// </summary>
    public static AnalysisAliasCatalog Build(DocumentAnalysisInput input) => Build(input, AnalysisAliasContracts.SchemaVersion);

    /// <summary>Come <see cref="Build(DocumentAnalysisInput)"/>, con la versione del contratto AI nell'identità del catalogo (T135: alias-1 o alias-2); la forma <c>alias-catalog-1</c> non cambia.</summary>
    public static AnalysisAliasCatalog Build(DocumentAnalysisInput input, string contractVersion)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (contractVersion is not (AnalysisAliasContracts.SchemaVersion or AnalysisAliasContracts.SchemaVersionV2))
        {
            throw new ArgumentException($"Versione di contratto sconosciuta per il catalogo alias: {contractVersion}.", nameof(contractVersion));
        }

        var evidence = input.Evidence;
        var snapshot = input.Snapshot;
        if (evidence.Count != snapshot.Evidence.Count)
        {
            throw Invalid("le evidenze del run non coincidono con quelle dello snapshot.");
        }

        if (evidence.Select(e => e.EvidenceId).Distinct().Count() != evidence.Count)
        {
            throw Invalid("evidenza duplicata nello snapshot.");
        }

        var dialog = input.Dialog.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var sourceEntries = new List<AliasSourceEntry>();
        var sourceByKey = new Dictionary<Guid, AliasSourceEntry>();
        var evidenceEntries = new List<AliasEvidenceEntry>(evidence.Count);
        var aliasOf = new Dictionary<Guid, string>(evidence.Count);

        for (var i = 0; i < evidence.Count; i++)
        {
            var e = evidence[i];
            var s = snapshot.Evidence[i];
            if (s.EvidenceId != e.EvidenceId || s.EvidenceVersionId != e.VersionId || s.ContentHash != e.ContentHash)
            {
                throw Invalid($"l'evidenza {e.EvidenceId:D} non coincide con la voce dello snapshot.");
            }

            var violations = PersistedSourceRules.Check(e.Sources);
            if (violations.Count > 0)
            {
                throw Invalid($"fonti dell'evidenza {e.EvidenceId:D} non valide: {string.Join(" | ", violations)}");
            }

            // Gruppi nell'ordine di prima comparsa, parti crescenti: stessa sequenza per ogni lettura del ledger.
            var groupOrder = new Dictionary<Guid, int>();
            foreach (var source in e.Sources)
            {
                groupOrder.TryAdd(source.CitationGroupId, groupOrder.Count);
            }

            var links = new List<AliasSourceLink>();
            foreach (var source in e.Sources.OrderBy(x => groupOrder[x.CitationGroupId]).ThenBy(x => x.Part))
            {
                if (source.Kind == EvidenceSourceKind.Message
                    && (!dialog.TryGetValue(source.MessageId!.Value, out var message) || message.Sequence != source.Sequence))
                {
                    throw Invalid($"il messaggio {source.MessageId:D} della fonte {source.SourceId:D} non è nel dialogo congelato.");
                }

                var entry = new AliasSourceEntry
                {
                    Alias = "S" + (sourceEntries.Count + 1), SourceId = source.SourceId, Provenance = source.Kind, MessageId = source.MessageId, Sequence = source.Sequence,
                    DocumentId = source.DocumentId, DocumentVersionId = source.DocumentVersionId, PreparationId = source.PreparationId, ArtifactId = source.ArtifactId,
                    BlockId = source.BlockId, Start = source.Start, End = source.End, Quote = source.Quote,
                };
                if (sourceByKey.TryGetValue(source.SourceId, out var known))
                {
                    // Lo stesso passaggio citato da più evidenze: un solo S, ma la posizione deve coincidere esattamente.
                    if (known with { Alias = entry.Alias } != entry)
                    {
                        throw Invalid($"la fonte {source.SourceId:D} ha posizioni diverse in evidenze diverse.");
                    }

                    entry = known;
                }
                else
                {
                    sourceByKey[source.SourceId] = entry;
                    sourceEntries.Add(entry);
                }

                if (links.Any(l => l.SourceAlias == entry.Alias))
                {
                    throw Invalid($"la fonte {source.SourceId:D} è citata due volte dalla stessa evidenza.");
                }

                links.Add(new AliasSourceLink(entry.Alias, source.Role, source.CitationGroupId, source.Part));
            }

            var passage = e.Sources.FirstOrDefault(x => x.Kind == EvidenceSourceKind.DocumentPassage);
            var alias = "E" + (i + 1);
            aliasOf[e.EvidenceId] = alias;
            evidenceEntries.Add(new AliasEvidenceEntry
            {
                Alias = alias, EvidenceId = e.EvidenceId, EvidenceVersionId = e.VersionId, ContentHash = e.ContentHash,
                EntryId = ReconciliationIds.EntryId(snapshot.SourceHash, e.EvidenceId, e.ContentHash), Origin = e.Origin,
                DocumentId = passage?.DocumentId, DocumentVersionId = passage?.DocumentVersionId, PreparationId = passage?.PreparationId,
                Kind = e.Kind, Polarity = e.Polarity, Certainty = e.Certainty, Sources = links,
            });
        }

        var position = evidenceEntries.Select((e, index) => (e.EvidenceId, index)).ToDictionary(t => t.EvidenceId, t => t.index);
        var relations = new List<SnapshotRelation>();
        foreach (var relation in snapshot.Relations)
        {
            if (!position.ContainsKey(relation.FromEvidenceId) || !position.ContainsKey(relation.ToEvidenceId))
            {
                throw Invalid($"la relazione {relation.RelationId:D} ha un endpoint fuori snapshot.");
            }

            relations.Add(relation);
        }

        if (relations.Select(r => r.RelationId).Distinct().Count() != relations.Count)
        {
            throw Invalid("relazione nota duplicata nello snapshot.");
        }

        var ordered = relations
            .OrderBy(r => position[r.FromEvidenceId]).ThenBy(r => position[r.ToEvidenceId]).ThenBy(r => (int)r.Type).ThenBy(r => r.RelationId.ToString("D"), StringComparer.Ordinal)
            .Select((r, index) => new AliasRelationEntry
            {
                Alias = "R" + (index + 1), RelationId = r.RelationId, FromAlias = aliasOf[r.FromEvidenceId], ToAlias = aliasOf[r.ToEvidenceId],
                FromEvidenceId = r.FromEvidenceId, ToEvidenceId = r.ToEvidenceId, Type = r.Type, Basis = r.Basis,
            })
            .ToList();

        return new AnalysisAliasCatalog(new AnalysisAliasCatalogData
        {
            CatalogVersion = Version, ContractVersion = contractVersion, CanonIdsVersion = CanonIdsVersion, RunId = input.AnalysisId,
            ProjectId = input.ProjectId, SourceHash = snapshot.SourceHash, Evidence = evidenceEntries, Sources = sourceEntries, Relations = ordered,
        });
    }

    /// <summary>Ricostruisce il catalogo dal payload del WorkOutput: il JSON non decodificabile, incoerente o con hash diverso dal congelato è <c>checkpoint_incompatible</c>.</summary>
    public static AnalysisAliasCatalog Decode(string json, string expectedCatalogHash)
    {
        AnalysisAliasCatalog catalog;
        try
        {
            catalog = new AnalysisAliasCatalog(ReconciliationCodec.Decode<AnalysisAliasCatalogData>(json));
        }
        catch (JsonException e)
        {
            throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, $"Catalogo alias non decodificabile: {e.Message}");
        }

        if (catalog.CatalogHash != expectedCatalogHash)
        {
            throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Il catalogo alias ricostruito ha un hash diverso da quello congelato.");
        }

        return catalog;
    }

    /// <summary>Forma canonica RFC 8785 (payload del WorkOutput); il suo SHA-256 è <see cref="CatalogHash"/>.</summary>
    public string Encode() => _canonical;

    public AliasEvidenceEntry? Evidence(string alias) => _evidence.GetValueOrDefault(alias);

    public AliasRelationEntry? Relation(string alias) => _relations.GetValueOrDefault(alias);

    public AliasSourceEntry? Source(string alias) => _sources.GetValueOrDefault(alias);

    public AliasEvidenceEntry? EvidenceById(Guid evidenceId) => _evidenceById.GetValueOrDefault(evidenceId);

    public AliasSourceEntry? SourceById(Guid sourceId) => _sourceById.GetValueOrDefault(sourceId);

    /// <summary>Fonti originali di un'evidenza nell'ordine gruppo/parte del catalogo.</summary>
    public IEnumerable<AliasSourceEntry> SourcesOf(AliasEvidenceEntry evidence) => evidence.Sources.Select(l => _sources[l.SourceAlias]);

    private static DocumentWorkException Invalid(string message) =>
        new(DocumentWorkException.SourceSnapshotInvalid, "Fonte originale non verificabile per il catalogo alias: " + message);

    private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> alias, char prefix)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        var n = 0;
        foreach (var item in items)
        {
            n++;
            var a = alias(item);
            if (a != prefix + n.ToString(System.Globalization.CultureInfo.InvariantCulture) || !map.TryAdd(a, item))
            {
                throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, $"Catalogo alias incoerente: alias {a} fuori sequenza o duplicato.");
            }
        }

        return map;
    }

    private static Dictionary<Guid, T> Index<T>(IEnumerable<T> items, Func<T, Guid> id)
    {
        var map = new Dictionary<Guid, T>();
        foreach (var item in items)
        {
            if (!map.TryAdd(id(item), item))
            {
                throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Catalogo alias incoerente: identificativo duplicato.");
            }
        }

        return map;
    }
}
