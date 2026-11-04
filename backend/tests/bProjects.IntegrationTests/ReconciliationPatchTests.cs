using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using F = bProjects.IntegrationTests.ReconciliationFixtures;

namespace bProjects.IntegrationTests;

/// <summary>
/// T076 (DET) — composizione delle patch dei passaggi gerarchici (AI §4.4) con output finti: stesso compositore del nucleo T121, origini e risultati dei batch,
/// membership/espansioni (endpoint Evidence riaperti integralmente nel batch d'origine) e disposition persistenti T122. Nessun database e nessun modello reale.
/// </summary>
public sealed class ReconciliationPatchTests
{
    private static readonly ReconciliationSnapshot Snapshot = F.Snapshot(withThird: true);

    private static string Entry(Guid id) => F.EntryId(id, Snapshot.Evidence.Single(e => e.EvidenceId == id).ContentHash);

    private static ItemProposal Item() => new()
    {
        LocalKey = "r1", Text = "Massimo 5 ordini per cliente A", Classification = DocumentAnalysisClassification.Documented, EvidenceIds = [F.E1, F.E2], SourceIds = [F.S1, F.S2],
    };

    private static BatchOutcome Outcome(string batchId, string stage, IReadOnlyList<string> members, IReadOnlyList<Patch> patches, bool withItem = false, ReconcileView view = ReconcileView.A)
    {
        var batch = new DerivedBatchInput(
            batchId, stage, view.ToString(), null, 0, 0, [], JsonSerializer.Serialize(new { members }), 10, "measured", F.InputHash, "reconcile-1", "document-reconcile-v2");
        var result = new ReconcileResult
        {
            BatchId = batchId, InputHash = F.InputHash, View = view, Level = 0, VisitedEntryIds = [.. members.Order(StringComparer.Ordinal)], VisitedNodeIds = [],
            Candidates = [], Dispositions = [], ItemProposals = withItem ? [Item()] : [], Patches = patches, Descriptor = null, NoFindingsReason = null,
        };
        return new(batch, ReconciliationCandidateTests.Sha("out|" + batchId), result);
    }

    /// <summary>Foglia con la proposta r1 (la base) che riapre E1, E2 ed E3 integralmente, più le patch indicate.</summary>
    private static BatchOutcome Leaf(string batchId, params Patch[] patches) =>
        Outcome(batchId, "leaf", [Entry(F.E1), Entry(F.E2), Entry(F.E3)], patches, withItem: true);

    private static ReconciliationBase BaseOf(IReadOnlyList<BatchOutcome> outcomes) => DocumentAnalysisBase.ComputeHierarchical(outcomes, Snapshot);

    private static Patch Stamp(Patch patch, ReconciliationBase baseState, string batchId) => F.WithId(patch with
    {
        BaseHash = baseState.BaseHash,
        Target = new PatchTarget { Kind = PatchTargetKind.Item, Id = "r1", ExpectedHash = baseState.TargetHashes[ReconciliationBase.TargetKey(PatchTargetKind.Item, "r1")] },
        OriginBatchIds = [batchId],
    });

    private static Patch Relation(ReconciliationBase b, string batchId, Guid from, Guid to, EvidenceRelationType type, string rationale = "motivo") => Stamp(new Patch
    {
        PatchId = "x", BaseHash = F.BaseHash, Target = new PatchTarget { Kind = PatchTargetKind.Item, Id = "r1", ExpectedHash = F.Hash1 }, Operation = PatchOperation.AddRelation,
        Value = new AddRelationValue { FromEvidenceId = from, ToEvidenceId = to, Type = type, Basis = EvidenceRelationBasis.Inferred, Rationale = rationale },
        EvidenceIds = [.. ReconciliationIds.SortedIds([from, to]).Select(Guid.Parse)],
        SourceIds = [.. new[] { from, to }.Select(SourceOf).Distinct().OrderBy(g => g.ToString("D"), StringComparer.Ordinal)], OriginBatchIds = [batchId],
    }, b, batchId);

    private static Guid SourceOf(Guid evidence) => Snapshot.Evidence.Single(e => e.EvidenceId == evidence).Sources[0].SourceId;

