using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T049 (DET) — lo schema strict inviato al modello per un'unità è allineato al decoder di <c>evidence-1.1.0</c>: stessi campi, enum chiusi,
/// oggetti senza proprietà extra, solo le <c>rangeKey</c> dell'unità, e ciò che il decoder produce è ciò che lo schema descrive.
/// </summary>
public sealed class DocumentExtractionSchemaTests
{
    private static JsonObject Schema(params string[] keys) => JsonNode.Parse(DocumentEvidenceSchema.ForUnit(keys.Length == 0 ? ["r1", "r2"] : keys).GetRawText())!.AsObject();

    private static IEnumerable<string> Names(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<string>());

    private static JsonObject EvidenceOf(JsonObject schema) => schema["properties"]!["evidences"]!["items"]!.AsObject();

    private static JsonObject SourceOf(JsonObject schema) => EvidenceOf(schema)["properties"]!["sources"]!["items"]!.AsObject();

    private static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    [Fact]
    public void Every_object_is_closed_with_all_properties_required()
    {
        void Walk(JsonNode? node, string path)
        {
            if (node is JsonObject obj)
            {
                if (obj["type"]?.ToString() == "object")
                {
                    Assert.False(obj["additionalProperties"]!.GetValue<bool>(), path);
                    Assert.Equal(obj["properties"]!.AsObject().Select(p => p.Key).Order(), Names(obj["required"]).Order());
                }

                foreach (var child in obj)
                {
                    Walk(child.Value, $"{path}/{child.Key}");
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array)
                {
                    Walk(child, path);
                }
            }
        }

        Walk(Schema(), "#");
    }

    [Fact]
    public void The_evidence_object_has_exactly_the_decoder_properties()
    {
        var decoder = typeof(DocumentExtractedEvidence).GetProperties().Select(p => Camel(p.Name)).Order().ToList();

        Assert.Equal(decoder, EvidenceOf(Schema())["properties"]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(decoder, DocumentEvidenceSchema.EvidenceProperties.Order());
    }

    [Fact]
    public void The_top_level_and_nested_shapes_match_the_decoder_dtos()
    {
        var schema = Schema();
        Assert.Equal(typeof(DocumentExtractionOutput).GetProperties().Select(p => Camel(p.Name)).Order(), schema["properties"]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(typeof(ExtractedQualifier).GetProperties().Select(p => Camel(p.Name)).Order(),
            EvidenceOf(schema)["properties"]!["qualifiers"]!["items"]!["properties"]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(typeof(ExtractedRelation).GetProperties().Select(p => Camel(p.Name)).Order(),
            EvidenceOf(schema)["properties"]!["relations"]!["items"]!["properties"]!.AsObject().Select(p => p.Key).Order());
    }

    [Fact]
    public void The_source_is_the_document_passage_variant_only_with_the_fields_the_converter_writes()
    {
        var written = JsonDocument.Parse(JsonSerializer.Serialize<CandidateSource>(new DocumentPassageCandidateSource
        {
            RangeKey = "r1", Quote = "q", Occurrence = 1, Role = EvidenceSourceRole.Primary, CitationGroupKey = "g", Part = 0,
        }, DocumentEvidenceCodec.Options)).RootElement.EnumerateObject().Select(p => p.Name).Order();
        var source = SourceOf(Schema());

        Assert.Equal(written, source["properties"]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(["DocumentPassage"], Names(source["properties"]!["kind"]!["enum"]));
        Assert.Equal(["Context", "Primary"], Names(source["properties"]!["role"]!["enum"]).Order());   // Referent è dei soli messaggi
        Assert.Equal(1, EvidenceOf(Schema())["properties"]!["sources"]!["minItems"]!.GetValue<int>());
    }

    [Fact]
    public void Only_the_range_keys_of_the_unit_are_citable_and_the_enums_are_the_closed_vocabularies_of_the_contract()
    {
        var schema = Schema("a", "b~3-9", "a");
        var properties = EvidenceOf(schema)["properties"]!;

        Assert.Equal(["a", "b~3-9"], Names(SourceOf(schema)["properties"]!["rangeKey"]!["enum"]));
        Assert.Equal(Enum.GetNames<EvidenceKind>(), Names(properties["kind"]!["enum"]));
        Assert.Equal(Enum.GetNames<EvidencePolarity>(), Names(properties["polarity"]!["enum"]));
        Assert.Equal(Enum.GetNames<EvidenceCertainty>(), Names(properties["certainty"]!["enum"]));
        Assert.Equal(Enum.GetNames<QualifierType>(), Names(properties["qualifiers"]!["items"]!["properties"]!["type"]!["enum"]));
        Assert.Equal(Enum.GetNames<EvidenceRelationType>(), Names(properties["relations"]!["items"]!["properties"]!["type"]!["enum"]));
        Assert.Equal(["evidence-1.1.0"], Names(schema["properties"]!["schemaVersion"]!["enum"]));
        Assert.Throws<ArgumentException>(() => DocumentEvidenceSchema.ForUnit([]));
    }

    [Fact]
    public void An_output_that_the_decoder_accepts_uses_only_properties_and_values_the_schema_allows()
    {
        var output = new DocumentExtractionOutput
        {
            SchemaVersion = "evidence-1.1.0",
            Evidences =
            [
                new DocumentExtractedEvidence
                {
                    Key = "e1",
                    Sources = [new DocumentPassageCandidateSource { RangeKey = "r1", Quote = "Cliente A", Occurrence = 1, Role = EvidenceSourceRole.Primary, CitationGroupKey = "g1", Part = 0 }],
                    Kind = EvidenceKind.Constraint, KindRationale = null, Statement = "Massimo 5 ordini.", Subject = "Cliente A", Action = null, Object = null, Scope = null,
                    Purpose = null, Instrument = null, Channel = null, Recipient = null, Qualifiers = [new ExtractedQualifier { Type = QualifierType.Maximum, Text = "5" }],
                    Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Relations = [],
                },
            ],
            NoEvidenceReason = null,
        };
        var json = JsonNode.Parse(DocumentEvidenceCodec.EncodeOutput(output))!.AsObject();
        var schema = Schema();

        Assert.Equal(schema["properties"]!.AsObject().Select(p => p.Key).Order(), json.Select(p => p.Key).Order());
        var evidence = json["evidences"]![0]!.AsObject();
        Assert.Equal(EvidenceOf(schema)["properties"]!.AsObject().Select(p => p.Key).Order(), evidence.Select(p => p.Key).Order());
        Assert.Contains(evidence["kind"]!.GetValue<string>(), Names(EvidenceOf(schema)["properties"]!["kind"]!["enum"]));
        Assert.Contains(evidence["sources"]![0]!["rangeKey"]!.GetValue<string>(), Names(SourceOf(schema)["properties"]!["rangeKey"]!["enum"]));
        Assert.Single(DocumentEvidenceCodec.DecodeOutput(DocumentEvidenceCodec.EncodeOutput(output)).Evidences);
    }
}
