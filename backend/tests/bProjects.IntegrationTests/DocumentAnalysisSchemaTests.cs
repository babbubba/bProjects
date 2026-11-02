using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>
/// T033 (DET) — AnalysisContent 1.1.0 (contracts/ai-workflow.md §1): decoder separato e strict, dispatch per versione esatta e
/// compatibilità con i JSON 1.0.0 storici, che non vengono convertiti né accettati dal decoder nuovo. Nessun database, nessun LLM.
/// </summary>
public sealed class DocumentAnalysisSchemaTests
{
    private static readonly Guid Project = Guid.Parse("a0000000-0000-4000-8000-000000000001");
    private static readonly Guid Analysis = Guid.Parse("a0000000-0000-4000-8000-000000000002");
    private static readonly Guid PmMessage = Guid.Parse("b0000000-0000-4000-8000-000000000001");
    private static readonly Guid AssistantMessage = Guid.Parse("b0000000-0000-4000-8000-000000000002");
    private static readonly Guid Revision = Guid.Parse("b0000000-0000-4000-8000-000000000003");
    private static readonly Guid Passage = Guid.Parse("c0000000-0000-4000-8000-000000000001");
    private static readonly Guid PassageB = Guid.Parse("c0000000-0000-4000-8000-000000000002");
    private static readonly Guid MessageSource = Guid.Parse("c0000000-0000-4000-8000-000000000003");
    private static readonly Guid AssistantSource = Guid.Parse("c0000000-0000-4000-8000-000000000004");
    private static readonly Guid RevisionSource = Guid.Parse("c0000000-0000-4000-8000-000000000005");
    private static readonly Guid Document = Guid.Parse("d0000000-0000-4000-8000-000000000001");

    private static JsonObject PassageSource(Guid id, int start = 10, int end = 30) => new()
    {
        ["id"] = id.ToString(), ["kind"] = "DocumentPassage", ["messageId"] = null, ["messageRole"] = null, ["revisionId"] = null,
        ["documentId"] = Document.ToString(), ["documentVersionId"] = Guid.NewGuid().ToString(), ["preparationId"] = Guid.NewGuid().ToString(),
        ["artifactId"] = Guid.NewGuid().ToString(), ["blockId"] = Guid.NewGuid().ToString(), ["start"] = start, ["end"] = end, ["quote"] = "Cliente A | 5 ordini",
    };

    private static JsonObject MessageSourceNode(Guid id, Guid message, string role) => new()
    {
        ["id"] = id.ToString(), ["kind"] = "Message", ["messageId"] = message.ToString(), ["messageRole"] = role, ["revisionId"] = null,
        ["documentId"] = null, ["documentVersionId"] = null, ["preparationId"] = null, ["artifactId"] = null, ["blockId"] = null,
        ["start"] = null, ["end"] = null, ["quote"] = null,
    };

    private static JsonObject RevisionSourceNode(Guid id) => new()
    {
        ["id"] = id.ToString(), ["kind"] = "PmRevision", ["messageId"] = null, ["messageRole"] = null, ["revisionId"] = Revision.ToString(),
        ["documentId"] = null, ["documentVersionId"] = null, ["preparationId"] = null, ["artifactId"] = null, ["blockId"] = null,
        ["start"] = null, ["end"] = null, ["quote"] = null,
    };

    private static JsonObject Item(string text, string classification, params Guid[] refs) => new()
    {
        ["id"] = Guid.NewGuid().ToString(), ["text"] = text, ["classification"] = classification,
        ["evidenceRefs"] = new JsonArray([.. refs.Select(r => (JsonNode)r.ToString())]),
    };

