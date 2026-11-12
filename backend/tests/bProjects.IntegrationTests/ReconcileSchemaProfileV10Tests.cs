using System.Text.Json.Nodes;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003, T040 dopo il Run 4 — profilo degli schemi per batch <c>reconcile-alias-schema-by-batch-3</c> (definizione v10): le sei varianti del profilo -2, byte per byte, tranne l'espansione che riceve
/// <c>candidates.maxItems = 16</c> (protezione contro l'enumerazione quadratica, non selezione). I profili -1 e -2 restano congelati: i loro hash sono quelli storici.
/// </summary>
public sealed class ReconcileSchemaProfileV10Tests
{
    private static readonly ReconcileBatchSchema[] All = Enum.GetValues<ReconcileBatchSchema>();

    // Golden del profilo -3: un run v10 congelato dipende da questi byte. Ogni cambiamento richiede un nuovo profilo e una nuova definizione.
    private const string PinnedV10ProfileHashes =
        "LeafA=17001aef9ed768714129aba3f5d4b861e8e7d36632e6eaf78c7cf60c1ee49f09,LeafB=800f277ba1daa4d14ed0694d2dbea68add81ebb8dd1915817ab1df3a6a1aa4ce,"
        + "Reduce=06058502b68f2d19363b1bb408bfd613b4ccafd3f8e6453c3c32eba43ba56b6c,Cross=bab4a2acd070c70569fb5b80c7ec251a1f9bff809f8f6e701d83dda6e659d5f9,"
        + "Expand=02490207aef819a90c708468ec75a43439695af6863bcc762fdf8fe49e45d89b,Compare=b8c471d1b076af5e9fae32761d66966315fefc1b05f5c4c667714dcf808914a3";

