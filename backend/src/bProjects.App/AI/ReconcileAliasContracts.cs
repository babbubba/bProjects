using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// DTO di output AI `reconcile-alias-1` (contracts/ai-workflow.md §11, T131): il modello restituisce, per UN batch della riconciliazione gerarchica, soli
// contenuti semantici con alias brevi (E<n> evidenze, N<n> nodi o insiemi, i<n> chiavi locali o item già proposti, R<n> relazioni note, f<n>/p<n> chiavi
// locali). Mai UUID, hash, batchId, inputHash, visited*, candidateId, patchId, baseHash, expectedHash, sourceIds, originBatchIds: li deriva il server
// (ReconcileAliasComposer) e il motore condiviso valida il risultato canonico `reconcile-1`. Nessun database, nessun LLM.

/// <summary>Sezioni di analysis dello schema v3 (le stesse di <see cref="AiItems"/>): il nome nel JSON è quello di <see cref="AiItems.Sections"/>.</summary>
public enum AiSection
{
    [JsonStringEnumMemberName("goals")] Goals,
    [JsonStringEnumMemberName("scopeOverview")] ScopeOverview,
    [JsonStringEnumMemberName("scopeIncluded")] ScopeIncluded,
    [JsonStringEnumMemberName("scopeExcluded")] ScopeExcluded,
    [JsonStringEnumMemberName("requirements")] Requirements,
    [JsonStringEnumMemberName("flows")] Flows,
    [JsonStringEnumMemberName("constraints")] Constraints,
    [JsonStringEnumMemberName("dependencies")] Dependencies,
    [JsonStringEnumMemberName("assumptions")] Assumptions,
    [JsonStringEnumMemberName("openQuestions")] OpenQuestions,
}

/// <summary>Esito dichiarato dal modello sul candidato in esame: Compared/Open nel confronto, Unresolved nell'espansione (il server lo traduce in NotProcessed candidate_unresolved).</summary>
public enum AiFocusOutcome { Compared, Open, Unresolved }

/// <summary>Estremo di un candidato: evidenza `E&lt;n&gt;` oppure nodo/insieme `N&lt;n&gt;` ricevuto nel batch.</summary>
public sealed class EndpointAliasConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return AnalysisAliasPattern.Matches(text, 'E') || AnalysisAliasPattern.Matches(text, 'N')
            ? text!
            : throw new JsonException("Formato non canonico: atteso E<n> o N<n> con n intero positivo senza zeri iniziali.");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>`contract` è la costante «reconcile-alias-1».</summary>