    private static JsonObject Valid(long cutoff = 4)
    {
        var sources = new JsonArray(
            PassageSource(Passage), PassageSource(PassageB, 40, 60), MessageSourceNode(MessageSource, PmMessage, "user"),
            MessageSourceNode(AssistantSource, AssistantMessage, "assistant"), RevisionSourceNode(RevisionSource));
        return new JsonObject
        {
            ["schemaVersion"] = "1.1.0", ["analysisId"] = Analysis.ToString(), ["projectId"] = Project.ToString(), ["sourceCutoffSequence"] = cutoff,
            ["validationState"] = "Unvalidated", ["sources"] = sources,
            ["goals"] = new JsonArray(Item("Ridurre gli errori d'ordine.", "Declared", MessageSource)),
            ["contextAndScope"] = new JsonObject
            {
                ["overview"] = new JsonArray(Item("Il capitolato limita gli ordini giornalieri.", "Documented", Passage, PassageB)),
                ["included"] = new JsonArray(), ["excluded"] = new JsonArray(),
            },
            ["requirements"] = new JsonArray(Item("Massimo 5 ordini al giorno per il cliente A.", "Documented", Passage)),
            ["operationalFlows"] = new JsonArray(), ["constraints"] = new JsonArray(), ["dependencies"] = new JsonArray(),
            ["assumptions"] = new JsonArray(Item("Il limite vale anche nei festivi.", "Assumed", Passage)),
            ["openQuestions"] = new JsonArray(Item("Il limite include gli ordini urgenti?", "Inferred")),
            ["conflicts"] = new JsonArray(),
        };
    }

    private static string Json(JsonNode node) => node.ToJsonString();

    // ---- Decoder 1.1 ----

    [Fact]
    public void A_valid_1_1_content_decodes_with_document_passages_and_the_documented_classification()
    {
        var node = Valid();
        var content = DocumentAnalysisContentJson.Deserialize(Json(node));

        Assert.Equal("1.1.0", content.SchemaVersion);
        Assert.Equal(5, content.Sources.Count);
        var passage = content.Sources[0];
        Assert.Equal(DocumentAnalysisSourceKind.DocumentPassage, passage.Kind);
        Assert.Equal((10, 30), (passage.Start!.Value, passage.End!.Value));
        Assert.Null(passage.MessageId);
        Assert.Equal(DocumentAnalysisClassification.Documented, content.Requirements[0].Classification);
        Assert.Equal([Passage, PassageB], content.ContextAndScope.Overview[0].EvidenceRefs);
        Assert.True(JsonNode.DeepEquals(node, JsonNode.Parse(DocumentAnalysisContentJson.Serialize(content))));
    }

    public static TheoryData<string> RejectedContents() =>
    [
        "root-extra", "root-missing-conflicts", "root-null-sources", "version-1.0.0", "version-1.2.0", "version-missing", "classification-numeric",
        "classification-unknown", "source-kind-numeric", "source-kind-unknown", "source-extra", "source-missing-quote", "source-missing-blockId", "role-numeric",
        "item-extra", "item-null-refs", "cutoff-string", "cutoff-fractional", "validation-state-numeric", "section-missing",
    ];

    [Theory]
    [MemberData(nameof(RejectedContents))]
    public void Extra_missing_null_numeric_and_unknown_values_are_rejected(string caseName)
    {
        var node = Valid();
        switch (caseName)
        {
            case "root-extra": node["extra"] = 1; break;
            case "root-missing-conflicts": node.Remove("conflicts"); break;
            case "root-null-sources": node["sources"] = null; break;
            case "version-1.0.0": node["schemaVersion"] = "1.0.0"; break;
            case "version-1.2.0": node["schemaVersion"] = "1.2.0"; break;
            case "version-missing": node.Remove("schemaVersion"); break;
            case "classification-numeric": node["goals"]![0]!["classification"] = 0; break;
            case "classification-unknown": node["goals"]![0]!["classification"] = "Confirmed"; break;
            case "source-kind-numeric": node["sources"]![0]!["kind"] = 2; break;
            case "source-kind-unknown": node["sources"]![0]!["kind"] = "Attachment"; break;
            case "source-extra": node["sources"]![0]!["page"] = 1; break;
            case "source-missing-quote": ((JsonObject)node["sources"]![0]!).Remove("quote"); break;
            case "source-missing-blockId": ((JsonObject)node["sources"]![0]!).Remove("blockId"); break;
            case "role-numeric": node["sources"]![2]!["messageRole"] = 0; break;
            case "item-extra": node["goals"]![0]!["note"] = "x"; break;
            case "item-null-refs": node["goals"]![0]!["evidenceRefs"] = null; break;
            case "cutoff-string": node["sourceCutoffSequence"] = "4"; break;
            case "cutoff-fractional": node["sourceCutoffSequence"] = 4.5; break;
            case "validation-state-numeric": node["validationState"] = 0; break;
            case "section-missing": ((JsonObject)node["contextAndScope"]!).Remove("excluded"); break;
            default: throw new ArgumentOutOfRangeException(nameof(caseName));
        }

        Assert.Throws<JsonException>(() => DocumentAnalysisContentJson.Deserialize(Json(node)));
    }

