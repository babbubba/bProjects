using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using bProjects.App.Documents;

namespace bProjects.IntegrationTests;

/// <summary>
/// DET sul generatore dei documenti grandi (T082/T083): dimensioni, determinismo, hash stabili, presenza di ogni fatto gold UNA sola volta, nessun fatto gold ne conflitto
/// duplicato o spurio nel riempitivo e nelle regole secondarie, zone dei fatti. Nessuna rete, nessun DGX, nessun file nel repository.
/// </summary>
public sealed class LargeDocumentCorpusTests
{
    private const int FillerChars = 60_000;
    private const int Bulk = 120;

    /// <summary>Hash di regressione del testo canonico di <c>Single(20000, 30)</c>: cambia solo se cambia il generatore o i fatti gold (PRNG e testi sono fissi).</summary>
    private const string PinnedSingleTextHash = "6f65ef6d1e01d4fe7885ae73169ed3ac06f0f798ca93f9e544c897913280bb28";

    private static IEnumerable<Corpus> Corpora => [LargeDocumentCorpus.Single(FillerChars, Bulk), LargeDocumentCorpus.Multi(FillerChars, Bulk)];

    private static string Fold(string text) => DocumentSmallFlowMafRealTests.Fold(text);

    [Fact]
    public void The_same_parameters_produce_byte_identical_documents_and_stable_hashes()
    {
        foreach (var (first, second) in new[] { (LargeDocumentCorpus.Single(FillerChars, Bulk), LargeDocumentCorpus.Single(FillerChars, Bulk)), (LargeDocumentCorpus.Multi(FillerChars, Bulk), LargeDocumentCorpus.Multi(FillerChars, Bulk)) })
        {
            Assert.Equal(first.Documents.Count, second.Documents.Count);
            for (var i = 0; i < first.Documents.Count; i++)
            {
                Assert.Equal(first.Documents[i].Bytes, second.Documents[i].Bytes);
                Assert.Equal(first.Documents[i].Sha256, second.Documents[i].Sha256);
                Assert.Equal(first.Documents[i].PlainTextSha256, second.Documents[i].PlainTextSha256);
                Assert.Matches("^[0-9a-f]{64}$", first.Documents[i].Sha256);
            }
        }
    }

    [Fact]
    public void The_canonical_text_hash_is_pinned_so_the_corpus_cannot_drift_silently()
    {
        Assert.Equal(PinnedSingleTextHash, LargeDocumentCorpus.Single(20_000, 30).Documents[0].PlainTextSha256);
    }

    [Fact]
    public void A_different_size_changes_the_filler_but_never_the_gold_facts()
    {
        var small = LargeDocumentCorpus.Single(20_000, 30);
        var large = LargeDocumentCorpus.Single(FillerChars, Bulk);

        Assert.True(large.PlainTextChars > small.PlainTextChars * 2);
        Assert.NotEqual(small.Documents[0].PlainTextSha256, large.Documents[0].PlainTextSha256);
        Assert.Equal(small.Gold, large.Gold);
    }

