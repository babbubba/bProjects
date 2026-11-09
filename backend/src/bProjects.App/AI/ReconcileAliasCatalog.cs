
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// Catalogo alias PER BATCH `reconcile-alias-catalog-1` (contracts/ai-workflow.md §11, T131): funzione pura del catalogo del run (`alias-catalog-1`, E/S/R), della
// richiesta del motore (voci, nodi, candidato in esame) e degli item già proposti dalle foglie A. Ogni alias è risolvibile solo qui: nessuna normalizzazione,
// nessuna ricerca fuzzy. Gli alias sono etichette di lettura, mai identità persistite.

/// <summary>Item già proposto da una foglia A (chiave locale canonica composta dal server), con le evidenze che cita: serve a numerare i bersagli delle patch.</summary>
public sealed record ReconcileItemRef(string LocalKey, string Text, IReadOnlyList<Guid> EvidenceIds);

public sealed record ReconcileCatalogEntry(string Alias, Guid EvidenceId, string EntryId, string ContentHash, EvidenceOriginKind Origin);

/// <summary>Estremo di gruppo citabile in un batch: nodo ricevuto o estremo Node/Set del candidato in esame. <see cref="Hash"/> è il memberSetHash.</summary>
public sealed record ReconcileCatalogNode(string Alias, CandidateEndpointKind Kind, string Id, string Hash);

public sealed record ReconcileCatalogItem(string Handle, string LocalKey);

public sealed record ReconcileCatalogRelation(string Alias, Guid RelationId);

/// <summary>Dati serializzabili del catalogo di un batch (payload JSONB del WorkOutput).</summary>
public sealed record ReconcileAliasCatalogData
{
    public required string CatalogVersion { get; init; }
    public required string ContractVersion { get; init; }
    public required Guid RunId { get; init; }
    public required string SourceHash { get; init; }
    public required string RunCatalogHash { get; init; }
    public required string BatchId { get; init; }
    public required string Stage { get; init; }
    public required ReconcileView View { get; init; }
    public required int Level { get; init; }
    public required IReadOnlyList<ReconcileCatalogEntry> Entries { get; init; }
    public required IReadOnlyList<ReconcileCatalogNode> Nodes { get; init; }
    public required IReadOnlyList<ReconcileCatalogItem> Items { get; init; }
    public required IReadOnlyList<ReconcileCatalogRelation> Relations { get; init; }
    public required string? FocusCandidateId { get; init; }
}

public sealed class ReconcileAliasCatalog
{
    public const string Version = ReconcileAliasContracts.CatalogVersion;

    private readonly Dictionary<string, ReconcileCatalogEntry> _entries;
    private readonly Dictionary<string, ReconcileCatalogNode> _nodes;
    private readonly Dictionary<string, ReconcileCatalogItem> _items;
    private readonly Dictionary<string, ReconcileCatalogRelation> _relations;
    private readonly string _canonical;

    private ReconcileAliasCatalog(
        ReconcileAliasCatalogData data, ReconcileCandidate? focus, IReadOnlyDictionary<string, string> itemTexts, IReadOnlyDictionary<string, IReadOnlyList<Guid>> itemEvidence)
    {
        Data = data;
        Focus = focus;
        ItemTexts = itemTexts;
        ItemEvidence = itemEvidence;
        _entries = data.Entries.ToDictionary(e => e.Alias, StringComparer.Ordinal);
        _nodes = data.Nodes.ToDictionary(n => n.Alias, StringComparer.Ordinal);
        _items = data.Items.ToDictionary(i => i.Handle, StringComparer.Ordinal);
        _relations = data.Relations.ToDictionary(r => r.Alias, StringComparer.Ordinal);
        _canonical = ReconciliationCodec.Canonical(data);
        CatalogHash = CanonicalJson.HashText(_canonical);
    }

    public ReconcileAliasCatalogData Data { get; }

    /// <summary>Candidato in esame (espansione/confronto), se il batch ne ha uno: i suoi estremi sono risolti in alias del catalogo.</summary>
    public ReconcileCandidate? Focus { get; }

    /// <summary>Testo degli item citabili, per il solo messaggio al modello (non fa parte dell'identità del catalogo).</summary>
    public IReadOnlyDictionary<string, string> ItemTexts { get; }

    /// <summary>Evidenze del batch citate da ciascun item citabile (per il solo messaggio al modello).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Guid>> ItemEvidence { get; }

    /// <summary>SHA-256 della forma canonica RFC 8785 del catalogo del batch.</summary>
    public string CatalogHash { get; }

    /// <summary>Forma canonica del catalogo (payload del WorkOutput).</summary>
    public string Encode() => _canonical;

