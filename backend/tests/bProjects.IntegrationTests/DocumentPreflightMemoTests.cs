using System.Text;
using bProjects.App.AI;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-030 (P-1) e RH-031 — memo dei token di schema e strumenti (<see cref="ExtraTokenMemo"/>), OFFLINE: runtime llama.cpp FINTO, nessuna rete, nessun database.
/// Condizione di arresto: se un solo <c>Tokens</c> o <c>PayloadHash</c> differisce con il memo, la modifica si ferma. Il memo riguarda SOLO <c>ResponseSchemaJson</c> e <c>ToolsJson</c>; il messaggio reso e i
/// messaggi non si memoizzano mai.
/// </summary>
[Trait("Category", "Offline")]
public sealed class DocumentPreflightMemoTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string V10 = DocumentAnalysisWorkflow.DefinitionVersionV10;
    private const string V6 = DocumentAnalysisWorkflow.DefinitionVersionV6;
    private const string V11 = DocumentAnalysisWorkflow.DefinitionVersionV11;

    private static readonly string[] Schemas =
    [
        """{"type":"object","properties":{"a":{"type":"string"}}}""",
        """{"type":"object","properties":{"b":{"type":"array","items":{"type":"integer"}}}}""",
        """{"type":"object","required":["c"],"properties":{"c":{"type":"boolean"}}}""",
    ];

    private static DocumentPayload PayloadOf(int i, string? tools = null) =>
        new(AiPhase.Analysis, "istruzioni", [new ChatMessage(ChatRole.User, $"Tipo di batch: foglia\nVista: A\n<<<VOCE [E{i}] testo numero {i}")], Schemas[i % Schemas.Length], tools);

    [Fact]
    public async Task One_hundred_measures_with_three_schemas_have_identical_tokens_and_hashes_and_tokenize_requests_drop_by_exactly_measures_minus_three()
    {
        using var rig = await MeasurementRig.CreateAsync(Ct);
        var fingerprint = rig.Profile.Fingerprint;

        var before = rig.Runtime.Count("/tokenize");
        var plain = new List<PayloadPreflight>();
        for (var i = 0; i < 100; i++)
        {
            plain.Add(await rig.Preflight.MeasureAsync(PayloadOf(i), Ct, fingerprint));
        }

        var plainRequests = rig.Runtime.Count("/tokenize") - before;

        var memo = new ExtraTokenMemo();
        before = rig.Runtime.Count("/tokenize");
        var memoized = new List<PayloadPreflight>();
        for (var i = 0; i < 100; i++)
        {
            memoized.Add(await rig.Preflight.MeasureAsync(PayloadOf(i), Ct, fingerprint, memo));
        }

        var memoRequests = rig.Runtime.Count("/tokenize") - before;

        Assert.Equal(plain.Select(p => (p.Tokens, p.PayloadHash, p.Quality)), memoized.Select(p => (p.Tokens, p.PayloadHash, p.Quality)));
        Assert.Equal(200, plainRequests);                                // 100 testi resi + 100 schemi
        Assert.Equal(plainRequests - (100 - 3), memoRequests);           // (misure − schemi distinti) richieste in meno, esattamente
        Assert.Equal(103, memoRequests);
        Assert.Equal(3, memo.Entries);
    }

    [Fact]
    public async Task The_rendered_message_and_the_messages_are_never_memoized()
    {
        using var rig = await MeasurementRig.CreateAsync(Ct);
        var memo = new ExtraTokenMemo();
        var payload = PayloadOf(1);
        var before = rig.Runtime.Count("/tokenize");
        await rig.Preflight.MeasureAsync(payload, Ct, rig.Profile.Fingerprint, memo);
        await rig.Preflight.MeasureAsync(payload, Ct, rig.Profile.Fingerprint, memo);
        Assert.Equal(2 + 1, rig.Runtime.Count("/tokenize") - before);   // due testi resi (stesso payload!) e un solo schema
        Assert.Equal(1, memo.Entries);
    }

    [Fact]
    public async Task Tools_json_is_memoized_like_the_schema_and_both_keep_the_same_total()
    {
        using var rig = await MeasurementRig.CreateAsync(Ct);
        var tools = """[{"name":"cerca","description":"strumento di prova"}]""";
        var plain = await rig.Preflight.MeasureAsync(PayloadOf(1, tools), Ct, rig.Profile.Fingerprint);
        var memo = new ExtraTokenMemo();
        var first = await rig.Preflight.MeasureAsync(PayloadOf(1, tools), Ct, rig.Profile.Fingerprint, memo);
        var before = rig.Runtime.Count("/tokenize");
        var second = await rig.Preflight.MeasureAsync(PayloadOf(1, tools), Ct, rig.Profile.Fingerprint, memo);
        Assert.Equal((plain.Tokens, plain.PayloadHash), (first.Tokens, first.PayloadHash));
        Assert.Equal((plain.Tokens, plain.PayloadHash), (second.Tokens, second.PayloadHash));
        Assert.Equal(1, rig.Runtime.Count("/tokenize") - before);       // solo il testo reso: schema e strumenti dal memo
        Assert.Equal(2, memo.Entries);
    }

    [Fact]
    public async Task A_different_runtime_fingerprint_never_reuses_a_count()
    {
        using var small = await MeasurementRig.CreateAsync(Ct);
        using var large = await MeasurementRig.CreateAsync(Ct, window: 65536);
        Assert.NotEqual(small.Profile.Fingerprint, large.Profile.Fingerprint);

        var memo = new ExtraTokenMemo();
        await small.Preflight.MeasureAsync(PayloadOf(1), Ct, small.Profile.Fingerprint, memo);
        var before = large.Runtime.Count("/tokenize", MeasureKinds.Leaf, "extra");
        await large.Preflight.MeasureAsync(PayloadOf(1), Ct, large.Profile.Fingerprint, memo);
        Assert.Equal(1, large.Runtime.Count("/tokenize", MeasureKinds.Leaf, "extra") - before);   // impronta diversa ⇒ nessun riuso
        Assert.Equal(2, memo.Entries);
    }

    [Fact]
    public async Task Without_a_memo_the_behaviour_is_the_historical_one()
    {
        using var rig = await MeasurementRig.CreateAsync(Ct);
        var before = rig.Runtime.Count("/tokenize");
        await rig.Preflight.MeasureAsync(PayloadOf(1), Ct, rig.Profile.Fingerprint);
        await rig.Preflight.MeasureAsync(PayloadOf(1), Ct, rig.Profile.Fingerprint);
        Assert.Equal(4, rig.Runtime.Count("/tokenize") - before);
    }

    // ================================================================ equivalenza sul ledger di RH-004 (piano di produzione)

    public static TheoryData<string, bool> Configurations() => new() { { V10, false }, { V10, true }, { V6, false }, { V6, true } };

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task The_production_plan_with_the_memo_has_the_same_batches_and_measures_as_without_it(string definition, bool facts)
    {
        var baseline = await ReconcileMeasurementBudgetTests.MeasureAsync(definition, facts, Ct, memo: false);
        var memoized = await ReconcileMeasurementBudgetTests.MeasureAsync(definition, facts, Ct, memo: true);

        // Stesso primo batch (confini, hash di input, token misurati) e stesse sequenze di Pack delle foglie.
        Assert.Equal((baseline.First.BatchId, baseline.First.InputHash, baseline.First.MeasuredTokens, baseline.First.Ordinal, baseline.First.TokenProvenance),
            (memoized.First.BatchId, memoized.First.InputHash, memoized.First.MeasuredTokens, memoized.First.Ordinal, memoized.First.TokenProvenance));
        Assert.True(baseline.First.InputIdsJson == memoized.First.InputIdsJson);
        Assert.Equal(baseline.LeafRuns(), memoized.LeafRuns());

        // `/apply-template` invariate; `/tokenize` dei testi resi invariate; solo i `/tokenize` di schema si riducono ai distinti.
        Assert.Equal(baseline.ApplyTotal, memoized.ApplyTotal);
        Assert.Equal(baseline.Apply(MeasureKinds.Leaf), memoized.Apply(MeasureKinds.Leaf));
        Assert.Equal(baseline.TokenizeRole(MeasureKinds.Leaf, "rendered"), memoized.TokenizeRole(MeasureKinds.Leaf, "rendered"));
        Assert.Equal(baseline.Tokenize(MeasureKinds.Page), memoized.Tokenize(MeasureKinds.Page));
        var distinctSchemas = baseline.Log.Where(r => r.Path == "/tokenize" && r.Role == "extra").Select(r => r.Characters).Distinct().Count();
        Assert.Equal(distinctSchemas, memoized.Log.Count(r => r.Path == "/tokenize" && r.Role == "extra"));
        Assert.Equal(
            baseline.Log.Where(r => r.Path == "/apply-template").Select(r => (r.Kind, r.Role, r.Entries, r.View, r.Repeated, r.Characters)),
            memoized.Log.Where(r => r.Path == "/apply-template").Select(r => (r.Kind, r.Role, r.Entries, r.View, r.Repeated, r.Characters)));
    }

    // ================================================================ RH-031 — misura della v11 e della v10 con P-1

    private sealed record Row(string Label, int Apply, int Tokenize, int Schema)
    {
        public int Total => Apply + Tokenize;
    }

    private static Row RowOf(string label, ReconcileMeasurementBudgetTests.Measured m) =>
        new(label, m.ApplyTotal, m.TokenizeTotal, m.Log.Count(r => r.Path == "/tokenize" && r.Role == "extra"));

    [Fact]
    public async Task V11_with_P1_costs_no_more_than_v10_without_it_and_the_tokenize_reduction_equals_schema_measures_minus_distinct_schemas_at_first_start_and_in_a_new_process()
    {
        var table = new StringBuilder();
        table.AppendLine("| Scenario | Definizione | P-1 | `/apply-template` | `/tokenize` | Totale | `/tokenize` di schema |");
        table.AppendLine("|---|---|---|---:|---:|---:|---:|");

        foreach (var scenario in new[] { "primo avvio", "processo nuovo" })
        {
            // «Processo nuovo»: il memo ha la vita del piano e il piano si ricostruisce da zero (rig, runtime e piano nuovi): costo identico al primo avvio.
            var v10Without = RowOf("v10", await ReconcileMeasurementBudgetTests.MeasureAsync(V10, false, Ct, memo: false));
            var v10With = RowOf("v10", await ReconcileMeasurementBudgetTests.MeasureAsync(V10, false, Ct, memo: true));
            var v11With = RowOf("v11", await ReconcileMeasurementBudgetTests.MeasureAsync(V11, false, Ct, memo: true));

            Assert.True(v11With.Total <= v10Without.Total, $"{scenario}: v11 con P-1 ({v11With.Total}) > v10 senza P-1 ({v10Without.Total})");
            var distinct = v10With.Schema;
            Assert.True(distinct is >= 1 and <= 3, $"schemi distinti attesi pochi, osservati {distinct}");
            Assert.Equal(v10Without.Tokenize - v10With.Tokenize, v10Without.Schema - distinct);      // riduzione = misure con schema − schemi distinti
            Assert.Equal(v10Without.Apply, v10With.Apply);

            table.AppendLine($"| {scenario} | v10 | no | {v10Without.Apply} | {v10Without.Tokenize} | {v10Without.Total} | {v10Without.Schema} |");
            table.AppendLine($"| {scenario} | v10 | sì | {v10With.Apply} | {v10With.Tokenize} | {v10With.Total} | {v10With.Schema} |");
            table.AppendLine($"| {scenario} | v11 | sì | {v11With.Apply} | {v11With.Tokenize} | {v11With.Total} | {v11With.Schema} |");
        }

        var directory = Path.Combine(Path.GetTempPath(), "bprojects-reconcile-measurement");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "rh031-evidence.md"), table.ToString());
        TestContext.Current.SendDiagnosticMessage(table.ToString());
    }
}
