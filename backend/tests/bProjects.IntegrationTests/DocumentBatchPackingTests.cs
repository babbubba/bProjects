using System.Security.Cryptography;
using System.Text;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// T068 (DET) — Pack comune (AI §4.2): massimo prefisso contiguo misurato sul payload completo, token non additivi, riga/evidenza
/// irriducibile = errore con ID, contesto replicato non proprietario, output variabile, replay con stessi input/hash, batch derivati
/// con token/provenance e genitori. Nessun database, nessun LLM: misuratore finto (parole + cornice per membro + contesto fisso).
/// </summary>
public sealed class DocumentBatchPackingTests
{
    private sealed record Item(string Id, string Text);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Words(int count, string prefix) => string.Join(' ', Enumerable.Range(0, count).Select(i => $"{prefix}{i}"));

    private static Item MakeItem(int index, int words) => new($"E{index:D3}", Words(words, $"i{index}x"));

    private static int Count(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>40 fissi (istruzioni/schema) + <paramref name="context"/> replicato non proprietario + (5 + parole) per membro.</summary>
    private static PackMeasurer<Item> Measurer(int context = 0, TokenCountQuality quality = TokenCountQuality.Measured, List<int>? attempts = null) => (members, _) =>
    {
        var owned = members.Sum(m => Count(m.Text));
        var tokens = 40 + context + members.Sum(m => 5 + Count(m.Text));
        attempts?.Add(members.Count);
        return new ValueTask<UnitMeasure>(new UnitMeasure(tokens, quality, Sha(string.Join('|', members.Select(m => m.Id)) + "#" + context), owned));
    };

    private static PlannerLimits Limits(int input, int output = 1_000_000) => new(input, output);

    private static readonly Func<Item, string> IdOf = i => i.Id;

    [Fact]
    public async Task The_pack_is_the_maximal_contiguous_prefix_each_batch_within_budget_and_every_item_exactly_once_in_order()
    {
        var items = Enumerable.Range(0, 7).Select(i => MakeItem(i, 100)).ToArray();   // 105 l'uno: 3 = 355 <= 400, 4 = 460 > 400
        var attempts = new List<int>();

        var batches = await DocumentBatchPacking.PackLevelAsync(items, 1, Limits(400), IdOf, Measurer(attempts: attempts), Ct);

        Assert.Equal([3, 3, 1], batches.Select(b => b.Members.Count));
        Assert.Equal(items.Select(i => i.Id), batches.SelectMany(b => b.Members).Select(m => m.Id));
        Assert.Equal([0, 1, 2], batches.Select(b => b.Ordinal));
        Assert.All(batches, b => Assert.InRange(b.Measure!.Tokens, 1, 400));
        Assert.Contains(4, attempts);   // il quarto membro è stato provato sul payload completo prima di essere escluso
    }

    [Fact]
    public async Task Tokens_are_not_additive_the_measured_whole_decides_not_the_sum_of_the_parts()
    {
        var items = Enumerable.Range(0, 4).Select(i => MakeItem(i, 100)).ToArray();
        PackMeasurer<Item> shared = (members, _) =>
            new ValueTask<UnitMeasure>(new UnitMeasure(members.Count == 1 ? 150 : 100 + 10 * members.Count, TokenCountQuality.Measured, Sha(string.Join(',', members.Select(m => m.Id))), 100 * members.Count));

        var batches = await DocumentBatchPacking.PackLevelAsync(items, 1, Limits(200), IdOf, shared, Ct);

        Assert.Single(batches);   // 4 x 150 additivi sarebbero quattro batch; il payload intero misura 140
        Assert.Equal(140, batches[0].Measure!.Tokens);
    }

    [Fact]
    public async Task The_budget_boundary_is_inclusive_one_token_more_closes_the_batch()
    {
        var items = new[] { MakeItem(0, 10), MakeItem(1, 10) };   // 2 membri = 40 + 30 = 70

        var exact = await DocumentBatchPacking.PackLevelAsync(items, 1, Limits(70), IdOf, Measurer(), Ct);
        var over = await DocumentBatchPacking.PackLevelAsync(items, 1, Limits(69), IdOf, Measurer(), Ct);

        Assert.Single(exact);
        Assert.Equal(2, over.Count);
    }

    [Fact]
    public async Task An_irreducible_item_fails_with_its_id_and_reason_without_cutting_or_dropping_it()
    {
        var items = new[] { MakeItem(0, 10), MakeItem(1, 900), MakeItem(2, 10) };

        var failure = await Assert.ThrowsAsync<DocumentWorkException>(() => DocumentBatchPacking.PackLevelAsync(items, 1, Limits(500), IdOf, Measurer(), Ct).AsTask());

        Assert.Equal(DocumentWorkException.DocumentIncomplete, failure.Code);
        Assert.Contains("E001", failure.Message);
        Assert.DoesNotContain("i1x5", failure.Message);   // nessun testo nei messaggi
    }

    [Fact]
    public async Task The_variable_output_estimate_bounds_the_batch_even_when_the_input_window_is_large()
    {
        var items = Enumerable.Range(0, 6).Select(i => MakeItem(i, 50)).ToArray();

        var batches = await DocumentBatchPacking.PackLevelAsync(items, 1, new PlannerLimits(10_000, 900, 8), IdOf, Measurer(), Ct);

        Assert.Equal([2, 2, 2], batches.Select(b => b.Members.Count));   // 100 parole x 8 = 800 <= 900; 150 x 8 = 1200 > 900
        Assert.All(batches, b => Assert.True(Math.Ceiling(b.Measure!.OwnedTokens * 8.0) <= 900));
    }

    [Fact]
    public async Task A_single_item_that_exceeds_only_the_output_estimate_is_irreducible_with_its_id()
    {
        var failure = await Assert.ThrowsAsync<DocumentWorkException>(
            () => DocumentBatchPacking.PackLevelAsync([MakeItem(7, 200)], 1, new PlannerLimits(10_000, 900, 8), IdOf, Measurer(), Ct).AsTask());

        Assert.Equal(DocumentWorkException.DocumentIncomplete, failure.Code);
        Assert.Contains("E007", failure.Message);
    }

    [Fact]
    public async Task Replicated_context_is_counted_once_per_payload_and_never_as_owned_text()
    {
        var items = new[] { MakeItem(0, 20), MakeItem(1, 20) };

        var batches = await DocumentBatchPacking.PackLevelAsync(items, 1, Limits(1000), IdOf, Measurer(context: 300), Ct);

        var batch = Assert.Single(batches);
        Assert.Equal(40 + 300 + 2 * 25, batch.Measure!.Tokens);   // contesto una volta sola, non per membro
        Assert.Equal(40, batch.Measure.OwnedTokens);               // il contesto non è mai proprietario
    }

    [Fact]
    public async Task An_unavailable_token_count_is_runtime_unverified_never_an_estimate()
    {
        var failure = await Assert.ThrowsAsync<RuntimeUnverifiedException>(
            () => DocumentBatchPacking.PackLevelAsync([MakeItem(0, 5)], 1, Limits(1000), IdOf, Measurer(quality: TokenCountQuality.Unavailable), Ct).AsTask());

        Assert.Equal("runtime_unverified", failure.Code);
    }

    [Fact]
    public async Task Reduction_levels_pack_at_least_two_descriptors_carry_a_last_single_node_and_strictly_shrink()
    {
        var items = Enumerable.Range(0, 5).Select(i => MakeItem(i, 50)).ToArray();   // 55 l'uno: 3 = 205 <= 250

        var batches = await DocumentBatchPacking.PackLevelAsync(items, 2, Limits(250), IdOf, Measurer(), Ct);

        Assert.Equal([3, 2], batches.Select(b => b.Members.Count));
        Assert.True(batches.Count < items.Length);

        var odd = await DocumentBatchPacking.PackLevelAsync(items.Take(3).ToArray(), 2, Limits(150), IdOf, Measurer(), Ct);
        Assert.Equal([2, 1], odd.Select(b => b.Members.Count));
        Assert.False(odd[0].Carried);
        Assert.True(odd[1].Carried);              // ultimo nodo singolo: inoltrato, nessuna AI
        Assert.Null(odd[1].Measure);
        Assert.Equal("E002", odd[1].Members[0].Id);
    }

    [Fact]
    public async Task Two_descriptors_that_do_not_fit_together_are_reconciliation_irreducible_naming_both_ids()
    {
        var items = new[] { MakeItem(0, 100), MakeItem(1, 100) };   // 40 + 210 = 250 > 200

        var failure = await Assert.ThrowsAsync<DocumentWorkException>(() => DocumentBatchPacking.PackLevelAsync(items, 2, Limits(200), IdOf, Measurer(), Ct).AsTask());

        Assert.Equal(DocumentWorkException.ReconciliationIrreducible, failure.Code);
        Assert.Contains("E000", failure.Message);
        Assert.Contains("E001", failure.Message);
    }

    [Fact]
    public async Task Packing_is_incremental_the_first_batch_does_not_depend_on_items_that_do_not_exist_yet()
    {
        var all = Enumerable.Range(0, 7).Select(i => MakeItem(i, 100)).ToArray();
        var attempts = new List<int>();

        var early = await DocumentBatchPacking.PackNextAsync(all.Take(4).ToArray(), 0, 0, 1, Limits(400), IdOf, Measurer(attempts: attempts), Ct);
        var late = await DocumentBatchPacking.PackNextAsync(all, 0, 0, 1, Limits(400), IdOf, Measurer(), Ct);
        var second = await DocumentBatchPacking.PackNextAsync(all, early.Members.Count, 1, 1, Limits(400), IdOf, Measurer(), Ct);

        Assert.Equal(late.Members.Select(m => m.Id), early.Members.Select(m => m.Id));
        Assert.Equal(late.Measure!.PayloadHash, early.Measure!.PayloadHash);
        Assert.Equal(["E003", "E004", "E005"], second.Members.Select(m => m.Id));   // riparte esattamente dopo il prefisso già impacchettato
        Assert.Equal(4, attempts.Max());
    }

    [Fact]
    public async Task The_same_inputs_and_measure_give_the_same_batches_and_the_same_derived_identities()
    {
        Item[] Build() => [.. Enumerable.Range(0, 9).Select(i => MakeItem(i, 80))];

        var a = await DocumentBatchPacking.PackLevelAsync(Build(), 1, Limits(300), IdOf, Measurer(), Ct);
        var b = await DocumentBatchPacking.PackLevelAsync(Build(), 1, Limits(300), IdOf, Measurer(), Ct);

        Assert.Equal(a.Select(x => x.Measure!.PayloadHash), b.Select(x => x.Measure!.PayloadHash));
        string[] parents = [Sha("p1"), Sha("p0")];
        var da = a.Select(x => DocumentBatchPacking.ToDerived(x, "ProcessingRun:r", "manifest", "def-1", "pack", "A", 0, parents, IdOf, "schema-1", "prompt-1")).ToArray();
        var db = b.Select(x => DocumentBatchPacking.ToDerived(x, "ProcessingRun:r", "manifest", "def-1", "pack", "A", 0, parents, IdOf, "schema-1", "prompt-1")).ToArray();
        Assert.Equal(da.Select(d => d.BatchId), db.Select(d => d.BatchId));
        Assert.Equal(da.Count(), da.Select(d => d.BatchId).Distinct().Count());
    }

    [Fact]
    public async Task A_derived_batch_records_tokens_provenance_ordinal_members_and_parent_hashes_and_depends_on_them()
    {
        var items = Enumerable.Range(0, 4).Select(i => MakeItem(i, 100)).ToArray();
        var packed = await DocumentBatchPacking.PackLevelAsync(items, 1, Limits(400), IdOf, Measurer(), Ct);
        var calibrated = await DocumentBatchPacking.PackLevelAsync(items, 1, Limits(400), IdOf, Measurer(quality: TokenCountQuality.CalibratedUpperBound), Ct);
        string[] parents = [Sha("out-a"), Sha("out-b")];

        var derived = DocumentBatchPacking.ToDerived(packed[1], "ProcessingRun:r", "manifest", "def-1", "pack", "A", 2, parents, IdOf, "schema-1", "prompt-1");

        Assert.Equal(packed[1].Measure!.PayloadHash, derived.InputHash);
        Assert.Equal(packed[1].Measure!.Tokens, derived.MeasuredTokens);
        Assert.Equal("measured", derived.TokenProvenance);
        Assert.Equal("calibrated_upper_bound", DocumentBatchPacking.ToDerived(calibrated[1], "ProcessingRun:r", "manifest", "def-1", "pack", "A", 2, parents, IdOf, "schema-1", "prompt-1").TokenProvenance);
        Assert.Equal(1, derived.Ordinal);
        Assert.Equal("pack", derived.Stage);
        Assert.Equal("A", derived.View);
        Assert.Equal(2, derived.Level);
        Assert.Equal(parents, derived.ParentOutputHashes);
        Assert.Contains("E003", derived.InputIdsJson);
        Assert.DoesNotContain("i3x", derived.InputIdsJson);   // ID e range, mai testo

        var otherParents = DocumentBatchPacking.ToDerived(packed[1], "ProcessingRun:r", "manifest", "def-1", "pack", "A", 2, [Sha("out-a"), Sha("out-c")], IdOf, "schema-1", "prompt-1");
        Assert.NotEqual(derived.BatchId, otherParents.BatchId);   // i genitori Committed fanno parte dell'identità del batch
    }

    [Fact]
    public async Task A_carried_node_is_never_turned_into_a_derived_batch()
    {
        var odd = await DocumentBatchPacking.PackLevelAsync(Enumerable.Range(0, 3).Select(i => MakeItem(i, 10)).ToArray(), 2, Limits(75), IdOf, Measurer(), Ct);
        var carried = odd[^1];
        Assert.True(carried.Carried);

        Assert.Throws<InvalidOperationException>(() => DocumentBatchPacking.ToDerived(carried, "ProcessingRun:r", "manifest", "def-1", "pack", "A", 1, [], IdOf, "schema-1", "prompt-1"));
    }
}
