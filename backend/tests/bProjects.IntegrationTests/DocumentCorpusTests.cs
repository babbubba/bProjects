using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace bProjects.IntegrationTests;

/// <summary>
/// T002 (GOLD): il manifest del corpus sintetico identifica ogni file con hash, attesa e licenza; i locator gold dei testi
/// puntano davvero alla citazione (offset UTF-16). Le fixture della 001 non sono coinvolte.
/// </summary>
public sealed class DocumentCorpusTests
{
    private static readonly JsonElement Manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(DocumentTestEnvironment.FixturesDirectory, "manifest.json"))).RootElement;

    private static IEnumerable<JsonElement> Files => Manifest.GetProperty("files").EnumerateArray();

    private static string PathOf(JsonElement file) => Path.Combine(DocumentTestEnvironment.FixturesDirectory, file.GetProperty("path").GetString()!);

    [Fact]
    public void Every_file_on_disk_is_listed_with_matching_hash_size_license_and_expectation()
    {
        var listed = Files.Select(f => f.GetProperty("path").GetString()!).ToHashSet();
        var onDisk = Directory.GetFiles(DocumentTestEnvironment.FixturesDirectory).Select(f => Path.GetFileName(f)).Where(n => n != "manifest.json").ToHashSet();
        Assert.Equal(onDisk, listed);

        foreach (var file in Files)
        {
            var bytes = File.ReadAllBytes(PathOf(file));
            Assert.Equal(file.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.Equal(file.GetProperty("byteLength").GetInt32(), bytes.Length);
            Assert.Contains("sintetico", file.GetProperty("license").GetString());
            Assert.True(file.GetProperty("expected").TryGetProperty("parse", out var parse) && parse.GetString() is "Complete" or "Incomplete" or "Rejected", file.GetProperty("id").GetString());
        }

        Assert.Equal(Files.Count(), Files.Select(f => f.GetProperty("id").GetString()).Distinct().Count());
    }

    [Fact]
    public void The_corpus_covers_the_four_formats_and_the_required_hostile_shapes()
    {
        var ids = Files.Select(f => f.GetProperty("id").GetString()!).ToHashSet();
        var formats = Files.Select(f => f.GetProperty("format").GetString()).ToHashSet();

        Assert.Superset(new HashSet<string?> { "pdf", "docx", "md", "txt" }, formats);
        Assert.Superset(
            new HashSet<string> { "pdf-text", "pdf-table-multipage", "docx-basic", "md-basic", "md-non-bmp", "txt-negations", "pdf-corrupt", "pdf-protected", "docx-corrupt", "pdf-mixed", "txt-long", "txt-conflict-a", "txt-conflict-b" },
            ids);
        Assert.Contains(Files, f => f.GetProperty("expected").GetProperty("parse").GetString() == "Rejected");
        Assert.Contains(Files, f => f.GetProperty("expected").GetProperty("parse").GetString() == "Incomplete");
    }

    [Fact]
    public void The_long_document_has_at_least_twelve_facts_at_start_middle_and_end_and_distant_conflicts()
    {
        var facts = Manifest.GetProperty("facts").EnumerateArray().Where(f => f.GetProperty("fileId").GetString() == "txt-long").ToList();

        Assert.True(facts.Count >= 12);
        var zones = facts.Select(f => f.GetProperty("zone").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?> { "start", "middle", "end" }, zones);

        var conflicts = Manifest.GetProperty("conflicts").EnumerateArray().ToList();
        Assert.Contains(conflicts, c => c.GetProperty("id").GetString() == "conflict-users");
        Assert.Contains(conflicts, c => c.GetProperty("id").GetString() == "conflict-payment");
        var factIds = Manifest.GetProperty("facts").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ToHashSet();
        Assert.All(conflicts.SelectMany(c => c.GetProperty("factIds").EnumerateArray()), id => Assert.Contains(id.GetString(), factIds));
    }

    [Fact]
    public void Text_locators_point_at_the_exact_quote_in_utf16_units()
    {
        var checkedLocators = 0;
        foreach (var fact in Manifest.GetProperty("facts").EnumerateArray())
        {
            var locator = fact.GetProperty("locator");
            if (locator.GetProperty("kind").GetString() != "text-range")
            {
                continue;
            }

            var file = Files.Single(f => f.GetProperty("id").GetString() == fact.GetProperty("fileId").GetString());
            var text = DecodeText(File.ReadAllBytes(PathOf(file)));
            var start = locator.GetProperty("startUtf16").GetInt32();
            var end = locator.GetProperty("endUtf16").GetInt32();

            Assert.Equal(fact.GetProperty("quote").GetString(), text.Substring(start, end - start));   // string .NET = unità UTF-16
            Assert.Equal(locator.GetProperty("line").GetInt32(), text[..start].Count(c => c == '\n') + 1);
            checkedLocators++;
        }

        Assert.True(checkedLocators >= 25);

        // Il caso non BMP: dopo i caratteri fuori dal BMP gli offset UTF-16 superano quelli dei codepoint.
        var nonBmp = Manifest.GetProperty("facts").EnumerateArray().Single(f => f.GetProperty("id").GetString() == "md-non-bmp-f1").GetProperty("locator");
        Assert.True(nonBmp.GetProperty("startUtf16").GetInt32() > nonBmp.GetProperty("startCodepoint").GetInt32());
    }

    [Fact]
    public void Classification_expectations_are_kept_apart_from_facts()
    {
        var classifications = Manifest.GetProperty("classifications").EnumerateArray().ToList();

        Assert.NotEmpty(classifications);
        Assert.All(classifications, c => Assert.False(c.TryGetProperty("quote", out _)));
        Assert.All(Manifest.GetProperty("facts").EnumerateArray(), f => Assert.False(f.TryGetProperty("expectedOutcome", out _)));
    }

    private static string DecodeText(byte[] bytes) =>
        bytes is [0xFF, 0xFE, ..] ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2) : new UTF8Encoding(false, true).GetString(bytes);
}
