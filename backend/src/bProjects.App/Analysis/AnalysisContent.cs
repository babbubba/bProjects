using System.Text.Json;
using System.Text.Json.Serialization;

namespace bProjects.App.Analysis;

// DTO tipizzati AnalysisContent 1.0.0 (contracts/ai-workflow.md §4). Rispecchiano required/tipi/
// additionalProperties=false del JSON Schema normativo; le regole semantiche (minLength non-whitespace,
// unicità, refs risolti, minimum, minItems, uniqueItems) sono del validatore (T027). Nessuna entità KB.

public enum AnalysisContentState { Unvalidated, PmConfirmed }

public enum AnalysisClassification { Declared, Inferred, Assumed }

public enum AnalysisSourceKind { Message, PmRevision }

public enum AnalysisMessageRole
{
    [JsonStringEnumMemberName("user")] User,
    [JsonStringEnumMemberName("assistant")] Assistant,
}

public enum AnalysisConflictStatus { Open, Resolved }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisItem
{
    public required Guid Id { get; init; }
    public required string Text { get; init; }
    public required AnalysisClassification Classification { get; init; }
    public required IReadOnlyList<Guid> EvidenceRefs { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisSource
{
    public required Guid Id { get; init; }
    public required AnalysisSourceKind Kind { get; init; }
    public required Guid? MessageId { get; init; }
    public required AnalysisMessageRole? MessageRole { get; init; }
    public required Guid? RevisionId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisConflictResolution
{
    public required IReadOnlyList<Guid> SelectedAlternativeIds { get; init; }
    public required string Reason { get; init; }
    public required IReadOnlyList<Guid> EvidenceRefs { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisConflict
{
    public required Guid Id { get; init; }
    public required string Description { get; init; }
    public required AnalysisConflictStatus Status { get; init; }
    public required IReadOnlyList<AnalysisItem> Alternatives { get; init; }
    public required AnalysisConflictResolution? Resolution { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisContextAndScope
{
    public required IReadOnlyList<AnalysisItem> Overview { get; init; }
    public required IReadOnlyList<AnalysisItem> Included { get; init; }
    public required IReadOnlyList<AnalysisItem> Excluded { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisContent
{
    public const string CurrentSchemaVersion = "1.0.0";

    [JsonConverter(typeof(SchemaVersionConverter))]
    public required string SchemaVersion { get; init; }
    public required Guid AnalysisId { get; init; }
    public required Guid ProjectId { get; init; }
    public required int SourceCutoffSequence { get; init; }
    public required AnalysisContentState ValidationState { get; init; }
    public required IReadOnlyList<AnalysisSource> Sources { get; init; }
    public required IReadOnlyList<AnalysisItem> Goals { get; init; }
    public required AnalysisContextAndScope ContextAndScope { get; init; }
    public required IReadOnlyList<AnalysisItem> Requirements { get; init; }
    public required IReadOnlyList<AnalysisItem> OperationalFlows { get; init; }
    public required IReadOnlyList<AnalysisItem> Constraints { get; init; }
    public required IReadOnlyList<AnalysisItem> Dependencies { get; init; }
    public required IReadOnlyList<AnalysisItem> Assumptions { get; init; }
    public required IReadOnlyList<AnalysisItem> OpenQuestions { get; init; }
    public required IReadOnlyList<AnalysisConflict> Conflicts { get; init; }
}

/// <summary>schemaVersion: const "1.0.0".</summary>
internal sealed class SchemaVersionConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || reader.GetString() != AnalysisContent.CurrentSchemaVersion)
        {
            throw new JsonException($"schemaVersion deve essere \"{AnalysisContent.CurrentSchemaVersion}\".");
        }

        return AnalysisContent.CurrentSchemaVersion;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>Opzioni JSON strette: camelCase, enum solo per nome, null/mancanti/extra rifiutati.</summary>
public static class AnalysisContentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public static AnalysisContent Deserialize(string json) =>
        JsonSerializer.Deserialize<AnalysisContent>(json, Options) ?? throw new JsonException("AnalysisContent nullo.");

    public static string Serialize(AnalysisContent content) => JsonSerializer.Serialize(content, Options);
}
