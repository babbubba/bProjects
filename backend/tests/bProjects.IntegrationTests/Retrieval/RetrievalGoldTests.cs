using System.Text.Json;

namespace bProjects.IntegrationTests;

/// <summary>003 T004: i gold promossi dal laboratorio sono quelli congelati (hash di PROVENANCE.json); una modifica fa fallire il test; risoluzione runtime gold → blocco.</summary>
public sealed class RetrievalGoldTests
{
    [Fact]
    public void Every_frozen_file_matches_its_recorded_hash_and_nothing_else_is_present()
    {
        var problems = RetrievalGold.Verify(RetrievalGold.Root);

        Assert.True(problems.Count == 0, "Gold congelato (D-1) alterato: " + string.Join("; ", problems));
    }

    [Fact]
    public void The_provenance_records_commit_author_and_freeze_date_and_lists_the_expected_files()
    {
        var manifest = RetrievalGold.ReadManifest(RetrievalGold.Root);

        Assert.Equal("f005c8dab923f12e3130ef86517b1318d707ce10", manifest.SourceCommit);
        Assert.Equal("singolo", manifest.QueryAuthor);
        Assert.Equal("2026-11-09", manifest.FrozenAt);
        var paths = manifest.Files.Select(f => f.Path).ToHashSet();
        foreach (var required in new[]
        {
            "cases.json", "T082-single/gold.json", "T083-multi/gold.json", "T082-single/capitolato-aurora.txt", "T083-multi/contratto-quadro-zefiro.txt",
            "T083-multi/allegato-tecnico-zefiro.md", "T083-multi/verbale-collaudo-zefiro.docx", "N4-fixtures/txt-conflict-a.txt", "N4-fixtures/txt-conflict-b.txt",
        })
        {
            Assert.Contains(required, paths);
        }

        Assert.All(manifest.Files, f =>
        {
            Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
            Assert.StartsWith("research/slice-003-lab/", f.Source);
        });
    }

    [Fact]
    public void A_modified_gold_makes_the_verification_fail()
    {
        var copy = Path.Combine(Path.GetTempPath(), "bprojects-gold-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(RetrievalGold.Root, copy);
            Assert.Empty(RetrievalGold.Verify(copy));

            // Un solo carattere cambiato in un gold.
            var gold = Path.Combine(copy, "T082-single", "gold.json");
            File.WriteAllText(gold, File.ReadAllText(gold).Replace("84 mesi", "85 mesi", StringComparison.Ordinal));
            var problems = RetrievalGold.Verify(copy);
            Assert.Contains(problems, p => p.StartsWith("T082-single/gold.json", StringComparison.Ordinal) || p.StartsWith("T082-single\\gold.json", StringComparison.Ordinal));

            // File mancante e file extra sono rilevati.
            File.Delete(Path.Combine(copy, "cases.json"));
            File.WriteAllText(Path.Combine(copy, "extra-gold.json"), "{}");
            problems = RetrievalGold.Verify(copy);
            Assert.Contains(problems, p => p.StartsWith("cases.json: mancante", StringComparison.Ordinal));
            Assert.Contains(problems, p => p.StartsWith("extra-gold.json", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(copy))
            {
                Directory.Delete(copy, recursive: true);
            }
        }
    }

    [Fact]
    public void The_frozen_query_sets_have_the_expected_shape()
    {
        using var cases = JsonDocument.Parse(File.ReadAllBytes(RetrievalGold.PathOf("cases.json")));
        Assert.Equal(12, cases.RootElement.GetProperty("cases").GetArrayLength());

        foreach (var set in new[] { "T082-single", "T083-multi" })
        {
            using var gold = JsonDocument.Parse(File.ReadAllBytes(RetrievalGold.PathOf(set, "gold.json")));
            var items = gold.RootElement.GetProperty("Gold");
            Assert.True(items.GetArrayLength() > 0);
            foreach (var item in items.EnumerateArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("Sentence").GetString()));   // il gold è un testo letterale
            }
        }
    }

    [Fact]
    public void Gold_sentences_resolve_to_exactly_one_block_of_the_source_text()
    {
        // Blocchi grezzi per riga non vuota del documento sintetico: la risoluzione reale userà i blocchi del parser (T018).
        var lines = File.ReadAllLines(RetrievalGold.PathOf("T082-single", "capitolato-aurora.txt")).Where(l => l.Length > 0).ToList();
        var blocks = lines.Select(l => (Guid.NewGuid(), l)).ToList();
        using var gold = JsonDocument.Parse(File.ReadAllBytes(RetrievalGold.PathOf("T082-single", "gold.json")));

        var first = gold.RootElement.GetProperty("Gold")[0].GetProperty("Sentence").GetString()!;
        var id = RetrievalGold.Resolve(blocks, first);

        Assert.Contains(first, blocks.Single(b => b.Item1 == id).Item2, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_gold_text_not_found_or_ambiguous_is_an_error()
    {
        var a = (Guid.NewGuid(), "Il termine di pagamento e' 30 giorni.");
        var b = (Guid.NewGuid(), "Altro: il termine di pagamento e' 30 giorni, salvo patto.");
        var c = (Guid.NewGuid(), "La penale e' del 5 per cento.");

        Assert.Equal(c.Item1, RetrievalGold.Resolve([a, c], "penale"));
        Assert.Throws<RetrievalGold.GoldResolutionException>(() => RetrievalGold.Resolve([a, c], "garanzia fideiussoria"));
        var ambiguous = Assert.Throws<RetrievalGold.GoldResolutionException>(() => RetrievalGold.Resolve([a, b, c], "termine di pagamento"));
        Assert.Contains("ambiguo", ambiguous.Message);
        Assert.Throws<ArgumentException>(() => RetrievalGold.Resolve([a], " "));
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }
    }
}
