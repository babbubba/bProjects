using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-015 (M1) — il piano di riconciliazione riconosce la definizione v11: ramo di <c>configHash</c> con <c>stageContract</c>, chiave <c>reconcileContract</c> nel piano JSON, validazione v2
/// (<c>reconcile-validation-v2</c>) e opzioni del compositore del contratto per stadio, schema LeafA del profilo <c>by-stage-1</c> e istruzioni v6 sul primo batch. Il piano di produzione è costruito per riflessione
/// con il runtime llama.cpp finto di RH-004 (nessuna rete, nessun database, nessuna inferenza). Golden di <c>configHash</c> e piano di v6, v8, v9 e v10 catturati PRIMA della modifica del piano. In M1 la v11 non è
/// funzionante end-to-end (le foglie A non producono item e la proiezione è di M2): qui si verifica solo l'integrazione di identità, schema, istruzioni, validazione e composizione.
/// </summary>
public sealed class ReconcileDefinitionV11PlanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string V6 = DocumentAnalysisWorkflow.DefinitionVersionV6;
    private const string V8 = DocumentAnalysisWorkflow.DefinitionVersionV8;
    private const string V9 = DocumentAnalysisWorkflow.DefinitionVersionV9;
    private const string V10 = DocumentAnalysisWorkflow.DefinitionVersionV10;
    private const string V11 = DocumentAnalysisWorkflow.DefinitionVersionV11;

    private sealed class NoOutputs : IBatchOutputs
    {
        public ValueTask<WorkOutput?> GetByOrdinalAsync(int ordinal, CancellationToken ct) => new((WorkOutput?)null);
    }

    private sealed record Prepared(MeasurementRig Rig, IDocumentBatchPlan Plan, DerivedBatchInput First) : IDisposable
    {
        public void Dispose() => Rig.Dispose();
    }

    private static async Task<Prepared> PrepareAsync(string definition)
    {
        var input = ReconcileMeasurementBudgetTests.SingleInput();
        var rig = await MeasurementRig.CreateAsync(Ct);
        var catalog = AnalysisAliasCatalog.Build(input, DocumentAnalysisWorkflow.SchemaVersionV3);
        var scopes = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider().GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var plan = ReconcileMeasurementBudgetTests.NewPlan(rig, input, new Dictionary<Guid, DirectoryFacts>(), catalog, definition, new DocumentOptions().ToLimits(), scopes);
        var first = await plan.PrepareAsync(0, new NoOutputs(), Ct);
        return new Prepared(rig, plan, first);
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ---------------------------------------------------------------- identità: golden storici e v11 distinta

    /// <summary><c>configHash</c> del manifesto e SHA-256 del piano JSON di ogni definizione storica (golden RH-015): catturati prima di toccare il piano.</summary>
    private static readonly IReadOnlyDictionary<string, (string ConfigHash, string PlanSha)> Goldens = new Dictionary<string, (string, string)>
    {
        [V6] = ("d401484ceb7a7b4f8c4f2e28dc126fceb0d0fcee103c5665db5c46f94759e894", "824dde72ee1c41f3948bca287857b8d04b591beb1bc89753b92ab08259879c9b"),
        [V8] = ("7f2493392d346e34ce49c0ce52b4c7f52f8321bc89252260f5a4e18192b05958", "5ccc506e686f1cddb2ed682a908f3a1b024533c5e932cc0df086b096e330534d"),
        [V9] = ("932018746fa9a1fdda724dae52562a83b287d39369e19d210d2e9f9bb46a1e67", "0fd8e62faf57da74279f34b5ea61881df1358c33be3799aa68021a6a00efa7a8"),
        [V10] = ("b5ef964d9988b557c8f862b06d251e6ed6b52ef1bef4625c0b247921cb88a5c8", "2c0841e0833fa326b3235991304efb9b32526f8c7f79dcb18bc953f7d88043dd"),
    };

    [Fact]
    public async Task The_config_hash_and_plan_of_v6_v8_v9_and_v10_are_unchanged_and_the_v11_has_its_own_identity()
    {
        var actual = new Dictionary<string, (string ConfigHash, string PlanSha)>();
        foreach (var definition in new[] { V6, V8, V9, V10 })
        {
            using var prepared = await PrepareAsync(definition);
            actual[definition] = (prepared.Plan.Manifest.ConfigHash, Sha(prepared.Plan.Manifest.PlanJson));
        }

        var report = string.Join('\n', actual.Select(kv => $"{kv.Key} {kv.Value.ConfigHash} {kv.Value.PlanSha}"));
        Assert.True(Goldens.All(kv => actual[kv.Key] == kv.Value), "Identità storiche diverse dai golden. Valori reali:\n" + report);

        using var v11 = await PrepareAsync(V11);
        var hash11 = v11.Plan.Manifest.ConfigHash;
        Assert.Matches("^[0-9a-f]{64}$", hash11);
        Assert.DoesNotContain(hash11, actual.Values.Select(v => v.ConfigHash));              // diverso da v6, v8, v9 e v10
        Assert.Equal(V11, v11.Plan.Manifest.DefinitionVersion);
        Assert.Equal("global-reconcile-v1", v11.Plan.Manifest.AlgorithmVersion);
    }

    [Fact]
    public async Task The_plan_json_carries_the_reconcile_contract_only_for_the_v11_next_to_the_profile_and_the_expansion_keys()
    {
        foreach (var definition in new[] { V6, V8, V9, V10 })
        {
            using var prepared = await PrepareAsync(definition);
            Assert.False(JsonNode.Parse(prepared.Plan.Manifest.PlanJson)!.AsObject().ContainsKey("reconcileContract"), definition);
        }

        using var v11 = await PrepareAsync(V11);
        var plan = JsonNode.Parse(v11.Plan.Manifest.PlanJson)!.AsObject();
        Assert.Equal("reconcile-stage-contract-1", plan["reconcileContract"]!.GetValue<string>());
        Assert.Equal("reconcile-alias-schema-by-stage-1", plan["reconcileSchemaProfile"]!.GetValue<string>());
        Assert.Equal("expansion-grid-1", plan["reconcileExpansion"]!["algorithm"]!.GetValue<string>());
        Assert.Equal(V11, plan["definition"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- validazione, schema e istruzioni del primo batch

    [Fact]
    public async Task The_lookup_expectation_is_the_alias_schema_with_validation_v2_for_the_v11_and_validation_v1_for_every_historical_definition()
    {
        foreach (var definition in new[] { V6, V8, V9, V10 })
        {
            using var prepared = await PrepareAsync(definition);
            Assert.Equal(new WorkLookupExpectation("reconcile-alias-1", "reconcile-validation-v1"), prepared.Plan.Expectation);
        }

        using var v11 = await PrepareAsync(V11);
        Assert.Equal(new WorkLookupExpectation("reconcile-alias-1", "reconcile-validation-v2"), v11.Plan.Expectation);
    }

    private static JsonElement? SentSchema(BatchAgentSpec spec) => (spec.ChatOptions.ResponseFormat as ChatResponseFormatJson)?.Schema;

    [Fact]
    public async Task The_first_v11_batch_carries_the_leaf_a_schema_of_the_by_stage_profile_and_the_v6_instructions_while_the_v10_keeps_its_own()
    {
        using var v11 = await PrepareAsync(V11);
        var agent11 = v11.Plan.AgentFor(new BatchRef(0, v11.First.BatchId, v11.First.InputHash));
        Assert.Equal(ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV6), agent11.Instructions);
        Assert.Equal(
            CanonicalJson.Canonicalize(ReconcileAliasContracts.SchemaOfByStage(ReconcileBatchSchema.LeafA).GetRawText()), CanonicalJson.Canonicalize(SentSchema(agent11)!.Value.GetRawText()));
        Assert.Equal("leaf", v11.First.Stage);
        Assert.Equal("A", v11.First.View);

        using var v10 = await PrepareAsync(V10);
        var agent10 = v10.Plan.AgentFor(new BatchRef(0, v10.First.BatchId, v10.First.InputHash));
        Assert.Equal(ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV5), agent10.Instructions);
        Assert.Equal(
            CanonicalJson.Canonicalize(ReconcileAliasContracts.SchemaOfV3(ReconcileBatchSchema.LeafA).GetRawText()), CanonicalJson.Canonicalize(SentSchema(agent10)!.Value.GetRawText()));
        Assert.NotEqual(CanonicalJson.Canonicalize(SentSchema(agent10)!.Value.GetRawText()), CanonicalJson.Canonicalize(SentSchema(agent11)!.Value.GetRawText()));
    }

    // ---------------------------------------------------------------- validazione v2 e compositore con opzioni, attraverso il piano

    private const string LeafWithOnlyADescriptor = """{"contract":"reconcile-alias-1","descriptor":"gruppo","noFindings":null,"items":[],"candidates":[],"focus":null,"patches":[]}""";
    private const string LeafWithBlankDescriptor = """{"contract":"reconcile-alias-1","descriptor":"   ","noFindings":"x","items":[],"candidates":[],"focus":null,"patches":[]}""";

    [Fact]
    public async Task A_batch_without_a_no_findings_reason_is_accepted_by_the_v11_plan_with_validation_v2_and_rejected_by_the_v10_plan()
    {
        using var v11 = await PrepareAsync(V11);
        var validated = await v11.Plan.ValidateAsync(new BatchRef(0, v11.First.BatchId, v11.First.InputHash), LeafWithOnlyADescriptor, new NoOutputs(), Ct);
        Assert.Equal("reconcile-validation-v2", validated.Output.ValidationVersion);
        Assert.Equal("reconcile-alias-1", validated.Output.SchemaVersion);

        using var v10 = await PrepareAsync(V10);
        var rejected = await Assert.ThrowsAsync<DocumentWorkException>(
            async () => await v10.Plan.ValidateAsync(new BatchRef(0, v10.First.BatchId, v10.First.InputHash), LeafWithOnlyADescriptor, new NoOutputs(), Ct));
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, rejected.Code);
        Assert.Contains("noFindingsReason: obbligatorio", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_v11_plan_composes_with_the_stage_contract_options_so_a_blank_descriptor_is_rejected_by_the_composer_and_the_message_names_the_violation_count()
    {
        using var v11 = await PrepareAsync(V11);
        var rejected = await Assert.ThrowsAsync<DocumentWorkException>(
            async () => await v11.Plan.ValidateAsync(new BatchRef(0, v11.First.BatchId, v11.First.InputHash), LeafWithBlankDescriptor, new NoOutputs(), Ct));
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, rejected.Code);
        Assert.Contains("(1 violazioni)", rejected.Message, StringComparison.Ordinal);                // rifiuto del compositore (con le opzioni), non del validatore canonico
        Assert.Contains("descriptor: non vuoto e al massimo 512 caratteri Unicode.", rejected.Message, StringComparison.Ordinal);

        using var v10 = await PrepareAsync(V10);
        var historical = await Assert.ThrowsAsync<DocumentWorkException>(
            async () => await v10.Plan.ValidateAsync(new BatchRef(0, v10.First.BatchId, v10.First.InputHash), LeafWithBlankDescriptor, new NoOutputs(), Ct));
        Assert.DoesNotContain("violazioni)", historical.Message, StringComparison.Ordinal);          // v10: lo respinge il validatore canonico, il compositore storico lo accetta
    }

    // ---------------------------------------------------------------- guardie del costruttore

    private static IDocumentBatchPlan Plan(MeasurementRig rig, string profile, string? expansion, string? stageContract, string prompt)
    {
        var input = ReconcileMeasurementBudgetTests.SingleInput();
        var assembly = typeof(DocumentAnalysisWorkflow).Assembly;
        var mode = assembly.GetType("bProjects.App.AI.ReconcileAliasMode", throwOnError: true)!;
        var alias = Activator.CreateInstance(
            mode, AnalysisAliasCatalog.Build(input, DocumentAnalysisWorkflow.SchemaVersionV3), true, ReconcileAliasContracts.JsonSchema(), V11, prompt, profile, expansion, stageContract);
        var constructor = assembly.GetType("bProjects.App.AI.DocumentReconcilePlan", throwOnError: true)!.GetConstructors().Single();
        var scopes = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider().GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        try
        {
            return (IDocumentBatchPlan)constructor.Invoke([new AnalysisRunClaim(MeasurementLedger.Id("run"), "owner", 1, 1), input, new Dictionary<Guid, DirectoryFacts>(), rig.Preflight, rig.Profile, rig.Options, rig.Client, scopes, null, new DocumentOptions().ToLimits(), alias, null]);
        }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is not null)
        {
            throw e.InnerException;
        }
    }

    [Fact]
    public async Task The_plan_rejects_an_incoherent_combination_of_stage_contract_profile_expansion_and_prompt()
    {
        using var rig = await MeasurementRig.CreateAsync(Ct);
        var v6 = ReconcileAliasContracts.PromptVersionV6;
        var byStage = ReconcileAliasContracts.SchemaProfileByStage;
        var grid = ReconcileAliasContracts.ExpansionAlgorithm;
        var contract = ReconcileAliasContracts.StageContract;

        Assert.NotNull(Plan(rig, byStage, grid, contract, v6));                                                                                            // la combinazione coerente
        Assert.Throws<ArgumentException>(() => Plan(rig, ReconcileAliasContracts.SchemaProfileByBatchV3, grid, contract, v6));                            // contratto con il profilo -3
        Assert.Throws<ArgumentException>(() => Plan(rig, byStage, grid, null, v6));                                                                        // profilo by-stage senza contratto
        Assert.Throws<ArgumentException>(() => Plan(rig, byStage, null, contract, v6));                                                                    // contratto senza espansione
        Assert.Throws<ArgumentException>(() => Plan(rig, byStage, "expansion-grid-2", contract, v6));                                                      // algoritmo ignoto
        Assert.Throws<ArgumentException>(() => Plan(rig, byStage, grid, "reconcile-stage-contract-2", v6));                                                // contratto ignoto
        Assert.Throws<ArgumentException>(() => Plan(rig, byStage, grid, contract, ReconcileAliasContracts.PromptVersionV5));                              // contratto con il prompt v5
    }
}
