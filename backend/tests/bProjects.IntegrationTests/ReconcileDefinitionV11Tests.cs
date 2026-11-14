using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-010 (M1) — identità della definizione v11, INATTIVA: <see cref="DocumentAnalysisWorkflow.ContractOf"/> restituisce 11 solo con l'insieme esatto delle chiavi della v6
/// con <c>reconcile = document-reconcile-v6</c>, <c>reconcileSchemaProfile = reconcile-alias-schema-by-stage-1</c>, <c>reconcileExpansion = expansion-grid-1</c>, <c>reconcileContract = reconcile-stage-contract-1</c>
/// e nessuna chiave <c>retrieval*</c>. Nessuna combinazione mista è reinterpretata; i freeze v6…v10 sono invariati byte per byte (golden) e nessuna nuova richiesta congela la v11
/// (<see cref="SourceSnapshotStore.DocumentaryAnalysisFreeze"/> resta v6/v10). Nessun database, nessuna inferenza.
/// </summary>
public sealed class ReconcileDefinitionV11Tests
{
    private const string V6 = "project-analysis-documents-v6";
    private const string V10 = "project-analysis-documents-v10";
    private const string V11 = "project-analysis-documents-v11";

    private static readonly RetrievalOptions Retrieval = new() { PolicyVersion = RetrievalPolicy.Version1 };

    private static ProcessingRun Run(Dictionary<string, string> keys, string definition = V11) =>
        new() { WorkflowDefinitionVersion = definition, MafVersion = "1.26.0", PromptVersions = JsonSerializer.Serialize(keys) };

