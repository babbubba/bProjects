using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-044 (M4, T177) — congelamento OFFLINE della definizione v11: <c>configHash</c> e piano, hash dei sette schemi del profilo <c>by-stage-1</c>, SHA del prompt unico
/// <c>document-reconcile-v6</c>, tabella <c>leaf-projection-1</c> (forma canonica, tutte le combinazioni tipo × origine × certezza) e JSON canonico di <c>stageContract</c>. Dopo il congelamento ogni
/// cambiamento della v11 richiede una definizione NUOVA (non si ritocca un golden per correggere un test). Il congelamento NON dichiara la v11 semanticamente validata né pronta al rilascio: RH-050/RH-051 con
/// modello reale restano obbligatori; la v11 non è il default.
/// </summary>
[Trait("Category", "Offline")]
public sealed class ReconcileV11FreezeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string V11 = DocumentAnalysisWorkflow.DefinitionVersionV11;

    private sealed class NoOutputs : IBatchOutputs
    {
        public ValueTask<WorkOutput?> GetByOrdinalAsync(int ordinal, CancellationToken ct) => new((WorkOutput?)null);
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ---------------------------------------------------------------- golden (valori OSSERVATI al congelamento del 2026-11-15)

    private const string ConfigHash = "2cbef06cdd5d9ccff9782125146d755063305cc7d09586de2e4d5e3a2f2643fa";
    private const string PlanSha = "4d03ee2bc9db56bed68095aa0ababc483932cdf9552a64dc75e6e7bdc0311f46";
    private const string PromptV6Sha = "36efd347fc43255bcba0ed494b7be61aa00df42d0c01d3427afdb38aed3d9d29";
    private const string LeafProjectionSha = "4d1d19c8f08a19b47ade3c8b9bb1949d2dbdc0d6e6014e504f09bcaaa83da47b";
    private const string StageContractSha = "b69bab4e044e06f4461b7bdc9dbf7f3ccee0a815bdbeb1d4dc02f5f954a247b7";
    private const string SchemaProfileSha = "87d6e243835ca8ec0312a69380194824d868e487b0d89b0f3791db5c7d42f0aa";

    private static IReadOnlyList<(string Variant, string Sha)> SchemaHashes() =>
        [.. Enum.GetValues<ReconcileBatchSchema>().Select(v => (v.ToString(), Sha(CanonicalJson.Canonicalize(ReconcileAliasContracts.SchemaOfByStage(v).GetRawText()))))];

    /// <summary>Forma canonica della tabella: una riga per tipo × origine × certezza («errore» se la regola rifiuta la combinazione).</summary>
    private static string LeafProjectionTable()
    {
        var lines = new List<string>();
        foreach (var kind in Enum.GetValues<EvidenceKind>())
        {
            foreach (var origin in Enum.GetValues<EvidenceOriginKind>())
            {
                foreach (var certainty in Enum.GetValues<EvidenceCertainty>())
                {
                    string outcome;
                    try
                    {
                        var (section, classification) = ReconcileLeafProjection.Rule(kind, origin, certainty);
                        outcome = $"{section}|{classification}";
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        outcome = "errore";
                    }

                    lines.Add($"{kind}|{origin}|{certainty}=>{outcome}");
                }
            }
        }

        return ReconcileLeafProjection.Version + "\n" + string.Join('\n', lines);
    }

    private static string StageContractCanonical() => CanonicalJson.Canonicalize(JsonSerializer.Serialize(ReconcileStageContracts.Table));

    private static async Task<(string ConfigHash, string PlanSha)> PlanIdentityAsync()
    {
        var input = ReconcileMeasurementBudgetTests.SingleInput();
        using var rig = await MeasurementRig.CreateAsync(Ct);
        var catalog = AnalysisAliasCatalog.Build(input, DocumentAnalysisWorkflow.SchemaVersionV3);
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var plan = ReconcileMeasurementBudgetTests.NewPlan(rig, input, new Dictionary<Guid, DirectoryFacts>(), catalog, V11, new DocumentOptions().ToLimits(), scopes);
        await plan.PrepareAsync(0, new NoOutputs(), Ct);
        return (plan.Manifest.ConfigHash, Sha(plan.Manifest.PlanJson));
    }

    [Fact]
    public async Task The_v11_definition_is_frozen_config_hash_plan_profile_prompt_projection_table_and_stage_contract()
    {
        var (configHash, planSha) = await PlanIdentityAsync();
        var schemas = SchemaHashes();
        var actual = new Dictionary<string, string>
        {
            ["configHash"] = configHash,
            ["planSha"] = planSha,
            ["promptV6Sha"] = Sha(ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV6).Replace("\r\n", "\n", StringComparison.Ordinal)),
            ["leafProjectionSha"] = Sha(LeafProjectionTable()),
            ["stageContractSha"] = Sha(StageContractCanonical()),
            ["schemaProfileSha"] = Sha(string.Join('\n', schemas.Select(s => $"{s.Variant}:{s.Sha}"))),
        };
        var expected = new Dictionary<string, string>
        {
            ["configHash"] = ConfigHash, ["planSha"] = PlanSha, ["promptV6Sha"] = PromptV6Sha, ["leafProjectionSha"] = LeafProjectionSha, ["stageContractSha"] = StageContractSha, ["schemaProfileSha"] = SchemaProfileSha,
        };
        var report = string.Join('\n', actual.Select(kv => $"{kv.Key} = {kv.Value}")) + "\n" + string.Join('\n', schemas.Select(s => $"schema {s.Variant} = {s.Sha}"));
        Assert.True(actual.All(kv => expected[kv.Key] == kv.Value), "Valori reali:\n" + report);
    }

    [Fact]
    public void The_frozen_identifiers_of_the_v11_are_the_documented_ones()
    {
        Assert.Equal("project-analysis-documents-v11", V11);
        Assert.Equal("leaf-projection-1", ReconcileLeafProjection.Version);
        Assert.Equal("reconcile-alias-schema-by-stage-1", ReconcileAliasContracts.SchemaProfileByStage);
        Assert.Equal("document-reconcile-v6", ReconcileAliasContracts.PromptVersionV6);
        Assert.Equal("expansion-grid-1", ReconcileAliasContracts.ExpansionAlgorithm);
        Assert.NotEqual(DocumentAnalysisWorkflow.DefinitionVersionV11, SourceSnapshotStore.AnalysisDefinitionV6);   // la v11 non è il default: le nuove richieste congelano la v6
    }
}
