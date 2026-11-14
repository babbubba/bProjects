using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-011 (M1) — profilo degli schemi <c>reconcile-alias-schema-by-stage-1</c> della definizione v11: sei varianti derivate dalla tabella di stadio
/// (<see cref="ReconcileStageContracts.Table"/>). Verifica: golden degli hash (LeafA e LeafB identici), solo keyword del sottoinsieme (<see cref="ReconcileSchemaSubset.ProviderProblems"/> vuoto), corrispondenza
/// campo per campo con la tabella, legame <c>op</c> ⇔ <c>value</c> delle patch (aggiornamento M1 per C-4/C-5), regola del focus del confronto e profili -1/-2/-3 invariati. Nessun database, nessuna inferenza.
/// </summary>
public sealed class ReconcileSchemaProfileByStageTests
{
    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Canon(JsonElement schema) => CanonicalJson.Canonicalize(schema.GetRawText());

    private static string Hash(JsonElement schema) => CanonicalJson.HashText(Canon(schema));

    private static JsonObject Node(JsonElement schema) => JsonNode.Parse(schema.GetRawText())!.AsObject();

    public static TheoryData<ReconcileBatchSchema> Variants() => new()
    {
        ReconcileBatchSchema.LeafA, ReconcileBatchSchema.LeafB, ReconcileBatchSchema.Reduce, ReconcileBatchSchema.Cross, ReconcileBatchSchema.Expand, ReconcileBatchSchema.Compare,
    };

    // ---------------------------------------------------------------- golden

    /// <summary>Hash canonici delle sei varianti (golden RH-011): LeafA e LeafB coincidono (LeafA = copia esatta di LeafB del -2). Un cambiamento è una nuova definizione, non un ritocco.</summary>
    private static readonly IReadOnlyDictionary<ReconcileBatchSchema, string> Goldens = new Dictionary<ReconcileBatchSchema, string>
    {
        [ReconcileBatchSchema.LeafA] = "800f277ba1daa4d14ed0694d2dbea68add81ebb8dd1915817ab1df3a6a1aa4ce",
        [ReconcileBatchSchema.LeafB] = "800f277ba1daa4d14ed0694d2dbea68add81ebb8dd1915817ab1df3a6a1aa4ce",
        [ReconcileBatchSchema.Reduce] = "06058502b68f2d19363b1bb408bfd613b4ccafd3f8e6453c3c32eba43ba56b6c",
        [ReconcileBatchSchema.Cross] = "bab4a2acd070c70569fb5b80c7ec251a1f9bff809f8f6e701d83dda6e659d5f9",
        [ReconcileBatchSchema.Expand] = "b7961b459461ee526bae3a65db40df3692973f83d5e0c5c71128b1c6a30042b2",
        [ReconcileBatchSchema.Compare] = "2830aebf25fff4f811dca3b5311f35eb219000544f8a4106054a0321a37ea200",
    };

