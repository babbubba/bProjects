using System.Globalization;
using System.Text.Json;

namespace bProjects.IntegrationTests;

/// <summary>003 T017 (DET): metriche calcolate a mano su classifiche sintetiche, ricalcolo indipendente, equivalenza con i valori del laboratorio, rapporto e regressione.</summary>
[Trait("Category", "RetrievalEval")]
public sealed class EvaluationMetricsTests
{
    private static string[] Rank(params string[] items) => items;

    [Fact]
    public void A_single_finding_at_position_three_has_values_computed_by_hand()
    {
        var m = EvaluationMetrics.ForQuery("q", Rank("a", "b", "g", "d", "e"), ["g"]);

        Assert.Equal(3, m.FirstRank);
        Assert.Equal([0.0, 1.0, 1.0, 1.0, 1.0], EvaluationMetrics.Ks.Select(k => m.Recall[k]));
        Assert.Equal([false, true, true, true, true], EvaluationMetrics.Ks.Select(k => m.Hit[k]));
    }

    [Fact]
    public void A_multi_finding_query_needs_all_findings_and_one_side_of_a_conflict_is_not_recall_one()
    {
        // Entrambi i lati di un conflitto: x in 2ª posizione, y in 7ª.
        var m = EvaluationMetrics.ForQuery("conflict", Rank("a", "x", "b", "c", "d", "e", "y", "f"), ["x", "y"]);

        Assert.Equal(2, m.FirstRank);
        Assert.Equal(0.0, m.Recall[1]);
        Assert.Equal(0.5, m.Recall[5]);   // un solo lato: NON vale 1
        Assert.Equal(1.0, m.Recall[10]);
        Assert.True(m.Hit[5]);            // Hit@5 conta anche un solo lato…
        Assert.False(m.Complete[5]);      // …ma la query non è completa
        Assert.True(m.Complete[10]);
    }

    [Fact]
    public void Duplicates_in_the_ranking_do_not_shift_distinct_positions_and_missing_findings_are_null()
    {
        var positions = EvaluationMetrics.Positions(Rank("a", "a", "b", "a", "c"), ["c", "b", "zzz"]);

        Assert.Equal(new int?[] { 3, 2, null }, positions);
        var m = EvaluationMetrics.ForQuery("q", positions);
        Assert.Equal(2, m.FirstRank);
        Assert.Equal(2.0 / 3, m.Recall[50], 12);
    }

    [Fact]
    public void A_query_that_never_finds_anything_has_no_first_rank_and_is_counted_apart()
    {
        var miss = EvaluationMetrics.ForQuery("miss", Rank("a", "b"), ["z"]);
        var hit = EvaluationMetrics.ForQuery("hit", Rank("z", "a"), ["z"]);
        var half = EvaluationMetrics.ForQuery("half", Rank("a", "b", "c", "d", "e", "f", "g"), ["g", "zz"]);

        var aggregate = EvaluationMetrics.Aggregate([miss, hit, half]);

        Assert.Equal(3, aggregate.Queries);
        Assert.Null(miss.FirstRank);
        Assert.Equal(1, aggregate.NeverFound);
        Assert.Equal((1 + 7) / 2.0, aggregate.FirstRankMean);                 // media sulle sole query che trovano
        Assert.Equal((0 + 1 + 0) / 3.0, aggregate.Recall[1], 12);             // hit: 1; half: g in 7ª, zz assente
        Assert.Equal((0 + 1 + 0.5) / 3.0, aggregate.Recall[10], 12);
        Assert.Equal(1 / 3.0, aggregate.Hit[1], 12);
        Assert.Equal(2 / 3.0, aggregate.Hit[10], 12);
        Assert.Equal(1 / 3.0, aggregate.Complete[10], 12);
    }

    [Fact]
    public void Queries_without_required_findings_are_rejected_by_the_query_metric_and_ignored_by_the_aggregate()
    {
        Assert.Throws<ArgumentException>(() => EvaluationMetrics.ForQuery("none", Rank("a"), Array.Empty<string>()));
        Assert.Equal(0, EvaluationMetrics.Aggregate([]).Queries);
        Assert.Throws<ArgumentOutOfRangeException>(() => EvaluationMetrics.ForQuery("bad", [0]));
    }

