using System.Security.Cryptography;
using System.Text.Json;

namespace bProjects.IntegrationTests;

/// <summary>
/// Gold di recupero promossi dal laboratorio (003 T004, D-1 «gold congelato»): verifica del manifest di provenienza e risoluzione a runtime dei gold,
/// definiti per testo letterale, nei blocchi prodotti dal parser. Nessun gold è modificato: un file diverso da quello congelato è un errore.
/// </summary>
internal static class RetrievalGold
{
    /// <summary>Radice dei gold copiata accanto ai test (vedi csproj).</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Retrieval", "Gold");

    public static string PathOf(params string[] relative) => Path.Combine([Root, .. relative]);

    public sealed record GoldFile(string Path, string Source, string Sha256, long Bytes);

    public sealed record Manifest(string SourceCommit, string QueryAuthor, string FrozenAt, IReadOnlyList<GoldFile> Files);

    public static Manifest ReadManifest(string root)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "PROVENANCE.json")));
        var r = json.RootElement;
        return new Manifest(
            r.GetProperty("sourceCommit").GetString()!,
            r.GetProperty("queryAuthor").GetString()!,
            r.GetProperty("frozenAt").GetString()!,
            r.GetProperty("files").EnumerateArray()
                .Select(f => new GoldFile(f.GetProperty("path").GetString()!, f.GetProperty("source").GetString()!, f.GetProperty("sha256").GetString()!, f.GetProperty("bytes").GetInt64()))
                .ToList());
    }

    /// <summary>Elenco dei problemi: file mancante, hash o dimensione diversi dal congelato, file non dichiarato. Vuoto = gold intatto.</summary>
    public static IReadOnlyList<string> Verify(string root)
    {
        var problems = new List<string>();
        var manifest = ReadManifest(root);
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(root, file.Path);
            if (!File.Exists(path))
            {
                problems.Add($"{file.Path}: mancante");
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (hash != file.Sha256 || bytes.LongLength != file.Bytes)
            {
                problems.Add($"{file.Path}: gold modificato (atteso {file.Sha256}, trovato {hash})");
            }
        }

        var declared = manifest.Files.Select(f => f.Path.Replace('/', Path.DirectorySeparatorChar)).ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path);
            if (relative != "PROVENANCE.json" && !declared.Contains(relative))
            {
                problems.Add($"{relative}: file non dichiarato in PROVENANCE.json");
            }
        }

        return problems;
    }

    public sealed class GoldResolutionException(string message) : Exception(message);

    /// <summary>
    /// Risoluzione <c>gold → BlockId</c> a runtime: il gold è un testo letterale; deve comparire in ESATTAMENTE un blocco (confronto ordinale, senza maiuscole).
    /// Testo non trovato o ambiguo (più blocchi) ⇒ errore, mai una scelta arbitraria.
    /// </summary>
    public static Guid Resolve(IReadOnlyList<(Guid BlockId, string Text)> blocks, string literal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(literal);
        var matches = blocks.Where(b => b.Text.Contains(literal, StringComparison.OrdinalIgnoreCase)).Select(b => b.BlockId).Distinct().ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new GoldResolutionException($"Gold non risolvibile: testo non trovato in alcun blocco ({literal.Length} caratteri)."),
            _ => throw new GoldResolutionException($"Gold ambiguo: il testo compare in {matches.Count} blocchi."),
        };
    }
}
