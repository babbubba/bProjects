using bProjects.App.Evidence;

namespace bProjects.App.AI;

// Composizione delle patch (contracts/ai-workflow.md §4.4, T121): tutte contro la stessa base, poi insieme ordinato per patchId; l'ordine
// di arrivo non dà precedenza. Nessun worker scrive un item corrente: il risultato è un insieme di effetti proposti e di gruppi di conflitto.

public enum PatchDispositionKind { Applied, Stale, Invalid, IntegrityConflict, PreservedAsConflict }

public enum ConflictKind { SameRuleContradicts, OppositeSupersedesCorrects, IncompatibleAlternatives }

/// <summary>Base immutabile del passaggio: baseHash e hash corrente di ogni target (chiave `Kind:Id`).</summary>
public sealed record ReconciliationBase(string BaseHash, IReadOnlyDictionary<string, string> TargetHashes)
{
    public static string TargetKey(PatchTargetKind kind, string id) => $"{kind}:{id}";
}

public sealed record PatchDispositionRecord(string PatchId, PatchDispositionKind Kind, string? Reason);

/// <summary>Effetto proposto da patch applicate: unione ordinata di evidenze/fonti/origini, mai una modifica di testo, polarità o classificazione.</summary>
public sealed record ComposedEffect(
    string Key, PatchOperation Operation, string TargetKey, IReadOnlyList<Guid> EvidenceIds, IReadOnlyList<Guid> SourceIds,
    IReadOnlyList<string> OriginBatchIds, IReadOnlyList<string> PatchIds, IReadOnlyList<string> Rationales);

public sealed record ConflictProposal(string PatchId, PatchOperation Operation, string Summary, IReadOnlyList<string> OriginBatchIds);

/// <summary>Gruppo di incompatibilità: nessuna proposta vince, tutte le fonti e le alternative sono conservate, decisione al PM.</summary>
public sealed record ConflictGroup(
    string GroupId, ConflictKind Kind, IReadOnlyList<string> PatchIds, IReadOnlyList<Guid> EvidenceIds, IReadOnlyList<Guid> SourceIds,
    IReadOnlyList<ConflictProposal> Proposals, bool RequiresPmDecision);

public sealed record PatchComposition(
    IReadOnlyList<PatchDispositionRecord> Dispositions, IReadOnlyList<ComposedEffect> Effects, IReadOnlyList<ConflictGroup> Conflicts,
    IReadOnlyList<string> BlockingPatchIds, bool HasIntegrityConflict, string Hash);

