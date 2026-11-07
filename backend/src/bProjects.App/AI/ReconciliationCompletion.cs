using bProjects.App.Analysis;

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

/// <summary>Tutto ciò che il server ha prodotto nel percorso gerarchico e su cui calcola la completezza (T077): snapshot, directory, gerarchia, candidati, round finale e patch.</summary>
public sealed record HierarchicalRun(
    ReconciliationSnapshot Snapshot, ReconciliationDirectoryResult Directory, HierarchyResult Hierarchy, CandidateRun Candidates, FinalPassResult? FinalPass,
    HierarchicalPatchComposition Patches);

/// <summary>ReconciliationCoverage server (AI §4.5): il <see cref="Report"/> comune più gli insiemi dei nodi, delle pagine, dei candidati, delle patch, della frontiera e della mappa delle evidenze.</summary>
public sealed record ReconciliationCoverage(
    CompletionReport Report, IReadOnlyList<string> ExpectedNodeIds, IReadOnlyList<string> VisitedNodeIds, IReadOnlyList<string> MissingNodeIds, IReadOnlyList<string> PageHashes,
    IReadOnlyList<string> UnresolvedCandidateIds, IReadOnlyList<string> BlockingPatchIds, IReadOnlyList<string> FrontierIds, IReadOnlyList<Guid> MappedEvidenceIds,
    IReadOnlyList<Guid> UnmappedEvidenceIds)
{
    public bool IsComplete => Report.IsComplete;
}

/// <summary>
/// Esito delle valutazioni dei Gap (`gap-assessment-1`, §13.2) come input della completezza v5 (T136): quali evidenze dello snapshot sono Gap, per quali esiste un record valido
/// e l'hash della loro forma canonica. Calcolato dal compositore, mai dichiarato dal modello. Con il contesto la completezza v5 (a) non pretende che un Gap sia mappato su un
/// item (lo pretende una valutazione), (b) segnala <c>gap_not_assessed</c> per ogni Gap senza record, anche se mappato, (c) include le valutazioni nel coverageHash.
/// Senza contesto (v2/v3/v4) il comportamento e l'hash storici restano identici.
/// </summary>
public sealed record GapAssessmentContext(IReadOnlySet<Guid> GapEvidenceIds, IReadOnlySet<Guid> AssessedGapEvidenceIds, string AssessmentsHash);

