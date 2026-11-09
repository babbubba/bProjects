using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// DTO e schema strict `gap-stage-alias-1` dello stadio Gap (002 D1, contracts/ai-workflow.md §13.7): SOLO `{ contract, gapAssessments[] }`. Il modello non propone mai openQuestion né
// conflitti (nessun campo per farlo: un campo extra è rifiutato dal codec) e il server non crea mai elementi AI per suo conto. I record sono quelli di §13.2 (`gap-assessment-1`),
// validati dalle regole esistenti `GapAssessmentRules` con le chiavi offerte nell'input come unici riferimenti Open ammessi. Nessun database, nessun LLM.

/// <summary>`contract` è la costante «gap-stage-1».</summary>
public sealed class GapStageContractConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() == GapStage.ContractValue
            ? GapStage.ContractValue
            : throw new JsonException($"contract deve essere \"{GapStage.ContractValue}\".");

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>Output AI dello stadio Gap: `contract` e `gapAssessments`, nient'altro.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiGapStageOutput
{
    [JsonConverter(typeof(GapStageContractConverter))]
    public required string Contract { get; init; }

    public required IReadOnlyList<AiGapAssessment> GapAssessments { get; init; }
}

/// <summary>
/// Esito della validazione server dello stadio. <see cref="Invalid"/> ⇒ <c>work_output_invalid</c> terminale per lo stadio (nessuna riparazione né retry); un Gap senza record ⇒
/// <c>ValidIncomplete</c> con <c>gap_not_assessed</c> (output riutilizzabile ma incompleto, mai Completed). I record sono interpretazione AI non validata semanticamente.
/// </summary>
public sealed record GapStageValidation(
    ReconciliationOutcome Outcome, string? ErrorCode, IReadOnlyList<string> Violations, AiGapStageOutput? Output, IReadOnlyList<string> MissingGaps, string? AiOutputHash, string? GapAssessmentsHash)
{
    public bool IsReusableWorkOutput => Outcome != ReconciliationOutcome.Invalid;
}

public static class GapStageContracts
{
    /// <summary>Codec strict: proprietà extra o duplicate, null non dichiarati, enum fuori dal set e record malformati (forma di §13.2) sono <see cref="JsonException"/>.</summary>
    public static AiGapStageOutput Decode(string json)
    {
        try
        {
            using (JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false }))
            {
            }

            var output = JsonSerializer.Deserialize<AiGapStageOutput>(json, AnalysisAliasContracts.Options) ?? throw new JsonException("Valore nullo.");
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

    public static string Encode(AiGapStageOutput output) => JsonSerializer.Serialize(output, AnalysisAliasContracts.Options);

    /// <summary>Forma canonica RFC 8785 del DTO, con i record ordinati per numero del Gap (l'ordine di emissione non incide sull'hash).</summary>
    public static string Canonical(AiGapStageOutput output) =>
        CanonicalJson.Canonicalize(Encode(output with { GapAssessments = [.. output.GapAssessments.OrderBy(r => AnalysisAliasPattern.Number(r.Gap))] }));

    /// <summary>`aiOutputHash` dello stadio: SHA-256 della forma canonica del DTO.</summary>
    public static string Hash(AiGapStageOutput output) => CanonicalJson.HashText(Canonical(output));

    private static readonly Lazy<JsonElement> Schema = new(() =>
    {
        var schema = AnalysisAliasContracts.ObjectSchema(
            ("contract", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(GapStage.ContractValue) }),
            ("gapAssessments", AnalysisAliasContracts.ArraySchema(AnalysisAliasContracts.GapAssessmentSchema())));
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    });

    /// <summary>JSON Schema strict di `gap-stage-alias-1`: oggetto chiuso con le sole proprietà `contract` e `gapAssessments`; alias con pattern esatto, nessun UUID o hash, nessun campo per proporre domande o conflitti.</summary>
    public static JsonElement JsonSchema() => Schema.Value;

    /// <summary>
    /// Validazione server: decodifica strict (rifiuto ⇒ <c>work_output_invalid</c>), poi le regole condivise <see cref="GapAssessmentRules.Check"/> sull'input assegnato (TUTTO il ledger:
    /// un record per ogni Gap, nessuno per altri tipi o fuori input, supporti non Gap) con <c>openItem</c>/<c>openConflict</c> risolvibili SOLO fra le chiavi offerte. Un record mancante
    /// non rende il DTO invalido (<c>allowMissing</c>): è <c>gap_not_assessed</c>.
    /// </summary>
    public static GapStageValidation Validate(string responseText, AnalysisAliasCatalog catalog, GapStageReferences references)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(references);
        AiGapStageOutput output;
        try
        {
            output = Decode(responseText);
        }
        catch (JsonException e)
        {
            return Reject([$"JSON non conforme a {GapStage.AiSchema}: {e.Message}"]);
        }

        var violations = GapAssessmentRules.Check(
            output.GapAssessments, GapAssessmentRules.Assigned(catalog), [.. references.Questions.Select(q => q.Key)], [.. references.Conflicts.Select(c => c.Key)], allowMissing: true);
        if (violations.Count > 0)
        {
            return Reject(violations);
        }

        var missing = GapAssessmentRules.MissingGaps(output.GapAssessments, GapAssessmentRules.Assigned(catalog));
        return new(
            missing.Count == 0 ? ReconciliationOutcome.Complete : ReconciliationOutcome.ValidIncomplete, null, [], output, missing, Hash(output), GapAssessmentRules.Hash(output.GapAssessments));
    }

    private static GapStageValidation Reject(IReadOnlyList<string> violations) =>
        new(ReconciliationOutcome.Invalid, DocumentWorkException.WorkOutputInvalid, violations, null, [], null, null);
}
