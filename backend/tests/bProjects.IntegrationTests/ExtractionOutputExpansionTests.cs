using System.Text;
using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003 T040 (DET, nessun DGX): calibrazione PREVENTIVA del fattore con cui il planner stima l'output dell'estrazione (<see cref="AiOptions.ExtractionOutputExpansion"/>, candidato 16).
/// Dopo il Run 1 (unità da 1430 token posseduti, stima 8,0 = 11440 ≤ O, generati ≥ 16384 con finish=length) il fattore 16 deve dividere quell'unità senza perdere né duplicare intervalli.
/// Limiti dichiarati: il misuratore è finto (rapporto 3,82 caratteri/token, ricavato dal Run 1: 5464 caratteri = 1430 token) e le unità sono ricostruite dal corpus riproducibile (seed fisso), non dalle porzioni
/// reali del parser. Dimostra la regola di divisione, non che 16 basti al modello reale: quella è una misura da fare con l'estrazione reale (autorizzazione separata).
/// </summary>
public sealed class ExtractionOutputExpansionTests
{
    private const double CharsPerToken = 5464 / 1430.0;   // misurato nel Run 1 con il tokenizer del runtime
    private const int FixedPayloadTokens = 2958;          // 4388 (prompt riportato) - 1430 (posseduti): istruzioni, schema, cornice
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Document = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Version = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Preparation = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid Artifact = Guid.Parse("55555555-5555-4555-8555-555555555555");

    private static DocumentSourceRange Range(int n, string text) =>
        new($"c{n}", Document, Version, Preparation, Artifact, Guid.Parse($"66666666-6666-4666-8666-{n:D12}"), text, 0, text.Length, true);

    private static UnitMeasurer Measurer() => (ranges, _) =>
    {
        var owned = (int)Math.Ceiling(ranges.Where(r => r.Owned).Sum(r => r.Text.ToString().Length) / CharsPerToken);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", ranges.Select(r => $"{r.RangeKey}:{r.Start}-{r.End}")))));
        return new ValueTask<UnitMeasure>(new UnitMeasure(FixedPayloadTokens + owned, TokenCountQuality.Measured, hash, owned));
    };

    /// <summary>Righe del corpus T040-single come intervalli; porzioni da <paramref name="perPortion"/> righe (come le due porzioni del Run 1: 32 range).</summary>
    private static (PlannedPortion[] Portions, int Chars) Portions(int firstChars, int perPortion)
    {
        var lines = RetrievalGapCampaign.BuildCorpus(RetrievalGapCampaign.Single).Documents[0].PlainText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var ranges = new List<DocumentSourceRange>();
        var chars = 0;
        foreach (var line in lines)
        {
            if (chars >= firstChars)
            {
                break;
            }

            ranges.Add(Range(ranges.Count + 1, line));
            chars += line.Length;
        }

        return ([.. ranges.Chunk(perPortion).Select((chunk, i) => new PlannedPortion(i, [.. chunk], []))], chars);
    }

    [Fact]
    public void The_candidate_is_16_is_configurable_and_validated_and_the_generic_planner_default_is_untouched()
    {
        Assert.Equal(16.0, PlannerLimits.ExtractionCandidateOutputExpansion);
        Assert.Equal(PlannerLimits.ExtractionCandidateOutputExpansion, new AiOptions().ExtractionOutputExpansion);
        Assert.Equal(8.0, PlannerLimits.DefaultOutputExpansion);   // altri chiamanti e test del planner restano a 8
        Assert.False(string.IsNullOrWhiteSpace(PlannerLimits.ExtractionCalibrationVersion));

        static IReadOnlyList<string> Errors(double factor)
        {
            var options = new AiOptions { Endpoint = "http://localhost:8080/v1/", ModelId = "m", ApiKey = "k", ExtractionOutputExpansion = factor };
            var result = new AiOptionsValidator().Validate(null, options);
            return result.Failures?.ToList() ?? [];
        }

        Assert.Empty(Errors(16));
        Assert.Empty(Errors(8));
        Assert.Contains(Errors(0.5), e => e.Contains("ExtractionOutputExpansion"));
        Assert.Contains(Errors(65), e => e.Contains("ExtractionOutputExpansion"));
        Assert.Contains(Errors(double.NaN), e => e.Contains("ExtractionOutputExpansion"));
    }

