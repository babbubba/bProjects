using System.Diagnostics;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T121 (DET) — validazione comune di patch e completezza (contracts/ai-workflow.md §4.4–4.5), caso singola chiamata: evidenze e snapshot congelati,
/// modelli finti, nessun database. Distingue validità del batch da completezza finale; nessuna fiducia in una dichiarazione «complete» dell'AI.
/// </summary>
public sealed class ReconciliationCoreValidationTests
{
    private static readonly Guid AnalysisId = Guid.Parse("a0000000-0000-4000-8000-000000000002");
    private static readonly Guid Outsider = Guid.Parse("99000000-0000-4000-8000-000000000009");
    private static readonly ReconciliationSnapshot Snapshot = ReconciliationFixtures.Snapshot();

    private static ReconciliationBase Base() => new(
        ReconciliationFixtures.BaseHash, new Dictionary<string, string> { [ReconciliationBase.TargetKey(PatchTargetKind.Item, "r1")] = ReconciliationFixtures.Hash1 });

    private static DocumentAnalysisFacts Facts(ReconciliationSnapshot snapshot) => new(ReconciliationFixtures.Project, AnalysisId, 0, 0, snapshot.Evidence.Count == 0 ? 1 : 1);

    private static ReconciliationValidation Validate(SingleCallOutput output, ReconciliationSnapshot? snapshot = null) =>
        ReconciliationValidator.ValidateSingleCall(ReconciliationCodec.Encode(output), snapshot ?? Snapshot, Base(), Facts(snapshot ?? Snapshot));

    private static SingleCallOutput WithPatches(params Patch[] patches)
    {
        var valid = ReconciliationFixtures.Valid();
        return valid with { Results = [valid.Results[0] with { Patches = patches }, valid.Results[1], valid.Results[2]] };
    }

    private static PatchComposition Compose(params Patch[] patches) => ReconciliationPatchComposer.Compose(patches, Base(), Snapshot);

    private static Patch Restamp(Patch p) => ReconciliationFixtures.WithId(p);

    private static Patch Relation(Guid from, Guid to, EvidenceRelationType type, string batch = "batch-1", string rationale = "motivo") => Restamp(new Patch
    {
        PatchId = "x", BaseHash = ReconciliationFixtures.BaseHash, Target = new PatchTarget { Kind = PatchTargetKind.Item, Id = "r1", ExpectedHash = ReconciliationFixtures.Hash1 },
        Operation = PatchOperation.AddRelation,
        Value = new AddRelationValue { FromEvidenceId = from, ToEvidenceId = to, Type = type, Basis = EvidenceRelationBasis.Inferred, Rationale = rationale },
        EvidenceIds = [.. ReconciliationIds.SortedIds([from, to]).Select(Guid.Parse)], SourceIds = [ReconciliationFixtures.S1, ReconciliationFixtures.S2], OriginBatchIds = [batch],
    });

    private static PatchDispositionKind Kind(PatchComposition c, Patch p) => c.Dispositions.Single(d => d.PatchId == p.PatchId).Kind;

    // ---- validità del batch e riferimenti ----

    [Fact]
    public void Valid_complete_output_is_admitted_and_complete()
    {
        var result = Validate(ReconciliationFixtures.Valid());

        Assert.Equal(ReconciliationOutcome.Complete, result.Outcome);
        Assert.Null(result.ErrorCode);
        Assert.Empty(result.Violations);
        Assert.True(result.Completion!.IsComplete);
        Assert.Empty(result.Completion.Gaps);
        Assert.Equal(2, result.Completion.ExpectedEntryIds.Count);
        Assert.Empty(result.Completion.MissingEntryIds);
        Assert.True(ReconciliationContractRules.IsHash(result.Completion.CoverageHash));
    }

