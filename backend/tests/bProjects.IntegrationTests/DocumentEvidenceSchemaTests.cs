using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T033 (DET) — schema evidence-1.1.0 (contracts/ai-workflow.md §2): output chiuso dell'AI, Content persistito con sources discriminate e
/// compatibilità con il Content evidence-1.0.0 storico, che resta decodificato soltanto dal decoder legacy. Nessun database, nessun LLM.
/// </summary>
public sealed class DocumentEvidenceSchemaTests
{
    private static readonly Guid Message = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Document = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid DocumentVersion = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Preparation = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid Artifact = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static readonly Guid Block = Guid.Parse("66666666-6666-4666-8666-666666666666");

    // ---- Fixture JSON (mutabili per i casi negativi) ----

    private static JsonObject DocumentSource(string rangeKey = "r25", string role = "Primary", string group = "g1", int part = 0) => new()
    {
        ["kind"] = "DocumentPassage", ["rangeKey"] = rangeKey, ["quote"] = "Cliente A | 5 ordini", ["occurrence"] = 1,
        ["role"] = role, ["citationGroupKey"] = group, ["part"] = part,
    };

    private static JsonObject MessageSource(string role = "Referent") => new()
    {
        ["kind"] = "Message", ["messageId"] = Message.ToString(), ["quote"] = "ok", ["occurrence"] = 1,
        ["role"] = role, ["citationGroupKey"] = "m1", ["part"] = 0,
    };

    private static JsonObject Candidate(params JsonObject[] sources) => new()
    {
        ["key"] = "e1",
        ["sources"] = new JsonArray(sources.Length == 0 ? [DocumentSource()] : [.. sources.Select(s => (JsonNode)s)]),
        ["kind"] = "Constraint", ["kindRationale"] = null, ["statement"] = "Massimo 5 ordini al giorno per il cliente A.",
        ["subject"] = "Cliente A", ["action"] = null, ["object"] = "ordini", ["scope"] = null, ["purpose"] = null,
        ["instrument"] = null, ["channel"] = null, ["recipient"] = null,
        ["qualifiers"] = new JsonArray(new JsonObject { ["type"] = "Maximum", ["text"] = "5 ordini al giorno" }),
        ["polarity"] = "Affirmed", ["certainty"] = "Declared",
        ["relations"] = new JsonArray(),
    };

    private static JsonObject Output(params JsonObject[] candidates) => new()
    {
        ["schemaVersion"] = "evidence-1.1.0",
        ["evidences"] = new JsonArray(candidates.Length == 0 ? [Candidate()] : [.. candidates.Select(c => (JsonNode)c)]),
        ["noEvidenceReason"] = null,
    };

    private static JsonObject Structure() => new()
    {
        ["subject"] = null, ["action"] = null, ["object"] = null, ["scope"] = null,
        ["purpose"] = null, ["instrument"] = null, ["channel"] = null, ["recipient"] = null,
    };

    private static JsonObject PersistedDocumentSource() => new()
    {
        ["sourceId"] = Guid.NewGuid().ToString(), ["kind"] = "DocumentPassage", ["role"] = "Primary", ["citationGroupId"] = Guid.NewGuid().ToString(),
        ["part"] = 0, ["messageId"] = null, ["sequence"] = null,
        ["documentId"] = Document.ToString(), ["documentVersionId"] = DocumentVersion.ToString(), ["preparationId"] = Preparation.ToString(),
        ["artifactId"] = Artifact.ToString(), ["blockId"] = Block.ToString(), ["start"] = 10, ["end"] = 30, ["quote"] = "Cliente A | 5 ordini",
    };

    private static JsonObject PersistedMessageSource() => new()
    {
        ["sourceId"] = Guid.NewGuid().ToString(), ["kind"] = "Message", ["role"] = "Context", ["citationGroupId"] = Guid.NewGuid().ToString(),
        ["part"] = 0, ["messageId"] = Message.ToString(), ["sequence"] = 3,
        ["documentId"] = null, ["documentVersionId"] = null, ["preparationId"] = null, ["artifactId"] = null, ["blockId"] = null,
        ["start"] = 0, ["end"] = 2, ["quote"] = "ok",
    };

    private static JsonObject Content(params JsonObject[] sources) => new()
    {
        ["schemaVersion"] = "evidence-1.1.0", ["kindRationale"] = null, ["structure"] = Structure(), ["qualifiers"] = new JsonArray(),
        ["sources"] = new JsonArray(sources.Length == 0 ? [PersistedDocumentSource()] : [.. sources.Select(s => (JsonNode)s)]),
    };

