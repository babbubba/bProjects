using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static bProjects.IntegrationTests.ConversationalAgentTests;

namespace bProjects.IntegrationTests;

/// <summary>
/// T018 - commit atomico di risposta, snapshot, watermark e Completed su PostgreSQL reale: atomicita' con guasto
/// iniettato, nessuna risposta duplicata, rifiuto di worker vecchi (lease/fence/revisione), gara concorrente e
/// riconciliazione post-commit. Il modello e' un fake; il dispatcher ospitato e' provato end-to-end.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ChatTurnCompletionTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();
    private ApiHarness? _app;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            _app = await ApiHarness.StartAsync(_database.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    private ApiHarness App
    {
        get
        {
            TestEnvironment.RequirePostgres();
            return _app!;
        }
    }

    // ---------------------------------------------------------------- commit atomico

    [Fact]
    public async Task Commits_reply_session_watermark_revision_and_Completed_together()
    {
        var s = await SeedAsync("Voglio un portale clienti");
        var claim = (await ClaimAs("a"))!;
        var reply = await RunAsync(claim, "Chi lo usera?");

        var result = await CommitAsync(claim, reply);

        Assert.Equal(ChatTurnCommitOutcome.Committed, result.Outcome);
        var assistants = await AssistantMessages(s.ConversationId);
        var answer = Assert.Single(assistants);
        Assert.Equal(result.ResultMessageId, answer.Id);
        Assert.Equal(2, answer.Sequence);
        Assert.Equal("Chi lo usera?", answer.Body);
        Assert.Equal(claim.TriggerMessageId, answer.ReplyToMessageId);
        Assert.Equal(claim.TurnId, answer.ChatTurnId);
        Assert.Equal(ConversationalAgent.AgentName, answer.AgentId);

        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var turn = await db.ChatTurns.AsNoTracking().SingleAsync(t => t.Id == claim.TurnId, Ct);
        Assert.Equal(ChatTurnStatus.Completed, turn.Status);
        Assert.Equal(answer.Id, turn.ResultMessageId);
        Assert.NotNull(turn.CompletedAt);
        Assert.Null(turn.LeaseOwner);

        var conversation = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == s.ConversationId, Ct);
        Assert.Equal(2, conversation.LastSequence);
        Assert.Equal(2, conversation.ContextThroughSequence);   // il watermark include la risposta assistant
        Assert.Equal(1, conversation.ChatSessionRevision);
        Assert.Equal(ConversationalAgent.ChatConfigVersion, conversation.ChatConfigVersion);
        var envelope = JsonSerializer.Deserialize<ChatSessionEnvelope>(conversation.MafChatSessionState!)!;
        Assert.Equal(new ChatMessageRef(answer.Id, 2), envelope.Messages[^1]);
        Assert.Equal(2, envelope.Messages.Count);               // trigger + risposta
    }

    [Fact]
    public async Task The_committed_snapshot_is_reused_by_the_next_turn()
    {
        var s = await SeedAsync("Voglio un portale clienti");
        var claim = (await ClaimAs("a"))!;
        await CommitAsync(claim, await RunAsync(claim, "Chi lo usera?"));
        await PostAsync(s.ProjectId, "Lo useranno gli agenti");
        var second = (await ClaimAs("a"))!;
        var model = new FakeModel(["Bene."]);

        using var scope = App.Services.CreateScope();
        var reply = await Agent(model).RunAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), second, null, Ct);

        Assert.True(reply.Restored.Reused);
        Assert.Equal(["Voglio un portale clienti", "Chi lo usera?", "Lo useranno gli agenti"],
            model.LastRequest.Where(m => m.Role != Microsoft.Extensions.AI.ChatRole.System).Select(m => m.Text));
    }

    [Fact]
    public async Task A_failure_in_the_middle_of_the_transaction_leaves_nothing_and_a_retry_commits_once()
    {
        var s = await SeedAsync("Ciao");
        var claim = (await ClaimAs("a"))!;
        var reply = await RunAsync(claim, "Salve.");
        // Il guasto arriva all'ultimo passo (Completed), quando messaggio, snapshot e watermark sono gia' scritti nella transazione.
        await Exec("""
            CREATE FUNCTION fail_completed() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'guasto iniettato'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_completed BEFORE UPDATE ON "ChatTurns" FOR EACH ROW WHEN (NEW."Status" = 'Completed') EXECUTE FUNCTION fail_completed();
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => CommitAsync(claim, reply));

        Assert.Empty(await AssistantMessages(s.ConversationId));
        await AssertConversationUntouched(s.ConversationId, lastSequence: 1);
        Assert.Equal("Running", await TurnStatus(claim.TurnId));
        Assert.Equal(DBNull.Value, await Raw("""SELECT "ResultMessageId" FROM "ChatTurns" WHERE "Id" = @t""", ("t", claim.TurnId)));

        await Exec("""DROP TRIGGER fail_completed ON "ChatTurns"; DROP FUNCTION fail_completed();""");
        var retry = await CommitAsync(claim, reply);

        Assert.Equal(ChatTurnCommitOutcome.Committed, retry.Outcome);
        Assert.Single(await AssistantMessages(s.ConversationId));
    }

    // ---------------------------------------------------------------- replay e riconciliazione

    [Fact]
    public async Task Replaying_the_commit_never_duplicates_the_reply()
    {
        var s = await SeedAsync("Ciao");
        var claim = (await ClaimAs("a"))!;
        var reply = await RunAsync(claim, "Salve.");
        var first = await CommitAsync(claim, reply);

        var replay = await CommitAsync(claim, reply);

        Assert.Equal(ChatTurnCommitOutcome.AlreadyCompleted, replay.Outcome);
        Assert.Equal(first.ResultMessageId, replay.ResultMessageId);
        Assert.Single(await AssistantMessages(s.ConversationId));
        Assert.Equal(1, await Raw<long>("""SELECT "ChatSessionRevision" FROM "Conversations" WHERE "Id" = @c""", ("c", s.ConversationId)));
    }

    [Fact]
    public async Task After_a_crash_following_the_commit_reconciliation_returns_the_same_reply_and_no_new_work_is_claimed()
    {
        var s = await SeedAsync("Ciao");
        var claim = (await ClaimAs("a"))!;
        var committed = await CommitAsync(claim, await RunAsync(claim, "Salve."));
        // Crash del processo prima di notificare: nessuno stato in memoria, solo il database.

        ChatTurnCommitResult? reconciled;
        using (var scope = App.Services.CreateScope())
        {
            reconciled = await ((ChatTurnCompletion)scope.ServiceProvider.GetRequiredService<IChatTurnCommitter>()).ReconcileAsync(claim.TurnId, Ct);
        }

        Assert.Equal(committed.ResultMessageId, reconciled!.ResultMessageId);
        Assert.Null(await ClaimAs("b"));                               // Completed e' terminale: nessun reclaim
        Assert.Single(await AssistantMessages(s.ConversationId));
    }

    [Fact]
    public async Task Reconcile_is_null_for_a_turn_that_is_not_completed()
    {
        await SeedAsync("Ciao");
        var claim = (await ClaimAs("a"))!;

        using var scope = App.Services.CreateScope();
        Assert.Null(await ((ChatTurnCompletion)scope.ServiceProvider.GetRequiredService<IChatTurnCommitter>()).ReconcileAsync(claim.TurnId, Ct));
    }

    // ---------------------------------------------------------------- worker vecchi

    [Fact]
    public async Task A_stale_worker_after_reclaim_changes_nothing_while_the_current_owner_commits()
    {
        var s = await SeedAsync("Ciao");
        var stale = (await ClaimAs("a"))!;
        var staleReply = await RunAsync(stale, "Risposta vecchia");
        var current = await ReclaimAs("b", stale.TurnId);
        var currentReply = await RunAsync(current, "Risposta valida");
        Assert.Equal(stale.FencingToken + 1, current.FencingToken);

        var rejected = await CommitAsync(stale, staleReply);

        Assert.Equal(ChatTurnCommitOutcome.Rejected, rejected.Outcome);
        Assert.Empty(await AssistantMessages(s.ConversationId));
        await AssertConversationUntouched(s.ConversationId, lastSequence: 1);
        Assert.Equal("Running", await TurnStatus(stale.TurnId));
        Assert.Equal("b", await Raw<string>("""SELECT "LeaseOwner" FROM "ChatTurns" WHERE "Id" = @t""", ("t", stale.TurnId)));

        var accepted = await CommitAsync(current, currentReply);

        Assert.Equal(ChatTurnCommitOutcome.Committed, accepted.Outcome);
        Assert.Equal("Risposta valida", Assert.Single(await AssistantMessages(s.ConversationId)).Body);

        // Anche dopo il Completed il vecchio worker non pubblica ne' cambia l'esito.
        var late = await CommitAsync(stale, staleReply);
        Assert.Equal(ChatTurnCommitOutcome.AlreadyCompleted, late.Outcome);
        Assert.Equal(accepted.ResultMessageId, late.ResultMessageId);
        Assert.Single(await AssistantMessages(s.ConversationId));
        Assert.Equal("Completed", await TurnStatus(stale.TurnId));
    }

    [Fact]
    public async Task A_worker_whose_lease_expired_without_reclaim_is_rejected()
    {
        var s = await SeedAsync("Ciao");
        var claim = (await ClaimAs("a"))!;
        var reply = await RunAsync(claim, "Salve.");
        await ExpireLease(claim.TurnId);

        var result = await CommitAsync(claim, reply);

        Assert.Equal(ChatTurnCommitOutcome.Rejected, result.Outcome);
        Assert.Empty(await AssistantMessages(s.ConversationId));
        await AssertConversationUntouched(s.ConversationId, lastSequence: 1);
    }

    [Fact]
    public async Task A_fencing_token_mismatch_with_the_same_owner_is_rejected()
    {
        var s = await SeedAsync("Ciao");
        var claim = (await ClaimAs("a"))!;
        var reply = await RunAsync(claim, "Salve.");

        var result = await CommitAsync(claim with { FencingToken = claim.FencingToken - 1 }, reply);

        Assert.Equal(ChatTurnCommitOutcome.Rejected, result.Outcome);
        Assert.Empty(await AssistantMessages(s.ConversationId));
        Assert.Equal("Running", await TurnStatus(claim.TurnId));
    }

    [Fact]
    public async Task A_changed_ChatSessionRevision_rejects_the_commit_without_changing_the_outcome()
    {
        var s = await SeedAsync("Ciao");
        var claim = (await ClaimAs("a"))!;
        var reply = await RunAsync(claim, "Salve.");
        await Exec("""UPDATE "Conversations" SET "ChatSessionRevision" = "ChatSessionRevision" + 1 WHERE "Id" = @c""", ("c", s.ConversationId));

        var result = await CommitAsync(claim, reply);

        Assert.Equal(ChatTurnCommitOutcome.Rejected, result.Outcome);
        Assert.Equal("session_revision_changed", result.Reason);
        Assert.Empty(await AssistantMessages(s.ConversationId));
        Assert.Equal("Running", await TurnStatus(claim.TurnId));
        Assert.Equal(1, await Raw<long>("""SELECT "ChatSessionRevision" FROM "Conversations" WHERE "Id" = @c""", ("c", s.ConversationId)));
    }

    [Fact]
    public async Task Stale_and_current_workers_racing_concurrently_produce_exactly_one_winner()
    {
        for (var round = 0; round < 8; round++)
        {
            var s = await SeedAsync($"Giro {round}");
            var stale = (await ClaimAs("a"))!;
            var staleReply = await RunAsync(stale, "vecchia");
            var current = await ReclaimAs("b", stale.TurnId);
            var currentReply = await RunAsync(current, "valida");
            var gate = new TaskCompletionSource();

            var tasks = new[] { (stale, staleReply), (current, currentReply), (current, currentReply) }
                .Select(x => Task.Run(async () =>
                {
                    await gate.Task;
                    return await CommitAsync(x.Item1, x.Item2);
                }, Ct)).ToArray();
            gate.SetResult();
            var results = await Task.WhenAll(tasks);

            Assert.Equal(1, results.Count(r => r.Outcome == ChatTurnCommitOutcome.Committed));
            Assert.NotEqual(ChatTurnCommitOutcome.Committed, results[0].Outcome);   // il vecchio worker non vince mai
            var answer = Assert.Single(await AssistantMessages(s.ConversationId));
            Assert.Equal("valida", answer.Body);
            Assert.Equal(1, await Raw<long>("""SELECT "ChatSessionRevision" FROM "Conversations" WHERE "Id" = @c""", ("c", s.ConversationId)));
            Assert.Equal(answer.Sequence, await Raw<long>("""SELECT "ContextThroughSequence" FROM "Conversations" WHERE "Id" = @c""", ("c", s.ConversationId)));
            Assert.Equal("Completed", await TurnStatus(stale.TurnId));
        }
    }

    // ---------------------------------------------------------------- dispatcher ospitato

    [Fact]
    public async Task The_hosted_dispatcher_completes_turns_end_to_end_with_a_fake_model()
    {
        TestEnvironment.RequirePostgres();
        var model = new FakeModel(["Capito. ", "Chi lo usera?"]);
        await using var hosted = await ApiHarness.StartAsync(_database.ConnectionString, prepare: false, configure: services =>
        {
            services.AddSingleton(Agent(model));
            services.AddSingleton<IChatTurnExecutor, ConversationalTurnExecutor>();
        });

        using var client = await hosted.LoginAsync();
        var created = await client.PostAsJsonAsync("/api/projects", new { name = $"Ospitato {Guid.NewGuid():N}" }, Ct);
        var projectId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var first = await PostAsync(client, projectId, "Voglio un portale clienti");
        await WaitCompleted(first);
        var second = await PostAsync(client, projectId, "Lo useranno gli agenti");
        await WaitCompleted(second);

        var conversationId = await Raw<Guid>("""SELECT "Id" FROM "Conversations" WHERE "ProjectId" = @p""", ("p", projectId));
        var assistants = await AssistantMessages(conversationId);
        Assert.Equal(2, assistants.Count);
        Assert.All(assistants, a => Assert.Equal("Capito. Chi lo usera?", a.Body));
        Assert.Equal([2L, 4L], assistants.Select(a => a.Sequence).ToArray());
        Assert.Equal(4, await Raw<long>("""SELECT "ContextThroughSequence" FROM "Conversations" WHERE "Id" = @c""", ("c", conversationId)));
        Assert.Equal(2, await Raw<long>("""SELECT "ChatSessionRevision" FROM "Conversations" WHERE "Id" = @c""", ("c", conversationId)));
        Assert.Equal(assistants[1].Id, await Raw<Guid>("""SELECT "ResultMessageId" FROM "ChatTurns" WHERE "Id" = @t""", ("t", second)));
        // Il secondo turno ha ripreso la sessione committata dal primo.
        Assert.Contains(model.LastRequest, m => m.Text == "Capito. Chi lo usera?");
    }

    // ---------------------------------------------------------------- supporto

    private sealed record Seed(Guid ProjectId, Guid ConversationId, Guid TurnId);

    private async Task<Seed> SeedAsync(string text)
    {
        using var client = await App.LoginAsync();
        var created = await client.PostAsJsonAsync("/api/projects", new { name = $"Commit {Guid.NewGuid():N}" }, Ct);
        var projectId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var turn = await PostAsync(client, projectId, text);
        var conversationId = await Raw<Guid>("""SELECT "Id" FROM "Conversations" WHERE "ProjectId" = @p""", ("p", projectId));
        return new(projectId, conversationId, turn);
    }

    private async Task<Guid> PostAsync(Guid projectId, string text)
    {
        using var client = await App.LoginAsync();
        return await PostAsync(client, projectId, text);
    }

    private static async Task<Guid> PostAsync(HttpClient client, Guid projectId, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("chatTurnId").GetGuid();
    }

    private async Task<ChatTurnClaim?> ClaimAs(string owner)
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextAsync(owner, Ct);
    }

    /// <summary>Il lease del vecchio owner scade e un nuovo owner prende il turno (nuovo fencing token).</summary>
    private async Task<ChatTurnClaim> ReclaimAs(string owner, Guid turnId)
    {
        await ExpireLease(turnId);
        var claim = await ClaimAs(owner);
        Assert.Equal(turnId, claim!.TurnId);
        return claim;
    }

    private Task ExpireLease(Guid turnId) =>
        Exec("""UPDATE "ChatTurns" SET "LeaseUntil" = clock_timestamp() - interval '1 second' WHERE "Id" = @t""", ("t", turnId));

    private async Task<ConversationalReply> RunAsync(ChatTurnClaim claim, string text)
    {
        using var scope = App.Services.CreateScope();
        return await Agent(new FakeModel([text])).RunAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), claim, null, Ct);
    }

    private async Task<ChatTurnCommitResult> CommitAsync(ChatTurnClaim claim, ConversationalReply reply)
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IChatTurnCommitter>().CommitAsync(claim, reply, Ct);
    }

    private async Task<List<ConversationMessage>> AssistantMessages(Guid conversationId)
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Role == MessageRole.Assistant).OrderBy(m => m.Sequence).ToListAsync(Ct);
    }

    private async Task AssertConversationUntouched(Guid conversationId, long lastSequence)
    {
        using var scope = App.Services.CreateScope();
        var c = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Conversations.AsNoTracking().SingleAsync(x => x.Id == conversationId, Ct);
        Assert.Null(c.MafChatSessionState);
        Assert.Equal(0, c.ContextThroughSequence);
        Assert.Equal(0, c.ChatSessionRevision);
        Assert.Equal(lastSequence, c.LastSequence);
    }

    private Task<string> TurnStatus(Guid turnId) => Raw<string>("""SELECT "Status" FROM "ChatTurns" WHERE "Id" = @t""", ("t", turnId));

    private async Task WaitCompleted(Guid turnId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await TurnStatus(turnId);
            if (status == "Completed")
            {
                return;
            }

            Assert.NotEqual("Failed", status);
            await Task.Delay(200, Ct);
        }

        Assert.Fail("Turno non completato entro il tempo previsto.");
    }

    private async Task Exec(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Build(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<object> Raw(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Build(connection, sql, parameters);
        return (await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<T> Raw<T>(string sql, params (string Name, object Value)[] parameters) => (T)await Raw(sql, parameters);

    private static NpgsqlCommand Build(NpgsqlConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