    private static ReconciliationBase Base() => BaseOf([Leaf("leaf-a")]);

    private static IReadOnlyList<string> Shape(PatchComposition c) =>
        [.. c.Effects.Select(e => $"{e.Key}|{string.Join(',', e.EvidenceIds)}|{string.Join(',', e.SourceIds)}|{string.Join(',', e.Rationales)}")];

    // ------------------------------------------------------------------ origini e permutazioni

    [Fact]
    public void Patches_from_several_origins_compose_into_one_effect_with_united_origins_and_the_order_of_arrival_is_irrelevant()
    {
        var b = Base();
        var p1 = Relation(b, "leaf-a", F.E1, F.E2, EvidenceRelationType.Qualifies);
        var p2 = Relation(b, "leaf-b", F.E1, F.E2, EvidenceRelationType.Qualifies) with { OriginBatchIds = ["leaf-b"] };
        var outcomes = new[] { Leaf("leaf-a", p1), Leaf("leaf-b", p2) };

        var forward = ReconciliationPatchComposer.ComposeHierarchical(outcomes, b, Snapshot);
        var backward = ReconciliationPatchComposer.ComposeHierarchical([.. outcomes.Reverse()], b, Snapshot);

        Assert.Equal(forward.Composition.Hash, backward.Composition.Hash);
        Assert.Equal(forward.RunDispositions, backward.RunDispositions);
        var effect = Assert.Single(forward.Composition.Effects);
        Assert.Equal(["leaf-a", "leaf-b"], effect.OriginBatchIds);
        Assert.Equal(2, forward.OriginOutputHashes[p1.PatchId].Count);
        Assert.Equal(RunPatchDispositionKind.Applied, Assert.Single(forward.RunDispositions).Kind);
        Assert.Empty(forward.Composition.BlockingPatchIds);
    }

    [Fact]
    public void Equivalent_patches_with_different_ids_are_one_logical_effect_and_the_extra_ones_are_duplicates_with_every_rationale_kept()
    {
        var b = Base();
        var first = Relation(b, "leaf-a", F.E1, F.E2, EvidenceRelationType.Confirms, "uno");
        var second = Relation(b, "leaf-b", F.E1, F.E2, EvidenceRelationType.Confirms, "due");

        var composed = ReconciliationPatchComposer.ComposeHierarchical([Leaf("leaf-a", first), Leaf("leaf-b", second)], b, Snapshot);

        var effect = Assert.Single(composed.Composition.Effects);
        Assert.Equal(["due", "uno"], effect.Rationales);
        Assert.Equal([RunPatchDispositionKind.Applied, RunPatchDispositionKind.Duplicate], composed.RunDispositions.OrderBy(d => d.Kind).Select(d => d.Kind));
        Assert.All(composed.RunDispositions, d => Assert.Equal(composed.Composition.Hash, d.ResultHash));
    }

    [Fact]
    public void The_same_patch_id_with_a_different_payload_in_two_outputs_is_an_integrity_conflict_never_last_write_wins()
    {
        var b = Base();
        var honest = Relation(b, "leaf-a", F.E1, F.E2, EvidenceRelationType.Qualifies);
        var forged = honest with { Value = new AddRelationValue { FromEvidenceId = F.E1, ToEvidenceId = F.E2, Type = EvidenceRelationType.Qualifies, Basis = EvidenceRelationBasis.Inferred, Rationale = "altro" }, OriginBatchIds = ["leaf-b"] };

        foreach (var outcomes in new[] { new[] { Leaf("leaf-a", honest), Leaf("leaf-b", forged) }, new[] { Leaf("leaf-b", forged), Leaf("leaf-a", honest) } })
        {
            var composed = ReconciliationPatchComposer.ComposeHierarchical(outcomes, b, Snapshot);

            Assert.True(composed.Composition.HasIntegrityConflict);
            Assert.Empty(composed.Composition.Effects);
            var record = Assert.Single(composed.RunDispositions);
            Assert.Equal(RunPatchDispositionKind.Invalid, record.Kind);
            Assert.Contains("integrity_conflict", record.Reason);
            Assert.Contains(honest.PatchId, composed.Composition.BlockingPatchIds);
        }
    }