    [Fact]
    public void Invented_endpoint_quote_or_source_is_a_permanent_error_never_a_committed_output()
    {
        var valid = ReconciliationFixtures.Valid();
        var ghost = ReconciliationFixtures.Candidate() with { Right = ReconciliationFixtures.Endpoint(Outsider, ReconciliationFixtures.Hash2) };
        var badEndpoint = valid with { Results = [valid.Results[0], valid.Results[1] with { Candidates = [ghost] }, valid.Results[2]] };
        var badQuote = valid with { Analysis = valid.Analysis with { Sources = [valid.Analysis.Sources[0] with { Quote = "Cliente Z | 9 ordini" }, valid.Analysis.Sources[1]] } };
        var proposal = valid.Results[0].ItemProposals[0];
        var badSource = valid with { Results = [valid.Results[0] with { ItemProposals = [proposal with { SourceIds = [ReconciliationFixtures.S1, Outsider] }] }, valid.Results[1], valid.Results[2]] };

        foreach (var output in new[] { badEndpoint, badQuote, badSource })
        {
            var result = Validate(output);
            Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);
            Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
            Assert.NotEmpty(result.Violations);
            Assert.Null(result.Completion);
            Assert.False(result.IsReusableWorkOutput);
        }
    }

    [Fact]
    public void Truncated_or_malformed_json_is_permanent_and_not_repaired()
    {
        var json = ReconciliationCodec.Encode(ReconciliationFixtures.Valid());
        foreach (var text in new[] { json[..(json.Length / 2)], string.Empty, "null", "[]", json + "{}" })
        {
            var result = ReconciliationValidator.ValidateSingleCall(text, Snapshot, Base(), Facts(Snapshot));
            Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);
            Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
            Assert.Null(result.Output);
            Assert.False(result.IsReusableWorkOutput);
        }
    }

    [Fact]
    public void A_self_declared_complete_flag_is_not_trusted()
    {
        var json = ReconciliationCodec.Encode(ReconciliationFixtures.Valid());
        var withFlag = json.Insert(1, "\"complete\":true,");
        var result = ReconciliationValidator.ValidateSingleCall(withFlag, Snapshot, Base(), Facts(Snapshot));
        Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);

        // Anche senza il flag, la completezza viene solo dal server: una vista non visitata resta incompleta.
        var valid = ReconciliationFixtures.Valid();
        var partial = valid with { Results = [valid.Results[0], valid.Results[1] with { VisitedEntryIds = [valid.Results[1].VisitedEntryIds[0]] }, valid.Results[2]] };
        Assert.NotEqual(ReconciliationOutcome.Complete, Validate(partial).Outcome);
    }

    [Fact]
    public void Analysis_content_rules_apply_to_the_embedded_analysis()
    {
        var valid = ReconciliationFixtures.Valid();
        var wrongProject = valid with { Analysis = valid.Analysis with { ProjectId = Guid.NewGuid() } };
        var result = Validate(wrongProject);
        Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Violations, m => m.Contains("projectId"));
    }

    [Fact]
    public void Zero_evidence_snapshot_is_complete_with_motivation_and_no_artificial_roots()
    {
        var empty = ReconciliationSnapshot.Empty(ReconciliationFixtures.Project, ReconciliationFixtures.SourceHash);
        var result = Validate(ReconciliationFixtures.Valid(empty), empty);

        Assert.Equal(ReconciliationOutcome.Complete, result.Outcome);
        Assert.Empty(result.Completion!.ExpectedEntryIds);
        Assert.All(result.Output!.Results, r => Assert.Empty(r.VisitedNodeIds));

        var output = ReconciliationFixtures.Valid(empty);
        var unmotivated = output with { Results = [.. output.Results.Select(r => r with { NoFindingsReason = null })] };
        Assert.Equal(ReconciliationOutcome.Invalid, Validate(unmotivated, empty).Outcome);
    }

    // ---- validità del batch distinta dalla completezza ----

    [Fact]
    public void Valid_batch_with_residual_coverage_is_reusable_but_does_not_complete()
    {
        var valid = ReconciliationFixtures.Valid();
        var partial = valid with { Results = [valid.Results[0], valid.Results[1] with { VisitedEntryIds = [valid.Results[1].VisitedEntryIds[0]] }, valid.Results[2]] };
        var result = Validate(partial);

        Assert.Equal(ReconciliationOutcome.ValidIncomplete, result.Outcome);
        Assert.Null(result.ErrorCode);
        Assert.True(result.IsReusableWorkOutput);
        Assert.False(result.Completion!.IsComplete);
        Assert.Contains(result.Completion.Gaps, g => g.Code == "entry_not_visited" && g.Detail.Contains("B"));
        Assert.Single(result.Completion.MissingEntryIds);
        Assert.True(result.Completion.AdditionalWorkJustified);
    }

    [Fact]
    public void Unmapped_evidence_blocks_completion_but_not_batch_validity()
    {
        var valid = ReconciliationFixtures.Valid();
        var partial = valid with { EvidenceMap = [new EvidenceMapEntry { ItemId = ReconciliationFixtures.Item1, EvidenceIds = [ReconciliationFixtures.E1] }] };
        var result = Validate(partial);

        Assert.Equal(ReconciliationOutcome.ValidIncomplete, result.Outcome);
        Assert.Contains(result.Completion!.Gaps, g => g.Code == "evidence_not_mapped" && g.Detail.Contains(ReconciliationFixtures.E2.ToString("D")));
    }

    [Fact]
    public void Candidate_and_known_relation_obligations_need_terminal_dispositions()
    {
        var withRelation = Snapshot with { Relations = [new SnapshotRelation(Guid.Parse("60000000-0000-4000-8000-000000000001"), ReconciliationFixtures.E1, ReconciliationFixtures.E2, EvidenceRelationType.Qualifies)] };
        var valid = ReconciliationFixtures.Valid();
        var candidate = ReconciliationFixtures.Candidate();
        CandidateDisposition D(CandidateState state, string[]? children = null, string? reason = "esito") => new()
        {
            CandidateId = candidate.CandidateId, State = state, ChildCandidateIds = children ?? [],
            ComparisonOutputHashes = state is CandidateState.Compared or CandidateState.Open ? [ReconciliationFixtures.Hash1] : [], Reason = reason,
        };

        SingleCallOutput With(ReconcileCandidate[] candidates, CandidateDisposition[] dispositions) =>
            valid with { Results = [valid.Results[0], valid.Results[1] with { Candidates = candidates, Dispositions = dispositions }, valid.Results[2]] };

        // relazione nota senza alcun candidato terminale: obbligazione diretta aperta
        var none = Validate(valid, withRelation);
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, none.Outcome);
        Assert.Contains(none.Completion!.Gaps, g => g.Code == "known_relation_unresolved");

        // candidato sulla stessa coppia (ordine inverso ammesso) con confronto integrale: completo
        Assert.Equal(ReconciliationOutcome.Complete, Validate(With([candidate], [D(CandidateState.Compared)]), withRelation).Outcome);
        Assert.Equal(ReconciliationOutcome.Complete, Validate(With([candidate], [D(CandidateState.Open)]), withRelation).Outcome);

        // candidato senza disposition, Pending, NotProcessed, Expanded con figli non terminali: non completa
        Assert.Contains(Validate(With([candidate], []), Snapshot).Completion!.Gaps, g => g.Code == "candidate_without_disposition");
        Assert.Contains(Validate(With([candidate], [D(CandidateState.Pending, reason: null)]), Snapshot).Completion!.Gaps, g => g.Code == "candidate_not_terminal");
        var notProcessed = Validate(With([candidate], [D(CandidateState.NotProcessed, reason: "coverage_incomplete")]), Snapshot);
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, notProcessed.Outcome);
        Assert.Contains(notProcessed.Completion!.Gaps, g => g.Code == "candidate_not_terminal");
        var orphanChild = Validate(With([candidate], [D(CandidateState.Expanded, [new string('7', 64)])]), Snapshot);
        Assert.Contains(orphanChild.Completion!.Gaps, g => g.Code == "candidate_not_terminal");
    }

    [Fact]
    public void Expanded_candidate_is_terminal_only_when_all_children_are_terminal()
    {
        var valid = ReconciliationFixtures.Valid();
        var parent = ReconciliationFixtures.Candidate(type: ReconcileProposedType.DependsOn);
        var child = ReconciliationFixtures.Candidate(type: ReconcileProposedType.Qualifies) with { ParentCandidateId = parent.CandidateId };
        CandidateDisposition Disp(ReconcileCandidate c, CandidateState s, params string[] children) => new()
        {
            CandidateId = c.CandidateId, State = s, ChildCandidateIds = [.. children], ComparisonOutputHashes = s == CandidateState.Compared ? [ReconciliationFixtures.Hash2] : [], Reason = "esito",
        };

        SingleCallOutput With(CandidateDisposition childDisposition) =>
            valid with { Results = [valid.Results[0], valid.Results[1] with { Candidates = [parent, child], Dispositions = [Disp(parent, CandidateState.Expanded, child.CandidateId), childDisposition] }, valid.Results[2]] };

        Assert.Equal(ReconciliationOutcome.Complete, Validate(With(Disp(child, CandidateState.Compared))).Outcome);
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, Validate(With(Disp(child, CandidateState.NotProcessed))).Outcome);
    }

    // ---- patch: stessa base, duplicati, stale, invalid ----

    [Fact]
    public void Identical_duplicate_patches_have_one_effect_with_merged_origins()
    {
        var a = ReconciliationFixtures.AddSupport();
        var b = a with { OriginBatchIds = ["batch-2"] };
        var composition = Compose(a, b);

        Assert.Single(composition.Dispositions);
        Assert.Equal(PatchDispositionKind.Applied, composition.Dispositions[0].Kind);
        var effect = Assert.Single(composition.Effects);
        Assert.Equal(["batch-1", "batch-2"], effect.OriginBatchIds);
        Assert.False(composition.HasIntegrityConflict);
    }

    [Fact]
    public void Same_patch_id_with_a_different_payload_is_an_integrity_conflict()
    {
        var a = ReconciliationFixtures.AddSupport();
        var forged = ((Patch)a with { Value = new AddSupportValue { EvidenceIds = [ReconciliationFixtures.E1], SourceIds = [ReconciliationFixtures.S1] }, Target = a.Target with { Id = "r2" } }) with { PatchId = a.PatchId };
        var composition = Compose(a, forged);

        Assert.True(composition.HasIntegrityConflict);
        Assert.Contains(composition.Dispositions, d => d.Kind == PatchDispositionKind.IntegrityConflict);
        Assert.Empty(composition.Effects);

        var result = Validate(WithPatches(a, forged));
        Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);
        Assert.Equal(DocumentWorkException.IntegrityConflict, result.ErrorCode);
        Assert.False(result.IsReusableWorkOutput);
    }

    [Fact]
    public void Patches_are_validated_against_the_same_base_and_stale_ones_are_not_applied_or_rebased()
    {
        var fresh = ReconciliationFixtures.AddSupport();
        var oldBase = Restamp(fresh with { BaseHash = new string('e', 64) });
        var oldTarget = Restamp(fresh with { Target = fresh.Target with { ExpectedHash = new string('e', 64) } });
        var composition = Compose(fresh, oldBase, oldTarget);

        Assert.Equal(PatchDispositionKind.Applied, Kind(composition, fresh));
        Assert.Equal(PatchDispositionKind.Stale, Kind(composition, oldBase));
        Assert.Equal(PatchDispositionKind.Stale, Kind(composition, oldTarget));
        Assert.Single(composition.Effects);
        Assert.DoesNotContain(composition.Effects.SelectMany(e => e.PatchIds), id => id == oldBase.PatchId || id == oldTarget.PatchId);
    }

    [Fact]
    public void Stale_patch_already_carried_by_an_applied_effect_does_not_block_but_a_necessary_one_does()
    {
        var applied = ReconciliationFixtures.AddSupport();
        var redundantStale = Restamp(applied with { BaseHash = new string('e', 64) });
        Assert.Empty(Compose(applied, redundantStale).BlockingPatchIds);

        var necessaryStale = Restamp(ReconciliationFixtures.AddSupport(ReconciliationFixtures.E2, ReconciliationFixtures.S2) with { BaseHash = new string('e', 64) });
        var composition = Compose(applied, necessaryStale);
        Assert.Equal([necessaryStale.PatchId], composition.BlockingPatchIds);

        var result = Validate(WithPatches(applied, necessaryStale));
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, result.Outcome);
        Assert.True(result.IsReusableWorkOutput);
        Assert.Contains(result.Completion!.Gaps, g => g.Code == "patch_blocking" && g.Detail.Contains(necessaryStale.PatchId));
        Assert.Equal(ReconciliationOutcome.Complete, Validate(WithPatches(applied, redundantStale)).Outcome);
    }

    [Fact]
    public void Invalid_patch_is_permanent_and_blocks_completion()
    {
        var outsider = ReconciliationFixtures.AddSupport(Outsider, ReconciliationFixtures.S1);
        var unknownTarget = Restamp(ReconciliationFixtures.AddSupport() with { Target = new PatchTarget { Kind = PatchTargetKind.Item, Id = "sconosciuto", ExpectedHash = ReconciliationFixtures.Hash1 } });
        var composition = Compose(ReconciliationFixtures.AddSupport(), outsider, unknownTarget);

        Assert.Equal(PatchDispositionKind.Invalid, Kind(composition, outsider));
        Assert.Equal(PatchDispositionKind.Invalid, Kind(composition, unknownTarget));
        Assert.Contains(outsider.PatchId, composition.BlockingPatchIds);

        var result = Validate(WithPatches(outsider));
        Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
        Assert.Null(result.Completion);
    }

    // ---- composizione, incompatibilità e permutazioni ----

    [Fact]
    public void AddSupport_on_the_same_target_unions_sources_without_touching_the_base()
    {
        var one = ReconciliationFixtures.AddSupport(ReconciliationFixtures.E1, ReconciliationFixtures.S1);
        var two = ReconciliationFixtures.AddSupport(ReconciliationFixtures.E2, ReconciliationFixtures.S2);
        var composition = Compose(two, one);

        var effect = Assert.Single(composition.Effects);
        Assert.Equal([ReconciliationFixtures.E1, ReconciliationFixtures.E2], effect.EvidenceIds);
        Assert.Equal([ReconciliationFixtures.S1, ReconciliationFixtures.S2], effect.SourceIds);
        Assert.Equal(PatchOperation.AddSupport, effect.Operation);
        Assert.Empty(composition.Conflicts);
    }

    [Fact]
    public void Equivalent_relations_merge_provenance_while_direction_is_preserved()
    {
        var a = Relation(ReconciliationFixtures.E1, ReconciliationFixtures.E2, EvidenceRelationType.DependsOn, "batch-1", "motivo uno");
        var b = Relation(ReconciliationFixtures.E1, ReconciliationFixtures.E2, EvidenceRelationType.DependsOn, "batch-2", "motivo due");
        var reverse = Relation(ReconciliationFixtures.E2, ReconciliationFixtures.E1, EvidenceRelationType.DependsOn);

        var merged = Compose(a, b);
        var effect = Assert.Single(merged.Effects);
        Assert.Equal(["batch-1", "batch-2"], effect.OriginBatchIds);
        Assert.Equal(2, effect.PatchIds.Count);
        Assert.Equal(["motivo due", "motivo uno"], effect.Rationales);

        var directed = Compose(a, reverse);
        Assert.Equal(2, directed.Effects.Count);
        Assert.Empty(directed.Conflicts);
    }

    public static TheoryData<string> IncompatibleCases => new() { "samerule-contradicts", "supersedes-opposed", "corrects-supersedes-opposed", "alternatives" };

    private static Patch[] Incompatible(string name) => name switch
    {
        "samerule-contradicts" =>
        [
            Relation(ReconciliationFixtures.E1, ReconciliationFixtures.E2, EvidenceRelationType.SameRule, "batch-1", "stessa regola"),
            Relation(ReconciliationFixtures.E2, ReconciliationFixtures.E1, EvidenceRelationType.Contradicts, "batch-2", "regole opposte"),
        ],
        "supersedes-opposed" =>
        [
            Relation(ReconciliationFixtures.E1, ReconciliationFixtures.E2, EvidenceRelationType.Supersedes, "batch-1", "E1 sostituisce E2"),
            Relation(ReconciliationFixtures.E2, ReconciliationFixtures.E1, EvidenceRelationType.Supersedes, "batch-2", "E2 sostituisce E1"),
        ],
        "corrects-supersedes-opposed" =>
        [
            Relation(ReconciliationFixtures.E1, ReconciliationFixtures.E2, EvidenceRelationType.Corrects, "batch-1", "E1 corregge E2"),
            Relation(ReconciliationFixtures.E2, ReconciliationFixtures.E1, EvidenceRelationType.Supersedes, "batch-2", "E2 sostituisce E1"),
        ],
        _ => [ReconciliationFixtures.Alternative("limite 8 ordini"), ReconciliationFixtures.Alternative("limite 12 ordini")],
    };

    [Theory]
    [MemberData(nameof(IncompatibleCases))]
    public void Incompatible_patches_become_a_conflict_group_keeping_every_alternative_and_source(string name)
    {
        var patches = Incompatible(name);
        var composition = Compose(patches);

        var group = Assert.Single(composition.Conflicts);
        Assert.Equal(patches.Select(p => p.PatchId).OrderBy(x => x, StringComparer.Ordinal), group.PatchIds);
        Assert.True(group.RequiresPmDecision);
        Assert.All(patches, p => Assert.Equal(PatchDispositionKind.PreservedAsConflict, Kind(composition, p)));
        Assert.Empty(composition.Effects); // base intatta: nessuna vince
        foreach (var patch in patches)
        {
            Assert.All(patch.SourceIds, s => Assert.Contains(s, group.SourceIds));
            Assert.All(patch.EvidenceIds, e => Assert.Contains(e, group.EvidenceIds));
        }

        Assert.Empty(composition.BlockingPatchIds); // PreservedAsConflict integrale non impedisce il completamento

        // I qualificatori/proposte restano tutti rappresentati.
        Assert.Equal(patches.Length, group.Proposals.Count);
        Assert.Equal(patches.Select(p => p.PatchId).OrderBy(x => x, StringComparer.Ordinal), group.Proposals.Select(x => x.PatchId));
    }

    [Theory]
    [MemberData(nameof(IncompatibleCases))]
    public void Permuting_patch_order_keeps_the_same_composition_hash_and_conflict(string name)
    {
        var patches = Incompatible(name);
        var extra = ReconciliationFixtures.AddSupport();
        var forward = Compose([.. patches, extra]);
        var backward = Compose([extra, .. patches.Reverse()]);

        Assert.Equal(forward.Hash, backward.Hash);
        Assert.Equal(forward.Conflicts.Single().GroupId, backward.Conflicts.Single().GroupId);
        Assert.Equal(forward.Effects.Count, backward.Effects.Count);
    }

    [Fact]
    public void Single_alternative_alone_is_applied_and_compatible_relations_are_not_flagged()
    {
        var alt = ReconciliationFixtures.Alternative();
        var sameRule = Relation(ReconciliationFixtures.E1, ReconciliationFixtures.E2, EvidenceRelationType.SameRule);
        var qualifies = Relation(ReconciliationFixtures.E1, ReconciliationFixtures.E2, EvidenceRelationType.Qualifies);
        var composition = Compose(alt, sameRule, qualifies);

        Assert.Empty(composition.Conflicts);
        Assert.All(new[] { alt, sameRule, qualifies }, p => Assert.Equal(PatchDispositionKind.Applied, Kind(composition, p)));
        Assert.Equal(3, composition.Effects.Count);
    }

    [Fact]
    public void Conflict_patches_in_a_valid_output_still_complete_when_fully_preserved()
    {
        var result = Validate(WithPatches(Incompatible("samerule-contradicts")));
        Assert.Equal(ReconciliationOutcome.Complete, result.Outcome);
        Assert.Single(result.Patches!.Conflicts);
    }

    [Fact]
    public void Structural_completion_does_not_certify_semantic_quality()
    {
        // Visite al 100% e mappa completa: la completezza è strutturale. Un gold semanticamente perso (es. nessun candidato sul conflitto reale)
        // resta un FAIL separato del gate di qualità: qui il validatore lo dichiara esplicitamente nel report.
        var result = Validate(ReconciliationFixtures.Valid());
        Assert.True(result.Completion!.IsComplete);
        Assert.False(result.Completion.SemanticQualityCertified);
    }

    [Fact]
    public void Ten_thousand_distinct_patches_compose_quickly_and_deterministically()
    {
        var targets = Enumerable.Range(0, 10_000).ToDictionary(i => ReconciliationBase.TargetKey(PatchTargetKind.Item, $"r{i}"), _ => ReconciliationFixtures.Hash1);
        var baseState = new ReconciliationBase(ReconciliationFixtures.BaseHash, targets);
        var patches = Enumerable.Range(0, 10_000).Select(i => ReconciliationFixtures.WithId(ReconciliationFixtures.AddSupport() with { Target = new PatchTarget { Kind = PatchTargetKind.Item, Id = $"r{i}", ExpectedHash = ReconciliationFixtures.Hash1 } })).ToArray();

        var watch = Stopwatch.StartNew();
        var forward = ReconciliationPatchComposer.Compose(patches, baseState, Snapshot);
        var backward = ReconciliationPatchComposer.Compose([.. patches.Reverse()], baseState, Snapshot);
        watch.Stop();

        Assert.Equal(10_000, forward.Effects.Count);
        Assert.Equal(forward.Hash, backward.Hash);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"troppo lento: {watch.Elapsed}");
    }

    [Fact]
    public void Empty_patch_list_composes_to_an_empty_stable_result()
    {
        var a = Compose();
        var b = Compose();
        Assert.Empty(a.Effects);
        Assert.Empty(a.Dispositions);
        Assert.Empty(a.Conflicts);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Throws<ArgumentNullException>(() => ReconciliationPatchComposer.Compose(null!, Base(), Snapshot));
    }
}
