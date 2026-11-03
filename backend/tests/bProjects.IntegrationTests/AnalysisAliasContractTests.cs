using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T125 parte A (DET) — DTO `document-analysis-alias-1` (contracts/ai-workflow.md §7.4): codec strict, alias con pattern esatto case-sensitive,
/// JSON Schema pubblicabile allineato al codec. Il modello non emette UUID, hash, id di run, Source né elenchi completi di voci visitate. Nessun database.
/// </summary>
public sealed class AnalysisAliasContractTests
{
    private static JsonObject Valid() => JsonNode.Parse(AnalysisAliasContracts.Encode(AliasFixtures.ValidDto()))!.AsObject();

    private static void AssertRejected(JsonNode node) => Assert.ThrowsAny<JsonException>(() => AnalysisAliasContracts.Decode(node.ToJsonString()));

    // ---- round-trip e struttura ----

    [Fact]
    public void A_valid_dto_round_trips_exactly_and_its_hash_is_the_hash_of_its_canonical_form()
    {
        var dto = AliasFixtures.ValidDto();
        var json = AnalysisAliasContracts.Encode(dto);
        var decoded = AnalysisAliasContracts.Decode(json);

        Assert.Equal(AnalysisAliasContracts.Canonical(dto), AnalysisAliasContracts.Canonical(decoded));
        Assert.Equal(CanonicalJson.Sha256Hex(json), AnalysisAliasContracts.Hash(decoded));
        Assert.Equal("alias-1", decoded.Contract);
        Assert.Equal("document-analysis-alias-1", AnalysisAliasContracts.SchemaVersion);
    }

    [Fact]
    public void The_hash_is_independent_of_property_order_and_changes_with_any_semantic_value()
    {
        var dto = AliasFixtures.ValidDto();
        var reordered = new JsonObject();
        foreach (var property in Valid().Reverse().ToList())
        {
            reordered[property.Key] = property.Value!.DeepClone();
        }

        Assert.Equal(AnalysisAliasContracts.Hash(dto), AnalysisAliasContracts.Hash(AnalysisAliasContracts.Decode(reordered.ToJsonString())));
        var changed = dto with { Items = dto.Items with { Goals = [dto.Items.Goals[0] with { Text = dto.Items.Goals[0].Text + "!" }] } };
        Assert.NotEqual(AnalysisAliasContracts.Hash(dto), AnalysisAliasContracts.Hash(changed));
    }

    [Fact]
    public void A_dto_with_every_optional_shape_round_trips()
    {
        var dto = AliasFixtures.FullDto();
        var decoded = AnalysisAliasContracts.Decode(AnalysisAliasContracts.Encode(dto));

        Assert.Equal(AnalysisAliasContracts.Canonical(dto), AnalysisAliasContracts.Canonical(decoded));
        Assert.Equal(3, decoded.Patches.Count);
        Assert.IsType<AiAddSupportValue>(decoded.Patches[0].Value);
        Assert.IsType<AiAddRelationValue>(decoded.Patches[1].Value);
        Assert.IsType<AiProposeAlternativeValue>(decoded.Patches[2].Value);
        Assert.Equal("i1", decoded.Patches[0].Target.Item);
        Assert.Null(decoded.Patches[0].Target.Relation);
    }

    // ---- alias malformati: pattern esatto, case-sensitive, nessuna normalizzazione ----