    private static string Json(JsonNode node) => node.ToJsonString();

    // ---- Output AI ----

    [Fact]
    public void A_closed_output_with_message_and_document_sources_decodes_with_every_field()
    {
        var output = DocumentEvidenceCodec.DecodeOutput(Json(Output(Candidate(DocumentSource("r10", "Context", "g1", 0), DocumentSource("r25", "Primary", "g1", 1), MessageSource()))));

        Assert.Equal("evidence-1.1.0", output.SchemaVersion);
        var evidence = Assert.Single(output.Evidences);
        Assert.Equal(EvidenceKind.Constraint, evidence.Kind);
        Assert.Equal(EvidenceCertainty.Declared, evidence.Certainty);
        Assert.Equal("Cliente A", evidence.Subject);
        Assert.Equal(3, evidence.Sources.Count);
        var header = Assert.IsType<DocumentPassageCandidateSource>(evidence.Sources[0]);
        Assert.Equal("r10", header.RangeKey);
        Assert.Equal(EvidenceSourceRole.Context, header.Role);
        Assert.Equal(("g1", 1), (evidence.Sources[1].CitationGroupKey, evidence.Sources[1].Part));
        var message = Assert.IsType<MessageCandidateSource>(evidence.Sources[2]);
        Assert.Equal(Message, message.MessageId);
        Assert.Equal(EvidenceSourceRole.Referent, message.Role);
    }

    [Fact]
    public void Zero_evidences_with_a_reason_decode()
    {
        var node = new JsonObject { ["schemaVersion"] = "evidence-1.1.0", ["evidences"] = new JsonArray(), ["noEvidenceReason"] = "Pagina di sommario senza requisiti." };

        var output = DocumentEvidenceCodec.DecodeOutput(Json(node));

        Assert.Empty(output.Evidences);
        Assert.Equal("Pagina di sommario senza requisiti.", output.NoEvidenceReason);
    }

    public static TheoryData<string> RejectedOutputs() => [.. CaseTable().Keys];