    [Fact]
    public void Single_has_at_least_twelve_gold_facts_spread_over_start_middle_end_with_two_distant_conflicts_and_qualifiers()
    {
        var gold = LargeDocumentCorpus.SingleGold;

        Assert.True(gold.Count >= 12);
        Assert.All(Enum.GetValues<CorpusZone>(), z => Assert.True(gold.Count(g => g.Zone == z) >= 4, z.ToString()));
        Assert.True(gold.Count(g => g.Negative) >= 3);
        Assert.Contains(gold, g => g.Sentence.Contains("eccezione", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(gold, g => g.Sentence.Contains("esclusi", StringComparison.OrdinalIgnoreCase) || g.Sentence.Contains("superiori", StringComparison.OrdinalIgnoreCase));
        foreach (var conflict in LargeDocumentCorpus.SingleConflicts)
        {
            var zones = conflict.Sides.Select(side => gold.Single(g => side.All(t => g.Quote.Contains(t))).Zone).ToList();
            Assert.True(zones.Distinct().Count() >= 2, conflict.Id);   // posizioni in zone distanti
            Assert.Contains(CorpusZone.End, zones);
        }
    }

    [Fact]
    public void Multi_has_three_distinct_formats_complements_conflicts_with_the_pm_paraphrases_and_distant_references()
    {
        var corpus = LargeDocumentCorpus.Multi(FillerChars, Bulk);

        Assert.Equal([DocumentFormat.Text, DocumentFormat.Markdown, DocumentFormat.Docx], corpus.Documents.Select(d => d.Format));
        Assert.Equal(3, corpus.Documents.Select(d => d.FileName).Distinct().Count());
        Assert.Contains(corpus.Gold, g => g.DocKey == "pm" && g.Class == "Declared");
        Assert.Contains(corpus.Conflicts, c => c.Id.Contains("PM", StringComparison.Ordinal));
        Assert.Contains(corpus.Conflicts, c => c.Id.Contains("doc/doc", StringComparison.Ordinal));
        Assert.Single(corpus.Equivalents);
        Assert.Contains(corpus.PmStatement!, corpus.Gold.Where(g => g.DocKey == "pm").Select(g => g.Sentence));
        // complementari: stesso soggetto in documenti diversi con aspetti diversi
        Assert.Contains(corpus.Gold, g => g.DocKey == "d1" && g.Id == "M07");
        Assert.Contains(corpus.Gold, g => g.DocKey == "d2" && g.Id == "M10");
        // riferimento a porzione distante: l'Allegato C e citato all'inizio di d1 e definito alla fine di d2
        Assert.Equal(CorpusZone.Start, corpus.Gold.Single(g => g.Id == "M06").Zone);
        Assert.Equal(CorpusZone.End, corpus.Gold.Single(g => g.Id == "M11").Zone);
        // alternative preservate: tre posizioni (due documenti e il PM) sul termine di pagamento
        Assert.Equal(3, corpus.Conflicts.Single(c => c.Id.StartsWith("pagamento", StringComparison.Ordinal)).Sides.Length);
    }

    [Fact]
    public void Every_gold_sentence_of_a_document_appears_exactly_once_in_its_own_text_and_nowhere_else()
    {
        foreach (var corpus in Corpora)
        {
            foreach (var fact in corpus.Gold.Where(g => g.DocKey != "pm"))
            {
                foreach (var doc in corpus.Documents)
                {
                    Assert.Equal(doc.Key == fact.DocKey ? 1 : 0, Regex.Matches(doc.PlainText, Regex.Escape(fact.Sentence)).Count);
                }
            }
        }
    }

    [Fact]
    public void Gold_sentences_are_all_distinct_and_each_conflict_side_token_occurs_only_in_gold_sentences()
    {
        foreach (var corpus in Corpora)
        {
            var sentences = corpus.Gold.Where(g => g.DocKey != "pm").Select(g => g.Sentence).ToList();
            Assert.Equal(sentences.Count, sentences.Distinct().Count());
            foreach (var token in corpus.Conflicts.SelectMany(c => c.Sides).SelectMany(s => s).Distinct())
            {
                foreach (var doc in corpus.Documents)
                {
                    var outside = doc.Fillers.Concat(doc.BulkRules).Where(t => t.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
                    Assert.True(outside.Count == 0, $"«{token}» compare fuori dai fatti gold: {outside.FirstOrDefault()}");
                }
            }
        }
    }

    [Fact]
    public void The_filler_is_realistic_non_applicable_text_with_no_digits_obligations_or_gold_vocabulary()
    {
        foreach (var corpus in Corpora)
        {
            foreach (var doc in corpus.Documents)
            {
                Assert.True(doc.Fillers.Count > 25);
                foreach (var paragraph in doc.Fillers)
                {
                    Assert.DoesNotMatch(@"\d", paragraph);
                    Assert.True(paragraph.Length > 200);
                    var folded = Fold(paragraph);
                    Assert.True(!LargeDocumentCorpus.ReservedStems.Any(s => folded.Contains(s, StringComparison.Ordinal)), "vocabolario gold nel riempitivo: " + paragraph);
                    Assert.True(!LargeDocumentCorpus.ObligationWords.Any(w => Regex.IsMatch(folded, @"\b" + Regex.Escape(Fold(w)))), "forma di obbligo nel riempitivo: " + paragraph);
                }
            }
        }
    }

    [Fact]
    public void Secondary_rules_are_unique_applicable_and_never_use_gold_vocabulary_or_repeat_a_line_name_across_documents()
    {
        foreach (var corpus in Corpora)
        {
            var all = corpus.Documents.SelectMany(d => d.BulkRules).ToList();
            Assert.Equal(corpus.Documents.Count * Bulk, all.Count);
            Assert.Equal(all.Count, all.Distinct().Count());
            var names = all.Select(r => Regex.Match(r, @"(?:linea|reparto) ([A-Z][a-z]+)").Groups[1].Value).ToList();
            Assert.All(names, n => Assert.NotEmpty(n));
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.All(all, rule => Assert.True(!LargeDocumentCorpus.ReservedStems.Any(s => Fold(rule).Contains(s, StringComparison.Ordinal)), rule));
            Assert.All(all, rule => Assert.Matches(@"\d+ (pezzi|gradi)", rule));
        }
    }

    [Fact]
    public void Gold_facts_sit_in_their_declared_third_of_the_text_and_the_size_scales_with_the_request()
    {
        foreach (var corpus in Corpora)
        {
            foreach (var doc in corpus.Documents)
            {
                Assert.True(doc.PlainText.Length > FillerChars / 4);
                foreach (var fact in corpus.Gold.Where(g => g.DocKey == doc.Key))
                {
                    var ratio = (double)doc.PlainText.IndexOf(fact.Sentence, StringComparison.Ordinal) / doc.PlainText.Length;
                    Assert.True(fact.Zone switch { CorpusZone.Start => ratio < 0.25, CorpusZone.Middle => ratio is >= 0.35 and <= 0.65, _ => ratio > 0.75 }, $"{fact.Id} {fact.Zone} {ratio:F2}");
                }
            }
        }

        Assert.True(LargeDocumentCorpus.Single(120_000, Bulk).PlainTextChars > LargeDocumentCorpus.Single(FillerChars, Bulk).PlainTextChars * 1.5);
        Assert.True(LargeDocumentCorpus.Single(FillerChars, Bulk).PlainTextChars >= FillerChars);
    }

    [Fact]
    public void The_docx_is_a_valid_package_whose_text_carries_the_gold_facts_and_the_markdown_has_headings()
    {
        var corpus = LargeDocumentCorpus.Multi(FillerChars, Bulk);
        var docx = corpus.Documents.Single(d => d.Format == DocumentFormat.Docx);

        using var zip = new ZipArchive(new MemoryStream(docx.Bytes), ZipArchiveMode.Read);
        Assert.Equal(["[Content_Types].xml", "_rels/.rels", "word/document.xml"], zip.Entries.Select(e => e.FullName));
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();
        Assert.Equal(docx.PlainText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length, Regex.Matches(xml, "<w:p>").Count);
        foreach (var fact in corpus.Gold.Where(g => g.DocKey == "d3"))
        {
            Assert.Contains(System.Security.SecurityElement.Escape(fact.Sentence), xml, StringComparison.Ordinal);
        }

        var markdown = Encoding.UTF8.GetString(corpus.Documents.Single(d => d.Format == DocumentFormat.Markdown).Bytes);
        Assert.StartsWith("# Allegato tecnico Zefiro", markdown, StringComparison.Ordinal);
        Assert.True(Regex.Matches(markdown, @"^## ", RegexOptions.Multiline).Count >= 20);
    }

    [Fact]
    public void Materialize_writes_the_originals_to_a_temporary_folder_with_the_recorded_hashes()
    {
        var corpus = LargeDocumentCorpus.Multi(20_000, 10);
        var directory = Path.Combine(Path.GetTempPath(), "bprojects-corpus-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manifest = corpus.Materialize(directory);

            var text = File.ReadAllText(manifest);
            foreach (var doc in corpus.Documents)
            {
                Assert.Equal(doc.Sha256, RealParser.Sha256(File.ReadAllBytes(Path.Combine(directory, doc.FileName))));
                Assert.Contains($"{doc.FileName} sha256={doc.Sha256}", text, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

/// <summary>PARSER (nessun DGX): il parser Docling reale gestisce TXT, Markdown e DOCX generati e ne conserva ogni fatto gold; Skipped, mai superato, senza endpoint/segreto del parser.</summary>
[Trait("Category", DocumentTestCategories.Parser)]
[Collection(RealParser.CollectionName)]
public sealed class LargeDocumentParserTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_real_parser_converts_the_generated_formats_to_a_complete_manifest_that_keeps_every_gold_sentence()
    {
        var client = RealParser.Client();
        var corpus = LargeDocumentCorpus.Multi(30_000, 20);
        foreach (var doc in corpus.Documents)
        {
            var sha = RealParser.Sha256(doc.Bytes);
            var response = await client.ConvertAsync(new ParserRequest(new MemoryStream(doc.Bytes), doc.Format, Guid.NewGuid(), sha, doc.Bytes.Length, RealParser.Limits), TimeSpan.FromMinutes(10), Ct);
            var manifest = ParsedManifestValidator.Validate(response.Body, new(sha, doc.Bytes.Length, doc.Format, response.ConfigHash, 1_000, 100_000, 250_000_000));

            Assert.Equal(ManifestStatus.Complete, manifest.Status);
            var text = DocumentSmallFlowMafRealTests.Fold(string.Join("\n", manifest.Blocks.Select(b => b.Text)));
            foreach (var fact in corpus.Gold.Where(g => g.DocKey == doc.Key))
            {
                Assert.True(text.Contains(DocumentSmallFlowMafRealTests.Fold(fact.Sentence), StringComparison.Ordinal), $"{doc.FileName}: {fact.Id} non conservato dal parser");
            }

            Assert.True(text.Length >= DocumentSmallFlowMafRealTests.Fold(doc.PlainText).Length * 0.97, doc.FileName);
        }
    }
}
