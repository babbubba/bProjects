using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003, T040 dopo il Run 4 — <see cref="DocumentAnalysisWorkflow.ContractOf"/> per la definizione v10: 10 solo con TUTTE le chiavi della v9 con prompt <c>document-reconcile-v5</c>,
/// <c>reconcileSchemaProfile = reconcile-alias-schema-by-batch-3</c> e <c>reconcileExpansion = expansion-grid-1</c>. Nessuna reinterpretazione fra v9 e v10 e il freeze v9 resta quello storico, byte per byte.
/// Le nuove richieste con recupero congelano la v10 tramite il normale percorso del prodotto (<see cref="SourceSnapshotStore.DocumentaryAnalysisFreeze"/>); le analisi v9 già congelate restano v9.
/// </summary>
public sealed class ReconcileDefinitionV10Tests
{
    private const string V9 = "project-analysis-documents-v9";
    private const string V10 = "project-analysis-documents-v10";

    private static readonly RetrievalOptions Retrieval = new() { PolicyVersion = RetrievalPolicy.Version1 };

    private static ProcessingRun Run(Dictionary<string, string> keys, string definition = V10) =>
        new() { WorkflowDefinitionVersion = definition, MafVersion = "1.26.0", PromptVersions = JsonSerializer.Serialize(keys) };

