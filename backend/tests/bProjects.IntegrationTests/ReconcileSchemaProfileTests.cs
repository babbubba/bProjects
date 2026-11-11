using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// Profilo di schema per batch (<c>reconcile-alias-schema-by-batch-1</c>, workflow v8) — deterministici, nessun database, nessuna rete. Lo schema per stadio/vista è solo un aiuto alla grammatica del provider:
/// il compositore resta autorevole. I test dimostrano che le varianti sono coerenti con il compositore (mai più permissive né più restrittive sui tre vincoli) e che l'output archiviato della chiamata 026
/// del Run 2 di T040 (foglia, vista B, 139 items, 0 candidati) violerebbe la variante NoItems.
/// </summary>
public sealed class ReconcileSchemaProfileTests
{
    private static readonly string BatchId = new('b', 64);
    private static readonly string InputHash = new('c', 64);

    private static readonly DocumentAnalysisInput Input = AliasFixtures.Input(relations: [AliasFixtures.Relation(3, 5, EvidenceRelationType.Contradicts)]);
    private static readonly AnalysisAliasCatalog RunCatalog = AnalysisAliasCatalog.Build(Input);

    private const string PinnedReconcileSchemaHash = "648915d419475bb78220e92aecebe0fd398c47a2032f2cee6e031d7be1535c54";

    // ================================================================ fixture minime (equivalenti a quelle di ReconcileAliasComposerTests)

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

    private static ReconcileAliasComposition Compose(string json, ReconcileBatchRequest request) =>
        ReconcileAliasComposer.Compose(json, ReconcileAliasCatalog.Build(RunCatalog, request, []), request, Input.Snapshot, null, null);

    private static JsonObject Dto(Action<JsonObject>? change = null)
    {
        var o = new JsonObject
        {
            ["contract"] = "reconcile-alias-1", ["descriptor"] = "gruppo", ["noFindings"] = "nessun collegamento emerso", ["items"] = new JsonArray(), ["candidates"] = new JsonArray(), ["focus"] = null,
            ["patches"] = new JsonArray(),
        };
        change?.Invoke(o);
        return o;
    }

    private static JsonNode Item(string key, string evidence = "E1") =>
        JsonNode.Parse(JsonSerializer.Serialize(new { key, section = "requirements", text = "testo " + key, classification = "Declared", evidence = new[] { evidence } }))!;

    private static JsonNode Cand(string key, string left, string right, string type = "Alias") =>
        JsonNode.Parse(JsonSerializer.Serialize(new { key, left, right, type, basis = "Inferred", rationale = "numeri diversi" }))!;

    private static JsonNode Patch() => JsonNode.Parse("""{"key":"p1","op":"AddSupport","target":{"item":"i1","relation":null},"evidence":["E1"],"value":{}}""")!;

    private static ReconcileCandidate FocusRelation(int a, int b)
    {
        var (l, r) = (ReconciliationFixtures.Endpoint(Input.Evidence[a - 1].EvidenceId, Input.Evidence[a - 1].ContentHash), ReconciliationFixtures.Endpoint(Input.Evidence[b - 1].EvidenceId, Input.Evidence[b - 1].ContentHash));
        return new()
        {
            CandidateId = ReconciliationIds.CandidateId(CandidateKind.Conflict, l, r, ReconcileProposedType.Contradicts, Input.Snapshot.SourceHash), Kind = CandidateKind.Conflict, Left = l, Right = r,
            ProposedType = ReconcileProposedType.Contradicts, Rationale = "shared_literal_key", Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = ["origine"], ParentCandidateId = null,
        };
    }

    private static readonly CatalogNode[] TwoNodes = [Node('1', 0), Node('2', 1)];

    /// <summary>Richiesta rappresentativa per (stadio, vista), con estremi coerenti con lo stadio.</summary>
    private static ReconcileBatchRequest RequestFor(string stage, ReconcileView view) => stage switch
    {
        "leaf" => Request("leaf", view, [1, 2]),
        "reduce" or "cross" => Request(stage, view, [], TwoNodes, null, level: 1),
        _ => Request(stage, view, [1, 2], null, [FocusRelation(1, 2)]),
    };

