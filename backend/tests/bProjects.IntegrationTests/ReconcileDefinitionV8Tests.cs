using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003, correzione T040 Run 2 (chiamata 026) — <see cref="DocumentAnalysisWorkflow.ContractOf"/> per la definizione v8: 8 solo con TUTTE le chiavi della v7 e la chiave
/// <c>reconcileSchemaProfile</c> con il solo valore supportato; la chiave in una v6/v7 (o precedente) è una combinazione mista (<c>null</c>, workflow_incompatible), mai reinterpretata.
/// Il freeze v7 non cambia di una chiave: i run v7 esistenti restano v7.
/// </summary>
public sealed class ReconcileDefinitionV8Tests
{
    private const string V8 = "project-analysis-documents-v8";

    private static ProcessingRun Run(Dictionary<string, string> keys, string definition = V8) =>
        new() { WorkflowDefinitionVersion = definition, MafVersion = "1.26.0", PromptVersions = JsonSerializer.Serialize(keys) };

    private static Dictionary<string, string> V7Keys() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(SourceSnapshotStore.AnalysisPromptVersionsV7(new RetrievalOptions { PolicyVersion = RetrievalPolicy.Version1 }))!;

    private static Dictionary<string, string> V8Keys() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(SourceSnapshotStore.AnalysisPromptVersionsV8(new RetrievalOptions { PolicyVersion = RetrievalPolicy.Version1 }))!;

    [Fact]
    public void A_v8_run_with_every_key_is_contract_8_and_its_freeze_is_the_v7_freeze_plus_the_schema_profile()
    {
        var v7 = V7Keys();
        var v8 = V8Keys();

        Assert.Equal(8, DocumentAnalysisWorkflow.ContractOf(Run(v8)));
        Assert.Equal(v7.Count + 1, v8.Count);
        Assert.All(v7, kv => Assert.Equal(kv.Value, v8[kv.Key]));   // nessuna chiave v7 cambiata
        Assert.Equal(ReconcileAliasContracts.SchemaProfileByBatch, v8[DocumentAnalysisWorkflow.ReconcileSchemaProfileKey]);
        Assert.Equal(V8, SourceSnapshotStore.AnalysisDefinitionV8);
        Assert.Equal(V8, DocumentAnalysisWorkflow.DefinitionVersionV8);
    }

    [Fact]
    public void The_v7_freeze_and_contract_are_untouched_by_the_v8_definition()
    {
        var v7 = V7Keys();
        Assert.Equal(24, v7.Count);   // invariato rispetto a RetrievalDefinitionV7Tests
        Assert.DoesNotContain(DocumentAnalysisWorkflow.ReconcileSchemaProfileKey, v7.Keys);
        Assert.Equal(7, DocumentAnalysisWorkflow.ContractOf(Run(v7, DocumentAnalysisWorkflow.DefinitionVersionV7)));
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v7)));   // chiavi v7 su una definizione v8: manca il profilo, mai promosse
    }

    [Fact]
    public void The_schema_profile_key_never_coexists_with_an_older_definition()
    {
        var v8 = V8Keys();
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v8, DocumentAnalysisWorkflow.DefinitionVersionV7)));

        var v6 = JsonSerializer.Deserialize<Dictionary<string, string>>(SourceSnapshotStore.AnalysisPromptVersionsV6())!;
        Assert.Equal(6, DocumentAnalysisWorkflow.ContractOf(Run(v6, DocumentAnalysisWorkflow.DefinitionVersionV6)));
        v6[DocumentAnalysisWorkflow.ReconcileSchemaProfileKey] = ReconcileAliasContracts.SchemaProfileByBatch;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v6, DocumentAnalysisWorkflow.DefinitionVersionV6)));

        var v5 = JsonSerializer.Deserialize<Dictionary<string, string>>(HistoricalAnalysisV5.Versions)!;
        v5[DocumentAnalysisWorkflow.ReconcileSchemaProfileKey] = ReconcileAliasContracts.SchemaProfileByBatch;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v5, DocumentAnalysisWorkflow.DefinitionVersionV5)));
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v5, DocumentAnalysisWorkflow.DefinitionVersionV4)));
    }

    [Theory]
    [InlineData("reconcile-alias-schema-by-batch-2")]
    [InlineData("")]
    public void A_v8_run_with_an_unsupported_profile_is_incompatible(string value)
    {
        var keys = V8Keys();
        keys[DocumentAnalysisWorkflow.ReconcileSchemaProfileKey] = value;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Theory]
    [InlineData("reconcileSchemaProfile")]
    [InlineData("retrievalPolicy")]
    [InlineData("gapStage")]
    [InlineData("reconcileAiSchema")]
    public void A_v8_run_missing_any_single_key_is_incompatible(string key)
    {
        var keys = V8Keys();
        keys.Remove(key);
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Fact]
    public void The_message_format_key_is_honoured_by_v8_like_v7()
    {
        var keys = V8Keys();
        keys["analysisMessageFormat"] = "message-format-2";
        Assert.Equal(8, DocumentAnalysisWorkflow.ContractOf(Run(keys)));
        keys["analysisMessageFormat"] = "message-format-9";
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Fact]
    public void Every_schema_variant_stays_within_a_few_characters_of_the_full_schema_measured_by_the_pack()
    {
        // Il Pack misura con lo schema completo; la rimisura usa la variante. Lo scarto massimo (Candidate = completo + "maxItems":0) deve restare trascurabile rispetto alla riserva
        // di intestazione del Pack (256 token): una variante molto più grande sposterebbe il rifiuto document_limit_exceeded dalla pianificazione all'invio.
        var full = ReconcileAliasContracts.JsonSchema().GetRawText().Length;
        Assert.All(Enum.GetValues<ReconcileSchemaVariant>(), v => Assert.True(ReconcileAliasContracts.SchemaOf(v).GetRawText().Length - full <= 32, v.ToString()));
    }

    [Fact]
    public void V8_shares_the_v7_reconcile_contract_versions_and_only_adds_the_profile()
    {
        // Stesso prompt, stesso DTO e stesso algoritmo della gerarchia: il profilo cambia solo lo schema inviato al modello, non ciò che viene validato o persistito.
        var keys = V8Keys();
        Assert.Equal(ReconcileAliasContracts.PromptVersion, keys["reconcile"]);
        Assert.Equal(ReconcileAliasContracts.SchemaVersion, keys["reconcileAiSchema"]);
        Assert.Equal(V7Keys()["reconcileAlgorithm"], keys["reconcileAlgorithm"]);
        Assert.True(bProjects.App.Evidence.DocumentCoverage.AppliesStructuralRepetition(V8));
    }
}