    [Fact]
    public void Invalid_citations_are_missing_blocks_out_of_range_intervals_and_quotes_that_differ_from_the_block()
    {
        var block = Guid.NewGuid();
        var texts = new Dictionary<Guid, string> { [block] = "Il termine di pagamento e' 30 giorni." };
        var ev = Guid.NewGuid();
        Citation Ok() => new(ev, block, 3, 10, "termine");

        Assert.Equal(0, EvaluationMetrics.InvalidCitations([Ok(), new(ev, block, 0, 37, texts[block])], texts));
        Assert.Equal(1, EvaluationMetrics.InvalidCitations([new(ev, Guid.NewGuid(), 3, 9, "termine")], texts));   // blocco fuori dal perimetro
        Assert.Equal(1, EvaluationMetrics.InvalidCitations([new(ev, block, 3, 99, "termine")], texts));           // intervallo oltre il testo
        Assert.Equal(1, EvaluationMetrics.InvalidCitations([new(ev, block, 3, 10, "terminE")], texts));            // testo diverso
        Assert.Equal(1, EvaluationMetrics.InvalidCitations([new(ev, block, 3, 9, "termine")], texts));            // intervallo e lunghezza discordi
        Assert.Equal(1, EvaluationMetrics.InvalidCitations([new(ev, block, -1, 6, "termine")], texts));
        Assert.Equal(2, EvaluationMetrics.InvalidCitations([Ok(), new(ev, block, 3, 10, "altro!!"), new(ev, block, 40, 41, "x")], texts));
    }

    [Fact]
    public void A_conflict_with_a_missing_side_is_lost_and_complete_conflicts_are_not()
    {
        IReadOnlyList<(string, IReadOnlyList<string>)> conflicts = [("pagamento", ["a", "b"]), ("garanzia", ["c", "d"]), ("limite", ["e", "f", "g"])];
        var available = new HashSet<string> { "a", "b", "c", "e", "f", "g" };

        Assert.Equal(["garanzia"], EvaluationMetrics.LostConflicts(conflicts, available));
        Assert.Empty(EvaluationMetrics.LostConflicts(conflicts, new HashSet<string> { "a", "b", "c", "d", "e", "f", "g" }));
        Assert.Equal(["pagamento", "garanzia", "limite"], EvaluationMetrics.LostConflicts(conflicts, new HashSet<string>()));
    }

    /// <summary>Ricalcolo indipendente (formulazione diversa, per posizione) e confronto con le funzioni del prodotto di test.</summary>
    [Fact]
    public void An_independent_recomputation_agrees_with_the_metric_functions_on_pseudo_random_rankings()
    {
        var random = new Random(20261109);
        for (var round = 0; round < 200; round++)
        {
            var universe = Enumerable.Range(0, 60).ToList();
            var ranking = universe.OrderBy(_ => random.Next()).Take(random.Next(1, 60)).ToList();
            var required = universe.OrderBy(_ => random.Next()).Take(random.Next(1, 4)).ToList();
            var m = EvaluationMetrics.ForQuery("r", ranking, required);

            foreach (var k in EvaluationMetrics.Ks)
            {
                var top = ranking.Take(k).ToHashSet();
                var found = required.Count(top.Contains);
                Assert.Equal(found / (double)required.Count, m.Recall[k], 12);
                Assert.Equal(found > 0, m.Hit[k]);
                Assert.Equal(found == required.Count, m.Complete[k]);
            }

            var firstIndex = ranking.Select((item, index) => (item, index)).Where(t => required.Contains(t.item)).Select(t => t.index + 1).DefaultIfEmpty(-1).Min();
            Assert.Equal(firstIndex == -1 ? null : firstIndex, m.FirstRank);
        }
    }

    /// <summary>Equivalenza con i valori del laboratorio (results/* @ f005c8d) per un sottoinsieme di 3 corpora × 3 metodi: per query e aggregati.</summary>
    [Fact]
    public void The_metric_functions_reproduce_the_laboratory_values_on_the_provided_results_subset()
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Retrieval", "Evaluation", "lab-metrics-subset.json")));
        var results = json.RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(9, results.Count);
        var checkedQueries = 0;