    private static Dictionary<string, Func<JsonObject>> CaseTable() => new()
    {
        ["root-extra-property"] = () => { var o = Output(); o["extra"] = 1; return o; },
        ["root-missing-schemaVersion"] = () => { var o = Output(); o.Remove("schemaVersion"); return o; },
        ["root-missing-noEvidenceReason"] = () => { var o = Output(); o.Remove("noEvidenceReason"); return o; },
        ["root-missing-evidences"] = () => { var o = Output(); o.Remove("evidences"); return o; },
        ["root-null-evidences"] = () => { var o = Output(); o["evidences"] = null; return o; },
        ["schema-version-1.0.0"] = () => { var o = Output(); o["schemaVersion"] = "evidence-1.0.0"; return o; },
        ["schema-version-unknown"] = () => { var o = Output(); o["schemaVersion"] = "evidence-1.2.0"; return o; },
        ["schema-version-number"] = () => { var o = Output(); o["schemaVersion"] = 1; return o; },
        ["candidate-extra-property"] = () => { var o = Output(); o["evidences"]![0]!["note"] = "x"; return o; },
        ["candidate-missing-recipient"] = () => { var o = Output(); ((JsonObject)o["evidences"]![0]!).Remove("recipient"); return o; },
        ["candidate-missing-relations"] = () => { var o = Output(); ((JsonObject)o["evidences"]![0]!).Remove("relations"); return o; },
        ["candidate-null-statement"] = () => { var o = Output(); o["evidences"]![0]!["statement"] = null; return o; },
        ["candidate-null-qualifiers"] = () => { var o = Output(); o["evidences"]![0]!["qualifiers"] = null; return o; },
        ["kind-numeric"] = () => { var o = Output(); o["evidences"]![0]!["kind"] = 2; return o; },
        ["kind-unknown"] = () => { var o = Output(); o["evidences"]![0]!["kind"] = "Rule"; return o; },
        ["polarity-numeric"] = () => { var o = Output(); o["evidences"]![0]!["polarity"] = 0; return o; },
        ["certainty-unknown"] = () => { var o = Output(); o["evidences"]![0]!["certainty"] = "Certain"; return o; },
        ["qualifier-type-unknown"] = () => { var o = Output(); o["evidences"]![0]!["qualifiers"]![0]!["type"] = "Rate"; return o; },
        ["qualifier-extra-property"] = () => { var o = Output(); o["evidences"]![0]!["qualifiers"]![0]!["unit"] = "d"; return o; },
        ["relation-type-numeric"] = () => { var o = Output(); o["evidences"]![0]!["relations"] = new JsonArray(new JsonObject { ["type"] = 1, ["target"] = "e2", ["basis"] = "Declared", ["rationale"] = null }); return o; },
        ["relation-missing-basis"] = () => { var o = Output(); o["evidences"]![0]!["relations"] = new JsonArray(new JsonObject { ["type"] = "Confirms", ["target"] = "e2", ["rationale"] = null }); return o; },
        ["source-kind-missing"] = () => { var o = Output(); ((JsonObject)o["evidences"]![0]!["sources"]![0]!).Remove("kind"); return o; },
        ["source-kind-unknown"] = () => Output(Candidate(new JsonObject { ["kind"] = "Revision", ["quote"] = "x", ["occurrence"] = 1, ["role"] = "Primary", ["citationGroupKey"] = "g", ["part"] = 0 })),
        ["source-kind-numeric"] = () => { var o = Output(); o["evidences"]![0]!["sources"]![0]!["kind"] = 1; return o; },
        ["document-source-with-messageId"] = () => { var s = DocumentSource(); s["messageId"] = Message.ToString(); return Output(Candidate(s)); },
        ["document-source-with-locator"] = () => { var s = DocumentSource(); s["page"] = 3; return Output(Candidate(s)); },
        ["document-source-with-blockId"] = () => { var s = DocumentSource(); s["blockId"] = Block.ToString(); return Output(Candidate(s)); },
        ["document-source-with-offsets"] = () => { var s = DocumentSource(); s["start"] = 0; s["end"] = 4; return Output(Candidate(s)); },
        ["document-source-missing-rangeKey"] = () => { var s = DocumentSource(); s.Remove("rangeKey"); return Output(Candidate(s)); },
        ["document-source-null-rangeKey"] = () => { var s = DocumentSource(); s["rangeKey"] = null; return Output(Candidate(s)); },
        ["document-source-missing-part"] = () => { var s = DocumentSource(); s.Remove("part"); return Output(Candidate(s)); },
        ["document-source-missing-citationGroupKey"] = () => { var s = DocumentSource(); s.Remove("citationGroupKey"); return Output(Candidate(s)); },
        ["message-source-with-rangeKey"] = () => { var s = MessageSource(); s["rangeKey"] = "r1"; return Output(Candidate(s)); },
        ["message-source-with-documentId"] = () => { var s = MessageSource(); s["documentId"] = Document.ToString(); return Output(Candidate(s)); },
        ["message-source-missing-messageId"] = () => { var s = MessageSource(); s.Remove("messageId"); return Output(Candidate(s)); },
        ["message-source-malformed-uuid"] = () => { var s = MessageSource(); s["messageId"] = "not-a-uuid"; return Output(Candidate(s)); },
        ["source-role-numeric"] = () => { var s = DocumentSource(); s["role"] = 0; return Output(Candidate(s)); },
        ["source-role-unknown"] = () => { var s = DocumentSource(); s["role"] = "Owner"; return Output(Candidate(s)); },
        ["occurrence-as-string"] = () => { var s = DocumentSource(); s["occurrence"] = "1"; return Output(Candidate(s)); },
        ["occurrence-fractional"] = () => { var s = DocumentSource(); s["occurrence"] = 1.5; return Output(Candidate(s)); },
        ["part-as-string"] = () => { var s = DocumentSource(); s["part"] = "0"; return Output(Candidate(s)); },
        ["quote-null"] = () => { var s = DocumentSource(); s["quote"] = null; return Output(Candidate(s)); },
        ["quote-number"] = () => { var s = DocumentSource(); s["quote"] = 5; return Output(Candidate(s)); },
    };

    [Theory]
    [MemberData(nameof(RejectedOutputs))]
    public void Extra_missing_null_numeric_and_unknown_values_are_rejected(string caseName)
    {
        var json = Json(CaseTable()[caseName]());

        Assert.Throws<JsonException>(() => DocumentEvidenceCodec.DecodeOutput(json));
    }