    [Fact]
    public void Duplicate_properties_are_rejected()
    {
        var json = Json(Valid()).Replace("\"validationState\":\"Unvalidated\"", "\"validationState\":\"Unvalidated\",\"validationState\":\"Unvalidated\"", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => DocumentAnalysisContentJson.Deserialize(json));
    }

    // ---- Compatibilità con 1.0.0 ----

    private static JsonObject Legacy() => new()
    {
        ["schemaVersion"] = "1.0.0", ["analysisId"] = Analysis.ToString(), ["projectId"] = Project.ToString(), ["sourceCutoffSequence"] = 4,
        ["validationState"] = "Unvalidated",
        ["sources"] = new JsonArray(new JsonObject
        {
            ["id"] = MessageSource.ToString(), ["kind"] = "Message", ["messageId"] = PmMessage.ToString(), ["messageRole"] = "user", ["revisionId"] = null,
        }),
        ["goals"] = new JsonArray(Item("Ridurre gli errori d'ordine.", "Declared", MessageSource)),
        ["contextAndScope"] = new JsonObject { ["overview"] = new JsonArray(), ["included"] = new JsonArray(), ["excluded"] = new JsonArray() },
        ["requirements"] = new JsonArray(), ["operationalFlows"] = new JsonArray(), ["constraints"] = new JsonArray(),
        ["dependencies"] = new JsonArray(), ["assumptions"] = new JsonArray(), ["openQuestions"] = new JsonArray(), ["conflicts"] = new JsonArray(),
    };

    [Fact]
    public void The_dispatch_selects_the_decoder_by_the_exact_schema_version()
    {
        var historical = AnalysisSchemaDispatch.Decode(Json(Legacy()));
        var documented = AnalysisSchemaDispatch.Decode(Json(Valid()));

        Assert.NotNull(historical.V10);
        Assert.Null(historical.V11);
        Assert.Equal("1.0.0", historical.V10!.SchemaVersion);
        Assert.NotNull(documented.V11);
        Assert.Null(documented.V10);
        Assert.Equal("1.1.0", documented.V11!.SchemaVersion);
    }

    [Fact]
    public void Historical_1_0_content_stays_byte_logically_identical_through_the_legacy_decoder()
    {
        var legacy = Legacy();

        var roundTrip = AnalysisContentJson.Serialize(AnalysisSchemaDispatch.Decode(Json(legacy)).V10!);

        Assert.True(JsonNode.DeepEquals(legacy, JsonNode.Parse(roundTrip)));
        Assert.Equal("1.0.0", AnalysisContent.CurrentSchemaVersion);
        Assert.Equal("1.0.0", AnalysisRevision.CurrentSchemaVersion);   // le revisioni nuove restano 1.0 finché non nascono da un'analisi documentale (T059)
    }

    [Fact]
    public void Neither_decoder_accepts_the_other_version_nor_the_documented_vocabulary()
    {
        Assert.Throws<JsonException>(() => AnalysisContentJson.Deserialize(Json(Valid())));       // il decoder 1.0 non legge 1.1
        Assert.Throws<JsonException>(() => DocumentAnalysisContentJson.Deserialize(Json(Legacy())));   // né il contrario
        var legacyWithDocumented = Legacy();
        legacyWithDocumented["goals"]![0]!["classification"] = "Documented";
        Assert.Throws<JsonException>(() => AnalysisContentJson.Deserialize(Json(legacyWithDocumented)));
        var legacyWithPassage = Legacy();
        legacyWithPassage["sources"]![0]!["kind"] = "DocumentPassage";
        Assert.Throws<JsonException>(() => AnalysisContentJson.Deserialize(Json(legacyWithPassage)));
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("2.0.0")]
    [InlineData("1.1")]
    [InlineData("")]
    public void Unknown_schema_versions_are_refused_explicitly(string version)
    {
        var node = Valid();
        node["schemaVersion"] = version;

        var failure = Assert.Throws<NotSupportedException>(() => AnalysisSchemaDispatch.Decode(Json(node)));

        Assert.Contains("schema", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":5}")]
    [InlineData("{\"schemaVersion\":null}")]
    public void A_payload_without_a_readable_schema_version_is_refused(string json)
    {
        Assert.ThrowsAny<Exception>(() => AnalysisSchemaDispatch.Decode(json));
    }

