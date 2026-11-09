using bProjects.App.AI;
using bProjects.App.Evidence;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>003 T025 (DET): composizione a budget del pacchetto: confini del budget, passaggio più lungo del budget, esclusi elencati con motivo, finestra N_rerank, costi sempre misurati.</summary>
public sealed class CandidatePackerTests
{
    private static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static RegistryBudget Budget(long limit, params (int Evidence, long Cost)[] costs) => new(limit, costs.ToDictionary(c => Id(c.Evidence), c => c.Cost));

    private static (Guid, int)[] Ranked(params int[] evidences) => [.. evidences.Select((e, index) => (Id(e), index + 1))];

    private static readonly Guid[] None = [];

    [Fact]
    public void The_budget_boundary_is_inclusive_an_evidence_that_exactly_fits_is_in_and_one_token_more_is_out()
    {
        var exact = CandidatePacker.Pack(None, Ranked(1, 2), Budget(30, (1, 10), (2, 20)));
        var oneOver = CandidatePacker.Pack(None, Ranked(1, 2), Budget(29, (1, 10), (2, 20)));

        Assert.Equal([Id(1), Id(2)], exact.Package);
        Assert.Empty(exact.Excluded);
        Assert.True(exact.Complete);
        Assert.Equal([Id(1)], oneOver.Package);
        Assert.Equal([(Id(2), "budget")], oneOver.Excluded.Select(e => (e.EvidenceId, e.Reason)));
        Assert.False(oneOver.Complete);
    }

    [Fact]
    public void Descent_stops_at_the_first_evidence_that_does_not_fit_and_never_skips_to_a_shorter_lower_ranked_one()
    {
        var packed = CandidatePacker.Pack(None, Ranked(1, 2, 3, 4), Budget(20, (1, 10), (2, 30), (3, 5), (4, 5)));

        Assert.Equal([Id(1)], packed.Package);   // la 3 e la 4 entrerebbero, ma si salterebbe il rango 2: nessuna selezione fuori ordine
        Assert.Equal([Id(2), Id(3), Id(4)], packed.Excluded.Select(e => e.EvidenceId));
        Assert.All(packed.Excluded, e => Assert.Equal(CandidateRegistry.ReasonBudget, e.Reason));
    }

    [Fact]
    public void A_passage_longer_than_the_whole_budget_is_never_shortened_and_leaves_the_package_empty_and_the_retrieval_incomplete()
    {
        var packed = CandidatePacker.Pack(None, Ranked(1, 2), Budget(100, (1, 101), (2, 10)));

        Assert.Empty(packed.Package);
        Assert.Equal([Id(1), Id(2)], packed.Excluded.Select(e => e.EvidenceId));
        Assert.False(packed.Complete);
    }

    [Fact]
    public void Always_included_evidences_come_first_and_one_that_does_not_fit_does_not_stop_the_others_but_makes_retrieval_incomplete()
    {
        var packed = CandidatePacker.Pack([Id(7), Id(8), Id(9)], Ranked(1), Budget(30, (7, 10), (8, 100), (9, 10), (1, 10)));

        Assert.Equal([Id(7), Id(9), Id(1)], packed.Package);
        Assert.Equal([(Id(8), "budget")], packed.Excluded.Select(e => (e.EvidenceId, e.Reason)));
        Assert.False(packed.Complete);
    }

    [Fact]
    public void Candidates_beyond_the_rerank_window_excluded_for_budget_are_listed_but_do_not_make_the_retrieval_incomplete()
    {
        var beyond = CandidatePacker.Pack(None, Ranked(1, 2, 3, 4), Budget(20, (1, 10), (2, 10), (3, 10), (4, 10)), rerankWindow: 2);
        var inside = CandidatePacker.Pack(None, Ranked(1, 2, 3, 4), Budget(10, (1, 10), (2, 10), (3, 10), (4, 10)), rerankWindow: 2);

        Assert.Equal([Id(1), Id(2)], beyond.Package);
        Assert.Equal([Id(3), Id(4)], beyond.Excluded.Select(e => e.EvidenceId));   // registrati comunque per identificativo
        Assert.True(beyond.Complete);
        Assert.False(inside.Complete);                                            // il rango 2 (entro la finestra) è escluso
    }

    [Fact]
    public void Without_a_window_every_budget_exclusion_makes_the_retrieval_incomplete()
    {
        var packed = CandidatePacker.Pack(None, Ranked(1, 2), Budget(10, (1, 10), (2, 10)));

        Assert.False(packed.Complete);
    }

