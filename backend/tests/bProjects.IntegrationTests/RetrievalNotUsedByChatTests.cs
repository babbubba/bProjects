using System.Text.RegularExpressions;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003 T037 (DET) — D-4: Chat e bozza (Genera) non invocano il recupero. Nessun file sorgente dei flussi di conversazione, chat documentale e bozza
/// referenzia il namespace <c>bProjects.App.Retrieval</c> né alcun tipo dichiarato nel modulo (i tipi sono derivati dai file di <c>Retrieval/</c>, non da un elenco fisso).
/// </summary>
public sealed class RetrievalNotUsedByChatTests
{
    private static string AppRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "bProjects.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Radice backend non trovata."), "src", "bProjects.App");
    }

    [Fact]
    public void Chat_and_draft_sources_never_reference_the_retrieval_module()
    {
        var app = AppRoot();
        var retrievalTypes = Directory.EnumerateFiles(Path.Combine(app, "Retrieval"), "*.cs").Select(Path.GetFileNameWithoutExtension).ToList();
        Assert.NotEmpty(retrievalTypes);

        var sources = Directory.EnumerateFiles(Path.Combine(app, "Conversations"), "*.cs")
            .Concat(Directory.EnumerateFiles(Path.Combine(app, "AI"), "*.cs").Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.Contains("Chat", StringComparison.Ordinal) || name.Contains("Draft", StringComparison.Ordinal);
            }))
            .ToList();
        Assert.Contains(sources, f => f.EndsWith("DocumentChatWorkflow.cs", StringComparison.Ordinal));
        Assert.Contains(sources, f => f.EndsWith("DocumentSourceDraftWorkflow.cs", StringComparison.Ordinal));
        Assert.Contains(sources, f => f.EndsWith("ConversationEndpoints.cs", StringComparison.Ordinal));

        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("bProjects.App.Retrieval", text, StringComparison.Ordinal);
            foreach (var type in retrievalTypes)
            {
                Assert.False(Regex.IsMatch(text, $@"\b{Regex.Escape(type!)}\b"), $"{Path.GetFileName(file)} referenzia il tipo di recupero {type}.");
            }
        }
    }
}