    /// <summary>
    /// Verifica minimale di un output rispetto a una variante: controlla esattamente i tre vincoli che il profilo introduce (items.maxItems, patches.maxItems, focus type null).
    /// Nessuna libreria di JSON Schema è referenziata dal progetto di test.
    /// </summary>
    private static List<string> SchemaViolations(JsonElement schema, string json)
    {
        var violations = new List<string>();
        var dto = JsonNode.Parse(json)!.AsObject();
        var properties = JsonNode.Parse(schema.GetRawText())!["properties"]!.AsObject();
        foreach (var name in new[] { "items", "patches" })
        {
            if (properties[name]!["maxItems"] is { } max && (dto[name] as JsonArray)!.Count > max.GetValue<int>())
            {
                violations.Add($"{name}: {(dto[name] as JsonArray)!.Count} elementi > maxItems {max.GetValue<int>()}");
            }
        }

        if (properties["focus"]!["type"] is JsonValue focusType && focusType.TryGetValue<string>(out var typeName) && typeName == "null" && dto["focus"] is not null)
        {
            violations.Add("focus: deve essere null");
        }

        return violations;
    }

    private static string Raw(ReconcileSchemaVariant v) => ReconcileAliasContracts.SchemaOf(v).GetRawText();

    private static JsonObject Parsed(ReconcileSchemaVariant v) => JsonNode.Parse(Raw(v))!.AsObject();

    private static string[] PropertyNames(JsonObject schema) => [.. schema["properties"]!.AsObject().Select(p => p.Key)];

    private static string Text(JsonNode? n) => n!.ToJsonString();

    // ================================================================ 1. tabella delle varianti