    // ---- Regole strutturali 1.1 (senza database) ----

    private static IReadOnlyList<string> Check(JsonObject node, long expectedCutoff = 4, int pmMessages = 2, int readyDocuments = 1) =>
        DocumentAnalysisContentRules.Check(
            DocumentAnalysisContentJson.Deserialize(Json(node)),
            new DocumentAnalysisFacts(Project, Analysis, expectedCutoff, pmMessages, readyDocuments));

    [Fact]
    public void A_consistent_content_passes_the_structural_rules()
    {
        Assert.Empty(Check(Valid()));
    }

    [Fact]
    public void Documented_requires_a_document_passage_and_declared_never_rests_on_documents_alone()
    {
        var documentedWithoutPassage = Valid();
        documentedWithoutPassage["requirements"] = new JsonArray(Item("Massimo 5 ordini.", "Documented", MessageSource));
        var declaredOnDocumentOnly = Valid();
        declaredOnDocumentOnly["goals"] = new JsonArray(Item("Il PM conferma il limite.", "Declared", Passage));
        var declaredOnAssistant = Valid();
        declaredOnAssistant["goals"] = new JsonArray(Item("Dichiarato dall'assistente.", "Declared", AssistantSource));
        var declaredMixed = Valid();
        declaredMixed["goals"] = new JsonArray(Item("Il PM ribadisce il limite.", "Declared", Passage, MessageSource));
        var declaredOnRevision = Valid();
        declaredOnRevision["goals"] = new JsonArray(Item("Correzione PM.", "Declared", RevisionSource));

        Assert.Contains(Check(documentedWithoutPassage), v => v.Contains("Documented", StringComparison.Ordinal));
        Assert.Contains(Check(declaredOnDocumentOnly), v => v.Contains("Declared", StringComparison.Ordinal));
        Assert.Contains(Check(declaredOnAssistant), v => v.Contains("Declared", StringComparison.Ordinal));
        Assert.Empty(Check(declaredMixed));   // un item misto conserva tutte le fonti e non eleva l'autorità della fonte documentale
        Assert.Empty(Check(declaredOnRevision));
    }

