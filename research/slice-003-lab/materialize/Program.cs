using System.Text.Json;
using bProjects.IntegrationTests;

// Compile the unchanged deterministic generator without building/running the application.
var output = Path.GetFullPath(args[0]);
var filler = int.Parse(args[1]);
foreach (var corpus in new[] { LargeDocumentCorpus.Single(filler, 260), LargeDocumentCorpus.Multi(filler, 90) })
{
    var directory = Path.Combine(output, corpus.Name);
    corpus.Materialize(directory);
    var manifest = new { corpus.Name, Seed = LargeDocumentCorpus.Seed, FillerChars = filler,
        BulkRulesPerDocument = corpus.Name.StartsWith("T082") ? 260 : 90,
        corpus.Gold, corpus.Conflicts, corpus.Equivalents, corpus.PmStatement,
        Documents = corpus.Documents.Select(d => new { d.Key, d.FileName, d.Sha256, d.PlainTextSha256 }) };
    File.WriteAllText(Path.Combine(directory, "gold.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
}

namespace bProjects.App.Documents { public enum DocumentFormat { Text, Markdown, Docx, Pdf } }
