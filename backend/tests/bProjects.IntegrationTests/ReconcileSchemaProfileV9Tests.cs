using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// Profilo di schema per batch <c>reconcile-alias-schema-by-batch-2</c> (definizione v9; 003, T040 dopo il Run 3, chiamata 026: <c>descriptor: null</c> ammesso dallo schema v8 e vietato dal validatore).
/// Deterministici, nessun database, nessuna rete. Dimostrano, per tutte le combinazioni di stadio e vista, che lo schema esprime i vincoli strutturali del contratto (livelli 1 e 2) senza mai essere più
/// restrittivo del validatore; che il validatore, invariato, resta l'unica garanzia e rifiuta anche le discrepanze rinviate (livello 3, D9–D12); che l'output archiviato della chiamata 026 del Run 3 è
/// rifiutato dalla grammatica v9 e i suoi cinque candidati sono conservati quando il descriptor è valorizzato.
/// </summary>
public sealed class ReconcileSchemaProfileV9Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string BatchId = new('b', 64);
    private static readonly string InputHash = new('c', 64);

    // Le cinque evidenze delle fixture alias più una sesta, per riprodurre i sei estremi distinti della chiamata 026.
    private static readonly DocumentAnalysisInput Input = AliasFixtures.Input(
        evidence: [.. AliasFixtures.Evidences(), AliasFixtures.Ev(6, EvidenceOriginKind.Document, "La reperibilità notturna vale solo per Padova.", AliasFixtures.Doc(AliasFixtures.G(308), AliasFixtures.G3, 0, 4, 7, 20, "Reperibilità notturna solo a Padova"))],
        relations: [AliasFixtures.Relation(3, 5, EvidenceRelationType.Contradicts)]);

    private static readonly AnalysisAliasCatalog RunCatalog = AnalysisAliasCatalog.Build(Input);

    // ================================================================ fixture

    private static CatalogEntry Entry(int n)
    {
        var e = Input.Evidence[n - 1];
        return new()
        {
            EntryId = ReconciliationIds.EntryId(Input.Snapshot.SourceHash, e.EvidenceId, e.ContentHash), EvidenceId = e.EvidenceId, EvidenceVersionId = e.VersionId, ContentHash = e.ContentHash,
            SourceIds = [.. e.Sources.Select(s => s.SourceId).Distinct()], Keys = [], KnownRelationIds = [], UnclassifiedViews = [],
        };
    }

    private static CatalogNode Node(char c, int ordinal) => new()
    {
        NodeId = new string(c, 64), View = ReconcileView.A, Level = 0, Ordinal = ordinal, ChildNodeIds = [], MemberSetHash = new string((char)('a' + (c - '0')), 64),
        MemberCount = 2, KeyPageHashes = [], RelationPageHashes = [], Descriptor = $"descriptor {c}", DescriptorOutputHash = new string('d', 64),
    };

    private static readonly CatalogNode[] TwoNodes = [Node('1', 0), Node('2', 1)];

    private static ReconcileBatchRequest Request(string stage, ReconcileView view, int[] entries, CatalogNode[]? nodes = null, ReconcileCandidate[]? focus = null, int level = 0) => new(
        new DerivedBatchInput(BatchId, stage, view.ToString(), null, level, 0, [], "{}", 10, "measured", InputHash, "reconcile-alias-1", "document-reconcile-v4"),
        view, level, [.. entries.Select(Entry)], nodes ?? [], focus);

    private static ReconcileCandidate FocusRelation(int a, int b)
    {
        var (l, r) = (ReconciliationFixtures.Endpoint(Input.Evidence[a - 1].EvidenceId, Input.Evidence[a - 1].ContentHash), ReconciliationFixtures.Endpoint(Input.Evidence[b - 1].EvidenceId, Input.Evidence[b - 1].ContentHash));
        return new()
        {
            CandidateId = ReconciliationIds.CandidateId(CandidateKind.Conflict, l, r, ReconcileProposedType.Contradicts, Input.Snapshot.SourceHash), Kind = CandidateKind.Conflict, Left = l, Right = r,
            ProposedType = ReconcileProposedType.Contradicts, Rationale = "shared_literal_key", Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = ["origine"], ParentCandidateId = null,
        };
    }

    /// <summary>Richiesta rappresentativa di ogni variante: stadio e vista reali del motore (il cross è sempre nella vista Cross), estremi coerenti con lo stadio.</summary>
    private static ReconcileBatchRequest RequestOf(ReconcileBatchSchema variant) => variant switch
    {
        ReconcileBatchSchema.LeafA => Request("leaf", ReconcileView.A, [1, 2, 3, 4, 5, 6]),
        ReconcileBatchSchema.LeafB => Request("leaf", ReconcileView.B, [1, 2, 3, 4, 5, 6]),
        ReconcileBatchSchema.Reduce => Request("reduce", ReconcileView.B, [], TwoNodes, level: 1),
        ReconcileBatchSchema.Cross => Request("cross", ReconcileView.Cross, [], TwoNodes, level: 1),
        ReconcileBatchSchema.Expand => Request("expand", ReconcileView.A, [3, 5], null, [FocusRelation(3, 5)]),
        _ => Request("compare", ReconcileView.A, [3, 5], null, [FocusRelation(3, 5)]),
    };

    private static ReconcileAliasComposition Compose(string json, ReconcileBatchRequest request, IReadOnlyList<ReconcileItemRef>? items = null, ReconciliationBase? baseState = null) =>
        ReconcileAliasComposer.Compose(json, ReconcileAliasCatalog.Build(RunCatalog, request, items ?? []), request, Input.Snapshot, baseState, null);

    private static bool BuildsNode(ReconcileBatchSchema v) => v is ReconcileBatchSchema.LeafA or ReconcileBatchSchema.LeafB or ReconcileBatchSchema.Reduce;

    /// <summary>Output valido per la variante: descriptor dove il batch costruisce un nodo, focus Compared solo nel confronto, nessuna proposta (noFindings motivato).</summary>
    private static JsonObject Valid(ReconcileBatchSchema v) => new()
    {
        ["contract"] = "reconcile-alias-1", ["descriptor"] = BuildsNode(v) ? "gruppo di regole sugli ordini" : null, ["noFindings"] = "nessun collegamento emerso", ["items"] = new JsonArray(),
        ["candidates"] = new JsonArray(), ["focus"] = v == ReconcileBatchSchema.Compare ? JsonNode.Parse("""{"outcome":"Compared","reason":null}""") : null, ["patches"] = new JsonArray(),
    };

    private static JsonNode Cand(string key, string left, string right, string type = "Contradicts", string rationale = "numeri diversi") =>
        JsonNode.Parse(JsonSerializer.Serialize(new { key, left, right, type, basis = "Inferred", rationale }))!;

    private static JsonObject Mutated(ReconcileBatchSchema v, Action<JsonObject> change)
    {
        var o = Valid(v);
        change(o);
        return o;
    }

    private static JsonElement SchemaOf(ReconcileBatchSchema v) => ReconcileAliasContracts.SchemaOf(v);

    private static JsonObject Parsed(ReconcileBatchSchema v) => JsonNode.Parse(SchemaOf(v).GetRawText())!.AsObject();

    private static string Join(IEnumerable<string> lines) => string.Join(" | ", lines);

    // ================================================================ 1. tabella e dispatch

    [Theory]
    [InlineData("leaf", ReconcileView.A, ReconcileBatchSchema.LeafA)]
    [InlineData("leaf", ReconcileView.B, ReconcileBatchSchema.LeafB)]
    [InlineData("reduce", ReconcileView.A, ReconcileBatchSchema.Reduce)]
    [InlineData("reduce", ReconcileView.B, ReconcileBatchSchema.Reduce)]
    [InlineData("cross", ReconcileView.Cross, ReconcileBatchSchema.Cross)]
    [InlineData("expand", ReconcileView.A, ReconcileBatchSchema.Expand)]
    [InlineData("expand", ReconcileView.B, ReconcileBatchSchema.Expand)]
    [InlineData("expand", ReconcileView.Cross, ReconcileBatchSchema.Expand)]
    [InlineData("compare", ReconcileView.A, ReconcileBatchSchema.Compare)]
    [InlineData("compare", ReconcileView.B, ReconcileBatchSchema.Compare)]
    [InlineData("compare", ReconcileView.Cross, ReconcileBatchSchema.Compare)]
    public void Every_stage_and_view_maps_to_its_v9_variant_and_the_profile_dispatches_exactly_that_schema(string stage, ReconcileView view, ReconcileBatchSchema expected)
    {
        Assert.Equal(expected, ReconcileAliasContracts.BatchSchemaOf(stage, view));
        var (name, schema) = ReconcileAliasContracts.ProfileSchemaFor(ReconcileAliasContracts.SchemaProfileByBatchV2, stage, view);
        Assert.Equal(expected.ToString(), name);
        Assert.Equal(SchemaOf(expected).GetRawText(), schema.GetRawText());
    }

    [Fact]
    public void Unknown_stages_leaves_outside_A_and_B_and_unknown_profiles_throw_and_never_fall_back()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.BatchSchemaOf("leaf", ReconcileView.Cross));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.BatchSchemaOf("Leaf", ReconcileView.A));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.BatchSchemaOf("", ReconcileView.A));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.ProfileSchemaFor("reconcile-alias-schema-by-batch-3", "leaf", ReconcileView.A));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.ProfileVariants(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.ProfileHashes("reconcile-alias-schema-by-batch-0"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.SchemaOf((ReconcileBatchSchema)99));
    }

    [Fact]
    public void The_v8_profile_is_still_dispatched_with_its_own_variants_and_hash_string()
    {
        // Il refactoring del piano (profilo per nome) non cambia la v8: stesse varianti, stessa stringa di hash nel configHash.
        Assert.Equal(ReconcileAliasContracts.SchemaProfileHashes(), ReconcileAliasContracts.ProfileHashes(ReconcileAliasContracts.SchemaProfileByBatch));
        Assert.Equal(["Full", "NoItems", "Candidate"], ReconcileAliasContracts.ProfileVariants(ReconcileAliasContracts.SchemaProfileByBatch).Select(v => v.Name));
        var (name, schema) = ReconcileAliasContracts.ProfileSchemaFor(ReconcileAliasContracts.SchemaProfileByBatch, "leaf", ReconcileView.B);
        Assert.Equal(("NoItems", ReconcileAliasContracts.SchemaOf(ReconcileSchemaVariant.NoItems).GetRawText()), (name, schema.GetRawText()));
    }

    // ================================================================ 2. forma delle varianti

    // Golden del profilo v9: un run congelato con il profilo -2 dipende da questi byte. Ogni cambiamento richiede un nuovo profilo e una nuova definizione.
    internal const string PinnedV9ProfileHashes =
        "LeafA=17001aef9ed768714129aba3f5d4b861e8e7d36632e6eaf78c7cf60c1ee49f09,LeafB=800f277ba1daa4d14ed0694d2dbea68add81ebb8dd1915817ab1df3a6a1aa4ce,"
        + "Reduce=06058502b68f2d19363b1bb408bfd613b4ccafd3f8e6453c3c32eba43ba56b6c,Cross=bab4a2acd070c70569fb5b80c7ec251a1f9bff809f8f6e701d83dda6e659d5f9,"
        + "Expand=37e08c9aa05d07b145dbee84d0775061f9f6c8735b670265cbbff1fd874cc97b,Compare=b8c471d1b076af5e9fae32761d66966315fefc1b05f5c4c667714dcf808914a3";

    [Fact]
    public void The_v9_variants_are_six_distinct_deterministic_and_pinned_schemas()
    {
        var hashes = ReconcileAliasContracts.SchemaProfileV2Hashes();
        Assert.Equal(hashes, ReconcileAliasContracts.ProfileHashes(ReconcileAliasContracts.SchemaProfileByBatchV2));
        Assert.Matches("^LeafA=[0-9a-f]{64},LeafB=[0-9a-f]{64},Reduce=[0-9a-f]{64},Cross=[0-9a-f]{64},Expand=[0-9a-f]{64},Compare=[0-9a-f]{64}$", hashes);
        Assert.Equal(6, hashes.Split(',').Select(p => p.Split('=')[1]).Distinct().Count());
        Assert.True(PinnedV9ProfileHashes == hashes, hashes);
        Assert.Equal("reconcile-alias-schema-by-batch-2", ReconcileAliasContracts.SchemaProfileByBatchV2);
        Assert.NotEqual(ReconcileAliasContracts.SchemaProfileHashes(), hashes);
    }

    [Theory]
    [InlineData(ReconcileBatchSchema.LeafA)]
    [InlineData(ReconcileBatchSchema.LeafB)]
    [InlineData(ReconcileBatchSchema.Reduce)]
    [InlineData(ReconcileBatchSchema.Cross)]
    [InlineData(ReconcileBatchSchema.Expand)]
    [InlineData(ReconcileBatchSchema.Compare)]
    public void Each_variant_is_the_full_schema_with_exactly_the_structural_constraints_of_its_stage(ReconcileBatchSchema variant)
    {
        var full = JsonNode.Parse(ReconcileAliasContracts.JsonSchema().GetRawText())!.AsObject();
        var schema = Parsed(variant);
        var props = schema["properties"]!.AsObject();
        var nodeEndpoints = variant is ReconcileBatchSchema.Reduce or ReconcileBatchSchema.Cross;
        var candidateStage = variant is ReconcileBatchSchema.Expand or ReconcileBatchSchema.Compare;

        Assert.Equal(full["required"]!.ToJsonString(), schema["required"]!.ToJsonString());
        Assert.Equal(full["properties"]!.AsObject().Select(p => p.Key), props.Select(p => p.Key));
        Assert.Equal(BuildsNode(variant) ? """{"type":"string","minLength":1}""" : """{"type":"null"}""", props["descriptor"]!.ToJsonString());   // nessun maxLength
        Assert.Equal(variant == ReconcileBatchSchema.LeafA ? null : 0, props["items"]!["maxItems"]?.GetValue<int>());
        Assert.Equal(candidateStage ? null : 0, props["patches"]!["maxItems"]?.GetValue<int>());
        var endpoint = nodeEndpoints ? "^N[1-9][0-9]*$" : "^E[1-9][0-9]*$";
        var candidate = props["candidates"]!["items"]!["properties"]!;
        Assert.Equal((endpoint, endpoint), (candidate["left"]!["pattern"]!.GetValue<string>(), candidate["right"]!["pattern"]!.GetValue<string>()));
        var focus = variant switch
        {
            ReconcileBatchSchema.Expand => "[\"object\",\"null\"]|[\"Unresolved\"]",
            ReconcileBatchSchema.Compare => "\"object\"|[\"Compared\",\"Open\"]",
            _ => "\"null\"|",
        };
        Assert.Equal(focus, $"{props["focus"]!["type"]!.ToJsonString()}|{props["focus"]!["properties"]?["outcome"]?["enum"]?.ToJsonString()}");

        // Ripristinati i soli vincoli dello stadio si ottiene esattamente lo schema completo: nient'altro è cambiato.
        props["descriptor"] = full["properties"]!["descriptor"]!.DeepClone();
        props["items"]!.AsObject().Remove("maxItems");
        props["patches"]!.AsObject().Remove("maxItems");
        props["focus"] = full["properties"]!["focus"]!.DeepClone();
        props["candidates"] = full["properties"]!["candidates"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(full, schema));
    }

    [Fact]
    public void Every_v9_variant_uses_only_constructs_the_llama_cpp_converter_supports_and_stays_within_the_pack_reserve()
    {
        var full = ReconcileAliasContracts.JsonSchema().GetRawText().Length;
        foreach (var v in Enum.GetValues<ReconcileBatchSchema>())
        {
            var problems = ReconcileSchemaSubset.ProviderProblems(SchemaOf(v));
            Assert.True(problems.Count == 0, $"{v}: {Join(problems)}");
            // Il Pack misura con lo schema completo e la rimisura con la variante: nessuna variante può superarlo oltre la riserva.
            Assert.True(SchemaOf(v).GetRawText().Length - full <= 32, v.ToString());
        }

        // Anche lo schema completo e le varianti v8 sono nell'insieme: il test non accetta keyword che il valutatore ignorerebbe.
        Assert.Empty(ReconcileSchemaSubset.ProviderProblems(ReconcileAliasContracts.JsonSchema()));
        Assert.All(Enum.GetValues<ReconcileSchemaVariant>(), v => Assert.Empty(ReconcileSchemaSubset.ProviderProblems(ReconcileAliasContracts.SchemaOf(v))));
    }

    // ================================================================ 3. equivalenza schema ⇔ validatore sulle regole strutturali (livelli 1 e 2)

    [Theory]
    [InlineData(ReconcileBatchSchema.LeafA)]
    [InlineData(ReconcileBatchSchema.LeafB)]
    [InlineData(ReconcileBatchSchema.Reduce)]
    [InlineData(ReconcileBatchSchema.Cross)]
    [InlineData(ReconcileBatchSchema.Expand)]
    [InlineData(ReconcileBatchSchema.Compare)]
    public void A_valid_output_of_each_stage_is_admitted_by_its_variant_and_composed(ReconcileBatchSchema variant)
    {
        var json = Valid(variant).ToJsonString();
        Assert.Empty(ReconcileSchemaSubset.Violations(SchemaOf(variant), json));
        var composed = Compose(json, RequestOf(variant));
        Assert.True(composed.IsValid, Join(composed.Violations));
    }

    public static TheoryData<ReconcileBatchSchema, string> StructuralMutations()
    {
        var data = new TheoryData<ReconcileBatchSchema, string>();
        foreach (var v in Enum.GetValues<ReconcileBatchSchema>())
        {
            foreach (var m in new[] { "items", "patches", "focus-compared", "focus-open", "focus-unresolved", "focus-null", "endpoints-E", "endpoints-N" })
            {
                data.Add(v, m);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StructuralMutations))]
    public void For_every_stage_the_variant_rejects_a_structural_violation_if_and_only_if_the_validator_rejects_it(ReconcileBatchSchema variant, string mutation)
    {
        var nodeStage = variant is ReconcileBatchSchema.Reduce or ReconcileBatchSchema.Cross;
        (Action<JsonObject> Change, string Rule) m = mutation switch
        {
            "items" => (o => o["items"] = new JsonArray(JsonNode.Parse("""{"key":"i1","section":"requirements","text":"Il cliente ordina via portale.","classification":"Documented","evidence":["E2"]}""")), "items: ammessi solo"),
            "patches" => (o => o["patches"] = new JsonArray(JsonNode.Parse("""{"key":"p1","op":"AddSupport","target":{"item":null,"relation":"R1"},"evidence":["E3"],"value":{}}""")), "patches: ammesse solo"),
            "focus-compared" => (o => o["focus"] = JsonNode.Parse("""{"outcome":"Compared","reason":null}"""), "focus"),
            "focus-open" => (o => o["focus"] = JsonNode.Parse("""{"outcome":"Open","reason":"limiti diversi senza un criterio di prevalenza"}"""), "focus"),
            "focus-unresolved" => (o => o["focus"] = JsonNode.Parse("""{"outcome":"Unresolved","reason":null}"""), "focus"),
            "focus-null" => (o => o["focus"] = null, "focus"),
            "endpoints-E" => (o => o["candidates"] = new JsonArray(Cand("f1", "E3", "E5")), "non è ammesso nello stadio"),
            _ => (o => o["candidates"] = new JsonArray(Cand("f1", "N1", "N2")), "non è ammesso nello stadio"),
        };
        var json = Mutated(variant, m.Change).ToJsonString();

        var schemaViolations = ReconcileSchemaSubset.Violations(SchemaOf(variant), json);
        var composed = Compose(json, RequestOf(variant));
        var validatorRejectsTheRule = composed.Violations.Any(v => v.Contains(m.Rule, StringComparison.Ordinal));

        Assert.True(
            schemaViolations.Count > 0 == validatorRejectsTheRule,
            $"{variant}/{mutation}: schema [{Join(schemaViolations)}] validatore [{Join(composed.Violations)}]");
        if (schemaViolations.Count == 0 && mutation != "patches")
        {
            // Ammesso dallo schema ⇒ composto (le patch restano un rifiuto del solo validatore senza la base del passaggio, che il test non fornisce).
            Assert.True(composed.IsValid, $"{variant}/{mutation}: {Join(composed.Violations)}");
        }

        // Plausibilità della matrice: gli estremi del tipo sbagliato sono sempre rifiutati.
        if (mutation == (nodeStage ? "endpoints-E" : "endpoints-N"))
        {
            Assert.NotEmpty(schemaViolations);
        }
    }

    // ================================================================ 4. descriptor: equivalenza sul vero validatore della gerarchia

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static Guid Id(int n) => Guid.Parse($"{n:x8}-0000-4000-8000-000000000000");

    private static ReconciliationSnapshot HierarchySnapshot(int count) => new(
        ReconciliationFixtures.Project, ReconciliationFixtures.SourceHash,
        [.. Enumerable.Range(1, count).Select(n => new SnapshotEvidence(Id(n), Guid.Parse($"{n:x8}-0000-4000-8000-0000000000aa"), Sha($"content-{n}"), [ReconciliationFixtures.Source(Guid.Parse($"{n:x8}-0000-4000-8000-0000000000bb"))]))],
        []);

    private static readonly ReconciliationMeasurers Measurers = new(
        (members, _) => new ValueTask<UnitMeasure>(new UnitMeasure(10 + 5 * members.Count, TokenCountQuality.Measured, Sha("leaf|" + string.Join(',', members.Select(m => m.EntryId))), members.Count)),
        (members, _) => new ValueTask<UnitMeasure>(new UnitMeasure(10, TokenCountQuality.Measured, Sha("page|" + members.Count), members.Count)));

    private static readonly PackMeasurer<CatalogNode> Descriptors = (members, _) =>
        new ValueTask<UnitMeasure>(new UnitMeasure(10 + 5 * members.Count, TokenCountQuality.Measured, Sha("nodes|" + string.Join(',', members.Select(m => m.NodeId))), members.Count));

    /// <summary>
    /// Gerarchia reale (foglie A e B, riduzioni, cross) con un modello finto che restituisce il canonico valido, salvo il descriptor del primo batch di (stadio, vista) indicato. Restituisce il messaggio
    /// del rifiuto del validatore, o null se la gerarchia è stata costruita.
    /// </summary>
    private static async Task<string?> HierarchyRejection(string stage, ReconcileView view, string? descriptor)
    {
        var snapshot = HierarchySnapshot(16);
        var facts = Enumerable.Range(1, 16).ToDictionary(n => Id(n), n => new DirectoryFacts(new EvidenceStructure($"s{n:D3}", $"a{n:D3}", null, null, null, null, null, null), []));
        var context = new ReconciliationContext("owner", "manifest", "project-analysis-documents-v9", new PlannerLimits(20, 1_000_000));
        var directory = await ReconciliationDirectory.BuildAsync(snapshot, facts, context, Measurers, Ct);
        var touched = false;
        ValueTask<string> Invoke(ReconcileBatchRequest request, CancellationToken ct)
        {
            var target = !touched && request.Batch.Stage == stage && request.View == view;
            touched |= target;
            var result = new ReconcileResult
            {
                BatchId = request.Batch.BatchId, InputHash = request.Batch.InputHash, View = request.View, Level = request.Level,
                VisitedEntryIds = [.. request.Entries.Select(e => e.EntryId).Order(StringComparer.Ordinal)], VisitedNodeIds = [.. request.Nodes.Select(n => n.NodeId).Order(StringComparer.Ordinal)],
                Candidates = [], Dispositions = [], ItemProposals = [], Patches = [],
                Descriptor = target ? descriptor : request.View == ReconcileView.Cross ? null : "gruppo", NoFindingsReason = "nessun collegamento emerso dalla visita",
            };
            return new(ReconciliationCodec.Encode(result));
        }

        try
        {
            await ReconciliationHierarchy.BuildAsync(snapshot, directory, context, Descriptors, Invoke, Ct);
            Assert.True(touched, $"nessun batch {stage}/{view} nella gerarchia di prova");
            return null;
        }
        catch (DocumentWorkException e) when (e.Code == DocumentWorkException.WorkOutputInvalid)
        {
            Assert.True(touched);
            return e.Message;
        }
    }

    private static readonly string LongDescriptor = new('x', 513);

    [Theory]
    // Batch che costruiscono nodi: null è rifiutato dalla grammatica v9 e dal validatore (la 026 del Run 3).
    [InlineData("leaf", ReconcileView.A, "null", true, true)]
    [InlineData("leaf", ReconcileView.B, "null", true, true)]
    [InlineData("reduce", ReconcileView.A, "null", true, true)]
    [InlineData("reduce", ReconcileView.B, "null", true, true)]
    [InlineData("leaf", ReconcileView.B, "empty", true, true)]          // "": minLength 1
    [InlineData("leaf", ReconcileView.B, "ok", false, false)]
    // Residui dichiarati: solo spazi e oltre 512 caratteri restano al validatore (nessun \S e nessun maxLength nella grammatica: niente troncamenti).
    [InlineData("leaf", ReconcileView.B, "blank", false, true)]
    [InlineData("reduce", ReconcileView.A, "long", false, true)]
    // Cross: non costruisce nodi, il descriptor è null; una stringa è rifiutata da entrambi.
    [InlineData("cross", ReconcileView.Cross, "ok", true, true)]
    [InlineData("cross", ReconcileView.Cross, "null", false, false)]
    public async Task The_descriptor_rule_of_the_hierarchy_validator_is_mirrored_by_the_v9_grammar_except_the_declared_residuals(
        string stage, ReconcileView view, string kind, bool schemaRejects, bool validatorRejects)
    {
        var descriptor = kind switch { "null" => null, "empty" => "", "blank" => "   ", "long" => LongDescriptor, _ => "gruppo di regole" };
        var variant = ReconcileAliasContracts.BatchSchemaOf(stage, view);
        var json = Mutated(variant, o => o["descriptor"] = descriptor).ToJsonString();

        var schemaViolations = ReconcileSchemaSubset.Violations(SchemaOf(variant), json);
        var rejection = await HierarchyRejection(stage, view, descriptor);

        Assert.Equal(schemaRejects, schemaViolations.Count > 0);
        Assert.True(schemaViolations.All(v => v.StartsWith("$.descriptor", StringComparison.Ordinal)), Join(schemaViolations));
        Assert.Equal(validatorRejects, rejection is not null);
        Assert.True(!schemaRejects || validatorRejects, "lo schema non è mai più restrittivo del validatore");
        if (rejection is not null)
        {
            Assert.Contains("descriptor", rejection, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Expand_and_compare_are_validated_by_the_same_engine_call_as_batches_that_never_build_nodes()
    {
        // Espansione e confronto passano dallo stesso InvokeAsync con buildsNode=false (descriptor obbligatoriamente null), come il cross; la variante v9 lo dichiara con type null.
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "bProjects.App", "AI", "ReconciliationCandidates.cs"));
        Assert.Contains("ReconciliationHierarchy.InvokeAsync(derived, view, 0, batch.Members, [], snapshot, catalog, invoke, false, [candidate], ct)", source, StringComparison.Ordinal);
        Assert.All(new[] { ReconcileBatchSchema.Expand, ReconcileBatchSchema.Compare }, v =>
            Assert.NotEmpty(ReconcileSchemaSubset.Violations(SchemaOf(v), Mutated(v, o => o["descriptor"] = "gruppo").ToJsonString())));
    }

    // ================================================================ 5. livello 3 (D9–D12): rinviato, il validatore continua a rifiutare

    private static readonly List<ItemProposal> Proposals =
    [
        new() { LocalKey = "leaf-A0.i1", Text = "limite 5", Classification = DocumentAnalysisClassification.Documented, EvidenceIds = [Input.Evidence[2].EvidenceId], SourceIds = [AliasFixtures.Sx] },
    ];

    [Theory]
    [InlineData("D9", """{"key":"p1","op":"AddSupport","target":{"item":"i1","relation":null},"evidence":["E3"],"value":{"from":"E3","to":"E5","type":"Confirms","basis":"Inferred","rationale":null}}""", "JSON non conforme a reconcile-alias-1")]   // il codec strict lega la forma di value a op
    [InlineData("D9", """{"key":"p1","op":"AddSupport","target":{"item":"i1","relation":"R1"},"evidence":["E3"],"value":{}}""", "esattamente uno fra item e relation")]
    [InlineData("D9", """{"key":"p1","op":"AddSupport","target":{"item":"i1","relation":null},"evidence":[],"value":{}}""", "obbligatoria per AddSupport")]
    [InlineData("D9", """{"key":"p1","op":"AddRelation","target":{"item":null,"relation":"R1"},"evidence":[],"value":{"from":"E3","to":"E5","type":"Confirms","basis":"Inferred","rationale":null}}""", "AddRelation ancora un item")]
    public void Level_3_patch_coherence_is_admitted_by_the_v9_grammar_and_still_rejected_by_the_validator(string discrepancy, string patch, string fragment)
    {
        var json = Mutated(ReconcileBatchSchema.Compare, o => o["patches"] = new JsonArray(JsonNode.Parse(patch))).ToJsonString();

        Assert.Empty(ReconcileSchemaSubset.Violations(SchemaOf(ReconcileBatchSchema.Compare), json));   // rinviato: la grammatica non lo esprime
        var composed = Compose(json, RequestOf(ReconcileBatchSchema.Compare), [new("leaf-A0.i1", "limite 5", [Input.Evidence[2].EvidenceId])], DocumentAnalysisBase.Compute(Proposals, Input.Snapshot));
        Assert.False(composed.IsValid, discrepancy);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, composed.ErrorCode);
        Assert.Contains(fragment, Join(composed.Violations), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("D10", """{"key":"i1","section":"assumptions","text":"Si assume il portale attivo.","classification":"Documented","evidence":["E2"]}""", "assumptions sono Assumed")]
    [InlineData("D10", """{"key":"i1","section":"requirements","text":"Il cliente ordina via portale.","classification":"Documented","evidence":[]}""", "evidence: obbligatoria")]
    [InlineData("D12", """{"key":"i1","section":"requirements","text":"","classification":"Documented","evidence":["E2"]}""", "text: non vuoto")]
    public void Level_3_item_rules_are_admitted_by_the_leaf_A_grammar_and_still_rejected_by_the_validator(string discrepancy, string item, string fragment)
    {
        var json = Mutated(ReconcileBatchSchema.LeafA, o => o["items"] = new JsonArray(JsonNode.Parse(item))).ToJsonString();

        Assert.Empty(ReconcileSchemaSubset.Violations(SchemaOf(ReconcileBatchSchema.LeafA), json));
        var composed = Compose(json, RequestOf(ReconcileBatchSchema.LeafA));
        Assert.False(composed.IsValid, discrepancy);
        Assert.Contains(fragment, Join(composed.Violations), StringComparison.Ordinal);
    }

    [Fact]
    public void Level_3_open_reason_and_empty_rationale_are_admitted_by_the_grammar_and_still_rejected_by_the_validator()
    {
        // D11: esito Open senza motivo.
        var open = Mutated(ReconcileBatchSchema.Compare, o => o["focus"] = JsonNode.Parse("""{"outcome":"Open","reason":null}""")).ToJsonString();
        Assert.Empty(ReconcileSchemaSubset.Violations(SchemaOf(ReconcileBatchSchema.Compare), open));
        Assert.Contains("focus.reason", Join(Compose(open, RequestOf(ReconcileBatchSchema.Compare)).Violations), StringComparison.Ordinal);

        // D12: motivazione vuota di un candidato.
        var rationale = Mutated(ReconcileBatchSchema.LeafB, o => o["candidates"] = new JsonArray(Cand("f1", "E3", "E5", rationale: ""))).ToJsonString();
        Assert.Empty(ReconcileSchemaSubset.Violations(SchemaOf(ReconcileBatchSchema.LeafB), rationale));
        Assert.Contains("rationale: non vuota", Join(Compose(rationale, RequestOf(ReconcileBatchSchema.LeafB)).Violations), StringComparison.Ordinal);
    }

    // ================================================================ 6. regressione sulla chiamata 026 del Run 3 e conservazione dei candidati

    /// <summary>
    /// Output grezzo della chiamata 026 del Run 3 di T040 (foglia, vista B, manifesto T040 v4, definizione v8, Qwen3.6-35B-Smart su llama.cpp b10703, <c>finish=stop</c>, 528 token di completamento):
    /// corpus sintetico generato (seed 20261106), nessun segreto. Archivio locale: <c>%TEMP%\bprojects-diag\20261111-160309-…RetrievalGapStageMafRealTests_Cam-48872147\026-assistant.txt</c>.
    /// </summary>
    internal const string Run3Call026 = """
        {
          "contract": "reconcile-alias-1",
          "descriptor": null,
          "noFindings": null,
          "items": [],
          "candidates": [
            {
              "key": "f1",
              "left": "E240",
              "right": "E225",
              "type": "DependsOn",
              "basis": "Inferred",
              "rationale": "La reperibilità notturna limitata a Padova (E240) potrebbe influenzare l'applicazione delle penali per ritardo (E225), richiedendo verifica della copertura territoriale."
            },
            {
              "key": "f2",
              "left": "E4",
              "right": "E225",
              "type": "DependsOn",
              "basis": "Inferred",
              "rationale": "L'incertezza sul termine di pagamento (E4) è rilevante per il calcolo del valore dell'ordine su cui si applicano le penali (E225)."
            },
            {
              "key": "f3",
              "left": "E2",
              "right": "E225",
              "type": "DependsOn",
              "basis": "Inferred",
              "rationale": "Il costo della formazione (E2) potrebbe incidere sul valore complessivo dell'ordine o sui costi di gestione legati alle penalità."
            },
            {
              "key": "f4",
              "left": "E3",
              "right": "E225",
              "type": "DependsOn",
              "basis": "Inferred",
              "rationale": "Il contatto assistenza (E3) è il canale per gestire le dispute sulle penali di ritardo (E225)."
            },
            {
              "key": "f5",
              "left": "E5",
              "right": "E225",
              "type": "DependsOn",
              "basis": "Inferred",
              "rationale": "Il limite del pegno (E5) potrebbe limitare la responsabilità finanziaria in caso di applicazione delle penali (E225)."
            }
          ],
          "focus": null,
          "patches": []
        }
        """;

    private static JsonObject Archived() => JsonNode.Parse(Run3Call026)!.AsObject();

    [Fact]
    public void The_fixture_is_the_archived_raw_output_when_the_archive_is_available()
    {
        var root = Environment.GetEnvironmentVariable("BPROJECTS_DIAG_DIR") is { Length: > 0 } custom ? custom : Path.Combine(Path.GetTempPath(), "bprojects-diag");
        var file = Directory.Exists(root)
            ? Directory.GetDirectories(root, "20261111-160309-*RetrievalGapStageMafRealTests*").Select(d => Path.Combine(d, "026-assistant.txt")).FirstOrDefault(File.Exists)
            : null;
        Assert.SkipWhen(file is null, "Archivio delle chiamate del Run 3 non presente su questa macchina: la fixture versionata resta la fonte.");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(file!)), Archived()));
    }

    [Fact]
    public void The_archived_call_026_is_admitted_by_the_v8_leaf_B_schema_and_rejected_by_the_v9_one_only_for_the_null_descriptor()
    {
        // Il buco del Run 3: la grammatica v8 ammetteva l'output (descriptor stringa o null)...
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileAliasContracts.SchemaOf(ReconcileSchemaVariant.NoItems), Run3Call026));
        Assert.Equal(ReconcileSchemaVariant.NoItems, ReconcileAliasContracts.SchemaVariantOf("leaf", ReconcileView.B));

        // ...la v9 lo rifiuta, e soltanto per il descriptor: items vuoti, cinque candidati E, focus null e patch vuote sono ammessi.
        var violation = Assert.Single(ReconcileSchemaSubset.Violations(SchemaOf(ReconcileBatchSchema.LeafB), Run3Call026));
        Assert.StartsWith("$.descriptor: tipo null non ammesso", violation, StringComparison.Ordinal);
        Assert.Equal(ReconcileBatchSchema.LeafB, ReconcileAliasContracts.BatchSchemaOf("leaf", ReconcileView.B));
        Assert.Null(ReconcileAliasContracts.Decode(Run3Call026).Descriptor);   // il codec, invariato, lo decodifica: è il validatore della gerarchia a rifiutarlo
    }

    [Fact]
    public async Task The_validator_still_rejects_the_null_descriptor_of_call_026_in_a_leaf_B_batch()
    {
        var rejection = await HierarchyRejection("leaf", ReconcileView.B, ReconcileAliasContracts.Decode(Run3Call026).Descriptor);
        Assert.NotNull(rejection);
        Assert.Contains("descriptor: obbligatorio nei batch che costruiscono nodi", rejection, StringComparison.Ordinal);   // lo stesso messaggio del Run 3
    }

    /// <summary>La 026 sugli alias del catalogo di prova: E225 → E1, E240 → E6, E2–E5 invariati (sei estremi distinti come nell'originale).</summary>
    private static JsonObject Remapped(string? descriptor)
    {
        var o = Archived();
        foreach (var c in o["candidates"]!.AsArray())
        {
            foreach (var side in new[] { "left", "right" })
            {
                c![side] = c[side]!.GetValue<string>() switch { "E225" => "E1", "E240" => "E6", var other => other };
            }
        }

        o["descriptor"] = descriptor;
        return o;
    }

    [Fact]
    public void With_a_descriptor_the_five_candidates_of_call_026_are_admitted_by_v9_and_composed_without_loss()
    {
        var request = RequestOf(ReconcileBatchSchema.LeafB);
        var withNull = Remapped(null).ToJsonString();
        var withDescriptor = Remapped("Reperibilità, pagamenti, formazione, assistenza e pegno in relazione alle penali per ritardo.").ToJsonString();

        Assert.NotEmpty(ReconcileSchemaSubset.Violations(SchemaOf(ReconcileBatchSchema.LeafB), withNull));
        Assert.Empty(ReconcileSchemaSubset.Violations(SchemaOf(ReconcileBatchSchema.LeafB), withDescriptor));

        var original = Compose(withNull, request);       // il compositore non giudica il descriptor: compone gli stessi candidati
        var corrected = Compose(withDescriptor, request);
        Assert.True(original.IsValid, Join(original.Violations));
        Assert.True(corrected.IsValid, Join(corrected.Violations));

        var archived = Archived()["candidates"]!.AsArray();
        Assert.Equal(5, corrected.Result!.Candidates.Count);
        Assert.Equal(original.Result!.Candidates.Select(c => c.CandidateId), corrected.Result.Candidates.Select(c => c.CandidateId));   // stesse identità canoniche
        Assert.Equal(5, corrected.Result.Candidates.Select(c => c.CandidateId).Distinct().Count());
        string IdOf(int n) => Input.Evidence[n - 1].EvidenceId.ToString("D");
        var expectedEndpoints = new[] { (IdOf(6), IdOf(1)), (IdOf(4), IdOf(1)), (IdOf(2), IdOf(1)), (IdOf(3), IdOf(1)), (IdOf(5), IdOf(1)) };
        for (var i = 0; i < 5; i++)
        {
            var c = corrected.Result.Candidates[i];
            Assert.Equal(expectedEndpoints[i], (c.Left.Id, c.Right.Id));
            Assert.Equal((ReconcileProposedType.DependsOn, EvidenceRelationBasis.Inferred, CandidateKind.Relation), (c.ProposedType, c.Basis, c.Kind));
            Assert.Equal(archived[i]!["rationale"]!.GetValue<string>(), c.Rationale);   // motivazione conservata parola per parola
            Assert.Equal([BatchId], c.OriginBatchIds);
        }

        Assert.Empty(corrected.Result.ItemProposals);
        Assert.Empty(corrected.Result.Patches);
        Assert.Equal(6, corrected.Result.VisitedEntryIds.Count);   // nessuna evidenza persa: il batch le visita tutte
        Assert.Null(original.Result.Descriptor);
        Assert.NotNull(corrected.Result.Descriptor);
    }
}