    // ------------------------------------------------------------------ membership ed espansioni

    [Fact]
    public void An_evidence_endpoint_not_reopened_in_the_origin_batch_is_invalid_and_blocks_completion()
    {
        var b = Base();
        var reach = Relation(b, "leaf-narrow", F.E1, F.E3, EvidenceRelationType.Contradicts);
        var narrow = Outcome("leaf-narrow", "leaf", [Entry(F.E1), Entry(F.E2)], [reach]);   // E3 non è stata riaperta in questo batch

        var composed = ReconciliationPatchComposer.ComposeHierarchical([Leaf("leaf-a"), narrow], b, Snapshot);

        var record = Assert.Single(composed.RunDispositions);
        Assert.Equal(RunPatchDispositionKind.Invalid, record.Kind);
        Assert.Contains("riaperto", record.Reason);
        Assert.Equal([reach.PatchId], composed.Composition.BlockingPatchIds);
        Assert.Empty(composed.Composition.Effects);
    }

    [Fact]
    public void A_descriptor_only_batch_never_supports_a_patch_and_an_expansion_batch_does_when_it_reopened_both_endpoints()
    {
        var b = Base();
        var fromReduce = Relation(b, "reduce-1", F.E1, F.E2, EvidenceRelationType.SameRule);
        var reduce = Outcome("reduce-1", "reduce", ["node-1", "node-2"], [fromReduce]);
        var viaExpansion = Relation(b, "expand-1", F.E1, F.E3, EvidenceRelationType.Contradicts);
        var expand = Outcome("expand-1", "expand", [Entry(F.E1), Entry(F.E3)], [viaExpansion]);

        var composed = ReconciliationPatchComposer.ComposeHierarchical([Leaf("leaf-a"), reduce, expand], b, Snapshot);

        Assert.Equal(RunPatchDispositionKind.Invalid, composed.RunDispositions.Single(d => d.PatchId == fromReduce.PatchId).Kind);
        Assert.Equal(RunPatchDispositionKind.Applied, composed.RunDispositions.Single(d => d.PatchId == viaExpansion.PatchId).Kind);
        Assert.Equal([fromReduce.PatchId], composed.Composition.BlockingPatchIds);
    }

    [Fact]
    public void A_patch_that_does_not_cite_the_batch_it_came_from_is_invalid()
    {
        var b = Base();
        var foreign = Relation(b, "somewhere-else", F.E1, F.E2, EvidenceRelationType.Qualifies);

        var composed = ReconciliationPatchComposer.ComposeHierarchical([Leaf("leaf-a", foreign)], b, Snapshot);

        Assert.Equal(RunPatchDispositionKind.Invalid, Assert.Single(composed.RunDispositions).Kind);
        Assert.Contains("originBatchIds", Assert.Single(composed.RunDispositions).Reason);
    }

    // ------------------------------------------------------------------ stale, conflitti, equivalenza con il nucleo

    [Fact]
    public void A_stale_necessary_patch_does_not_complete_and_is_never_rebased()
    {
        var b = Base();
        var stale = Relation(b, "leaf-a", F.E1, F.E2, EvidenceRelationType.Qualifies) with { BaseHash = new string('e', 64) };
        stale = F.WithId(stale);

        var composed = ReconciliationPatchComposer.ComposeHierarchical([Leaf("leaf-a", stale)], b, Snapshot);

        Assert.Equal(RunPatchDispositionKind.Stale, Assert.Single(composed.RunDispositions).Kind);
        Assert.Equal([stale.PatchId], composed.Composition.BlockingPatchIds);
        Assert.Empty(composed.Composition.Effects);
        Assert.Null(Assert.Single(composed.RunDispositions).ResultHash);
    }