    private static Dictionary<string, string> Keys(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

    private static Dictionary<string, string> V9Keys() => Keys(SourceSnapshotStore.AnalysisPromptVersionsV9(Retrieval));

    private static Dictionary<string, string> V10Keys() => Keys(SourceSnapshotStore.AnalysisPromptVersionsV10(Retrieval));

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void A_v10_run_with_every_key_is_contract_10_and_differs_from_the_v9_freeze_only_by_prompt_profile_and_the_expansion_key()
    {
        var v9 = V9Keys();
        var v10 = V10Keys();

        Assert.Equal(10, DocumentAnalysisWorkflow.ContractOf(Run(v10)));
        Assert.Equal([DocumentAnalysisWorkflow.ReconcileExpansionKey], v10.Keys.Except(v9.Keys));        // una sola chiave aggiunta
        Assert.Empty(v9.Keys.Except(v10.Keys));
        Assert.Equal(["reconcile", DocumentAnalysisWorkflow.ReconcileSchemaProfileKey], v9.Where(kv => v10[kv.Key] != kv.Value).Select(kv => kv.Key).Order(StringComparer.Ordinal));
        Assert.Equal(ReconcileAliasContracts.PromptVersionV5, v10["reconcile"]);
        Assert.Equal("reconcile-alias-schema-by-batch-3", v10[DocumentAnalysisWorkflow.ReconcileSchemaProfileKey]);
        Assert.Equal("expansion-grid-1", v10[DocumentAnalysisWorkflow.ReconcileExpansionKey]);
        Assert.Equal((V10, V10), (SourceSnapshotStore.AnalysisDefinitionV10, DocumentAnalysisWorkflow.DefinitionVersionV10));
    }

    [Fact]
    public void The_v9_freeze_and_contract_are_byte_for_byte_the_historical_ones()
    {
        var v9 = V9Keys();
        Assert.Equal(25, v9.Count);
        Assert.Equal(9, DocumentAnalysisWorkflow.ContractOf(Run(v9, V9)));
        Assert.Equal(ReconcileAliasContracts.PromptVersionV4, v9["reconcile"]);
        Assert.Equal(ReconcileAliasContracts.SchemaProfileByBatchV2, v9[DocumentAnalysisWorkflow.ReconcileSchemaProfileKey]);
        Assert.False(v9.ContainsKey(DocumentAnalysisWorkflow.ReconcileExpansionKey));
        Assert.Equal(26, V10Keys().Count);
    }

    [Fact]
    public void The_v9_and_v10_definitions_are_never_reinterpreted_across_each_other()
    {
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V9Keys(), V10)));   // freeze v9 su una definizione v10
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V10Keys(), V9)));   // freeze v10 su una definizione v9: la chiave dell'espansione non esiste in v9
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V10Keys(), DocumentAnalysisWorkflow.DefinitionVersionV8)));
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V10Keys(), DocumentAnalysisWorkflow.DefinitionVersionV7)));

        var v6 = Keys(SourceSnapshotStore.AnalysisPromptVersionsV6());
        v6[DocumentAnalysisWorkflow.ReconcileExpansionKey] = ReconcileAliasContracts.ExpansionAlgorithm;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v6, DocumentAnalysisWorkflow.DefinitionVersionV6)));
    }

    [Theory]
    [InlineData("reconcile-alias-schema-by-batch-2")]
    [InlineData("reconcile-alias-schema-by-batch-1")]
    [InlineData("reconcile-alias-schema-by-batch-4")]
    [InlineData("")]
    public void A_v10_run_with_an_unsupported_profile_is_incompatible(string value)
    {
        var keys = V10Keys();
        keys[DocumentAnalysisWorkflow.ReconcileSchemaProfileKey] = value;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Theory]
    [InlineData("expansion-grid-2")]
    [InlineData("expansion-grid-0")]
    [InlineData("")]
    public void A_v10_run_with_an_unsupported_expansion_algorithm_is_incompatible(string value)
    {
        var keys = V10Keys();
        keys[DocumentAnalysisWorkflow.ReconcileExpansionKey] = value;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Theory]
    [InlineData("reconcileExpansion")]
    [InlineData("reconcileSchemaProfile")]
    [InlineData("reconcile")]
    [InlineData("retrievalPolicy")]
    [InlineData("gapStage")]
    [InlineData("reconcileAiSchema")]
    public void A_v10_run_missing_any_single_key_is_incompatible(string key)
    {
        var keys = V10Keys();
        keys.Remove(key);
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Theory]
    [InlineData("document-reconcile-v3")]
    [InlineData("document-reconcile-v4")]
    [InlineData("document-reconcile-v6")]
    public void A_v10_run_frozen_with_another_reconcile_prompt_is_incompatible_and_never_reinterpreted(string prompt)
    {
        var keys = V10Keys();
        keys["reconcile"] = prompt;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Fact]
    public void The_message_format_key_is_honoured_by_v10_like_v9()
    {
        var keys = V10Keys();
        keys["analysisMessageFormat"] = "message-format-2";
        Assert.Equal(10, DocumentAnalysisWorkflow.ContractOf(Run(keys)));
        keys["analysisMessageFormat"] = "message-format-9";
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Theory]
    [InlineData("project-analysis-documents-v4", "document-reconcile-v3", null, null)]
    [InlineData("project-analysis-documents-v7", "document-reconcile-v3", null, null)]
    [InlineData("project-analysis-documents-v8", "document-reconcile-v4", "reconcile-alias-schema-by-batch-1", null)]
    [InlineData("project-analysis-documents-v9", "document-reconcile-v4", "reconcile-alias-schema-by-batch-2", null)]
    [InlineData("project-analysis-documents-v10", "document-reconcile-v5", "reconcile-alias-schema-by-batch-3", "expansion-grid-1")]
    public void Each_definition_dispatches_its_own_frozen_reconcile_prompt_schema_profile_and_expansion(string definition, string prompt, string? profile, string? expansion)
    {
        Assert.Equal(prompt, DocumentAnalysisWorkflow.ReconcilePromptOf(definition));
        Assert.Equal(profile, DocumentAnalysisWorkflow.ReconcileSchemaProfileOf(definition));
        Assert.Equal(expansion, DocumentAnalysisWorkflow.ReconcileExpansionOf(definition));
    }

    [Fact]
    public void The_new_documentary_analysis_freeze_is_v10_with_retrieval_and_v6_without_while_the_v9_freeze_remains_available()
    {
        Assert.Equal((V10, SourceSnapshotStore.AnalysisPromptVersionsV10(Retrieval)), SourceSnapshotStore.DocumentaryAnalysisFreeze(true, Retrieval));
        Assert.Equal((SourceSnapshotStore.AnalysisDefinitionV6, SourceSnapshotStore.AnalysisPromptVersionsV6()), SourceSnapshotStore.DocumentaryAnalysisFreeze(false, Retrieval));
        Assert.Equal(9, DocumentAnalysisWorkflow.ContractOf(Run(V9Keys(), V9)));   // un'analisi v9 già congelata continua a essere v9
        Assert.True(bProjects.App.Evidence.DocumentCoverage.AppliesStructuralRepetition(V10));
    }

    [Fact]
    public void The_historical_reconcile_prompts_v3_and_v4_are_byte_for_byte_the_ones_frozen_in_the_manifests_and_v5_only_adds_the_expansion_rules()
    {
        // v4: SHA-256 del file registrato nel manifesto T040 v5 (`d3e46528…a79d`, invariato dal Run 3); l'istruzione incorporata è lo stesso testo senza spazi di coda.
        string File(string name) => System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "bProjects.App", "AI", "Prompts", name));
        Assert.Equal("d3e46528736e856eedfa43e1fa41785b0a87ab7ddba91881f57bb9c09c0da79d", Sha(File("document-reconcile-v4.md")));
        Assert.Equal("0503d17360e21e8f72e357d1fcf0f0e32fbead294d970193af4eb61ff847c9e5", Sha(File("document-reconcile-v3.md")));   // già bloccato da DocumentGapStageContractsTests
        Assert.Equal(File("document-reconcile-v4.md").Trim(), ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV4));
        Assert.Equal(File("document-reconcile-v5.md").Trim(), ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV5));

        var v4 = ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV4).Split('\n');
        var v5 = ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV5).Split('\n');
        Assert.Equal(v4.Length, v5.Length);
        var changed = Enumerable.Range(0, v4.Length).Where(i => v4[i] != v5[i]).ToList();
        Assert.Equal(3, changed.Count);                                              // solo le righe di Input (VOCE, candidato in esame) e di `candidates`
        Assert.All(changed, i => Assert.True(v4[i].StartsWith("- Evidenze:", StringComparison.Ordinal) || v4[i].StartsWith("- `Candidato in esame:`", StringComparison.Ordinal) || v4[i].StartsWith("- `candidates`:", StringComparison.Ordinal)));
        Assert.Contains("gruppo=N1", ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV5), StringComparison.Ordinal);
        Assert.Contains("al massimo 16", ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV5), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.InstructionsFor("document-reconcile-v6"));
    }
}
