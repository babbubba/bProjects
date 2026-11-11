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
    public void A_v8_run_with_every_key_is_contract_8_and_its_freeze_is_the_v7_freeze_with_prompt_v4_plus_the_schema_profile()
    {
        var v7 = V7Keys();
        var v8 = V8Keys();

        Assert.Equal(8, DocumentAnalysisWorkflow.ContractOf(Run(v8)));
        Assert.Equal(v7.Count + 1, v8.Count);
        Assert.All(v7.Where(kv => kv.Key != "reconcile"), kv => Assert.Equal(kv.Value, v8[kv.Key]));   // ogni altra chiave v7 invariata
        Assert.Equal(("document-reconcile-v3", "document-reconcile-v4"), (v7["reconcile"], v8["reconcile"]));
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
    public void V8_uses_prompt_v4_and_shares_every_other_reconcile_contract_version_with_v7()
    {
        // Prompt v4 (chiarimento delle foglie); stesso DTO, stesso algoritmo e stesso catalogo della gerarchia: nulla cambia in ciò che viene validato o persistito.
        var keys = V8Keys();
        Assert.Equal(ReconcileAliasContracts.PromptVersionV4, keys["reconcile"]);
        Assert.Equal(ReconcileAliasContracts.SchemaVersion, keys["reconcileAiSchema"]);
        Assert.Equal(V7Keys()["reconcileAlgorithm"], keys["reconcileAlgorithm"]);
        Assert.Equal(V7Keys()["reconcileCatalog"], keys["reconcileCatalog"]);
        Assert.True(bProjects.App.Evidence.DocumentCoverage.AppliesStructuralRepetition(V8));
    }

    // ================================================================ prompt document-reconcile-v4 (completamento della v8)

    private const string ReconcileV3PromptSha = "0503d17360e21e8f72e357d1fcf0f0e32fbead294d970193af4eb61ff847c9e5";   // invariato (DocumentGapStageContractsTests)
    private const string ReconcileV4PromptSha = "d3e46528736e856eedfa43e1fa41785b0a87ab7ddba91881f57bb9c09c0da79d";

    private static string Sha(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private static string PromptFile(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "bProjects.App", "AI", "Prompts", file));

    private static string Embedded(string version)
    {
        using var stream = typeof(ReconcileAliasContracts).Assembly.GetManifestResourceStream($"Prompts.{version}.md")
            ?? throw new InvalidOperationException($"Prompt {version} non incorporato.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // I soli frammenti aggiunti dal v4 al testo del v3.
    private const string ItemsScope = ", comprese le foglie della vista B";
    private const string CoverageScopeV4 = "Nelle foglie della vista A, e solo in quelle, ogni evidenza del batch deve essere citata da almeno un elemento, con qualificatori, negazioni e condizioni; negli altri batch questo obbligo di copertura non esiste.";
    private const string CoverageV3 = "Ogni evidenza del batch deve essere citata da almeno un elemento, con qualificatori, negazioni e condizioni.";
    private const string LeafBTask = " Nelle foglie della vista B il compito è soltanto individuare questi candidati fra le evidenze del batch: non serve citare ogni evidenza e non vanno inventati collegamenti; se non ne individui, `candidates` è `[]` e `noFindings` motiva l'assenza.";

    [Fact]
    public void Prompt_v4_is_a_new_embedded_resource_and_prompt_v3_is_byte_for_byte_unchanged()
    {
        Assert.Equal(ReconcileV3PromptSha, Sha(PromptFile("document-reconcile-v3.md")));
        Assert.Equal(ReconcileV4PromptSha, Sha(PromptFile("document-reconcile-v4.md")));
        Assert.Equal(ReconcileV3PromptSha, Sha(Embedded("document-reconcile-v3")));   // la risorsa incorporata è il file, senza trasformazioni
        Assert.Equal(ReconcileV4PromptSha, Sha(Embedded("document-reconcile-v4")));
        // Le istruzioni inviate sono la risorsa con lo stesso Trim() di ogni prompt incorporato.
        Assert.Equal(Embedded("document-reconcile-v3").Trim(), ReconcileAliasContracts.Instructions);
        Assert.Equal(Embedded("document-reconcile-v3").Trim(), ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersion));
        Assert.Equal(Embedded("document-reconcile-v4").Trim(), ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV4));
        Assert.Equal(("document-reconcile-v3", "document-reconcile-v4"), (ReconcileAliasContracts.PromptVersion, ReconcileAliasContracts.PromptVersionV4));
    }

    [Fact]
    public void Prompt_v4_differs_from_v3_only_by_the_leaf_clarification_and_keeps_every_other_rule_verbatim()
    {
        var v3 = Embedded("document-reconcile-v3");
        var v4 = Embedded("document-reconcile-v4");

        // Tolti i soli frammenti del chiarimento, il v4 è il v3 byte per byte: conflitti, candidati, focus, patch, etichette e formato restano identici.
        var reverted = v4.Replace(ItemsScope, string.Empty, StringComparison.Ordinal).Replace(CoverageScopeV4, CoverageV3, StringComparison.Ordinal).Replace(LeafBTask, string.Empty, StringComparison.Ordinal);
        Assert.Equal(v3, reverted);
        Assert.Equal(1, Occurrences(v4, ItemsScope));
        Assert.Equal(1, Occurrences(v4, CoverageScopeV4));
        Assert.Equal(1, Occurrences(v4, LeafBTask));
        Assert.Equal(0, Occurrences(v4, CoverageV3));   // l'obbligo di copertura non compare più senza il vincolo «solo foglie A»

        // La regola sui conflitti è letteralmente quella del v3.
        const string Conflicts = "Se due formulazioni sembrano incompatibili o diverse nei numeri, nelle date o nelle condizioni, proponi il candidato: non decidere quale sia giusta e non fonderle.";
        Assert.Equal(1, Occurrences(v3, Conflicts));
        Assert.Equal(1, Occurrences(v4, Conflicts));
    }

    private static int Occurrences(string text, string fragment)
    {
        var count = 0;
        for (var i = text.IndexOf(fragment, StringComparison.Ordinal); i >= 0; i = text.IndexOf(fragment, i + fragment.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Theory]
    [InlineData("project-analysis-documents-v4", "document-reconcile-v3")]
    [InlineData("project-analysis-documents-v5", "document-reconcile-v3")]
    [InlineData("project-analysis-documents-v6", "document-reconcile-v3")]
    [InlineData("project-analysis-documents-v7", "document-reconcile-v3")]
    [InlineData("project-analysis-documents-v8", "document-reconcile-v4")]
    public void Each_definition_dispatches_its_own_frozen_reconcile_prompt(string definition, string prompt) =>
        Assert.Equal(prompt, DocumentAnalysisWorkflow.ReconcilePromptOf(definition));

    [Theory]
    [InlineData("document-reconcile-v2")]
    [InlineData("document-reconcile-v5")]
    [InlineData("")]
    public void An_unknown_reconcile_prompt_version_is_rejected_and_never_falls_back(string version) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.InstructionsFor(version));

    [Fact]
    public void A_v8_run_frozen_with_prompt_v3_is_incompatible_and_never_reinterpreted()
    {
        // Forma della v8 prima del completamento (prompt v3): nessun run così è stato avviato; se esistesse resterebbe workflow_incompatible, mai promosso al v4.
        var keys = V8Keys();
        keys["reconcile"] = ReconcileAliasContracts.PromptVersion;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(keys)));
    }

    [Fact]
    public void Prompt_v4_never_validates_an_older_definition()
    {
        var v7 = V7Keys();
        v7["reconcile"] = ReconcileAliasContracts.PromptVersionV4;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v7, DocumentAnalysisWorkflow.DefinitionVersionV7)));

        var v6 = JsonSerializer.Deserialize<Dictionary<string, string>>(SourceSnapshotStore.AnalysisPromptVersionsV6())!;
        v6["reconcile"] = ReconcileAliasContracts.PromptVersionV4;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v6, DocumentAnalysisWorkflow.DefinitionVersionV6)));

        var v5 = JsonSerializer.Deserialize<Dictionary<string, string>>(HistoricalAnalysisV5.Versions)!;
        Assert.Equal(5, DocumentAnalysisWorkflow.ContractOf(Run(v5, DocumentAnalysisWorkflow.DefinitionVersionV5)));
        v5["reconcile"] = ReconcileAliasContracts.PromptVersionV4;
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(v5, DocumentAnalysisWorkflow.DefinitionVersionV5)));
    }
}
