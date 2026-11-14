using System.Reflection;
using System.Text.Json;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-017 (M1) — replay PER BATCH delle fixture reali dei Run 2-5 (stadi diversi dalla foglia A: confronto, espansione, foglia B, riduzione, cross) con il costruttore di fixture
/// (RH-006) e il compositore e la validazione di produzione. (a) Caratterizzazione v10 (schema -3, V1, nessuna opzione) secondo l'assessment §3. (b) v11: se l'output archiviato, COSÌ COM'È, è ammesso dallo schema
/// <c>by-stage-1</c> della sua variante, composizione e validazione V2 lo accettano; altrimenti l'esito è «non generabile sotto v11» e l'output NON viene trasformato (nessuna riparazione). Atteso esplicito:
/// Run 5 030 accettata dalla v11. Le foglie A sono escluse: nella v11 non producono item e la proiezione è di M2. Nessun database, nessuna inferenza.
/// </summary>
public sealed partial class ReconcileReplayFixturesTests
{
    private static readonly MethodInfo InvokeAsyncMethod = typeof(ReconciliationHierarchy).GetMethod("InvokeAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ReconciliationHierarchy.InvokeAsync non trovato: la firma interna è cambiata.");

    /// <summary>Fixture di RH-017: tutte tranne le foglie della vista A.</summary>
    private static IEnumerable<ReplayFixture> V11Scope() => ReplayFixtures.All().Where(f => f.Stage != "leaf" || f.View == "B");

    private static ReconcileBatchSchema VariantOf(ReplayFixture f) => ReconcileAliasContracts.BatchSchemaOf(f.Stage, Enum.Parse<ReconcileView>(f.View));

    /// <summary>
    /// Esito di UN output attraverso la catena di produzione: <c>accepted</c>, <c>composer:&lt;classi&gt;</c> (rifiuto del compositore, con le classi tipizzate) oppure <c>engine</c> (rifiuto del validatore canonico,
    /// dei riferimenti o della gerarchia, cioè di <c>InvokeAsync</c>), oppure <c>undecodable</c> (JSON non decodificabile).
    /// </summary>
    private static string Outcome(ReplayBatch batch, string json, ReconcileComposeOptions? options, ReconcileValidationRules rules, out string detail)
    {
        detail = string.Empty;
        var composition = ReconcileAliasComposer.Compose(json, batch.Catalog, batch.Request, batch.Snapshot, batch.Base, null, options);
        if (!composition.IsValid)
        {
            detail = string.Join(" | ", composition.Violations);
            var kinds = composition.TypedViolations.Select(v => v.Kind).Distinct().Order().ToList();
            return kinds is [ReconcileViolationKind.Undecodable] ? "undecodable" : "composer:" + string.Join('+', kinds);
        }

        var request = batch.Request;
        var text = ReconciliationCodec.Encode(composition.Result!);
        ReconcileInvoker invoker = (_, _) => new ValueTask<string>(text);
        var buildsNode = request.Batch.Stage is "leaf" or "reduce";
        var task = (ValueTask<BatchOutcome>)InvokeAsyncMethod.Invoke(
            null, [request.Batch, request.View, request.Level, request.Entries, request.Nodes, batch.Snapshot, new ReconciliationCatalog(request.Nodes, [], []), invoker, buildsNode, request.Focus, CancellationToken.None, request.Groups, rules])!;
        try
        {
            _ = task.AsTask().GetAwaiter().GetResult();
            return "accepted";
        }
        catch (DocumentWorkException e)
        {
            detail = e.Message;
            return "engine";
        }
    }

    private static readonly ReconcileComposeOptions V11Options = new(ReconcileAliasContracts.StageContract);

    /// <summary>Esito v10 di ogni fixture dell'ambito (caratterizzazione RH-017(a)); i casi dell'assessment §3 sono asseriti a parte.</summary>
    private static readonly IReadOnlyDictionary<string, string> V10Pinned = new Dictionary<string, string>
    {
        ["run2-024"] = "accepted", ["run2-025"] = "accepted", ["run2-026"] = "composer:StageShape",
        ["run3-024"] = "accepted", ["run3-025"] = "accepted", ["run3-026"] = "engine",
        ["run4-024"] = "accepted", ["run4-025"] = "accepted", ["run4-026"] = "accepted", ["run4-027"] = "accepted", ["run4-028"] = "accepted",
        ["run4-029"] = "accepted", ["run4-030"] = "accepted", ["run4-031"] = "accepted", ["run4-032"] = "accepted", ["run4-033"] = "accepted",
        ["run4-034"] = "accepted", ["run4-035"] = "accepted", ["run4-036"] = "undecodable",
        ["run5-025"] = "accepted", ["run5-026"] = "accepted", ["run5-027"] = "accepted", ["run5-028"] = "accepted", ["run5-029"] = "accepted", ["run5-030"] = "engine",
    };

