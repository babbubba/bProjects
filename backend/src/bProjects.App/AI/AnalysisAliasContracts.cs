using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// DTO di output AI `document-analysis-alias-1` (contracts/ai-workflow.md §7.4, T125): solo contenuto semantico con alias brevi, mai UUID, hash,
// id di run/batch/progetto, Source o elenchi completi di voci visitate. Distinto dal canonico persistito (AnalysisContent 1.1.0 / SingleCallOutput):
// il compositore (AnalysisAliasComposer) risolve gli alias contro il catalogo congelato. Nessun database, nessun LLM.

public enum AiFindingOutcome { Compared, Open }

public enum AiKnownRelationVerdict { Confirmed, NoRelation, OpenForPM }

/// <summary>Formato esatto degli alias e delle chiavi locali: lettera maiuscola/minuscola fissa + intero decimale positivo senza zeri iniziali, nessuna normalizzazione.</summary>
public static class AnalysisAliasPattern
{
    public const string Evidence = "^E[1-9][0-9]*$";
    public const string Source = "^S[1-9][0-9]*$";
    public const string Relation = "^R[1-9][0-9]*$";
    public const string ItemKey = "^i[1-9][0-9]*$";
    public const string ConflictKey = "^c[1-9][0-9]*$";
    public const string FindingKey = "^f[1-9][0-9]*$";
    public const string PatchKey = "^p[1-9][0-9]*$";

    private const int MaxLength = 10;

    /// <summary>Controllo manuale (niente regex: in .NET `$` ammette un «\n» finale). Case-sensitive, solo cifre ASCII, primo numero non zero.</summary>
    public static bool Matches(string? value, char prefix) =>
        value is { Length: >= 2 and <= MaxLength } && value[0] == prefix && value[1] is >= '1' and <= '9' && value.AsSpan(2).ToString().All(char.IsAsciiDigit);

    public static int Number(string alias) => int.Parse(alias.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
}

public abstract class AliasStringConverter(char prefix) : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return AnalysisAliasPattern.Matches(text, prefix) ? text! : throw new JsonException($"Formato non canonico: atteso {prefix}<n> con n intero positivo senza zeri iniziali.");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public abstract class AliasListConverter(char prefix) : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Atteso un array di alias {prefix}<n>.");
        }

        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            list.Add(AnalysisAliasPattern.Matches(text, prefix) ? text! : throw new JsonException($"Formato non canonico: atteso {prefix}<n> con n intero positivo senza zeri iniziali."));
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}

public sealed class EvidenceAliasConverter() : AliasStringConverter('E');

public sealed class RelationAliasConverter() : AliasStringConverter('R');

public sealed class ItemKeyConverter() : AliasStringConverter('i');

public sealed class ConflictKeyConverter() : AliasStringConverter('c');

public sealed class FindingKeyConverter() : AliasStringConverter('f');

public sealed class PatchKeyConverter() : AliasStringConverter('p');

public sealed class EvidenceAliasListConverter() : AliasListConverter('E');

/// <summary>`contract` è la costante «alias-1».</summary>
public sealed class AliasContractConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() == AnalysisAliasContracts.ContractValue
            ? AnalysisAliasContracts.ContractValue
            : throw new JsonException($"contract deve essere \"{AnalysisAliasContracts.ContractValue}\".");

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>`contract` è la costante «alias-2» (T135, contracts/ai-workflow.md §13.2).</summary>
public sealed class AliasContractV2Converter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() == AnalysisAliasContracts.ContractValueV2
            ? AnalysisAliasContracts.ContractValueV2
            : throw new JsonException($"contract deve essere \"{AnalysisAliasContracts.ContractValueV2}\".");

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public enum GapOutcome { AnsweredInSnapshot, Open }

public enum GapReason { Answered, NoAnswer, Partial, Ambiguous }