    [Fact]
    public void Incompatible_patches_from_different_passes_are_preserved_as_a_conflict_with_all_sources_and_qualifiers_in_any_order()
    {
        var b = Base();
        var same = Relation(b, "leaf-a", F.E1, F.E2, EvidenceRelationType.SameRule, "stessa regola");
        var contradicts = Relation(b, "expand-1", F.E2, F.E1, EvidenceRelationType.Contradicts, "30 giorni contro 45");
        var outcomes = new[] { Leaf("leaf-a", same), Outcome("expand-1", "expand", [Entry(F.E1), Entry(F.E2)], [contradicts]) };

        var forward = ReconciliationPatchComposer.ComposeHierarchical(outcomes, b, Snapshot);
        var backward = ReconciliationPatchComposer.ComposeHierarchical([.. outcomes.Reverse()], b, Snapshot);

        Assert.Equal(forward.Composition.Hash, backward.Composition.Hash);
        var group = Assert.Single(forward.Composition.Conflicts);
        Assert.Equal(ConflictKind.SameRuleContradicts, group.Kind);
        Assert.Equal(2, group.Proposals.Count);
        Assert.Equal([F.S1, F.S2], group.SourceIds.Order());
        Assert.All(forward.RunDispositions, d => Assert.Equal(RunPatchDispositionKind.PreservedAsConflict, d.Kind));
        Assert.Empty(forward.Composition.Effects);
        Assert.Empty(forward.Composition.BlockingPatchIds);
    }

    [Fact]
    public void The_same_set_of_patches_gives_the_same_dispositions_conflicts_and_effects_whether_it_comes_from_one_call_or_from_hierarchical_passes()
    {
        var b = Base();
        var patches = new[]
        {
            Relation(b, "x", F.E1, F.E2, EvidenceRelationType.Qualifies, "a"),
            Relation(b, "x", F.E1, F.E3, EvidenceRelationType.SameRule, "b"),
            Relation(b, "x", F.E3, F.E1, EvidenceRelationType.Contradicts, "c"),
            Relation(b, "x", F.E1, F.E2, EvidenceRelationType.Confirms, "d"),
        };
        var single = ReconciliationPatchComposer.Compose(patches, b, Snapshot);

        var hierarchical = ReconciliationPatchComposer.ComposeHierarchical(
            [Leaf("p0", patches[0] with { OriginBatchIds = ["p0"] }), Leaf("p1", patches[1] with { OriginBatchIds = ["p1"] }),
             Leaf("p2", patches[2] with { OriginBatchIds = ["p2"] }), Leaf("p3", patches[3] with { OriginBatchIds = ["p3"] })],
            b, Snapshot).Composition;

        Assert.Equal(single.Dispositions.Select(d => (d.PatchId, d.Kind)), hierarchical.Dispositions.Select(d => (d.PatchId, d.Kind)));
        Assert.Equal(single.Conflicts.Select(c => (c.GroupId, c.PatchIds)), hierarchical.Conflicts.Select(c => (c.GroupId, c.PatchIds)));
        Assert.Equal(Shape(single), Shape(hierarchical));
        Assert.Equal(single.BlockingPatchIds, hierarchical.BlockingPatchIds);
    }

    [Fact]
    public void Null_arguments_are_rejected_and_an_empty_input_is_an_empty_composition()
    {
        Assert.Throws<ArgumentNullException>(() => ReconciliationPatchComposer.ComposeHierarchical(null!, Base(), Snapshot));

        var composed = ReconciliationPatchComposer.ComposeHierarchical([], Base(), Snapshot);

        Assert.Empty(composed.RunDispositions);
        Assert.Empty(composed.Composition.Effects);
    }

    [Fact]
    public void The_base_of_a_hierarchical_pass_comes_only_from_the_leaf_proposals_so_later_batches_cannot_move_it()
    {
        var leaf = Leaf("leaf-a");
        var expandWithItem = Outcome("expand-1", "expand", [Entry(F.E1)], [], withItem: true);
        expandWithItem = expandWithItem with { Result = expandWithItem.Result with { ItemProposals = [Item() with { LocalKey = "late", Text = "tardiva" }] } };

        var baseState = DocumentAnalysisBase.ComputeHierarchical([leaf, expandWithItem], Snapshot);

        Assert.Equal(BaseOf([leaf]).BaseHash, baseState.BaseHash);
        Assert.DoesNotContain(ReconciliationBase.TargetKey(PatchTargetKind.Item, "late"), baseState.TargetHashes.Keys);
    }
}
