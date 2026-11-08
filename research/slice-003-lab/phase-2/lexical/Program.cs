using System.Text.Json;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

const LuceneVersion Version = LuceneVersion.LUCENE_48;
var input = JsonSerializer.Deserialize<Input>(Console.OpenStandardInput())
    ?? throw new InvalidDataException("Missing lexical input.");
using var directory = new RAMDirectory();
using var analyzer = new StandardAnalyzer(Version);
using (var writer = new IndexWriter(directory, new IndexWriterConfig(Version, analyzer)))
{
    foreach (var child in input.Children)
    {
        writer.AddDocument(new Document
        {
            new TextField("content", child.Text, Field.Store.NO),
            new StringField("parent_id", child.Parent, Field.Store.YES),
            new StringField("child_id", child.Id, Field.Store.YES),
        });
    }

    writer.Commit();
}

using var reader = DirectoryReader.Open(directory);
var searcher = new IndexSearcher(reader);
var parser = new QueryParser(Version, "content", analyzer);
var results = input.Queries.ToDictionary(q => q.Id, q =>
{
    var query = parser.Parse(QueryParser.Escape(q.Text));
    return searcher.Search(query, input.BranchK * 3).ScoreDocs
        .Select(hit => searcher.Doc(hit.Doc))
        .DistinctBy(doc => doc.Get("parent_id"))
        .Take(input.BranchK)
        .Select(doc => doc.Get("child_id"))
        .ToArray();
});

Console.WriteLine(JsonSerializer.Serialize(new { Similarity = searcher.Similarity.ToString(), Results = results }));

record Input(int BranchK, Child[] Children, Question[] Queries);
record Child(string Id, string Parent, string Text);
record Question(string Id, string Text);
