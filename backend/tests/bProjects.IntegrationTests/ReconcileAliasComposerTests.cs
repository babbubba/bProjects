using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T131 (deterministici, nessun database) — adattatore alias `reconcile-alias-1` per batch (contracts/ai-workflow.md §11): DTO e codec strict, catalogo per batch
/// (E/S/R del run, N per i nodi, item già proposti), compositore server-side di batchId, inputHash, visited*, candidateId, patchId, baseHash, expectedHash,
/// sourceIds, originBatchIds e id canonici, e rifiuto di alias sconosciuti/duplicati/malformati come §7.6 senza correzione fuzzy.
/// </summary>
public sealed class ReconcileAliasComposerTests
{
    private static readonly string BatchId = new('b', 64);
    private static readonly string InputHash = new('c', 64);

    private static readonly DocumentAnalysisInput Input = AliasFixtures.Input(relations: [AliasFixtures.Relation(3, 5, EvidenceRelationType.Contradicts)]);
    private static readonly AnalysisAliasCatalog RunCatalog = AnalysisAliasCatalog.Build(Input);

    private static CatalogEntry Entry(int n)
    {
        var e = Input.Evidence[n - 1];
        return new()
        {
            EntryId = ReconciliationIds.EntryId(Input.Snapshot.SourceHash, e.EvidenceId, e.ContentHash), EvidenceId = e.EvidenceId, EvidenceVersionId = e.VersionId, ContentHash = e.ContentHash,
            SourceIds = [.. e.Sources.Select(s => s.SourceId).Distinct()], Keys = [], KnownRelationIds = [], UnclassifiedViews = [],
        };
    }

    private static CatalogNode Node(char c, int ordinal = 0) => new()
    {
        NodeId = new string(c, 64), View = ReconcileView.A, Level = 0, Ordinal = ordinal, ChildNodeIds = [], MemberSetHash = new string((char)('a' + (c - '0')), 64),
        MemberCount = 2, KeyPageHashes = [], RelationPageHashes = [], Descriptor = $"descriptor {c}", DescriptorOutputHash = new string('d', 64),
    };

    private static ReconcileBatchRequest Request(string stage, ReconcileView view, int[] entries, CatalogNode[]? nodes = null, ReconcileCandidate[]? focus = null, int level = 0) => new(
        new DerivedBatchInput(BatchId, stage, view.ToString(), null, level, 0, [], "{}", 10, "measured", InputHash, "reconcile-alias-1", "document-reconcile-v3"),
        view, level, [.. entries.Select(Entry)], nodes ?? [], focus);

    private static ReconcileAliasCatalog Catalog(ReconcileBatchRequest request, IReadOnlyList<ReconcileItemRef>? items = null) => ReconcileAliasCatalog.Build(RunCatalog, request, items ?? []);

    private static ReconcileAliasComposition Compose(string json, ReconcileBatchRequest request, IReadOnlyList<ReconcileItemRef>? items = null, ReconciliationBase? baseState = null, string? expected = null) =>
        ReconcileAliasComposer.Compose(json, Catalog(request, items), request, Input.Snapshot, baseState, expected);

    private static JsonObject Dto(Action<JsonObject>? change = null, string? noFindings = "nessun collegamento emerso", string? descriptor = "gruppo")
    {
        var o = new JsonObject
        {
            ["contract"] = "reconcile-alias-1", ["descriptor"] = descriptor, ["noFindings"] = noFindings, ["items"] = new JsonArray(), ["candidates"] = new JsonArray(), ["focus"] = null,
            ["patches"] = new JsonArray(),
        };
        change?.Invoke(o);
        return o;
    }

    private static JsonNode Item(string key, string section, string classification, params string[] evidence) => JsonNode.Parse(JsonSerializer.Serialize(new { key, section, text = "testo " + key, classification, evidence }))!;

    private static JsonNode Cand(string key, string left, string right, string type = "Contradicts") => JsonNode.Parse(JsonSerializer.Serialize(new { key, left, right, type, basis = "Inferred", rationale = "numeri diversi" }))!;

    private static ReconcileBatchRequest LeafA(params int[] entries) => Request("leaf", ReconcileView.A, entries.Length == 0 ? [1, 2, 3] : entries);

    // ================================================================ DTO e codec strict