    [Fact]
    public void The_six_variant_hashes_are_the_pinned_ones_and_leaf_a_and_leaf_b_are_the_same_schema()
    {
        var actual = Enum.GetValues<ReconcileBatchSchema>().ToDictionary(v => v, v => Hash(ReconcileAliasContracts.SchemaOfByStage(v)));
        var report = string.Join('\n', actual.Select(kv => $"{kv.Key}={kv.Value}"));
        Assert.True(Goldens.All(kv => actual[kv.Key] == kv.Value), "Hash diversi dai golden. Valori reali:\n" + report);
        Assert.Equal(actual[ReconcileBatchSchema.LeafA], actual[ReconcileBatchSchema.LeafB]);
        Assert.Equal(5, actual.Values.Distinct().Count());                                         // sei varianti, cinque schemi distinti
        Assert.Equal(string.Join(',', actual.Select(kv => $"{kv.Key}={kv.Value}")), ReconcileAliasContracts.SchemaProfileByStageHashes());
        Assert.Equal(ReconcileAliasContracts.SchemaProfileByStageHashes(), ReconcileAliasContracts.ProfileHashes(ReconcileAliasContracts.SchemaProfileByStage));
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void Every_variant_uses_only_keywords_the_provider_converter_handles(ReconcileBatchSchema variant)
    {
        Assert.Empty(ReconcileSchemaSubset.ProviderProblems(ReconcileAliasContracts.SchemaOfByStage(variant)));
    }

    // ---------------------------------------------------------------- tabella ⇔ schema, campo per campo

    [Fact]
    public void The_stage_table_has_one_row_per_variant_with_the_approved_values()
    {
        Assert.Equal(Enum.GetValues<ReconcileBatchSchema>(), ReconcileStageContracts.Table.Select(c => c.Variant));
        Assert.All(ReconcileStageContracts.Table, c => Assert.False(c.ModelItems));                // nella v11 il modello non produce item in nessuno stadio
        Assert.Equal([ReconcileBatchSchema.LeafA], ReconcileStageContracts.Table.Where(c => c.ServerItems).Select(c => c.Variant));
        Assert.Equal(16, ReconcileStageContracts.For(ReconcileBatchSchema.Expand).CandidateCap);
        Assert.Equal(0, ReconcileStageContracts.For(ReconcileBatchSchema.Compare).CandidateCap);
        Assert.Equal(ReconcileFocusRule.ComparedOrOpen, ReconcileStageContracts.For(ReconcileBatchSchema.Compare).Focus);
        Assert.Equal(ReconcileFocusRule.NullOrUnresolved, ReconcileStageContracts.For(ReconcileBatchSchema.Expand).Focus);
        Assert.Equal(['E', 'E', 'N', 'N', 'E', null], ReconcileStageContracts.Table.Select(c => c.EndpointPrefix));
        Assert.Equal([true, true, true, false, false, false], ReconcileStageContracts.Table.Select(c => c.DescriptorRequired));
        Assert.Equal([false, false, false, false, true, true], ReconcileStageContracts.Table.Select(c => c.Patches));
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void The_schema_matches_its_table_row_field_by_field(ReconcileBatchSchema variant)
    {
        var row = ReconcileStageContracts.For(variant);
        var schema = Node(ReconcileAliasContracts.SchemaOfByStage(variant));
        var properties = schema["properties"]!.AsObject();

        // descriptor: stringa non vuota (minLength senza pattern) oppure null.
        Assert.Equal(row.DescriptorRequired ? """{"type":"string","minLength":1}""" : """{"type":"null"}""", properties["descriptor"]!.ToJsonString());

        // items: il modello non li produce mai (maxItems 0 accanto a items).
        Assert.Equal(0, properties["items"]!["maxItems"]!.GetValue<int>());
        Assert.NotNull(properties["items"]!["items"]);

        // candidates: tetto e prefisso degli estremi.
        var candidates = properties["candidates"]!.AsObject();
        Assert.Equal(row.CandidateCap, candidates["maxItems"]?.GetValue<int>());
        if (row.EndpointPrefix is { } prefix)
        {
            var expected = prefix == 'E' ? AnalysisAliasPattern.Evidence : ReconcileAliasContracts.NodeAliasPattern;
            Assert.Equal(expected, candidates["items"]!["properties"]!["left"]!["pattern"]!.GetValue<string>());
            Assert.Equal(expected, candidates["items"]!["properties"]!["right"]!["pattern"]!.GetValue<string>());
        }

        // focus
        var focus = properties["focus"]!.AsObject();
        switch (row.Focus)
        {
            case ReconcileFocusRule.None:
                Assert.Equal("""{"type":"null"}""", focus.ToJsonString());
                break;
            case ReconcileFocusRule.NullOrUnresolved:
                Assert.Equal("""["object","null"]""", focus["type"]!.ToJsonString());
                Assert.Equal("""["Unresolved"]""", focus["properties"]!["outcome"]!["enum"]!.ToJsonString());
                break;
            case ReconcileFocusRule.ComparedOrOpen:
                var branches = focus["anyOf"]!.AsArray();
                Assert.Equal(2, branches.Count);
                Assert.Null(focus["type"]);                                                        // nessun fratello di anyOf
                Assert.Equal("""["Compared"]""", branches[0]!["properties"]!["outcome"]!["enum"]!.ToJsonString());
                Assert.Equal("""{"type":["string","null"]}""", branches[0]!["properties"]!["reason"]!.ToJsonString());
                Assert.Equal("""["Open"]""", branches[1]!["properties"]!["outcome"]!["enum"]!.ToJsonString());
                Assert.Equal("""{"type":"string","minLength":1}""", branches[1]!["properties"]!["reason"]!.ToJsonString());
                break;
        }

        // patches: vietate (maxItems 0) oppure un anyOf di tre rami legati a op.
        var patches = properties["patches"]!.AsObject();
        if (row.Patches)
        {
            Assert.Null(patches["maxItems"]);
            var branches = patches["items"]!["anyOf"]!.AsArray();
            Assert.Equal(Enum.GetNames<PatchOperation>().Length, branches.Count);
            for (var i = 0; i < branches.Count; i++)
            {
                Assert.Equal($"[\"{Enum.GetNames<PatchOperation>()[i]}\"]", branches[i]!["properties"]!["op"]!["enum"]!.ToJsonString());
            }
        }
        else
        {
            Assert.Equal(0, patches["maxItems"]!.GetValue<int>());
        }

        // contract e noFindings invariati: ordine delle proprietà e required come nel DTO.
        Assert.Equal("""["contract","descriptor","noFindings","items","candidates","focus","patches"]""", JsonSerializer.Serialize(properties.Select(p => p.Key)));
        Assert.Equal("""["contract","descriptor","noFindings","items","candidates","focus","patches"]""", schema["required"]!.ToJsonString());
        Assert.Equal("""{"type":["string","null"]}""", properties["noFindings"]!.ToJsonString());
        Assert.Equal("""{"type":"string","enum":["reconcile-alias-1"]}""", properties["contract"]!.ToJsonString());
    }

    // ---------------------------------------------------------------- derivazione dai profili storici

    [Fact]
    public void Leaf_b_reduce_and_cross_are_the_minus_2_variants_and_expand_and_compare_differ_only_where_the_contract_says()
    {
        foreach (var v in new[] { ReconcileBatchSchema.LeafB, ReconcileBatchSchema.Reduce, ReconcileBatchSchema.Cross })
        {
            Assert.Equal(Canon(ReconcileAliasContracts.SchemaOf(v)), Canon(ReconcileAliasContracts.SchemaOfByStage(v)));
        }

        Assert.Equal(Canon(ReconcileAliasContracts.SchemaOf(ReconcileBatchSchema.LeafB)), Canon(ReconcileAliasContracts.SchemaOfByStage(ReconcileBatchSchema.LeafA)));

        string Without(JsonElement schema, params string[] properties)
        {
            var node = Node(schema);
            foreach (var p in properties)
            {
                node["properties"]!.AsObject().Remove(p);
            }

            return CanonicalJson.Canonicalize(node.ToJsonString());
        }

        // Expand = -3 (tetto di 16 compreso) tranne le patch.
        Assert.Equal(Without(ReconcileAliasContracts.SchemaOfV3(ReconcileBatchSchema.Expand), "patches"), Without(ReconcileAliasContracts.SchemaOfByStage(ReconcileBatchSchema.Expand), "patches"));
        // Compare = -2 tranne candidates (maxItems 0), focus e patch.
        Assert.Equal(
            Without(ReconcileAliasContracts.SchemaOf(ReconcileBatchSchema.Compare), "patches", "focus", "candidates"),
            Without(ReconcileAliasContracts.SchemaOfByStage(ReconcileBatchSchema.Compare), "patches", "focus", "candidates"));
        var candidates = Node(ReconcileAliasContracts.SchemaOfByStage(ReconcileBatchSchema.Compare))["properties"]!["candidates"]!.AsObject();
        var minus2 = Node(ReconcileAliasContracts.SchemaOf(ReconcileBatchSchema.Compare))["properties"]!["candidates"]!.AsObject();
        minus2["maxItems"] = 0;
        Assert.Equal(CanonicalJson.Canonicalize(minus2.ToJsonString()), CanonicalJson.Canonicalize(candidates.ToJsonString()));
    }

    /// <summary>SHA-256 delle stringhe degli hash dei profili storici (golden RH-011): le funzioni dei profili -1/-2/-3 non sono nel diff di M1.</summary>
    private static readonly IReadOnlyDictionary<string, string> HistoricalProfileGoldens = new Dictionary<string, string>
    {
        [ReconcileAliasContracts.SchemaProfileByBatch] = "3917a529ffd9e4074ff66c0009bc7ac0e4223abf0dc9185d904a741de827d81f",
        [ReconcileAliasContracts.SchemaProfileByBatchV2] = "f2cbfb117c35cfce510817aa6bcf55ec3735cd80984b1dda50019af454092c1f",
        [ReconcileAliasContracts.SchemaProfileByBatchV3] = "9abe6493e6090142adce97d658e94d74f05b373886a266d5ec47b208337113f2",
    };

    [Fact]
    public void The_hashes_of_the_historical_profiles_minus_1_minus_2_and_minus_3_are_unchanged()
    {
        var actual = HistoricalProfileGoldens.Keys.ToDictionary(k => k, k => Sha(ReconcileAliasContracts.ProfileHashes(k)));
        Assert.True(HistoricalProfileGoldens.All(kv => actual[kv.Key] == kv.Value), "Profili storici diversi dai golden. Valori reali:\n" + string.Join('\n', actual.Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.Equal(6, ReconcileAliasContracts.ProfileVariants(ReconcileAliasContracts.SchemaProfileByBatchV3).Count);
        Assert.Equal(
            Enum.GetNames<ReconcileBatchSchema>(), ReconcileAliasContracts.ProfileVariants(ReconcileAliasContracts.SchemaProfileByStage).Select(v => v.Name));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.ProfileVariants("reconcile-alias-schema-by-stage-2"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.ProfileHashes("reconcile-alias-schema-by-stage-2"));
    }

    [Theory]
    [InlineData("leaf", ReconcileView.A, "LeafA")]
    [InlineData("leaf", ReconcileView.B, "LeafB")]
    [InlineData("reduce", ReconcileView.A, "Reduce")]
    [InlineData("cross", ReconcileView.Cross, "Cross")]
    [InlineData("expand", ReconcileView.A, "Expand")]
    [InlineData("compare", ReconcileView.B, "Compare")]
    public void The_variant_for_a_stage_and_view_is_the_named_one_and_an_unknown_stage_is_an_error(string stage, ReconcileView view, string name)
    {
        var (resolved, schema) = ReconcileAliasContracts.ProfileSchemaFor(ReconcileAliasContracts.SchemaProfileByStage, stage, view);
        Assert.Equal(name, resolved);
        Assert.Equal(Canon(ReconcileAliasContracts.SchemaOfByStage(Enum.Parse<ReconcileBatchSchema>(name))), Canon(schema));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.ProfileSchemaFor(ReconcileAliasContracts.SchemaProfileByStage, "gap", view));
    }

    // ---------------------------------------------------------------- legame op ⇔ value (C-4/C-5)

    private const string AddSupportValue = "{}";
    private const string AddRelationValue = """{"from":"E3","to":"E5","type":"Confirms","basis":"Inferred","rationale":null}""";
    private const string AlternativeValue = """{"text":"alternativa","classification":"Documented","reason":"motivo"}""";

    private static string Patch(string op, string value) =>
        $$"""{"key":"p1","op":"{{op}}","target":{"item":"i2","relation":null},"evidence":["E3"],"value":{{value}}}""";

    private static string Dto(ReconcileBatchSchema variant, string patch, string focus) =>
        ReconcileContractMatrixTests.Dto(noFindings: "x", change: o =>
        {
            o["focus"] = JsonNode.Parse(focus);
            o["patches"] = new JsonArray(JsonNode.Parse(patch));
        }).ToJsonString();

    [Theory]
    [InlineData(ReconcileBatchSchema.Expand, "null")]
    [InlineData(ReconcileBatchSchema.Compare, """{"outcome":"Compared","reason":null}""")]
    public void Each_patch_branch_admits_only_its_own_operation_and_value_pair_and_rejects_the_six_crossed_ones(ReconcileBatchSchema variant, string focus)
    {
        var schema = ReconcileAliasContracts.SchemaOfByStage(variant);
        var ops = new[] { ("AddSupport", AddSupportValue), ("AddRelation", AddRelationValue), ("ProposeAlternative", AlternativeValue) };
        foreach (var (op, _) in ops)
        {
            foreach (var (valueOf, value) in ops)
            {
                var violations = ReconcileSchemaSubset.Violations(schema, Dto(variant, Patch(op, value), focus));
                if (op == valueOf)
                {
                    Assert.True(violations.Count == 0, $"{op}/{valueOf} deve essere ammesso: {string.Join(" | ", violations)}");
                }
                else
                {
                    Assert.NotEmpty(violations);
                }
            }
        }
    }

    [Fact]
    public void The_minus_3_profile_admits_the_crossed_pairs_that_the_by_stage_profile_rejects_which_is_the_baseline_of_the_undecodable_class()
    {
        var crossed = Dto(ReconcileBatchSchema.Expand, Patch("AddSupport", AlternativeValue), "null");
        Assert.True(ReconcileSchemaSubset.Violations(ReconcileAliasContracts.SchemaOfV3(ReconcileBatchSchema.Expand), crossed).Count == 0);       // -3: lo schema lo ammette, la decodifica lo rifiuta
        Assert.NotEmpty(ReconcileSchemaSubset.Violations(ReconcileAliasContracts.SchemaOfByStage(ReconcileBatchSchema.Expand), crossed));
    }

    // ---------------------------------------------------------------- focus del confronto (D3/D4)

    [Theory]
    [InlineData("""{"outcome":"Compared","reason":null}""", true)]
    [InlineData("""{"outcome":"Compared","reason":"già confrontati"}""", true)]
    [InlineData("""{"outcome":"Open","reason":"30 giorni contro 45"}""", true)]
    [InlineData("""{"outcome":"Open","reason":null}""", false)]
    [InlineData("""{"outcome":"Open","reason":""}""", false)]
    [InlineData("""{"outcome":"Unresolved","reason":null}""", false)]
    [InlineData("null", false)]
    public void The_compare_focus_is_mandatory_and_open_requires_a_non_empty_reason_by_grammar(string focus, bool admitted)
    {
        var json = ReconcileContractMatrixTests.Dto(noFindings: null, change: o => o["focus"] = JsonNode.Parse(focus)).ToJsonString();
        var violations = ReconcileSchemaSubset.Violations(ReconcileAliasContracts.SchemaOfByStage(ReconcileBatchSchema.Compare), json);
        Assert.Equal(admitted, violations.Count == 0);
    }

    [Fact]
    public void The_compare_schema_admits_no_candidates_and_an_empty_expansion_block_without_no_findings()
    {
        var candidate = JsonNode.Parse("""{"key":"f1","left":"E3","right":"E5","type":"Confirms","basis":"Inferred","rationale":"x"}""")!;
        var compare = ReconcileAliasContracts.SchemaOfByStage(ReconcileBatchSchema.Compare);
        var withCandidate = ReconcileContractMatrixTests.Dto(change: o =>
        {
            o["focus"] = JsonNode.Parse("""{"outcome":"Open","reason":"x"}""");
            o["candidates"] = new JsonArray(candidate.DeepClone());
        }).ToJsonString();
        Assert.Contains(ReconcileSchemaSubset.Violations(compare, withCandidate), m => m.Contains("maxItems 0", StringComparison.Ordinal));

        Assert.Empty(ReconcileSchemaSubset.Violations(compare, ReconcileContractMatrixTests.Dto(change: o => o["focus"] = JsonNode.Parse("""{"outcome":"Open","reason":"x"}""")).ToJsonString()));
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileAliasContracts.SchemaOfByStage(ReconcileBatchSchema.Expand), ReconcileContractMatrixTests.Dto().ToJsonString()));   // blocco vuoto
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileAliasContracts.SchemaOfByStage(ReconcileBatchSchema.LeafB), ReconcileContractMatrixTests.Dto(descriptor: "gruppo").ToJsonString()));   // foglia B senza candidati né noFindings
    }
}
