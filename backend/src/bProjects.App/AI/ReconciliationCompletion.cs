namespace bProjects.App.AI;

// Completezza finale (contracts/ai-workflow.md §4.5, T121): calcolata dal server su visite, obbligazioni, patch e mappa delle evidenze,
// mai da una dichiarazione dell'AI. Un batch valido con residui resta riutilizzabile ma non completa: i residui giustificano additional_work.

public sealed record CompletionGap(string Code, string Detail);

/// <summary>Coverage report: insiemi attesi/visitati/mancanti e hash, non un booleano del modello.</summary>
public sealed record CompletionReport(
    bool IsComplete, IReadOnlyList<CompletionGap> Gaps, IReadOnlyList<string> ExpectedEntryIds, IReadOnlyList<string> VisitedEntryIdsA,
    IReadOnlyList<string> VisitedEntryIdsB, IReadOnlyList<string> MissingEntryIds, string CoverageHash)
{
    /// <summary>Vero se restano obbligazioni reali che giustificano un passo successivo (additional_work, B1).</summary>
    public bool AdditionalWorkJustified => !IsComplete;

    /// <summary>La completezza è strutturale: un gold semanticamente perso resta un FAIL separato del gate di qualità.</summary>
    public bool SemanticQualityCertified => false;
}

public static class ReconciliationCompletion
{
    public static CompletionReport EvaluateSingleCall(ReconciliationSnapshot snapshot, SingleCallOutput output, PatchComposition composition)
    {
        var gaps = new List<CompletionGap>();
        var entries = snapshot.Evidence.Select(e => (e.EvidenceId, EntryId: ReconciliationIds.EntryId(snapshot.SourceHash, e.EvidenceId, e.ContentHash))).ToList();
        var expected = entries.Select(e => e.EntryId).Order(StringComparer.Ordinal).ToList();

        IReadOnlyList<string> Visited(ReconcileView view) =>
            [.. output.Results.Where(r => r.View == view).SelectMany(r => r.VisitedEntryIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        var visitedA = Visited(ReconcileView.A);
        var visitedB = Visited(ReconcileView.B);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (view, visited) in new[] { ("A", visitedA), ("B", visitedB) })
        {
            foreach (var id in expected.Where(id => !visited.Contains(id, StringComparer.Ordinal)))
            {
                missing.Add(id);
                gaps.Add(new("entry_not_visited", $"vista {view}: voce {id} non visitata."));
            }
        }

        var candidates = output.Results.SelectMany(r => r.Candidates).GroupBy(c => c.CandidateId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var dispositions = output.Results.SelectMany(r => r.Dispositions).GroupBy(d => d.CandidateId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var terminal = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var id in candidates.Keys.Order(StringComparer.Ordinal))
        {
            if (!dispositions.ContainsKey(id))
            {
                gaps.Add(new("candidate_without_disposition", $"candidato {id} senza disposition."));
            }
            else if (!IsTerminal(id, dispositions, terminal, []))
            {
                gaps.Add(new("candidate_not_terminal", $"candidato {id} non terminale (Pending, NotProcessed o figli non terminali)."));
            }
        }

        // Ogni relazione nota genera un'obbligazione diretta sui suoi due EvidenceId: serve un candidato Evidence sulla stessa coppia, terminale.
        foreach (var relation in snapshot.Relations.OrderBy(r => r.RelationId))
        {
            var pair = new[] { relation.FromEvidenceId.ToString("D"), relation.ToEvidenceId.ToString("D") }.Order(StringComparer.Ordinal).ToArray();
            var resolved = candidates.Values.Any(c => c.Left.Kind == CandidateEndpointKind.Evidence && c.Right.Kind == CandidateEndpointKind.Evidence
                && new[] { c.Left.Id, c.Right.Id }.Order(StringComparer.Ordinal).SequenceEqual(pair) && dispositions.ContainsKey(c.CandidateId)
                && IsTerminal(c.CandidateId, dispositions, terminal, []));
            if (!resolved)
            {
                gaps.Add(new("known_relation_unresolved", $"relazione nota {relation.RelationId:D} ({pair[0]}, {pair[1]}) senza confronto terminale."));
            }
        }

        foreach (var patchId in composition.BlockingPatchIds)
        {
            gaps.Add(new("patch_blocking", $"patch {patchId} Invalid/Stale necessaria o in conflitto d'integrità."));
        }

        var mapped = output.EvidenceMap.SelectMany(e => e.EvidenceIds).ToHashSet();
        foreach (var evidence in snapshot.Evidence.OrderBy(e => e.EvidenceId.ToString("D"), StringComparer.Ordinal).Where(e => !mapped.Contains(e.EvidenceId)))
        {
            gaps.Add(new("evidence_not_mapped", $"evidenza {evidence.EvidenceId:D} non mappata su alcun item."));
        }

        var hash = CanonicalJson.HashText(ReconciliationCodec.Canonical(new { expected, visitedA, visitedB, missing = missing.ToList(), gaps }));
        return new(gaps.Count == 0, gaps, expected, visitedA, visitedB, [.. missing], hash);
    }

    /// <summary>Compared/Open sono terminali; Expanded solo con figli presenti e tutti terminali; Pending e NotProcessed no. Cicli = non terminale.</summary>
    private static bool IsTerminal(string id, Dictionary<string, CandidateDisposition> dispositions, Dictionary<string, bool> memo, HashSet<string> path)
    {
        if (memo.TryGetValue(id, out var known))
        {
            return known;
        }

        if (!dispositions.TryGetValue(id, out var d) || !path.Add(id))
        {
            return false;
        }

        var result = d.State switch
        {
            CandidateState.Compared or CandidateState.Open => true,
            CandidateState.Expanded => d.ChildCandidateIds.Count > 0 && d.ChildCandidateIds.All(c => IsTerminal(c, dispositions, memo, path)),
            _ => false,
        };
        path.Remove(id);
        memo[id] = result;
        return result;
    }
}