        foreach (var result in results)
        {
            var label = $"{result.GetProperty("scenario").GetString()}/{result.GetProperty("method").GetString()}";
            var metrics = new List<QueryMetrics>();
            foreach (var q in result.GetProperty("queries").EnumerateArray())
            {
                var positions = q.GetProperty("goldRanks").EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.Null ? (int?)null : p.Value.GetInt32()).ToList();
                var m = EvaluationMetrics.ForQuery(q.GetProperty("id").GetString()!, positions);
                metrics.Add(m);
                checkedQueries++;

                foreach (var k in EvaluationMetrics.Ks)
                {
                    var key = k.ToString(CultureInfo.InvariantCulture);
                    Assert.Equal(q.GetProperty("recall").GetProperty(key).GetDouble(), m.Recall[k], 12);
                    Assert.Equal(q.GetProperty("hit").GetProperty(key).GetInt32() == 1, m.Hit[k]);
                }

                var first = q.GetProperty("firstCorrectRank");
                Assert.Equal(first.ValueKind == JsonValueKind.Null ? null : first.GetInt32(), m.FirstRank);
            }

            var summary = result.GetProperty("summary");
            var aggregate = EvaluationMetrics.Aggregate(metrics);
            Assert.True(aggregate.Queries == summary.GetProperty("queries").GetInt32(), label);
            foreach (var k in EvaluationMetrics.Ks)
            {
                var key = k.ToString(CultureInfo.InvariantCulture);
                Assert.Equal(summary.GetProperty("recall").GetProperty(key).GetDouble(), aggregate.Recall[k], 12);
                Assert.Equal(summary.GetProperty("hit").GetProperty(key).GetDouble(), aggregate.Hit[k], 12);
            }

            Assert.Equal(summary.GetProperty("firstCorrectRankMean").GetDouble(), aggregate.FirstRankMean!.Value, 9);
        }

        Assert.Equal(12 * 3 + 19 * 3 + 25 * 3, checkedQueries);
    }

    [Fact]
    public void The_acceptance_decision_never_says_accepted_without_the_two_independent_requirements()
    {
        Assert.Equal(AcceptanceDecision.NotAssessable, AcceptanceDecision.Decide(simulatedMode: true, thresholdsMet: true, anonymizedCorpus: true, independentQueries: true).Status);
        Assert.Equal(AcceptanceDecision.NotAccepted, AcceptanceDecision.Decide(false, thresholdsMet: false, true, true).Status);
        Assert.Equal(AcceptanceDecision.Accepted, AcceptanceDecision.Decide(false, true, true, true).Status);

        foreach (var (anonymized, independent) in new[] { (false, false), (true, false), (false, true) })
        {
            var (status, statement) = AcceptanceDecision.Decide(false, true, anonymized, independent);
            Assert.Equal(AcceptanceDecision.AcceptedWithReservation, status);
            Assert.Contains("manca la validazione su corpus anonimizzato e su query indipendenti", statement, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_regression_check_tolerates_two_points_flags_more_and_refuses_different_input_sets()
    {
        var inputs = new Dictionary<string, string> { ["gold"] = "h1", ["queries"] = "h2" };
        var baseline = new Dictionary<string, double> { ["recall@10"] = 0.95, ["hit@1"] = 0.90 };

        Assert.Empty(RegressionCheck.Compare(inputs, inputs, baseline, new Dictionary<string, double> { ["recall@10"] = 0.93, ["hit@1"] = 0.95 }));
        var regressed = RegressionCheck.Compare(inputs, inputs, baseline, new Dictionary<string, double> { ["recall@10"] = 0.92, ["hit@1"] = 0.90 });
        Assert.Single(regressed);
        Assert.StartsWith("recall@10", regressed[0], StringComparison.Ordinal);

        var other = new Dictionary<string, string> { ["gold"] = "h1", ["queries"] = "DIVERSO" };
        var refused = RegressionCheck.Compare(inputs, other, baseline, baseline);
        Assert.Single(refused);
        Assert.StartsWith("input_hash_mismatch", refused[0], StringComparison.Ordinal);
        Assert.Contains("metrica assente", Assert.Single(RegressionCheck.Compare(inputs, inputs, baseline, new Dictionary<string, double> { ["hit@1"] = 0.9 })), StringComparison.Ordinal);
    }
}