    [Fact]
    public void Duplicate_properties_and_non_object_payloads_are_rejected()
    {
        var valid = Json(Output());
        var duplicateRoot = valid.Replace("\"noEvidenceReason\":null", "\"noEvidenceReason\":null,\"noEvidenceReason\":null", StringComparison.Ordinal);
        var duplicateSource = valid.Replace("\"part\":0", "\"part\":0,\"part\":0", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => DocumentEvidenceCodec.DecodeOutput(duplicateRoot));
        Assert.Throws<JsonException>(() => DocumentEvidenceCodec.DecodeOutput(duplicateSource));
        foreach (var payload in new[] { "", "null", "[]", "\"x\"", "5", "```json\n" + valid + "\n```", valid[..^10] })
        {
            Assert.ThrowsAny<JsonException>(() => DocumentEvidenceCodec.DecodeOutput(payload));
        }
    }

    // ---- Content persistito ----

    [Fact]
    public void Persisted_content_round_trips_with_document_and_message_sources_and_stable_bytes()
    {
        var source = Json(Content(PersistedDocumentSource(), PersistedMessageSource()));

        var content = DocumentEvidenceCodec.DecodeContent(source);
        var again = DocumentEvidenceCodec.EncodeContent(content);

        Assert.Equal("evidence-1.1.0", content.SchemaVersion);
        Assert.Equal([EvidenceSourceKind.DocumentPassage, EvidenceSourceKind.Message], content.Sources.Select(s => s.Kind));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(source), JsonNode.Parse(again)));
        Assert.Equal(again, DocumentEvidenceCodec.EncodeContent(DocumentEvidenceCodec.DecodeContent(again)));
        var document = content.Sources[0];
        Assert.Null(document.MessageId);
        Assert.Null(document.Sequence);
        Assert.Equal((Block, 10, 30), (document.BlockId!.Value, document.Start, document.End));
        var message = content.Sources[1];
        Assert.Equal((Message, 3L), (message.MessageId!.Value, message.Sequence!.Value));
        Assert.Null(message.DocumentId);
    }

    public static TheoryData<string> RejectedContents() =>
    [
        "extra-root-property", "missing-schemaVersion", "missing-structure-field", "extra-structure-field", "legacy-schemaVersion", "unknown-kind", "numeric-role",
        "no-sources-property", "null-sources", "document-with-messageId", "document-with-sequence", "document-missing-blockId", "document-missing-artifactId",
        "message-with-documentId", "message-with-blockId", "message-missing-sequence", "message-missing-messageId", "missing-citationGroupId", "negative-part",
        "start-missing", "quote-null", "empty-range", "negative-start", "duplicate-sourceId",
    ];

    [Theory]
    [MemberData(nameof(RejectedContents))]
    public void Persisted_content_rejects_malformed_xor_and_inconsistent_sources(string caseName)
    {
        var document = PersistedDocumentSource();
        var message = PersistedMessageSource();
        JsonObject content = caseName switch
        {
            "extra-root-property" => With(Content(document), c => c["origin"] = "x"),
            "missing-schemaVersion" => With(Content(document), c => c.Remove("schemaVersion")),
            "missing-structure-field" => With(Content(document), c => ((JsonObject)c["structure"]!).Remove("channel")),
            "extra-structure-field" => With(Content(document), c => c["structure"]!["when"] = "x"),
            "legacy-schemaVersion" => With(Content(document), c => c["schemaVersion"] = "evidence-1.0.0"),
            "unknown-kind" => Content(Mutate(document, "kind", "Revision")),
            "numeric-role" => Content(Mutate(document, "role", 0)),
            "no-sources-property" => With(Content(document), c => c.Remove("sources")),
            "null-sources" => With(Content(document), c => c["sources"] = null),
            "document-with-messageId" => Content(Mutate(document, "messageId", Message.ToString())),
            "document-with-sequence" => Content(Mutate(document, "sequence", 4)),
            "document-missing-blockId" => Content(Mutate(document, "blockId", null)),
            "document-missing-artifactId" => Content(Mutate(document, "artifactId", null)),
            "message-with-documentId" => Content(Mutate(message, "documentId", Document.ToString())),
            "message-with-blockId" => Content(Mutate(message, "blockId", Block.ToString())),
            "message-missing-sequence" => Content(Mutate(message, "sequence", null)),
            "message-missing-messageId" => Content(Mutate(message, "messageId", null)),
            "missing-citationGroupId" => Content(Remove(document, "citationGroupId")),
            "negative-part" => Content(Mutate(document, "part", -1)),
            "start-missing" => Content(Remove(document, "start")),
            "quote-null" => Content(Mutate(document, "quote", null)),
            "empty-range" => Content(Mutate(Mutate(document, "start", 5), "end", 5)),
            "negative-start" => Content(Mutate(document, "start", -1)),
            "duplicate-sourceId" => Content(document, Mutate(PersistedDocumentSource(), "sourceId", document["sourceId"]!.GetValue<string>())),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };

        Assert.Throws<JsonException>(() => DocumentEvidenceCodec.DecodeContent(Json(content)));
    }

    private static JsonObject With(JsonObject node, Action<JsonObject> change)
    {
        change(node);
        return node;
    }

    private static JsonObject Mutate(JsonObject node, string name, JsonNode? value)
    {
        node[name] = value;
        return node;
    }

    private static JsonObject Remove(JsonObject node, string name)
    {
        node.Remove(name);
        return node;
    }

    // ---- Compatibilità con evidence-1.0.0 ----

    private static JsonObject LegacyContent() => new()
    {
        ["kindRationale"] = null,
        ["structure"] = Structure(),
        ["qualifiers"] = new JsonArray(),
        ["sources"] = new JsonArray(new JsonObject
        {
            ["messageId"] = Message.ToString(), ["sequence"] = 1, ["role"] = "Primary", ["start"] = 0, ["end"] = 5, ["quote"] = "Hello",
        }),
    };

    [Fact]
    public void Historical_content_is_decoded_only_by_the_legacy_decoder_and_is_never_reinterpreted_as_1_1()
    {
        var legacy = Json(LegacyContent());

        var decoded = EvidenceSchemaDispatch.DecodeContent("evidence-1.0.0", legacy);

        Assert.NotNull(decoded.Legacy);
        Assert.Null(decoded.Document);
        var span = Assert.Single(decoded.Legacy!.Sources);
        Assert.Equal((Message, 1L, 5), (span.MessageId, span.Sequence, span.End));
        // Il vecchio JSON non è accettato dal decoder 1.1 (nessun decoder permissivo) e viceversa.
        Assert.Throws<JsonException>(() => DocumentEvidenceCodec.DecodeContent(legacy));
        Assert.ThrowsAny<JsonException>(() => EvidenceJson.Deserialize<EvidenceContent>(Json(Content())));
        Assert.Throws<JsonException>(() => EvidenceSchemaDispatch.DecodeContent("evidence-1.0.0", Json(Content())));
        Assert.Throws<JsonException>(() => EvidenceSchemaDispatch.DecodeContent("evidence-1.1.0", legacy));
    }

    [Fact]
    public void Historical_content_re_encodes_to_the_same_logical_json()
    {
        var legacy = LegacyContent();

        var roundTrip = EvidenceJson.Serialize(EvidenceSchemaDispatch.DecodeContent("evidence-1.0.0", Json(legacy)).Legacy!);

        Assert.True(JsonNode.DeepEquals(legacy, JsonNode.Parse(roundTrip)));
        Assert.Equal("evidence-1.0.0", EvidenceExtractionVersion.CurrentSchemaVersion);   // costante storica invariata
        Assert.Equal("coverage-v1", EvidenceCoverage.CheckerVersion);                      // il checker 001 non cambia
    }

    [Theory]
    [InlineData("")]
    [InlineData("evidence-2.0.0")]
    [InlineData("evidence-1.1")]
    [InlineData("Evidence-1.1.0")]
    [InlineData("1.1.0")]
    public void Unknown_schema_versions_are_refused_explicitly(string schemaVersion)
    {
        var failure = Assert.Throws<NotSupportedException>(() => EvidenceSchemaDispatch.DecodeContent(schemaVersion, Json(Content())));

        Assert.Contains("schema", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_codec_never_elevates_certainty_or_origin_of_a_document_hypothesis()
    {
        // Un'ipotesi trovata in un documento resta tale; Declared su documento significa «asserito dalla fonte», non conferma del PM.
        var hypothesis = Candidate(DocumentSource());
        hypothesis["certainty"] = "Hypothesized";
        hypothesis["kind"] = "Assumption";
        var declared = Candidate(DocumentSource());
        declared["key"] = "e2";

        var output = DocumentEvidenceCodec.DecodeOutput(Json(Output(hypothesis, declared)));

        Assert.Equal([EvidenceCertainty.Hypothesized, EvidenceCertainty.Declared], output.Evidences.Select(e => e.Certainty));
        Assert.All(output.Evidences, e => Assert.All(e.Sources, s => Assert.IsType<DocumentPassageCandidateSource>(s)));
        var encoded = EvidenceJson.Serialize(output);
        Assert.Contains("\"certainty\":\"Hypothesized\"", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("PmConfirmed", encoded, StringComparison.Ordinal);
    }
}
