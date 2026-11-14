using System.Text.Json.Nodes;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-012 (M1) — regole di validazione V1/V2 (<see cref="ReconcileValidationRules"/>, D3). V1 (<c>reconcile-validation-v1</c>, predefinita, v2–v10) è il comportamento di sempre:
/// <c>noFindingsReason</c> obbligatorio senza proposte, candidati né patch e mai di soli spazi. V2 (<c>reconcile-validation-v2</c>, solo v11) non ha alcuna regola su quel campo: è una spiegazione informativa
/// e si conserva così com'è. Ogni altra regola resta identica. I casi sono quelli 1, 5 e 6 della matrice RH-002 (V1 rifiuta, V2 accetta). Nessun database, nessuna inferenza.
/// </summary>
public sealed class ReconcileValidationRulesTests
{
    private const string Required = "noFindingsReason: obbligatorio quando non ci sono proposte, candidati né patch.";
    private const string NotBlank = "noFindingsReason: non vuoto quando valorizzato.";
    private const string DescriptorRule = "descriptor: non vuoto e al massimo 512 caratteri Unicode.";

    private static string Json(string? noFindings = null, string? descriptor = null, Action<JsonObject>? change = null) => ReconcileContractMatrixTests.Dto(noFindings, descriptor, change).ToJsonString();

