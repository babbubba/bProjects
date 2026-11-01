using System.Security.Cryptography;
using bProjects.App.Documents;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// Collegamento al parser reale (container Docling, nessun DGX) per i test di livello PARSER: endpoint e segreto arrivano solo da variabili
/// d'ambiente; se mancano il test risulta Skipped con il motivo, mai superato.
/// </summary>
internal static class RealParser
{
    /// <summary>Il parser reale ha un solo slot di conversione: le classi che lo usano non girano in parallelo fra loro (altrimenti 429).</summary>
    public const string CollectionName = "RealParser";

    public const string SecretVariable = "BPROJECTS_TEST_PARSER_SECRET";

    public static ParserOptions Options()
    {
        var endpoint = DocumentTestEnvironment.RequireParser();
        var secret = Environment.GetEnvironmentVariable(SecretVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(secret), $"{SecretVariable} non impostata: il parser reale richiede il segreto interno.");
        return new ParserOptions { BaseUrl = endpoint.ToString(), Secret = secret };
    }

    public static ParserClient Client(ParserOptions? options = null)
    {
        options ??= Options();
        return new ParserClient(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, Microsoft.Extensions.Options.Options.Create(options));
    }

    public static string FixturePath(string name) => Path.Combine(DocumentTestEnvironment.FixturesDirectory, name);

    public static byte[] Fixture(string name) => File.ReadAllBytes(FixturePath(name));

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static DocumentFormat FormatOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => DocumentFormat.Pdf,
        ".docx" => DocumentFormat.Docx,
        ".md" => DocumentFormat.Markdown,
        _ => DocumentFormat.Text,
    };

    public static readonly ParserLimits Limits = new(250_000_000, 1_000, 100_000, 100_000_000, 250);
}