    [Theory]
    [InlineData("e3")]
    [InlineData("E03")]
    [InlineData("E0")]
    [InlineData("E 3")]
    [InlineData(" E3")]
    [InlineData("E3 ")]
    [InlineData("E3\n")]
    [InlineData("E")]
    [InlineData("E-1")]
    [InlineData("E+1")]
    [InlineData("E1.0")]
    [InlineData("E١")]            // cifra araba-indica: non ASCII
    [InlineData("E1​")]      // zero-width space
    [InlineData("S1")]            // namespace sbagliato: S dove serve E
    [InlineData("R1")]
    [InlineData("")]
    [InlineData("E12345678901")]
    public void A_malformed_or_wrong_namespace_evidence_alias_is_rejected_everywhere_an_evidence_is_expected(string alias)
    {
        var places = new Func<JsonObject, JsonObject>[]
        {
            n => Set(n, o => o["visited"]!["A"]!.AsArray().Add(alias)),
            n => Set(n, o => o["items"]!["goals"]![0]!["evidence"]!.AsArray().Add(alias)),
            n => Set(n, o => o["findings"] = new JsonArray(Finding(left: alias))),
            n => Set(n, o => o["findings"] = new JsonArray(Finding(right: alias))),
            n => Set(n, o => o["patches"] = new JsonArray(AddSupportPatch(alias))),
        };
        foreach (var mutate in places)
        {
            AssertRejected(mutate(Valid()));
        }
    }

    [Theory]
    [InlineData("E1")]
    [InlineData("E9")]
    [InlineData("E10")]
    [InlineData("E123456789")]
    public void Canonical_evidence_aliases_are_accepted(string alias)
    {
        var node = Valid();
        node["visited"]!["A"]!.AsArray().Add(alias);
        var decoded = AnalysisAliasContracts.Decode(node.ToJsonString());
        Assert.Contains(alias, decoded.Visited.A);
    }

    [Theory]
    [InlineData("r1")]
    [InlineData("R01")]
    [InlineData("E1")]
    [InlineData("S1")]
    [InlineData("R 1")]
    public void A_malformed_relation_alias_is_rejected(string alias)
    {
        var node = Valid();
        node["knownRelationChecks"] = new JsonArray(new JsonObject { ["relation"] = alias, ["verdict"] = "Confirmed", ["rationale"] = "ok" });
        AssertRejected(node);
        node = Valid();
        node["patches"] = new JsonArray(new JsonObject
        {
            ["key"] = "p1", ["op"] = "AddSupport", ["target"] = new JsonObject { ["item"] = null, ["relation"] = alias }, ["evidence"] = new JsonArray("E1"), ["value"] = new JsonObject(),
        });
        AssertRejected(node);
    }

    [Theory]
    [InlineData("i0")]
    [InlineData("I1")]
    [InlineData("i01")]
    [InlineData("c1")]
    [InlineData("i 1")]
    [InlineData("e1")]
    public void A_malformed_item_key_is_rejected(string key)
    {
        var node = Valid();
        node["items"]!["goals"]![0]!["key"] = key;
        AssertRejected(node);
    }

    [Fact]
    public void Local_keys_of_the_wrong_kind_are_rejected()
    {
        var conflict = new JsonObject { ["key"] = "i9", ["description"] = "d", ["alternatives"] = new JsonArray() };      // chiave di item per un conflitto
        var node = Valid();
        node["conflicts"] = new JsonArray(conflict);
        AssertRejected(node);

        node = Valid();
        node["findings"] = new JsonArray(Finding(key: "p1"));                                                          // chiave di patch per un finding
        AssertRejected(node);

        node = Valid();
        node["patches"] = new JsonArray(AddSupportPatch("E1", key: "f1"));                                             // chiave di finding per una patch
        AssertRejected(node);
    }

    // ---- struttura strict ----

    [Fact]
    public void Extra_missing_null_and_mistyped_members_are_rejected_at_every_level()
    {
        var dto = AliasFixtures.FullDto();
        var baseline = JsonNode.Parse(AnalysisAliasContracts.Encode(dto))!.AsObject();

        var mutations = 0;
        foreach (var (container, name) in EveryObjectMember(baseline))
        {
            // estraneo
            var copy = (JsonObject)baseline.DeepClone();
            AtSamePath(baseline, copy, container)[name + "Extra"] = 1;
            AssertRejected(copy);
            mutations++;

            // mancante
            copy = (JsonObject)baseline.DeepClone();
            AtSamePath(baseline, copy, container).Remove(name);
            AssertRejected(copy);
            mutations++;
        }

        Assert.True(mutations > 60);
    }