    [Fact]
    public async Task The_run1_unit_is_one_batch_at_8_and_is_split_at_16_into_batches_that_each_fit_O_with_every_range_exactly_once()
    {
        var (portions, chars) = Portions(firstChars: 5464, perPortion: 16);
        Assert.InRange(chars, 5464, 6400);
        const int budget = 15360;
        const int output = 16384;

        var at8 = await new DocumentPortionPlanner(new PlannerLimits(budget, output, 8.0)).PartitionAsync(portions, Measurer(), Ct);
        var at16 = await new DocumentPortionPlanner(new PlannerLimits(budget, output, 16.0)).PartitionAsync(portions, Measurer(), Ct);

        Assert.Single(at8.Batches);   // il comportamento del Run 1: nessuna divisione
        Assert.Equal(2, at16.Batches.Count);   // 1430x16 > O: il planner riempie il primo batch fino al massimo prefisso che entra e il resto va nel secondo (anche a meta di porzione, su confini di range)
        Assert.All(at16.Batches, b => Assert.True(Math.Ceiling(b.Measure.OwnedTokens * 16.0) <= output, $"batch {b.Ordinal}: stima {b.Measure.OwnedTokens * 16.0} > O"));
        Assert.All(at16.Batches, b => Assert.InRange(b.Measure.Tokens, 1, budget));

        DocumentPortionPlanner.VerifyExhaustive(portions, at16);   // copertura esaustiva, nessun duplicato, nessuna omissione
        var expectedKeys = portions.SelectMany(p => p.Owned).Select(r => r.RangeKey).ToArray();
        Assert.Equal(expectedKeys, at16.Batches.SelectMany(b => b.Owned).Select(r => r.RangeKey).ToArray());   // stesso ordine, ciascuno una volta
        Assert.Equal(portions.SelectMany(p => p.Owned).Sum(r => r.Text.Length), at16.Batches.SelectMany(b => b.Owned).Sum(r => r.Text.Length));   // nessun carattere perso
    }

    [Fact]
    public async Task The_split_is_deterministic_and_depends_on_the_factor()
    {
        var (portions, _) = Portions(5464, 16);
        var limits = new PlannerLimits(15360, 16384, 16.0);
        var a = await new DocumentPortionPlanner(limits).PartitionAsync(portions, Measurer(), Ct);
        var b = await new DocumentPortionPlanner(limits).PartitionAsync(portions, Measurer(), Ct);
        Assert.Equal(a.Batches.Select(DocumentPortionPlanner.DescribeInputs), b.Batches.Select(DocumentPortionPlanner.DescribeInputs));

        var at8 = await new DocumentPortionPlanner(limits with { OutputExpansion = 8.0 }).PartitionAsync(portions, Measurer(), Ct);
        Assert.NotEqual(a.Batches.Count, at8.Batches.Count);   // il piano dipende dal fattore (nel manifesto il fattore e nell'hash di configurazione: non coperto da questo test, vedi M3-real.md)
    }

    [Fact]
    public async Task Whole_corpus_batch_count_estimate_at_8_versus_16_is_an_estimate_not_a_measurement()
    {
        // Stima offline (misuratore finto) del solo numero di batch del documento T040-single. Il tempo reale NON e derivabile da qui.
        var (portions, _) = Portions(int.MaxValue, 16);
        const int budget = 15360;
        const int output = 16384;
        var owned = (int)Math.Ceiling(portions.SelectMany(p => p.Owned).Sum(r => r.Text.Length) / CharsPerToken);
        var at8 = await new DocumentPortionPlanner(new PlannerLimits(budget, output, 8.0)).PartitionAsync(portions, Measurer(), Ct);
        var at16 = await new DocumentPortionPlanner(new PlannerLimits(budget, output, 16.0)).PartitionAsync(portions, Measurer(), Ct);
        TestContext.Current.SendDiagnosticMessage($"STIMA T040-single: token posseduti ~{owned}; batch a 8 = {at8.Batches.Count}, a 16 = {at16.Batches.Count}");
        Assert.True(at16.Batches.Count > at8.Batches.Count);
        Assert.True(at16.Batches.Count >= (int)Math.Ceiling(owned * 16.0 / output));
        DocumentPortionPlanner.VerifyExhaustive(portions, at16);
        DocumentPortionPlanner.VerifyExhaustive(portions, at8);
    }
}