/// <summary>
/// Record `gap-assessment-1` (§13.2): valutazione emessa dal modello per un'evidenza Gap dello snapshot assegnato. Interpretazione AI non validata
/// semanticamente: il server verifica solo forma e riferimenti (<see cref="GapAssessmentRules"/>), mai pertinenza o prossimità.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiGapAssessment
{
    [JsonConverter(typeof(EvidenceAliasConverter))]
    public required string Gap { get; init; }

    public required GapOutcome Outcome { get; init; }
    public required GapReason Reason { get; init; }
    public required string Rationale { get; init; }

    [JsonConverter(typeof(EvidenceAliasListConverter))]
    public required IReadOnlyList<string> SupportingEvidence { get; init; }

    [JsonConverter(typeof(ItemKeyConverter))]
    public required string? OpenItem { get; init; }

    [JsonConverter(typeof(ConflictKeyConverter))]
    public required string? OpenConflict { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiVisited
{
    [JsonPropertyName("A"), JsonConverter(typeof(EvidenceAliasListConverter))]
    public required IReadOnlyList<string> A { get; init; }

    [JsonPropertyName("B"), JsonConverter(typeof(EvidenceAliasListConverter))]
    public required IReadOnlyList<string> B { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiNoFindings
{
    [JsonPropertyName("A")]
    public required string? A { get; init; }

    [JsonPropertyName("B")]
    public required string? B { get; init; }

    [JsonPropertyName("Cross")]
    public required string? Cross { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiItem
{
    [JsonConverter(typeof(ItemKeyConverter))]
    public required string Key { get; init; }

    public required string Text { get; init; }
    public required DocumentAnalysisClassification Classification { get; init; }

    [JsonConverter(typeof(EvidenceAliasListConverter))]
    public required IReadOnlyList<string> Evidence { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiItems
{
    public required IReadOnlyList<AiItem> Goals { get; init; }
    public required IReadOnlyList<AiItem> ScopeOverview { get; init; }
    public required IReadOnlyList<AiItem> ScopeIncluded { get; init; }
    public required IReadOnlyList<AiItem> ScopeExcluded { get; init; }
    public required IReadOnlyList<AiItem> Requirements { get; init; }
    public required IReadOnlyList<AiItem> Flows { get; init; }
    public required IReadOnlyList<AiItem> Constraints { get; init; }
    public required IReadOnlyList<AiItem> Dependencies { get; init; }
    public required IReadOnlyList<AiItem> Assumptions { get; init; }
    public required IReadOnlyList<AiItem> OpenQuestions { get; init; }

    /// <summary>Sezioni in ordine canonico, con il nome di percorso usato nei messaggi di rifiuto.</summary>
    public IEnumerable<(string Name, IReadOnlyList<AiItem> Items)> Sections()
    {
        yield return ("goals", Goals);
        yield return ("scopeOverview", ScopeOverview);
        yield return ("scopeIncluded", ScopeIncluded);
        yield return ("scopeExcluded", ScopeExcluded);
        yield return ("requirements", Requirements);
        yield return ("flows", Flows);
        yield return ("constraints", Constraints);
        yield return ("dependencies", Dependencies);
        yield return ("assumptions", Assumptions);
        yield return ("openQuestions", OpenQuestions);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiConflict
{
    [JsonConverter(typeof(ConflictKeyConverter))]
    public required string Key { get; init; }

    public required string Description { get; init; }
    public required IReadOnlyList<AiItem> Alternatives { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiFinding
{
    [JsonConverter(typeof(FindingKeyConverter))]
    public required string Key { get; init; }

    public required ReconcileView View { get; init; }

    [JsonConverter(typeof(EvidenceAliasConverter))]
    public required string Left { get; init; }

    [JsonConverter(typeof(EvidenceAliasConverter))]
    public required string Right { get; init; }

    public required EvidenceRelationType Type { get; init; }
    public required EvidenceRelationBasis Basis { get; init; }
    public required string Rationale { get; init; }
    public required AiFindingOutcome Outcome { get; init; }
    public required string? Reason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiKnownRelationCheck
{
    [JsonConverter(typeof(RelationAliasConverter))]
    public required string Relation { get; init; }

    public required AiKnownRelationVerdict Verdict { get; init; }
    public required string Rationale { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiPatchTarget
{
    [JsonConverter(typeof(ItemKeyConverter))]
    public required string? Item { get; init; }

    [JsonConverter(typeof(RelationAliasConverter))]
    public required string? Relation { get; init; }
}

/// <summary>Union strict del valore di una patch: il discriminante è <see cref="AiPatch.Op"/>.</summary>
public abstract record AiPatchValue;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiAddSupportValue : AiPatchValue;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiAddRelationValue : AiPatchValue
{
    [JsonConverter(typeof(EvidenceAliasConverter))]
    public required string From { get; init; }

    [JsonConverter(typeof(EvidenceAliasConverter))]
    public required string To { get; init; }

    public required EvidenceRelationType Type { get; init; }
    public required EvidenceRelationBasis Basis { get; init; }
    public required string? Rationale { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiProposeAlternativeValue : AiPatchValue
{
    public required string Text { get; init; }
    public required DocumentAnalysisClassification Classification { get; init; }
    public required string Reason { get; init; }
}

[JsonConverter(typeof(AiPatchConverter))]
public sealed record AiPatch
{
    public required string Key { get; init; }
    public required PatchOperation Op { get; init; }
    public required AiPatchTarget Target { get; init; }
    public required IReadOnlyList<string> Evidence { get; init; }
    public required AiPatchValue Value { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiAnalysisOutput
{
    [JsonConverter(typeof(AliasContractConverter))]
    public required string Contract { get; init; }

    public required AiVisited Visited { get; init; }
    public required AiNoFindings NoFindings { get; init; }
    public required AiItems Items { get; init; }
    public required IReadOnlyList<AiConflict> Conflicts { get; init; }
    public required IReadOnlyList<AiFinding> Findings { get; init; }
    public required IReadOnlyList<AiKnownRelationCheck> KnownRelationChecks { get; init; }
    public required IReadOnlyList<AiPatch> Patches { get; init; }
}

/// <summary>Output AI `document-analysis-alias-2` (§13.2): gli otto campi di alias-1 con <c>contract: "alias-2"</c> più <c>gapAssessments</c>, obbligatoria anche vuota.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiAnalysisOutputV2
{
    [JsonConverter(typeof(AliasContractV2Converter))]
    public required string Contract { get; init; }

    public required AiVisited Visited { get; init; }
    public required AiNoFindings NoFindings { get; init; }
    public required AiItems Items { get; init; }
    public required IReadOnlyList<AiConflict> Conflicts { get; init; }
    public required IReadOnlyList<AiFinding> Findings { get; init; }
    public required IReadOnlyList<AiKnownRelationCheck> KnownRelationChecks { get; init; }
    public required IReadOnlyList<AiPatch> Patches { get; init; }
    public required IReadOnlyList<AiGapAssessment> GapAssessments { get; init; }
}

/// <summary>Patch con value union discriminato da `op`: esattamente key, op, target, evidence, value; nessuna proprietà extra o duplicata.</summary>
internal sealed class AiPatchConverter : JsonConverter<AiPatch>
{
    private static readonly string[] Names = ["key", "op", "target", "evidence", "value"];

    public override AiPatch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Patch: oggetto richiesto.");
        }

        var names = root.EnumerateObject().Select(p => p.Name).ToList();
        if (names.Count != Names.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Count || Names.Any(n => !names.Contains(n)))
        {
            throw new JsonException("Patch: proprietà esatte key, op, target, evidence, value.");
        }

        var key = root.GetProperty("key");
        if (key.ValueKind != JsonValueKind.String || !AnalysisAliasPattern.Matches(key.GetString(), 'p'))
        {
            throw new JsonException("Patch.key: formato non canonico, atteso p<n>.");
        }

        var evidence = root.GetProperty("evidence");
        if (evidence.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Patch.evidence: array di alias E<n> richiesto.");
        }

        var aliases = new List<string>();
        foreach (var item in evidence.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            aliases.Add(AnalysisAliasPattern.Matches(text, 'E') ? text! : throw new JsonException("Patch.evidence: formato non canonico, atteso E<n>."));
        }

        var operation = root.GetProperty("op").Deserialize<PatchOperation>(options);
        var valueElement = root.GetProperty("value");
        if (valueElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Patch.value: oggetto richiesto.");
        }

        AiPatchValue value = operation switch
        {
            PatchOperation.AddSupport => valueElement.Deserialize<AiAddSupportValue>(options)!,
            PatchOperation.AddRelation => valueElement.Deserialize<AiAddRelationValue>(options)!,
            PatchOperation.ProposeAlternative => valueElement.Deserialize<AiProposeAlternativeValue>(options)!,
            _ => throw new JsonException("Patch.op sconosciuta."),
        };

        return new AiPatch
        {
            Key = key.GetString()!,
            Op = operation,
            Target = root.GetProperty("target").Deserialize<AiPatchTarget>(options) ?? throw new JsonException("Patch.target: null non ammesso."),
            Evidence = aliases,
            Value = value,
        };
    }

    public override void Write(Utf8JsonWriter writer, AiPatch value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("key", value.Key);
        writer.WritePropertyName("op");
        JsonSerializer.Serialize(writer, value.Op, options);
        writer.WritePropertyName("target");
        JsonSerializer.Serialize(writer, value.Target, options);
        writer.WriteStartArray("evidence");
        foreach (var alias in value.Evidence)
        {
            writer.WriteStringValue(alias);
        }

        writer.WriteEndArray();
        writer.WritePropertyName("value");
        JsonSerializer.Serialize(writer, value.Value, value.Value.GetType(), options);
        writer.WriteEndObject();
    }
}

/// <summary>Codec strict e JSON Schema di `document-analysis-alias-1`.</summary>
public static class AnalysisAliasContracts
{
    public const string SchemaVersion = "document-analysis-alias-1";
    public const string ContractValue = "alias-1";
    public const string SchemaVersionV2 = "document-analysis-alias-2";
    public const string ContractValueV2 = "alias-2";

    /// <summary>Come il codec `reconcile-1`: extra, duplicati, null non dichiarati, enum fuori dal set (anche per maiuscole) e numeri fra virgolette sono <see cref="JsonException"/>.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        Converters = { new ExactEnumConverterFactory() },
    };

    public static AiAnalysisOutput Decode(string json)
    {
        try
        {
            // Rifiuta i duplicati anche nei sotto-oggetti letti tramite JsonElement (patch).
            using (JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false }))
            {
            }

            return JsonSerializer.Deserialize<AiAnalysisOutput>(json, Options) ?? throw new JsonException("Valore nullo.");
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
        {
            throw new JsonException(e.Message, e);
        }
    }

    public static string Encode(AiAnalysisOutput output) => JsonSerializer.Serialize(output, Options);

    /// <summary>Forma canonica RFC 8785 del DTO.</summary>
    public static string Canonical(AiAnalysisOutput output) => CanonicalJson.Canonicalize(Encode(output));

    /// <summary>`aiOutputHash`: SHA-256 della forma canonica del DTO (non dell'OutputHash del WorkOutput che lo contiene).</summary>
    public static string Hash(AiAnalysisOutput output) => CanonicalJson.HashText(Canonical(output));

    /// <summary>Codec strict di `document-analysis-alias-2` (T135): stesse regole di <see cref="Decode"/> più la forma dei record `gap-assessment-1` (<see cref="GapAssessmentRules.ValidateShape"/>).</summary>
    public static AiAnalysisOutputV2 DecodeV2(string json)
    {
        try
        {
            using (JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false }))
            {
            }

            var output = JsonSerializer.Deserialize<AiAnalysisOutputV2>(json, Options) ?? throw new JsonException("Valore nullo.");
            GapAssessmentRules.ValidateShape(output.GapAssessments);
            return output;
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
        {
            throw new JsonException(e.Message, e);
        }
    }

    public static string Encode(AiAnalysisOutputV2 output) => JsonSerializer.Serialize(output, Options);

    public static string Canonical(AiAnalysisOutputV2 output) => CanonicalJson.Canonicalize(Encode(output));

    /// <summary>`aiOutputHash` del DTO alias-2: copre anche <c>gapAssessments</c>.</summary>
    public static string Hash(AiAnalysisOutputV2 output) => CanonicalJson.HashText(Canonical(output));

    private static readonly Lazy<JsonElement> SchemaV2 = new(() => BuildSchema(ContractValueV2, gapAssessments: true));

    /// <summary>JSON Schema strict di `document-analysis-alias-2`: quello di alias-1 con <c>contract</c> «alias-2» e <c>gapAssessments</c> richiesta.</summary>
    public static JsonElement JsonSchemaV2() => SchemaV2.Value;

    internal static JsonObject GapAssessmentSchema() => ObjectSchema(
        ("gap", AliasSchema(AnalysisAliasPattern.Evidence)), ("outcome", EnumSchema<GapOutcome>()), ("reason", EnumSchema<GapReason>()), ("rationale", TextSchema()),
        ("supportingEvidence", ArraySchema(AliasSchema(AnalysisAliasPattern.Evidence))), ("openItem", NullableAliasSchema(AnalysisAliasPattern.ItemKey)),
        ("openConflict", NullableAliasSchema(AnalysisAliasPattern.ConflictKey)));

    private static readonly Lazy<JsonElement> Schema = new(() => BuildSchema(ContractValue, gapAssessments: false));

    /// <summary>
    /// JSON Schema (strict, scritto a mano come gli altri contratti: il value di una patch è un'unione) da inviare al modello: ogni oggetto chiuso,
    /// ogni proprietà richiesta, alias con pattern esatto, nessun UUID o hash. Vincola la forma; la risoluzione degli alias è del compositore.
    /// </summary>
    public static JsonElement JsonSchema() => Schema.Value;

    internal static JsonObject ObjectSchema(params (string Name, JsonNode Schema)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
            required.Add(name);
        }

        return new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = required, ["properties"] = props };
    }

    internal static JsonObject TextSchema() => new() { ["type"] = "string" };

    internal static JsonObject NullableTextSchema() => new() { ["type"] = new JsonArray("string", "null") };

    internal static JsonObject AliasSchema(string pattern) => new() { ["type"] = "string", ["pattern"] = pattern };

    internal static JsonObject NullableAliasSchema(string pattern) => new() { ["type"] = new JsonArray("string", "null"), ["pattern"] = pattern };

    internal static JsonObject EnumSchema<T>() where T : struct, Enum =>
        new() { ["type"] = "string", ["enum"] = new JsonArray([.. Enum.GetNames<T>().Select(n => (JsonNode)JsonValue.Create(n)!)]) };

    internal static JsonObject ArraySchema(JsonNode items) => new() { ["type"] = "array", ["items"] = items };

    private static JsonElement BuildSchema(string contract, bool gapAssessments)
    {
        var evidenceList = ArraySchema(AliasSchema(AnalysisAliasPattern.Evidence));
        var item = ObjectSchema(
            ("key", AliasSchema(AnalysisAliasPattern.ItemKey)), ("text", TextSchema()), ("classification", EnumSchema<DocumentAnalysisClassification>()), ("evidence", evidenceList.DeepClone()));
        var items = ArraySchema(item);
        var relationValue = ObjectSchema(
            ("from", AliasSchema(AnalysisAliasPattern.Evidence)), ("to", AliasSchema(AnalysisAliasPattern.Evidence)), ("type", EnumSchema<EvidenceRelationType>()),
            ("basis", EnumSchema<EvidenceRelationBasis>()), ("rationale", NullableTextSchema()));
        var alternativeValue = ObjectSchema(("text", TextSchema()), ("classification", EnumSchema<DocumentAnalysisClassification>()), ("reason", TextSchema()));
        var patch = ObjectSchema(
            ("key", AliasSchema(AnalysisAliasPattern.PatchKey)), ("op", EnumSchema<PatchOperation>()),
            ("target", ObjectSchema(("item", NullableAliasSchema(AnalysisAliasPattern.ItemKey)), ("relation", NullableAliasSchema(AnalysisAliasPattern.Relation)))),
            ("evidence", evidenceList.DeepClone()),
            ("value", new JsonObject { ["anyOf"] = new JsonArray(ObjectSchema(), relationValue, alternativeValue) }));
        var schema = ObjectSchema(
            ("contract", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(contract) }),
            ("visited", ObjectSchema(("A", evidenceList.DeepClone()), ("B", evidenceList.DeepClone()))),
            ("noFindings", ObjectSchema(("A", NullableTextSchema()), ("B", NullableTextSchema()), ("Cross", NullableTextSchema()))),
            ("items", ObjectSchema(
                ("goals", items.DeepClone()), ("scopeOverview", items.DeepClone()), ("scopeIncluded", items.DeepClone()), ("scopeExcluded", items.DeepClone()),
                ("requirements", items.DeepClone()), ("flows", items.DeepClone()), ("constraints", items.DeepClone()), ("dependencies", items.DeepClone()),
                ("assumptions", items.DeepClone()), ("openQuestions", items.DeepClone()))),
            ("conflicts", ArraySchema(ObjectSchema(("key", AliasSchema(AnalysisAliasPattern.ConflictKey)), ("description", TextSchema()), ("alternatives", items.DeepClone())))),
            ("findings", ArraySchema(ObjectSchema(
                ("key", AliasSchema(AnalysisAliasPattern.FindingKey)), ("view", EnumSchema<ReconcileView>()), ("left", AliasSchema(AnalysisAliasPattern.Evidence)),
                ("right", AliasSchema(AnalysisAliasPattern.Evidence)), ("type", EnumSchema<EvidenceRelationType>()), ("basis", EnumSchema<EvidenceRelationBasis>()),
                ("rationale", TextSchema()), ("outcome", EnumSchema<AiFindingOutcome>()), ("reason", NullableTextSchema())))),
            ("knownRelationChecks", ArraySchema(ObjectSchema(
                ("relation", AliasSchema(AnalysisAliasPattern.Relation)), ("verdict", EnumSchema<AiKnownRelationVerdict>()), ("rationale", TextSchema())))),
            ("patches", ArraySchema(patch)));
        if (gapAssessments)
        {
            schema["properties"]!["gapAssessments"] = ArraySchema(GapAssessmentSchema());
            schema["required"]!.AsArray().Add("gapAssessments");
        }

        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Regole e canonicalizzazione dei record `gap-assessment-1` (T135, §13.2). Il server verifica solo forma e riferimenti: un record è interpretazione AI non
/// validata semanticamente, mai pertinenza, prossimità o correttezza del riscontro. Nessun matching lessicale.
/// </summary>
public static class GapAssessmentRules
{
    public const string Version = "gap-assessment-1";

    /// <summary>
    /// Forma autonoma dei record (decoder): matrice esito/motivo/riferimenti, motivo non vuoto, supporti distinti e diversi dal Gap, un solo record per Gap.
    /// Non richiede il catalogo; una violazione è <see cref="JsonException"/> come ogni altro rifiuto del codec strict.
    /// </summary>
    public static void ValidateShape(IReadOnlyList<AiGapAssessment> records)
    {
        var problems = ShapeViolations(records);
        if (problems.Count > 0)
        {
            throw new JsonException("gapAssessments non valide: " + string.Join(" | ", problems));
        }
    }

    /// <summary>
    /// Verifica i record contro l'input assegnato (alias→Kind): un record per ogni Gap, nessuno per altri tipi o fuori dall'input, supporti solo non-Gap
    /// dell'input (niente catene o cicli fra Gap), <c>openItem</c>/<c>openConflict</c> risolvibili fra le domande aperte e i conflitti dell'output.
    /// Include le violazioni di forma di <see cref="ValidateShape"/>. Elenco vuoto = nessuna violazione.
    /// </summary>
    public static IReadOnlyList<string> Check(
        IReadOnlyList<AiGapAssessment> records, IReadOnlyDictionary<string, EvidenceKind> assigned, IReadOnlyCollection<string> openQuestionKeys, IReadOnlyCollection<string> conflictKeys)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(assigned);
        ArgumentNullException.ThrowIfNull(openQuestionKeys);
        ArgumentNullException.ThrowIfNull(conflictKeys);

        var problems = ShapeViolations(records);
        foreach (var record in records)
        {
            if (!assigned.TryGetValue(record.Gap, out var kind))
            {
                problems.Add($"gapAssessments[{record.Gap}]: evidenza fuori dall'input assegnato.");
            }
            else if (kind != EvidenceKind.Gap)
            {
                problems.Add($"gapAssessments[{record.Gap}]: l'evidenza non è un Gap.");
            }

            foreach (var support in record.SupportingEvidence)
            {
                if (!assigned.TryGetValue(support, out var supportKind))
                {
                    problems.Add($"gapAssessments[{record.Gap}].supportingEvidence: {support} è fuori dall'input assegnato.");
                }
                else if (supportKind == EvidenceKind.Gap)
                {
                    problems.Add($"gapAssessments[{record.Gap}].supportingEvidence: {support} è un Gap e non può essere un riscontro.");
                }
            }

            if (record.OpenItem is not null && !openQuestionKeys.Contains(record.OpenItem))
            {
                problems.Add($"gapAssessments[{record.Gap}].openItem: {record.OpenItem} non è una domanda aperta dell'output.");
            }

            if (record.OpenConflict is not null && !conflictKeys.Contains(record.OpenConflict))
            {
                problems.Add($"gapAssessments[{record.Gap}].openConflict: {record.OpenConflict} non è un conflitto dell'output.");
            }
        }

        var assessed = records.Select(r => r.Gap).ToHashSet(StringComparer.Ordinal);
        foreach (var gap in assigned.Where(kv => kv.Value == EvidenceKind.Gap && !assessed.Contains(kv.Key)).Select(kv => kv.Key).OrderBy(AnalysisAliasPattern.Number))
        {
            problems.Add($"gapAssessments: manca il record per il Gap {gap}.");
        }

        return problems;
    }

    /// <summary>Input assegnato di una chiamata singola: tutte le evidenze del catalogo con il loro tipo.</summary>
    public static IReadOnlyDictionary<string, EvidenceKind> Assigned(AnalysisAliasCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Data.Evidence.ToDictionary(e => e.Alias, e => e.Kind, StringComparer.Ordinal);
    }

    /// <summary>Input assegnato di un batch gerarchico: le sole voci del batch, con il tipo dal catalogo del run.</summary>
    public static IReadOnlyDictionary<string, EvidenceKind> Assigned(AnalysisAliasCatalog run, ReconcileAliasCatalog batch)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(batch);
        return batch.Data.Entries.ToDictionary(
            e => e.Alias,
            e => run.Evidence(e.Alias)?.Kind ?? throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, $"La voce {e.Alias} non è nel catalogo alias del run."),
            StringComparer.Ordinal);
    }

    /// <summary>Forma canonica RFC 8785 della lista, ordinata per numero del Gap: l'ordine di emissione non incide su hash e confronti.</summary>
    public static string Canonical(IReadOnlyList<AiGapAssessment> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var ordered = records.OrderBy(r => AnalysisAliasPattern.Number(r.Gap)).ToList();
        return CanonicalJson.Canonicalize(JsonSerializer.Serialize(ordered, AnalysisAliasContracts.Options));
    }

    /// <summary>`gapAssessmentsHash`: SHA-256 della forma canonica della lista.</summary>
    public static string Hash(IReadOnlyList<AiGapAssessment> records) => CanonicalJson.HashText(Canonical(records));

    private static List<string> ShapeViolations(IReadOnlyList<AiGapAssessment> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var problems = new List<string>();
        foreach (var duplicate in records.GroupBy(r => r.Gap, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"gapAssessments: più record per il Gap {duplicate.Key}.");
        }

        foreach (var r in records)
        {
            var at = $"gapAssessments[{r.Gap}]";
            if (string.IsNullOrWhiteSpace(r.Rationale))
            {
                problems.Add($"{at}.rationale: stringa non vuota richiesta.");
            }

            if (r.SupportingEvidence.Distinct(StringComparer.Ordinal).Count() != r.SupportingEvidence.Count)
            {
                problems.Add($"{at}.supportingEvidence: alias duplicati.");
            }

            if (r.SupportingEvidence.Contains(r.Gap, StringComparer.Ordinal))
            {
                problems.Add($"{at}.supportingEvidence: il Gap non può riscontrare se stesso.");
            }

            if (r.Outcome == GapOutcome.AnsweredInSnapshot)
            {
                if (r.Reason != GapReason.Answered)
                {
                    problems.Add($"{at}: AnsweredInSnapshot richiede reason Answered.");
                }

                if (r.SupportingEvidence.Count == 0)
                {
                    problems.Add($"{at}: AnsweredInSnapshot richiede almeno un riscontro.");
                }

                if (r.OpenItem is not null || r.OpenConflict is not null)
                {
                    problems.Add($"{at}: AnsweredInSnapshot richiede openItem e openConflict null.");
                }
            }
            else
            {
                if (r.Reason == GapReason.Answered)
                {
                    problems.Add($"{at}: Open richiede reason NoAnswer, Partial o Ambiguous.");
                }

                if ((r.OpenItem is null) == (r.OpenConflict is null))
                {
                    problems.Add($"{at}: Open richiede esattamente uno fra openItem e openConflict.");
                }

                if (r.Reason != GapReason.NoAnswer && r.SupportingEvidence.Count == 0)
                {
                    problems.Add($"{at}: {r.Reason} richiede almeno un riscontro.");
                }
            }
        }

        return problems;
    }
}
