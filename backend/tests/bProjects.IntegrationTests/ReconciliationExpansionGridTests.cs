using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// Griglia deterministica di espansione dei candidati di gruppo: ogni coppia rilevante (un id in S1, l'altro in S2, distinti) deve comparire
/// insieme in almeno un blocco. Test di proprietà con seme fisso, verifica esatta, deduplica simmetrica, ordinamento per stringa «D» ordinale.
/// </summary>
public sealed class ReconciliationExpansionGridTests
{
    private static Guid G(int n) => Guid.Parse($"{n:x8}-0000-4000-8000-000000000000");

    private static List<Guid> Pool(Random rng, int count)
    {
        var set = new HashSet<Guid>();
        var list = new List<Guid>();
        while (list.Count < count)
        {
            var bytes = new byte[16];
            rng.NextBytes(bytes);
            var id = new Guid(bytes);
            if (set.Add(id))
            {
                list.Add(id);
            }
        }

        return list;
    }

    private static List<int> RandomSizes(Random rng, int total, int maxPart)
    {
        var sizes = new List<int>();
        var remaining = total;
        while (remaining > 0)
        {
            var size = rng.Next(1, Math.Min(remaining, maxPart) + 1);
            sizes.Add(size);
            remaining -= size;
        }

        return sizes;
    }

    private static List<int> EqualSizes(int chunks, int size) => Enumerable.Repeat(size, chunks).ToList();

    /// <summary>Genera (S1, S2) ordinati; modalità di sovrapposizione 0 = nessuna, 1 = parziale, 2 = totale (S2 = S1).</summary>
    private static (IReadOnlyList<Guid> S1, IReadOnlyList<Guid> S2) Groups(Random rng, int overlapMode)
    {
        var n1 = rng.Next(0, 46);
        var n2 = rng.Next(0, 46);
        if (overlapMode == 2)
        {
            n2 = n1;
        }

        var pool = Pool(rng, n1 + n2);
        var s1 = pool.Take(n1).ToList();
        List<Guid> s2;
        if (overlapMode == 0)
        {
            s2 = pool.Skip(n1).ToList();
        }
        else if (overlapMode == 2)
        {
            s2 = s1.ToList();
        }
        else
        {
            var shared = n1 == 0 || n2 == 0 ? 0 : rng.Next(0, Math.Min(n1, n2) + 1);
            s2 = s1.OrderBy(_ => rng.Next()).Take(shared).Concat(pool.Skip(n1).Take(n2 - shared)).ToList();
        }

        return (ReconciliationExpansionGrid.Ordered(s1), ReconciliationExpansionGrid.Ordered(s2));
    }

