using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Persistence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T082/T083 offline (PG + modello FINTO, nessun DGX): un run creato dal vero endpoint Analizza (quindi v4) su evidenze ricavate dal testo del generatore, grande oltre 2x il budget di input
/// Analysis del modello finto, entra nella riconciliazione gerarchica v4, completa, copre tutte le evidenze e conserva ogni frase gold come fonte. Usa lo STESSO harness di
/// <see cref="DocumentReconcileAliasWorkflowTests"/> (nessuna infrastruttura nuova) e le STESSE query sui segnali del run (<see cref="RunSignalSql"/>) che le prove DGX applicano. Valida inoltre
/// su un database reale le query sul testo estratto e sulle porzioni usate dalle prove DGX. Il budget qui e quello del modello finto (parole): prova il cablaggio, non la qualita semantica.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class LargeDocumentHierarchyTests : ReconcileRig
{
    private const int FakeBudgetWords = DocumentChatWorkflowTests.FakeChatModel.Window - 1500 - 20;

    private static (Corpus Corpus, int Words) SizedCorpus()
    {
        var filler = FakeBudgetWords * 6;
        while (true)
        {
            var corpus = LargeDocumentCorpus.Single(filler, 20);
            var words = Words(corpus.Documents[0].PlainText);
            if (words > 2.2 * FakeBudgetWords)
            {
                return (corpus, words);
            }

            filler = filler * 12 / 10;
        }
    }

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [Fact]
    public async Task A_v4_Analizza_run_over_generated_text_above_twice_the_budget_enters_the_hierarchy_completes_and_keeps_every_gold_fact_as_a_source()
    {
        var (corpus, words) = SizedCorpus();
        Assert.True(words > 2 * FakeBudgetWords);   // il testo estratto supera 2x il budget di input Analysis effettivo (del modello finto)
        var passages = corpus.Documents[0].PlainText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => !Regex.IsMatch(p, @"^[A-Za-z ]{1,40}$")).ToList();
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        for (var i = 0; i < passages.Count; i++)
        {
            await NewDocument(client, project, $"p{i:D3}", $"Requisito {i + 1}: {passages[i]}", subject: $"s{i:D3}");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var run = (await Json(response)).GetProperty("id").GetGuid();
        var claim = await ClaimAsync(run);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => DocumentReconcileAliasWorkflowTests.Dto(DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText)) };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        var args = ("r", (object)run);
        Assert.Equal(DocumentAnalysisWorkflow.DefinitionVersionV4, await Scalar<string>(RunSignalSql.Definition, args));
        var batches = await Count(RunSignalSql.Batches, args);
        Assert.True(batches > 1);
        Assert.True(await Count(RunSignalSql.Nodes, args) > 0);
        Assert.Equal(batches, await Count(RunSignalSql.AliasOutputs, args));
        Assert.Equal(0, await Count(RunSignalSql.SingleOutputs, args));
        Assert.True(await Scalar<int>(RunSignalSql.Calls, args) > 1);
        Assert.Equal(model.Requests.Count, await Scalar<int>(RunSignalSql.Calls, args));
        Assert.Equal(0, await Count(RunSignalSql.OpenCandidates, args));
        Assert.Equal(0, await Count(RunSignalSql.BadPatches, args));
        var selected = await Count(RunSignalSql.SelectedEvidences, args);
        Assert.Equal(passages.Count, selected);
        Assert.Equal(selected, await Count(RunSignalSql.LinkedEvidences, args));
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", args));
        foreach (var fact in corpus.Gold)
        {
            Assert.Contains(content.Sources, s => s.Quote is not null && s.Quote.Contains(fact.Sentence, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_extraction_and_portion_queries_used_by_the_DGX_tests_run_on_the_real_schema()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewDocument(client, project, "q1", "Requisito 1: il cliente puo emettere al massimo 5 ordini al giorno.");

        var preparation = await Scalar<Guid>(RunSignalSql.LatestPreparation, ("d", doc.Document));
        Assert.Equal(0, await Count(RunSignalSql.Portions, ("p", preparation)));
        Assert.Equal(0, await Count(RunSignalSql.PendingPortions, ("p", preparation)));
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand(RunSignalSql.Blocks, connection);
        command.Parameters.AddWithValue("p", preparation);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.False(await reader.ReadAsync(Ct));   // nessun blocco: il documento di prova non e passato dal parser
    }
}