    [Fact]
    public void Null_where_not_declared_and_wrong_json_types_are_rejected_but_declared_nulls_are_accepted()
    {
        foreach (var (path, value) in new (string[], JsonNode?)[]
        {
            (["contract"], null), (["contract"], 1), (["visited"], null), (["visited", "A"], null), (["visited", "A"], "E1"),
            (["items", "goals"], null), (["items", "goals", "0", "text"], null), (["items", "goals", "0", "text"], 5), (["items", "goals", "0", "evidence"], null),
            (["items", "goals", "0", "classification"], 0), (["items", "goals", "0", "classification"], "declared"), (["items", "goals", "0", "classification"], "Unknown"),
            (["conflicts"], null), (["findings"], "not-an-array"), (["knownRelationChecks"], null), (["patches"], null), (["noFindings", "A"], 4),
        })
        {
            var node = Valid();
            SetAt(node, path, value);
            AssertRejected(node);
        }

        var ok = Valid();
        SetAt(ok, ["noFindings", "A"], null);
        SetAt(ok, ["noFindings", "B"], null);
        SetAt(ok, ["noFindings", "Cross"], "nessun finding");
        Assert.Null(AnalysisAliasContracts.Decode(ok.ToJsonString()).NoFindings.A);
    }

    [Fact]
    public void Enums_are_exact_names_numbers_and_quoted_numbers_are_rejected()
    {
        var node = Valid();
        node["findings"] = new JsonArray(Finding());
        Assert.Single(AnalysisAliasContracts.Decode(node.ToJsonString()).Findings);

        foreach (var (field, bad) in new (string, JsonNode)[]
        {
            ("view", "cross"), ("view", 2), ("view", "Global"), ("type", "contradicts"), ("type", 2), ("type", "Alias"), ("basis", "declared"), ("basis", 0),
            ("outcome", "compared"), ("outcome", "Pending"), ("outcome", 1),
        })
        {
            node = Valid();
            var finding = Finding();
            finding[field] = bad;
            node["findings"] = new JsonArray(finding);
            AssertRejected(node);
        }

        node = Valid();
        node["knownRelationChecks"] = new JsonArray(new JsonObject { ["relation"] = "R1", ["verdict"] = "confirmed", ["rationale"] = "x" });
        AssertRejected(node);
    }

    [Fact]
    public void Duplicate_properties_truncated_or_non_object_json_and_trailing_content_are_rejected()
    {
        var json = AnalysisAliasContracts.Encode(AliasFixtures.ValidDto());
        foreach (var text in new[] { json[..(json.Length / 2)], string.Empty, "null", "[]", "42", "\"x\"", json + "{}", json.Insert(1, "\"contract\":\"alias-1\",") })
        {
            Assert.ThrowsAny<JsonException>(() => AnalysisAliasContracts.Decode(text));
        }
    }

    [Fact]
    public void The_model_cannot_emit_uuids_hashes_run_ids_sources_or_complete_visited_lists()
    {
        foreach (var (name, value) in new (string, JsonNode)[]
        {
            ("id", Guid.NewGuid().ToString()), ("analysisId", Guid.NewGuid().ToString()), ("projectId", Guid.NewGuid().ToString()), ("batchId", new string('a', 64)),
            ("inputHash", new string('b', 64)), ("sources", new JsonArray()), ("evidenceMap", new JsonArray()), ("visitedEntryIds", new JsonArray()),
            ("results", new JsonArray()), ("analysis", new JsonObject()), ("candidates", new JsonArray()), ("itemProposals", new JsonArray()),
            ("sourceCutoffSequence", 3), ("validationState", "Unvalidated"), ("schemaVersion", "1.1.0"), ("complete", true),
        })
        {
            var node = Valid();
            node[name] = value;
            AssertRejected(node);

            node = Valid();
            node["items"]!["goals"]![0]![name] = value.DeepClone();
            AssertRejected(node);
        }

        var wrongContract = Valid();
        wrongContract["contract"] = "alias-2";
        AssertRejected(wrongContract);
        wrongContract["contract"] = "document-analysis-alias-1";
        AssertRejected(wrongContract);
    }

