using bProjects.App.AI;
using bProjects.App.Evidence;
using Cx = bProjects.IntegrationTests.ReconciliationCandidateTests;

namespace bProjects.IntegrationTests;

/// <summary>
/// T075 (DET) — round finale bounded (AI §4.3) con modello FINTO: merge delle chiavi emerse, espansione e confronto dei pendenti una volta per coppia, un solo
/// round (nuovo lavoro → reconciliation_round_limit), limiti di chiamate (retry inclusi), candidati, byte e deadline e stallo → coverage_incomplete con frontiera.
/// Il replay non conta avanzamento e non c'è alcun ciclo fino a convergenza.
/// </summary>
public sealed class ReconciliationLimitsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InferredKey Alias(int evidence, string value) => new(Cx.Id(evidence), new CatalogKey
    {
        View = ReconcileView.A, Kind = CatalogKeyKind.Alias, Value = value, Origin = CatalogKeyOrigin.Inferred, OriginOutputHash = new string('9', 64),
    });

    /// <summary>Alias inferiti che uniscono evidenze di foglie lontane: due gruppi nuovi.</summary>
    private static InferredKey[] Emerged() => [Alias(10, "intesa x"), Alias(21, "intesa x"), Alias(12, "intesa y"), Alias(23, "intesa y")];

    private static async Task<(Cx.Rig Rig, CandidateRun Phases)> PhasesAsync(Cx.Model model, ReconciliationLimits? limits = null, CallBudget? calls = null, TimeProvider? time = null,
        PackMeasurer<CatalogEntry>? measurer = null)
    {
        var rig = await Cx.SetupAsync(model, limits: limits, calls: calls, time: time, measurer: measurer);
        return (rig, await rig.Engine.RunPhasesAsync(Ct));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ------------------------------------------------------------------ feedback e cache

    [Fact]
    public async Task Emerged_aliases_create_new_pairs_that_the_final_round_expands_and_closes_once_and_duplicates_add_no_ai()
    {
        var (rig, phases) = await PhasesAsync(new Cx.Model());
        var callsBefore = rig.Model.Calls.Count;
        var existing = phases.Candidates.Select(c => c.CandidateId).ToHashSet();

        var result = await ReconciliationFinalPass.RunAsync(rig.Engine, rig.Snapshot, rig.Directory, Emerged(), Ct);

        Assert.Equal(FinalPassOutcome.Complete, result.Outcome);
        Assert.Null(result.Code);
        Assert.Empty(result.Frontier);
        var added = result.Run.Candidates.Where(c => !existing.Contains(c.CandidateId)).ToList();
        Assert.Equal(2, added.Count);   // un candidato per gruppo emerso, mai k² coppie
        Assert.All(added, c => Assert.Equal(CandidateState.Compared, result.Run.Dispositions.Single(d => d.CandidateId == c.CandidateId).State));
        Assert.True(rig.Model.Calls.Count > callsBefore);
        Assert.True(result.Progress);
        Assert.Empty(result.Run.Unresolved);

        // Un alias identico a un gruppo già coperto (stessa chiave E1/E30) non genera lavoro nuovo.
        var (rig2, _) = await PhasesAsync(new Cx.Model());
        var before = rig2.Model.Calls.Count;
        var duplicate = await ReconciliationFinalPass.RunAsync(rig2.Engine, rig2.Snapshot, rig2.Directory, [Alias(1, "regola"), Alias(30, "regola")], Ct);
        Assert.Equal(before, rig2.Model.Calls.Count);
        Assert.Equal(FinalPassOutcome.Complete, duplicate.Outcome);
    }

    [Fact]
    public async Task Replay_of_the_final_round_is_not_progress_and_never_iterates_to_convergence()
    {
        var (rig, _) = await PhasesAsync(new Cx.Model());
        var first = await ReconciliationFinalPass.RunAsync(rig.Engine, rig.Snapshot, rig.Directory, Emerged(), Ct);
        var calls = rig.Model.Calls.Count;
        var used = rig.Engine.Calls.Used;

        var again = await ReconciliationFinalPass.RunAsync(rig.Engine, rig.Snapshot, rig.Directory, Emerged(), Ct);

        Assert.Same(first, again);
        Assert.Equal((calls, used), (rig.Model.Calls.Count, rig.Engine.Calls.Used));
    }

    // ------------------------------------------------------------------ un solo round

    [Fact]
    public async Task New_work_that_the_final_round_itself_discovers_is_kept_not_processed_round_limit_and_never_iterated()
    {
        var created = 0;
        var model = new Cx.Model
        {
            Compare = (q, r) =>
            {
                var focus = q.Focus!.Single();
                var n = 5 + 2 * created++;   // ogni confronto fa emergere una coppia nuova e distinta
                return r with
                {
                    Dispositions = [new CandidateDisposition { CandidateId = focus.CandidateId, State = CandidateState.Compared, ChildCandidateIds = [], ComparisonOutputHashes = [q.Batch.InputHash], Reason = null }],
                    Candidates = [Cx.Link(Cx.Id(n), Cx.Id(n + 1), focus.CandidateId, q.Batch.BatchId, ReconcileProposedType.Confirms)], NoFindingsReason = null,
                };
            },
        };
        var (rig, phases) = await PhasesAsync(model);
        var pending = rig.Engine.Pending;
        Assert.Single(pending);   // il confronto di fase ha lasciato il collegamento emerso Pending
        var compares = rig.Model.Calls.Count(c => c.Batch.Stage == "compare");

        var result = await ReconciliationFinalPass.RunAsync(rig.Engine, rig.Snapshot, rig.Directory, [], Ct);

        Assert.Equal((FinalPassOutcome.RoundLimit, DocumentWorkException.ReconciliationRoundLimit), (result.Outcome, result.Code));
        Assert.Equal(compares + 1, rig.Model.Calls.Count(c => c.Batch.Stage == "compare"));   // il pendente è confrontato una volta; il nuovo non viene inseguito
        var frontier = Assert.Single(result.Frontier);
        Assert.Equal(ReconciliationFinalPass.RoundLimitReason, frontier.Reason);
        var d = result.Run.Dispositions.Single(x => x.CandidateId == frontier.CandidateId);
        Assert.Equal(CandidateState.NotProcessed, d.State);
        Assert.Empty(rig.Engine.Pending);
        Assert.Contains(frontier.CandidateId, result.Run.Unresolved);
        _ = phases;
    }

    // ------------------------------------------------------------------ limiti globali

    [Fact]
    public async Task The_call_limit_includes_retries_and_stops_the_round_with_an_explicit_frontier()
    {
        var probe = await PhasesAsync(new Cx.Model());
        var used = probe.Rig.Engine.Calls.Used;
        var budget = new CallBudget();
        var model = new Cx.Model();
        var limits = new ReconciliationLimits(MaxCalls: used + 3);
        var (rig, _) = await PhasesAsync(model, limits, budget);
        Assert.Equal(used, budget.Used);

        var result = await ReconciliationFinalPass.RunAsync(rig.Engine, rig.Snapshot, rig.Directory, Emerged(), Ct);

        Assert.Equal((FinalPassOutcome.CoverageIncomplete, DocumentWorkException.CoverageIncomplete), (result.Outcome, result.Code));
        Assert.StartsWith(ReconciliationCandidates.LimitReached, result.Reason);
        Assert.Equal(used + 3, budget.Used);   // esattamente il massimo, mai oltre
        Assert.NotEmpty(result.Frontier);
        Assert.All(result.Frontier, f => Assert.StartsWith(ReconciliationCandidates.LimitReached, f.Reason));
        Assert.Empty(rig.Engine.Pending);   // nessun pendente dimenticato: tutti terminali espliciti, nessun pruning silenzioso

        // Un retry dichiarato dal port consuma il budget come una chiamata: con un retry per invio il massimo si raggiunge prima.
        var retryBudget = new CallBudget();
        var retrying = new Cx.Model { OnCall = q => { if (q.Batch.Stage is "expand" or "compare") { retryBudget.Add(); } } };
        var (rig2, _) = await PhasesAsync(retrying, new ReconciliationLimits(MaxCalls: 2 * used + 4), retryBudget);
        var r2 = await ReconciliationFinalPass.RunAsync(rig2.Engine, rig2.Snapshot, rig2.Directory, Emerged(), Ct);
        Assert.Equal(FinalPassOutcome.CoverageIncomplete, r2.Outcome);
        Assert.True(retryBudget.Used >= 2 * used + 4);
        Assert.True(retryBudget.Used <= 2 * used + 4 + 1);   // al più il retry dell'invio in corso
    }

    [Fact]
    public async Task Candidate_byte_and_deadline_limits_end_with_coverage_incomplete_and_the_pending_work_as_frontier()
    {
        var probe = await PhasesAsync(new Cx.Model());
        var bytes = probe.Rig.Engine.Calls.DerivedBytes;
        var (byteRig, _) = await PhasesAsync(new Cx.Model(), new ReconciliationLimits(MaxDerivedBytes: bytes + 1));
        var byByte = await ReconciliationFinalPass.RunAsync(byteRig.Engine, byteRig.Snapshot, byteRig.Directory, Emerged(), Ct);
        Assert.Equal(FinalPassOutcome.CoverageIncomplete, byByte.Outcome);
        Assert.Contains("byte", byByte.Reason);
        Assert.NotEmpty(byByte.Frontier);

        var count = probe.Phases.Candidates.Count;
        var (countRig, _) = await PhasesAsync(new Cx.Model(), new ReconciliationLimits(MaxCandidates: count));
        var byCount = await ReconciliationFinalPass.RunAsync(countRig.Engine, countRig.Snapshot, countRig.Directory, Emerged(), Ct);
        Assert.Equal(FinalPassOutcome.CoverageIncomplete, byCount.Outcome);
        Assert.Contains("candidati", byCount.Reason);
        Assert.Equal(count, byCount.Run.Candidates.Count);   // oltre il limite non si aggiunge né si scarta nulla

        var (timeRig, _) = await PhasesAsync(new Cx.Model(), new ReconciliationLimits(Deadline: DateTimeOffset.UtcNow.AddDays(1)), time: new FixedTime(DateTimeOffset.UtcNow));
        var before = timeRig.Model.Calls.Count;
        var late = new ReconciliationLimits(Deadline: DateTimeOffset.UtcNow.AddDays(-1));
        var (lateRig, _) = await PhasesAsyncWithLateDeadline(late);
        var byTime = await ReconciliationFinalPass.RunAsync(lateRig.Engine, lateRig.Snapshot, lateRig.Directory, Emerged(), Ct);
        Assert.Equal(FinalPassOutcome.CoverageIncomplete, byTime.Outcome);
        Assert.Contains("deadline", byTime.Reason);
        Assert.Equal(before, timeRig.Model.Calls.Count);
    }

    /// <summary>Le fasi A/B/Cross girano con una deadline lontana, poi il round finale trova la deadline scaduta (orologio che avanza).</summary>
    private static async Task<(Cx.Rig Rig, CandidateRun Phases)> PhasesAsyncWithLateDeadline(ReconciliationLimits late)
    {
        var clock = new MutableTime(late.Deadline!.Value.AddDays(-1));
        var result = await PhasesAsync(new Cx.Model(), late, time: clock);
        clock.Now = late.Deadline.Value.AddSeconds(1);
        return result;
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ------------------------------------------------------------------ stallo

    [Fact]
    public async Task Nothing_pending_but_unresolved_candidates_is_a_stall_with_the_frontier_and_no_ai()
    {
        PackMeasurer<CatalogEntry> strict = (members, ct) => members.Count >= 2
            ? new ValueTask<UnitMeasure>(new UnitMeasure(1000, TokenCountQuality.Measured, Cx.Sha("big"), members.Count))
            : Cx.Leaf(members, ct);
        var (rig, phases) = await PhasesAsync(new Cx.Model(), measurer: strict);
        Assert.NotEmpty(phases.Unresolved);
        var calls = rig.Model.Calls.Count;

        var result = await ReconciliationFinalPass.RunAsync(rig.Engine, rig.Snapshot, rig.Directory, [], Ct);

        Assert.Equal((FinalPassOutcome.CoverageIncomplete, DocumentWorkException.CoverageIncomplete), (result.Outcome, result.Code));
        Assert.StartsWith("stallo", result.Reason);
        Assert.False(result.Progress);
        Assert.Equal(calls, rig.Model.Calls.Count);
        Assert.Contains(result.Frontier, f => f.Reason.StartsWith(ReconciliationCandidates.PairDoesNotFit, StringComparison.Ordinal));
        Assert.Equal(phases.Unresolved, result.Frontier.Select(f => f.CandidateId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_invalid_emerged_key_is_a_permanent_input_error_never_silently_ignored()
    {
        var (rig, _) = await PhasesAsync(new Cx.Model());
        var bad = new InferredKey(Cx.Id(3), new CatalogKey { View = ReconcileView.A, Kind = CatalogKeyKind.Alias, Value = "x", Origin = CatalogKeyOrigin.Inferred, OriginOutputHash = null });

        var ex = await Assert.ThrowsAsync<DocumentWorkException>(async () => await ReconciliationFinalPass.RunAsync(rig.Engine, rig.Snapshot, rig.Directory, [bad], Ct));

        Assert.Equal(DocumentWorkException.InvalidInput, ex.Code);
    }
}