public static class ReconciliationPatchComposer
{
    public static PatchComposition Compose(IReadOnlyList<Patch> patches, ReconciliationBase baseState, ReconciliationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(patches);
        var dispositions = new Dictionary<string, PatchDispositionRecord>(StringComparer.Ordinal);
        var unique = new SortedDictionary<string, Patch>(StringComparer.Ordinal);
        var integrity = false;

        foreach (var group in patches.GroupBy(p => p.PatchId, StringComparer.Ordinal))
        {
            var canonical = group.Select(p => ReconciliationCodec.Canonical(p with { OriginBatchIds = [] })).Distinct(StringComparer.Ordinal).ToList();
            if (canonical.Count > 1)
            {
                integrity = true;
                dispositions[group.Key] = new(group.Key, PatchDispositionKind.IntegrityConflict, "Stesso patchId con payload diverso.");
                continue;
            }

            var first = group.First();
            unique[group.Key] = first with { OriginBatchIds = [.. group.SelectMany(p => p.OriginBatchIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)] };
        }

        var staleCandidates = new List<Patch>();
        var valid = new List<Patch>();
        foreach (var patch in unique.Values)
        {
            var problems = ReconciliationContractRules.CheckPatch(patch).Concat(ReconciliationReferences.CheckPatch(patch, snapshot)).ToList();
            var key = ReconciliationBase.TargetKey(patch.Target.Kind, patch.Target.Id);
            if (problems.Count > 0)
            {
                dispositions[patch.PatchId] = new(patch.PatchId, PatchDispositionKind.Invalid, string.Join(" | ", problems));
            }
            else if (!baseState.TargetHashes.TryGetValue(key, out var current))
            {
                dispositions[patch.PatchId] = new(patch.PatchId, PatchDispositionKind.Invalid, $"target {key} assente dalla base.");
            }
            else if (patch.BaseHash != baseState.BaseHash || patch.Target.ExpectedHash != current)
            {
                dispositions[patch.PatchId] = new(patch.PatchId, PatchDispositionKind.Stale, "baseHash o expectedHash superato: nessuna applicazione né rebase.");
                staleCandidates.Add(patch);
            }
            else
            {
                valid.Add(patch);
            }
        }

        var conflicts = DetectConflicts(valid);
        var inConflict = conflicts.SelectMany(c => c.PatchIds).ToHashSet(StringComparer.Ordinal);
        foreach (var patch in valid)
        {
            dispositions[patch.PatchId] = inConflict.Contains(patch.PatchId)
                ? new(patch.PatchId, PatchDispositionKind.PreservedAsConflict, "Incompatibile: conservata nel gruppo di conflitto.")
                : new(patch.PatchId, PatchDispositionKind.Applied, null);
        }

        var effects = BuildEffects(valid.Where(p => !inConflict.Contains(p.PatchId)));
        var blocking = dispositions.Values.Where(d => d.Kind is PatchDispositionKind.Invalid or PatchDispositionKind.IntegrityConflict).Select(d => d.PatchId)
            .Concat(staleCandidates.Where(p => !IsCarried(p, effects)).Select(p => p.PatchId))
            .Order(StringComparer.Ordinal).ToList();

        var ordered = dispositions.Values.OrderBy(d => d.PatchId, StringComparer.Ordinal).ToList();
        var hash = CanonicalJson.HashText(ReconciliationCodec.Canonical(new { dispositions = ordered, effects, conflicts }));
        return new(ordered, effects, conflicts, blocking, integrity, hash);
    }

