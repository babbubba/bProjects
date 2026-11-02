using System.Text;
using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T049 (DET) — pianificatore delle unità di estrazione: partizione ESAUSTIVA e contigua dei range proprietari, packing nel massimo prefisso
/// misurato sul payload completo (token non additivi), contesto replicato mai proprietario né contato due volte, divisione solo su confini
/// strutturali con chiavi deterministiche, unità irriducibile = errore esplicito (mai una sottostringa), output stimato limitato, determinismo.
/// Nessun database, nessun LLM: misuratore finto (parole + overhead fisso per range).
/// </summary>
public sealed class DocumentPortionPlannerTests
{
    private static readonly Guid Document = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Version = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Preparation = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid Artifact = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int _block;

    private static DocumentSourceRange Whole(string key, string text, bool owned = true)
    {
        var block = Guid.Parse($"66666666-6666-4666-8666-{Interlocked.Increment(ref _block):D12}");
        return new DocumentSourceRange(key, Document, Version, Preparation, Artifact, block, text, 0, text.Length, owned);
    }

    private static string Sha(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Words(int count, string prefix = "w") => string.Join(' ', Enumerable.Range(0, count).Select(i => $"{prefix}{i}"));

    private static string Paragraphs(int paragraphs, int wordsEach) =>
        string.Join("\n\n", Enumerable.Range(0, paragraphs).Select(p => Words(wordsEach, $"p{p}x")) );

    /// <summary>Misura finta: una parola = un token, più 5 token di cornice per range e 40 fissi (istruzioni/schema). Qualità e hash dichiarati.</summary>
    private static UnitMeasurer Measurer(TokenCountQuality quality = TokenCountQuality.Measured, List<int>? attempts = null) => (ranges, _) =>
    {
        int Count(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var tokens = 40 + ranges.Sum(r => 5 + Count(r.Text.ToString()));
        var owned = ranges.Where(r => r.Owned).Sum(r => Count(r.Text.ToString()));
        attempts?.Add(tokens);
        var hash = Sha(string.Join("|", ranges.Select(r => $"{r.RangeKey}:{r.Start}-{r.End}:{r.Owned}")));
        return new ValueTask<UnitMeasure>(new UnitMeasure(tokens, quality, hash, owned));
    };

    private static PlannedPortion Portion(int ordinal, string owned, params DocumentSourceRange[] context) =>
        new(ordinal, [Whole($"o{ordinal}", owned)], [.. context]);

    private static DocumentPortionPlanner Planner(int input = 1000, int output = 100_000, double expansion = 8.0) => new(new PlannerLimits(input, output, expansion));

    private static string OwnedText(PlannedBatch batch) => string.Concat(batch.Owned.Select(r => r.Text.ToString()));

    // ------------------------------------------------------------------ packing e copertura

    [Fact]
    public async Task Portions_that_fit_together_are_packed_in_one_batch_in_reading_order_with_every_owned_range_once()
    {
        var portions = new[] { Portion(0, Words(20, "a")), Portion(1, Words(20, "b")), Portion(2, Words(20, "c")) };

        var partition = await Planner().PartitionAsync(portions, Measurer(), Ct);

        var batch = Assert.Single(partition.Batches);
        Assert.Equal([0, 1, 2], batch.PortionOrdinals);
        Assert.Equal(["o0", "o1", "o2"], batch.Owned.Select(r => r.RangeKey));
        Assert.All(batch.Owned, r => Assert.True(r.Owned));
        DocumentPortionPlanner.VerifyExhaustive(portions, partition);
    }

    [Fact]
    public async Task Portions_beyond_the_whole_window_become_several_batches_each_within_budget_and_none_is_omitted()
    {
        // 6 porzioni da 300 parole: due per volta (2*305+40) entrano in 700, tre no; l'insieme (1800+) supera di molto la finestra.
        var portions = Enumerable.Range(0, 6).Select(i => Portion(i, Words(300, $"s{i}x"))).ToArray();
        var attempts = new List<int>();

        var partition = await Planner(input: 700).PartitionAsync(portions, Measurer(attempts: attempts), Ct);

        Assert.Equal(3, partition.Batches.Count);
        Assert.Equal([[0, 1], [2, 3], [4, 5]], partition.Batches.Select(b => b.PortionOrdinals.ToArray()));
        Assert.All(partition.Batches, b => Assert.InRange(b.Measure.Tokens, 1, 700));
        Assert.True(attempts.Count > partition.Batches.Count, "ogni prefisso candidato è misurato: i token non sono additivi");
        DocumentPortionPlanner.VerifyExhaustive(portions, partition);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => $"o{i}"), partition.Batches.SelectMany(b => b.Owned).Select(r => r.RangeKey));
    }

