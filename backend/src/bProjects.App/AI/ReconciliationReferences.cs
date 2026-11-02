using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// Risoluzione dei riferimenti di `reconcile-1` contro lo snapshot congelato (T120). Solo membership delle evidenze, versioni, fonti e quote:
// la membership di nodi/insiemi e lo schema dei cataloghi sono estesi in T071/T122 (nessun algoritmo gerarchico qui).

/// <summary>Evidenza dello snapshot: versione, hash del contenuto e fonti persistite (ledger, evidence-1.1.0).</summary>
public sealed record SnapshotEvidence(Guid EvidenceId, Guid EvidenceVersionId, string ContentHash, IReadOnlyList<PersistedSource> Sources);

/// <summary>Relazione nota fra evidenze dello snapshot.</summary>
public sealed record SnapshotRelation(Guid RelationId, Guid FromEvidenceId, Guid ToEvidenceId, EvidenceRelationType Type);

/// <summary>Fatti immutabili dello snapshot del progetto (nessun accesso al database): tutto ciò che non è qui è fuori progetto o fuori snapshot.</summary>
public sealed record ReconciliationSnapshot(Guid ProjectId, string SourceHash, IReadOnlyList<SnapshotEvidence> Evidence, IReadOnlyList<SnapshotRelation> Relations)
{
    public static ReconciliationSnapshot Empty(Guid projectId, string sourceHash) => new(projectId, sourceHash, [], []);
}

public static class ReconciliationReferences
{
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
            else if (!evidenceIds.Contains(owner))
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
            Sources = snapshot.Evidence.SelectMany(e => e.Sources).ToDictionary(s => s.SourceId);
            SourceOwners = snapshot.Evidence.SelectMany(e => e.Sources.Select(s => (s.SourceId, e.EvidenceId))).ToDictionary(t => t.SourceId, t => t.EvidenceId);
        }

        public Dictionary<Guid, SnapshotEvidence> Evidence { get; }
        public HashSet<string> EntryIds { get; }
        public Dictionary<Guid, PersistedSource> Sources { get; }
        public Dictionary<Guid, Guid> SourceOwners { get; }
    }
}
