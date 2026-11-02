using System.Text.Json;
using System.Text.Json.Serialization;

namespace bProjects.App.Evidence;

// Uscita del modello per il contratto `evidence-1.1.0` (contracts/ai-workflow.md §2.1). DTO e decoder distinti da quelli di
// `evidence-1.0.0` (EvidenceExtraction.cs), che restano invariati. Identità, UUID documentali, locator, bbox e offset non sono mai
// proprietà dell'output: il modello indica solo la `rangeKey` assegnata dal server, la citazione e l'occorrenza.

/// <summary>Uscita per unità: `{schemaVersion, evidences, noEvidenceReason}`, tutti i campi obbligatori, nessun extra.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentExtractionOutput
{
    [JsonConverter(typeof(DocumentEvidenceSchemaVersionConverter))]
    public required string SchemaVersion { get; init; }
    public required IReadOnlyList<DocumentExtractedEvidence> Evidences { get; init; }
    public required string? NoEvidenceReason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentExtractedEvidence
{
    public required string Key { get; init; }
    public required IReadOnlyList<CandidateSource> Sources { get; init; }
    public required EvidenceKind Kind { get; init; }
    public required string? KindRationale { get; init; }
    public required string Statement { get; init; }
    public required string? Subject { get; init; }
    public required string? Action { get; init; }
    public required string? Object { get; init; }
    public required string? Scope { get; init; }
    public required string? Purpose { get; init; }
    public required string? Instrument { get; init; }
    public required string? Channel { get; init; }
    public required string? Recipient { get; init; }
    public required IReadOnlyList<ExtractedQualifier> Qualifiers { get; init; }
    public required EvidencePolarity Polarity { get; init; }
    public required EvidenceCertainty Certainty { get; init; }
    public required IReadOnlyList<ExtractedRelation> Relations { get; init; }
}

/// <summary>
/// Unione discriminata chiusa (`kind`): ogni variante ha esattamente i propri campi, nessun campo dell'altra. La decodifica è
/// manuale per rifiutare proprietà extra, duplicate, null e valori numerici dove è atteso un enum.
/// </summary>
[JsonConverter(typeof(CandidateSourceConverter))]
public abstract record CandidateSource
{
    public required string Quote { get; init; }
    public required int Occurrence { get; init; }
    public required EvidenceSourceRole Role { get; init; }
    public required string CitationGroupKey { get; init; }
    public required int Part { get; init; }
}

/// <summary>Messaggio fornito all'unità: `messageId` ammesso dal server; assistant solo Referent.</summary>
public sealed record MessageCandidateSource : CandidateSource
{
    public required Guid MessageId { get; init; }
}

/// <summary>Intervallo del manifest server dell'unità, identificato dalla sola `rangeKey`.</summary>
public sealed record DocumentPassageCandidateSource : CandidateSource
{
    public required string RangeKey { get; init; }
}

internal sealed class CandidateSourceConverter : JsonConverter<CandidateSource>
{
    private const string Message = "Message";
    private const string DocumentPassage = "DocumentPassage";

    public override CandidateSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("La fonte deve essere un oggetto.");
        }

        string? kind = null, rangeKey = null, quote = null, group = null;
        Guid? messageId = null;
        int? occurrence = null, part = null;
        EvidenceSourceRole? role = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString()!;
            if (!seen.Add(name))
            {
                throw new JsonException($"Proprietà duplicata della fonte: «{name}».");
            }

            reader.Read();
            switch (name)
            {
                case "kind": kind = ReadString(ref reader, name); break;
                case "rangeKey": rangeKey = ReadString(ref reader, name); break;
                case "quote": quote = ReadString(ref reader, name); break;
                case "citationGroupKey": group = ReadString(ref reader, name); break;
                case "messageId": messageId = JsonSerializer.Deserialize<Guid>(ref reader, options); break;
                case "occurrence": occurrence = ReadInt(ref reader, name); break;
                case "part": part = ReadInt(ref reader, name); break;
                case "role": role = JsonSerializer.Deserialize<EvidenceSourceRole>(ref reader, options); break;
                default: throw new JsonException($"Proprietà non ammessa nella fonte: «{name}».");
            }
        }

        Require(kind, "kind");
        Require(quote, "quote");
        Require(occurrence, "occurrence");
        Require(role, "role");
        Require(group, "citationGroupKey");
        Require(part, "part");
        switch (kind)
        {
            case Message:
                Forbid(rangeKey, "rangeKey", Message);
                Require(messageId, "messageId");
                return new MessageCandidateSource
                {
                    MessageId = messageId!.Value, Quote = quote!, Occurrence = occurrence!.Value, Role = role!.Value, CitationGroupKey = group!, Part = part!.Value,
                };
            case DocumentPassage:
                Forbid(messageId, "messageId", DocumentPassage);
                Require(rangeKey, "rangeKey");
                if (role == EvidenceSourceRole.Referent)
                {
                    throw new JsonException("Una fonte DocumentPassage è Primary o Context, mai Referent.");
                }

                return new DocumentPassageCandidateSource
                {
                    RangeKey = rangeKey!, Quote = quote!, Occurrence = occurrence!.Value, Role = role!.Value, CitationGroupKey = group!, Part = part!.Value,
                };
            default:
                throw new JsonException($"Tipo di fonte non ammesso: «{kind}».");
        }
    }

    public override void Write(Utf8JsonWriter writer, CandidateSource value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case MessageCandidateSource message:
                writer.WriteString("kind", Message);
                writer.WriteString("messageId", message.MessageId);
                break;
            case DocumentPassageCandidateSource passage:
                writer.WriteString("kind", DocumentPassage);
                writer.WriteString("rangeKey", passage.RangeKey);
                break;
            default:
                throw new JsonException("Fonte sconosciuta.");
        }

        writer.WriteString("quote", value.Quote);
        writer.WriteNumber("occurrence", value.Occurrence);
        writer.WritePropertyName("role");
        JsonSerializer.Serialize(writer, value.Role, options);
        writer.WriteString("citationGroupKey", value.CitationGroupKey);
        writer.WriteNumber("part", value.Part);
        writer.WriteEndObject();
    }

    private static string ReadString(ref Utf8JsonReader reader, string name) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw new JsonException($"«{name}» deve essere una stringa non nulla.");

    private static int ReadInt(ref Utf8JsonReader reader, string name) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) ? value : throw new JsonException($"«{name}» deve essere un intero.");

    private static void Require<T>(T? value, string name)
    {
        if (value is null)
        {
            throw new JsonException($"Proprietà obbligatoria mancante della fonte: «{name}».");
        }
    }

    private static void Forbid<T>(T? value, string name, string kind)
    {
        if (value is not null)
        {
            throw new JsonException($"«{name}» non è ammessa in una fonte {kind}.");
        }
    }
}
