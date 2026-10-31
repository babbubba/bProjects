using System.Security.Cryptography;
using System.Text;
using bProjects.App.AI;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>
/// Prompt di consolidamento V8 (<c>analysis-v3</c>): il testo incorporato è quello registrato nell'HANDOFF e la versione congelata
/// nei run cambia con lui, così un run o un checkpoint nato con <c>analysis-v2</c> (V7) non riprende con il nuovo prompt.
/// Nessun modello, nessun database.
/// </summary>
public sealed class AnalysisPromptVersionTests
{
    // SHA-256 del system prompt effettivo (risorsa incorporata dopo Trim, UTF-8). Se cambia il prompt cambia anche la versione.
    private const string V8Sha256 = "930d9b41223411b22cd87b8df35bcb7c288088fa33de66e06c79466e14e49588";

    [Fact]
    public void The_embedded_consolidation_prompt_is_V8_with_its_own_version()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ConversationalAgent.ConsolidationInstructions)));

        Assert.Equal(V8Sha256, hash);
        Assert.Equal("analysis-v3", ConversationalAgent.ConsolidationPromptVersion);
    }

    [Fact]
    public void Chat_document_and_graph_versions_are_unchanged()
    {
        Assert.Equal("conversation-v2", ConversationalAgent.PromptVersion);
        Assert.Equal("document-v2", DocumentAgent.PromptVersion);
        Assert.Equal("project-analysis-v2", ProcessingRun.AnalysisWorkflowVersion);
    }

    [Theory]
    [InlineData("""{"analysis":"analysis-v2"}""", false)]
    [InlineData("""{"analysis":"analysis-v3"}""", true)]
    public void A_run_frozen_with_the_V7_prompt_no_longer_matches(string frozen, bool matches)
    {
        var run = new ProcessingRun { WorkflowDefinitionVersion = ProcessingRun.AnalysisWorkflowVersion, MafVersion = "1.26.0", PromptVersions = frozen };

        Assert.Equal(matches, run.PromptVersionMatches("analysis", ConversationalAgent.ConsolidationPromptVersion));
    }
}
