using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>003 T014 (DET): fusione RRF k = 60, pesi pari, tie-break per BlockId, ranghi per segnale conservati, indipendenza dall'ordine di arrivo.</summary>
public sealed class RrfTests
{
    private static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static RankedBlock[] List(params int[] blocks) => [.. blocks.Select((b, i) => new RankedBlock(Id(b), i + 1))];

    [Fact]
    public void Golden_vectors_of_known_ranks_give_the_documented_scores_and_order()
    {
        // lessicale: 1=A 2=B 3=C ; denso: 1=B 2=D 3=A
        var fused = Rrf.Fuse(List(1, 2, 3), List(2, 4, 1));

        Assert.Equal([Id(2), Id(1), Id(4), Id(3)], fused.Select(f => f.BlockId));
        Assert.Equal([1, 2, 3, 4], fused.Select(f => f.RrfRank));
        Assert.Equal(1.0 / 62 + 1.0 / 61, fused[0].Score, 12);   // B: rango 2 lessicale + rango 1 denso
        Assert.Equal(1.0 / 61 + 1.0 / 63, fused[1].Score, 12);   // A: rango 1 + rango 3
        Assert.Equal(1.0 / 62, fused[2].Score, 12);              // D: solo denso rango 2
        Assert.Equal(1.0 / 63, fused[3].Score, 12);              // C: solo lessicale rango 3
        Assert.Equal(60, Rrf.K);
    }

    [Fact]
    public void Per_signal_ranks_are_preserved_and_absent_signals_are_null()
    {
        var fused = Rrf.Fuse(List(1, 2), List(3, 1));

        var a = fused.Single(f => f.BlockId == Id(1));
        var b = fused.Single(f => f.BlockId == Id(2));
        var c = fused.Single(f => f.BlockId == Id(3));
        Assert.Equal((1, 2), (a.LexicalRank, a.DenseRank));
        Assert.Equal((2, (int?)null), (b.LexicalRank, b.DenseRank));
        Assert.Equal(((int?)null, 1), (c.LexicalRank, c.DenseRank));
    }

    [Fact]
    public void Ties_are_broken_by_ascending_block_id_deterministically()
    {
        // X: lessicale 1, denso 2; Y: lessicale 2, denso 1 -> stesso punteggio esatto: vince il BlockId minore.
        var x = Id(20);
        var y = Id(10);
        var fused = Rrf.Fuse([new(x, 1), new(y, 2)], [new(y, 1), new(x, 2)]);

        Assert.Equal(fused[0].Score, fused[1].Score);
        Assert.Equal([y, x], fused.Select(f => f.BlockId));
    }

    [Fact]
    public void A_single_empty_list_still_fuses_the_other_and_two_empty_lists_give_nothing()
    {
        var onlyDense = Rrf.Fuse([], List(5, 6, 7));
        Assert.Equal([Id(5), Id(6), Id(7)], onlyDense.Select(f => f.BlockId));
        Assert.All(onlyDense, f => Assert.Null(f.LexicalRank));

        var onlyLexical = Rrf.Fuse(List(5, 6), []);
        Assert.Equal([Id(5), Id(6)], onlyLexical.Select(f => f.BlockId));
        Assert.All(onlyLexical, f => Assert.Null(f.DenseRank));

        Assert.Empty(Rrf.Fuse([], []));
    }

    [Fact]
    public void The_result_does_not_depend_on_the_arrival_order_of_the_entries()
    {
        var lexical = List(1, 2, 3, 4, 5);
        var dense = List(5, 3, 1, 6, 2);
        var expected = Rrf.Fuse(lexical, dense).Select(f => (f.BlockId, f.RrfRank)).ToArray();

        var rng = new Random(7);
        for (var i = 0; i < 20; i++)
        {
            var shuffledLexical = lexical.OrderBy(_ => rng.Next()).ToArray();
            var shuffledDense = dense.OrderBy(_ => rng.Next()).ToArray();
            Assert.Equal(expected, Rrf.Fuse(shuffledLexical, shuffledDense).Select(f => (f.BlockId, f.RrfRank)).ToArray());
        }
    }

    [Fact]
    public void A_repeated_block_in_one_list_keeps_its_best_rank_and_invalid_ranks_are_rejected()
    {
        var fused = Rrf.Fuse([new(Id(1), 5), new(Id(1), 2)], []);

        Assert.Equal(2, Assert.Single(fused).LexicalRank);
        Assert.Throws<ArgumentOutOfRangeException>(() => Rrf.Fuse([new(Id(1), 0)], []));
    }
}