    [Fact]
    public async Task The_packing_prefix_is_measured_on_the_whole_payload_not_summed_from_parts()
    {
        // Misuratore non additivo: un batch con due range costa meno della somma (dedup del template). Il planner deve fidarsi della misura.
        var portions = new[] { Portion(0, Words(100, "a")), Portion(1, Words(100, "b")) };
        UnitMeasurer cheapPairs = (ranges, _) =>
        {
            var words = ranges.Sum(r => r.Text.ToString().Split(' ').Length);
            var tokens = ranges.Count >= 2 ? 150 : words + 60;   // 2 range = 150 (< somma 320)
            return new ValueTask<UnitMeasure>(new UnitMeasure(tokens, TokenCountQuality.Measured, Sha(string.Join(',', ranges.Select(r => r.RangeKey))), words));
        };

        var partition = await Planner(input: 200).PartitionAsync(portions, cheapPairs, Ct);

        Assert.Single(partition.Batches);   // con 100+100 additivi sarebbero due batch
    }

    [Fact]
    public async Task Replicated_context_is_not_owned_is_counted_once_and_a_context_range_owned_in_the_same_batch_appears_only_as_owned()
    {
        var header = Whole("hdr", "Massimo giornaliero | Cliente", owned: true);
        var contextOfHeader = header with { Owned = false };
        var portions = new[]
        {
            new PlannedPortion(0, [header], []),
            new PlannedPortion(1, [Whole("row1", "A | 5 ordini")], [contextOfHeader]),
            new PlannedPortion(2, [Whole("row2", "B | 7 ordini")], [contextOfHeader]),
        };

        var partition = await Planner().PartitionAsync(portions, Measurer(), Ct);

        var batch = Assert.Single(partition.Batches);
        Assert.Equal(["hdr", "row1", "row2"], batch.Ranges.Select(r => r.RangeKey));   // l'header non è duplicato come contesto
        Assert.Equal([true, true, true], batch.Ranges.Select(r => r.Owned));

        // Se l'header non è proprietario nel batch compare una volta sola, come contesto, prima del testo che lo usa.
        var contextOnly = new[]
        {
            new PlannedPortion(0, [Whole("row1", "A | 5 ordini")], [contextOfHeader]),
            new PlannedPortion(1, [Whole("row2", "B | 7 ordini")], [contextOfHeader]),
        };
        var batch2 = Assert.Single((await Planner().PartitionAsync(contextOnly, Measurer(), Ct)).Batches);
        Assert.Equal(["hdr", "row1", "row2"], batch2.Ranges.Select(r => r.RangeKey));
        Assert.Equal([false, true, true], batch2.Ranges.Select(r => r.Owned));
        Assert.Equal(["row1", "row2"], batch2.Owned.Select(r => r.RangeKey));
    }

    // ------------------------------------------------------------------ divisione