    [Fact]
    public void The_dto_round_trips_and_its_hash_is_independent_of_property_order_and_whitespace()
    {
        var json = Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Documented", "E2"))).ToJsonString();
        var dto = ReconcileAliasContracts.Decode(json);
        var reordered = JsonNode.Parse(json)!.AsObject();
        var items = reordered["items"]!.DeepClone();
        reordered.Remove("items");
        reordered["items"] = items;

        Assert.Equal(ReconcileAliasContracts.Hash(dto), ReconcileAliasContracts.Hash(ReconcileAliasContracts.Decode(reordered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))));
        Assert.Matches("^[0-9a-f]{64}$", ReconcileAliasContracts.Hash(dto));
        Assert.Equal(AiSection.Requirements, dto.Items[0].Section);
        Assert.Equal("reconcile-alias-1", ReconcileAliasContracts.SchemaVersion);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("missing_property")]
    [InlineData("null_items")]
    [InlineData("bad_contract")]
    [InlineData("numeric_enum")]
    [InlineData("lowercase_enum")]
    [InlineData("quoted_number_alias")]
    [InlineData("lowercase_alias")]
    [InlineData("leading_zero_alias")]
    [InlineData("spaced_alias")]
    [InlineData("uuid_alias")]
    [InlineData("unknown_section")]
    [InlineData("endpoint_prefix")]
    [InlineData("duplicate_property")]
    public void The_codec_rejects_anything_that_is_not_exactly_the_closed_contract(string kind)
    {
        var o = Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Documented", "E2")));
        o["candidates"] = new JsonArray(Cand("f1", "E1", "E2"));
        string json;
        switch (kind)
        {
            case "extra": o["batchId"] = "x"; json = o.ToJsonString(); break;
            case "missing_property": o.Remove("focus"); json = o.ToJsonString(); break;
            case "null_items": o["items"] = null; json = o.ToJsonString(); break;
            case "bad_contract": o["contract"] = "alias-1"; json = o.ToJsonString(); break;
            case "numeric_enum": o["candidates"]![0]!["type"] = 2; json = o.ToJsonString(); break;
            case "lowercase_enum": o["candidates"]![0]!["type"] = "contradicts"; json = o.ToJsonString(); break;
            case "quoted_number_alias": json = o.ToJsonString().Replace("\"E2\"", "2"); break;
            case "lowercase_alias": o["items"]![0]!["evidence"] = new JsonArray("e2"); json = o.ToJsonString(); break;
            case "leading_zero_alias": o["items"]![0]!["evidence"] = new JsonArray("E02"); json = o.ToJsonString(); break;
            case "spaced_alias": o["items"]![0]!["evidence"] = new JsonArray("E 2"); json = o.ToJsonString(); break;
            case "uuid_alias": o["items"]![0]!["evidence"] = new JsonArray(Guid.NewGuid().ToString()); json = o.ToJsonString(); break;
            case "unknown_section": o["items"]![0]!["section"] = "risks"; json = o.ToJsonString(); break;
            case "endpoint_prefix": o["candidates"]![0]!["left"] = "S1"; json = o.ToJsonString(); break;
            default: json = o.ToJsonString().Replace("\"descriptor\":\"gruppo\"", "\"descriptor\":\"gruppo\",\"descriptor\":\"altro\""); break;
        }

        Assert.ThrowsAny<JsonException>(() => ReconcileAliasContracts.Decode(json));
    }

    [Fact]
    public void The_json_schema_is_a_closed_contract_with_alias_patterns_and_no_identifier_or_hash_field()
    {
        var schema = ReconcileAliasContracts.JsonSchema().GetRawText();
        using var document = JsonDocument.Parse(schema);
        var root = document.RootElement;

        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["contract", "descriptor", "noFindings", "items", "candidates", "focus", "patches"],
            root.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Contains("^[EN][1-9][0-9]*$", schema);
        Assert.Contains("^E[1-9][0-9]*$", schema);
        Assert.DoesNotContain("uuid", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("batchId", schema);
        Assert.DoesNotContain("candidateId", schema);
        Assert.DoesNotContain("baseHash", schema);
        Assert.Equal(ReconcileAliasContracts.JsonSchema().GetRawText(), schema);   // stabile fra due letture
    }

    // ================================================================ catalogo per batch

    [Fact]
    public void The_batch_catalog_uses_the_run_aliases_for_entries_numbers_nodes_per_batch_and_exposes_only_the_received_endpoints()
    {
        var focus = new ReconcileCandidate
        {
            CandidateId = new string('f', 64), Kind = CandidateKind.Relation, Left = new CandidateEndpoint { Kind = CandidateEndpointKind.Set, Id = new string('5', 64), Hash = new string('6', 64) },
            Right = new CandidateEndpoint { Kind = CandidateEndpointKind.Evidence, Id = Input.Evidence[2].EvidenceId.ToString("D"), Hash = Input.Evidence[2].ContentHash },
            ProposedType = ReconcileProposedType.SameRule, Rationale = "shared_literal_key", Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = ["x"], ParentCandidateId = null,
        };
        var request = Request("expand", ReconcileView.A, [3, 5], [Node('1')], [focus]);

        var catalog = Catalog(request);

        Assert.Equal("E3", catalog.Entry("E3")!.Alias);
        Assert.Equal("E5", catalog.Entry("E5")!.Alias);
        Assert.Null(catalog.Entry("E1"));                 // esiste nel run ma non in questo batch
        Assert.Null(catalog.Entry("e3"));                 // nessuna normalizzazione
        Assert.Equal(new string('1', 64), catalog.Node("N1")!.Id);
        Assert.Equal(CandidateEndpointKind.Set, catalog.Node("N2")!.Kind);   // l'estremo Set del candidato in esame prosegue la numerazione
        Assert.Equal(new string('6', 64), catalog.Node("N2")!.Hash);
        Assert.Null(catalog.Node("N3"));
        Assert.Equal(catalog.CatalogHash, Catalog(request).CatalogHash);
        Assert.NotEqual(catalog.CatalogHash, Catalog(Request("expand", ReconcileView.A, [3], [Node('1')], [focus])).CatalogHash);
    }

    [Fact]
    public void The_batch_catalog_lists_known_relations_only_when_both_endpoints_are_in_the_batch_and_items_only_for_candidate_stages()
    {
        var items = new List<ReconcileItemRef>
        {
            new("leaf-A0.i1", "primo", [Input.Evidence[0].EvidenceId]), new("leaf-A0.i2", "limite", [Input.Evidence[2].EvidenceId, Input.Evidence[3].EvidenceId]),
            new("leaf-A1.i1", "altro limite", [Input.Evidence[4].EvidenceId]),
        };

        var compare = Catalog(Request("compare", ReconcileView.A, [3, 5]), items);
        var leaf = Catalog(Request("leaf", ReconcileView.A, [3, 5]), items);
        var oneSide = Catalog(Request("compare", ReconcileView.A, [3, 4]), items);

        Assert.Equal("leaf-A0.i2", compare.Item("i2")!.LocalKey);     // handle globale: indice nella lista dei item già proposti
        Assert.Equal("leaf-A1.i1", compare.Item("i3")!.LocalKey);
        Assert.Null(compare.Item("i1"));                                // non cita nessuna evidenza del batch
        Assert.Null(leaf.Item("i2"));                                   // le foglie non mostrano item di altri batch
        Assert.Equal("R1", compare.Relation("R1")!.Alias);
        Assert.Null(oneSide.Relation("R1"));                            // la relazione nota collega E3 a E5: E5 non è nel batch
    }

    // ================================================================ composizione foglia A

    [Fact]
    public void A_leaf_a_batch_composes_the_canonical_result_deriving_every_identity_hash_visit_and_source_on_the_server()
    {
        var request = LeafA();
        var json = Dto(o => o["items"] = new JsonArray(
            Item("i1", "goals", "Declared", "E1"), Item("i2", "requirements", "Documented", "E3", "E2"), Item("i3", "assumptions", "Assumed", "E3"))).ToJsonString();

        var composed = Compose(json, request);

        Assert.True(composed.IsValid, string.Join(" | ", composed.Violations));
        var result = composed.Result!;
        Assert.Equal((BatchId, InputHash, ReconcileView.A, 0), (result.BatchId, result.InputHash, result.View, result.Level));
        Assert.Equal(request.Entries.Select(e => e.EntryId).Order(StringComparer.Ordinal), result.VisitedEntryIds);
        Assert.Empty(result.VisitedNodeIds);
        Assert.Equal("gruppo", result.Descriptor);
        Assert.Equal(3, result.ItemProposals.Count);
        Assert.Equal("leaf-A0.i1", result.ItemProposals[0].LocalKey);
        var multi = result.ItemProposals[1];
        Assert.Equal(ReconciliationIds.SortedIds([Input.Evidence[1].EvidenceId, Input.Evidence[2].EvidenceId]).ToArray(), ReconciliationIds.SortedIds(multi.EvidenceIds).ToArray());
        Assert.Equal(multi.EvidenceIds.Select(g => g.ToString("D")).Order(StringComparer.Ordinal), multi.EvidenceIds.Select(g => g.ToString("D")));
        Assert.Equal(
            Input.Evidence[1].Sources.Concat(Input.Evidence[2].Sources).Select(s => s.SourceId).Distinct().Select(g => g.ToString("D")).Order(StringComparer.Ordinal),
            multi.SourceIds.Select(g => g.ToString("D")));
        Assert.Equal(AiSection.Goals, composed.Sections["leaf-A0.i1"]);
        Assert.Equal(AiSection.Assumptions, composed.Sections["leaf-A0.i3"]);
        Assert.Empty(ReconciliationContractRules.CheckResult(result, Input.Snapshot.SourceHash));
        Assert.Empty(ReconciliationReferences.CheckResult(result, Input.Snapshot, new ReconciliationCatalog([], [], [])));
        Assert.Matches("^[0-9a-f]{64}$", composed.AiOutputHash!);
        Assert.Equal(CanonicalJson.Sha256Hex(ReconciliationCodec.Encode(result)), composed.CanonicalHash);
        Assert.Equal(Catalog(request).CatalogHash, composed.CatalogHash);
        Assert.Equal(composed.CanonicalHash, Compose(json, request, expected: composed.CanonicalHash).CanonicalHash);   // stessi input, stesso output
    }

    [Fact]
    public void An_item_without_evidence_is_accepted_only_as_an_ungrounded_open_question_and_never_becomes_a_proposal()
    {
        var request = LeafA(1, 2);
        var ok = Compose(Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Declared", "E1"), Item("i2", "openQuestions", "Assumed"))).ToJsonString(), request);
        var bad = Compose(Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Declared"))).ToJsonString(), request);

        Assert.True(ok.IsValid, string.Join(" | ", ok.Violations));
        Assert.Single(ok.Result!.ItemProposals);
        var question = Assert.Single(ok.Ungrounded);
        Assert.Equal((AiSection.OpenQuestions, "leaf-A0.i2"), (question.Section, question.LocalKey));
        Assert.False(bad.IsValid);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, bad.ErrorCode);
        Assert.Contains("items[0]", string.Join(" | ", bad.Violations));
    }

    [Theory]
    [InlineData("assumptions", "Documented", "E3", "assumptions sono Assumed")]               // assumptions solo Assumed
    [InlineData("requirements", "Declared", "E3", "Declared richiede")]                      // E3 è un documento: mai Declared
    [InlineData("requirements", "Documented", "E1", "Documented richiede")]                  // E1 è un messaggio del PM: mai Documented
    public void The_content_rules_of_analysis_1_1_are_applied_to_each_batch_before_any_other_call_is_spent(string section, string classification, string alias, string expected)
    {
        var composed = Compose(Dto(o => o["items"] = new JsonArray(Item("i1", section, classification, alias))).ToJsonString(), LeafA(1, 2, 3));

        Assert.False(composed.IsValid);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, composed.ErrorCode);
        Assert.Contains(expected, string.Join(" | ", composed.Violations));
        Assert.Contains("items[0]", string.Join(" | ", composed.Violations));
    }

    // ================================================================ rifiuti (§7.6)

    public static TheoryData<string, string> Rejections() => new()
    {
        { "unknown", "E999" },
        { "not_in_batch", "E4" },          // esiste nel run, non fra le voci ricevute: nessuna ricerca nel resto del catalogo
        { "other_namespace", "S1" },
        { "node_for_entry", "N1" },
        { "lowercase", "e1" },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void Unknown_foreign_or_malformed_evidence_aliases_are_rejected_with_alias_and_path_and_never_corrected(string kind, string alias)
    {
        var request = LeafA(1, 2, 3);
        var json = Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Declared", "E1")), noFindings: null).ToJsonString().Replace("\"E1\"", $"\"{alias}\"");

        var composed = Compose(json, request);

        Assert.False(composed.IsValid, kind);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, composed.ErrorCode);
        Assert.Null(composed.Result);
        Assert.NotEmpty(composed.Violations);
        if (kind is "unknown" or "not_in_batch")
        {
            Assert.Contains(composed.Violations, v => v.Contains(alias, StringComparison.Ordinal) && v.Contains("items[0].evidence[0]", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Duplicate_aliases_keys_and_identities_are_rejected_without_deduplication()
    {
        var request = LeafA(1, 2, 3);
        string Reason(Action<JsonObject> change) =>
            string.Join(" | ", Compose(Dto(change, noFindings: null).ToJsonString(), request).Violations);

        Assert.Contains("E1", Reason(o => o["items"] = new JsonArray(Item("i1", "requirements", "Declared", "E1", "E1"))));
        Assert.Contains("i1", Reason(o => o["items"] = new JsonArray(Item("i1", "requirements", "Declared", "E1"), Item("i1", "goals", "Declared", "E2"))));
        Assert.Contains("f1", Reason(o =>
        {
            o["items"] = new JsonArray(Item("i1", "requirements", "Declared", "E1"));
            o["candidates"] = new JsonArray(Cand("f1", "E1", "E2"), Cand("f1", "E1", "E3"));
        }));
        // stessa identità canonica (kind, estremi, tipo): candidato duplicato, anche con chiavi diverse
        Assert.Contains("duplicato", Reason(o =>
        {
            o["items"] = new JsonArray(Item("i1", "requirements", "Declared", "E1"));
            o["candidates"] = new JsonArray(Cand("f1", "E1", "E2"), Cand("f2", "E2", "E1"));   // Contradicts è simmetrico
        }));
        Assert.Contains("coincidenti", Reason(o =>
        {
            o["items"] = new JsonArray(Item("i1", "requirements", "Declared", "E1"));
            o["candidates"] = new JsonArray(Cand("f1", "E2", "E2"));
        }));
    }

    // ================================================================ regole per tipo di batch

    [Fact]
    public void Items_are_allowed_only_in_leaf_a_batches_and_patches_only_in_expand_and_compare_batches()
    {
        var item = new JsonArray(Item("i1", "requirements", "Declared", "E1"));
        string Violations(ReconcileBatchRequest request, Action<JsonObject> change, string? noFindings = null) =>
            string.Join(" | ", Compose(Dto(change, noFindings).ToJsonString(), request).Violations);

        Assert.Contains("items", Violations(Request("leaf", ReconcileView.B, [1, 2]), o => o["items"] = item.DeepClone()));
        Assert.Contains("items", Violations(Request("expand", ReconcileView.A, [1, 2], null, [FocusRelation(1, 2)]), o => o["items"] = item.DeepClone()));
        Assert.Contains("patches", Violations(LeafA(1, 2), o =>
        {
            o["items"] = item.DeepClone();
            o["patches"] = new JsonArray(JsonNode.Parse("""{"key":"p1","op":"AddSupport","target":{"item":"i1","relation":null},"evidence":["E1"],"value":{}}"""));
        }));
    }

    private static ReconcileCandidate FocusRelation(int a, int b, ReconcileProposedType type = ReconcileProposedType.Contradicts)
    {
        var (l, r) = (ReconciliationFixtures.Endpoint(Input.Evidence[a - 1].EvidenceId, Input.Evidence[a - 1].ContentHash), ReconciliationFixtures.Endpoint(Input.Evidence[b - 1].EvidenceId, Input.Evidence[b - 1].ContentHash));
        return new()
        {
            CandidateId = ReconciliationIds.CandidateId(CandidateKind.Conflict, l, r, type, Input.Snapshot.SourceHash), Kind = CandidateKind.Conflict, Left = l, Right = r,
            ProposedType = type, Rationale = "shared_literal_key", Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = ["origine"], ParentCandidateId = null,
        };
    }

    [Fact]
    public void A_compare_batch_requires_a_compared_or_open_focus_and_derives_the_disposition_with_the_ai_output_hash()
    {
        var focus = FocusRelation(3, 5);
        var request = Request("compare", ReconcileView.A, [3, 5], null, [focus]);
        string Json(string? focusJson) => Dto(o => o["focus"] = focusJson is null ? null : JsonNode.Parse(focusJson)).ToJsonString();

        var compared = Compose(Json("""{"outcome":"Compared","reason":null}"""), request);
        var open = Compose(Json("""{"outcome":"Open","reason":"30 giorni contro 45"}"""), request);

        Assert.True(compared.IsValid && open.IsValid, string.Join(" | ", compared.Violations.Concat(open.Violations)));
        var d = Assert.Single(compared.Result!.Dispositions);
        Assert.Equal((focus.CandidateId, CandidateState.Compared), (d.CandidateId, d.State));
        Assert.Equal([compared.AiOutputHash!], d.ComparisonOutputHashes);
        Assert.Equal((CandidateState.Open, "30 giorni contro 45"), (Assert.Single(open.Result!.Dispositions).State, Assert.Single(open.Result!.Dispositions).Reason));
        Assert.Empty(ReconciliationContractRules.CheckDisposition(d));

        foreach (var bad in new[] { null, """{"outcome":"Unresolved","reason":null}""", """{"outcome":"Open","reason":null}""", """{"outcome":"Open","reason":"  "}""" })
        {
            var rejected = Compose(Json(bad), request);
            Assert.False(rejected.IsValid, bad ?? "focus null");
            Assert.Contains("focus", string.Join(" | ", rejected.Violations));
        }
    }

    [Fact]
    public void An_expand_batch_allows_only_no_focus_or_the_fixed_candidate_unresolved_state()
    {
        var focus = FocusRelation(3, 5);
        var request = Request("expand", ReconcileView.A, [3, 5], null, [focus]);

        var none = Compose(Dto(o => { }).ToJsonString(), request);
        var unresolved = Compose(Dto(o => o["focus"] = JsonNode.Parse("""{"outcome":"Unresolved","reason":"collegamento senza estremi"}""")).ToJsonString(), request);
        var compared = Compose(Dto(o => o["focus"] = JsonNode.Parse("""{"outcome":"Compared","reason":null}""")).ToJsonString(), request);

        Assert.True(none.IsValid);
        Assert.Empty(none.Result!.Dispositions);
        var d = Assert.Single(unresolved.Result!.Dispositions);
        Assert.Equal((CandidateState.NotProcessed, ReconciliationCandidates.CandidateUnresolved), (d.State, d.Reason));   // il motivo è fisso: il server non copia testo del modello
        Assert.False(compared.IsValid);   // una visita non è un confronto
        Assert.Contains("focus", string.Join(" | ", compared.Violations));
        Assert.False(Compose(Dto(o => o["focus"] = JsonNode.Parse("""{"outcome":"Compared","reason":null}""")).ToJsonString(), LeafA(1, 2)).IsValid);   // focus fuori da expand/compare
    }

    [Fact]
    public void Candidates_derive_kind_id_origin_and_parent_on_the_server_and_follow_the_endpoint_rules_of_each_stage()
    {
        var focus = FocusRelation(3, 5);
        var expand = Request("expand", ReconcileView.A, [3, 4, 5], null, [focus]);
        var json = Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E3", "E4", "Contradicts"), Cand("f2", "E4", "E5", "DependsOn"))).ToJsonString();

        var composed = Compose(json, expand);

        Assert.True(composed.IsValid, string.Join(" | ", composed.Violations));
        Assert.All(composed.Result!.Candidates, c =>
        {
            Assert.Equal([BatchId], c.OriginBatchIds);
            Assert.Equal(focus.CandidateId, c.ParentCandidateId);
            Assert.Equal(CandidateEndpointKind.Evidence, c.Left.Kind);
            Assert.Empty(ReconciliationContractRules.CheckCandidate(c, Input.Snapshot.SourceHash));
        });
        Assert.Equal(CandidateKind.Conflict, composed.Result.Candidates.Single(c => c.ProposedType == ReconcileProposedType.Contradicts).Kind);
        Assert.Equal(CandidateKind.Relation, composed.Result.Candidates.Single(c => c.ProposedType == ReconcileProposedType.DependsOn).Kind);

        // Alias: il tipo Alias genera un candidato di kind Alias; le foglie non hanno parent e propongono solo estremi Evidence.
        var alias = Compose(Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E1", "E2", "Alias"))).ToJsonString(), LeafA(1, 2));
        Assert.Equal(CandidateKind.Alias, Assert.Single(alias.Result!.Candidates).Kind);
        Assert.Null(alias.Result.Candidates[0].ParentCandidateId);
        Assert.False(Compose(Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E1", "N1"))).ToJsonString(), Request("leaf", ReconcileView.A, [1, 2], [Node('1')])).IsValid);
    }

    [Fact]
    public void Reduce_and_cross_batches_cite_only_nodes_derive_visited_nodes_and_never_visited_entries()
    {
        var nodes = new[] { Node('1', 0), Node('2', 1) };
        var request = Request("reduce", ReconcileView.A, [], nodes, null, level: 1);
        var json = Dto(o => o["candidates"] = new JsonArray(Cand("f1", "N1", "N2", "SameRule")), descriptor: "unione dei gruppi").ToJsonString();

        var composed = Compose(json, request);

        Assert.True(composed.IsValid, string.Join(" | ", composed.Violations));
        Assert.Equal(nodes.Select(n => n.NodeId).Order(StringComparer.Ordinal), composed.Result!.VisitedNodeIds);
        Assert.Empty(composed.Result.VisitedEntryIds);
        var c = Assert.Single(composed.Result.Candidates);
        Assert.Equal((CandidateEndpointKind.Node, nodes[0].NodeId, nodes[0].MemberSetHash), (c.Left.Kind, c.Left.Id, c.Left.Hash));   // hash dell'endpoint = memberSetHash del catalogo
        Assert.False(Compose(Dto(o => o["candidates"] = new JsonArray(Cand("f1", "N1", "N9"))).ToJsonString(), request).IsValid);
        Assert.False(Compose(Dto(o => o["candidates"] = new JsonArray(Cand("f1", "N1", "E1"))).ToJsonString(), request).IsValid);
    }

    // ================================================================ patch composte sulla base del server

    [Fact]
    public void A_patch_is_stamped_with_the_server_base_hash_expected_hash_patch_id_sources_and_origin_and_validated_against_that_base()
    {
        var proposals = new List<ItemProposal>
        {
            new() { LocalKey = "leaf-A0.i1", Text = "limite 5", Classification = DocumentAnalysisClassification.Documented, EvidenceIds = [Input.Evidence[2].EvidenceId], SourceIds = [AliasFixtures.Sx] },
        };
        var baseState = DocumentAnalysisBase.Compute(proposals, Input.Snapshot);
        var items = new List<ReconcileItemRef> { new("leaf-A0.i1", "limite 5", [Input.Evidence[2].EvidenceId]) };
        var request = Request("compare", ReconcileView.A, [3, 5], null, [FocusRelation(3, 5)]);
        var json = Dto(o =>
        {
            o["focus"] = JsonNode.Parse("""{"outcome":"Compared","reason":null}""");
            o["patches"] = new JsonArray(
                JsonNode.Parse("""{"key":"p1","op":"AddRelation","target":{"item":"i1","relation":null},"evidence":[],"value":{"from":"E3","to":"E5","type":"Confirms","basis":"Inferred","rationale":"stesso limite"}}"""),
                JsonNode.Parse("""{"key":"p2","op":"AddSupport","target":{"item":null,"relation":"R1"},"evidence":["E3"],"value":{}}"""));
        }).ToJsonString();

        var composed = Compose(json, request, items, baseState);

        Assert.True(composed.IsValid, string.Join(" | ", composed.Violations));
        Assert.Equal(2, composed.Result!.Patches.Count);
        Assert.All(composed.Result.Patches, p =>
        {
            Assert.Equal(baseState.BaseHash, p.BaseHash);
            Assert.Equal([BatchId], p.OriginBatchIds);
            Assert.Empty(ReconciliationContractRules.CheckPatch(p));
            Assert.Empty(ReconciliationReferences.CheckPatch(p, Input.Snapshot));
            Assert.Equal(baseState.TargetHashes[ReconciliationBase.TargetKey(p.Target.Kind, p.Target.Id)], p.Target.ExpectedHash);
        });
        var relation = composed.Result.Patches.Single(p => p.Operation == PatchOperation.AddRelation);
        Assert.Equal(("leaf-A0.i1", PatchTargetKind.Item), (relation.Target.Id, relation.Target.Kind));
        Assert.Equal(2, relation.EvidenceIds.Count);
        var support = composed.Result.Patches.Single(p => p.Operation == PatchOperation.AddSupport);
        Assert.Equal((PatchTargetKind.Relation, AliasFixtures.RelationId.ToString("D")), (support.Target.Kind, support.Target.Id));
        Assert.Equal(ReconciliationIds.PatchId(support), support.PatchId);
        // Senza la base del passaggio nessuna patch può essere firmata.
        Assert.False(Compose(json, request, items, null).IsValid);
    }

    [Theory]
    [InlineData("""{"key":"p1","op":"AddSupport","target":{"item":"i7","relation":null},"evidence":["E3"],"value":{}}""", "i7")]            // handle inesistente
    [InlineData("""{"key":"p1","op":"AddSupport","target":{"item":null,"relation":"R9"},"evidence":["E3"],"value":{}}""", "R9")]          // relazione sconosciuta
    [InlineData("""{"key":"p1","op":"AddSupport","target":{"item":"i1","relation":"R1"},"evidence":["E3"],"value":{}}""", "target")]      // due bersagli
    [InlineData("""{"key":"p1","op":"AddSupport","target":{"item":"i1","relation":null},"evidence":[],"value":{}}""", "evidence")]         // AddSupport senza evidenze
    [InlineData("""{"key":"p1","op":"AddSupport","target":{"item":"i1","relation":null},"evidence":["E1"],"value":{}}""", "E1")]           // evidenza fuori dal batch
    [InlineData("""{"key":"p1","op":"AddRelation","target":{"item":"i1","relation":null},"evidence":[],"value":{"from":"E3","to":"E3","type":"Confirms","basis":"Inferred","rationale":null}}""", "coincidenti")]
    public void An_invalid_patch_is_rejected_with_its_alias_and_path(string patch, string expectedFragment)
    {
        var proposals = new List<ItemProposal>
        {
            new() { LocalKey = "leaf-A0.i1", Text = "limite 5", Classification = DocumentAnalysisClassification.Documented, EvidenceIds = [Input.Evidence[2].EvidenceId], SourceIds = [AliasFixtures.Sx] },
        };
        var baseState = DocumentAnalysisBase.Compute(proposals, Input.Snapshot);
        var request = Request("compare", ReconcileView.A, [3, 5], null, [FocusRelation(3, 5)]);

        var composed = Compose(
            Dto(o =>
            {
                o["focus"] = JsonNode.Parse("""{"outcome":"Compared","reason":null}""");
                o["patches"] = new JsonArray(JsonNode.Parse(patch));
            }).ToJsonString(),
            request, [new("leaf-A0.i1", "limite 5", [Input.Evidence[2].EvidenceId])], baseState);

        Assert.False(composed.IsValid);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, composed.ErrorCode);
        Assert.Contains(expectedFragment, string.Join(" | ", composed.Violations));
    }

    // ================================================================ replay e integrità

    [Fact]
    public void A_recomposition_with_a_different_canonical_hash_is_an_integrity_conflict_and_is_never_trusted()
    {
        var request = LeafA(1, 2);
        var json = Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Declared", "E1"))).ToJsonString();

        var composed = Compose(json, request, expected: new string('0', 64));

        Assert.False(composed.IsValid);
        Assert.Equal(DocumentWorkException.IntegrityConflict, composed.ErrorCode);
        Assert.Contains("canonicalHash", string.Join(" | ", composed.Violations));
    }

    [Fact]
    public void The_prompt_v3_is_embedded_states_the_alias_contract_and_never_asks_to_copy_identifiers_while_v2_stays_frozen()
    {
        var v3 = ReconcileAliasContracts.Instructions;

        Assert.Equal("document-reconcile-v3", ReconcileAliasContracts.PromptVersion);
        Assert.Equal("document-reconcile-v2", ReconciliationHierarchy.PromptVersion);
        Assert.Contains("reconcile-alias-1", v3);
        Assert.Contains("E<n>", v3);
        Assert.Contains("N<n>", v3);
        Assert.Contains("focus", v3);
        Assert.DoesNotContain("batchId", v3);
        Assert.DoesNotContain("inputHash", v3);
        Assert.DoesNotContain("entryId", v3);
        Assert.DoesNotContain("candidateId", v3);
        Assert.DoesNotContain("visitedEntryIds", v3);
        Assert.Matches("[Nn]on .*(UUID|hash)", v3);   // vieta esplicita di scrivere UUID o hash
        Assert.DoesNotContain("reconcile-alias-1", ReconciliationHierarchy.Instructions);   // v2 non è stato toccato
        Assert.Contains("reconcile-1", ReconciliationHierarchy.Instructions);
    }

    // ================================================================ T135: reconcile-alias-2 e gap-assessment-1 (contracts/ai-workflow.md §13.1-§13.2)

    private static JsonObject DtoV2(Action<JsonObject>? change = null, params JsonNode[] gaps)
    {
        var o = Dto();
        o["contract"] = "reconcile-alias-2";
        o["gapAssessments"] = new JsonArray(gaps);
        change?.Invoke(o);
        return o;
    }

    private static JsonNode GapRecord(string gap = "E2", string outcome = "AnsweredInSnapshot", string reason = "Answered", string[]? support = null, string? item = null, string? conflict = null) =>
        JsonNode.Parse(JsonSerializer.Serialize(new { gap, outcome, reason, rationale = "La scheda risponde.", supportingEvidence = support ?? ["E3"], openItem = item, openConflict = conflict }))!;

    private static void AssertRejectedV2(JsonNode node) => Assert.ThrowsAny<JsonException>(() => ReconcileAliasContracts.DecodeV2(node.ToJsonString()));

    [Fact]
    public void The_reconcile_alias_2_dto_round_trips_and_its_hash_covers_the_gap_assessments()
    {
        var json = DtoV2(null, GapRecord(), GapRecord("E5", "Open", "Partial", ["E3"], item: "i1")).ToJsonString();
        var dto = ReconcileAliasContracts.DecodeV2(json);

        Assert.Equal("reconcile-alias-2", dto.Contract);
        Assert.Equal("reconcile-alias-2", ReconcileAliasContracts.SchemaVersionV2);
        Assert.Equal(2, dto.GapAssessments.Count);
        Assert.Equal(ReconcileAliasContracts.Canonical(dto), ReconcileAliasContracts.Canonical(ReconcileAliasContracts.DecodeV2(ReconcileAliasContracts.Encode(dto))));
        Assert.NotEqual(ReconcileAliasContracts.Hash(dto), ReconcileAliasContracts.Hash(dto with { GapAssessments = [dto.GapAssessments[0]] }));
        Assert.NotEqual(ReconcileAliasContracts.Hash(dto), ReconcileAliasContracts.Hash(ReconcileAliasContracts.DecodeV2(DtoV2().ToJsonString())));
        Assert.Empty(ReconcileAliasContracts.DecodeV2(DtoV2().ToJsonString()).GapAssessments);
    }

    [Fact]
    public void The_gap_assessments_list_is_required_and_the_two_reconcile_contracts_do_not_cross()
    {
        var missing = DtoV2();
        missing.Remove("gapAssessments");
        AssertRejectedV2(missing);
        AssertRejectedV2(DtoV2(o => o["gapAssessments"] = null));
        AssertRejectedV2(DtoV2(o => o["contract"] = "reconcile-alias-1"));
        AssertRejectedV2(DtoV2(o => o["contract"] = "alias-2"));
        AssertRejectedV2(Dto());                                                                  // un output v1 senza lista non è v2
        Assert.ThrowsAny<JsonException>(() => ReconcileAliasContracts.Decode(DtoV2().ToJsonString()));
        Assert.ThrowsAny<JsonException>(() => ReconcileAliasContracts.Decode(Dto(o => o["gapAssessments"] = new JsonArray()).ToJsonString()));
    }

    [Fact]
    public void Reconcile_alias_2_applies_the_same_assessment_matrix_and_rejects_everything_else()
    {
        AssertRejectedV2(DtoV2(null, GapRecord(reason: "NoAnswer")));
        AssertRejectedV2(DtoV2(null, GapRecord(support: [])));
        AssertRejectedV2(DtoV2(null, GapRecord(item: "i1")));
        AssertRejectedV2(DtoV2(null, GapRecord(outcome: "Open", reason: "Answered", item: "i1")));
        AssertRejectedV2(DtoV2(null, GapRecord(outcome: "Open", reason: "NoAnswer", support: [])));                       // nessun riferimento aperto
        AssertRejectedV2(DtoV2(null, GapRecord(outcome: "Open", reason: "NoAnswer", support: [], item: "i1", conflict: "c1")));
        AssertRejectedV2(DtoV2(null, GapRecord(outcome: "Open", reason: "Ambiguous", support: [], item: "i1")));
        AssertRejectedV2(DtoV2(null, GapRecord(support: ["E3", "E3"])));
        AssertRejectedV2(DtoV2(null, GapRecord(support: ["E3", "E2"])));
        AssertRejectedV2(DtoV2(null, GapRecord(), GapRecord()));                                                             // gap duplicato
        AssertRejectedV2(DtoV2(null, GapRecord("S1")));
        AssertRejectedV2(DtoV2(null, GapRecord("N1")));
        AssertRejectedV2(DtoV2(null, GapRecord(support: ["N1"])));
        AssertRejectedV2(DtoV2(null, GapRecord(outcome: "Open", reason: "NoAnswer", support: [], item: "c1")));
        AssertRejectedV2(DtoV2(null, GapRecord(outcome: "Open", reason: "NoAnswer", support: [], conflict: "i1")));
        AssertRejectedV2(DtoV2(null, GapRecord(outcome: "open")));
        AssertRejectedV2(DtoV2(null, GapRecord(reason: "answered")));
        AssertRejectedV2(DtoV2(o => o["extra"] = 1, GapRecord()));

        var extra = GapRecord();
        extra["id"] = Guid.NewGuid().ToString();
        AssertRejectedV2(DtoV2(null, extra));
        var missingMember = GapRecord();
        missingMember.AsObject().Remove("openConflict");
        AssertRejectedV2(DtoV2(null, missingMember));

        Assert.Single(ReconcileAliasContracts.DecodeV2(DtoV2(null, GapRecord(outcome: "Open", reason: "NoAnswer", support: [], conflict: "c4")).ToJsonString()).GapAssessments);
    }

    [Fact]
    public void The_reconcile_alias_2_schema_is_closed_requires_gap_assessments_and_the_alias_1_schema_is_unchanged()
    {
        using var v2 = JsonDocument.Parse(ReconcileAliasContracts.JsonSchemaV2().GetRawText());
        var root = v2.RootElement;

        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["contract", "descriptor", "noFindings", "items", "candidates", "focus", "patches", "gapAssessments"],
            root.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal("reconcile-alias-2", root.GetProperty("properties").GetProperty("contract").GetProperty("enum")[0].GetString());
        var gap = root.GetProperty("properties").GetProperty("gapAssessments").GetProperty("items");
        Assert.False(gap.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["gap", "outcome", "reason", "rationale", "supportingEvidence", "openItem", "openConflict"],
            gap.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.DoesNotContain("uuid", root.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var v1 = ReconcileAliasContracts.JsonSchema().GetRawText();
        Assert.DoesNotContain("gapAssessments", v1);
        Assert.DoesNotContain("reconcile-alias-2", v1);
        Assert.True(PinnedReconcileSchemaHash == CanonicalJson.HashText(CanonicalJson.Canonicalize(v1)), CanonicalJson.HashText(CanonicalJson.Canonicalize(v1)));
    }

    private static AnalysisAliasCatalog GapRunCatalog(string contract) =>
        AnalysisAliasCatalog.Build(AliasFixtures.Input(
            relations: [AliasFixtures.Relation(3, 5, EvidenceRelationType.Contradicts)],
            evidence: [.. AliasFixtures.Evidences().Select((e, i) => i is 1 or 4 ? e with { Kind = EvidenceKind.Gap } : e)]), contract);

    [Fact]
    public void The_reconcile_alias_2_catalog_keeps_its_shape_with_a_new_contract_version_and_a_different_stable_hash()
    {
        var request = LeafA(2, 3, 5);
        var v1 = Catalog(request);
        var v2 = ReconcileAliasCatalog.Build(RunCatalog, request, [], ReconcileAliasContracts.SchemaVersionV2);

        Assert.Equal("reconcile-alias-catalog-1", v2.Data.CatalogVersion);
        Assert.Equal("reconcile-alias-1", v1.Data.ContractVersion);
        Assert.Equal("reconcile-alias-2", v2.Data.ContractVersion);
        Assert.NotEqual(v1.CatalogHash, v2.CatalogHash);
        Assert.Equal(v2.CatalogHash, ReconcileAliasCatalog.Build(RunCatalog, request, [], ReconcileAliasContracts.SchemaVersionV2).CatalogHash);
        Assert.Equal(v1.Data.Entries, v2.Data.Entries);
        Assert.Equal(v1.Data.Nodes, v2.Data.Nodes);
        Assert.Equal(v1.Data.Relations, v2.Data.Relations);
        Assert.Equal(v1.Data.BatchId, v2.Data.BatchId);
        Assert.Throws<ArgumentException>(() => ReconcileAliasCatalog.Build(RunCatalog, request, [], "reconcile-alias-3"));
        Assert.Throws<ArgumentException>(() => ReconcileAliasCatalog.Build(RunCatalog, request, [], "alias-2"));
    }

    [Fact]
    public void The_assigned_input_of_a_batch_is_the_batch_entries_with_their_kinds_taken_from_the_run_catalog()
    {
        var run = GapRunCatalog(AnalysisAliasContracts.SchemaVersionV2);
        var batch = ReconcileAliasCatalog.Build(run, LeafA(2, 3), [], ReconcileAliasContracts.SchemaVersionV2);

        var assigned = GapAssessmentRules.Assigned(run, batch);

        Assert.Equal(2, assigned.Count);                                // E5 è Gap nel run ma non è assegnato a questo batch
        Assert.Equal(EvidenceKind.Gap, assigned["E2"]);
        Assert.Equal(EvidenceKind.Requirement, assigned["E3"]);
        Assert.False(assigned.ContainsKey("E5"));

        Assert.Empty(GapAssessmentRules.Check([GapAssessment("E2", "E3")], assigned, [], []));
        Assert.NotEmpty(GapAssessmentRules.Check([GapAssessment("E2", "E3"), GapAssessment("E5", "E3")], assigned, [], []));   // E5 fuori dal batch
        Assert.NotEmpty(GapAssessmentRules.Check([GapAssessment("E2", "E5")], assigned, [], []));                              // riscontro fuori dal batch
        Assert.NotEmpty(GapAssessmentRules.Check([], assigned, [], []));                                                       // Gap assegnato senza record
    }

    private static AiGapAssessment GapAssessment(string gap, string support) => new()
    {
        Gap = gap, Outcome = GapOutcome.AnsweredInSnapshot, Reason = GapReason.Answered, Rationale = "Risponde.", SupportingEvidence = [support], OpenItem = null, OpenConflict = null,
    };

    [Fact]
    public void The_reconcile_alias_1_vectors_hashes_catalog_and_decoder_are_unchanged()
    {
        Assert.Equal("reconcile-alias-1", ReconcileAliasContracts.SchemaVersion);
        Assert.Equal("document-reconcile-v3", ReconcileAliasContracts.PromptVersion);
        Assert.Equal("reconcile-alias-catalog-1", ReconcileAliasContracts.CatalogVersion);
        var dto = ReconcileAliasContracts.Decode(Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Documented", "E2"))).ToJsonString());
        Assert.True(PinnedReconcileDtoHash == ReconcileAliasContracts.Hash(dto), ReconcileAliasContracts.Hash(dto));
        var catalogHash = Catalog(LeafA()).CatalogHash;
        Assert.True(PinnedReconcileCatalogHash == catalogHash, catalogHash);
        Assert.Equal(catalogHash, ReconcileAliasCatalog.Build(RunCatalog, LeafA(), [], ReconcileAliasContracts.SchemaVersion).CatalogHash);
    }

    // Valori calcolati sul codice di HEAD 240fa18 (prima di T135).
    private const string PinnedReconcileDtoHash = "a8bd564c4802da685adcd2a5db15e46edf2e1139656aefd47d25819bd1fabde2";
    private const string PinnedReconcileCatalogHash = "8b58bab0ae1c662d3aff58ac13a90989ddee989843b5a38d622808077808a9c3";
    private const string PinnedReconcileSchemaHash = "648915d419475bb78220e92aecebe0fd398c47a2032f2cee6e031d7be1535c54";
    /// <summary>
    /// Riproduzione OFFLINE del rifiuto osservato nel Run 2 di T040 (chiamata 026: foglia, vista B, 139 items) con l'output grezzo ARCHIVIATO in locale (<c>%TEMP%projects-diag</c>): nessuna inferenza.
    /// Senza archivio il test è saltato esplicitamente (mai superato). Il controllo sulla foglia A dimostra che la violazione dipende dallo stadio/vista e non dal contenuto.
    /// </summary>
    [Fact]
    public void The_archived_run2_output_of_a_leaf_B_batch_with_items_is_rejected_by_the_validator_and_would_not_be_by_the_stage_rule_in_leaf_A()
    {
        var root = Environment.GetEnvironmentVariable("BPROJECTS_DIAG_DIR") is { Length: > 0 } custom ? custom : Path.Combine(Path.GetTempPath(), "bprojects-diag");
        string? assistant = null;
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.GetDirectories(root, "*RetrievalGapStageMafRealTests*").OrderByDescending(d => d, StringComparer.Ordinal))
            {
                var hit = AnalysisCallReport.Build(dir).FirstOrDefault(c => c!["itemsOutsideLeafA"]?.GetValue<bool>() == true && c["batchStage"]?.ToString() == "leaf" && c["batchView"]?.ToString() == "B");
                if (hit is not null)
                {
                    assistant = File.ReadAllText(Path.Combine(dir, hit["rawAssistantFile"]!.ToString()));
                    break;
                }
            }
        }

        Assert.SkipWhen(assistant is null, "Archivio delle chiamate del Run 2 non presente su questa macchina: riproduzione non eseguibile.");

        var rejected = Compose(assistant!, Request("leaf", ReconcileView.B, [1, 2]));

        Assert.False(rejected.IsValid);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, rejected.ErrorCode);
        Assert.Contains("items: ammessi solo nelle foglie della vista A (stadio leaf, vista B); le altre viste e gli altri stadi non propongono item.", rejected.Violations);   // la violazione osservata nel run

        var asLeafA = Compose(assistant!, LeafA(1, 2));
        Assert.DoesNotContain(asLeafA.Violations, v => v.StartsWith("items: ammessi solo", StringComparison.Ordinal));   // in A la regola di stadio non scatta (restano alias fuori catalogo)
    }
}