    private static JsonNode Focus(string outcome, string? reason) => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new { outcome, reason }))!;

    public static TheoryData<ReconcileBatchSchema, string, string> EmptyOutcomes() => new()
    {
        // RH-002 caso 1: confronto con esito Open motivato, 0 candidati, noFindings null.
        { ReconcileBatchSchema.Compare, "compare-open", Json(change: o => o["focus"] = Focus("Open", "30 giorni contro 45")) },
        // Un confronto Compared senza candidati è «senza risultati» per la regola dei campi.
        { ReconcileBatchSchema.Compare, "compare-compared", Json(change: o => o["focus"] = Focus("Compared", null)) },
        // RH-002 caso 5: blocco di espansione vuoto.
        { ReconcileBatchSchema.Expand, "expand-empty", Json() },
        // RH-002 caso 6: foglia B senza candidati.
        { ReconcileBatchSchema.LeafB, "leaf-b-empty", Json(descriptor: "gruppo") },
        { ReconcileBatchSchema.Reduce, "reduce-empty", Json(descriptor: "gruppo") },
        { ReconcileBatchSchema.Cross, "cross-empty", Json() },
    };

    [Theory]
    [MemberData(nameof(EmptyOutcomes))]
    public void V1_rejects_an_outcome_without_a_no_findings_reason_and_V2_accepts_it_with_the_field_persisted_as_it_is(ReconcileBatchSchema variant, string label, string json)
    {
        var v1 = ReconcileBatchWorld.Create(variant, ContractProfile.V10).Evaluate(json);
        var v2 = ReconcileBatchWorld.Create(variant, ContractProfile.V11).Evaluate(json);

        Assert.True(v1.Composition.IsValid, label + ": " + v1.Report);                // il compositore accetta in entrambi i casi: la regola è del validatore
        Assert.Equal([Required], v1.ValidatorViolations);
        Assert.NotNull(v1.HierarchyError);
        Assert.True(v2.SchemaAdmits, label + ": " + string.Join(" | ", v2.SchemaViolations));
        Assert.True(v2.Accepted, label + ": " + v2.Report);
        Assert.Null(v2.Composition.Result!.NoFindingsReason);                           // nessun testo inventato dal server
    }

    [Theory]
    [InlineData(ReconcileBatchSchema.Expand)]
    [InlineData(ReconcileBatchSchema.LeafB)]
    public void A_no_findings_reason_of_only_spaces_is_rejected_by_V1_and_accepted_by_V2_and_persisted_verbatim(ReconcileBatchSchema variant)
    {
        var json = Json(noFindings: "   \t ", descriptor: variant == ReconcileBatchSchema.LeafB ? "gruppo" : null);
        var v1 = ReconcileBatchWorld.Create(variant, ContractProfile.V10).Evaluate(json);
        var v2 = ReconcileBatchWorld.Create(variant, ContractProfile.V11).Evaluate(json);

        Assert.Equal([NotBlank], v1.ValidatorViolations);
        Assert.True(v2.Accepted, v2.Report);
        Assert.Equal("   \t ", v2.Composition.Result!.NoFindingsReason);               // conservato byte per byte, mai normalizzato
    }

    [Fact]
    public void A_non_empty_explanation_is_accepted_by_both_and_every_other_rule_is_identical_under_V2()
    {
        var withReason = Json(noFindings: "nessun collegamento emerso", descriptor: "gruppo");
        foreach (var profile in new[] { ContractProfile.V10, ContractProfile.V11 })
        {
            Assert.True(ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafB, profile).Evaluate(withReason).Accepted);
        }

        // Il descriptor di soli spazi è rifiutato dal validatore anche in V2, con lo stesso messaggio.
        var blankDescriptor = Json(noFindings: "x", descriptor: " ");
        var v1 = ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafB, ContractProfile.V10).Evaluate(blankDescriptor);
        var v2 = ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafB, ContractProfile.V11).Evaluate(blankDescriptor);
        Assert.Equal([DescriptorRule], v1.ValidatorViolations);
        Assert.Equal([DescriptorRule], v2.ValidatorViolations);

        // Un descriptor nei batch che non costruiscono nodi e il descriptor assente nelle foglie sono rifiutati in V2 come in V1 (regola della gerarchia, non di CheckResult).
        Assert.NotNull(ReconcileBatchWorld.Create(ReconcileBatchSchema.Expand, ContractProfile.V11).Evaluate(Json(descriptor: "gruppo")).HierarchyError);
        Assert.NotNull(ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafB, ContractProfile.V11).Evaluate(Json()).HierarchyError);
    }

    [Fact]
    public void The_default_rules_are_V1_everywhere_so_every_historical_definition_keeps_its_behaviour()
    {
        Assert.Equal(ReconcileValidationRules.V1, default(ReconcileValidationRules));
        Assert.Equal(ReconcileValidationRules.V1, new ReconciliationContext("o", "m", "d", new PlannerLimits(1, 1)).Rules);
        Assert.Equal(ReconcileValidationRules.V2, new ReconciliationContext("o", "m", "d", new PlannerLimits(1, 1), Rules: ReconcileValidationRules.V2).Rules);

        // CheckResult senza il parametro coincide con V1 sullo stesso risultato.
        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.Expand, ContractProfile.V11);
        var result = world.Evaluate(Json(noFindings: "x")).Composition.Result!;
        var empty = result with { NoFindingsReason = null };
        Assert.Equal(
            ReconciliationContractRules.CheckResult(empty, world.Input.Snapshot.SourceHash),
            ReconciliationContractRules.CheckResult(empty, world.Input.Snapshot.SourceHash, ReconcileValidationRules.V1));
        Assert.Equal([Required], ReconciliationContractRules.CheckResult(empty, world.Input.Snapshot.SourceHash));
        Assert.Empty(ReconciliationContractRules.CheckResult(empty, world.Input.Snapshot.SourceHash, ReconcileValidationRules.V2));
        Assert.Empty(ReconciliationContractRules.CheckResult(empty with { NoFindingsReason = " " }, world.Input.Snapshot.SourceHash, ReconcileValidationRules.V2));
    }

    [Fact]
    public void The_single_call_contract_keeps_the_V1_rules_and_the_plan_constant_for_V2_is_the_one_in_the_task()
    {
        Assert.Equal("reconcile-validation-v1", DocumentReconcilePlanConstants.ValidationV1);
        Assert.Equal("reconcile-validation-v2", DocumentReconcilePlanConstants.ValidationV2);
    }
}

/// <summary><c>DocumentReconcilePlan</c> è <c>internal</c> e l'assembly non espone gli internals ai test: le costanti si leggono per riflessione.</summary>
internal static class DocumentReconcilePlanConstants
{
    private static string Read(string name) =>
        (string)typeof(DocumentAnalysisWorkflow).Assembly.GetType("bProjects.App.AI.DocumentReconcilePlan", throwOnError: true)!
            .GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;

    public static string ValidationV1 => Read("ValidationVersion");

    public static string ValidationV2 => Read("ValidationVersionV2");
}