    [Fact]
    public void A_uuid_in_place_of_an_alias_is_rejected()
    {
        var node = Valid();
        node["items"]!["goals"]![0]!["evidence"] = new JsonArray(Guid.NewGuid().ToString("D"));
        AssertRejected(node);
        node = Valid();
        node["items"]!["goals"]![0]!["key"] = Guid.NewGuid().ToString("D");
        AssertRejected(node);
    }

    // ---- patch: union discriminata ----

    [Fact]
    public void A_patch_value_must_match_its_operation_with_exact_members()
    {
        string[][] cases =
        [
            ["AddSupport", """{"text":"x"}"""],
            ["AddSupport", """{"from":"E1","to":"E2","type":"Confirms","basis":"Declared","rationale":null}"""],
            ["AddRelation", "{}"],
            ["AddRelation", """{"from":"E1","to":"E2","type":"Confirms","basis":"Declared"}"""],
            ["AddRelation", """{"from":"E1","to":"E2","type":"Confirms","basis":"Declared","rationale":null,"extra":1}"""],
            ["AddRelation", """{"from":"e1","to":"E2","type":"Confirms","basis":"Declared","rationale":null}"""],
            ["ProposeAlternative", """{"text":"x","classification":"Inferred"}"""],
            ["ProposeAlternative", """{"text":"x","classification":"Inferred","reason":null}"""],
            ["ProposeAlternative", """{"text":"x","classification":"Other","reason":"y"}"""],
            ["Replace", "{}"],
        ];
        foreach (var c in cases)
        {
            var patch = new JsonObject
            {
                ["key"] = "p1", ["op"] = c[0], ["target"] = new JsonObject { ["item"] = "i1", ["relation"] = null }, ["evidence"] = new JsonArray("E1"),
                ["value"] = JsonNode.Parse(c[1]),
            };
            var node = Valid();
            node["patches"] = new JsonArray(patch);
            AssertRejected(node);
        }

        var patchWithExtra = AddSupportPatch("E1");
        patchWithExtra["path"] = "/items/0";
        var withExtra = Valid();
        withExtra["patches"] = new JsonArray(patchWithExtra);
        AssertRejected(withExtra);

        var missingTarget = AddSupportPatch("E1");
        ((JsonObject)missingTarget["target"]!).Remove("relation");
        var withMissing = Valid();
        withMissing["patches"] = new JsonArray(missingTarget);
        AssertRejected(withMissing);
    }

    // ---- JSON Schema ----

