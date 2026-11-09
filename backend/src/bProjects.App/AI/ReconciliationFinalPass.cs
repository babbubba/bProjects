namespace bProjects.App.AI;

// Round finale bounded (002 T075; contracts/ai-workflow.md §4.3). Dopo A/B/Cross c'è UN solo round: merge delle chiavi/alias emersi, espansione di tutti i candidati
// ancora pendenti e confronti diretti dei loro endpoint (una volta per coppia: i duplicati sono cache hit logici). Nuovo lavoro residuo → NotProcessed
// reconciliation_round_limit senza iterare; limiti globali (512 chiamate retry inclusi, 100000 candidati, byte derivati, deadline) e stallo → coverage_incomplete con
// la frontiera esplicita. Un limite non autorizza mai il pruning: ciò che non è stato risolto resta non terminale.

public enum FinalPassOutcome { Complete, RoundLimit, CoverageIncomplete }

public sealed record FrontierItem(string CandidateId, string Reason);

/// <summary>Terminale del round: codice stabile, frontiera (candidati non terminali e motivo) e se almeno un candidato è stato risolto o un batch nuovo completato.</summary>
public sealed record FinalPassResult(FinalPassOutcome Outcome, string? Code, string? Reason, IReadOnlyList<FrontierItem> Frontier, CandidateRun Run, bool Progress);

/// <summary>Chiave inferita emersa dai passaggi (alias): entra nella directory solo con originOutputHash, senza riscrivere le chiavi letterali.</summary>
public sealed record InferredKey(Guid EvidenceId, CatalogKey Key);

public static class ReconciliationFinalPass
{
    public const string RoundLimitReason = "reconciliation_round_limit";

    public static async ValueTask<FinalPassResult> RunAsync(
        ReconciliationCandidates work, ReconciliationSnapshot snapshot, ReconciliationDirectoryResult directory, IReadOnlyList<InferredKey> emerged, CancellationToken ct)
    {
        if (work.FinalResult is { } done)
        {
            return done;   // replay: un solo round finale, nessun lavoro nuovo e nessun avanzamento contato
        }

        var outcomesBefore = work.Result().Outcomes.Count;
        IReadOnlyList<string> initial = [];
        string? limit = null;
        try
        {
            MergeEmerged(work, snapshot, directory, emerged);
            initial = work.Pending;
            var children = new List<string>();
            foreach (var id in work.Pending)
            {
                if (work.Disposition(id).State == CandidateState.Pending)
                {
                    children.AddRange(await work.ProcessAsync(id, ct));
                }
            }

            foreach (var child in children.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (work.Disposition(child).State == CandidateState.Pending)
                {
                    await work.ProcessAsync(child, ct);
                }
            }
        }
        catch (DocumentWorkException e) when (e.Code == DocumentWorkException.CoverageIncomplete && e.Message.StartsWith(ReconciliationCandidates.LimitReached, StringComparison.Ordinal))
        {
            limit = e.Message;
        }

        var moved = initial.Any(id => work.Disposition(id).State != CandidateState.Pending);

        // Nuovo lavoro emerso nel round (o non raggiunto per un limite): conservato come non elaborato, mai iterato né scartato.
        foreach (var id in work.Pending)
        {
            work.MarkNotProcessed(id, limit is null ? RoundLimitReason : limit);
        }

        var run = work.Result();
        var progress = run.Outcomes.Count > outcomesBefore || moved;
        var unresolved = run.Unresolved;
        var frontier = unresolved.Select(id => new FrontierItem(id, run.Dispositions.Single(d => d.CandidateId == id).Reason ?? run.Dispositions.Single(d => d.CandidateId == id).State.ToString())).ToList();
        var result = limit is not null
            ? new FinalPassResult(FinalPassOutcome.CoverageIncomplete, DocumentWorkException.CoverageIncomplete, limit, frontier, run, progress)
            : frontier.Any(f => f.Reason == RoundLimitReason)
                ? new FinalPassResult(FinalPassOutcome.RoundLimit, DocumentWorkException.ReconciliationRoundLimit, $"{RoundLimitReason}: nuovo lavoro emerso nel round finale.", frontier, run, progress)
                : frontier.Count > 0
                    ? new FinalPassResult(
                        FinalPassOutcome.CoverageIncomplete, DocumentWorkException.CoverageIncomplete,
                        !progress ? "stallo: nessun avanzamento, restano candidati non terminali." : "candidati non terminali dopo il round finale.", frontier, run, progress)
                    : new FinalPassResult(FinalPassOutcome.Complete, null, null, [], run, progress);
        work.FinalResult = result;
        return result;
    }

    /// <summary>Merge delle chiavi inferite: nuove coppie di insiemi producono nuovi candidati (Pending); ciò che già esiste non si riapre.</summary>
    private static void MergeEmerged(ReconciliationCandidates work, ReconciliationSnapshot snapshot, ReconciliationDirectoryResult directory, IReadOnlyList<InferredKey> emerged)
    {
        if (emerged.Count == 0)
        {
            return;
        }

        foreach (var violation in emerged.SelectMany(e => ReconciliationCatalogRules.CheckKey(e.Key)))
        {
            throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"Chiave inferita non valida: {violation}");
        }

        var byEvidence = emerged.GroupBy(e => e.EvidenceId).ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToList());
        var entries = directory.Entries.Select(entry =>
        {
            if (!byEvidence.TryGetValue(entry.EvidenceId, out var extra))
            {
                return entry;
            }

            var keys = entry.Keys.Concat(extra).ToList();
            keys.Sort(ReconciliationDirectory.CompareKeys);
            var distinct = keys.Where((k, i) => i == 0 || ReconciliationDirectory.CompareKeys(keys[i - 1], k) != 0).ToList();
            return entry with { Keys = distinct };
        }).ToList();

        var (sets, candidates) = ReconciliationDirectory.SharedKeyCandidates(snapshot, directory.Leaves, entries);
        work.AddSets(sets);
        foreach (var candidate in candidates.Where(c => !work.Has(c.CandidateId)))
        {
            work.Add(candidate, sets.Single(s => s.SetId == candidate.Left.Id).View);
        }
    }
}
