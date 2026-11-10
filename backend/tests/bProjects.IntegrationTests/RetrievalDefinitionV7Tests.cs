using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003 T028 (DET) — <see cref="DocumentAnalysisWorkflow.ContractOf"/> per la definizione v7: 7 solo con TUTTE le chiavi della v6 e TUTTE le cinque chiavi `retrieval*` con valori supportati;
/// la v6 (e ogni definizione precedente) rifiuta le chiavi `retrieval*`; ogni combinazione mista è `null` (workflow_incompatible), mai reinterpretata. Il freeze delle nuove richieste
/// resta v6 senza `Retrieval:Enabled` e il freeze v6 non cambia di una chiave.
/// </summary>
public sealed class RetrievalDefinitionV7Tests
{
    private const string V7 = "project-analysis-documents-v7";

    private static ProcessingRun Run(Dictionary<string, string> keys, string definition = V7) =>
        new() { WorkflowDefinitionVersion = definition, MafVersion = "1.26.0", PromptVersions = JsonSerializer.Serialize(keys) };

    private static Dictionary<string, string> Keys()
    {
        var keys = AnalysisV6Fixture.Keys();
        foreach (var (key, value) in RetrievalStage.FreezeValues(new RetrievalOptions { PolicyVersion = RetrievalPolicy.Version1 }))
        {
            keys[key] = value;
        }

        return keys;
    }

    [Fact]
    public void A_v7_run_with_every_key_is_contract_7_and_the_freeze_of_a_new_request_matches_exactly()
    {
        Assert.Equal(7, DocumentAnalysisWorkflow.ContractOf(Run(Keys())));
        var frozen = JsonSerializer.Deserialize<Dictionary<string, string>>(SourceSnapshotStore.AnalysisPromptVersionsV7(new RetrievalOptions()))!;
        Assert.Equal(24, frozen.Count);   // le 19 chiavi della v6 (18 + analysisMessageFormat) più le cinque `retrieval*`
        Assert.All(RetrievalStage.Keys, key => Assert.Contains(key, frozen.Keys));
        Assert.Equal(7, DocumentAnalysisWorkflow.ContractOf(Run(frozen)));
        Assert.Equal(V7, SourceSnapshotStore.AnalysisDefinitionV7);
    }

    [Fact]
    public void The_v6_freeze_is_untouched_by_the_v7_definition()
    {
        var v6 = JsonSerializer.Deserialize<Dictionary<string, string>>(SourceSnapshotStore.AnalysisPromptVersionsV6())!;
        Assert.Equal(19, v6.Count);
        Assert.DoesNotContain(v6.Keys, key => key.StartsWith("retrieval", StringComparison.Ordinal));
        Assert.Equal(6, DocumentAnalysisWorkflow.ContractOf(Run(v6, "project-analysis-documents-v6")));
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v6)));   // chiavi v6 su una definizione v7: manca il recupero, mai promosse
    }

    [Theory]
    [InlineData("retrievalIndex")]
    [InlineData("retrievalPolicy")]
    [InlineData("retrievalRegistry")]
    [InlineData("retrievalOutput")]
    [InlineData("retrievalModels")]
    [InlineData("gapStage")]
    [InlineData("analysisOutputPolicy")]
    [InlineData("reconcileAlgorithm")]
    public void A_v7_run_missing_any_single_key_is_incompatible(string key)
    {
        var keys = Keys();
        keys.Remove(key);
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Theory]
    [InlineData("retrievalIndex", "index-text-2")]
    [InlineData("retrievalPolicy", "retrieval-policy-3")]
    [InlineData("retrievalRegistry", "retrieval-registry-2")]
    [InlineData("retrievalOutput", "retrieval-output-2")]
    [InlineData("retrievalModels", "")]
    [InlineData("gapPrompt", "document-gap-v2")]
    public void A_v7_run_with_an_unsupported_value_is_incompatible_and_never_reinterpreted(string key, string value)
    {
        var keys = Keys();
        keys[key] = value;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Fact]
    public void Both_supported_policies_are_accepted_and_the_choice_is_explicit()
    {
        foreach (var version in new[] { RetrievalPolicy.Version1, RetrievalPolicy.Version2 })
        {
            var keys = Keys();
            keys["retrievalPolicy"] = version;
            Assert.Equal(7, DocumentAnalysisWorkflow.ContractOf(Run(keys)));
        }

        Assert.Equal(RetrievalPolicy.Version1, new RetrievalOptions().PolicyVersion);   // il default NON promuove la politica sperimentale
    }

    [Fact]
    public void Retrieval_keys_never_coexist_with_an_older_definition()
    {
        // v6 con le cinque chiavi: mista.
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(Keys(), "project-analysis-documents-v6")));

        // v5 storico con una chiave `retrieval*`: mista.
        var v5 = JsonSerializer.Deserialize<Dictionary<string, string>>(HistoricalAnalysisV5.Versions)!;
        Assert.Equal(5, DocumentAnalysisWorkflow.ContractOf(Run(v5, "project-analysis-documents-v5")));
        foreach (var key in RetrievalStage.Keys)
        {
            var mixed = JsonSerializer.Deserialize<Dictionary<string, string>>(HistoricalAnalysisV5.Versions)!;
            mixed[key] = RetrievalStage.FreezeValues(new RetrievalOptions())[key];
            Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(mixed, "project-analysis-documents-v5")));
            Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(mixed, "project-analysis-documents-v4")));
        }

        // v7 con la policy v5: mista.
        var withGapPolicy = Keys();
        withGapPolicy["reconcileGapPolicy"] = "hierarchical-gap-incomplete-1";
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(withGapPolicy)));
    }

    [Fact]
    public void A_v7_run_without_frozen_versions_or_with_another_maf_is_not_comparable()
    {
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(new ProcessingRun { WorkflowDefinitionVersion = V7, MafVersion = "1.26.0", PromptVersions = null }));
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(new ProcessingRun { WorkflowDefinitionVersion = V7, MafVersion = "1.25.0", PromptVersions = JsonSerializer.Serialize(Keys()) }));
    }

    [Fact]
    public void The_message_format_key_is_honoured_by_v7_like_v6()
    {
        var keys = Keys();
        keys["analysisMessageFormat"] = "message-format-2";
        Assert.Equal(7, DocumentAnalysisWorkflow.ContractOf(Run(keys)));
        keys["analysisMessageFormat"] = "message-format-9";
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Fact]
    public void Policy_options_are_a_closed_set_validated_at_startup()
    {
        Assert.Same(RetrievalPolicy.V1, RetrievalPolicy.ByVersion(RetrievalPolicy.Version1));
        Assert.Same(RetrievalPolicy.V2, RetrievalPolicy.ByVersion(RetrievalPolicy.Version2));
        Assert.Null(RetrievalPolicy.ByVersion("retrieval-policy-9"));
        var invalid = new RetrievalOptionsValidator().Validate(null, new RetrievalOptions { PolicyVersion = "retrieval-policy-9" });
        Assert.True(invalid.Failed);
        Assert.Contains(invalid.Failures!, f => f.Contains("PolicyVersion", StringComparison.Ordinal));
    }
}