    public ReconcileCatalogEntry? Entry(string alias) => _entries.GetValueOrDefault(alias);

    public ReconcileCatalogNode? Node(string alias) => _nodes.GetValueOrDefault(alias);

    public ReconcileCatalogItem? Item(string handle) => _items.GetValueOrDefault(handle);

    public ReconcileCatalogRelation? Relation(string alias) => _relations.GetValueOrDefault(alias);

    /// <summary>Alias dell'estremo di un candidato: E del run per le evidenze, N del batch per i nodi e gli insiemi.</summary>
    public string? AliasOf(CandidateEndpoint endpoint) => endpoint.Kind == CandidateEndpointKind.Evidence
        ? Data.Entries.FirstOrDefault(e => e.EvidenceId.ToString("D") == endpoint.Id)?.Alias
        : Data.Nodes.FirstOrDefault(n => n.Kind == endpoint.Kind && n.Id == endpoint.Id)?.Alias;

    /// <summary>Ricostruisce il catalogo di un batch dalla richiesta del motore (voci in ordine di richiesta, nodi N1.., estremi di gruppo del candidato in esame di seguito).</summary>
    public static ReconcileAliasCatalog Build(
        AnalysisAliasCatalog run, ReconcileBatchRequest request, IReadOnlyList<ReconcileItemRef> priorItems, string contractVersion = ReconcileAliasContracts.SchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(priorItems);
        if (contractVersion is not (ReconcileAliasContracts.SchemaVersion or ReconcileAliasContracts.SchemaVersionV2))
        {
            throw new ArgumentException($"Versione di contratto sconosciuta per il catalogo del batch: {contractVersion}.", nameof(contractVersion));
        }

        var entries = new List<ReconcileCatalogEntry>(request.Entries.Count);
        foreach (var entry in request.Entries)
        {
            var known = run.EvidenceById(entry.EvidenceId)
                ?? throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, $"La voce {entry.EntryId} non è nel catalogo alias del run.");
            if (known.EntryId != entry.EntryId || known.ContentHash != entry.ContentHash)
            {
                throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, $"La voce {entry.EntryId} non coincide con il catalogo alias del run.");
            }

            entries.Add(new(known.Alias, entry.EvidenceId, entry.EntryId, entry.ContentHash, known.Origin));
        }

        var nodes = new List<ReconcileCatalogNode>();
        foreach (var node in request.Nodes)
        {
            nodes.Add(new("N" + (nodes.Count + 1), CandidateEndpointKind.Node, node.NodeId, node.MemberSetHash));
        }

        var focus = request.Focus?.FirstOrDefault();
        foreach (var endpoint in (focus is null ? Array.Empty<CandidateEndpoint>() : new[] { focus.Left, focus.Right }).Where(e => e.Kind != CandidateEndpointKind.Evidence))
        {
            if (!nodes.Any(n => n.Kind == endpoint.Kind && n.Id == endpoint.Id))
            {
                nodes.Add(new("N" + (nodes.Count + 1), endpoint.Kind, endpoint.Id, endpoint.Hash));
            }
        }

        var inBatch = entries.Select(e => e.EvidenceId).ToHashSet();
        var candidateStage = request.Batch.Stage is "expand" or "compare";
        var items = new List<ReconcileCatalogItem>();
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var cited = new Dictionary<string, IReadOnlyList<Guid>>(StringComparer.Ordinal);
        for (var i = 0; candidateStage && i < priorItems.Count; i++)
        {
            if (priorItems[i].EvidenceIds.Any(inBatch.Contains))
            {
                var handle = "i" + (i + 1);
                items.Add(new(handle, priorItems[i].LocalKey));
                texts[handle] = priorItems[i].Text;
                cited[handle] = [.. priorItems[i].EvidenceIds.Where(inBatch.Contains)];
            }
        }

        // Relazioni note con ENTRAMBI gli estremi nel batch: sono quelle che il modello può nominare come bersaglio di una patch.
        var relations = run.Data.Relations.Where(r => inBatch.Contains(r.FromEvidenceId) && inBatch.Contains(r.ToEvidenceId)).Select(r => new ReconcileCatalogRelation(r.Alias, r.RelationId)).ToList();

        return new ReconcileAliasCatalog(
            new ReconcileAliasCatalogData
            {
                CatalogVersion = Version, ContractVersion = contractVersion, RunId = run.Data.RunId, SourceHash = run.Data.SourceHash, RunCatalogHash = run.CatalogHash,
                BatchId = request.Batch.BatchId, Stage = request.Batch.Stage, View = request.View, Level = request.Level, Entries = entries, Nodes = nodes, Items = items,
                Relations = relations, FocusCandidateId = focus?.CandidateId,
            },
            focus, texts, cited);
    }
}
