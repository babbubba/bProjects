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

    // ================================================================ T135: alias-2, gap-assessment-1 (contracts/ai-workflow.md §13.1-§13.2)

    /// <summary>Snapshot con E2 ed E5 di tipo Gap (E1, E3, E4 restano Requirement).</summary>
    private static DocumentAnalysisInput GapInput()
    {
        var evidence = AliasFixtures.Evidences().Select((e, i) => i is 1 or 4 ? e with { Kind = EvidenceKind.Gap } : e).ToList();
        return AliasFixtures.Input(evidence: evidence);
    }

    private static AiGapAssessment Answered(string gap = "E2", params string[] support) => new()
    {
        Gap = gap, Outcome = GapOutcome.AnsweredInSnapshot, Reason = GapReason.Answered, Rationale = "La scheda risponde.", SupportingEvidence = support.Length == 0 ? ["E3"] : support,
        OpenItem = null, OpenConflict = null,
    };

    private static AiGapAssessment OpenItemRecord(string gap = "E5", GapReason reason = GapReason.NoAnswer, string item = "i9", params string[] support) => new()
    {
        Gap = gap, Outcome = GapOutcome.Open, Reason = reason, Rationale = "Nessuna risposta nello snapshot.", SupportingEvidence = support, OpenItem = item, OpenConflict = null,
    };

    private static AiAnalysisOutputV2 V2(params AiGapAssessment[] records) => new()
    {
        Contract = "alias-2", Visited = AliasFixtures.ValidDto().Visited, NoFindings = AliasFixtures.ValidDto().NoFindings, Items = AliasFixtures.ValidDto().Items,
        Conflicts = [], Findings = [], KnownRelationChecks = [], Patches = [], GapAssessments = records,
    };

    private static JsonObject ValidV2(params AiGapAssessment[] records) => JsonNode.Parse(AnalysisAliasContracts.Encode(V2(records)))!.AsObject();

    private static void AssertRejectedV2(JsonNode node) => Assert.ThrowsAny<JsonException>(() => AnalysisAliasContracts.DecodeV2(node.ToJsonString()));

    private static JsonObject GapJson(Action<JsonObject>? change = null)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Answered(), AnalysisAliasContracts.Options))!.AsObject();
        change?.Invoke(node);
        return node;
    }

    private static JsonObject WithGap(JsonObject gap)
    {
        var node = ValidV2();
        node["gapAssessments"] = new JsonArray(gap);
        return node;
    }

    [Fact]
    public void The_alias_2_dto_round_trips_with_gap_assessments_and_its_hash_covers_them()
    {
        var dto = V2(Answered(), OpenItemRecord());
        var decoded = AnalysisAliasContracts.DecodeV2(AnalysisAliasContracts.Encode(dto));

        Assert.Equal("alias-2", decoded.Contract);
        Assert.Equal("document-analysis-alias-2", AnalysisAliasContracts.SchemaVersionV2);
        Assert.Equal(AnalysisAliasContracts.Canonical(dto), AnalysisAliasContracts.Canonical(decoded));
        Assert.Equal(2, decoded.GapAssessments.Count);
        Assert.Equal(GapOutcome.AnsweredInSnapshot, decoded.GapAssessments[0].Outcome);
        Assert.Equal("i9", decoded.GapAssessments[1].OpenItem);
        Assert.NotEqual(AnalysisAliasContracts.Hash(V2()), AnalysisAliasContracts.Hash(dto));
        Assert.NotEqual(AnalysisAliasContracts.Hash(dto), AnalysisAliasContracts.Hash(dto with { GapAssessments = [Answered(), OpenItemRecord() with { Rationale = "altro motivo" }] }));
    }

    [Fact]
    public void The_gap_assessments_list_is_required_even_when_empty_and_null_is_not_an_empty_list()
    {
        Assert.Empty(AnalysisAliasContracts.DecodeV2(ValidV2().ToJsonString()).GapAssessments);

        var missing = ValidV2();
        missing.Remove("gapAssessments");
        AssertRejectedV2(missing);
        var nulled = ValidV2();
        nulled["gapAssessments"] = null;
        AssertRejectedV2(nulled);
        var notArray = ValidV2();
        notArray["gapAssessments"] = new JsonObject();
        AssertRejectedV2(notArray);
    }

    [Fact]
    public void Contracts_do_not_cross_alias_1_rejects_alias_2_and_gap_assessments_and_alias_2_rejects_alias_1()
    {
        Assert.ThrowsAny<JsonException>(() => AnalysisAliasContracts.Decode(ValidV2().ToJsonString()));
        var v1WithGaps = Valid();
        v1WithGaps["gapAssessments"] = new JsonArray();
        AssertRejected(v1WithGaps);

        var v2WithOldContract = ValidV2();
        v2WithOldContract["contract"] = "alias-1";
        AssertRejectedV2(v2WithOldContract);
        v2WithOldContract["contract"] = "document-analysis-alias-2";
        AssertRejectedV2(v2WithOldContract);
        AssertRejectedV2(Valid());                                   // un output alias-1 senza gapAssessments non è alias-2
    }

    [Fact]
    public void Every_valid_combination_of_the_assessment_matrix_is_accepted()
    {
        AiGapAssessment[] records =
        [
            Answered("E2", "E3", "E4"),
            OpenItemRecord("E5", GapReason.NoAnswer, "i9"),                                    // NoAnswer: supporti vuoti ammessi
            OpenItemRecord("E5", GapReason.NoAnswer, "i9", "E3"),                              // NoAnswer con supporto resta ammesso
            OpenItemRecord("E5", GapReason.Partial, "i9", "E3"),
            OpenItemRecord("E5", GapReason.Ambiguous, "i9", "E3", "E4"),
            OpenItemRecord("E5", GapReason.Partial, "i9", "E3") with { OpenItem = null, OpenConflict = "c1" },
            OpenItemRecord("E5", GapReason.Ambiguous, "i9", "E3") with { OpenItem = null, OpenConflict = "c12" },
        ];
        foreach (var record in records)
        {
            var decoded = AnalysisAliasContracts.DecodeV2(AnalysisAliasContracts.Encode(V2(record)));
            Assert.Equal(record.Reason, decoded.GapAssessments[0].Reason);
        }
    }

    public static TheoryData<string> InvalidAssessmentShapes() => new()
    {
        "answered_with_noanswer_reason", "answered_with_partial_reason", "answered_without_support", "answered_with_open_item", "answered_with_open_conflict",
        "open_with_answered_reason", "open_without_reference", "open_with_both_references", "partial_without_support", "ambiguous_without_support",
        "empty_rationale", "blank_rationale", "support_duplicated", "support_contains_the_gap_itself", "open_with_both_item_and_conflict",
    };

    [Theory]
    [MemberData(nameof(InvalidAssessmentShapes))]
    public void An_assessment_that_violates_the_outcome_reason_reference_matrix_is_rejected(string kind)
    {
        var record = kind switch
        {
            "answered_with_noanswer_reason" => Answered() with { Reason = GapReason.NoAnswer },
            "answered_with_partial_reason" => Answered() with { Reason = GapReason.Partial },
            "answered_without_support" => Answered() with { SupportingEvidence = [] },
            "answered_with_open_item" => Answered() with { OpenItem = "i1" },
            "answered_with_open_conflict" => Answered() with { OpenConflict = "c1" },
            "open_with_answered_reason" => OpenItemRecord(reason: GapReason.Answered, support: "E3"),
            "open_without_reference" => OpenItemRecord(support: "E3") with { OpenItem = null },
            "open_with_both_references" => OpenItemRecord(support: "E3") with { OpenConflict = "c1" },
            "partial_without_support" => OpenItemRecord(reason: GapReason.Partial),
            "ambiguous_without_support" => OpenItemRecord(reason: GapReason.Ambiguous),
            "empty_rationale" => Answered() with { Rationale = string.Empty },
            "blank_rationale" => Answered() with { Rationale = "  \t" },
            "support_duplicated" => Answered("E2", "E3", "E3"),
            "support_contains_the_gap_itself" => Answered("E2", "E3", "E2"),
            _ => OpenItemRecord(support: "E3") with { OpenConflict = "c2" },
        };

        Assert.ThrowsAny<JsonException>(() => AnalysisAliasContracts.DecodeV2(AnalysisAliasContracts.Encode(V2(record))));
    }

    [Fact]
    public void Two_records_for_the_same_gap_are_rejected_by_the_codec()
    {
        Assert.ThrowsAny<JsonException>(() => AnalysisAliasContracts.DecodeV2(AnalysisAliasContracts.Encode(V2(Answered(), Answered() with { Rationale = "ancora" }))));
    }

    [Fact]
    public void An_assessment_record_rejects_extra_missing_null_and_mistyped_members_and_inexact_enums()
    {
        var baseline = GapJson();
        foreach (var name in baseline.Select(p => p.Key).ToList())
        {
            AssertRejectedV2(WithGap(GapJson(g => g[name + "Extra"] = 1)));
            AssertRejectedV2(WithGap(GapJson(g => g.Remove(name))));
        }

        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("gap", null), ("outcome", null), ("reason", null), ("rationale", null), ("rationale", 3), ("supportingEvidence", null), ("supportingEvidence", "E3"),
            ("outcome", "answeredInSnapshot"), ("outcome", 0), ("outcome", "Closed"), ("reason", "answered"), ("reason", 1), ("reason", "Unknown"), ("outcome", "AnsweredInSnapshot "),
        })
        {
            AssertRejectedV2(WithGap(GapJson(g => g[field] = value)));
        }

        AssertRejectedV2(WithGap(GapJson(g => g["id"] = Guid.NewGuid().ToString())));
        AssertRejectedV2(WithGap(GapJson(g => g["evidenceMap"] = new JsonArray())));
    }

    [Theory]
    [InlineData("gap", "e2")]
    [InlineData("gap", "E02")]
    [InlineData("gap", "S1")]
    [InlineData("gap", "R1")]
    [InlineData("gap", "N1")]
    [InlineData("gap", "i1")]
    [InlineData("gap", "")]
    [InlineData("openItem", "c1")]
    [InlineData("openItem", "I1")]
    [InlineData("openItem", "i0")]
    [InlineData("openItem", "E1")]
    [InlineData("openConflict", "i1")]
    [InlineData("openConflict", "C1")]
    [InlineData("openConflict", "c01")]
    public void Wrong_namespace_or_malformed_aliases_and_keys_are_rejected_in_every_assessment_position(string field, string alias)
    {
        AssertRejectedV2(WithGap(GapJson(g => g[field] = alias)));
        if (field == "gap")
        {
            AssertRejectedV2(WithGap(GapJson(g => g["supportingEvidence"] = new JsonArray(alias))));
        }
    }

    [Fact]
    public void A_uuid_hash_or_source_alias_in_the_supporting_evidence_is_rejected()
    {
        foreach (var bad in new[] { Guid.NewGuid().ToString("D"), new string('a', 64), "S1", "R1" })
        {
            AssertRejectedV2(WithGap(GapJson(g => g["supportingEvidence"] = new JsonArray(bad))));
        }
    }

    [Fact]
    public void The_alias_2_codec_stays_strict_on_the_historical_members_too()
    {
        var node = ValidV2();
        node["extra"] = 1;
        AssertRejectedV2(node);
        node = ValidV2();
        node.Remove("patches");
        AssertRejectedV2(node);
        Assert.ThrowsAny<JsonException>(() => AnalysisAliasContracts.DecodeV2(ValidV2().ToJsonString().Insert(1, "\"gapAssessments\":[],")));
        Assert.ThrowsAny<JsonException>(() => AnalysisAliasContracts.DecodeV2("null"));
    }

    [Fact]
    public void The_alias_2_schema_is_closed_requires_gap_assessments_and_the_alias_1_schema_is_unchanged()
    {
        var schema = JsonNode.Parse(AnalysisAliasContracts.JsonSchemaV2().GetRawText())!;
        var objects = 0;
        Walk(schema, node =>
        {
            if (node is JsonObject o && o["type"] is JsonValue tv && tv.TryGetValue<string>(out var typeName) && typeName == "object")
            {
                objects++;
                Assert.False(o["additionalProperties"]!.GetValue<bool>());
                Assert.Equal(o["properties"]!.AsObject().Select(p => p.Key).Order().ToArray(), o["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order().ToArray());
            }
        });
        Assert.True(objects >= 13);
        Assert.Contains("gapAssessments", schema["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(["alias-2"], schema["properties"]!["contract"]!["enum"]!.AsArray().Select(r => r!.GetValue<string>()));
        var gap = schema["properties"]!["gapAssessments"]!["items"]!;
        Assert.Equal(
            new[] { "gap", "outcome", "reason", "rationale", "supportingEvidence", "openItem", "openConflict" }.Order(),
            gap["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order());
        Assert.Equal(["AnsweredInSnapshot", "Open"], gap["properties"]!["outcome"]!["enum"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(["Answered", "NoAnswer", "Partial", "Ambiguous"], gap["properties"]!["reason"]!["enum"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.DoesNotContain("uuid", schema.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        var errors = new List<string>();
        SchemaCheck(schema, ValidV2(Answered(), OpenItemRecord()), "$", errors);
        Assert.Empty(errors);
        foreach (var mutate in new Action<JsonObject>[] { n => n.Remove("gapAssessments"), n => n["contract"] = "alias-1", n => n["gapAssessments"]![0]!["extra"] = 1, n => n["gapAssessments"]![0]!["gap"] = "S1", n => n["gapAssessments"]![0]!["reason"] = "Closed" })
        {
            var node = ValidV2(Answered());
            mutate(node);
            errors = [];
            SchemaCheck(schema, node, "$", errors);
            Assert.NotEmpty(errors);
            AssertRejectedV2(node);
        }

        var v1 = AnalysisAliasContracts.JsonSchema().GetRawText();
        Assert.DoesNotContain("gapAssessments", v1, StringComparison.Ordinal);
        Assert.Contains("\"alias-1\"", v1, StringComparison.Ordinal);
        Assert.DoesNotContain("alias-2", v1, StringComparison.Ordinal);
    }

    // ---- verifiche sul catalogo: un record per ogni Gap, supporti solo non-Gap dello snapshot, riferimenti risolvibili ----

    private static readonly string[] OpenQuestions = ["i9"];
    private static readonly string[] Conflicts = ["c1"];

    private static IReadOnlyList<string> Check(IReadOnlyList<AiGapAssessment> records, IReadOnlyCollection<string>? questions = null, IReadOnlyCollection<string>? conflicts = null) =>
        GapAssessmentRules.Check(records, GapAssessmentRules.Assigned(AnalysisAliasCatalog.Build(GapInput(), AnalysisAliasContracts.SchemaVersionV2)), questions ?? OpenQuestions, conflicts ?? Conflicts);

    [Fact]
    public void The_assigned_kinds_come_from_the_catalog_and_a_complete_valid_set_has_no_violation()
    {
        var kinds = GapAssessmentRules.Assigned(AnalysisAliasCatalog.Build(GapInput(), AnalysisAliasContracts.SchemaVersionV2));
        Assert.Equal(5, kinds.Count);
        Assert.Equal(EvidenceKind.Gap, kinds["E2"]);
        Assert.Equal(EvidenceKind.Gap, kinds["E5"]);
        Assert.Equal(EvidenceKind.Requirement, kinds["E3"]);

        Assert.Empty(Check([Answered(), OpenItemRecord(support: "E3") with { Reason = GapReason.Partial }]));
        Assert.Empty(Check([OpenItemRecord("E2"), OpenItemRecord("E5", GapReason.Ambiguous, "i9", "E3") with { OpenItem = null, OpenConflict = "c1" }]));
    }

    [Fact]
    public void A_gap_without_a_record_is_reported_and_no_record_is_invented_for_the_other_kinds()
    {
        var missing = Check([Answered()]);
        Assert.Contains(missing, m => m.Contains("E5", StringComparison.Ordinal));

        var forNonGap = Check([Answered(), OpenItemRecord(), OpenItemRecord("E3")]);
        Assert.Contains(forNonGap, m => m.Contains("E3", StringComparison.Ordinal));

        Assert.NotEmpty(Check([Answered(), OpenItemRecord("E9")]));                                    // fuori snapshot
        Assert.NotEmpty(Check([]));                                                                    // lista vuota con Gap assegnati
    }

    [Fact]
    public void Supporting_evidence_must_be_a_non_gap_of_the_snapshot_so_gap_chains_and_cycles_are_impossible()
    {
        Assert.NotEmpty(Check([Answered("E2", "E5"), OpenItemRecord("E5", GapReason.Partial, "i9", "E2")]));     // ciclo E2 <-> E5
        Assert.NotEmpty(Check([Answered("E2", "E5"), OpenItemRecord()]));                                         // Gap come riscontro
        Assert.NotEmpty(Check([Answered("E2", "E9"), OpenItemRecord()]));                                         // fuori snapshot
        Assert.NotEmpty(Check([Answered("E2", "E3"), OpenItemRecord() with { SupportingEvidence = ["E6"] }]));
        Assert.Empty(Check([Answered("E2", "E3", "E1", "E4"), OpenItemRecord()]));
    }

    [Fact]
    public void An_open_reference_must_resolve_among_the_open_questions_or_the_conflicts_of_the_output()
    {
        Assert.NotEmpty(Check([Answered(), OpenItemRecord(item: "i7")]));
        Assert.NotEmpty(Check([Answered(), OpenItemRecord() with { OpenItem = null, OpenConflict = "c2" }]));
        Assert.Empty(Check([Answered(), OpenItemRecord(item: "i7")], ["i7"]));
        Assert.Empty(Check([Answered(), OpenItemRecord() with { OpenItem = null, OpenConflict = "c2" }], conflicts: ["c2"]));
        Assert.Empty(Check([Answered(), OpenItemRecord()]));
    }

    [Fact]
    public void Check_does_not_trust_a_malformed_record_list_and_reports_duplicates_and_shape_violations()
    {
        Assert.NotEmpty(Check([Answered(), Answered(), OpenItemRecord()]));
        Assert.NotEmpty(Check([Answered() with { SupportingEvidence = [] }, OpenItemRecord()]));
        Assert.NotEmpty(Check([Answered(), OpenItemRecord() with { OpenItem = null }]));
    }

    [Fact]
    public void Gap_assessments_have_a_stable_canonical_form_and_hash_independent_of_the_emission_order()
    {
        var records = new[] { Answered(), OpenItemRecord() };
        var reversed = new[] { records[1], records[0] };

        Assert.Equal(GapAssessmentRules.Canonical(records), GapAssessmentRules.Canonical(reversed));
        Assert.Equal(GapAssessmentRules.Hash(records), GapAssessmentRules.Hash(reversed));
        Assert.Matches("^[0-9a-f]{64}$", GapAssessmentRules.Hash(records));
        Assert.Equal(CanonicalJson.HashText(GapAssessmentRules.Canonical(records)), GapAssessmentRules.Hash(records));
        Assert.NotEqual(GapAssessmentRules.Hash(records), GapAssessmentRules.Hash([records[0], records[1] with { Rationale = "diverso" }]));
        Assert.NotEqual(GapAssessmentRules.Hash(records), GapAssessmentRules.Hash([records[0]]));
        Assert.Equal("[]", GapAssessmentRules.Canonical([]));
        Assert.Matches("^[0-9a-f]{64}$", GapAssessmentRules.Hash([]));
        Assert.Equal("gap-assessment-1", GapAssessmentRules.Version);
    }

    // ---- catalogo: forma invariata, contratto nell'identità ----

    [Fact]
    public void The_alias_2_catalog_has_the_same_shape_and_kind_a_new_contract_version_and_a_different_stable_hash()
    {
        var input = GapInput();
        var v1 = AnalysisAliasCatalog.Build(input);
        var v2 = AnalysisAliasCatalog.Build(input, AnalysisAliasContracts.SchemaVersionV2);

        Assert.Equal("alias-catalog-1", v2.Data.CatalogVersion);
        Assert.Equal("document-analysis-alias-1", v1.Data.ContractVersion);
        Assert.Equal("document-analysis-alias-2", v2.Data.ContractVersion);
        Assert.NotEqual(v1.CatalogHash, v2.CatalogHash);
        Assert.NotEqual(v1.RunIndependentHash, v2.RunIndependentHash);
        Assert.Equal(v2.CatalogHash, AnalysisAliasCatalog.Build(input, AnalysisAliasContracts.SchemaVersionV2).CatalogHash);
        Assert.Equal(EvidenceKind.Gap, v2.Evidence("E2")!.Kind);
        Assert.Equal(EvidenceKind.Requirement, v2.Evidence("E3")!.Kind);
        Assert.Equal(ReconciliationCodec.Canonical(v1.Data with { ContractVersion = AnalysisAliasContracts.SchemaVersionV2 }), v2.Encode());   // stessa forma, cambia solo il contratto

        var decoded = AnalysisAliasCatalog.Decode(v2.Encode(), v2.CatalogHash);
        Assert.Equal("document-analysis-alias-2", decoded.Data.ContractVersion);
        Assert.Equal(v2.CatalogHash, decoded.CatalogHash);
    }

    [Fact]
    public void An_unknown_contract_version_cannot_be_frozen_into_a_catalog()
    {
        Assert.ThrowsAny<ArgumentException>(() => AnalysisAliasCatalog.Build(AliasFixtures.Input(), "document-analysis-alias-3"));
        Assert.ThrowsAny<ArgumentException>(() => AnalysisAliasCatalog.Build(AliasFixtures.Input(), "alias-2"));
    }

    [Fact]
    public void The_alias_1_vectors_hashes_and_decoder_are_unchanged()
    {
        Assert.Equal("document-analysis-alias-1", AnalysisAliasContracts.SchemaVersion);
        Assert.Equal("alias-1", AnalysisAliasContracts.ContractValue);
        var dto = AliasFixtures.ValidDto();
        Assert.Equal(PinnedValidDtoHash, AnalysisAliasContracts.Hash(dto));
        Assert.Equal(PinnedFullDtoHash, AnalysisAliasContracts.Hash(AliasFixtures.FullDto()));
        Assert.Equal(PinnedCatalogHash, AnalysisAliasCatalog.Build(AliasFixtures.Input()).CatalogHash);
        Assert.Equal(PinnedCatalogHash, AnalysisAliasCatalog.Build(AliasFixtures.Input(), AnalysisAliasContracts.SchemaVersion).CatalogHash);
        Assert.Equal(PinnedSchemaHash, CanonicalJson.HashText(CanonicalJson.Canonicalize(AnalysisAliasContracts.JsonSchema().GetRawText())));
        Assert.Equal("alias-1", AnalysisAliasContracts.Decode(AnalysisAliasContracts.Encode(dto)).Contract);
    }

    // Valori calcolati sul codice di HEAD 240fa18 (prima di T135): qualsiasi variazione dei vettori storici alias-1 li rompe.
    private const string PinnedValidDtoHash = "aecfff91674806783c537d5a47cdd6b63c482e492bb50f172f44151c29e03d15";
    private const string PinnedFullDtoHash = "1aff2491bd0885debd690ce2b63315c394f0c25abde32dac15caef65342a9b36";
    private const string PinnedCatalogHash = "b65687ca249f44b946173c336c187cc324f5fdecb4af5071dd6ff84344857c84";
    private const string PinnedSchemaHash = "504fb0e7cca77f7f83abfa7d4df021d41b7dd7276ec2e88c483eaa5237232b50";

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
