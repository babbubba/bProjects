using System.Text.Json;

namespace bProjects.App.Analysis;

public sealed record DecodedAnalysisContent(AnalysisContent? V10, DocumentAnalysisContent? V11);

/// <summary>
/// Selezione del decoder di AnalysisContent per versione di schema esatta (contracts/ai-workflow.md §1). Il JSON 1.0.0 resta decodificato
/// soltanto da <see cref="AnalysisContentJson"/> e non viene convertito; una versione ignota o assente è rifiutata esplicitamente.
/// </summary>
public static class AnalysisSchemaDispatch
{
    public static string ReadSchemaVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.String)
        {
            throw new NotSupportedException("Versione di schema AnalysisContent assente o non leggibile.");
        }

        return version.GetString()!;
    }

    public static DecodedAnalysisContent Decode(string json) => ReadSchemaVersion(json) switch
    {
        AnalysisContent.CurrentSchemaVersion => new(AnalysisContentJson.Deserialize(json), null),
        DocumentAnalysisContent.SchemaVersionValue => new(null, DocumentAnalysisContentJson.Deserialize(json)),
        var other => throw new NotSupportedException($"Versione di schema AnalysisContent non supportata: «{other}»."),
    };
}