    [Theory]
    [InlineData("leaf", ReconcileView.A, ReconcileSchemaVariant.Full)]
    [InlineData("leaf", ReconcileView.B, ReconcileSchemaVariant.NoItems)]
    [InlineData("reduce", ReconcileView.A, ReconcileSchemaVariant.NoItems)]
    [InlineData("reduce", ReconcileView.B, ReconcileSchemaVariant.NoItems)]
    [InlineData("cross", ReconcileView.A, ReconcileSchemaVariant.NoItems)]
    [InlineData("cross", ReconcileView.B, ReconcileSchemaVariant.NoItems)]
    [InlineData("expand", ReconcileView.A, ReconcileSchemaVariant.Candidate)]
    [InlineData("compare", ReconcileView.A, ReconcileSchemaVariant.Candidate)]
    public void The_variant_of_each_stage_and_view_follows_the_composer_rules(string stage, ReconcileView view, ReconcileSchemaVariant expected)
    {
        Assert.Equal(expected, ReconcileAliasContracts.SchemaVariantOf(stage, view));
        Assert.Equal(Raw(expected), ReconcileAliasContracts.JsonSchemaFor(stage, view).GetRawText());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("Leaf")]
    public void An_unknown_stage_throws_and_never_falls_back_to_the_full_schema(string stage)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.SchemaVariantOf(stage, ReconcileView.A));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.JsonSchemaFor(stage, ReconcileView.B));
    }

    // ================================================================ 2. Full invariato

    [Fact]
    public void The_full_variant_is_the_pinned_schema_unchanged()
    {
        var full = Raw(ReconcileSchemaVariant.Full);

        Assert.Equal(ReconcileAliasContracts.JsonSchema().GetRawText(), full);
        var hash = CanonicalJson.HashText(CanonicalJson.Canonicalize(full));
        Assert.True(PinnedReconcileSchemaHash == hash, hash);
    }

    // ================================================================ 3. NoItems

    [Fact]
    public void The_no_items_variant_only_empties_items_and_patches_and_forces_focus_null()
    {
        var full = Parsed(ReconcileSchemaVariant.Full);
        var noItems = Parsed(ReconcileSchemaVariant.NoItems);
        var props = noItems["properties"]!.AsObject();

        Assert.Equal(0, props["items"]!["maxItems"]!.GetValue<int>());
        Assert.NotNull(props["items"]!["items"]);                                   // lo schema degli elementi resta accanto a maxItems
        Assert.Equal(Text(full["properties"]!["items"]!["items"]), Text(props["items"]!["items"]));
        Assert.Equal(0, props["patches"]!["maxItems"]!.GetValue<int>());
        Assert.Equal("""{"type":"null"}""", Text(props["focus"]));
        Assert.Equal(Text(full["required"]), Text(noItems["required"]));
        Assert.Equal(PropertyNames(full), PropertyNames(noItems));
        Assert.False(noItems["additionalProperties"]!.GetValue<bool>());
        foreach (var name in new[] { "contract", "descriptor", "noFindings", "candidates" })
        {
            Assert.Equal(Text(full["properties"]![name]), Text(props[name]));
        }

        // Tutto il resto è identico: ripristinati i tre vincoli si ottiene esattamente lo schema completo.
        props["items"]!.AsObject().Remove("maxItems");
        props["patches"]!.AsObject().Remove("maxItems");
        props["focus"] = full["properties"]!["focus"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(full, noItems));
    }

    // ================================================================ 4. Candidate

    [Fact]
    public void The_candidate_variant_only_empties_items()
    {
        var full = Parsed(ReconcileSchemaVariant.Full);
        var candidate = Parsed(ReconcileSchemaVariant.Candidate);
        var props = candidate["properties"]!.AsObject();

        Assert.Equal(0, props["items"]!["maxItems"]!.GetValue<int>());
        Assert.NotNull(props["items"]!["items"]);
        Assert.Equal(Text(full["properties"]!["patches"]), Text(props["patches"]));
        Assert.Equal(Text(full["properties"]!["focus"]), Text(props["focus"]));
        Assert.Equal(PropertyNames(full), PropertyNames(candidate));
        Assert.Equal(Text(full["required"]), Text(candidate["required"]));

        props["items"]!.AsObject().Remove("maxItems");
        Assert.True(JsonNode.DeepEquals(full, candidate));
    }

    // ================================================================ 5. determinismo

    [Fact]
    public void The_variants_and_their_profile_hashes_are_deterministic_and_distinct()
    {
        foreach (var v in Enum.GetValues<ReconcileSchemaVariant>())
        {
            Assert.Equal(Raw(v), Raw(v));
        }

        var first = ReconcileAliasContracts.SchemaProfileHashes();
        Assert.Equal(first, ReconcileAliasContracts.SchemaProfileHashes());
        Assert.Matches("^Full=[0-9a-f]{64},NoItems=[0-9a-f]{64},Candidate=[0-9a-f]{64}$", first);
        var hashes = first.Split(',').Select(p => p.Split('=')[1]).ToArray();
        Assert.Equal(3, hashes.Distinct().Count());
        Assert.Equal(PinnedReconcileSchemaHash, hashes[0]);
        Assert.Equal("reconcile-alias-schema-by-batch-1", ReconcileAliasContracts.SchemaProfileByBatch);
    }

    // ================================================================ 6. schema ⇔ compositore

    [Theory]
    [InlineData("leaf", ReconcileView.B)]
    [InlineData("reduce", ReconcileView.A)]
    [InlineData("cross", ReconcileView.A)]
    [InlineData("expand", ReconcileView.A)]
    [InlineData("compare", ReconcileView.A)]
    public void Non_empty_items_are_rejected_by_the_variant_and_by_the_composer(string stage, ReconcileView view)
    {
        var json = Dto(o => o["items"] = new JsonArray(Item("i1"))).ToJsonString();

        var schemaViolations = SchemaViolations(ReconcileAliasContracts.JsonSchemaFor(stage, view), json);
        var composed = Compose(json, RequestFor(stage, view));

        Assert.Contains(schemaViolations, v => v.StartsWith("items", StringComparison.Ordinal));
        Assert.False(composed.IsValid);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, composed.ErrorCode);
        Assert.Contains("items", string.Join(" | ", composed.Violations));
    }

    [Theory]
    [InlineData("leaf", ReconcileView.B)]
    [InlineData("reduce", ReconcileView.A)]
    [InlineData("cross", ReconcileView.A)]
    public void Non_empty_patches_are_rejected_by_the_variant_and_by_the_composer_where_patches_are_not_allowed(string stage, ReconcileView view)
    {
        var json = Dto(o => o["patches"] = new JsonArray(Patch())).ToJsonString();

        Assert.Contains(SchemaViolations(ReconcileAliasContracts.JsonSchemaFor(stage, view), json), v => v.StartsWith("patches", StringComparison.Ordinal));
        var composed = Compose(json, RequestFor(stage, view));
        Assert.False(composed.IsValid);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, composed.ErrorCode);
        Assert.Contains("patches", string.Join(" | ", composed.Violations));
    }

    [Theory]
    [InlineData("leaf", ReconcileView.A)]
    [InlineData("leaf", ReconcileView.B)]
    [InlineData("reduce", ReconcileView.A)]
    [InlineData("cross", ReconcileView.A)]
    public void A_non_null_focus_outside_expand_and_compare_is_rejected_by_the_composer_and_by_the_variant_where_the_profile_constrains_it(string stage, ReconcileView view)
    {
        var json = Dto(o => o["focus"] = JsonNode.Parse("""{"outcome":"Compared","reason":null}""")).ToJsonString();
        var composed = Compose(json, RequestFor(stage, view));

        Assert.False(composed.IsValid);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, composed.ErrorCode);
        Assert.Contains("focus", string.Join(" | ", composed.Violations));
        // Nella foglia A lo schema resta quello completo (focus nullable): è il compositore a rifiutare; altrove la grammatica lo vieta già.
        var expectedSchemaViolation = ReconcileAliasContracts.SchemaVariantOf(stage, view) == ReconcileSchemaVariant.NoItems;
        Assert.Equal(expectedSchemaViolation, SchemaViolations(ReconcileAliasContracts.JsonSchemaFor(stage, view), json).Any(v => v.StartsWith("focus", StringComparison.Ordinal)));
    }

    [Fact]
    public void Leaf_A_with_valid_items_is_allowed_by_the_full_variant_and_composed()
    {
        var json = Dto(o => o["items"] = new JsonArray(Item("i1", "E1"), Item("i2", "E1"))).ToJsonString();

        Assert.Empty(SchemaViolations(ReconcileAliasContracts.JsonSchemaFor("leaf", ReconcileView.A), json));
        var composed = Compose(json, Request("leaf", ReconcileView.A, [1, 2]));
        Assert.True(composed.IsValid, string.Join(" | ", composed.Violations));
        Assert.Equal(2, composed.Result!.ItemProposals.Count);
    }

    [Fact]
    public void Leaf_B_with_empty_items_and_a_valid_candidate_is_allowed_by_no_items_and_composed_without_loss()
    {
        var json = Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E1", "E2"), Cand("f2", "E2", "E3", "Contradicts"))).ToJsonString();

        Assert.Empty(SchemaViolations(ReconcileAliasContracts.JsonSchemaFor("leaf", ReconcileView.B), json));
        var composed = Compose(json, Request("leaf", ReconcileView.B, [1, 2, 3]));

        Assert.True(composed.IsValid, string.Join(" | ", composed.Violations));
        Assert.Equal(2, composed.Result!.Candidates.Count);
        Assert.Empty(composed.Result.ItemProposals);
        Assert.Equal([BatchId, BatchId], composed.Result.Candidates.SelectMany(c => c.OriginBatchIds));
        Assert.Equal(3, composed.Result.VisitedEntryIds.Count);                       // nessuna evidenza persa: il batch le visita tutte
    }

    // ================================================================ 7. input malformati o semanticamente errati

    [Fact]
    public void Malformed_json_and_unknown_aliases_are_work_output_invalid_even_under_a_restrictive_variant()
    {
        var request = Request("leaf", ReconcileView.B, [1, 2]);

        var malformed = Compose("{\"contract\":", request);
        var empty = Compose("", request);
        var unknownAlias = Compose(Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E1", "E99"))).ToJsonString(), request);
        var outOfBatch = Compose(Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E1", "E3"))).ToJsonString(), request);

        foreach (var rejected in new[] { malformed, empty, unknownAlias, outOfBatch })
        {
            Assert.False(rejected.IsValid);
            Assert.Equal(DocumentWorkException.WorkOutputInvalid, rejected.ErrorCode);
        }
    }

    // ================================================================ 8. regressione sulla chiamata 026 archiviata

    [Fact]
    public void The_archived_call_026_violates_the_no_items_variant_and_is_still_rejected_by_the_validator()
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

        // (d) la foglia B usa la variante NoItems.
        Assert.Equal(ReconcileSchemaVariant.NoItems, ReconcileAliasContracts.SchemaVariantOf("leaf", ReconcileView.B));

        // (a) il validatore è invariato: rifiuta ancora l'output.
        var composed = Compose(assistant!, Request("leaf", ReconcileView.B, [1, 2]));
        Assert.False(composed.IsValid);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, composed.ErrorCode);
        Assert.Contains(composed.Violations, v => v.StartsWith("items: ammessi solo", StringComparison.Ordinal));

        // (b) lo stesso output viola la variante NoItems per il numero di items.
        var violations = SchemaViolations(ReconcileAliasContracts.JsonSchemaFor("leaf", ReconcileView.B), assistant!);
        Assert.Contains(violations, v => v.StartsWith("items:", StringComparison.Ordinal));
        var dto = JsonNode.Parse(assistant!)!.AsObject();
        Assert.True(((JsonArray)dto["items"]!).Count > 0);

        // (c) non sposterebbe la violazione altrove: 0 candidati, 0 patch, focus null.
        Assert.Empty((JsonArray)dto["candidates"]!);
        Assert.Empty((JsonArray)dto["patches"]!);
        Assert.Null(dto["focus"]);
        Assert.All(violations, v => Assert.StartsWith("items:", v, StringComparison.Ordinal));
    }
}
