using System.Text.Json;
using System.Text.Json.Nodes;

namespace bProjects.App.Evidence;

/// <summary>
/// JSON Schema (strict) dell'uscita `evidence-1.1.0` inviato al modello per UNA unità di estrazione documentale
/// (contracts/ai-workflow.md §2.1). È scritto a mano perché la fonte è un'unione discriminata con decodifica manuale: un generatore
/// da tipi produrrebbe uno schema aperto. Ogni oggetto ha <c>additionalProperties: false</c> e tutte le proprietà obbligatorie; le
/// <c>rangeKey</c> ammesse sono un enum dei soli intervalli dell'unità (il modello non fornisce UUID, locator né offset). Lo schema
/// vincola la forma, ma non sostituisce il validatore deterministico (<see cref="DocumentEvidenceValidator"/>): citazioni, ruoli e
/// gruppi si verificano sempre sul testo reale. Nessun tetto di lunghezza o di numero di evidenze: l'unità si divide prima della
/// chiamata (planner), mai tagliando l'elenco.
/// </summary>
public static class DocumentEvidenceSchema
{
    public const string Name = "document_evidence_extraction";

    private const string KeyPattern = "^e[1-9][0-9]{0,2}$";

    /// <summary>Schema dell'unità: <paramref name="rangeKeys"/> sono le sole <c>rangeKey</c> citabili (proprietari e di contesto).</summary>
    public static JsonElement ForUnit(IEnumerable<string> rangeKeys)
    {
        var keys = rangeKeys.Distinct(StringComparer.Ordinal).Select(k => (JsonNode)JsonValue.Create(k)!).ToArray();
        if (keys.Length == 0)
        {
            throw new ArgumentException("Un'unità senza intervalli non ha uno schema.", nameof(rangeKeys));
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = Names("schemaVersion", "evidences", "noEvidenceReason"),
            ["properties"] = new JsonObject
            {
                ["schemaVersion"] = new JsonObject { ["type"] = "string", ["enum"] = Names(DocumentEvidenceContent.SchemaVersionValue) },
                ["evidences"] = new JsonObject { ["type"] = "array", ["items"] = Evidence(new JsonArray(keys)) },
                ["noEvidenceReason"] = NullableString(),
            },
        };
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>Nomi di proprietà richiesti per ciascun livello (stessa fonte del test di allineamento con i DTO).</summary>
    public static IReadOnlyList<string> EvidenceProperties { get; } =
    [
        "key", "sources", "kind", "kindRationale", "statement", "subject", "action", "object", "scope", "purpose", "instrument", "channel",
        "recipient", "qualifiers", "polarity", "certainty", "relations",
    ];

    private static JsonObject Evidence(JsonArray rangeKeys) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = Names(EvidenceProperties.ToArray()),
        ["properties"] = new JsonObject
        {
            ["key"] = new JsonObject { ["type"] = "string", ["pattern"] = KeyPattern },
            ["sources"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["items"] = Source(rangeKeys) },
            ["kind"] = EnumOf<EvidenceKind>(),
            ["kindRationale"] = NullableString(),
            ["statement"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
            ["subject"] = NullableString(),
            ["action"] = NullableString(),
            ["object"] = NullableString(),
            ["scope"] = NullableString(),
            ["purpose"] = NullableString(),
            ["instrument"] = NullableString(),
            ["channel"] = NullableString(),
            ["recipient"] = NullableString(),
            ["qualifiers"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = Names("type", "text"),
                    ["properties"] = new JsonObject
                    {
                        ["type"] = EnumOf<QualifierType>(),
                        ["text"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                    },
                },
            },
            ["polarity"] = EnumOf<EvidencePolarity>(),
            ["certainty"] = EnumOf<EvidenceCertainty>(),
            ["relations"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = Names("type", "target", "basis", "rationale"),
                    ["properties"] = new JsonObject
                    {
                        ["type"] = EnumOf<EvidenceRelationType>(),
                        ["target"] = new JsonObject { ["type"] = "string", ["pattern"] = KeyPattern },
                        ["basis"] = EnumOf<EvidenceRelationBasis>(),
                        ["rationale"] = NullableString(),
                    },
                },
            },
        },
    };

    /// <summary>Solo la variante DocumentPassage: un'unità documentale non riceve messaggi e quindi non può citarli.</summary>
    private static JsonObject Source(JsonArray rangeKeys) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = Names("kind", "rangeKey", "quote", "occurrence", "role", "citationGroupKey", "part"),
        ["properties"] = new JsonObject
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = Names("DocumentPassage") },
            ["rangeKey"] = new JsonObject { ["type"] = "string", ["enum"] = rangeKeys },
            ["quote"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
            ["occurrence"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
            ["role"] = new JsonObject { ["type"] = "string", ["enum"] = Names("Primary", "Context") },
            ["citationGroupKey"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
            ["part"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
        },
    };

    private static JsonObject NullableString() => new() { ["type"] = new JsonArray("string", "null") };

    private static JsonObject EnumOf<T>()
        where T : struct, Enum => new() { ["type"] = "string", ["enum"] = Names(Enum.GetNames<T>()) };

    private static JsonArray Names(params string[] names) => new(names.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
}