public static class ReconciliationCompletion
{
    public static CompletionReport EvaluateSingleCall(ReconciliationSnapshot snapshot, SingleCallOutput output, PatchComposition composition, GapAssessmentContext? gapAssessments = null)
    {
        var gaps = new List<CompletionGap>();
        var entries = snapshot.Evidence.Select(e => (e.EvidenceId, EntryId: ReconciliationIds.EntryId(snapshot.SourceHash, e.EvidenceId, e.ContentHash))).ToList();
        var expected = entries.Select(e => e.EntryId).Order(StringComparer.Ordinal).ToList();

        IReadOnlyList<string> Visited(ReconcileView view) =>
            [.. output.Results.Where(r => r.View == view).SelectMany(r => r.VisitedEntryIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        var visitedA = Visited(ReconcileView.A);
        var visitedB = Visited(ReconcileView.B);
        var missing = MissingVisits(expected, visitedA, visitedB, gaps);

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
        // v5: un Gap non si mappa su un item per essere rendicontato (lo fa la valutazione); ogni altra evidenza resta mappata come prima.
        foreach (var evidence in snapshot.Evidence.OrderBy(e => e.EvidenceId.ToString("D"), StringComparer.Ordinal)
            .Where(e => !mapped.Contains(e.EvidenceId) && gapAssessments?.GapEvidenceIds.Contains(e.EvidenceId) != true))
        {
            gaps.Add(new("evidence_not_mapped", $"evidenza {evidence.EvidenceId:D} non mappata su alcun item."));
        }

        if (gapAssessments is not null)
        {
            foreach (var gap in gapAssessments.GapEvidenceIds.Except(gapAssessments.AssessedGapEvidenceIds).OrderBy(g => g.ToString("D"), StringComparer.Ordinal))
            {
                gaps.Add(new("gap_not_assessed", $"evidenza {gap:D}: Gap senza valutazione finale (gap-assessment-1)."));
            }
        }

        var hash = CoverageHash(expected, visitedA, visitedB, missing.ToList(), gaps, gapAssessments);
        return new(gaps.Count == 0, gaps, expected, visitedA, visitedB, [.. missing], hash);
    }

    /// <summary>
    /// coverageHash del percorso a chiamata singola: insiemi attesi/visitati/mancanti e obbligazioni residue; con le valutazioni dei Gap (v5) anche l'insieme dei Gap attesi e valutati
    /// e l'hash delle valutazioni. Senza contesto la forma è quella storica (hash invariati per v2/v3/v4).
    /// </summary>
    internal static string CoverageHash(
        IReadOnlyList<string> expected, IReadOnlyList<string> visitedA, IReadOnlyList<string> visitedB, IReadOnlyList<string> missing, IReadOnlyList<CompletionGap> gaps,
        GapAssessmentContext? gapAssessments) =>
        gapAssessments is null
            ? CanonicalJson.HashText(ReconciliationCodec.Canonical(new { expected, visitedA, visitedB, missing, gaps }))
            : CanonicalJson.HashText(ReconciliationCodec.Canonical(new
            {
                expected, visitedA, visitedB, missing, gaps,
                gapAssessments = new
                {
                    version = GapAssessmentRules.Version, hash = gapAssessments.AssessmentsHash,
                    expectedGaps = gapAssessments.GapEvidenceIds.Select(g => g.ToString("D")).Order(StringComparer.Ordinal).ToList(),
                    assessedGaps = gapAssessments.AssessedGapEvidenceIds.Select(g => g.ToString("D")).Order(StringComparer.Ordinal).ToList(),
                },
            }));

    /// <summary>Voci attese non visitate nella vista A o B (stesso controllo per singola chiamata e gerarchico).</summary>
    private static SortedSet<string> MissingVisits(IReadOnlyList<string> expected, IReadOnlyList<string> visitedA, IReadOnlyList<string> visitedB, List<CompletionGap> gaps)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (view, visited) in new[] { ("A", visitedA), ("B", visitedB) })
        {
            foreach (var id in expected.Where(id => !visited.Contains(id, StringComparer.Ordinal)))
            {
                missing.Add(id);
                gaps.Add(new("entry_not_visited", $"vista {view}: voce {id} non visitata."));
            }
        }

        return missing;
    }

    /// <summary>
    /// Completezza del percorso gerarchico (T077, AI §4.5), calcolata dal server sugli insiemi attesi/visitati e mai da una dichiarazione dell'AI: voci A/B, nodi,
    /// pagine e livelli (catalogo), radici e Cross, relazioni note, candidati, round finale e frontiera, patch e mappa di ogni EvidenceId. Un Open fondato su
    /// originali è ammesso solo se il dubbio è rappresentato nella revisione; Pending/NotProcessed/Stale/Invalid necessari impediscono Completed. Usa gli stessi
    /// gap e lo stesso report di <see cref="EvaluateSingleCall"/>: nessun percorso di completamento alternativo.
    /// </summary>
    public static ReconciliationCoverage EvaluateHierarchical(HierarchicalRun run, DocumentAnalysisContent analysis, IReadOnlyList<EvidenceMapEntry> evidenceMap)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(evidenceMap);
        var gaps = new List<CompletionGap>();
        var snapshot = run.Snapshot;
        var hierarchy = run.Hierarchy;
        var expected = snapshot.Evidence.Select(e => ReconciliationIds.EntryId(snapshot.SourceHash, e.EvidenceId, e.ContentHash)).Order(StringComparer.Ordinal).ToList();