    private static List<ComposedEffect> BuildEffects(IEnumerable<Patch> applied)
    {
        var buckets = new SortedDictionary<string, List<Patch>>(StringComparer.Ordinal);
        foreach (var patch in applied)
        {
            var key = patch.Value switch
            {
                AddSupportValue => $"support|{ReconciliationBase.TargetKey(patch.Target.Kind, patch.Target.Id)}",
                AddRelationValue r => $"relation|{r.FromEvidenceId:D}|{r.ToEvidenceId:D}|{r.Type}",
                _ => $"alternative|{patch.PatchId}",
            };
            if (!buckets.TryGetValue(key, out var list))
            {
                buckets[key] = list = [];
            }

            list.Add(patch);
        }

        return [.. buckets.Select(b => new ComposedEffect(
            b.Key, b.Value[0].Operation, ReconciliationBase.TargetKey(b.Value[0].Target.Kind, b.Value[0].Target.Id),
            [.. b.Value.SelectMany(p => p.EvidenceIds).Distinct().OrderBy(g => g.ToString("D"), StringComparer.Ordinal)],
            [.. b.Value.SelectMany(p => p.SourceIds).Distinct().OrderBy(g => g.ToString("D"), StringComparer.Ordinal)],
            [.. b.Value.SelectMany(p => p.OriginBatchIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. b.Value.Select(p => p.PatchId).Order(StringComparer.Ordinal)],
            [.. b.Value.Select(p => p.Value switch { AddRelationValue r => r.Rationale, ProposeAlternativeValue a => a.Text, _ => null }).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]))];
    }

    /// <summary>Una patch Stale non è necessaria se il suo effetto è già portato da una patch applicata.</summary>
    private static bool IsCarried(Patch stale, List<ComposedEffect> effects) => stale.Value switch
    {
        AddSupportValue => effects.Any(e => e.Operation == PatchOperation.AddSupport && e.TargetKey == ReconciliationBase.TargetKey(stale.Target.Kind, stale.Target.Id)
            && stale.EvidenceIds.All(e.EvidenceIds.Contains) && stale.SourceIds.All(e.SourceIds.Contains)),
        AddRelationValue r => effects.Any(e => e.Key == $"relation|{r.FromEvidenceId:D}|{r.ToEvidenceId:D}|{r.Type}"),
        ProposeAlternativeValue a => effects.Any(e => e.Operation == PatchOperation.ProposeAlternative
            && e.TargetKey == ReconciliationBase.TargetKey(stale.Target.Kind, stale.Target.Id) && e.Rationales.Contains(a.Text)),
        _ => false,
    };

    private static List<ConflictGroup> DetectConflicts(List<Patch> valid)
    {
        var groups = new SortedDictionary<string, (ConflictKind Kind, List<Patch> Patches)>(StringComparer.Ordinal);

        void Add(string key, ConflictKind kind, Patch patch)
        {
            if (!groups.TryGetValue(key, out var entry))
            {
                groups[key] = entry = (kind, []);
            }

            if (!entry.Patches.Contains(patch))
            {
                entry.Patches.Add(patch);
            }
        }

        // Stessa coppia di endpoint (non ordinata): SameRule+Contradicts, oppure Supersedes/Corrects con direzione opposta.
        var relations = valid.Where(p => p.Value is AddRelationValue).Select(p => (Patch: p, Value: (AddRelationValue)p.Value))
            .GroupBy(x => string.CompareOrdinal(x.Value.FromEvidenceId.ToString("D"), x.Value.ToEvidenceId.ToString("D")) <= 0
                ? $"{x.Value.FromEvidenceId:D}|{x.Value.ToEvidenceId:D}" : $"{x.Value.ToEvidenceId:D}|{x.Value.FromEvidenceId:D}");
        foreach (var pair in relations)
        {
            var items = pair.ToList();
            if (items.Any(x => x.Value.Type == EvidenceRelationType.SameRule) && items.Any(x => x.Value.Type == EvidenceRelationType.Contradicts))
            {
                foreach (var x in items.Where(x => x.Value.Type is EvidenceRelationType.SameRule or EvidenceRelationType.Contradicts))
                {
                    Add($"samerule|{pair.Key}", ConflictKind.SameRuleContradicts, x.Patch);
                }
            }

            var directed = items.Where(x => x.Value.Type is EvidenceRelationType.Supersedes or EvidenceRelationType.Corrects).ToList();
            if (directed.Select(x => x.Value.FromEvidenceId).Distinct().Count() > 1)
            {
                foreach (var x in directed)
                {
                    Add($"opposite|{pair.Key}", ConflictKind.OppositeSupersedesCorrects, x.Patch);
                }
            }
        }

        // Proposte alternative diverse (testo o classificazione) sullo stesso target: incompatibili finché il PM non decide.
        foreach (var target in valid.Where(p => p.Value is ProposeAlternativeValue).GroupBy(p => ReconciliationBase.TargetKey(p.Target.Kind, p.Target.Id)))
        {
            var distinct = target.Select(p => (ProposeAlternativeValue)p.Value).Select(v => (v.Text, v.Classification)).Distinct().Count();
            if (distinct > 1)
            {
                foreach (var patch in target)
                {
                    Add($"alternatives|{target.Key}", ConflictKind.IncompatibleAlternatives, patch);
                }
            }
        }

        return [.. groups.Select(g =>
        {
            var patches = g.Value.Patches.OrderBy(p => p.PatchId, StringComparer.Ordinal).ToList();
            var ids = patches.Select(p => p.PatchId).ToList();
            return new ConflictGroup(
                CanonicalJson.HashText(ReconciliationCodec.Canonical(new { t = "conflict", kind = g.Value.Kind, patchIds = ids })), g.Value.Kind, ids,
                [.. patches.SelectMany(p => p.EvidenceIds).Distinct().OrderBy(x => x.ToString("D"), StringComparer.Ordinal)],
                [.. patches.SelectMany(p => p.SourceIds).Distinct().OrderBy(x => x.ToString("D"), StringComparer.Ordinal)],
                [.. patches.Select(p => new ConflictProposal(p.PatchId, p.Operation, Summary(p), p.OriginBatchIds))], true);
        }).OrderBy(g => g.GroupId, StringComparer.Ordinal)];
    }

    private static string Summary(Patch p) => p.Value switch
    {
        AddRelationValue r => $"{r.Type} {r.FromEvidenceId:D} -> {r.ToEvidenceId:D}" + (r.Rationale is null ? string.Empty : $": {r.Rationale}"),
        ProposeAlternativeValue a => $"{a.Text} ({a.Reason})",
        _ => "AddSupport",
    };
}