    private static Dictionary<string, string> Keys(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

    private static Dictionary<string, string> V6Keys() => Keys(SourceSnapshotStore.AnalysisPromptVersionsV6());

    private static Dictionary<string, string> V10Keys() => Keys(SourceSnapshotStore.AnalysisPromptVersionsV10(Retrieval));

    private static Dictionary<string, string> V11Keys() => Keys(SourceSnapshotStore.AnalysisPromptVersionsV11());

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ---------------------------------------------------------------- identità

    [Fact]
    public void A_v11_run_with_every_key_is_contract_11_and_the_freeze_differs_from_the_v6_freeze_in_exactly_four_keys()
    {
        var v6 = V6Keys();
        var v11 = V11Keys();

        Assert.Equal(11, DocumentAnalysisWorkflow.ContractOf(Run(v11)));
        Assert.Equal(
            [DocumentAnalysisWorkflow.ReconcileContractKey, DocumentAnalysisWorkflow.ReconcileExpansionKey, DocumentAnalysisWorkflow.ReconcileSchemaProfileKey],
            v11.Keys.Except(v6.Keys).Order(StringComparer.Ordinal));                                   // tre chiavi aggiunte
        Assert.Empty(v6.Keys.Except(v11.Keys));
        Assert.Equal(["reconcile"], v6.Where(kv => v11[kv.Key] != kv.Value).Select(kv => kv.Key));        // una chiave cambia di valore: il prompt
        Assert.Equal(4, v11.Count(kv => !v6.TryGetValue(kv.Key, out var old) || old != kv.Value));         // in totale esattamente quattro differenze
        Assert.Equal(v6.Count + 3, v11.Count);

        Assert.Equal("document-reconcile-v6", v11["reconcile"]);
        Assert.Equal("reconcile-alias-schema-by-stage-1", v11[DocumentAnalysisWorkflow.ReconcileSchemaProfileKey]);
        Assert.Equal("expansion-grid-1", v11[DocumentAnalysisWorkflow.ReconcileExpansionKey]);
        Assert.Equal("reconcile-stage-contract-1", v11[DocumentAnalysisWorkflow.ReconcileContractKey]);
        Assert.DoesNotContain(v11.Keys, k => k.StartsWith("retrieval", StringComparison.Ordinal));
        Assert.Equal((V11, V11), (SourceSnapshotStore.AnalysisDefinitionV11, DocumentAnalysisWorkflow.DefinitionVersionV11));
    }

    [Fact]
    public void The_v11_definition_is_never_reinterpreted_as_another_definition_and_vice_versa()
    {
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V6Keys())));                                // freeze v6 su una definizione v11
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V10Keys())));                               // freeze v10 su una definizione v11
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V11Keys(), V6)));                           // freeze v11 su una definizione v6: le chiavi della v11 non esistono in v6
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V11Keys(), V10)));
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V11Keys(), DocumentAnalysisWorkflow.DefinitionVersionV9)));
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(V11Keys(), DocumentAnalysisWorkflow.DefinitionVersionV7)));
    }

    [Fact]
    public void The_new_v11_contract_key_is_a_mixed_combination_on_every_other_definition_including_v6_and_v10_while_their_historical_freezes_stay_valid()
    {
        var v6 = V6Keys();
        v6[DocumentAnalysisWorkflow.ReconcileContractKey] = ReconcileAliasContracts.StageContract;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v6, V6)));

        var v10 = V10Keys();
        v10[DocumentAnalysisWorkflow.ReconcileContractKey] = ReconcileAliasContracts.StageContract;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v10, V10)));

        Assert.Equal(6, DocumentAnalysisWorkflow.ContractOf(Run(V6Keys(), V6)));
        Assert.Equal(10, DocumentAnalysisWorkflow.ContractOf(Run(V10Keys(), V10)));                       // il ramo v10 non è stato toccato: la v10 storica resta 10
    }

    [Fact]
    public void A_v11_run_carrying_any_retrieval_key_is_incompatible_because_the_v11_does_not_use_retrieval()
    {
        var retrievalKeys = RetrievalStage.FreezeValues(Retrieval).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.NotEmpty(retrievalKeys);
        foreach (var (key, value) in retrievalKeys)
        {
            var keys = V11Keys();
            keys[key] = value;
            Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
        }

        var all = V11Keys();
        foreach (var (key, value) in retrievalKeys)
        {
            all[key] = value;
        }

        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(all)));
    }

    [Theory]
    [InlineData("reconcile", "document-reconcile-v5")]
    [InlineData("reconcile", "document-reconcile-v4")]
    [InlineData("reconcile", "document-reconcile-v3")]
    [InlineData("reconcile", "document-reconcile-v7")]
    [InlineData("reconcileSchemaProfile", "reconcile-alias-schema-by-batch-3")]
    [InlineData("reconcileSchemaProfile", "reconcile-alias-schema-by-batch-2")]
    [InlineData("reconcileSchemaProfile", "reconcile-alias-schema-by-stage-2")]
    [InlineData("reconcileSchemaProfile", "")]
    [InlineData("reconcileExpansion", "expansion-grid-2")]
    [InlineData("reconcileExpansion", "")]
    [InlineData("reconcileContract", "reconcile-stage-contract-2")]
    [InlineData("reconcileContract", "")]
    [InlineData("gapStage", "gap-stage-2")]
    [InlineData("analysisOutputPolicy", "single-call-output-estimate-2")]
    public void A_v11_run_with_a_different_value_is_incompatible_and_never_reinterpreted(string key, string value)
    {
        var keys = V11Keys();
        keys[key] = value;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Theory]
    [InlineData("reconcile")]
    [InlineData("reconcileSchemaProfile")]
    [InlineData("reconcileExpansion")]
    [InlineData("reconcileContract")]
    [InlineData("gapStage")]
    [InlineData("reconcileAiSchema")]
    [InlineData("analysisOutputPolicy")]
    [InlineData("analysis")]
    public void A_v11_run_missing_any_single_key_is_incompatible(string key)
    {
        var keys = V11Keys();
        Assert.True(keys.Remove(key));
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Fact]
    public void A_v11_run_without_frozen_versions_or_with_another_maf_version_is_not_comparable()
    {
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(new ProcessingRun { WorkflowDefinitionVersion = V11, MafVersion = "1.26.0", PromptVersions = null }));
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(new ProcessingRun { WorkflowDefinitionVersion = V11, MafVersion = "1.25.0", PromptVersions = JsonSerializer.Serialize(V11Keys()) }));
    }

    [Fact]
    public void The_message_format_key_is_honoured_by_v11_like_v6_and_v10_and_an_unknown_value_is_incompatible()
    {
        var keys = V11Keys();
        Assert.Equal("message-format-2", keys["analysisMessageFormat"]);                      // ereditata dal freeze v6
        Assert.Equal(11, DocumentAnalysisWorkflow.ContractOf(Run(keys)));
        Assert.Equal(DocumentAnalysisWorkflow.MessageFormatState.V2, DocumentAnalysisWorkflow.MessageFormatOf(Run(keys)));
        keys["analysisMessageFormat"] = "message-format-9";
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
        keys.Remove("analysisMessageFormat");
        Assert.Equal(11, DocumentAnalysisWorkflow.ContractOf(Run(keys)));                      // assenza = formato 1 storico: la v11 la accetta come la v6
        Assert.Equal(DocumentAnalysisWorkflow.MessageFormatState.V1, DocumentAnalysisWorkflow.MessageFormatOf(Run(keys)));
    }

    // ---------------------------------------------------------------- dispatch delle chiavi congelate

    [Theory]
    [InlineData("project-analysis-documents-v6", "document-reconcile-v3", null, null, null)]
    [InlineData("project-analysis-documents-v8", "document-reconcile-v4", "reconcile-alias-schema-by-batch-1", null, null)]
    [InlineData("project-analysis-documents-v10", "document-reconcile-v5", "reconcile-alias-schema-by-batch-3", "expansion-grid-1", null)]
    [InlineData("project-analysis-documents-v11", "document-reconcile-v6", "reconcile-alias-schema-by-stage-1", "expansion-grid-1", "reconcile-stage-contract-1")]
    public void Each_definition_dispatches_its_own_frozen_prompt_profile_expansion_and_stage_contract(string definition, string prompt, string? profile, string? expansion, string? contract)
    {
        Assert.Equal(prompt, DocumentAnalysisWorkflow.ReconcilePromptOf(definition));
        Assert.Equal(profile, DocumentAnalysisWorkflow.ReconcileSchemaProfileOf(definition));
        Assert.Equal(expansion, DocumentAnalysisWorkflow.ReconcileExpansionOf(definition));
        Assert.Equal(contract, DocumentAnalysisWorkflow.ReconcileContractOf(definition));
    }

    [Fact]
    public void The_v11_single_call_inputs_are_those_of_the_v6_only_the_definition_and_the_hierarchy_keys_differ()
    {
        // Il single-call si costruisce da: prompt di analisi (GapPromptOf), formato del messaggio, policy di stima dell'output, schema alias-2, catalogo alias-2. Tutti leggono le chiavi del single-call, identiche in v6 e v11.
        var (v6, v11) = (Run(V6Keys(), V6), Run(V11Keys()));
        Assert.Equal(DocumentAnalysisWorkflow.GapPromptOf(v6), DocumentAnalysisWorkflow.GapPromptOf(v11));
        Assert.Equal(DocumentAnalysisWorkflow.MessageFormatOf(v6), DocumentAnalysisWorkflow.MessageFormatOf(v11));
        Assert.Equal(DocumentAnalysisWorkflow.OutputPolicyOf(v6), DocumentAnalysisWorkflow.OutputPolicyOf(v11));
        Assert.Equal(DocumentAnalysisWorkflow.OutputPolicyState.Current, DocumentAnalysisWorkflow.OutputPolicyOf(v11));

        var (a, b) = (V6Keys(), V11Keys());
        foreach (var key in new[] { "analysis", "analysisSchema", "analysisAiSchema", "aliasCatalog", "canonIds", "algorithm", "validation", "analysisOutputPolicy", "analysisMessageFormat" })
        {
            Assert.Equal(a[key], b[key]);
        }
    }

    [Fact]
    public void The_v11_is_a_documentary_definition_like_the_v6_for_the_structural_repetition_warning()
    {
        Assert.True(DocumentCoverage.AppliesStructuralRepetition(V11));
        Assert.True(DocumentCoverage.AppliesStructuralRepetition(V6));
        Assert.False(DocumentCoverage.AppliesStructuralRepetition("project-analysis-documents-v5"));
    }

    // ---------------------------------------------------------------- freeze storici invariati e v11 non default

    /// <summary>SHA-256 dei JSON dei freeze (golden RH-010): calcolati sul codice che NON li ha toccati (le funzioni <c>AnalysisPromptVersionsV6…V10</c> e le loro costanti non sono nel diff di M1).</summary>
    private static readonly IReadOnlyDictionary<string, string> FreezeGoldens = new Dictionary<string, string>
    {
        ["v6"] = "a6e88805ca52069647487de047ca9d2c8733dba3f7920a35c21f15c932196a15",
        ["v7"] = "fd3e3cc8f50b9254a655faaca00f08fe131a18a6cc1b5d264ba75f275c008134",
        ["v8"] = "1cddf5d8082f2535a7fcf57a4f1e9a716061b07c058eb3ba7817d89ff9681af8",
        ["v9"] = "dcba005b5e70c05b2f8f36308902367211686698a6f28869d57151b572bba384",
        ["v10"] = "e450378ede84eee38fe097a0b70bdc73d3932999c422193249e4c73c8fd1d63a",
    };

    [Fact]
    public void The_freezes_of_v6_to_v10_are_byte_for_byte_the_historical_ones()
    {
        var actual = new Dictionary<string, string>
        {
            ["v6"] = Sha(SourceSnapshotStore.AnalysisPromptVersionsV6()),
            ["v7"] = Sha(SourceSnapshotStore.AnalysisPromptVersionsV7(Retrieval)),
            ["v8"] = Sha(SourceSnapshotStore.AnalysisPromptVersionsV8(Retrieval)),
            ["v9"] = Sha(SourceSnapshotStore.AnalysisPromptVersionsV9(Retrieval)),
            ["v10"] = Sha(SourceSnapshotStore.AnalysisPromptVersionsV10(Retrieval)),
        };
        var expectedText = string.Join('\n', FreezeGoldens.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        var actualText = string.Join('\n', actual.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        Assert.True(expectedText == actualText, "Freeze storici diversi dai golden. Valori reali:\n" + actualText);
    }

    [Fact]
    public void The_documentary_analysis_freeze_is_unchanged_v10_with_retrieval_and_v6_without_and_never_v11()
    {
        Assert.Equal((V10, SourceSnapshotStore.AnalysisPromptVersionsV10(Retrieval)), SourceSnapshotStore.DocumentaryAnalysisFreeze(true, Retrieval));
        Assert.Equal((V6, SourceSnapshotStore.AnalysisPromptVersionsV6()), SourceSnapshotStore.DocumentaryAnalysisFreeze(false, Retrieval));
        Assert.NotEqual(V11, SourceSnapshotStore.DocumentaryAnalysisFreeze(true, Retrieval).Definition);
        Assert.NotEqual(V11, SourceSnapshotStore.DocumentaryAnalysisFreeze(false, Retrieval).Definition);
        Assert.NotEqual(SourceSnapshotStore.AnalysisPromptVersionsV11(), SourceSnapshotStore.DocumentaryAnalysisFreeze(false, Retrieval).PromptVersions);
        Assert.Equal(6, DocumentAnalysisWorkflow.ContractOf(Run(Keys(SourceSnapshotStore.DocumentaryAnalysisFreeze(false, Retrieval).PromptVersions), V6)));
    }
}
