using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 T082 (DET) - stima prudente dell'output della chiamata singola di analisi: componenti, monotonia, margine conservativo, caso dei 297 elementi
/// (oltre il tetto indipendentemente dalla lunghezza), generalita (dipende da numero e dimensione, non dal corpus) e coefficienti fissati dalla versione
/// della policy. Nessun database, nessun modello.
/// </summary>
public sealed class AnalysisOutputEstimateTests
{
    private const int Cap = 16_384;

    private static IEnumerable<int> Lengths(int count, int length) => Enumerable.Repeat(length, count);

    [Fact]
    public void The_policy_version_and_its_coefficients_are_pinned_so_any_change_needs_a_new_version()
    {
        Assert.Equal("single-call-output-estimate-1", AnalysisOutputEstimate.PolicyVersion);
        Assert.Equal(2.5, AnalysisOutputEstimate.CharsPerToken);
        Assert.Equal(1.25, AnalysisOutputEstimate.TextExpansion);
        Assert.Equal(600, AnalysisOutputEstimate.FixedTokens);
        Assert.Equal(40, AnalysisOutputEstimate.ItemStructureTokens);
        Assert.Equal(14, AnalysisOutputEstimate.VisitedTokens);
        Assert.Equal(200, AnalysisOutputEstimate.GapTokens);
        Assert.Equal(50, AnalysisOutputEstimate.RelationTokens);
        Assert.Equal(0.08, AnalysisOutputEstimate.ConflictReserve);
        Assert.Equal(1.25, AnalysisOutputEstimate.SafetyMargin);
    }

    [Fact]
    public void An_empty_selection_costs_only_the_skeleton_with_the_margins()
    {
        var expected = (long)Math.Ceiling(600 * 1.08 * 1.25);
        Assert.Equal(expected, AnalysisOutputEstimate.Estimate([], 0, 0));
    }

    [Fact]
    public void Each_component_adds_to_the_estimate_and_the_estimate_is_monotonic()
    {
        var baseline = AnalysisOutputEstimate.Estimate(Lengths(10, 100), 0, 0);
        Assert.True(AnalysisOutputEstimate.Estimate(Lengths(11, 100), 0, 0) > baseline);      // piu evidenze
        Assert.True(AnalysisOutputEstimate.Estimate(Lengths(10, 200), 0, 0) > baseline);      // enunciati piu lunghi
        Assert.True(AnalysisOutputEstimate.Estimate(Lengths(10, 100), 1, 0) > baseline);      // un Gap
        Assert.True(AnalysisOutputEstimate.Estimate(Lengths(10, 100), 0, 1) > baseline);      // una relazione nota
        for (var n = 1; n < 200; n++)
        {
            Assert.True(AnalysisOutputEstimate.Estimate(Lengths(n + 1, 80), 0, 0) > AnalysisOutputEstimate.Estimate(Lengths(n, 80), 0, 0));
        }
    }

    [Fact]
    public void The_per_evidence_cost_is_above_the_only_real_lower_bound_and_the_297_evidence_case_is_over_the_cap_even_with_empty_statements()
    {
        // Misura reale parziale (risposta troncata): almeno 55 token per evidenza. Non e il fattore: la stima e un limite superiore prudente, sempre sopra quel minimo.
        foreach (var length in new[] { 0, 20, 60, 120, 400 })
        {
            Assert.True(AnalysisOutputEstimate.Estimate(Lengths(297, length), 0, 0) >= 297L * 55);
        }

        Assert.True(AnalysisOutputEstimate.Exceeds(Lengths(297, 0), 0, 0, Cap));      // anche con enunciati vuoti
        Assert.True(AnalysisOutputEstimate.Exceeds(Lengths(297, 90), 0, 0, Cap));     // con enunciati realistici
        Assert.True(AnalysisOutputEstimate.Exceeds(Lengths(297, 90), 5, 3, Cap));     // con Gap e relazioni
    }

    [Fact]
    public void Small_selections_stay_in_a_single_call_and_the_criterion_depends_on_count_size_and_cap()
    {
        Assert.False(AnalysisOutputEstimate.Exceeds(Lengths(20, 100), 0, 0, Cap));
        Assert.False(AnalysisOutputEstimate.Exceeds(Lengths(5, 800), 1, 1, Cap));
        // Stesso numero di evidenze: cambia la dimensione e cambia l'esito.
        Assert.False(AnalysisOutputEstimate.Exceeds(Lengths(60, 80), 0, 0, Cap));
        Assert.True(AnalysisOutputEstimate.Exceeds(Lengths(60, 900), 0, 0, Cap));
        // Stessa selezione: cambia il tetto di output e cambia l'esito.
        Assert.True(AnalysisOutputEstimate.Exceeds(Lengths(20, 100), 0, 0, 1_500));
    }

    [Fact]
    public void An_unusable_output_cap_is_always_treated_as_exceeded()
    {
        Assert.True(AnalysisOutputEstimate.Exceeds([], 0, 0, 0));
        Assert.True(AnalysisOutputEstimate.Exceeds([], 0, 0, -1));
    }

    [Fact]
    public void The_margins_are_applied_to_the_raw_sum()
    {
        const int n = 40;
        const int length = 100;
        var raw = 600 + n * (40 + 14 + Math.Ceiling(length * 1.25 / 2.5));
        Assert.Equal((long)Math.Ceiling(raw * 1.08 * 1.25), AnalysisOutputEstimate.Estimate(Lengths(n, length), 0, 0));
    }

    [Fact]
    public void Invalid_arguments_are_rejected_and_negative_lengths_count_as_empty()
    {
        Assert.Throws<ArgumentNullException>(() => AnalysisOutputEstimate.Estimate(null!, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalysisOutputEstimate.Estimate([], -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalysisOutputEstimate.Estimate([], 0, -1));
        Assert.Equal(AnalysisOutputEstimate.Estimate([0], 0, 0), AnalysisOutputEstimate.Estimate([-5], 0, 0));
    }

    [Fact]
    public void The_boundary_is_exact_an_estimate_equal_to_the_cap_fits_and_one_token_more_does_not()
    {
        var lengths = Lengths(30, 120).ToList();
        var estimate = AnalysisOutputEstimate.Estimate(lengths, 2, 4);

        Assert.False(AnalysisOutputEstimate.Exceeds(lengths, 2, 4, (int)estimate));
        Assert.True(AnalysisOutputEstimate.Exceeds(lengths, 2, 4, (int)estimate - 1));
    }
}
