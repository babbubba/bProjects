using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T014 — claim, FIFO, lease, fencing, retry e recovery dei ChatTurn su PostgreSQL reale. Sono prove del
/// dispatcher e dello store: nessun agente MAF né modello (le prove MAF reali appartengono ai task successivi).
/// Ogni prova usa un database nuovo, perché il limite AI globale rende gli stati dei test interdipendenti.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ChatTurnDispatchTests : IAsyncLifetime
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

    // ---------------------------------------------------------------- claim e lease

    [Fact]
    public async Task Claim_takes_a_queued_turn_with_owner_fence_attempt_and_a_database_computed_lease()
    {
        var turn = (await SeedProject(1))[0];

        var claim = await ClaimAs("owner-a");

        Assert.NotNull(claim);
        Assert.Equal(turn, claim.TurnId);
        Assert.Equal(1, claim.AttemptCount);
        Assert.Equal(1, claim.FencingToken);
        Assert.Equal("owner-a", claim.LeaseOwner);
        Assert.Equal("Running", await TurnStatus(turn));
        var secondsLeft = await Scalar<double>("SELECT extract(epoch FROM (\"LeaseUntil\" - clock_timestamp()))::float8 FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn));
        Assert.InRange(secondsLeft, 25, 30);
        Assert.NotNull(await Scalar<object?>("SELECT \"StartedAt\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public async Task Only_one_turn_runs_at_a_time_across_projects()
    {
        await SeedProject(1);
        await SeedProject(1);

        Assert.NotNull(await ClaimAs("a"));
        Assert.Null(await ClaimAs("b"));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ChatTurns\" WHERE \"Status\" = 'Running'"));
    }

    [Fact]
    public async Task Concurrent_claims_never_assign_the_same_turn_twice()
    {
        const int rounds = 25;
        for (var i = 0; i < rounds; i++)
        {
            await SeedProject(1);
        }

        // Molti round con partenza simultanea: una sola gara può passare per caso, venticinque no.
        for (var round = 1; round <= rounds; round++)
        {
            var gate = new TaskCompletionSource();
            var workers = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                await gate.Task;
                return await ClaimAs($"worker-{round}-{i}");
            }, Ct)).ToArray();
            gate.SetResult();

            var claim = Assert.Single(await Task.WhenAll(workers), c => c is not null)!;
            Assert.Equal(1, await Count("SELECT count(*) FROM \"ChatTurns\" WHERE \"Status\" = 'Running'"));
            Assert.Equal(round, await Count("SELECT count(*) FROM \"ChatTurns\" WHERE \"AttemptCount\" > 0"));
            await Execute("UPDATE \"ChatTurns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x' WHERE \"Id\" = @t", ("t", claim.TurnId));
        }
    }

    [Fact]
    public async Task Concurrent_claims_on_one_conversation_respect_fifo_and_a_single_running_turn()
    {
        var turns = await SeedProject(4);

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => ClaimAs($"worker-{i}"), Ct)));

        Assert.Equal(turns[0], Assert.Single(claims, c => c is not null)!.TurnId);
        foreach (var later in turns.Skip(1))
        {
            Assert.Equal("Queued", await TurnStatus(later));
        }
    }

    [Fact]
    public async Task A_valid_lease_is_not_reclaimed()
    {
        await SeedProject(1);
        Assert.NotNull(await ClaimAs("a"));

        Assert.Null(await ClaimAs("b"));
    }

    [Fact]
    public async Task A_turn_is_not_claimed_before_its_next_attempt_time()
    {
        var turn = (await SeedProject(1))[0];
        await Execute("UPDATE \"ChatTurns\" SET \"NextAttemptAt\" = clock_timestamp() + interval '1 hour' WHERE \"Id\" = @t", ("t", turn));

        Assert.Null(await ClaimAs("a"));
        Assert.Equal(0, await Scalar<int>("SELECT \"AttemptCount\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    // ---------------------------------------------------------------- FIFO

    [Fact]
    public async Task A_later_turn_never_overtakes_an_earlier_turn_in_backoff()
    {
        var turns = await SeedProject(3);
        var first = await ClaimAs("a");
        Assert.NotNull(first);
        var release = await ReleaseTransient(first);
        Assert.Equal(TurnTransition.Requeued, release.Outcome);

        // Il primo è in backoff e il lease è libero: il secondo non può superarlo.
        Assert.Null(await ClaimAs("b"));
        Assert.Equal("Queued", await TurnStatus(turns[1]));

        await Execute("UPDATE \"ChatTurns\" SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @t", ("t", turns[0]));
        var retry = await ClaimAs("b");
        Assert.Equal(turns[0], retry!.TurnId);
        Assert.Equal(2, retry.AttemptCount);
    }

    [Fact]
    public async Task A_turn_in_backoff_does_not_block_other_conversations()
    {
        var first = await SeedProject(1);
        var second = await SeedProject(1);
        var claim = await ClaimAs("a");
        Assert.Equal(first[0], claim!.TurnId);
        await ReleaseTransient(claim);

        var other = await ClaimAs("b");

        Assert.Equal(second[0], other!.TurnId);
    }

    [Fact]
    public async Task Turns_of_one_conversation_are_processed_strictly_in_order()
    {
        var turns = await SeedProject(3);
        var order = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var claim = await ClaimAs("a");
            order.Add(claim!.TurnId);
            await Execute("UPDATE \"ChatTurns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x' WHERE \"Id\" = @t", ("t", claim.TurnId));
        }

        Assert.Equal(turns, order);
    }

    [Fact]
    public async Task The_database_rejects_two_running_turns_in_one_conversation()
    {
        var turns = await SeedProject(2);
        await Execute("UPDATE \"ChatTurns\" SET \"Status\" = 'Running' WHERE \"Id\" = @t", ("t", turns[0]));

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            Execute("UPDATE \"ChatTurns\" SET \"Status\" = 'Running' WHERE \"Id\" = @t", ("t", turns[1])));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
    }

    // ---------------------------------------------------------------- reclaim e fencing

    [Fact]
    public async Task An_expired_lease_is_reclaimed_with_a_new_owner_fence_and_attempt()
    {
        var turn = (await SeedProject(1))[0];
        var first = (await ClaimAs("old-owner"))!;
        await ExpireLease(turn);

        var second = await ClaimAs("new-owner");

        Assert.Equal(turn, second!.TurnId);
        Assert.Equal("new-owner", second.LeaseOwner);
        Assert.Equal(first.FencingToken + 1, second.FencingToken);
        Assert.Equal(2, second.AttemptCount);
        Assert.Equal("Running", await TurnStatus(turn));
    }

    [Fact]
    public async Task A_stale_owner_cannot_renew_fail_release_or_complete_after_a_reclaim()
    {
        var turn = (await SeedProject(1))[0];
        var stale = (await ClaimAs("old-owner"))!;
        await ExpireLease(turn);
        var current = (await ClaimAs("new-owner"))!;
        var answer = await InsertAssistantMessage(turn);

        using var scope = App.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>();

        Assert.False(await store.RenewAsync(stale, Ct));
        Assert.False(await store.FailAsync(stale, "x", "x", Ct));
        Assert.False(await store.CompleteAsync(stale, answer, Ct));
        Assert.Equal(TurnTransition.Rejected, (await store.ReleaseAfterFailureAsync(stale, new ModelFailure(true, "model_unreachable", null), "x", "x", Ct)).Outcome);

        // Lo stato appartiene ancora al nuovo owner, intatto.
        Assert.Equal("Running", await TurnStatus(turn));
        Assert.Equal("new-owner", await Scalar<string>("SELECT \"LeaseOwner\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal(current.FencingToken, await Scalar<long>("SELECT \"FencingToken\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.True(await store.CompleteAsync(current, answer, Ct));
        Assert.Equal("Completed", await TurnStatus(turn));
        Assert.Equal(answer, await Scalar<Guid>("SELECT \"ResultMessageId\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public async Task A_completed_turn_cannot_be_changed_by_its_former_owner()
    {
        var turn = (await SeedProject(1))[0];
        var claim = (await ClaimAs("a"))!;
        var answer = await InsertAssistantMessage(turn);
        using var scope = App.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>();
        Assert.True(await store.CompleteAsync(claim, answer, Ct));

        Assert.False(await store.CompleteAsync(claim, answer, Ct));
        Assert.False(await store.FailAsync(claim, "x", "x", Ct));
        Assert.False(await store.RenewAsync(claim, Ct));
        Assert.Equal("Completed", await TurnStatus(turn));
    }

    [Fact]
    public async Task Renewal_extends_the_lease_but_never_the_deadline()
    {
        var turn = (await SeedProject(1))[0];
        var claim = (await ClaimAs("a"))!;
        var deadline = await Scalar<DateTime>("SELECT \"DeadlineAt\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn));
        await Execute("UPDATE \"ChatTurns\" SET \"LeaseUntil\" = clock_timestamp() + interval '2 seconds' WHERE \"Id\" = @t", ("t", turn));

        using var scope = App.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().RenewAsync(claim, Ct));

        var left = await Scalar<double>("SELECT extract(epoch FROM (\"LeaseUntil\" - clock_timestamp()))::float8 FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn));
        Assert.InRange(left, 25, 30);
        Assert.Equal(deadline, await Scalar<DateTime>("SELECT \"DeadlineAt\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal(1, await Scalar<int>("SELECT \"AttemptCount\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public async Task An_owner_with_an_expired_lease_can_neither_renew_nor_complete()
    {
        var turn = (await SeedProject(1))[0];
        var claim = (await ClaimAs("a"))!;
        var answer = await InsertAssistantMessage(turn);
        await ExpireLease(turn);
        using var scope = App.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>();

        // Un lease scaduto potrebbe essere già stato sostituito da un'altra esecuzione: niente rinnovo né commit.
        Assert.False(await store.RenewAsync(claim, Ct));
        Assert.False(await store.CompleteAsync(claim, answer, Ct));
        Assert.Equal("Running", await TurnStatus(turn));
    }

    // ---------------------------------------------------------------- retry, backoff, limiti

    [Fact]
    public async Task A_transient_failure_requeues_with_a_persisted_backoff_of_2_then_4_seconds()
    {
        var turn = (await SeedProject(1))[0];

        var first = await ReleaseTransient((await ClaimAs("a"))!);
        Assert.Equal(TurnTransition.Requeued, first.Outcome);
        Assert.InRange((first.NextAttemptAt!.Value - await DatabaseNow()).TotalSeconds, 0.5, 2.0);
        Assert.Null(await Scalar<string?>("SELECT \"LeaseOwner\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal("Queued", await TurnStatus(turn));

        await Execute("UPDATE \"ChatTurns\" SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @t", ("t", turn));
        var second = await ReleaseTransient((await ClaimAs("a"))!);
        Assert.InRange((second.NextAttemptAt!.Value - await DatabaseNow()).TotalSeconds, 2.5, 4.0);
        Assert.Equal(2, await Scalar<int>("SELECT \"AttemptCount\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public async Task Retry_after_defers_the_next_attempt_when_it_exceeds_the_backoff()
    {
        await SeedProject(1);

        var release = await ReleaseTransient((await ClaimAs("a"))!, retryAfter: TimeSpan.FromSeconds(60));

        Assert.Equal(TurnTransition.Requeued, release.Outcome);
        Assert.InRange((release.NextAttemptAt!.Value - await DatabaseNow()).TotalSeconds, 55, 60);
    }

    [Fact]
    public async Task Transient_failures_stop_at_max_attempts_with_a_terminal_failed_state()
    {
        var turn = (await SeedProject(1))[0];
        TurnRelease last = default;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Execute("UPDATE \"ChatTurns\" SET \"NextAttemptAt\" = NULL WHERE \"Id\" = @t", ("t", turn));
            var claim = await ClaimAs("a");
            Assert.Equal(attempt, claim!.AttemptCount);
            last = await ReleaseTransient(claim);
        }

        Assert.Equal(TurnTransition.Failed, last.Outcome);
        Assert.Equal("attempts_exhausted", last.ErrorCode);
        Assert.Equal("Failed", await TurnStatus(turn));
        Assert.Null(await ClaimAs("a"));
        Assert.Equal(3, await Scalar<int>("SELECT \"AttemptCount\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public async Task A_permanent_failure_is_terminal_without_retry()
    {
        var turn = (await SeedProject(1))[0];
        var claim = (await ClaimAs("a"))!;
        using var scope = App.Services.CreateScope();

        var release = await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>()
            .ReleaseAfterFailureAsync(claim, new ModelFailure(false, "model_unauthorized", null), "model_unauthorized", "Rifiutato.", Ct);

        Assert.Equal(TurnTransition.Failed, release.Outcome);
        Assert.Equal("model_unauthorized", release.ErrorCode);
        Assert.Equal("Failed", await TurnStatus(turn));
        Assert.NotNull(await Scalar<object?>("SELECT \"CompletedAt\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Null(await ClaimAs("a"));
    }

    [Fact]
    public async Task A_retry_that_would_exceed_the_deadline_fails_with_deadline_exceeded()
    {
        var turn = (await SeedProject(1))[0];
        var claim = (await ClaimAs("a"))!;
        await Execute("UPDATE \"ChatTurns\" SET \"DeadlineAt\" = clock_timestamp() + interval '1 second' WHERE \"Id\" = @t", ("t", turn));

        var release = await ReleaseTransient(claim);

        Assert.Equal(TurnTransition.Failed, release.Outcome);
        Assert.Equal("deadline_exceeded", release.ErrorCode);
    }

    [Fact]
    public async Task A_queued_turn_past_its_deadline_fails_and_unblocks_the_next_one()
    {
        var turns = await SeedProject(2);
        await Execute("UPDATE \"ChatTurns\" SET \"DeadlineAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @t", ("t", turns[0]));

        var claim = await ClaimAs("a");

        Assert.Equal(turns[1], claim!.TurnId);
        Assert.Equal("Failed", await TurnStatus(turns[0]));
        Assert.Equal("deadline_exceeded", await Scalar<string>("SELECT \"ErrorCode\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turns[0])));
    }

    [Fact]
    public async Task A_crashed_turn_with_no_attempts_left_fails_instead_of_being_reclaimed()
    {
        var turn = (await SeedProject(1))[0];
        await ClaimAs("a");
        await Execute("UPDATE \"ChatTurns\" SET \"AttemptCount\" = \"MaxAttempts\", \"LeaseUntil\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @t", ("t", turn));

        Assert.Null(await ClaimAs("b"));

        Assert.Equal("Failed", await TurnStatus(turn));
        Assert.Equal("attempts_exhausted", await Scalar<string>("SELECT \"ErrorCode\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public async Task A_reclaim_after_a_crash_does_not_add_the_error_backoff()
    {
        var turn = (await SeedProject(1))[0];
        await ClaimAs("a");
        await ExpireLease(turn);

        var claim = await ClaimAs("b");

        Assert.Equal(turn, claim!.TurnId);
        Assert.Null(await Scalar<object?>("SELECT \"NextAttemptAt\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    // ---------------------------------------------------------------- restart

    [Fact]
    public async Task Attempts_lease_and_fence_survive_an_application_restart()
    {
        var turn = (await SeedProject(1))[0];
        await ClaimAs("before-restart");
        await _app!.DisposeAsync();
        _app = null;

        await using var restarted = await ApiHarness.StartAsync(_database.ConnectionString, prepare: false);
        using (var scope = restarted.Services.CreateScope())
        {
            // Lease ancora valido: nessun reclaim, contatori intatti.
            Assert.Null(await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextAsync("after", Ct));
        }

        await ExpireLease(turn);
        using var again = restarted.Services.CreateScope();
        var claim = await again.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextAsync("after-restart", Ct);

        Assert.Equal(2, claim!.AttemptCount);
        Assert.Equal(2, claim.FencingToken);
    }

    // ---------------------------------------------------------------- dispatcher

    [Fact]
    public async Task Without_an_executor_the_dispatcher_leaves_pending_turns_untouched()
    {
        var turn = (await SeedProject(1))[0];
        var dispatcher = Dispatcher(executor: null);

        Assert.False(await dispatcher.RunOnceAsync(Ct));

        Assert.Equal("Queued", await TurnStatus(turn));
        Assert.Equal(0, await Scalar<int>("SELECT \"AttemptCount\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public async Task The_hosted_dispatcher_does_not_consume_turns_while_no_executor_exists()
    {
        var turn = (await SeedProject(1))[0];

        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct); // più cicli di polling di quello del dispatcher reale

        Assert.Equal("Queued", await TurnStatus(turn));
        Assert.Equal(0, await Scalar<int>("SELECT \"AttemptCount\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public void The_dispatcher_is_registered_as_a_hosted_service()
    {
        Assert.Contains(App.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is AiDispatchService);
    }

    [Fact]
    public async Task A_network_timeout_from_the_executor_requeues_the_turn()
    {
        var turn = (await SeedProject(1))[0];
        // Cancellazione non dovuta né all'arresto né al lease: timeout del trasporto, quindi transitorio.
        var dispatcher = Dispatcher(new DelegateExecutor((_, _) => throw new TaskCanceledException("timeout")));

        Assert.True(await dispatcher.RunOnceAsync(Ct));

        Assert.Equal("Queued", await TurnStatus(turn));
        Assert.NotNull(await Scalar<object?>("SELECT \"NextAttemptAt\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public async Task Stopping_the_application_leaves_the_turn_running_for_a_later_reclaim()
    {
        var turn = (await SeedProject(1))[0];
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var dispatcher = Dispatcher(new DelegateExecutor(async (_, ct) =>
        {
            await stop.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }));

        Assert.True(await dispatcher.RunOnceAsync(stop.Token));

        // Né Failed né Queued: il lease scade e il turno viene ripreso con un nuovo tentativo.
        Assert.Equal("Running", await TurnStatus(turn));
        await ExpireLease(turn);
        Assert.Equal(2, (await ClaimAs("next"))!.AttemptCount);
    }

    [Fact]
    public async Task A_transient_executor_error_requeues_the_turn_with_backoff()
    {
        var turn = (await SeedProject(1))[0];
        var dispatcher = Dispatcher(new DelegateExecutor((_, _) => throw new HttpRequestException("giù", null, HttpStatusCode.ServiceUnavailable)));

        Assert.True(await dispatcher.RunOnceAsync(Ct));

        Assert.Equal("Queued", await TurnStatus(turn));
        Assert.Equal(1, await Scalar<int>("SELECT \"AttemptCount\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.NotNull(await Scalar<object?>("SELECT \"NextAttemptAt\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.False(await dispatcher.RunOnceAsync(Ct)); // ancora in backoff
    }

    [Fact]
    public async Task A_permanent_executor_error_fails_the_turn_without_leaking_the_exception_message()
    {
        var turn = (await SeedProject(1))[0];
        var dispatcher = Dispatcher(new DelegateExecutor((_, _) => throw new HttpRequestException("segreto-nel-messaggio", null, HttpStatusCode.Unauthorized)));

        Assert.True(await dispatcher.RunOnceAsync(Ct));

        Assert.Equal("Failed", await TurnStatus(turn));
        Assert.Equal("model_unauthorized", await Scalar<string>("SELECT \"ErrorCode\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.DoesNotContain("segreto", await Scalar<string>("SELECT \"ErrorMessage\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public async Task An_executor_that_returns_without_completing_does_not_leave_a_half_done_turn()
    {
        var turn = (await SeedProject(1))[0];
        var dispatcher = Dispatcher(new DelegateExecutor((_, _) => Task.CompletedTask));

        Assert.True(await dispatcher.RunOnceAsync(Ct));

        Assert.Equal("Failed", await TurnStatus(turn));
        Assert.Equal("processing_failed", await Scalar<string>("SELECT \"ErrorCode\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public async Task The_dispatcher_renews_the_lease_while_the_executor_runs_and_keeps_the_deadline()
    {
        var turn = (await SeedProject(1))[0];
        var deadlineBefore = await Scalar<DateTime>("SELECT \"DeadlineAt\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn));
        DateTime renewedUntil = default;
        DateTime claimedUntil = default;
        DateTime deadlineDuring = default;
        var dispatcher = Dispatcher(new DelegateExecutor(async (claim, ct) =>
        {
            claimedUntil = claim.LeaseUntil.UtcDateTime;
            await Task.Delay(TimeSpan.FromSeconds(2.5), ct);
            renewedUntil = await Scalar<DateTime>("SELECT \"LeaseUntil\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn));
            deadlineDuring = await Scalar<DateTime>("SELECT \"DeadlineAt\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn));
            var answer = await InsertAssistantMessage(turn);
            using var scope = App.Services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().CompleteAsync(claim, answer, ct));
        }), renewSeconds: 1);

        Assert.True(await dispatcher.RunOnceAsync(Ct));

        Assert.Equal("Completed", await TurnStatus(turn));
        Assert.Equal(deadlineBefore, deadlineDuring);
        Assert.True(renewedUntil > claimedUntil.AddSeconds(1), "il lease deve essere stato rinnovato oltre la scadenza iniziale");
    }

    [Fact]
    public async Task Losing_the_lease_cancels_the_executor_and_nothing_is_committed_by_the_old_owner()
    {
        var turn = (await SeedProject(1))[0];
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Dispatcher(new DelegateExecutor(async (claim, ct) =>
        {
            // Un altro owner prende il turno (reclaim simulato sul database): il rinnovo successivo deve fallire.
            await Execute("UPDATE \"ChatTurns\" SET \"LeaseOwner\" = 'altro', \"FencingToken\" = \"FencingToken\" + 1 WHERE \"Id\" = @t", ("t", turn));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult(true);
                throw;
            }
        }), renewSeconds: 1);

        Assert.True(await dispatcher.RunOnceAsync(Ct));

        Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal("Running", await TurnStatus(turn));
        Assert.Equal("altro", await Scalar<string>("SELECT \"LeaseOwner\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    // ---------------------------------------------------------------- supporto

    private sealed class DelegateExecutor(Func<ChatTurnClaim, CancellationToken, Task> run) : IChatTurnExecutor
    {
        public Task ExecuteAsync(ChatTurnClaim claim, CancellationToken ct) => run(claim, ct);
    }

    private AiDispatchService Dispatcher(IChatTurnExecutor? executor, int renewSeconds = 10) =>
        new(
            App.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ExecutionOptions { RenewIntervalSeconds = renewSeconds }),
            NullLogger<AiDispatchService>.Instance,
            executor);

    private async Task<ChatTurnClaim?> ClaimAs(string owner)
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextAsync(owner, Ct);
    }

    private async Task<TurnRelease> ReleaseTransient(ChatTurnClaim claim, TimeSpan? retryAfter = null)
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>()
            .ReleaseAfterFailureAsync(claim, new ModelFailure(true, "model_unreachable", retryAfter), "model_error", "x", Ct);
    }

    /// <summary>Crea un progetto e <paramref name="messages"/> messaggi PM (un turno Queued ciascuno, in ordine).</summary>
    private async Task<List<Guid>> SeedProject(int messages)
    {
        using var client = await App.LoginAsync();
        var created = await client.PostAsJsonAsync("/api/projects", new { name = $"Dispatch {Guid.NewGuid():N}" }, Ct);
        var projectId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var turns = new List<Guid>();
        for (var i = 1; i <= messages; i++)
        {
            var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/messages", new { clientMessageId = Guid.NewGuid(), text = $"m{i}" }, Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            turns.Add((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("chatTurnId").GetGuid());
        }

        return turns;
    }

    /// <summary>Messaggio assistant collegato al turno, come lo scriverà il commit di T018 (qui solo per provare la guardia di Completed).</summary>
    private async Task<Guid> InsertAssistantMessage(Guid turnId)
    {
        var id = Guid.NewGuid();
        await Execute(
            """
            INSERT INTO "ConversationMessages" ("Id", "ConversationId", "Sequence", "Role", "Body", "CreatedAt", "AgentId", "ReplyToMessageId", "ChatTurnId")
            SELECT @id, t."ConversationId", (SELECT max(m2."Sequence") + 1 FROM "ConversationMessages" m2 WHERE m2."ConversationId" = t."ConversationId"),
                   'Assistant', 'risposta di prova', clock_timestamp(), 'test', t."TriggerMessageId", t."Id"
            FROM "ChatTurns" t WHERE t."Id" = @t
            """,
            ("id", id), ("t", turnId));
        return id;
    }

    private Task ExpireLease(Guid turn) =>
        Execute("UPDATE \"ChatTurns\" SET \"LeaseUntil\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @t", ("t", turn));

    private async Task<DateTimeOffset> DatabaseNow() =>
        new(DateTime.SpecifyKind(await Scalar<DateTime>("SELECT clock_timestamp()"), DateTimeKind.Utc));

    private Task<string> TurnStatus(Guid turn) => Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn));

    private async Task<long> Count(string sql, params (string Name, object Value)[] parameters) => await Scalar<long>(sql, parameters);

    private async Task Execute(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Build(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Build(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is null or DBNull ? default! : (T)value;
    }

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