    [Fact]
    public async Task An_oversized_range_is_split_on_paragraphs_into_contiguous_pieces_that_tile_it_exactly_with_deterministic_keys()
    {
        var text = Paragraphs(8, 100);   // 8 paragrafi da 100 parole
        var portions = new[] { Portion(0, text) };

        var partition = await Planner(input: 360).PartitionAsync(portions, Measurer(), Ct);

        Assert.True(partition.Batches.Count >= 3);
        var pieces = partition.Batches.SelectMany(b => b.Owned).ToList();
        Assert.All(pieces, p => Assert.StartsWith("o0~", p.RangeKey));
        Assert.Equal(text, string.Concat(pieces.Select(p => p.Text.ToString())));   // nessun carattere perso né duplicato
        for (var i = 1; i < pieces.Count; i++)
        {
            Assert.Equal(pieces[i - 1].End, pieces[i].Start);
        }

        Assert.All(pieces, p => Assert.Equal($"o0~{p.Start}-{p.End}", p.RangeKey));
        // Ogni taglio cade a inizio paragrafo: nessun pezzo comincia a metà di un paragrafo.
        Assert.All(pieces.Skip(1), p => Assert.StartsWith("p", p.Text.ToString()));
        DocumentPortionPlanner.VerifyExhaustive(portions, partition);
    }

    [Fact]
    public async Task Without_paragraphs_the_split_uses_lines_then_sentences_and_never_cuts_inside_a_sentence()
    {
        var sentences = string.Join(' ', Enumerable.Range(0, 30).Select(i => $"{Words(30, $"f{i}x")}."));
        var portions = new[] { Portion(0, sentences) };

        var partition = await Planner(input: 400).PartitionAsync(portions, Measurer(), Ct);

        var pieces = partition.Batches.SelectMany(b => b.Owned).ToList();
        Assert.True(pieces.Count > 1);
        Assert.Equal(sentences, string.Concat(pieces.Select(p => p.Text.ToString())));
        Assert.All(pieces, p => Assert.EndsWith(".", p.Text.ToString().TrimEnd()));
        Assert.All(pieces, p => Assert.StartsWith("f", p.Text.ToString()));
    }

    [Fact]
    public async Task A_split_never_breaks_a_surrogate_pair_and_every_piece_is_a_valid_source_range()
    {
        var sentence = "Il cliente 😀 ordina 𝄞 beni. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 120));
        var portions = new[] { Portion(0, text) };

        var partition = await Planner(input: 200).PartitionAsync(portions, Measurer(), Ct);

        foreach (var batch in partition.Batches)
        {
            _ = new DocumentSourceMap(batch.Ranges);   // lancia se un confine cade a metà di una coppia surrogata
        }

        Assert.Equal(text, string.Concat(partition.Batches.SelectMany(b => b.Owned).Select(p => p.Text.ToString())));
    }

    [Fact]
    public async Task A_range_with_no_structural_boundary_that_does_not_fit_is_irreducible_and_never_cut_to_a_substring()
    {
        var portions = new[] { Portion(0, Words(900)) };   // una sola riga, nessuna punteggiatura di fine frase

        var failure = await Assert.ThrowsAsync<DocumentWorkException>(() => Planner(input: 500).PartitionAsync(portions, Measurer(), Ct).AsTask());

        Assert.Equal(DocumentWorkException.DocumentIncomplete, failure.Code);
        Assert.Contains("o0", failure.Message);
        Assert.DoesNotContain("w100", failure.Message);   // nessun testo del documento nei messaggi
    }

    [Fact]
    public async Task The_estimated_output_bounds_the_owned_text_of_a_batch_even_when_the_input_window_is_large()
    {
        // 6 porzioni da 50 parole: l'input (10000) basterebbe per tutte, ma l'output stimato (parole proprietarie x 8) deve stare in 900.
        var portions = Enumerable.Range(0, 6).Select(i => Portion(i, Words(50, $"s{i}x"))).ToArray();

        var partition = await Planner(input: 10_000, output: 900, expansion: 8).PartitionAsync(portions, Measurer(), Ct);

        Assert.Equal(3, partition.Batches.Count);   // 2 porzioni = 100 parole * 8 = 800 <= 900; 3 = 1200 > 900
        Assert.All(partition.Batches, b => Assert.True(Math.Ceiling(b.Measure.OwnedTokens * 8.0) <= 900));
        DocumentPortionPlanner.VerifyExhaustive(portions, partition);
    }