    private static Dictionary<string, string> Parse(string hashes) => hashes.Split(',').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);

    [Fact]
    public void The_v10_profile_has_six_variants_five_identical_to_the_v9_ones_and_only_the_expansion_different()
    {
        var v9 = Parse(ReconcileSchemaProfileV9Tests.PinnedV9ProfileHashes);
        var v10 = Parse(ReconcileAliasContracts.SchemaProfileV3Hashes());

        Assert.Equal(["LeafA", "LeafB", "Reduce", "Cross", "Expand", "Compare"], v10.Keys);
        foreach (var name in new[] { "LeafA", "LeafB", "Reduce", "Cross", "Compare" })
        {
            Assert.Equal(v9[name], v10[name]);                                   // hash identici
        }

        Assert.NotEqual(v9["Expand"], v10["Expand"]);
        foreach (var variant in All.Where(v => v != ReconcileBatchSchema.Expand))
        {
            Assert.Equal(ReconcileAliasContracts.SchemaOf(variant).GetRawText(), ReconcileAliasContracts.SchemaOfV3(variant).GetRawText());   // testo identico
        }
    }

    [Fact]
    public void The_expansion_variant_differs_from_the_v9_one_only_by_the_candidates_cap_next_to_items()
    {
        var v9 = JsonNode.Parse(ReconcileAliasContracts.SchemaOf(ReconcileBatchSchema.Expand).GetRawText())!.AsObject();
        var v10 = JsonNode.Parse(ReconcileAliasContracts.SchemaOfV3(ReconcileBatchSchema.Expand).GetRawText())!.AsObject();

        Assert.Equal(16, v10["properties"]!["candidates"]!["maxItems"]!.GetValue<int>());
        Assert.Equal(ReconcileAliasContracts.ExpansionCandidateCap, v10["properties"]!["candidates"]!["maxItems"]!.GetValue<int>());
        Assert.NotNull(v10["properties"]!["candidates"]!["items"]);              // maxItems accanto a items: il costrutto che il convertitore di llama.cpp applica
        Assert.Null(v9["properties"]!["candidates"]!["maxItems"]);
        v10["properties"]!["candidates"]!.AsObject().Remove("maxItems");
        Assert.Equal(v9.ToJsonString(), v10.ToJsonString());                     // tolto il tetto, lo schema è quello del -2
    }

    [Fact]
    public void Only_the_expansion_has_a_candidates_cap_in_the_v10_profile()
    {
        foreach (var variant in All)
        {
            var maxItems = JsonNode.Parse(ReconcileAliasContracts.SchemaOfV3(variant).GetRawText())!["properties"]!["candidates"]!["maxItems"];
            Assert.Equal(variant == ReconcileBatchSchema.Expand ? 16 : null, maxItems?.GetValue<int>());
        }
    }

    [Fact]
    public void The_v10_profile_hashes_are_pinned_and_the_historical_profiles_are_unchanged()
    {
        var v10 = ReconcileAliasContracts.SchemaProfileV3Hashes();
        Assert.True(PinnedV10ProfileHashes == v10, v10);
        Assert.Equal(v10, ReconcileAliasContracts.ProfileHashes(ReconcileAliasContracts.SchemaProfileByBatchV3));
        Assert.Equal("reconcile-alias-schema-by-batch-3", ReconcileAliasContracts.SchemaProfileByBatchV3);

        // Profili storici: invariati byte per byte (il -2 dal golden della v9; il -1 dal profilo v8).
        Assert.True(ReconcileSchemaProfileV9Tests.PinnedV9ProfileHashes == ReconcileAliasContracts.SchemaProfileV2Hashes());
        Assert.Equal(ReconcileAliasContracts.SchemaProfileHashes(), ReconcileAliasContracts.ProfileHashes(ReconcileAliasContracts.SchemaProfileByBatch));
        Assert.Equal(ReconcileSchemaProfileV9Tests.PinnedV9ProfileHashes, ReconcileAliasContracts.ProfileHashes(ReconcileAliasContracts.SchemaProfileByBatchV2));
        Assert.NotEqual(ReconcileAliasContracts.SchemaProfileV2Hashes(), v10);
    }

    [Theory]
    [InlineData("leaf", ReconcileView.A, ReconcileBatchSchema.LeafA)]
    [InlineData("leaf", ReconcileView.B, ReconcileBatchSchema.LeafB)]
    [InlineData("reduce", ReconcileView.A, ReconcileBatchSchema.Reduce)]
    [InlineData("cross", ReconcileView.Cross, ReconcileBatchSchema.Cross)]
    [InlineData("expand", ReconcileView.A, ReconcileBatchSchema.Expand)]
    [InlineData("expand", ReconcileView.Cross, ReconcileBatchSchema.Expand)]
    [InlineData("compare", ReconcileView.B, ReconcileBatchSchema.Compare)]
    public void The_v10_profile_dispatches_the_stage_variant_with_its_own_schema_and_never_the_v9_one_for_the_expansion(string stage, ReconcileView view, ReconcileBatchSchema expected)
    {
        var (name, schema) = ReconcileAliasContracts.ProfileSchemaFor(ReconcileAliasContracts.SchemaProfileByBatchV3, stage, view);

        Assert.Equal(expected.ToString(), name);
        Assert.Equal(ReconcileAliasContracts.SchemaOfV3(expected).GetRawText(), schema.GetRawText());
        Assert.Equal(["LeafA", "LeafB", "Reduce", "Cross", "Expand", "Compare"], ReconcileAliasContracts.ProfileVariants(ReconcileAliasContracts.SchemaProfileByBatchV3).Select(v => v.Name));
        Assert.Equal(
            ReconcileAliasContracts.ProfileVariants(ReconcileAliasContracts.SchemaProfileByBatchV3).Select(v => v.Schema.GetRawText()),
            All.Select(v => ReconcileAliasContracts.SchemaOfV3(v).GetRawText()));
    }

    [Fact]
    public void Unknown_profiles_and_variants_still_throw_and_never_fall_back()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.ProfileSchemaFor("reconcile-alias-schema-by-batch-4", "expand", ReconcileView.A));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.ProfileVariants("reconcile-alias-schema-by-batch-4"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.ProfileHashes("reconcile-alias-schema-by-batch-4"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.SchemaOfV3((ReconcileBatchSchema)99));
    }
}
