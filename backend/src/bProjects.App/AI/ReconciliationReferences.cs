using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// Risoluzione dei riferimenti di `reconcile-1` contro lo snapshot congelato (T120). Solo membership delle evidenze, versioni, fonti e quote:
// la membership di nodi/insiemi e lo schema dei cataloghi sono estesi in T071/T122 (nessun algoritmo gerarchico qui).

/// <summary>Evidenza dello snapshot: versione, hash del contenuto e fonti persistite (ledger, evidence-1.1.0).</summary>
public sealed record SnapshotEvidence(Guid EvidenceId, Guid EvidenceVersionId, string ContentHash, IReadOnlyList<PersistedSource> Sources);

/// <summary>Relazione nota fra evidenze dello snapshot.</summary>
public sealed record SnapshotRelation(Guid RelationId, Guid FromEvidenceId, Guid ToEvidenceId, EvidenceRelationType Type, EvidenceRelationBasis Basis = EvidenceRelationBasis.Inferred);

/// <summary>Fatti immutabili dello snapshot del progetto (nessun accesso al database): tutto ciò che non è qui è fuori progetto o fuori snapshot.</summary>
public sealed record ReconciliationSnapshot(Guid ProjectId, string SourceHash, IReadOnlyList<SnapshotEvidence> Evidence, IReadOnlyList<SnapshotRelation> Relations)
{
    public static ReconciliationSnapshot Empty(Guid projectId, string sourceHash) => new(projectId, sourceHash, [], []);
}

/// <summary>Cataloghi gerarchici di un run: nodi, pagine e insiemi di routing (membership esatta, mai un secondo registro Evidence).</summary>
public sealed record ReconciliationCatalog(IReadOnlyList<CatalogNode> Nodes, IReadOnlyList<CatalogPage> Pages, IReadOnlyList<CatalogSet> Sets);

