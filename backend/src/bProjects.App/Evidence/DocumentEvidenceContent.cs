using System.Text.Json;
using System.Text.Json.Serialization;

namespace bProjects.App.Evidence;

// Content persistito `evidence-1.1.0` (contracts/ai-workflow.md §2.2): sources discriminate, tutte le proprietà obbligatorie, XOR fra
// campi del messaggio e campi documentali. Lo stesso ledger, nessun secondo registro: il Content 1.0 storico non viene riscritto né
// letto con questo decoder; la versione di schema della riga EvidenceExtractionVersion seleziona il decoder (EvidenceSchemaDispatch).

public enum EvidenceSourceKind
{
    Message,
    DocumentPassage,
}

internal sealed class DocumentEvidenceSchemaVersionConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() == DocumentEvidenceContent.SchemaVersionValue
            ? DocumentEvidenceContent.SchemaVersionValue
            : throw new JsonException($"schemaVersion deve essere \"{DocumentEvidenceContent.SchemaVersionValue}\".");

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentEvidenceStructure
{
    public required string? Subject { get; init; }
    public required string? Action { get; init; }
    public required string? Object { get; init; }
    public required string? Scope { get; init; }
    public required string? Purpose { get; init; }
    public required string? Instrument { get; init; }
    public required string? Channel { get; init; }
    public required string? Recipient { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentEvidenceQualifier
{
    public required QualifierType Type { get; init; }
    public required string Text { get; init; }
}

/// <summary>
/// Fonte persistita. Message: <c>MessageId</c>/<c>Sequence</c> valorizzati e campi documentali null; DocumentPassage: tutti i campi
/// documentali valorizzati e <c>MessageId</c>/<c>Sequence</c> null. <c>Start</c>/<c>End</c> sono UTF-16 [start,end) sul messaggio originale
/// o sul CanonicalText del blocco; <c>Quote</c> è la sottostringa esatta. UUID generati dal server, mai dal modello.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersistedSource
{
    public required Guid SourceId { get; init; }
    public required EvidenceSourceKind Kind { get; init; }
    public required EvidenceSourceRole Role { get; init; }
    public required Guid CitationGroupId { get; init; }
    public required int Part { get; init; }
    public required Guid? MessageId { get; init; }
    public required long? Sequence { get; init; }
    public required Guid? DocumentId { get; init; }
    public required Guid? DocumentVersionId { get; init; }
    public required Guid? PreparationId { get; init; }
    public required Guid? ArtifactId { get; init; }
    public required Guid? BlockId { get; init; }
    public required int Start { get; init; }
    public required int End { get; init; }
    public required string Quote { get; init; }
}

/// <summary>Contenuto JSONB di <see cref="EvidenceItem.Content"/> per le righe con schema `evidence-1.1.0`.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentEvidenceContent
{
    public const string SchemaVersionValue = "evidence-1.1.0";

    [JsonConverter(typeof(DocumentEvidenceSchemaVersionConverter))]
    public required string SchemaVersion { get; init; }
    public required string? KindRationale { get; init; }
    public required DocumentEvidenceStructure Structure { get; init; }
    public required IReadOnlyList<DocumentEvidenceQualifier> Qualifiers { get; init; }
    public required IReadOnlyList<PersistedSource> Sources { get; init; }
}

/// <summary>Regole strutturali di una fonte persistita (verificabili senza leggere l'originale).</summary>
public static class PersistedSourceRules
{
    public static IReadOnlyList<string> Check(IReadOnlyList<PersistedSource> sources)
    {
        var violations = new List<string>();
        var ids = new HashSet<Guid>();
        var parts = new HashSet<(Guid, int)>();
        for (var i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            var path = $"sources[{i}]";
            if (!ids.Add(s.SourceId))
            {
                violations.Add($"{path}.sourceId: duplicato nella stessa evidenza.");
            }

            if (!parts.Add((s.CitationGroupId, s.Part)))
            {
                violations.Add($"{path}: coppia (citationGroupId, part) duplicata.");
            }

            if (s.Part < 0)
            {
                violations.Add($"{path}.part: minimo 0.");
            }

            var documentFields = new object?[] { s.DocumentId, s.DocumentVersionId, s.PreparationId, s.ArtifactId, s.BlockId };
            switch (s.Kind)
            {
                case EvidenceSourceKind.Message:
                    if (s.MessageId is null || s.Sequence is null or < 1 || documentFields.Any(f => f is not null))
                    {
                        violations.Add($"{path}: una fonte Message richiede messageId e sequence e campi documentali null.");
                    }

                    break;
                case EvidenceSourceKind.DocumentPassage:
                    if (s.MessageId is not null || s.Sequence is not null || documentFields.Any(f => f is null))
                    {
                        violations.Add($"{path}: una fonte DocumentPassage richiede tutti i campi documentali e messageId/sequence null.");
                    }

                    if (s.Role == EvidenceSourceRole.Referent)
                    {
                        violations.Add($"{path}.role: una fonte DocumentPassage è Primary o Context.");
                    }

                    break;
                default:
                    violations.Add($"{path}.kind: sconosciuto.");
                    break;
            }

            if (!Enum.IsDefined(s.Role))
            {
                violations.Add($"{path}.role: sconosciuto.");
            }

            if (s.Start < 0 || s.End <= s.Start)
            {
                violations.Add($"{path}: range [start,end) non valido (start>=0, end>start).");
            }
            else if (string.IsNullOrWhiteSpace(s.Quote) || s.Quote.Length != s.End - s.Start)
            {
                violations.Add($"{path}.quote: non vuota e lunga quanto il range UTF-16.");
            }
        }

        return violations;
    }
}

/// <summary>Codec strict di `evidence-1.1.0`: tutto ciò che non è esattamente il contratto è un <see cref="JsonException"/>.</summary>
public static class DocumentEvidenceCodec
{
    /// <summary>Opzioni del registro con numeri rigorosi: un intero fra virgolette non è accettato.</summary>
    public static JsonSerializerOptions Options { get; } = new(EvidenceJson.Options) { NumberHandling = JsonNumberHandling.Strict };

    public static DocumentExtractionOutput DecodeOutput(string json) =>
        JsonSerializer.Deserialize<DocumentExtractionOutput>(json, Options) ?? throw new JsonException("Output nullo.");

    public static string EncodeOutput(DocumentExtractionOutput output) => JsonSerializer.Serialize(output, Options);

    public static string EncodeContent(DocumentEvidenceContent content) => JsonSerializer.Serialize(content, Options);

    public static DocumentEvidenceContent DecodeContent(string json)
    {
        var content = JsonSerializer.Deserialize<DocumentEvidenceContent>(json, Options) ?? throw new JsonException("Content nullo.");
        var violations = PersistedSourceRules.Check(content.Sources);
        return violations.Count == 0
            ? content
            : throw new JsonException("Content evidence-1.1.0 non coerente: " + string.Join(" | ", violations));
    }
}

public sealed record DecodedEvidenceContent(EvidenceContent? Legacy, DocumentEvidenceContent? Document);

/// <summary>
/// Selezione del decoder per la versione di schema registrata sulla riga (<see cref="EvidenceExtractionVersion.SchemaVersion"/>).
/// Nessun decoder permissivo: una versione ignota è rifiutata esplicitamente, il JSON storico non è mai reinterpretato come 1.1.
/// </summary>
public static class EvidenceSchemaDispatch
{
    public static DecodedEvidenceContent DecodeContent(string schemaVersion, string json) => schemaVersion switch
    {
        EvidenceExtractionVersion.CurrentSchemaVersion => new(EvidenceJson.Deserialize<EvidenceContent>(json), null),
        DocumentEvidenceContent.SchemaVersionValue => new(null, DocumentEvidenceCodec.DecodeContent(json)),
        _ => throw new NotSupportedException($"Versione di schema Evidence non supportata: «{schemaVersion}»."),
    };
}