    [Fact]
    public async Task An_unavailable_token_count_is_runtime_unverified_not_an_estimate()
    {
        var failure = await Assert.ThrowsAsync<RuntimeUnverifiedException>(
            () => Planner().PartitionAsync([Portion(0, Words(5))], Measurer(TokenCountQuality.Unavailable), Ct).AsTask());

        Assert.Equal("runtime_unverified", failure.Code);
    }

    // ------------------------------------------------------------------ determinismo e copertura

    [Fact]
    public async Task The_same_input_and_measure_produce_the_same_batches_inputs_and_hashes_regardless_of_the_run()
    {
        PlannedPortion[] Build() => [.. Enumerable.Range(0, 5).Select(i => Portion(i, Paragraphs(3, 120)))];
        var first = Build();
        var second = first.Select(p => p).ToArray();

        var a = await Planner(input: 400).PartitionAsync(first, Measurer(), Ct);
        var b = await Planner(input: 400).PartitionAsync(second, Measurer(), Ct);

        Assert.Equal(a.Batches.Select(DocumentPortionPlanner.DescribeInputs), b.Batches.Select(DocumentPortionPlanner.DescribeInputs));
        Assert.Equal(a.Batches.Select(x => x.Measure.PayloadHash), b.Batches.Select(x => x.Measure.PayloadHash));
    }

    [Fact]
    public async Task The_coverage_check_rejects_a_partition_with_a_missing_or_overlapping_piece()
    {
        var portions = new[] { Portion(0, Paragraphs(8, 100)) };
        var partition = await Planner(input: 360).PartitionAsync(portions, Measurer(), Ct);
        Assert.True(partition.Batches.Count >= 3);

        var dropped = new ExtractionPartition([.. partition.Batches.Skip(1)]);
        var gap = Assert.Throws<DocumentWorkException>(() => DocumentPortionPlanner.VerifyExhaustive(portions, dropped));
        Assert.Equal(DocumentWorkException.InvalidInput, gap.Code);

        var duplicated = new ExtractionPartition([partition.Batches[0], .. partition.Batches]);
        Assert.Throws<DocumentWorkException>(() => DocumentPortionPlanner.VerifyExhaustive(portions, duplicated));
    }

    [Fact]
    public async Task Random_documents_are_always_tiled_exactly_once_whatever_the_budget()
    {
        var random = new Random(20261102);
        for (var round = 0; round < 40; round++)
        {
            var portions = new List<PlannedPortion>();
            for (var p = 0; p < random.Next(1, 6); p++)
            {
                var text = new StringBuilder();
                for (var s = 0; s < random.Next(1, 40); s++)
                {
                    text.Append(Words(random.Next(1, 25), $"r{round}p{p}s{s}x")).Append(random.Next(3) == 0 ? ".\n\n" : random.Next(2) == 0 ? ". " : "\n");
                }

                portions.Add(Portion(p, text.ToString().TrimEnd()));
            }

            var planner = Planner(input: random.Next(150, 900));
            ExtractionPartition partition;
            try
            {
                partition = await planner.PartitionAsync(portions, Measurer(), Ct);
            }
            catch (DocumentWorkException ex) when (ex.Code == DocumentWorkException.DocumentIncomplete)
            {
                continue;   // un budget troppo piccolo rende irriducibile: è un esito ammesso, mai una copertura parziale
            }

            DocumentPortionPlanner.VerifyExhaustive(portions, partition);
            foreach (var portion in portions)
            {
                var pieces = partition.Batches.SelectMany(b => b.Owned).Where(r => DocumentPortionPlanner.OriginOf(r.RangeKey) == portion.Owned[0].RangeKey).ToList();
                Assert.Equal(portion.Owned[0].Text.ToString(), string.Concat(pieces.Select(x => x.Text.ToString())));
            }
        }
    }
}