    [Fact]
    public void An_assumption_stays_assumed_even_when_it_cites_a_document()
    {
        var node = Valid();
        node["assumptions"] = new JsonArray(Item("Il limite vale nei festivi.", "Documented", Passage));

        Assert.Contains(Check(node), v => v.Contains("assumptions", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("document-missing-block")]
    [InlineData("document-with-message")]
    [InlineData("document-empty-range")]
    [InlineData("document-negative-start")]
    [InlineData("document-blank-quote")]
    [InlineData("message-with-document-fields")]
    [InlineData("message-with-locator")]
    [InlineData("revision-with-message")]
    [InlineData("duplicate-source-id")]
    [InlineData("unresolved-ref")]
    [InlineData("duplicate-ref")]
    public void Source_discrimination_is_enforced_with_xor_between_message_and_document_fields(string caseName)
    {
        var node = Valid();
        var sources = (JsonArray)node["sources"]!;
        switch (caseName)
        {
            case "document-missing-block": sources[0]!["blockId"] = null; break;
            case "document-with-message": sources[0]!["messageId"] = PmMessage.ToString(); break;
            case "document-empty-range": sources[0]!["end"] = sources[0]!["start"]!.GetValue<int>(); break;
            case "document-negative-start": sources[0]!["start"] = -1; break;
            case "document-blank-quote": sources[0]!["quote"] = "   "; break;
            case "message-with-document-fields": sources[2]!["documentId"] = Document.ToString(); break;
            case "message-with-locator": sources[2]!["start"] = 0; break;
            case "revision-with-message": sources[4]!["messageId"] = PmMessage.ToString(); break;
            case "duplicate-source-id": sources[1]!["id"] = Passage.ToString(); break;
            case "unresolved-ref": node["goals"]![0]!["evidenceRefs"] = new JsonArray(Guid.NewGuid().ToString()); break;
            case "duplicate-ref": node["goals"]![0]!["evidenceRefs"] = new JsonArray(MessageSource.ToString(), MessageSource.ToString()); break;
            default: throw new ArgumentOutOfRangeException(nameof(caseName));
        }

        Assert.NotEmpty(Check(node));
    }

    [Fact]
    public void Identity_must_match_the_server_values()
    {
        var wrongProject = Valid();
        wrongProject["projectId"] = Guid.NewGuid().ToString();
        var wrongAnalysis = Valid();
        wrongAnalysis["analysisId"] = Guid.NewGuid().ToString();

        Assert.Contains(Check(wrongProject), v => v.StartsWith("projectId", StringComparison.Ordinal));
        Assert.Contains(Check(wrongAnalysis), v => v.StartsWith("analysisId", StringComparison.Ordinal));
        Assert.Contains(Check(Valid(), expectedCutoff: 5), v => v.StartsWith("sourceCutoffSequence", StringComparison.Ordinal));
    }

    [Fact]
    public void A_zero_cutoff_is_allowed_only_without_pm_messages_and_with_a_non_empty_ready_document_snapshot()
    {
        // Solo documenti: nessun messaggio PM, almeno una fonte documentale pronta.
        Assert.Empty(Check(Valid(cutoff: 0), expectedCutoff: 0, pmMessages: 0, readyDocuments: 2));
        // Snapshot documentale vuoto o non pronto: lo zero non ha base (e nessun messaggio PM fittizio lo sostituisce).
        Assert.Contains(Check(Valid(cutoff: 0), expectedCutoff: 0, pmMessages: 0, readyDocuments: 0), v => v.StartsWith("sourceCutoffSequence", StringComparison.Ordinal));
        // Con messaggi PM il cutoff non può essere zero, e un cutoff reale non coincide con il valore del server.
        Assert.Contains(Check(Valid(cutoff: 0), expectedCutoff: 0, pmMessages: 3, readyDocuments: 2), v => v.StartsWith("sourceCutoffSequence", StringComparison.Ordinal));
        Assert.Contains(Check(Valid(cutoff: -1), expectedCutoff: -1, pmMessages: 0, readyDocuments: 1), v => v.StartsWith("sourceCutoffSequence", StringComparison.Ordinal));
        // Il ramo 001 non cambia: il validatore 1.0 continua a pretendere cutoff >= 1.
        var legacy = Legacy();
        legacy["sourceCutoffSequence"] = 0;
        var legacyContext = new AnalysisValidationContext(Project, Analysis, 0, [], new HashSet<Guid>());
        var result = AnalysisContentValidator.ValidateText(Json(legacy), legacyContext, AnalysisActorKind.Ai);
        Assert.False(result.Accepted);
        Assert.Contains(result.Violations, v => v.Contains("sourceCutoffSequence", StringComparison.Ordinal));
    }

    [Fact]
    public void A_conflict_alternative_may_rest_on_a_document_but_not_on_the_assistant_alone()
    {
        var node = Valid();
        node["conflicts"] = new JsonArray(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["description"] = "Limite giornaliero discordante.", ["status"] = "Open",
            ["alternatives"] = new JsonArray(Item("5 ordini al giorno.", "Documented", Passage), Item("8 ordini al giorno.", "Declared", MessageSource)),
            ["resolution"] = null,
        });
        var onAssistant = node.DeepClone().AsObject();
        onAssistant["conflicts"]![0]!["alternatives"] = new JsonArray(Item("5 ordini.", "Inferred", AssistantSource), Item("8 ordini.", "Declared", MessageSource));

        Assert.Empty(Check(node));
        Assert.Contains(Check(onAssistant), v => v.Contains("alternatives", StringComparison.Ordinal));
    }
}