    [Fact]
    public void The_json_schema_is_strict_closed_and_lists_every_property_as_required()
    {
        var schema = JsonNode.Parse(AnalysisAliasContracts.JsonSchema().GetRawText())!;
        var objects = 0;
        Walk(schema, node =>
        {
            if (node is JsonObject o && o["type"] is JsonValue tv && tv.TryGetValue<string>(out var typeName) && typeName == "object")
            {
                objects++;
                Assert.False(o["additionalProperties"]!.GetValue<bool>());
                var properties = o["properties"]!.AsObject().Select(p => p.Key).Order().ToArray();
                var required = o["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order().ToArray();
                Assert.Equal(properties, required);
            }
        });
        Assert.True(objects >= 12);
        var text = schema.ToJsonString();
        Assert.DoesNotContain("uuid", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"alias-1\"", text, StringComparison.Ordinal);
        foreach (var pattern in new[] { AnalysisAliasPattern.Evidence, AnalysisAliasPattern.Relation, AnalysisAliasPattern.ItemKey, AnalysisAliasPattern.ConflictKey, AnalysisAliasPattern.FindingKey, AnalysisAliasPattern.PatchKey })
        {
            Assert.Contains(pattern, schema.ToJsonString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_schema_accepts_what_the_codec_emits_and_rejects_alias_and_structure_violations_the_codec_also_rejects()
    {
        var schema = JsonNode.Parse(AnalysisAliasContracts.JsonSchema().GetRawText())!;
        foreach (var dto in new[] { AliasFixtures.ValidDto(), AliasFixtures.FullDto() })
        {
            var errors = new List<string>();
            SchemaCheck(schema, JsonNode.Parse(AnalysisAliasContracts.Encode(dto))!, "$", errors);
            Assert.Empty(errors);
        }

        foreach (var mutate in new Action<JsonObject>[]
        {
            n => n["visited"]!["A"]!.AsArray().Add("e1"),
            n => n["items"]!["goals"]![0]!["key"] = "i0",
            n => n["extra"] = 1,
            n => n.Remove("patches"),
            n => n["contract"] = "alias-2",
            n => n["items"]!["goals"]![0]!["classification"] = "declared",
            n => n["items"]!["goals"]![0]!["id"] = Guid.NewGuid().ToString(),
        })
        {
            var node = (JsonObject)JsonNode.Parse(AnalysisAliasContracts.Encode(AliasFixtures.ValidDto()))!;
            mutate(node);
            var errors = new List<string>();
            SchemaCheck(schema, node, "$", errors);
            Assert.NotEmpty(errors);
            AssertRejected(node);   // il codec rifiuta ciò che lo schema rifiuta
        }
    }

    [Fact]
    public void The_schema_pattern_constants_agree_with_the_codec_on_edge_cases()
    {
        foreach (var (prefix, pattern) in new[] { ('E', AnalysisAliasPattern.Evidence), ('R', AnalysisAliasPattern.Relation), ('i', AnalysisAliasPattern.ItemKey), ('c', AnalysisAliasPattern.ConflictKey), ('f', AnalysisAliasPattern.FindingKey), ('p', AnalysisAliasPattern.PatchKey) })
        {
            foreach (var sample in new[] { $"{prefix}1", $"{prefix}10", $"{prefix}0", $"{prefix}01", $"{prefix}", char.ToUpperInvariant(prefix) == prefix ? $"{char.ToLowerInvariant(prefix)}1" : $"{char.ToUpperInvariant(prefix)}1", $"{prefix}1 " })
            {
                // la regex ECMAScript usa `$` che ammette «\n» finale solo in .NET: il confronto usa RegexOptions.ECMAScript con \z equivalente
                var expected = Regex.IsMatch(sample, pattern.Replace("$", "\\z", StringComparison.Ordinal));
                Assert.Equal(expected, AnalysisAliasPattern.Matches(sample, prefix));
            }
        }
    }

    // ---- helper ----

    private static JsonObject Set(JsonObject node, Action<JsonObject> action)
    {
        action(node);
        return node;
    }

    private static JsonObject Finding(string key = "f1", string left = "E1", string right = "E2") => new()
    {
        ["key"] = key, ["view"] = "Cross", ["left"] = left, ["right"] = right, ["type"] = "Contradicts", ["basis"] = "Inferred", ["rationale"] = "motivo",
        ["outcome"] = "Compared", ["reason"] = null,
    };

    private static JsonObject AddSupportPatch(string evidence, string key = "p1") => new()
    {
        ["key"] = key, ["op"] = "AddSupport", ["target"] = new JsonObject { ["item"] = "i1", ["relation"] = null }, ["evidence"] = new JsonArray(evidence), ["value"] = new JsonObject(),
    };

    private static void SetAt(JsonNode root, string[] path, JsonNode? value)
    {
        var node = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            node = int.TryParse(path[i], out var index) ? node![index] : node![path[i]];
        }

        var last = path[^1];
        if (node is JsonArray array)
        {
            array[int.Parse(last)] = value;
        }
        else
        {
            node![last] = value;
        }
    }

    /// <summary>Ogni coppia (percorso dell'oggetto, nome del membro) di ogni oggetto dell'albero, array compresi.</summary>
    private static IEnumerable<(string Container, string Name)> EveryObjectMember(JsonNode root)
    {
        var result = new List<(string, string)>();
        void Visit(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var p in o.ToList())
                    {
                        result.Add((path, p.Key));
                        Visit(p.Value, path + "/" + p.Key);
                    }

                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        Visit(a[i], path + "/" + i);
                    }

                    break;
            }
        }

        Visit(root, string.Empty);
        return result;
    }

