using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;

namespace bProjects.App.Documents;

/// <summary>
/// Limiti documentali congelati su un run di Analizza (<c>ProcessingRuns.DocumentLimitsSnapshot</c>, JSONB): fotografia dell'intero
/// <see cref="DocumentLimits"/> alla richiesta, con la revisione della policy di origine e un hash del contenuto. Scritta una sola volta, nella stessa
/// transazione che crea il run; da allora è immutabile (trigger PostgreSQL) e l'unica autorità dei limiti del run: retry, recovery e nuovi claim la rileggono,
/// mai la policy corrente. Non è <c>ProcessingRun.PolicyHash</c> (quello copre solo i timer del regime).
/// </summary>
/// <remarks>
/// Forma persistita: <c>{"schemaVersion":1,"policyRevision":N,"limits":{...},"hash":"…"}</c>. L'hash è calcolato sulla forma canonica ricostruita dal
/// tipo (proprietà in ordine fisso), non sul testo letto: PostgreSQL JSONB riordina le chiavi e l'hash resta verificabile dopo il roundtrip.
/// Uno schema non interpretabile è un errore esplicito: nessun default è applicato a una fotografia di altra versione.
/// </remarks>
public sealed record RunLimitsSnapshot(int SchemaVersion, long PolicyRevision, DocumentLimits Limits, string Hash)
{
    public const int CurrentSchemaVersion = 1;
    public const string ErrorCode = "config_invalid";

    private static readonly string[] Properties = ["schemaVersion", "policyRevision", "limits", "hash"];

    /// <summary>Fotografia dei limiti della policy così come sono ora (richiesta di Analizza).</summary>
    public static RunLimitsSnapshot Of(DocumentPolicySnapshot policy) =>
        new(CurrentSchemaVersion, policy.Revision, policy.Limits, ComputeHash(CurrentSchemaVersion, policy.Revision, policy.Limits));

    /// <summary>Hash della forma canonica: schema, revisione e limiti serializzati dal tipo (camelCase, ordine fisso).</summary>
    public static string ComputeHash(int schemaVersion, long policyRevision, DocumentLimits limits) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"run-limits|schema={schemaVersion}|revision={policyRevision}|limits={limits.ToJson()}")));

    /// <summary>JSON da persistere.</summary>
    public string ToJson() => $$"""{"schemaVersion":{{SchemaVersion}},"policyRevision":{{PolicyRevision}},"limits":{{Limits.ToJson()}},"hash":"{{Hash}}"}""";

    /// <summary>
    /// Limiti effettivi di un run di Analizza dalla colonna persistita. Manca, non è JSON, ha forma o schema non supportati, limiti non validi o hash
    /// incoerente: <see cref="DocumentWorkException"/> <c>config_invalid</c>, senza alcun ripiego sulla policy corrente.
    /// </summary>
    public static DocumentLimits Read(string? json) => Parse(json).Limits;

    public static RunLimitsSnapshot Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw Invalid("lo snapshot dei limiti documentali del run manca");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("lo snapshot dei limiti documentali non è un oggetto");
            }

            var names = root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            if (!names.SequenceEqual(Properties.Order(StringComparer.Ordinal)))
            {
                throw Invalid("lo snapshot dei limiti documentali ha una forma non riconosciuta");
            }

            if (!root.GetProperty("schemaVersion").TryGetInt32(out var schema) || schema != CurrentSchemaVersion)
            {
                throw Invalid("lo schema dello snapshot dei limiti documentali non è supportato");
            }

            if (!root.GetProperty("policyRevision").TryGetInt64(out var revision) || revision < 1)
            {
                throw Invalid("la revisione di policy dello snapshot non è valida");
            }

            var hash = root.GetProperty("hash").GetString();
            var limits = DocumentLimits.FromJson(root.GetProperty("limits").GetRawText());
            if (limits is null || limits.Validate().Count > 0)
            {
                throw Invalid("i limiti documentali dello snapshot non sono validi");
            }

            if (!string.Equals(hash, ComputeHash(schema, revision, limits), StringComparison.Ordinal))
            {
                throw Invalid("l'hash dello snapshot dei limiti documentali non è coerente");
            }

            return new RunLimitsSnapshot(schema, revision, limits, hash!);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            throw Invalid("lo snapshot dei limiti documentali è malformato");
        }
    }

    private static DocumentWorkException Invalid(string reason) =>
        new(ErrorCode, "La configurazione congelata del run non è valida: " + reason + ". Nessuna richiesta è stata inviata.");
}
