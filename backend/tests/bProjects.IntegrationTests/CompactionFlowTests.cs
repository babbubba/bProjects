using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// T047 - le stesse prove di <see cref="CompactionMafTests"/> contro un server del modello finto ma realistico (tokenizer, limite di
/// finestra con 400 come llama.cpp, sintesi che riporta intestazioni e frasi chiave): verificano deterministicamente cablaggio,
/// compattazione nativa, budget, originali e limiti strutturali con PostgreSQL reale, senza il DGX. Non sostituiscono la prova MafReal.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class CompactionFlowTests : CompactionScenarios
{
    private readonly FakeModelServer _server = new();

    protected override bool Real => false;

    protected override int FakeContextLimit => _server.ContextLimit;

    protected override AiOptions Services() => new() { Endpoint = "http://dgx.test/v1/", ApiKey = "chiave-di-prova", ModelId = "modello-finto" };

    protected override HttpMessageHandler NewServer() => _server;

    [Fact]
    public async Task An_empty_summary_is_reported_as_compaction_failed_and_not_as_a_successful_chat_turn()
    {
        TestEnvironment.RequirePostgres();
        var connection = Connection;
        _server.SummaryOverride = "   ";
        var options = new AiOptions { Endpoint = Services().Endpoint, ApiKey = "k", ModelId = "m", CompactionTriggerTokens = 3000 };

        await using var host = await CompactionHost.StartAsync(connection, options, _server, chat: true, analysis: false, prepare: true);
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        // Un solo messaggio di ~2000 token sotto soglia, poi un secondo che la supera: la sintesi e' richiesta e restituisce vuoto.
        var first = await CompactionHost.WaitTurnAsync(pm, project, await CompactionHost.PostAsync(pm, project, CompactionDialogs.Message(0, 8000)));
        Assert.Equal("Completed", first.GetProperty("status").GetString());
        var second = await CompactionHost.WaitTurnAsync(pm, project, await CompactionHost.PostAsync(pm, project, CompactionDialogs.Message(1, 8000)));

        Assert.Equal("Failed", second.GetProperty("status").GetString());
        Assert.Equal("compaction_failed", second.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, second.GetProperty("attemptCount").GetInt32());
        Assert.Single(host.Recorder.Main);       // solo il primo turno ha raggiunto il modello
        Assert.Equal(2, (await CompactionHost.OriginalsAsync(connection)).Count(m => m.Role == "User"));
    }
}
