using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003, T040 dopo il Run 3 — <see cref="DocumentAnalysisWorkflow.ContractOf"/> per la definizione v9: 9 solo con TUTTE le chiavi della v8 (prompt document-reconcile-v4 compreso) e
/// <c>reconcileSchemaProfile = reconcile-alias-schema-by-batch-2</c>. Nessuna reinterpretazione fra v8 e v9: il profilo di una è incompatibile con l'altra. Il freeze v8 non cambia.
/// </summary>
public sealed class ReconcileDefinitionV9Tests
{
    private const string V8 = "project-analysis-documents-v8";
    private const string V9 = "project-analysis-documents-v9";

    private static readonly RetrievalOptions Retrieval = new() { PolicyVersion = RetrievalPolicy.Version1 };

    private static ProcessingRun Run(Dictionary<string, string> keys, string definition = V9) =>
        new() { WorkflowDefinitionVersion = definition, MafVersion = "1.26.0", PromptVersions = JsonSerializer.Serialize(keys) };

    private static Dictionary<string, string> Keys(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

    private static Dictionary<string, string> V8Keys() => Keys(SourceSnapshotStore.AnalysisPromptVersionsV8(Retrieval));

    private static Dictionary<string, string> V9Keys() => Keys(SourceSnapshotStore.AnalysisPromptVersionsV9(Retrieval));

    [Fact]
    public void A_v9_run_with_every_key_is_contract_9_and_its_freeze_is_the_v8_freeze_with_only_the_schema_profile_changed()
    {
        var v8 = V8Keys();
        var v9 = V9Keys();

        Assert.Equal(9, DocumentAnalysisWorkflow.ContractOf(Run(v9)));
        Assert.Equal(v8.Keys.Order(StringComparer.Ordinal), v9.Keys.Order(StringComparer.Ordinal));   // stesso insieme di chiavi
        Assert.Equal([DocumentAnalysisWorkflow.ReconcileSchemaProfileKey], v8.Where(kv => v9[kv.Key] != kv.Value).Select(kv => kv.Key));   // cambia solo il profilo
        Assert.Equal(("reconcile-alias-schema-by-batch-1", "reconcile-alias-schema-by-batch-2"), (v8[DocumentAnalysisWorkflow.ReconcileSchemaProfileKey], v9[DocumentAnalysisWorkflow.ReconcileSchemaProfileKey]));
        Assert.Equal(ReconcileAliasContracts.PromptVersionV4, v9["reconcile"]);   // document-reconcile-v4 invariato
        Assert.Equal((V9, V9), (SourceSnapshotStore.AnalysisDefinitionV9, DocumentAnalysisWorkflow.DefinitionVersionV9));
    }

    [Fact]
    public void The_v8_freeze_and_contract_are_untouched_by_the_v9_definition()
    {
        var v8 = V8Keys();
        Assert.Equal(25, v8.Count);   // 24 chiavi della v7 + reconcileSchemaProfile (ReconcileDefinitionV8Tests)
        Assert.Equal(8, DocumentAnalysisWorkflow.ContractOf(Run(v8, V8)));
        Assert.Equal(ReconcileAliasContracts.SchemaProfileByBatch, v8[DocumentAnalysisWorkflow.ReconcileSchemaProfileKey]);
    }

    [Fact]
    public void The_profiles_of_v8_and_v9_are_never_reinterpreted_across_the_two_definitions()
    {
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V8Keys(), V9)));   // profilo -1 su una v9
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V9Keys(), V8)));   // profilo -2 su una v8 (es. un run del Run 3 riscritto)
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V9Keys(), DocumentAnalysisWorkflow.DefinitionVersionV7)));

        var v6 = Keys(SourceSnapshotStore.AnalysisPromptVersionsV6());
        v6[DocumentAnalysisWorkflow.ReconcileSchemaProfileKey] = ReconcileAliasContracts.SchemaProfileByBatchV2;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v6, DocumentAnalysisWorkflow.DefinitionVersionV6)));
    }

    [Theory]
    [InlineData("reconcile-alias-schema-by-batch-3")]
    [InlineData("")]
    public void A_v9_run_with_an_unsupported_profile_is_incompatible(string value)
    {
        var keys = V9Keys();
        keys[DocumentAnalysisWorkflow.ReconcileSchemaProfileKey] = value;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Theory]
    [InlineData("reconcileSchemaProfile")]
    [InlineData("reconcile")]
    [InlineData("retrievalPolicy")]
    [InlineData("gapStage")]
    [InlineData("reconcileAiSchema")]
    public void A_v9_run_missing_any_single_key_is_incompatible(string key)
    {
        var keys = V9Keys();
        keys.Remove(key);
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Fact]
    public void A_v9_run_frozen_with_prompt_v3_is_incompatible_and_never_reinterpreted()
    {
        var keys = V9Keys();
        keys["reconcile"] = ReconcileAliasContracts.PromptVersion;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Fact]
    public void The_message_format_key_is_honoured_by_v9_like_v8()
    {
        var keys = V9Keys();
        keys["analysisMessageFormat"] = "message-format-2";
        Assert.Equal(9, DocumentAnalysisWorkflow.ContractOf(Run(keys)));
        keys["analysisMessageFormat"] = "message-format-9";
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Theory]
    [InlineData("project-analysis-documents-v4", "document-reconcile-v3", null)]
    [InlineData("project-analysis-documents-v7", "document-reconcile-v3", null)]
    [InlineData("project-analysis-documents-v8", "document-reconcile-v4", "reconcile-alias-schema-by-batch-1")]
    [InlineData("project-analysis-documents-v9", "document-reconcile-v4", "reconcile-alias-schema-by-batch-2")]
    public void Each_definition_dispatches_its_own_frozen_reconcile_prompt_and_schema_profile(string definition, string prompt, string? profile)
    {
        Assert.Equal(prompt, DocumentAnalysisWorkflow.ReconcilePromptOf(definition));
        Assert.Equal(profile, DocumentAnalysisWorkflow.ReconcileSchemaProfileOf(definition));
    }

    [Fact]
    public void The_new_documentary_analysis_freeze_is_v9_with_retrieval_and_v6_without()
    {
        Assert.Equal((V9, SourceSnapshotStore.AnalysisPromptVersionsV9(Retrieval)), SourceSnapshotStore.DocumentaryAnalysisFreeze(true, Retrieval));
        Assert.Equal((SourceSnapshotStore.AnalysisDefinitionV6, SourceSnapshotStore.AnalysisPromptVersionsV6()), SourceSnapshotStore.DocumentaryAnalysisFreeze(false, Retrieval));
        Assert.True(bProjects.App.Evidence.DocumentCoverage.AppliesStructuralRepetition(V9));
    }
}