        IReadOnlyList<string> Visited(ReconcileView view) => [.. hierarchy.Outcomes.Where(o => o.Batch.Stage == "leaf" && o.Result.View == view)
            .SelectMany(o => o.Result.VisitedEntryIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        var visitedA = Visited(ReconcileView.A);
        var visitedB = Visited(ReconcileView.B);
        var missing = MissingVisits(expected, visitedA, visitedB, gaps);

        // Nodi: ogni radice e ogni figlio di un genitore non inoltrato deve essere stato visitato da un batch di riduzione o di Cross (un nodo inoltrato non ha AI).
        var carried = hierarchy.Carries.ToHashSet(StringComparer.Ordinal);
        var expectedNodes = hierarchy.Nodes.Where(n => n.Level > 0 && !carried.Contains(n.NodeId)).SelectMany(n => n.ChildNodeIds).Concat(hierarchy.Roots.Values)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var visitedNodes = hierarchy.Outcomes.Where(o => o.Batch.Stage is "reduce" or "cross").SelectMany(o => o.Result.VisitedNodeIds).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
        var missingNodes = expectedNodes.Where(id => !visitedNodes.Contains(id, StringComparer.Ordinal)).ToList();
        gaps.AddRange(missingNodes.Select(id => new CompletionGap("node_not_visited", $"nodo {id} non visitato da alcun batch di riduzione o Cross.")));
        foreach (var view in run.Directory.Leaves.Select(l => l.View).Distinct().Order())
        {
            if (!hierarchy.Roots.ContainsKey(view))
            {
                gaps.Add(new("root_missing", $"vista {view}: nessuna radice."));
            }
        }

        if (hierarchy.Roots.Count == 2 && !hierarchy.Outcomes.Any(o => o.Batch.Stage == "cross"))
        {
            gaps.Add(new("cross_not_visited", "passaggio Cross fra le radici A e B non eseguito."));
        }

        // Pagine e livelli: concatenazione, membership disgiunta e cardinalità verificate sul catalogo (nessun buco).
        var pageHashes = hierarchy.Pages.Select(ReconciliationIds.PageHash).Order(StringComparer.Ordinal).ToList();
        foreach (var violation in ReconciliationReferences.CheckCatalog(new ReconciliationCatalog(hierarchy.Nodes, hierarchy.Pages, run.Directory.Sets), snapshot, run.Directory.InputHash, complete: true).Take(8))
        {
            gaps.Add(new("catalog_invalid", violation));
        }

        // Candidati e relazioni note: disposition terminale fondata su originali.
        var candidates = run.Candidates;
        var byId = candidates.Dispositions.ToDictionary(d => d.CandidateId, StringComparer.Ordinal);
        var unresolved = candidates.Unresolved;
        foreach (var id in unresolved)
        {
            var state = byId.TryGetValue(id, out var d) ? $"{d.State}{(d.Reason is null ? string.Empty : ": " + d.Reason)}" : "senza disposition";
            gaps.Add(new("candidate_not_terminal", $"candidato {id} non terminale ({state})."));
        }

        foreach (var d in candidates.Dispositions.Where(d => d.State is CandidateState.Compared or CandidateState.Open && d.ComparisonOutputHashes.Count == 0))
        {
            gaps.Add(new("candidate_not_grounded", $"candidato {d.CandidateId} {d.State} senza l'hash dell'output di confronto sugli originali."));
        }

        var known = candidates.Candidates.Select(c => c.CandidateId).ToHashSet(StringComparer.Ordinal);
        foreach (var relation in run.Directory.KnownRelations.OrderBy(c => c.CandidateId, StringComparer.Ordinal).Where(c => !known.Contains(c.CandidateId) || unresolved.Contains(c.CandidateId)))
        {
            gaps.Add(new("known_relation_unresolved", $"relazione nota (candidato {relation.CandidateId}) senza confronto terminale."));
        }

        var frontier = run.FinalPass?.Frontier.Select(f => f.CandidateId).Order(StringComparer.Ordinal).ToList() ?? [];
        if (run.FinalPass is null)
        {
            gaps.Add(new("final_pass_missing", "round finale non eseguito."));
        }
        else if (run.FinalPass.Outcome != FinalPassOutcome.Complete)
        {
            gaps.Add(new(run.FinalPass.Code ?? DocumentWorkException.CoverageIncomplete, run.FinalPass.Reason ?? "round finale non completo."));
            gaps.AddRange(run.FinalPass.Frontier.Select(f => new CompletionGap("frontier", $"candidato {f.CandidateId}: {f.Reason}")));
        }

        // Patch: soltanto Applied/Duplicate/PreservedAsConflict sono terminali; Invalid, Stale necessari e NotProcessed impediscono Completed.
        var composition = run.Patches.Composition;
        foreach (var patchId in composition.BlockingPatchIds)
        {
            gaps.Add(new("patch_blocking", $"patch {patchId} Invalid/Stale necessaria o in conflitto d'integrità."));
        }

        foreach (var d in run.Patches.RunDispositions.Where(d => d.Kind == RunPatchDispositionKind.NotProcessed))
        {
            gaps.Add(new("patch_not_processed", $"patch {d.PatchId} NotProcessed."));
        }

        // Rappresentazione nella revisione: i conflitti di patch e i candidati Open vanno portati con tutte le fonti nelle alternative (o nelle domande aperte).
        foreach (var group in composition.Conflicts)
        {
            var represented = analysis.Conflicts.Any(c => c.Alternatives.Count >= group.Proposals.Count && group.SourceIds.All(s => c.Alternatives.SelectMany(a => a.EvidenceRefs).Contains(s)));
            if (!represented)
            {
                gaps.Add(new("conflict_not_represented", $"gruppo di conflitto {group.GroupId}: nessun conflitto della revisione porta tutte le proposte e fonti."));
            }
        }

        var sourcesOf = snapshot.Evidence.ToDictionary(e => e.EvidenceId.ToString("D"), e => e.Sources.Select(s => s.SourceId).ToList(), StringComparer.Ordinal);
        foreach (var open in candidates.Dispositions.Where(d => d.State == CandidateState.Open).OrderBy(d => d.CandidateId, StringComparer.Ordinal))
        {
            var candidate = candidates.Candidates.Single(c => c.CandidateId == open.CandidateId);
            var needed = new[] { candidate.Left, candidate.Right }.Where(e => e.Kind == CandidateEndpointKind.Evidence).SelectMany(e => sourcesOf.GetValueOrDefault(e.Id) ?? []).ToList();
            var held = analysis.Conflicts.Any(c => needed.All(s => c.Alternatives.SelectMany(a => a.EvidenceRefs).Contains(s)))
                || analysis.OpenQuestions.Any(q => needed.All(s => q.EvidenceRefs.Contains(s)));
            if (!held)
            {
                gaps.Add(new("open_candidate_not_represented", $"candidato Open {open.CandidateId}: le fonti di entrambe le alternative non sono rappresentate in un conflitto o in una domanda aperta della revisione."));
            }
        }

        // Mappa di ogni EvidenceId su item della revisione.
        var items = ReconciliationContractRules.ItemIds(analysis).ToHashSet();
        foreach (var entry in evidenceMap.Where(m => !items.Contains(m.ItemId)))
        {
            gaps.Add(new("evidence_map_item_unknown", $"evidenceMap: item {entry.ItemId:D} assente dalla revisione."));
        }

        var mapped = evidenceMap.SelectMany(e => e.EvidenceIds).Distinct().OrderBy(g => g.ToString("D"), StringComparer.Ordinal).ToList();
        var unmapped = snapshot.Evidence.Select(e => e.EvidenceId).Where(id => !mapped.Contains(id)).OrderBy(g => g.ToString("D"), StringComparer.Ordinal).ToList();
        gaps.AddRange(unmapped.Select(id => new CompletionGap("evidence_not_mapped", $"evidenza {id:D} non mappata su alcun item.")));

        var hash = CanonicalJson.HashText(ReconciliationCodec.Canonical(new
        {
            expected, visitedA, visitedB, missing = missing.ToList(), expectedNodes, visitedNodes, missingNodes, pageHashes,
            candidates = candidates.Candidates.Select(c => c.CandidateId).Order(StringComparer.Ordinal).ToList(), unresolved,
            patches = run.Patches.RunDispositions.Select(d => $"{d.PatchId}:{d.Kind}").Order(StringComparer.Ordinal).ToList(), blocking = composition.BlockingPatchIds, frontier,
            mapped = mapped.Select(g => g.ToString("D")).ToList(), gaps,
        }));
        var report = new CompletionReport(gaps.Count == 0, gaps, expected, visitedA, visitedB, [.. missing], hash);
        return new(report, expectedNodes, visitedNodes, missingNodes, pageHashes, unresolved, [.. composition.BlockingPatchIds], frontier, mapped, unmapped);
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