public static class ReconciliationReferences
{
    /// <summary>
    /// Membership dei cataloghi contro lo snapshot: pagine contigue e concatenate, foglie con membership esplicita, genitori = unione disgiunta dei figli,
    /// cardinalità e hash, insiemi = unione esatta dei nodi, riferimenti alle pagine. <paramref name="complete"/> richiede che ogni evidenza dello snapshot
    /// compaia una volta in ogni vista con nodi (foglie).
    /// </summary>
    public static IReadOnlyList<string> CheckCatalog(ReconciliationCatalog catalog, ReconciliationSnapshot snapshot, string inputHash, bool complete = false)
    {
        var v = new List<string>();
        var evidence = snapshot.Evidence.ToDictionary(e => e.EvidenceId);
        var relations = snapshot.Relations.ToDictionary(r => r.RelationId);
        var nodes = new Dictionary<string, CatalogNode>(StringComparer.Ordinal);
        foreach (var node in catalog.Nodes)
        {
            if (!nodes.TryAdd(node.NodeId, node))
            {
                v.Add($"nodes: {node.NodeId} duplicato.");
            }

            v.AddRange(ReconciliationCatalogRules.CheckNode(node, inputHash).Select(m => $"node {node.NodeId}: {m}"));
        }

        foreach (var group in catalog.Nodes.GroupBy(n => (n.View, n.Level, n.Ordinal)).Where(g => g.Count() > 1))
        {
            v.Add($"nodes: ordinal {group.Key.Ordinal} duplicato in {group.Key.View}/{group.Key.Level}.");
        }

        // Pagine: oggetto, catena per (nodo,kind) e riferimenti; l'hash della pagina è la sua identità nei nodi.
        var chains = new Dictionary<(string Node, CatalogPageKind Kind), List<CatalogPage>>();
        foreach (var page in catalog.Pages)
        {
            if (!nodes.ContainsKey(page.NodeId))
            {
                v.Add($"page {page.PageId}: nodo {page.NodeId} sconosciuto.");
                continue;
            }

            v.AddRange(ReconciliationCatalogRules.CheckPage(page).Select(m => $"page {page.PageId}: {m}"));
            if (!chains.TryGetValue((page.NodeId, page.Kind), out var list))
            {
                chains[(page.NodeId, page.Kind)] = list = [];
            }

            list.Add(page);
        }

        var ordered = new Dictionary<(string Node, CatalogPageKind Kind), List<CatalogPage>>();
        foreach (var (key, pages) in chains)
        {
            v.AddRange(ReconciliationCatalogRules.CheckPageChain(pages).Select(m => $"pages {key.Node}/{key.Kind}: {m}"));
            ordered[key] = [.. pages.OrderBy(p => p.Ordinal)];
        }

        var memberPageHashes = ordered.Where(c => c.Key.Kind == CatalogPageKind.Members).SelectMany(c => c.Value).Select(ReconciliationIds.PageHash).ToHashSet(StringComparer.Ordinal);
        List<string> PageHashes(string nodeId, CatalogPageKind kind) => ordered.TryGetValue((nodeId, kind), out var p) ? [.. p.Select(ReconciliationIds.PageHash)] : [];

        // Membership: foglie dalle pagine Members, genitori come unione disgiunta dei figli (livelli crescenti).
        var members = new Dictionary<string, Dictionary<Guid, MemberPageEntry>>(StringComparer.Ordinal);
        foreach (var node in catalog.Nodes.OrderBy(n => n.Level))
        {
            if (members.ContainsKey(node.NodeId))
            {
                continue;
            }

            var own = new Dictionary<Guid, MemberPageEntry>();
            if (node.Level == 0)
            {
                if (!ordered.TryGetValue((node.NodeId, CatalogPageKind.Members), out var memberPages))
                {
                    v.Add($"node {node.NodeId}: una foglia richiede la membership esplicita in pagine Members.");
                }
                else
                {
                    foreach (var entry in memberPages.SelectMany(p => p.Entries).OfType<MemberPageEntry>())
                    {
                        if (!own.TryAdd(entry.EvidenceId, entry))
                        {
                            v.Add($"node {node.NodeId}: membro {entry.EvidenceId} ripetuto.");
                        }
                        else if (!evidence.TryGetValue(entry.EvidenceId, out var e))
                        {
                            v.Add($"node {node.NodeId}: membro {entry.EvidenceId} fuori snapshot.");
                        }
                        else if (e.EvidenceVersionId != entry.EvidenceVersionId || e.ContentHash != entry.ContentHash)
                        {
                            v.Add($"node {node.NodeId}: membro {entry.EvidenceId} con versione o contentHash diversi dallo snapshot.");
                        }
                    }
                }

                var leafKeys = PageHashes(node.NodeId, CatalogPageKind.Keys);
                var leafRelations = PageHashes(node.NodeId, CatalogPageKind.Relations);
                if (!node.KeyPageHashes.SequenceEqual(leafKeys, StringComparer.Ordinal) || !node.RelationPageHashes.SequenceEqual(leafRelations, StringComparer.Ordinal))
                {
                    v.Add($"node {node.NodeId}: keyPageHashes/relationPageHashes diversi dalle pagine concatenate del nodo.");
                }

                foreach (var entry in ordered.TryGetValue((node.NodeId, CatalogPageKind.Keys), out var kp) ? kp.SelectMany(p => p.Entries).OfType<KeyPageEntry>() : [])
                {
                    foreach (var id in entry.MemberIds.Where(id => !own.ContainsKey(id)))
                    {
                        v.Add($"node {node.NodeId}: la chiave cita {id}, che non è un membro della foglia.");
                    }

                    foreach (var reference in entry.MemberPageRefs.Where(r => !memberPageHashes.Contains(r)))
                    {
                        v.Add($"node {node.NodeId}: la chiave cita la pagina Members {reference}, inesistente.");
                    }
                }

                foreach (var entry in ordered.TryGetValue((node.NodeId, CatalogPageKind.Relations), out var rp) ? rp.SelectMany(p => p.Entries).OfType<RelationPageEntry>() : [])
                {
                    if (!relations.TryGetValue(entry.RelationId, out var known) || known.FromEvidenceId != entry.FromEvidenceId || known.ToEvidenceId != entry.ToEvidenceId
                        || known.Type != entry.Type || known.Basis != entry.Basis)
                    {
                        v.Add($"node {node.NodeId}: la relazione {entry.RelationId} non coincide con una relazione nota dello snapshot.");
                    }
                }
            }
            else
            {
                foreach (var childId in node.ChildNodeIds)
                {
                    if (!nodes.TryGetValue(childId, out var child) || child.Level != node.Level - 1 || (node.View != ReconcileView.Cross && child.View != node.View) || !members.TryGetValue(childId, out var childMembers))
                    {
                        v.Add($"node {node.NodeId}: figlio {childId} inesistente, di vista o di livello non coerenti.");
                        continue;
                    }

                    foreach (var (id, entry) in childMembers)
                    {
                        if (!own.TryAdd(id, entry))
                        {
                            v.Add($"node {node.NodeId}: membro {id} presente in più figli (unione non disgiunta).");
                        }
                    }
                }

                var childNodes = node.ChildNodeIds.Where(nodes.ContainsKey).Select(c => nodes[c]).ToList();
                if (!node.KeyPageHashes.SequenceEqual(childNodes.SelectMany(c => c.KeyPageHashes), StringComparer.Ordinal)
                    || !node.RelationPageHashes.SequenceEqual(childNodes.SelectMany(c => c.RelationPageHashes), StringComparer.Ordinal))
                {
                    v.Add($"node {node.NodeId}: il genitore deve citare esattamente le pagine keys/relations dei figli.");
                }
            }

            members[node.NodeId] = own;
            if (own.Count != node.MemberCount || ReconciliationIds.MemberSetHash(own.Values) != node.MemberSetHash)
            {
                v.Add($"node {node.NodeId}: memberCount o memberSetHash diversi dalla membership.");
            }
        }

        // Unione disgiunta per vista e livello; copertura completa delle foglie solo su richiesta.
        foreach (var level in catalog.Nodes.GroupBy(n => (n.View, n.Level)))
        {
            var seen = new HashSet<Guid>();
            foreach (var id in level.Where(n => members.ContainsKey(n.NodeId)).SelectMany(n => members[n.NodeId].Keys).Where(id => !seen.Add(id)).Distinct())
            {
                v.Add($"membership {level.Key.View}/{level.Key.Level}: {id} compare in più nodi.");
            }

            if (complete && level.Key.Level == 0)
            {
                foreach (var e in snapshot.Evidence.Where(e => !seen.Contains(e.EvidenceId)))
                {
                    v.Add($"membership {level.Key.View}: l'evidenza {e.EvidenceId} non compare nelle foglie.");
                }
            }
        }

        // Insiemi di routing: unione esatta dei nodi indicati, nessun membro eliminato per overlap.
        foreach (var set in catalog.Sets)
        {
            v.AddRange(ReconciliationCatalogRules.CheckSet(set).Select(m => $"set {set.SetId}: {m}"));
            var union = new Dictionary<Guid, MemberPageEntry>();
            foreach (var nodeId in set.MemberNodeIds)
            {
                if (!nodes.TryGetValue(nodeId, out var node) || node.View != set.View || !members.TryGetValue(nodeId, out var nodeMembers))
                {
                    v.Add($"set {set.SetId}: nodo {nodeId} inesistente o di un'altra vista.");
                    continue;
                }

                foreach (var (id, entry) in nodeMembers.Where(m => !union.TryAdd(m.Key, m.Value)))
                {
                    v.Add($"set {set.SetId}: il membro {id} compare in più nodi (overlap).");
                }
            }

            if (union.Count != set.MemberCount || ReconciliationIds.MemberSetHash(union.Values) != set.MemberSetHash)
            {
                v.Add($"set {set.SetId}: memberCount o memberSetHash diversi dall'unione dei nodi.");
            }
        }

        return [.. v.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Come la variante con insieme di ID noti, ma gli endpoint Node/Set sono risolti nel catalogo e il loro hash è il memberSetHash.</summary>
    public static IReadOnlyList<string> CheckResult(ReconcileResult result, ReconciliationSnapshot snapshot, ReconciliationCatalog catalog)
    {
        var nodes = catalog.Nodes.ToDictionary(n => n.NodeId, n => n.MemberSetHash, StringComparer.Ordinal);
        var sets = catalog.Sets.ToDictionary(s => s.SetId, s => s.MemberSetHash, StringComparer.Ordinal);
        var v = new List<string>(CheckResult(result, snapshot, nodes.Keys.Concat(sets.Keys).ToHashSet(StringComparer.Ordinal)));
        for (var i = 0; i < result.Candidates.Count; i++)
        {
            foreach (var endpoint in new[] { result.Candidates[i].Left, result.Candidates[i].Right }.Where(e => e.Kind != CandidateEndpointKind.Evidence))
            {
                var group = endpoint.Kind == CandidateEndpointKind.Node ? nodes : sets;
                if (!group.TryGetValue(endpoint.Id, out var hash))
                {
                    v.Add($"candidates[{i}]: endpoint {endpoint.Kind} {endpoint.Id} non è del tipo dichiarato nel catalogo.");
                }
                else if (hash != endpoint.Hash)
                {
                    v.Add($"candidates[{i}]: hash dell'endpoint {endpoint.Id} diverso dal memberSetHash del catalogo.");
                }
            }
        }

        return v;
    }


    /// <summary>Gli ID di Node/Set sono accettati solo se il consumer gerarchico li fornisce (T071); nella singola chiamata l'insieme è vuoto.</summary>
    public static IReadOnlyList<string> CheckResult(ReconcileResult result, ReconciliationSnapshot snapshot, IReadOnlySet<string>? knownGroupIds = null)
    {
        var index = new Index(snapshot);
        var v = new List<string>();
        foreach (var entryId in result.VisitedEntryIds.Where(id => !index.EntryIds.Contains(id)))
        {
            v.Add($"visitedEntryIds: {entryId} non è una voce dello snapshot.");
        }

        if (result.VisitedNodeIds.Count > 0)
        {
            foreach (var nodeId in result.VisitedNodeIds.Where(id => knownGroupIds is null || !knownGroupIds.Contains(id)))
            {
                v.Add($"visitedNodeIds: {nodeId} non è un nodo noto.");
            }
        }

        for (var i = 0; i < result.Candidates.Count; i++)
        {
            foreach (var endpoint in new[] { result.Candidates[i].Left, result.Candidates[i].Right })
            {
                CheckEndpoint(v, $"candidates[{i}]", endpoint, index, knownGroupIds);
            }
        }

        for (var i = 0; i < result.ItemProposals.Count; i++)
        {
            CheckSet(v, $"itemProposals[{i}]", result.ItemProposals[i].EvidenceIds, result.ItemProposals[i].SourceIds, index);
        }

        for (var i = 0; i < result.Patches.Count; i++)
        {
            v.AddRange(CheckPatch(result.Patches[i], snapshot, index).Select(m => $"patches[{i}].{m}"));
        }

        return v;
    }

    public static IReadOnlyList<string> CheckPatch(Patch patch, ReconciliationSnapshot snapshot) => CheckPatch(patch, snapshot, new Index(snapshot));

    private static List<string> CheckPatch(Patch patch, ReconciliationSnapshot snapshot, Index index)
    {
        var v = new List<string>();
        CheckSet(v, "ids", patch.EvidenceIds, patch.SourceIds, index);
        if (patch.Value is AddRelationValue relation)
        {
            foreach (var id in new[] { relation.FromEvidenceId, relation.ToEvidenceId }.Where(id => !index.Evidence.ContainsKey(id)))
            {
                v.Add($"value: endpoint {id} fuori snapshot.");
            }
        }

        if (patch.Target.Kind == PatchTargetKind.Relation
            && (!Guid.TryParseExact(patch.Target.Id, "D", out var relationId) || !snapshot.Relations.Any(r => r.RelationId == relationId)))
        {
            v.Add($"target: relazione {patch.Target.Id} fuori snapshot.");
        }

        return v;
    }

    /// <summary>Output della singola chiamata: tutti i result, la mappa item→EvidenceId e le fonti di analysis risolte nello snapshot (quote comprese).</summary>
    public static IReadOnlyList<string> CheckSingleCall(SingleCallOutput output, ReconciliationSnapshot snapshot)
    {
        var index = new Index(snapshot);
        var v = new List<string>();
        for (var i = 0; i < output.Results.Count; i++)
        {
            v.AddRange(CheckResult(output.Results[i], snapshot).Select(m => $"results[{i}].{m}"));
        }

        for (var i = 0; i < output.EvidenceMap.Count; i++)
        {
            foreach (var id in output.EvidenceMap[i].EvidenceIds.Where(id => !index.Evidence.ContainsKey(id)))
            {
                v.Add($"evidenceMap[{i}]: evidenza {id} fuori snapshot.");
            }
        }

        if (output.Analysis.ProjectId != snapshot.ProjectId)
        {
            v.Add("analysis.projectId: diverso dal progetto dello snapshot.");
        }

        foreach (var source in output.Analysis.Sources)
        {
            if (source.Kind == DocumentAnalysisSourceKind.PmRevision)
            {
                continue;
            }

            if (!index.Sources.TryGetValue(source.Id, out var persisted))
            {
                v.Add($"analysis.sources: {source.Id} fuori snapshot.");
            }
            else if (source.Kind == DocumentAnalysisSourceKind.DocumentPassage
                && (persisted.Kind != EvidenceSourceKind.DocumentPassage || persisted.DocumentId != source.DocumentId || persisted.DocumentVersionId != source.DocumentVersionId
                    || persisted.PreparationId != source.PreparationId || persisted.ArtifactId != source.ArtifactId || persisted.BlockId != source.BlockId
                    || persisted.Start != source.Start || persisted.End != source.End || !string.Equals(persisted.Quote, source.Quote, StringComparison.Ordinal)))
            {
                v.Add($"analysis.sources: {source.Id} non coincide con blocco, range e quote dello snapshot.");
            }
            else if (source.Kind == DocumentAnalysisSourceKind.Message && (persisted.Kind != EvidenceSourceKind.Message || persisted.MessageId != source.MessageId))
            {
                v.Add($"analysis.sources: {source.Id} non coincide con il messaggio dello snapshot.");
            }
        }

        return v;
    }

    private static void CheckEndpoint(List<string> v, string path, CandidateEndpoint endpoint, Index index, IReadOnlySet<string>? knownGroupIds)
    {
        if (endpoint.Kind == CandidateEndpointKind.Evidence)
        {
            if (!Guid.TryParseExact(endpoint.Id, "D", out var id) || !index.Evidence.TryGetValue(id, out var evidence))
            {
                v.Add($"{path}: endpoint Evidence {endpoint.Id} fuori snapshot.");
            }
            else if (evidence.ContentHash != endpoint.Hash)
            {
                v.Add($"{path}: hash dell'endpoint {endpoint.Id} diverso dal contentHash dello snapshot.");
            }
        }
        else if (knownGroupIds is null || !knownGroupIds.Contains(endpoint.Id))
        {
            v.Add($"{path}: endpoint {endpoint.Kind} {endpoint.Id} non risolvibile (cataloghi gerarchici assenti).");
        }
    }

    /// <summary>EvidenceId nello snapshot; ogni sourceId esiste e appartiene a una delle evidenze citate.</summary>
    private static void CheckSet(List<string> v, string path, IReadOnlyList<Guid> evidenceIds, IReadOnlyList<Guid> sourceIds, Index index)
    {
        foreach (var id in evidenceIds.Where(id => !index.Evidence.ContainsKey(id)))
        {
            v.Add($"{path}.evidenceIds: {id} fuori snapshot.");
        }

        foreach (var sourceId in sourceIds)
        {
            if (!index.SourceOwners.TryGetValue(sourceId, out var owner))
            {
                v.Add($"{path}.sourceIds: {sourceId} fuori snapshot.");
            }
            else if (!evidenceIds.Any(owner.Contains))
            {
                v.Add($"{path}.sourceIds: {sourceId} non appartiene alle evidenze citate.");
            }
        }
    }

    private sealed class Index
    {
        public Index(ReconciliationSnapshot snapshot)
        {
            Evidence = snapshot.Evidence.ToDictionary(e => e.EvidenceId);
            EntryIds = snapshot.Evidence.Select(e => ReconciliationIds.EntryId(snapshot.SourceHash, e.EvidenceId, e.ContentHash)).ToHashSet(StringComparer.Ordinal);
            // Due evidenze possono citare lo stesso passaggio (stesso sourceId): la fonte e unica, i proprietari sono un insieme.
            Sources = snapshot.Evidence.SelectMany(e => e.Sources).GroupBy(s => s.SourceId).ToDictionary(g => g.Key, g => g.First());
            SourceOwners = snapshot.Evidence.SelectMany(e => e.Sources.Select(s => (s.SourceId, e.EvidenceId))).GroupBy(t => t.SourceId).ToDictionary(g => g.Key, g => g.Select(t => t.EvidenceId).ToHashSet());
        }

        public Dictionary<Guid, SnapshotEvidence> Evidence { get; }
        public HashSet<string> EntryIds { get; }
        public Dictionary<Guid, PersistedSource> Sources { get; }
        public Dictionary<Guid, HashSet<Guid>> SourceOwners { get; }
    }
}
