using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// T150-P (DET, nessun servizio, nessun DGX): prova OFFLINE che i corpus T150 (<see cref="DocumentGapStageMafRealTests"/>) soddisfano CONTEMPORANEAMENTE, a W=131072, le due condizioni che il caso positivo richiede:
/// (A) la chiamata singola non e affidabile (stima di output single-call-output-estimate-1 &gt; O), quindi la gerarchia v4-equivalente si attiva (instradamento b1 di AI §13.7);
/// (B) il ledger COMPLETO + la maggiorazione prudente dei riferimenti entra nel budget di input B dello stadio Gap, quindi niente gap_ledger_exceeds_input.
/// Limiti dichiarati (nessun tokenizer reale, nessuna estrazione reale): il numero di evidenze e stimato dal corpus (un'evidenza per fatto gold e per regola secondaria, osservato 297 evidenze su 276
/// fatti+regole in I5, cioe ~1,08) e verificato su una FASCIA 0,75x–1,4x del nominale; le dimensioni per evidenza sono gli scenari della misura D1 (DocumentGapStagePayloadMeasureTests) al rapporto
/// prudente 2,5 caratteri/token. La prova non sostituisce la misura reale: le evidenze e i token effettivi si dichiarano solo dopo l'esecuzione autorizzata.
/// </summary>
public sealed class DocumentGapStageCorpusSizingTests
{
    private const int Window = DocumentGapStageMafRealTests.WindowTokens;
    private const int OutputCap = 16384;
    private const int Margin = 1024;
    private const int Budget = Window - OutputCap - Margin;   // 113664, come I5
    private const int Gaps = 3;

    /// <summary>Token fissi dello stadio (prompt dedicato, schema, framing): proxy prudente = prompt di analisi v6 (16134 byte) a 2,5 car/token piu lo schema.</summary>
    private const int FixedTokens = 8000;

    /// <summary>Token per evidenza nel formato VOCE a 2,5 car/token: «verboso» = enunciato 300, due citazioni da 400 e ~150 di metadati.</summary>
    private const int VerboseTokensPerEvidence = (300 + 800 + 150) * 10 / 25;

    private static readonly int[] StatementLengths = [120, 180, 300];

    private static (string Name, int Nominal) Single() =>
        ("T150-single", LargeDocumentCorpus.SingleGold.Count + DocumentGapStageMafRealTests.SingleBulkRules);

    private static (string Name, int Nominal) Multi() =>
        ("T150-multi", LargeDocumentCorpus.MultiGold.Count(g => g.DocKey != "pm") + (3 * DocumentGapStageMafRealTests.MultiBulkRulesPerDocument));

    [Fact]
    public void The_effective_stage_budget_at_the_required_window_is_the_one_measured_in_I5()
    {
        Assert.Equal(131072, DocumentGapStageMafRealTests.WindowTokens);
        Assert.Equal(113_664, Budget);
    }

    [Fact]
    public void The_generated_corpora_have_the_declared_rules_the_gold_the_three_question_targets_and_no_phone_number()
    {
        var single = LargeDocumentCorpus.Single(0, DocumentGapStageMafRealTests.SingleBulkRules);
        var multi = LargeDocumentCorpus.Multi(0, DocumentGapStageMafRealTests.MultiBulkRulesPerDocument);

        Assert.Equal(DocumentGapStageMafRealTests.SingleBulkRules, single.Documents.Sum(d => d.BulkRules.Count));
        Assert.Equal(3 * DocumentGapStageMafRealTests.MultiBulkRulesPerDocument, multi.Documents.Sum(d => d.BulkRules.Count));
        Assert.All(single.Documents.Concat(multi.Documents), d => Assert.Empty(d.Fillers));   // nessun riempitivo artificiale

        // Q1/Q3 hanno i loro riscontri nel testo, Q2 e davvero assente.
        static string Text(Corpus c) => string.Join("\n", c.Documents.Select(d => d.PlainText));
        Assert.Contains("84 mesi", Text(single), StringComparison.Ordinal);
        Assert.Contains("30 giorni", Text(single), StringComparison.Ordinal);
        Assert.Contains("60 giorni", Text(single), StringComparison.Ordinal);
        Assert.Contains("48 mesi", Text(multi), StringComparison.Ordinal);
        Assert.Contains("24 mesi", Text(multi), StringComparison.Ordinal);
        Assert.Contains("36 mesi", Text(multi), StringComparison.Ordinal);
        Assert.DoesNotContain("telefon", Text(single), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("telefon", Text(multi), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.0)]
    [InlineData(1.08)]
    public void Condition_A_the_single_call_output_estimate_exceeds_the_output_cap_over_the_band_for_every_statement_length(double factor)
    {
        foreach (var (name, nominal) in new[] { Single(), Multi() })
        {
            var evidences = (int)Math.Floor(nominal * factor);
            foreach (var length in StatementLengths)
            {
                Assert.True(AnalysisOutputEstimate.Exceeds(Enumerable.Repeat(length, evidences), Gaps, 0, OutputCap),
                    $"{name}: {evidences} evidenze da {length} caratteri non attivano la gerarchia (stima {AnalysisOutputEstimate.Estimate(Enumerable.Repeat(length, evidences), Gaps, 0)} <= O={OutputCap})");
            }
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.08)]
    [InlineData(1.4)]
    public void Condition_B_the_complete_ledger_plus_the_reference_allowance_fits_the_stage_input_budget_even_in_the_verbose_scenario(double factor)
    {
        foreach (var (name, nominal) in new[] { Single(), Multi() })
        {
            var evidences = (int)Math.Ceiling(nominal * factor);
            var tokens = FixedTokens + GapStageBudget.ReferenceAllowance(Gaps) + ((evidences + Gaps) * VerboseTokensPerEvidence);
            Assert.True(tokens <= Budget, $"{name}: {evidences} evidenze verbose = {tokens} token oltre B={Budget} (gap_ledger_exceeds_input)");
        }
    }

    [Fact]
    public void The_two_conditions_leave_a_real_margin_on_both_sides_not_an_arbitrary_size()
    {
        // Lato A: il nominale supera di almeno il 20% la soglia minima di attivazione (enunciato 120, il piu corto fra gli scenari).
        var thresholdAtShortest = Enumerable.Range(1, 2000).First(e => AnalysisOutputEstimate.Exceeds(Enumerable.Repeat(StatementLengths[0], e), Gaps, 0, OutputCap));
        // Lato B: massimo di evidenze verbose che entrano nel budget.
        var capacityVerbose = ((Budget - FixedTokens - GapStageBudget.ReferenceAllowance(Gaps)) / VerboseTokensPerEvidence) - Gaps;
        foreach (var (name, nominal) in new[] { Single(), Multi() })
        {
            Assert.True(nominal >= thresholdAtShortest * 1.2, $"{name}: nominale {nominal} troppo vicino alla soglia di attivazione {thresholdAtShortest}");
            Assert.True(nominal * 1.4 <= capacityVerbose, $"{name}: 1,4x nominale ({nominal * 1.4:F0}) oltre la capacita verbosa {capacityVerbose}");
        }
    }
}