public sealed class ReconcileAliasContractConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() == ReconcileAliasContracts.SchemaVersion
            ? ReconcileAliasContracts.SchemaVersion
            : throw new JsonException($"contract deve essere \"{ReconcileAliasContracts.SchemaVersion}\".");

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiReconcileItem
{
    [JsonConverter(typeof(ItemKeyConverter))]
    public required string Key { get; init; }

    public required AiSection Section { get; init; }
    public required string Text { get; init; }
    public required DocumentAnalysisClassification Classification { get; init; }

    [JsonConverter(typeof(EvidenceAliasListConverter))]
    public required IReadOnlyList<string> Evidence { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiReconcileCandidate
{
    [JsonConverter(typeof(FindingKeyConverter))]
    public required string Key { get; init; }

    [JsonConverter(typeof(EndpointAliasConverter))]
    public required string Left { get; init; }

    [JsonConverter(typeof(EndpointAliasConverter))]
    public required string Right { get; init; }

    public required ReconcileProposedType Type { get; init; }
    public required EvidenceRelationBasis Basis { get; init; }
    public required string Rationale { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiReconcileFocus
{
    public required AiFocusOutcome Outcome { get; init; }
    public required string? Reason { get; init; }
}

/// <summary>
/// Output AI di un batch (<c>reconcile-alias-1</c>): tutte le proprietà obbligatorie, extra/duplicate/enum numerici/numeri fra virgolette rifiutati, <c>null</c> solo dove
/// dichiarato. Le patch riusano <see cref="AiPatch"/> di <c>document-analysis-alias-1</c> (bersaglio: item già proposto <c>i&lt;n&gt;</c> o relazione nota <c>R&lt;n&gt;</c>).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiReconcileOutput
{
    [JsonConverter(typeof(ReconcileAliasContractConverter))]
    public required string Contract { get; init; }

    public required string? Descriptor { get; init; }
    public required string? NoFindings { get; init; }
    public required IReadOnlyList<AiReconcileItem> Items { get; init; }
    public required IReadOnlyList<AiReconcileCandidate> Candidates { get; init; }
    public required AiReconcileFocus? Focus { get; init; }
    public required IReadOnlyList<AiPatch> Patches { get; init; }
}

/// <summary>Codec strict, JSON Schema e prompt di `reconcile-alias-1` (T131).</summary>
public static class ReconcileAliasContracts
{
    public const string SchemaVersion = "reconcile-alias-1";
    public const string SchemaName = "reconcile_alias";
    public const string PromptVersion = "document-reconcile-v3";
    public const string CatalogVersion = "reconcile-alias-catalog-1";

    private static readonly Lazy<string> Prompt = new(() => LoadPrompt(PromptVersion + ".md"));

    /// <summary>Istruzioni di sistema dei batch alias (AI/Prompts/document-reconcile-v3.md, risorsa incorporata); il v2 resta congelato.</summary>
    public static string Instructions => Prompt.Value;

    /// <summary>Stesse regole del codec dell'analisi con alias: extra, duplicati, null non dichiarati, enum fuori dal set e numeri fra virgolette sono <see cref="JsonException"/>.</summary>
    public static AiReconcileOutput Decode(string json)
    {
        try
        {
            // Rifiuta i duplicati anche nei sotto-oggetti letti tramite JsonElement (patch).
            using (JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false }))
            {
            }

            return JsonSerializer.Deserialize<AiReconcileOutput>(json, AnalysisAliasContracts.Options) ?? throw new JsonException("Valore nullo.");
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

    public static string Encode(AiReconcileOutput output) => JsonSerializer.Serialize(output, AnalysisAliasContracts.Options);

    /// <summary>Forma canonica RFC 8785 del DTO.</summary>
    public static string Canonical(AiReconcileOutput output) => CanonicalJson.Canonicalize(Encode(output));

    /// <summary>`aiOutputHash`: SHA-256 della forma canonica del DTO.</summary>
    public static string Hash(AiReconcileOutput output) => CanonicalJson.HashText(Canonical(output));

    private static readonly Lazy<JsonElement> Schema = new(BuildSchema);

    /// <summary>JSON Schema strict del DTO (stesso criterio di <see cref="AnalysisAliasContracts.JsonSchema"/>): vincola la forma, la risoluzione degli alias è del compositore.</summary>
    public static JsonElement JsonSchema() => Schema.Value;

    private static JsonElement BuildSchema()
    {
        var evidenceList = AnalysisAliasContracts.ArraySchema(AnalysisAliasContracts.AliasSchema(AnalysisAliasPattern.Evidence));
        var sections = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. new[] { "goals", "scopeOverview", "scopeIncluded", "scopeExcluded", "requirements", "flows", "constraints", "dependencies", "assumptions", "openQuestions" }.Select(n => (JsonNode)JsonValue.Create(n)!)]) };
        var item = AnalysisAliasContracts.ObjectSchema(
            ("key", AnalysisAliasContracts.AliasSchema(AnalysisAliasPattern.ItemKey)), ("section", sections), ("text", AnalysisAliasContracts.TextSchema()),
            ("classification", AnalysisAliasContracts.EnumSchema<DocumentAnalysisClassification>()), ("evidence", evidenceList.DeepClone()));
        var candidate = AnalysisAliasContracts.ObjectSchema(
            ("key", AnalysisAliasContracts.AliasSchema(AnalysisAliasPattern.FindingKey)), ("left", AnalysisAliasContracts.AliasSchema("^[EN][1-9][0-9]*$")),
            ("right", AnalysisAliasContracts.AliasSchema("^[EN][1-9][0-9]*$")), ("type", AnalysisAliasContracts.EnumSchema<ReconcileProposedType>()),
            ("basis", AnalysisAliasContracts.EnumSchema<EvidenceRelationBasis>()), ("rationale", AnalysisAliasContracts.TextSchema()));
        var focus = AnalysisAliasContracts.ObjectSchema(("outcome", AnalysisAliasContracts.EnumSchema<AiFocusOutcome>()), ("reason", AnalysisAliasContracts.NullableTextSchema()));
        focus["type"] = new JsonArray("object", "null");
        var relationValue = AnalysisAliasContracts.ObjectSchema(
            ("from", AnalysisAliasContracts.AliasSchema(AnalysisAliasPattern.Evidence)), ("to", AnalysisAliasContracts.AliasSchema(AnalysisAliasPattern.Evidence)),
            ("type", AnalysisAliasContracts.EnumSchema<EvidenceRelationType>()), ("basis", AnalysisAliasContracts.EnumSchema<EvidenceRelationBasis>()),
            ("rationale", AnalysisAliasContracts.NullableTextSchema()));
        var alternativeValue = AnalysisAliasContracts.ObjectSchema(
            ("text", AnalysisAliasContracts.TextSchema()), ("classification", AnalysisAliasContracts.EnumSchema<DocumentAnalysisClassification>()), ("reason", AnalysisAliasContracts.TextSchema()));
        var patch = AnalysisAliasContracts.ObjectSchema(
            ("key", AnalysisAliasContracts.AliasSchema(AnalysisAliasPattern.PatchKey)), ("op", AnalysisAliasContracts.EnumSchema<PatchOperation>()),
            ("target", AnalysisAliasContracts.ObjectSchema(
                ("item", AnalysisAliasContracts.NullableAliasSchema(AnalysisAliasPattern.ItemKey)), ("relation", AnalysisAliasContracts.NullableAliasSchema(AnalysisAliasPattern.Relation)))),
            ("evidence", evidenceList.DeepClone()),
            ("value", new JsonObject { ["anyOf"] = new JsonArray(AnalysisAliasContracts.ObjectSchema(), relationValue, alternativeValue) }));
        var schema = AnalysisAliasContracts.ObjectSchema(
            ("contract", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(SchemaVersion) }),
            ("descriptor", AnalysisAliasContracts.NullableTextSchema()), ("noFindings", AnalysisAliasContracts.NullableTextSchema()),
            ("items", AnalysisAliasContracts.ArraySchema(item)), ("candidates", AnalysisAliasContracts.ArraySchema(candidate)), ("focus", focus),
            ("patches", AnalysisAliasContracts.ArraySchema(patch)));

        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }

    private static string LoadPrompt(string file)
    {
        using var stream = typeof(ReconcileAliasContracts).Assembly.GetManifestResourceStream("Prompts." + file)
            ?? throw new InvalidOperationException($"Prompt {file} non incorporato nell'assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }
}