    [Fact]
    public void No_budget_means_no_limit_and_no_cost_is_needed()
    {
        var packed = CandidatePacker.Pack([Id(7)], Ranked(1, 2, 3), budget: null);

        Assert.Equal([Id(7), Id(1), Id(2), Id(3)], packed.Package);
        Assert.True(packed.Complete);
    }

    [Fact]
    public void A_missing_cost_is_an_error_never_zero_and_negative_values_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => CandidatePacker.Pack(None, Ranked(1, 2), Budget(100, (1, 10))));
        Assert.Throws<ArgumentException>(() => CandidatePacker.Pack([Id(7)], Ranked(1), Budget(100, (1, 10))));
        Assert.Throws<ArgumentOutOfRangeException>(() => CandidatePacker.Pack(None, Ranked(1), Budget(100, (1, -1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => CandidatePacker.Pack(None, Ranked(1), Budget(-1, (1, 1))));
    }

    [Fact]
    public void The_result_is_deterministic()
    {
        var a = CandidatePacker.Pack([Id(7)], Ranked(3, 1, 2), Budget(25, (7, 5), (1, 10), (2, 10), (3, 10)), 3);
        var b = CandidatePacker.Pack([Id(7)], Ranked(3, 1, 2), Budget(25, (7, 5), (1, 10), (2, 10), (3, 10)), 3);

        Assert.Equal(a.Package, b.Package);
        Assert.Equal(a.Excluded, b.Excluded);
        Assert.Equal(a.Complete, b.Complete);
    }

    // ---- budget dello stadio ----

    [Fact]
    public void The_package_budget_is_the_stage_input_budget_minus_the_measured_fixed_payload_and_never_negative()
    {
        var stage = new ContextBudget(InputBudgetTokens: 1000, TriggerTokens: 1000);
        var cost = new Dictionary<Guid, long>();

        Assert.Equal(700, RegistryBudget.For(stage, 300, cost).Limit);
        Assert.Equal(1000, RegistryBudget.For(stage, 0, cost).Limit);
        Assert.Equal(0, RegistryBudget.For(stage, 1500, cost).Limit);   // payload fisso oltre il budget: nessuna evidenza entra
        Assert.Throws<ArgumentOutOfRangeException>(() => RegistryBudget.For(stage, -1, cost));
    }

    // ---- nel registro ----

    private static Guid BlockId(int n) => Id(n);

    private static BlockProvenance Prov(int block) => new(BlockId(block), Id(900), "Contratto", 1, "Art. 1", 0, 40, new string('a', 64));

    private static RetrievalRegistry Build(RegistryBudget budget, int? window)
    {
        FusedBlock[] fused = [.. Enumerable.Range(1, 4).Select(b => new FusedBlock(BlockId(b), b, null, b, 1.0 / (60 + b)))];
        RegistryEvidence[] evidences = [.. Enumerable.Range(1, 4).Select(e => new RegistryEvidence(Id(100 + e), EvidenceOriginKind.Document, EvidenceKind.Requirement, [BlockId(e)]))];
        return CandidateRegistry.Build(
            "snap", new string('1', 64), "retrieval-policy-1", new string('2', 64), [new(Id(501), "q", fused)],
            Enumerable.Range(1, 4).ToDictionary(BlockId, Prov), evidences, budget: budget, rerankWindow: window);
    }

    [Fact]
    public void In_the_registry_excluded_evidences_are_listed_by_id_with_reason_and_the_window_decides_completeness()
    {
        var budget = Budget(20, (101, 10), (102, 10), (103, 10), (104, 10));

        var windowed = Build(budget, window: 2);
        var unwindowed = Build(budget, window: null);

        Assert.Equal([Id(101), Id(102)], windowed.PackageEvidence);
        Assert.Equal([(Id(103), "budget"), (Id(104), "budget")], windowed.Excluded.Select(e => (e.EvidenceId, e.Reason)));
        Assert.True(windowed.RetrievalComplete);
        Assert.False(unwindowed.RetrievalComplete);
        var candidates = windowed.Gaps.Single().Candidates;
        Assert.Equal([true, true, false, false], candidates.Select(c => c.InPackage));
        Assert.Equal([null, null, "budget", "budget"], candidates.Select(c => c.ExcludedReason));
    }
}