    private static JsonObject AtSamePath(JsonObject _, JsonObject copy, string path)
    {
        JsonNode node = copy;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = int.TryParse(part, out var index) && node is JsonArray a ? a[index]! : node[part]!;
        }

        return (JsonObject)node;
    }

    private static void Walk(JsonNode? node, Action<JsonNode> visit)
    {
        if (node is null)
        {
            return;
        }

        visit(node);
        switch (node)
        {
            case JsonObject o:
                foreach (var p in o)
                {
                    Walk(p.Value, visit);
                }

                break;
            case JsonArray a:
                foreach (var item in a)
                {
                    Walk(item, visit);
                }

                break;
        }
    }

    /// <summary>Validatore minimo dei costrutti usati dallo schema (type, enum, pattern, properties/required/additionalProperties, items, anyOf, const).</summary>
    private static void SchemaCheck(JsonNode schema, JsonNode? value, string path, List<string> errors)
    {
        var s = schema.AsObject();
        if (s["anyOf"] is JsonArray any)
        {
            if (!any.Any(option =>
            {
                var local = new List<string>();
                SchemaCheck(option!, value, path, local);
                return local.Count == 0;
            }))
            {
                errors.Add($"{path}: nessuna alternativa di anyOf.");
            }

            return;
        }

        if (s["type"] is { } type)
        {
            var allowed = type is JsonArray types ? types.Select(t => t!.GetValue<string>()).ToArray() : [type.GetValue<string>()];
            var actual = value switch
            {
                null => "null",
                JsonObject => "object",
                JsonArray => "array",
                JsonValue v when v.TryGetValue<string>(out _) => "string",
                JsonValue v when v.TryGetValue<bool>(out _) => "boolean",
                _ => "number",
            };
            if (!allowed.Contains(actual))
            {
                errors.Add($"{path}: tipo {actual} non ammesso.");
                return;
            }
        }

        if (value is null)
        {
            return;
        }

        if (s["enum"] is JsonArray values && !values.Any(v => v!.ToJsonString() == value.ToJsonString()))
        {
            errors.Add($"{path}: valore fuori dall'enum.");
        }

        if (s["const"] is { } constant && constant.ToJsonString() != value.ToJsonString())
        {
            errors.Add($"{path}: costante diversa.");
        }

        if (s["pattern"] is { } pattern && value is JsonValue pv && pv.TryGetValue<string>(out var text)
            && !Regex.IsMatch(text, pattern.GetValue<string>().Replace("$", "\\z", StringComparison.Ordinal)))
        {
            errors.Add($"{path}: non rispetta il pattern.");
        }

        if (value is JsonObject o)
        {
            var properties = s["properties"]?.AsObject();
            foreach (var required in s["required"]?.AsArray().Select(r => r!.GetValue<string>()) ?? [])
            {
                if (!o.ContainsKey(required))
                {
                    errors.Add($"{path}.{required}: mancante.");
                }
            }

            foreach (var member in o)
            {
                if (properties is not null && properties.ContainsKey(member.Key))
                {
                    SchemaCheck(properties[member.Key]!, member.Value, $"{path}.{member.Key}", errors);
                }
                else if (s["additionalProperties"] is JsonValue ap && ap.TryGetValue<bool>(out var allowed) && !allowed)
                {
                    errors.Add($"{path}.{member.Key}: proprietà non ammessa.");
                }
            }
        }

        if (value is JsonArray array && s["items"] is { } items)
        {
            for (var i = 0; i < array.Count; i++)
            {
                SchemaCheck(items, array[i], $"{path}[{i}]", errors);
            }
        }
    }
}
