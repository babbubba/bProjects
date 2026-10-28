using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Persistence;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T028 — ICheckpointStore&lt;JsonElement&gt; su PostgreSQL reale: roundtrip con CheckpointManager.CreateJson e un workflow
/// MAF nativo ripreso da un nuovo contesto, unicità, catena dei parent, indice oldest-first e scrittura atomica
/// protetta da owner/fencing. Il modello è un IChatClient finto: nessuna chiamata al DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class WorkflowCheckpointStoreTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private const string Owner = "worker-a";

    private ApiHarness? _app;

    public async ValueTask InitializeAsync()
    {
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            _app = await ApiHarness.StartAsync(database.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_native_workflow_checkpoints_into_postgres_and_resumes_from_a_new_context()
    {
        TestEnvironment.RequirePostgres();
        var run = await SeedRunningRunAsync();
        var session = Guid.NewGuid().ToString("N");
        var agent = new ChatClientAgent(new FixedChatClient("fatto"), instructions: "test", name: "Agent");
        var executor = agent.BindAsExecutor(emitEvents: true);
        var workflow = new WorkflowBuilder(executor).WithOutputFrom(executor).Build();

        await using (var context = NewContext())
        {
            var checkpoints = CheckpointManager.CreateJson(new PostgresCheckpointStore(context, run.Writer));
            await using var streaming = await InProcessExecution.RunStreamingAsync(
                workflow, new List<ChatMessage> { new(ChatRole.User, "ciao") }, checkpoints, session, TestContext.Current.CancellationToken);
            Assert.True(await streaming.TrySendMessageAsync(new TurnToken(emitEvents: true)));
            await foreach (var _ in streaming.WatchStreamAsync(TestContext.Current.CancellationToken))
            {
            }
        }

        // «nuovo processo»: contesto, store e CheckpointManager nuovi; l'unico stato condiviso è PostgreSQL.
        await using var restoredContext = NewContext();
        var restoredCheckpoints = CheckpointManager.CreateJson(new PostgresCheckpointStore(restoredContext, run.Writer));
        var latest = await restoredCheckpoints.GetLatestCheckpointAsync(session, TestContext.Current.CancellationToken);
        Assert.NotNull(latest);
        Assert.Equal(session, latest.SessionId);

        await using var resumed = await InProcessExecution.ResumeStreamingAsync(workflow, latest, restoredCheckpoints, TestContext.Current.CancellationToken);
        Assert.Equal(session, resumed.SessionId);

        var rows = await QueryAsync($"SELECT count(*)::text FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = '{run.Id}'");
        Assert.True(int.Parse(rows.Single()) >= 1);
        // lo store non aggiunge alcun campo di dominio sul run né una tabella di step
        Assert.Equal(["Running"], await QueryAsync($"SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = '{run.Id}'"));
    }

    [Fact]
    public async Task The_payload_is_opaque_and_roundtrips_unchanged()
    {
        TestEnvironment.RequirePostgres();
        var run = await SeedRunningRunAsync();
        await using var context = NewContext();
        var store = new PostgresCheckpointStore(context, run.Writer);
        var payload = JsonDocument.Parse("""{"z":1,"a":{"$type":"x","n":[1,2,{"k":null}]},"testo":"è àccentata ☃","vuoto":[]}""").RootElement;

        var info = await store.CreateCheckpointAsync("s1", payload);
        var read = await store.RetrieveCheckpointAsync("s1", info);

        Assert.True(JsonElement.DeepEquals(payload, read));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await store.RetrieveCheckpointAsync("s1", new CheckpointInfo("s1", "inesistente")));
        // lo stesso checkpoint non è visibile con un'altra sessione né da un altro run
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await store.RetrieveCheckpointAsync("s2", info));
        var other = await SeedRunningRunAsync();
        await using var otherContext = NewContext();
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await new PostgresCheckpointStore(otherContext, other.Writer).RetrieveCheckpointAsync("s1", info));
    }

    [Fact]
    public async Task Index_is_oldest_first_per_session_and_filterable_by_parent()
    {
        TestEnvironment.RequirePostgres();
        var run = await SeedRunningRunAsync();
        await using var context = NewContext();
        var store = new PostgresCheckpointStore(context, run.Writer);
        var json = JsonDocument.Parse("{}").RootElement;

        var a = await store.CreateCheckpointAsync("s1", json);
        var b = await store.CreateCheckpointAsync("s1", json, a);
        var other = await store.CreateCheckpointAsync("s2", json);
        var c = await store.CreateCheckpointAsync("s1", json, b);
        var d = await store.CreateCheckpointAsync("s1", json, a);

        Assert.Equal([a, b, c, d], await store.RetrieveIndexAsync("s1"));
        Assert.Equal([other], await store.RetrieveIndexAsync("s2"));
        Assert.Equal([b, d], await store.RetrieveIndexAsync("s1", a));
        Assert.Equal([c], await store.RetrieveIndexAsync("s1", b));
        Assert.Empty(await store.RetrieveIndexAsync("s1", c));
        Assert.Empty(await store.RetrieveIndexAsync("assente"));

        // CommitSequence cresce di uno a ogni scrittura del run, anche fra sessioni
        Assert.Equal(["1", "2", "3", "4", "5"], await QueryAsync(
            $"SELECT \"CommitSequence\"::text FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = '{run.Id}' ORDER BY \"CommitSequence\""));
        // il parent è registrato come JSON e come chiave
        Assert.Equal([a.CheckpointId], await QueryAsync($"SELECT \"ParentInfo\"->>'CheckpointId' FROM \"WorkflowCheckpoints\" WHERE \"CheckpointKey\" = '{b.CheckpointId}'"));
        Assert.Equal([""], await QueryAsync($"SELECT COALESCE(\"ParentInfo\"::text, '') FROM \"WorkflowCheckpoints\" WHERE \"CheckpointKey\" = '{a.CheckpointId}'"));
    }

    [Fact]
    public async Task Parent_must_exist_in_the_same_run_and_session_and_keys_are_unique()
    {
        TestEnvironment.RequirePostgres();
        var run = await SeedRunningRunAsync();
        await using var context = NewContext();
        var store = new PostgresCheckpointStore(context, run.Writer);
        var json = JsonDocument.Parse("{}").RootElement;
        var a = await store.CreateCheckpointAsync("s1", json);

        // parent inesistente o di un'altra sessione: rifiutato dal database
        var missing = await Assert.ThrowsAsync<PostgresException>(async () => await store.CreateCheckpointAsync("s1", json, new CheckpointInfo("s1", "nessuno")));
        Assert.Equal("23503", missing.SqlState);
        var crossSession = await Assert.ThrowsAsync<PostgresException>(async () => await store.CreateCheckpointAsync("s2", json, a));
        Assert.Equal("23503", crossSession.SqlState);

        // unicità (run, sessione, chiave): lo stesso insieme non si duplica
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(run.Id, "s1", a.CheckpointId, 99));
        Assert.Equal("23505", duplicate.SqlState);
        // la stessa chiave è però ammessa in un'altra sessione
        await InsertRawAsync(run.Id, "s9", a.CheckpointId, 99);
        // e la stessa CommitSequence non si ripete nel run
        var sequence = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(run.Id, "s9", "altra", 99));
        Assert.Equal("23505", sequence.SqlState);
        // versioni di maf/definizione copiate dal run
        Assert.Equal(["1.26.0|project-analysis-v2"], await QueryAsync(
            $"SELECT \"MafVersion\" || '|' || \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\" WHERE \"CheckpointKey\" = '{a.CheckpointId}' AND \"WorkflowSessionId\" = 's1'"));
    }

    [Fact]
    public async Task Writes_are_atomic_and_fenced_by_owner_fencing_token_status_and_lease()
    {
        TestEnvironment.RequirePostgres();
        var run = await SeedRunningRunAsync();
        await using var context = NewContext();
        var json = JsonDocument.Parse("""{"v":1}""").RootElement;
        var good = new PostgresCheckpointStore(context, run.Writer);
        await good.CreateCheckpointAsync("s1", json);

        // owner diverso, fencing token vecchio o futuro
        foreach (var stale in new[] { run.Writer with { LeaseOwner = "worker-b" }, run.Writer with { FencingToken = run.Writer.FencingToken - 1 }, run.Writer with { FencingToken = run.Writer.FencingToken + 1 } })
        {
            await Assert.ThrowsAsync<CheckpointFencedException>(async () => await new PostgresCheckpointStore(context, stale).CreateCheckpointAsync("s1", json));
        }

        // run inesistente
        await Assert.ThrowsAsync<CheckpointFencedException>(async () =>
            await new PostgresCheckpointStore(context, run.Writer with { ProcessingRunId = Guid.NewGuid() }).CreateCheckpointAsync("s1", json));
        Assert.Equal(["1"], await QueryAsync($"SELECT count(*)::text FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = '{run.Id}'"));

        // un nuovo claim (fencing token incrementato) esclude il vecchio owner e ammette il nuovo
        await ExecuteAsync($"UPDATE \"ProcessingRuns\" SET \"FencingToken\" = \"FencingToken\" + 1, \"LeaseOwner\" = 'worker-b' WHERE \"Id\" = '{run.Id}'");
        await Assert.ThrowsAsync<CheckpointFencedException>(async () => await good.CreateCheckpointAsync("s1", json));
        var next = new PostgresCheckpointStore(context, run.Writer with { LeaseOwner = "worker-b", FencingToken = run.Writer.FencingToken + 1 });
        await next.CreateCheckpointAsync("s1", json);
        Assert.Equal(["2"], await QueryAsync($"SELECT count(*)::text FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = '{run.Id}'"));

        // lease scaduto e run non più Running: nessuna scrittura
        await ExecuteAsync($"UPDATE \"ProcessingRuns\" SET \"LeaseUntil\" = now() - interval '1 second' WHERE \"Id\" = '{run.Id}'");
        await Assert.ThrowsAsync<CheckpointFencedException>(async () => await next.CreateCheckpointAsync("s1", json));
        await ExecuteAsync($"UPDATE \"ProcessingRuns\" SET \"LeaseUntil\" = now() + interval '1 minute', \"Status\" = 'Failed', \"ErrorCode\" = 'x' WHERE \"Id\" = '{run.Id}'");
        await Assert.ThrowsAsync<CheckpointFencedException>(async () => await next.CreateCheckpointAsync("s1", json));
        Assert.Equal(["2"], await QueryAsync($"SELECT count(*)::text FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = '{run.Id}'"));

        // le letture non richiedono il fence (resume di un nuovo processo)
        Assert.Equal(2, (await good.RetrieveIndexAsync("s1")).Count());
    }

    [Fact]
    public async Task Concurrent_writers_of_the_same_run_get_distinct_consecutive_sequences()
    {
        TestEnvironment.RequirePostgres();
        var run = await SeedRunningRunAsync();
        var json = JsonDocument.Parse("{}").RootElement;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var context = NewContext();
            await new PostgresCheckpointStore(context, run.Writer).CreateCheckpointAsync($"s{i % 2}", json);
        }));

        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8"], await QueryAsync(
            $"SELECT \"CommitSequence\"::text FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = '{run.Id}' ORDER BY \"CommitSequence\""));
    }

    // ------------------------------------------------------------------ supporto

    private sealed record SeededRun(Guid Id, CheckpointWriter Writer);

    private AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options);

    private async Task<SeededRun> SeedRunningRunAsync()
    {
        var user = (await QueryAsync("SELECT \"Id\"::text FROM \"AspNetUsers\" LIMIT 1")).Single();
        var project = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var run = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO "Projects" ("Id","Name","CreatedBy","CreatedAt") VALUES ('{project}','Checkpoint','{user}', now());
            INSERT INTO "Conversations" ("Id","ProjectId","CreatedAt","LastSequence","MafVersion","ContextThroughSequence","ChatSessionRevision")
                VALUES ('{conversation}','{project}', now(), 0, '1.26.0', 0, 0);
            INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","ConversationId","SourceCutoffSequence","RequestKey","RequestedBy","RequestedAt","Status","StartedAt","DeadlineAt",
                "WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","LeaseOwner","LeaseUntil","FencingToken","UpdatedAt")
            VALUES ('{run}','{project}','Analysis','{conversation}',1,'{Guid.NewGuid()}','{user}',now(),'Running',now(),now() + interval '3 minutes',
                'project-analysis-v2','1.26.0',1,3,'{Owner}', now() + interval '1 minute', 7, now());
            """);
        return new SeededRun(run, new CheckpointWriter(run, Owner, 7));
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private Task InsertRawAsync(Guid run, string session, string key, int sequence) => ExecuteAsync(
        "INSERT INTO \"WorkflowCheckpoints\" (\"Id\",\"ProcessingRunId\",\"WorkflowSessionId\",\"CheckpointKey\",\"CheckpointInfo\",\"Payload\",\"CommitSequence\",\"CreatedAt\",\"MafVersion\",\"WorkflowDefinitionVersion\") " +
        $"VALUES ('{Guid.NewGuid()}','{run}','{session}','{key}','{{}}','{{}}',{sequence},now(),'1.26.0','project-analysis-v2')");

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>LLM sostitutivo deterministico: un testo fisso, anche in streaming.</summary>
    private sealed class FixedChatClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
