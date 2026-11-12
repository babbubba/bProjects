using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003, T040 dopo il Run 4 (DET, modello FINTO) — espansione con membership della definizione v10 (algoritmo expansion-grid-1): griglia deterministica di blocchi con ogni coppia rilevante co-locata,
/// membership dichiarata in ogni richiesta, saturazione fail-closed (precedenza su «non risolto»), nessuna falsa completezza, precheck deterministico del budget di chiamate, ripresa dopo interruzione.
/// Il motore storico (senza policy) è coperto da <see cref="ReconciliationCandidateTests"/> e non cambia.
/// </summary>
public sealed class ReconciliationExpansionV10Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReconciliationContext Context => new("owner", "manifest", "project-analysis-documents-v10", new PlannerLimits(20, 1_000_000));

    /// <summary>Capacità 2 voci per blocco (10 + 5 token per voce, budget 20): la griglia dell'insieme {E1,E2} x {E29,E30} è di 4 blocchi da 2.</summary>
    private static ExpansionMeasurer Measure(Func<int, int>? tokens = null) => (candidate, view, groups, members, _) => new ValueTask<UnitMeasure>(new UnitMeasure(
        (tokens ?? (n => 10 + 5 * n))(members.Count), TokenCountQuality.Measured,
        ReconciliationCandidateTests.Sha("exp|" + candidate.CandidateId + "|" + view + "|" + string.Join(',', members.Select(m => m.EntryId + ":" + groups.Tags[m.EvidenceId]))), members.Count));

    private static ExpansionPolicy Policy(int cap = 2, int limit = 100_000, int reserved = 1) => new(cap, limit, reserved);

    private static Guid Id(int n) => ReconciliationCandidateTests.Id(n);

    private static ReconcileCandidate Link(Guid left, Guid right, ReconcileBatchRequest q) =>
        ReconciliationCandidateTests.Link(left, right, q.Focus!.Single().CandidateId, q.Batch.BatchId);

    private static ReconcileResult Unresolved(ReconcileBatchRequest q, ReconcileResult r) => r with
    {
        Dispositions =
        [
            new CandidateDisposition
            {
                CandidateId = q.Focus!.Single().CandidateId, State = CandidateState.NotProcessed, ChildCandidateIds = [], ComparisonOutputHashes = [], Reason = ReconciliationCandidates.CandidateUnresolved,
            },
        ],
    };

    private static async Task<(ReconciliationCandidateTests.Rig Rig, ReconcileCandidate Group)> GroupRigAsync(
        ReconciliationCandidateTests.Model model, ExpansionPolicy? policy = null, ExpansionMeasurer? measurer = null)
    {
        var rig = await ReconciliationCandidateTests.SetupAsync(model, expansion: policy ?? Policy(), expansionMeasurer: measurer ?? Measure(), context: Context);
        var group = Assert.Single(rig.Directory.Candidates, c => c.Rationale == ReconciliationDirectory.SharedKeyRationale && rig.Directory.Sets.Single(s => s.SetId == c.Left.Id).View == ReconcileView.A);
        return (rig, group);
    }

    private static List<ReconcileBatchRequest> ExpandCalls(ReconciliationCandidateTests.Rig rig, ReconcileCandidate group) =>
        [.. rig.Model.Calls.Where(c => c.Batch.Stage == "expand" && c.Focus!.Single().CandidateId == group.CandidateId)];

    // ------------------------------------------------------------------ copertura e membership

    [Fact]
    public async Task Every_relevant_pair_of_the_two_groups_is_colocated_in_a_block_and_the_candidate_is_compared_only_with_all_blocks_complete()
    {
        var (rig, group) = await GroupRigAsync(new ReconciliationCandidateTests.Model { Expand = (_, r) => r });

        var run = await rig.Engine.RunPhasesAsync(Ct);

        var calls = ExpandCalls(rig, group);
        Assert.Equal(4, calls.Count);                                       // {E1,E2} x {E29,E30} con capacità 2: quattro blocchi da due voci
        foreach (var left in new[] { 1, 2 })
        {
            foreach (var right in new[] { 29, 30 })
            {
                Assert.Contains(calls, c => c.Entries.Any(e => e.EvidenceId == Id(left)) && c.Entries.Any(e => e.EvidenceId == Id(right)));
            }
        }

        Assert.Equal([0, 1, 2, 3], calls.Select(c => c.Batch.Ordinal));      // ordinali locali 0..n-1, nell'ordine deterministico della griglia
        Assert.All(calls, c => Assert.Equal(0, c.Level));
        Assert.All(calls, c => Assert.Empty(c.Batch.ParentOutputHashes));
        Assert.Equal(4, calls.Select(c => c.Batch.BatchId).Distinct().Count());
        var disposition = run.Dispositions.Single(d => d.CandidateId == group.CandidateId);
        Assert.Equal((CandidateState.Compared, ReconciliationCandidates.NoCandidateAfterFullScan), (disposition.State, disposition.Reason));
        Assert.Equal(4, disposition.ComparisonOutputHashes.Count);          // l'hash di OGNI output è nella disposition
        Assert.DoesNotContain(group.CandidateId, run.Unresolved);
    }

    [Fact]
    public async Task Every_request_declares_the_membership_of_each_entry_over_the_whole_groups_and_orders_entries_by_group()
    {
        var (rig, group) = await GroupRigAsync(new ReconciliationCandidateTests.Model { Expand = (_, r) => r });

        await rig.Engine.RunPhasesAsync(Ct);

        var calls = ExpandCalls(rig, group);
        Assert.All(calls, c => Assert.NotNull(c.Groups));
        foreach (var call in calls)
        {
            var tags = call.Groups!.Tags;
            Assert.Equal(ExpansionTag.Left, tags[Id(1)]);
            Assert.Equal(ExpansionTag.Left, tags[Id(2)]);
            Assert.Equal(ExpansionTag.Right, tags[Id(29)]);
            Assert.Equal(ExpansionTag.Right, tags[Id(30)]);
            var order = call.Entries.Select(e => ReconciliationExpansionGrid.OrderTag(tags[e.EvidenceId])).ToList();
            Assert.Equal(order.Order(), order);                              // solo-sinistro, poi entrambi, poi solo-destro
        }
    }

    [Fact]
    public async Task Groups_that_fit_in_one_batch_produce_a_single_block_with_every_entry()
    {
        var (rig, group) = await GroupRigAsync(new ReconciliationCandidateTests.Model { Expand = (_, r) => r }, measurer: Measure(n => 10));

        await rig.Engine.RunPhasesAsync(Ct);

        var call = Assert.Single(ExpandCalls(rig, group));
        Assert.Equal([Id(1), Id(2), Id(29), Id(30)], call.Entries.Select(e => e.EvidenceId).Order());
    }

    [Fact]
    public async Task Overlapping_groups_are_covered_pair_by_pair_with_both_tags_and_no_pair_missing()
    {
        var model = new ReconciliationCandidateTests.Model
        {
            Hierarchy = (q, r) => q.View == ReconcileView.Cross ? r with { Candidates = [CrossNodeCandidate(q)], NoFindingsReason = null } : r,
            Expand = (_, r) => r,
        };
        var rig = await ReconciliationCandidateTests.SetupAsync(model, expansion: Policy(limit: 1_000_000), expansionMeasurer: Measure(), context: Context);
        var cross = Assert.Single(rig.Hierarchy.Outcomes.Single(o => o.Result.View == ReconcileView.Cross).Result.Candidates);

        await rig.Engine.RunPhasesAsync(Ct);

        var calls = rig.Model.Calls.Where(c => c.Batch.Stage == "expand" && c.Focus!.Single().CandidateId == cross.CandidateId).ToList();
        Assert.All(calls, c => Assert.All(c.Entries, e => Assert.Equal(ExpansionTag.Both, c.Groups!.Tags[e.EvidenceId])));   // le due radici contengono tutte le evidenze
        var pairs = calls.Select(c => string.Join('+', c.Entries.Select(e => e.EvidenceId.ToString("D")).Order(StringComparer.Ordinal))).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(30 * 29 / 2, pairs.Count);                             // tutte le coppie non ordinate di evidenze distinte, nessuna x=x
        Assert.Equal(30 * 29 / 2, calls.Count);
        Assert.All(calls, c => Assert.Equal(2, c.Entries.Count));
    }

    private static ReconcileCandidate CrossNodeCandidate(ReconcileBatchRequest q)
    {
        var l = new CandidateEndpoint { Kind = CandidateEndpointKind.Node, Id = q.Nodes[0].NodeId, Hash = q.Nodes[0].MemberSetHash };
        var r = new CandidateEndpoint { Kind = CandidateEndpointKind.Node, Id = q.Nodes[1].NodeId, Hash = q.Nodes[1].MemberSetHash };
        return new()
        {
            CandidateId = ReconciliationIds.CandidateId(CandidateKind.Relation, l, r, ReconcileProposedType.SameRule, ReconciliationFixtures.SourceHash), Kind = CandidateKind.Relation, Left = l,
            Right = r, ProposedType = ReconcileProposedType.SameRule, Rationale = "raccordo fra le radici", Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = [q.Batch.BatchId], ParentCandidateId = null,
        };
    }

    // ------------------------------------------------------------------ saturazione, non risolto, nessuna falsa completezza

    [Fact]
    public async Task A_saturated_block_keeps_its_valid_children_but_the_parent_is_never_expanded_or_compared()
    {
        var model = new ReconciliationCandidateTests.Model
        {
            Expand = (q, r) => q.Batch.Ordinal == 0
                ? r with { Candidates = [Link(Id(1), Id(29), q), Link(Id(1), Id(30), q)], NoFindingsReason = null }
                : r,
        };
        var (rig, group) = await GroupRigAsync(model, Policy(cap: 2));

        var run = await rig.Engine.RunPhasesAsync(Ct);

        var disposition = run.Dispositions.Single(d => d.CandidateId == group.CandidateId);
        Assert.Equal(CandidateState.NotProcessed, disposition.State);
        Assert.StartsWith(ReconciliationCandidates.ExpansionSaturated, disposition.Reason, StringComparison.Ordinal);
        Assert.Equal(2, disposition.ChildCandidateIds.Count);               // i figli validi restano conservati
        Assert.Equal(4, disposition.ComparisonOutputHashes.Count);          // anche l'hash dei blocchi saturi
        Assert.Contains(group.CandidateId, run.Unresolved);                 // il genitore non è terminale: nessuna falsa completezza
        Assert.All(disposition.ChildCandidateIds, id => Assert.Contains(run.Dispositions, d => d.CandidateId == id));
        Assert.Equal(4, ExpandCalls(rig, group).Count);                     // si proseguono tutti i blocchi dopo quello saturo
    }

    [Fact]
    public async Task One_candidate_below_the_cap_is_not_saturation_and_the_parent_is_expanded()
    {
        var model = new ReconciliationCandidateTests.Model
        {
            Expand = (q, r) => q.Batch.Ordinal == 0 ? r with { Candidates = [Link(Id(1), Id(29), q)], NoFindingsReason = null } : r,
        };
        var (rig, group) = await GroupRigAsync(model, Policy(cap: 2));

        var run = await rig.Engine.RunPhasesAsync(Ct);

        Assert.Equal(CandidateState.Expanded, run.Dispositions.Single(d => d.CandidateId == group.CandidateId).State);
    }

    [Fact]
    public async Task An_unresolved_block_leaves_the_parent_not_processed_and_saturation_takes_precedence_over_it()
    {
        var unresolvedOnly = new ReconciliationCandidateTests.Model { Expand = (q, r) => q.Batch.Ordinal == 1 ? Unresolved(q, r) : r };
        var (rig, group) = await GroupRigAsync(unresolvedOnly);
        var run = await rig.Engine.RunPhasesAsync(Ct);
        var disposition = run.Dispositions.Single(d => d.CandidateId == group.CandidateId);
        Assert.Equal((CandidateState.NotProcessed, ReconciliationCandidates.CandidateUnresolved), (disposition.State, disposition.Reason));

        var both = new ReconciliationCandidateTests.Model
        {
            Expand = (q, r) => q.Batch.Ordinal switch
            {
                0 => r with { Candidates = [Link(Id(1), Id(29), q), Link(Id(1), Id(30), q)], NoFindingsReason = null },
                1 => Unresolved(q, r),
                _ => r,
            },
        };
        var (rig2, group2) = await GroupRigAsync(both, Policy(cap: 2));
        var run2 = await rig2.Engine.RunPhasesAsync(Ct);
        Assert.StartsWith(ReconciliationCandidates.ExpansionSaturated, run2.Dispositions.Single(d => d.CandidateId == group2.CandidateId).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pair_that_does_not_fit_even_alone_leaves_the_candidate_not_processed_before_any_call()
    {
        var (rig, group) = await GroupRigAsync(new ReconciliationCandidateTests.Model(), measurer: Measure(n => n >= 2 ? 1000 : 10));

        var run = await rig.Engine.RunPhasesAsync(Ct);

        var disposition = run.Dispositions.Single(d => d.CandidateId == group.CandidateId);
        Assert.Equal(CandidateState.NotProcessed, disposition.State);
        Assert.StartsWith(ReconciliationCandidates.PairDoesNotFit, disposition.Reason, StringComparison.Ordinal);
        Assert.Empty(ExpandCalls(rig, group));
    }

    [Fact]
    public async Task An_unavailable_tokenizer_is_a_runtime_error_never_a_pair_that_does_not_fit()
    {
        ExpansionMeasurer unavailable = (_, _, _, members, _) => new ValueTask<UnitMeasure>(new UnitMeasure(0, TokenCountQuality.Unavailable, "0", members.Count));
        var (rig, _) = await GroupRigAsync(new ReconciliationCandidateTests.Model(), measurer: unavailable);

        await Assert.ThrowsAsync<RuntimeUnverifiedException>(async () => await rig.Engine.RunPhasesAsync(Ct));
        Assert.DoesNotContain(rig.Model.Calls, c => c.Batch.Stage == "expand");
    }

    // ------------------------------------------------------------------ precheck del budget di chiamate

    [Fact]
    public async Task The_budget_precheck_is_deterministic_fail_closed_and_spends_no_call_when_the_worst_case_does_not_fit()
    {
        // 4 blocchi x (1 espansione + 2 confronti dei figli) = 12 chiamate nel caso peggiore; il tetto del run deve coprirle oltre a gerarchia e riserva.
        var probe = await ReconciliationCandidateTests.SetupAsync(new ReconciliationCandidateTests.Model(), context: Context);
        var hierarchyCalls = probe.Hierarchy.Outcomes.Count;
        const int reserved = 1;

        var (tooSmall, group) = await GroupRigAsync(new ReconciliationCandidateTests.Model(), Policy(cap: 2, limit: hierarchyCalls + reserved + 11, reserved: reserved));
        var run = await tooSmall.Engine.RunPhasesAsync(Ct);
        var refused = run.Dispositions.Single(d => d.CandidateId == group.CandidateId);
        Assert.Equal(CandidateState.NotProcessed, refused.State);
        Assert.StartsWith(ReconciliationCandidates.ExpansionBudgetExceeded, refused.Reason, StringComparison.Ordinal);
        Assert.Contains("servono fino a 12", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("ne restano 11", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(ExpandCalls(tooSmall, group));                          // nessuna chiamata spesa per questo candidato
        Assert.Empty(refused.ChildCandidateIds);

        var (exact, group2) = await GroupRigAsync(new ReconciliationCandidateTests.Model { Expand = (_, r) => r }, Policy(cap: 2, limit: hierarchyCalls + reserved + 12, reserved: reserved));
        var run2 = await exact.Engine.RunPhasesAsync(Ct);
        Assert.Equal(CandidateState.Compared, run2.Dispositions.Single(d => d.CandidateId == group2.CandidateId).State);
        Assert.Equal(4, ExpandCalls(exact, group2).Count);
    }

    [Fact]
    public async Task The_budget_decision_counts_the_compare_calls_already_owed_to_pending_candidates()
    {
        var probe = await ReconciliationCandidateTests.SetupAsync(new ReconciliationCandidateTests.Model(), context: Context);
        var hierarchyCalls = probe.Hierarchy.Outcomes.Count;
        // Una relazione nota Evidence-Evidence resta Pending finché la sua fase non la confronta: è un confronto già dovuto che l'espansione dei gruppi deve contare.
        var relation = new SnapshotRelation(Guid.Parse("60000000-0000-4000-8000-000000000001"), Id(5), Id(6), EvidenceRelationType.Confirms, EvidenceRelationBasis.Declared);
        var rig = await ReconciliationCandidateTests.SetupAsync(
            new ReconciliationCandidateTests.Model { Expand = (_, r) => r }, relations: [relation], expansion: Policy(cap: 2, limit: hierarchyCalls + 1 + 12, reserved: 1), expansionMeasurer: Measure(), context: Context);
        var group = Assert.Single(rig.Directory.Candidates, c => c.Rationale == ReconciliationDirectory.SharedKeyRationale && rig.Directory.Sets.Single(s => s.SetId == c.Left.Id).View == ReconcileView.A);

        var run = await rig.Engine.RunPhasesAsync(Ct);

        // Il tetto basterebbe da solo (12 chiamate), ma almeno un confronto è già stato consumato o è dovuto: la stima conservativa rifiuta, senza spendere chiamate per l'espansione.
        var disposition = run.Dispositions.Single(d => d.CandidateId == group.CandidateId);
        Assert.StartsWith(ReconciliationCandidates.ExpansionBudgetExceeded, disposition.Reason, StringComparison.Ordinal);
        Assert.Empty(ExpandCalls(rig, group));
    }

    // ------------------------------------------------------------------ determinismo, replay e ripresa

    private static string Signature(ReconcileBatchRequest c) => string.Join(
        "|", c.Batch.Stage, c.Batch.BatchId, c.Batch.InputHash, c.Batch.Ordinal, string.Join(',', c.Entries.Select(e => e.EntryId)), c.Groups is null ? "-" : string.Join(',', c.Entries.Select(e => c.Groups.Tags[e.EvidenceId])));

    [Fact]
    public async Task The_block_sequence_and_the_decisions_are_a_pure_function_of_the_data_so_a_resumed_run_replays_the_same_batches()
    {
        static ReconciliationCandidateTests.Model NewModel() => new()
        {
            Expand = (q, r) => q.Batch.Ordinal == 0 ? r with { Candidates = [Link(Id(1), Id(29), q)], NoFindingsReason = null } : r,
        };

        var (first, group1) = await GroupRigAsync(NewModel());
        var run1 = await first.Engine.RunPhasesAsync(Ct);
        var (second, group2) = await GroupRigAsync(NewModel());
        var run2 = await second.Engine.RunPhasesAsync(Ct);

        Assert.Equal(group1.CandidateId, group2.CandidateId);
        Assert.Equal(first.Model.Calls.Select(Signature), second.Model.Calls.Select(Signature));   // stessa sequenza di batch, stessi id, stessi hash di payload, stesse membership
        Assert.Equal(run1.Dispositions.Select(d => (d.CandidateId, d.State, d.Reason)), run2.Dispositions.Select(d => (d.CandidateId, d.State, d.Reason)));
        Assert.Equal(run1.ProcessingOrder, run2.ProcessingOrder);
    }

    [Fact]
    public async Task The_legacy_expansion_is_untouched_when_no_policy_is_given()
    {
        var rig = await ReconciliationCandidateTests.SetupAsync(new ReconciliationCandidateTests.Model());

        var run = await rig.Engine.RunPhasesAsync(Ct);

        Assert.All(rig.Model.Calls.Where(c => c.Batch.Stage == "expand"), c => Assert.Null(c.Groups));   // nessuna membership nelle definizioni v4–v9
        Assert.Empty(run.Unresolved);
    }

    [Fact]
    public async Task A_policy_and_its_measurer_must_be_given_together()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ReconciliationCandidateTests.SetupAsync(new ReconciliationCandidateTests.Model(), expansion: Policy()));
        await Assert.ThrowsAsync<ArgumentException>(() => ReconciliationCandidateTests.SetupAsync(new ReconciliationCandidateTests.Model(), expansionMeasurer: Measure()));
    }
}