    /// <summary>Esito v11 di ogni fixture dell'ambito (RH-017(b)): <c>not-generable</c> se l'output archiviato non è ammesso dallo schema by-stage-1 della sua variante.</summary>
    private static readonly IReadOnlyDictionary<string, string> V11Pinned = new Dictionary<string, string>
    {
        ["run2-024"] = "accepted", ["run2-025"] = "accepted", ["run2-026"] = "not-generable",
        ["run3-024"] = "accepted", ["run3-025"] = "accepted", ["run3-026"] = "not-generable",
        ["run4-024"] = "accepted", ["run4-025"] = "accepted", ["run4-026"] = "accepted", ["run4-027"] = "accepted", ["run4-028"] = "accepted",
        ["run4-029"] = "not-generable", ["run4-030"] = "not-generable", ["run4-031"] = "not-generable", ["run4-032"] = "not-generable", ["run4-033"] = "not-generable",
        ["run4-034"] = "accepted", ["run4-035"] = "accepted", ["run4-036"] = "not-generable",
        ["run5-025"] = "accepted", ["run5-026"] = "accepted", ["run5-027"] = "accepted", ["run5-028"] = "accepted", ["run5-029"] = "accepted", ["run5-030"] = "accepted",
    };

    private static (string V10, string V11, string V11Detail, bool SchemaAdmits) Evaluate(ReplayFixture f)
    {
        var batch = ReplayFixtureBuilder.Build(f);
        var v10 = Outcome(batch, f.Assistant, null, ReconcileValidationRules.V1, out _);

        bool admits;
        try
        {
            admits = ReconcileSchemaSubset.Violations(ReconcileAliasContracts.SchemaOfByStage(VariantOf(f)), f.Assistant).Count == 0;
        }
        catch (JsonException)
        {
            admits = false;   // JSON troncato (Run 4 036): nessuna istanza dello schema
        }

        if (!admits)
        {
            return (v10, "not-generable", string.Empty, false);
        }

        var v11 = Outcome(batch, f.Assistant, V11Options, ReconcileValidationRules.V2, out var detail);
        return (v10, v11, detail, true);
    }

    [Fact]
    public void The_v10_characterization_and_the_v11_replay_of_every_non_leaf_a_fixture_are_the_pinned_table_and_no_output_is_transformed()
    {
        var rows = V11Scope().Select(f => (f.Name, Eval: Evaluate(f))).ToList();
        var v10Report = string.Join('\n', rows.Select(r => $"{r.Name} = {r.Eval.V10}"));
        var v11Report = string.Join('\n', rows.Select(r => $"{r.Name} = {r.Eval.V11}"));

        Assert.True(
            string.Join('\n', V10Pinned.Select(kv => $"{kv.Key} = {kv.Value}")) == v10Report && string.Join('\n', V11Pinned.Select(kv => $"{kv.Key} = {kv.Value}")) == v11Report,
            "Tabella RH-017 non pinnata o diversa.\nv10:\n" + v10Report + "\nv11:\n" + v11Report);
    }

    [Fact]
    public void The_assessment_section_3_outcomes_hold_for_the_v10_and_run_5_call_030_is_accepted_by_the_v11()
    {
        string V10(int run, int call) => Evaluate(ReplayFixtures.Load(run, call)).V10;

        // Run 5 030: confronto Open con motivo, 0 candidati, noFindings null ⇒ rifiutato dalla validazione V1 (I-1).
        var run5030 = ReplayFixtures.Load(5, 30);
        var batch030 = ReplayFixtureBuilder.Build(run5030);
        Assert.Equal("engine", V10(5, 30));
        Outcome(batch030, run5030.Assistant, null, ReconcileValidationRules.V1, out var detail);
        Assert.Contains("noFindingsReason: obbligatorio quando non ci sono proposte, candidati né patch.", detail, StringComparison.Ordinal);

        // Run 4 029–033: accettate dalla v10 (con il candidato ridondante sulla stessa coppia); 034 e 035: espansioni accettate come `candidate_unresolved`; 036: JSON non decodificabile.
        foreach (var call in new[] { 29, 30, 31, 32, 33, 34, 35 })
        {
            Assert.Equal("accepted", V10(4, call));
        }

        Assert.Equal("undecodable", V10(4, 36));
        Assert.Equal("composer:StageShape", V10(2, 26));          // item nelle foglie B
        Assert.Equal("engine", V10(3, 26));                       // descriptor nullo: lo respinge la gerarchia (schema ≠ validatore)

        // Atteso esplicito: la v11 accetta la 030 del Run 5, così com'è.
        var v11 = Evaluate(run5030);
        Assert.True(v11.SchemaAdmits);
        Assert.Equal("accepted", v11.V11);
    }

    [Fact]
    public void A_v11_composition_is_attempted_only_on_outputs_that_the_by_stage_schema_admits_as_they_are_and_those_are_all_accepted()
    {
        var admitted = 0;
        foreach (var f in V11Scope())
        {
            var eval = Evaluate(f);
            if (eval.SchemaAdmits)
            {
                admitted++;
                Assert.True(eval.V11 == "accepted", $"{f.Name}: ammesso dallo schema by-stage-1 ma non accettato dalla v11 ({eval.V11}): {eval.V11Detail}");
            }
            else
            {
                Assert.Equal("not-generable", eval.V11);   // non generabile sotto v11: l'output non è stato trasformato né riprovato
            }
        }

        Assert.True(admitted >= 1);                           // la 030 del Run 5 almeno
    }
}
