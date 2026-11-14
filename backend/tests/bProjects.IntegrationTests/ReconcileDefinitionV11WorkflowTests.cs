using System.Net;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-010 (M1, PostgreSQL + modello FINTO) — la definizione v11 riconosciuta ma inattiva si comporta come la v6 dove il piano dice «identico»: (1) con un ledger entro budget la richiesta di
/// analisi (single-call) ha messaggi, istruzioni e schema uguali byte per byte a quelli di un run v6 sugli stessi documenti; (2) con un ledger oltre il budget entrambe entrano nella gerarchia (<c>RunStagedAsync</c>) e
/// il primo batch è una foglia della vista A; la v11 porta le istruzioni v6 e lo schema LeafA del profilo <c>by-stage-1</c>. Il run v11 si ottiene congelando esplicitamente la definizione (nessuna richiesta nuova la
/// congela). In M1 la v11 NON è funzionante end-to-end (gli item delle foglie A sono di M2): si confrontano solo le richieste inviate, mai l'esito del run. Servono BPROJECTS_TEST_POSTGRES; senza, Skipped.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ReconcileDefinitionV11WorkflowTests : ReconcileRig
{
    private static string Statement(int n, int words) => $"Requisito {n}: " + string.Join(' ', Enumerable.Range(0, words).Select(i => $"w{n}x{i}"));

    private async Task<Guid> StartNativeAsync(HttpClient client, Guid project)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Congela esplicitamente il run come v11 (la v11 non è il freeze di nessuna nuova richiesta): stesse fonti del run v6 della stessa selezione.</summary>
    private Task FreezeV11(Guid run) => Execute(
        "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = CAST(@p AS jsonb) WHERE \"Id\" = @r",
        ("d", DocumentAnalysisWorkflow.DefinitionVersionV11), ("p", SourceSnapshotStore.AnalysisPromptVersionsV11()), ("r", run));

    /// <summary>Modello che registra le richieste e risponde con un JSON vuoto (non valido per ogni contratto): serve solo a catturare ciò che il piano invia, mai a completare il run.</summary>
    private static DocumentChatWorkflowTests.FakeChatModel Recorder(int? window = null) =>
        window is { } w ? new() { ContextWindow = w, Responder = _ => "{}" } : new() { Responder = _ => "{}" };

    private async Task<(Guid Run, AnalysisRunClaim Claim)> StartAsync(HttpClient client, Guid project, bool v11)
    {
        var run = await StartNativeAsync(client, project);

        // Default invariato: la richiesta nativa dell'endpoint congela la v6 (nessuna nuova richiesta congela la v11), persistita come tale prima di ogni congelamento esplicito.
        var frozen = await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));
        Assert.Equal(SourceSnapshotStore.AnalysisDefinitionV6, frozen);
        Assert.NotEqual(DocumentAnalysisWorkflow.DefinitionVersionV11, frozen);
        if (v11)
        {
            await FreezeV11(run);
        }

        return (run, await ClaimAsync(run));
    }

    [Fact]
    public async Task The_single_call_request_of_a_v11_run_is_byte_for_byte_the_one_of_a_v6_run_on_the_same_documents()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement(1, 10), "aaa");
        await NewDocument(client, project, "note", Statement(2, 10), "bbb");

        var (run6, claim6) = await StartAsync(client, project, v11: false);
        var model6 = Recorder();
        await RunAsync(claim6, model6);

        var (run11, claim11) = await StartAsync(client, project, v11: true);
        var model11 = Recorder();
        await RunAsync(claim11, model11);

        Assert.Equal(DocumentAnalysisWorkflow.DefinitionVersionV11, await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run11)));
        Assert.NotEmpty(model6.Requests);
        Assert.Equal(model6.Requests.Count, model11.Requests.Count);
        Assert.Equal(model6.Requests.Select(r => r.AllText), model11.Requests.Select(r => r.AllText));
        Assert.Equal(model6.Requests.Select(r => r.System), model11.Requests.Select(r => r.System));
        Assert.Equal(model6.Requests.Select(r => r.ResponseSchema), model11.Requests.Select(r => r.ResponseSchema));
        Assert.DoesNotMatch("Tipo di batch:", model11.Requests[0].AllText);                       // single-call: nessuna gerarchia
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" IN ('leaf','reduce','cross','expand','compare')", ("r", run11)));
        _ = run6;
    }

    [Fact]
    public async Task Beyond_the_single_call_budget_a_v11_run_enters_the_hierarchy_like_the_v6_with_a_leaf_a_first_batch_and_its_own_instructions_and_schema()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        for (var n = 1; n <= 8; n++)
        {
            await NewDocument(client, project, $"doc{n}", Statement(n, 120), n == 1 ? "aaa" : $"mmm{n}");
        }

        var (_, claim6) = await StartAsync(client, project, v11: false);
        var model6 = Recorder();
        await RunAsync(claim6, model6);

        var (_, claim11) = await StartAsync(client, project, v11: true);
        var model11 = Recorder();
        await RunAsync(claim11, model11);

        Assert.NotEmpty(model6.Requests);
        Assert.NotEmpty(model11.Requests);
        foreach (var first in new[] { model6.Requests[0], model11.Requests[0] })
        {
            Assert.Matches(new Regex(@"^Tipo di batch: foglia\n", RegexOptions.Multiline), first.AllText);   // entrambe in gerarchia, prima foglia
            Assert.Contains("Vista: A", first.AllText, StringComparison.Ordinal);
        }

        // v6: prompt v3 e schema unico; v11: istruzioni v6 e schema LeafA del profilo by-stage-1 (items vuoto: maxItems 0).
        Assert.Equal(ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersion), model6.Requests[0].System);
        Assert.Equal(ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV6), model11.Requests[0].System);
        var schema11 = System.Text.Json.JsonDocument.Parse(model11.Requests[0].ResponseSchema!).RootElement;
        Assert.Equal(0, schema11.GetProperty("properties").GetProperty("items").GetProperty("maxItems").GetInt32());
    }
}