    private static long ExpectedPairs(IReadOnlyList<Guid> s1, IReadOnlyList<Guid> s2)
    {
        var in1 = s1.ToHashSet();
        var in2 = s2.ToHashSet();
        var union = in1.Union(in2).ToList();
        long count = 0;
        for (var i = 0; i < union.Count; i++)
        {
            for (var j = i + 1; j < union.Count; j++)
            {
                if ((in1.Contains(union[i]) && in2.Contains(union[j])) || (in1.Contains(union[j]) && in2.Contains(union[i])))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static IReadOnlyList<ExpansionCell> RandomCells(Random rng, IReadOnlyList<Guid> s1, IReadOnlyList<Guid> s2)
    {
        var left = ReconciliationExpansionGrid.Chunk(s1, RandomSizes(rng, s1.Count, 12));
        var right = ReconciliationExpansionGrid.Chunk(s2, RandomSizes(rng, s2.Count, 12));
        return ReconciliationExpansionGrid.Cells(left, right);
    }

    private static List<ExpansionCell> SplitFully(ExpansionCell cell)
    {
        var parts = ReconciliationExpansionGrid.Split(cell);
        return parts is null ? [cell] : parts.SelectMany(SplitFully).ToList();
    }

    private static List<string> Product(IEnumerable<ExpansionCell> cells) =>
        cells.SelectMany(c => c.Left.SelectMany(l => c.Right.Select(r => $"{l:D}>{r:D}"))).OrderBy(s => s, StringComparer.Ordinal).ToList();

    [Fact]
    public void Random_cells_blocks_verify_is_complete_and_required_pairs_match_combinatorics()
    {
        var rng = new Random(20260610);
        for (var iteration = 0; iteration < 450; iteration++)
        {
            var (s1, s2) = Groups(rng, iteration % 3);
            var tags = ReconciliationExpansionGrid.Tags(s1, s2);
            var blocks = ReconciliationExpansionGrid.Blocks(RandomCells(rng, s1, s2), tags);
            var coverage = ReconciliationExpansionGrid.Verify(s1, s2, blocks);

            Assert.True(coverage.Complete, $"iterazione {iteration}: mancanti {coverage.FirstMissing.Count}");
            Assert.Equal(ExpectedPairs(s1, s2), coverage.RequiredPairs);
            Assert.Empty(coverage.FirstMissing);
            for (var i = 0; i < blocks.Count; i++)
            {
                Assert.Equal(i, blocks[i].Ordinal);
            }
        }
    }

    [Fact]
    public void Splitting_random_cells_recursively_keeps_coverage_complete_and_product_unchanged()
    {
        var rng = new Random(77);
        for (var iteration = 0; iteration < 120; iteration++)
        {
            var (s1, s2) = Groups(rng, iteration % 3);
            var tags = ReconciliationExpansionGrid.Tags(s1, s2);
            var cells = RandomCells(rng, s1, s2);
            var replaced = new List<ExpansionCell>();
            foreach (var cell in cells)
            {
                if (rng.Next(2) == 0)
                {
                    replaced.AddRange(SplitFully(cell));
                }
                else
                {
                    replaced.Add(cell);
                }
            }

            var before = Product(cells);
            var after = Product(replaced);
            Assert.Equal(before, after);
            Assert.Equal(after.Count, after.Distinct().Count());

            var coverage = ReconciliationExpansionGrid.Verify(s1, s2, ReconciliationExpansionGrid.Blocks(replaced, tags));
            Assert.True(coverage.Complete, $"iterazione {iteration}");
            Assert.Equal(ExpectedPairs(s1, s2), coverage.RequiredPairs);
        }
    }

    [Fact]
    public void Fully_split_cells_end_in_single_member_pairs()
    {
        var cell = new ExpansionCell([G(1), G(2), G(3)], [G(4), G(5)]);
        var leaves = SplitFully(cell);

        Assert.Equal(6, leaves.Count);
        Assert.All(leaves, leaf =>
        {
            Assert.Single(leaf.Left);
            Assert.Single(leaf.Right);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void Symmetric_groups_with_equal_chunking_yield_m_times_m_plus_one_over_two_blocks(int m)
    {
        var ids = ReconciliationExpansionGrid.Ordered(Enumerable.Range(1, m * 3).Select(G));
        var tags = ReconciliationExpansionGrid.Tags(ids, ids);
        var chunks = ReconciliationExpansionGrid.Chunk(ids, EqualSizes(m, 3));
        var blocks = ReconciliationExpansionGrid.Blocks(ReconciliationExpansionGrid.Cells(chunks, chunks), tags);

        Assert.Equal(m * (m + 1) / 2, blocks.Count);
        var coverage = ReconciliationExpansionGrid.Verify(ids, ids, blocks);
        Assert.True(coverage.Complete);
        Assert.Equal(ids.Count * (ids.Count - 1L) / 2, coverage.RequiredPairs);
    }

    [Fact]
    public void Symmetric_groups_with_single_member_chunks_drop_the_diagonal()
    {
        const int m = 6;
        var ids = ReconciliationExpansionGrid.Ordered(Enumerable.Range(1, m).Select(G));
        var tags = ReconciliationExpansionGrid.Tags(ids, ids);
        var chunks = ReconciliationExpansionGrid.Chunk(ids, EqualSizes(m, 1));
        var blocks = ReconciliationExpansionGrid.Blocks(ReconciliationExpansionGrid.Cells(chunks, chunks), tags);

        Assert.Equal(m * (m - 1) / 2, blocks.Count);
        Assert.True(ReconciliationExpansionGrid.Verify(ids, ids, blocks).Complete);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(4, 5)]
    [InlineData(7, 2)]
    public void Disjoint_groups_yield_a_times_b_blocks(int a, int b)
    {
        var s1 = ReconciliationExpansionGrid.Ordered(Enumerable.Range(1, a * 4).Select(G));
        var s2 = ReconciliationExpansionGrid.Ordered(Enumerable.Range(1000, b * 3).Select(G));
        var tags = ReconciliationExpansionGrid.Tags(s1, s2);
        var cells = ReconciliationExpansionGrid.Cells(ReconciliationExpansionGrid.Chunk(s1, EqualSizes(a, 4)), ReconciliationExpansionGrid.Chunk(s2, EqualSizes(b, 3)));
        var blocks = ReconciliationExpansionGrid.Blocks(cells, tags);

        Assert.Equal(a * b, blocks.Count);
        var coverage = ReconciliationExpansionGrid.Verify(s1, s2, blocks);
        Assert.True(coverage.Complete);
        Assert.Equal(s1.Count * (long)s2.Count, coverage.RequiredPairs);
    }

    [Fact]
    public void A_single_both_member_alone_is_not_relevant_but_both_plus_left_is()
    {
        var x = G(1);
        var y = G(2);
        var z = G(3);
        var tags = ReconciliationExpansionGrid.Tags([x, y], [x, z]);

        Assert.Equal(ExpansionTag.Both, tags[x]);
        Assert.Equal(ExpansionTag.Left, tags[y]);
        Assert.Equal(ExpansionTag.Right, tags[z]);
        Assert.False(ReconciliationExpansionGrid.IsRelevant([x], tags));
        Assert.True(ReconciliationExpansionGrid.IsRelevant([x, y], tags));
        Assert.True(ReconciliationExpansionGrid.IsRelevant([x, z], tags));
        Assert.True(ReconciliationExpansionGrid.IsRelevant([y, z], tags));
        Assert.False(ReconciliationExpansionGrid.IsRelevant([y], tags));
        Assert.False(ReconciliationExpansionGrid.IsRelevant([], tags));

        var onlyBoth = ReconciliationExpansionGrid.Blocks([new ExpansionCell([x], [x])], ReconciliationExpansionGrid.Tags([x], [x]));
        Assert.Empty(onlyBoth);

        var bothAndLeft = ReconciliationExpansionGrid.Blocks([new ExpansionCell([x, y], [x])], tags);
        Assert.Single(bothAndLeft);
    }

    [Fact]
    public void Members_all_on_one_side_have_no_relevant_pair_and_the_cell_is_dropped()
    {
        var a = G(1);
        var b = G(2);
        var c = G(3);
        var d = G(4);
        var tags = ReconciliationExpansionGrid.Tags([a, b], [c, d]);

        Assert.False(ReconciliationExpansionGrid.IsRelevant([a, b], tags));
        Assert.False(ReconciliationExpansionGrid.IsRelevant([c, d], tags));
        Assert.Empty(ReconciliationExpansionGrid.Blocks([new ExpansionCell([a, b], [])], tags));
        Assert.Empty(ReconciliationExpansionGrid.Blocks([new ExpansionCell([], [c, d])], tags));
    }

    [Fact]
    public void Verify_detects_a_removed_block()
    {
        var s1 = ReconciliationExpansionGrid.Ordered(Enumerable.Range(1, 9).Select(G));
        var s2 = ReconciliationExpansionGrid.Ordered(Enumerable.Range(100, 6).Select(G));
        var tags = ReconciliationExpansionGrid.Tags(s1, s2);
        var cells = ReconciliationExpansionGrid.Cells(ReconciliationExpansionGrid.Chunk(s1, EqualSizes(3, 3)), ReconciliationExpansionGrid.Chunk(s2, EqualSizes(2, 3)));
        var blocks = ReconciliationExpansionGrid.Blocks(cells, tags);

        for (var removed = 0; removed < blocks.Count; removed++)
        {
            var remaining = blocks.Where(b => b.Ordinal != removed).ToList();
            var coverage = ReconciliationExpansionGrid.Verify(s1, s2, remaining);

            Assert.False(coverage.Complete);
            Assert.Equal(9L, coverage.RequiredPairs - coverage.CoveredPairs);
            Assert.NotEmpty(coverage.FirstMissing);
            Assert.True(coverage.FirstMissing.Count <= 8);
            foreach (var (first, second) in coverage.FirstMissing)
            {
                Assert.DoesNotContain(remaining, b => b.Members.Contains(first) && b.Members.Contains(second));
                Assert.True(ReconciliationExpansionGrid.Compare(first, second) < 0);
            }
        }
    }

    [Fact]
    public void Verify_ignores_irrelevant_blocks_and_reports_zero_blocks_as_incomplete_when_pairs_exist()
    {
        var s1 = ReconciliationExpansionGrid.Ordered([G(1), G(2)]);
        var s2 = ReconciliationExpansionGrid.Ordered([G(3), G(4)]);
        var tags = ReconciliationExpansionGrid.Tags(s1, s2);
        var blocks = ReconciliationExpansionGrid.Blocks(ReconciliationExpansionGrid.Cells([s1], [s2]), tags);
        var withIrrelevant = blocks.Append(new ExpansionBlock(99, [G(1), G(2)], [G(1), G(2)], [])).ToList();

        var plain = ReconciliationExpansionGrid.Verify(s1, s2, blocks);
        var extra = ReconciliationExpansionGrid.Verify(s1, s2, withIrrelevant);
        Assert.True(plain.Complete);
        Assert.True(extra.Complete);
        Assert.Equal(plain.RequiredPairs, extra.RequiredPairs);
        Assert.Equal(plain.CoveredPairs, extra.CoveredPairs);

        var none = ReconciliationExpansionGrid.Verify(s1, s2, []);
        Assert.False(none.Complete);
        Assert.Equal(4L, none.RequiredPairs);
        Assert.Equal(0L, none.CoveredPairs);
        Assert.Equal(4, none.FirstMissing.Count);
    }

    [Fact]
    public void Verify_reports_at_most_eight_missing_pairs_in_ordinal_order()
    {
        var s1 = ReconciliationExpansionGrid.Ordered(Enumerable.Range(1, 5).Select(G));
        var s2 = ReconciliationExpansionGrid.Ordered(Enumerable.Range(100, 5).Select(G));

        var coverage = ReconciliationExpansionGrid.Verify(s1, s2, []);

        Assert.Equal(25L, coverage.RequiredPairs);
        Assert.Equal(8, coverage.FirstMissing.Count);
        Assert.Equal((G(1), G(100)), coverage.FirstMissing[0]);
        Assert.Equal((G(1), G(101)), coverage.FirstMissing[1]);
    }

    [Fact]
    public void Results_are_deterministic_across_runs()
    {
        for (var run = 0; run < 2; run++)
        {
            Assert.Equal(Snapshot(), Snapshot());
        }

        static string Snapshot()
        {
            var rng = new Random(5150);
            var (s1, s2) = Groups(rng, 1);
            var tags = ReconciliationExpansionGrid.Tags(s1, s2);
            var blocks = ReconciliationExpansionGrid.Blocks(RandomCells(rng, s1, s2), tags);
            return string.Join(';', blocks.Select(b => $"{b.Ordinal}:{string.Join(',', b.Members.Select(m => m.ToString("D")))}|{b.Left.Count}/{b.Right.Count}"));
        }
    }

    [Fact]
    public void Ordering_uses_the_d_string_ordinal_not_guid_compare_to()
    {
        // Verifica che l'ordine della griglia coincida con quello della stringa «D» ordinale (su .NET 10 Guid.CompareTo non diverge in pratica: non si può provare con esso).
        var rng = new Random(99);
        var ids = Pool(rng, 4000);
        var byString = ids.OrderBy(i => i.ToString("D"), StringComparer.Ordinal).ToList();

        Assert.Equal(byString, ReconciliationExpansionGrid.Ordered(ids));
        Assert.All(byString.Zip(byString.Skip(1)), pair => Assert.True(ReconciliationExpansionGrid.Compare(pair.First, pair.Second) < 0));
        Assert.Equal(0, ReconciliationExpansionGrid.Compare(ids[0], ids[0]));

        var low = Guid.Parse("00000001-0000-0000-0000-000000000000");
        var high = Guid.Parse("80000000-0000-0000-0000-000000000000");
        Assert.True(ReconciliationExpansionGrid.Compare(low, high) < 0);
        Assert.Equal(new[] { low, high }, ReconciliationExpansionGrid.Ordered([high, low]));

        // La stringa «D» è minuscola e ordinale: «a» (0x61) viene dopo «9» (0x39) e un confronto di cultura non deve influire.
        var digit = Guid.Parse("00000009-0000-0000-0000-000000000000");
        var letter = Guid.Parse("0000000a-0000-0000-0000-000000000000");
        Assert.Equal(new[] { digit, letter }, ReconciliationExpansionGrid.Ordered([letter, digit]));
    }

    [Fact]
    public void Tags_are_computed_over_whole_groups_not_chunks()
    {
        var shared = G(5);
        var s1 = ReconciliationExpansionGrid.Ordered([G(1), G(2), shared, G(7)]);
        var s2 = ReconciliationExpansionGrid.Ordered([G(10), shared]);
        var tags = ReconciliationExpansionGrid.Tags(s1, s2);

        // «shared» compare in un solo frammento di S1 e in un solo frammento di S2, ma resta Both per i gruppi interi.
        var leftChunks = ReconciliationExpansionGrid.Chunk(s1, [2, 2]);
        var rightChunks = ReconciliationExpansionGrid.Chunk(s2, [1, 1]);
        Assert.Contains(shared, leftChunks[1]);
        Assert.DoesNotContain(shared, leftChunks[0]);
        Assert.Equal(ExpansionTag.Both, tags[shared]);
        Assert.Equal(ExpansionTag.Left, tags[G(1)]);
        Assert.Equal(ExpansionTag.Right, tags[G(10)]);
        Assert.Equal(5, tags.Count);

        var blocks = ReconciliationExpansionGrid.Blocks(ReconciliationExpansionGrid.Cells(leftChunks, rightChunks), tags);
        Assert.True(ReconciliationExpansionGrid.Verify(s1, s2, blocks).Complete);
        Assert.Equal(rightChunks.Count * leftChunks.Count, blocks.Count + CountDropped(leftChunks, rightChunks, tags));
    }

    private static int CountDropped(IReadOnlyList<IReadOnlyList<Guid>> left, IReadOnlyList<IReadOnlyList<Guid>> right, IReadOnlyDictionary<Guid, ExpansionTag> tags) =>
        ReconciliationExpansionGrid.Cells(left, right).Count(c => !ReconciliationExpansionGrid.IsRelevant(ReconciliationExpansionGrid.UnionOf(c), tags));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    public void Empty_group_yields_no_blocks_and_complete_coverage(int n1, int n2)
    {
        var s1 = ReconciliationExpansionGrid.Ordered(Enumerable.Range(1, n1).Select(G));
        var s2 = ReconciliationExpansionGrid.Ordered(Enumerable.Range(100, n2).Select(G));
        var tags = ReconciliationExpansionGrid.Tags(s1, s2);
        var cells = ReconciliationExpansionGrid.Cells(ReconciliationExpansionGrid.Chunk(s1, n1 == 0 ? [] : [n1]), ReconciliationExpansionGrid.Chunk(s2, n2 == 0 ? [] : [n2]));

        Assert.Empty(cells);
        var blocks = ReconciliationExpansionGrid.Blocks(cells, tags);
        Assert.Empty(blocks);
        var coverage = ReconciliationExpansionGrid.Verify(s1, s2, blocks);
        Assert.Equal(0L, coverage.RequiredPairs);
        Assert.True(coverage.Complete);
    }

    [Fact]
    public void Ordered_rejects_duplicates_and_tags_reject_duplicates_within_a_group()
    {
        Assert.Throws<ArgumentException>(() => ReconciliationExpansionGrid.Ordered([G(1), G(2), G(1)]));
        Assert.Throws<ArgumentException>(() => ReconciliationExpansionGrid.Tags([G(1), G(1)], [G(2)]));
        Assert.Empty(ReconciliationExpansionGrid.Ordered([]));
    }

    [Fact]
    public void Chunk_validates_sizes_and_partitions_contiguously()
    {
        var ids = ReconciliationExpansionGrid.Ordered(Enumerable.Range(1, 5).Select(G));

        Assert.Throws<ArgumentException>(() => ReconciliationExpansionGrid.Chunk(ids, [2, 2]));
        Assert.Throws<ArgumentException>(() => ReconciliationExpansionGrid.Chunk(ids, [2, 4]));
        Assert.Throws<ArgumentException>(() => ReconciliationExpansionGrid.Chunk(ids, [0, 5]));
        Assert.Throws<ArgumentException>(() => ReconciliationExpansionGrid.Chunk(ids, [6, -1]));
        Assert.Throws<ArgumentException>(() => ReconciliationExpansionGrid.Chunk(ids, []));
        Assert.Empty(ReconciliationExpansionGrid.Chunk([], []));

        var chunks = ReconciliationExpansionGrid.Chunk(ids, [1, 3, 1]);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(ids, chunks.SelectMany(c => c).ToList());
        Assert.Equal([1, 3, 1], chunks.Select(c => c.Count).ToArray());
    }

    [Fact]
    public void Cells_follow_lexicographic_order_and_union_is_sorted_and_deduplicated()
    {
        var l = ReconciliationExpansionGrid.Chunk(ReconciliationExpansionGrid.Ordered([G(1), G(2), G(3)]), [2, 1]);
        var r = ReconciliationExpansionGrid.Chunk(ReconciliationExpansionGrid.Ordered([G(2), G(9)]), [1, 1]);
        var cells = ReconciliationExpansionGrid.Cells(l, r);

        Assert.Equal(4, cells.Count);
        Assert.Same(l[0], cells[0].Left);
        Assert.Same(r[0], cells[0].Right);
        Assert.Same(l[0], cells[1].Left);
        Assert.Same(r[1], cells[1].Right);
        Assert.Same(l[1], cells[2].Left);
        Assert.Same(r[0], cells[2].Right);
        Assert.Equal(new[] { G(1), G(2) }, ReconciliationExpansionGrid.UnionOf(cells[0]));
        Assert.Equal(new[] { G(1), G(2), G(9) }, ReconciliationExpansionGrid.UnionOf(cells[1]));
    }

    [Fact]
    public void Blocks_keep_the_first_of_equal_member_sets_and_number_them_in_input_order()
    {
        var s = ReconciliationExpansionGrid.Ordered([G(1), G(2), G(3), G(4)]);
        var tags = ReconciliationExpansionGrid.Tags(s, s);
        var a = new ExpansionCell([G(1), G(2)], [G(3)]);
        var mirror = new ExpansionCell([G(3)], [G(2), G(1)]);
        var other = new ExpansionCell([G(4)], [G(1)]);

        var blocks = ReconciliationExpansionGrid.Blocks([a, mirror, other], tags);

        Assert.Equal(2, blocks.Count);
        Assert.Equal([0, 1], blocks.Select(b => b.Ordinal).ToArray());
        Assert.Equal(new[] { G(1), G(2), G(3) }, blocks[0].Members);
        Assert.Same(a.Left, blocks[0].Left);
        Assert.Same(a.Right, blocks[0].Right);
        Assert.Equal(new[] { G(1), G(4) }, blocks[1].Members);
    }

    [Fact]
    public void Message_order_groups_left_then_both_then_right_each_by_d_ordinal()
    {
        var s1 = ReconciliationExpansionGrid.Ordered([G(1), G(4), G(6), G(8)]);
        var s2 = ReconciliationExpansionGrid.Ordered([G(4), G(6), G(2), G(9)]);
        var tags = ReconciliationExpansionGrid.Tags(s1, s2);

        var order = ReconciliationExpansionGrid.MessageOrder([G(9), G(6), G(2), G(8), G(4), G(1)], tags);

        Assert.Equal(new[] { G(1), G(8), G(4), G(6), G(2), G(9) }, order);
        Assert.Equal(0, ReconciliationExpansionGrid.OrderTag(ExpansionTag.Left));
        Assert.Equal(1, ReconciliationExpansionGrid.OrderTag(ExpansionTag.Both));
        Assert.Equal(2, ReconciliationExpansionGrid.OrderTag(ExpansionTag.Right));
    }

    [Fact]
    public void Split_returns_null_for_a_one_by_one_or_smaller_cell()
    {
        Assert.Null(ReconciliationExpansionGrid.Split(new ExpansionCell([G(1)], [G(2)])));
        Assert.Null(ReconciliationExpansionGrid.Split(new ExpansionCell([G(1)], [])));
        Assert.Null(ReconciliationExpansionGrid.Split(new ExpansionCell([], [])));
    }

    [Fact]
    public void Split_halves_the_larger_side_with_ceiling_left_half_and_left_wins_ties()
    {
        var left = new ExpansionCell([G(1), G(2), G(3), G(4), G(5)], [G(10), G(11)]);
        var parts = ReconciliationExpansionGrid.Split(left)!;
        Assert.Equal(2, parts.Count);
        Assert.Equal(new[] { G(1), G(2), G(3) }, parts[0].Left);
        Assert.Equal(new[] { G(4), G(5) }, parts[1].Left);
        Assert.Equal(left.Right, parts[0].Right);
        Assert.Equal(left.Right, parts[1].Right);

        var right = new ExpansionCell([G(1), G(2)], [G(10), G(11), G(12)]);
        var rightParts = ReconciliationExpansionGrid.Split(right)!;
        Assert.Equal(new[] { G(10), G(11) }, rightParts[0].Right);
        Assert.Equal(new[] { G(12) }, rightParts[1].Right);
        Assert.Equal(right.Left, rightParts[0].Left);

        var tie = new ExpansionCell([G(1), G(2)], [G(10), G(11)]);
        var tieParts = ReconciliationExpansionGrid.Split(tie)!;
        Assert.Equal(new[] { G(1) }, tieParts[0].Left);
        Assert.Equal(new[] { G(2) }, tieParts[1].Left);
        Assert.Equal(tie.Right, tieParts[0].Right);

        var oneByMany = ReconciliationExpansionGrid.Split(new ExpansionCell([G(1)], [G(10), G(11)]))!;
        Assert.Equal(new[] { G(10) }, oneByMany[0].Right);
        Assert.Equal(new[] { G(11) }, oneByMany[1].Right);
    }
}
