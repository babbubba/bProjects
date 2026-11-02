using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T119 (PG + esecutore finto) — timer root e sweep nel dispatcher esistente: il monitor delle scadenze gira ogni secondo, indipendente dallo
/// slot AI, chiude root scaduti (anche con lease ancora valido) e i figli aperti, si riarma sempre dal database (un vecchio claim o timer a 180 s
/// non chiude un root già promosso; la scadenza esatta impedisce commit), riconcilia al restart senza ricreare budget, e non promuove mai: né
/// coda né tempo. Nessun secondo scheduler, nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentDeadlineDispatchTests
{
    private const string Owner = "worker-a";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ExecutionLeaseStore Leases(AppDbContext context) => new(context, Options.Create(new ExecutionOptions()));

    private static async Task<T> Value<T>(ParsingDatabase database, string sql) => await database.ScalarAsync<T>(sql);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, Ct);
        }

        Assert.Fail($"Condizione non raggiunta entro {timeout.TotalSeconds:0} s: {because}");
    }

    private sealed class BlockingExecutor : IChatTurnExecutor
    {
        public TaskCompletionSource<Guid> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(ChatTurnClaim claim, CancellationToken ct)
        {
            Started.TrySetResult(claim.TurnId);
            try
            {
                await Release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class Rig(ParsingDatabase database, ExecutionOptions options, BlockingExecutor executor) : IAsyncDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString))
            .AddSingleton<IOptions<ExecutionOptions>>(Options.Create(options))
            .AddScoped<ExecutionLeaseStore>()
            .BuildServiceProvider();

        public BlockingExecutor Executor { get; } = executor;

        public ParsingDatabase Database => database;

        public AiDispatchService Dispatcher => field ??= new(
            _services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), NullLogger<AiDispatchService>.Instance, executor: Executor);

        public async ValueTask DisposeAsync()
        {
            Executor.Release.TrySetResult();
            await Dispatcher.StopAsync(CancellationToken.None);
            await _services.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    private static async Task<Rig> NewRigAsync(ExecutionOptions? options = null) =>
        new(await ParsingDatabase.CreateAsync(), options ?? new ExecutionOptions(), new BlockingExecutor());

    // ------------------------------------------------------------------ sweep

    [Fact]
    public async Task The_sweep_closes_expired_documentary_roots_even_with_a_live_lease_and_nothing_else()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var runningExpired = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { EffectiveSql = "clock_timestamp() - interval '1 millisecond'" });
        var queuedExpired = await DocumentRegimeRoots.SeedAsync(database, RootKind.Run, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, EffectiveSql = "clock_timestamp() - interval '1 millisecond'" });
        // Scadenza esatta a t0+3600: il clock del database la raggiunge, l'ultimo istante non appartiene più al root.
        var exactCap = await DocumentRegimeRoots.SeedAsync(database, RootKind.Run, new RootSpec { Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Extended, ElapsedSeconds = 3600 });
        var live = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { ElapsedSeconds = 170 });
        var promoted = await DocumentRegimeRoots.SeedAsync(database, RootKind.Run, new RootSpec { Regime = ExecutionRegime.Extended, PromotionCount = 1, ElapsedSeconds = 200 });   // i 180 s sono passati: il vecchio limite non vale più
        var legacy = await DocumentRegimeRoots.SeedAsync(database, RootKind.Run, new RootSpec { Legacy = true, LegacyDeadlineSql = "clock_timestamp() - interval '1 minute'" });
        await using var context = database.NewContext();
        var leases = Leases(context);

        // Senza chi esegue quel tipo di lavoro non si legge né si tocca (come per i claim).
        Assert.False((await leases.SweepExpiredDocumentWorkAsync(turns: false, analysisRuns: false, documentRuns: false, Ct)).Any);
        Assert.Equal("Running", await Value<string>(database, $"SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = '{runningExpired.Id}'"));

        var closed = await leases.SweepExpiredDocumentWorkAsync(turns: true, analysisRuns: true, documentRuns: true, Ct);

        Assert.Equal(new[] { runningExpired.Id }, closed.Turns.ToArray());
        Assert.Equal(new[] { queuedExpired.Id, exactCap.Id }.Order().ToArray(), closed.Runs.Order().ToArray());
        foreach (var root in new[] { runningExpired, queuedExpired, exactCap })
        {
            Assert.Equal(1, await Value<int>(database, $"""
                SELECT count(*) FROM "{root.Table}" WHERE "Id" = '{root.Id}' AND "Status" = 'Failed' AND "ErrorCode" = 'deadline_exceeded' AND "CompletedAt" IS NOT NULL
                  AND "LeaseOwner" IS NULL AND "LeaseUntil" IS NULL
                """));
        }

        Assert.Equal(4L, await Value<long>(database, $"SELECT \"FencingToken\" FROM \"ChatTurns\" WHERE \"Id\" = '{runningExpired.Id}'"));   // il vecchio possessore è fence-ato
        Assert.Equal(1L, await Value<long>(database, $"SELECT \"FencingToken\" FROM \"ProcessingRuns\" WHERE \"Id\" = '{queuedExpired.Id}'"));
        Assert.Equal("Running", await Value<string>(database, $"SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = '{live.Id}'"));
        Assert.Equal("Running", await Value<string>(database, $"SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = '{promoted.Id}'"));
        Assert.Equal("Running", await Value<string>(database, $"SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = '{legacy.Id}'"));   // ramo 001 invariato

        // Dopo la chiusura il vecchio possessore non rinnova, non committa e non scrive checkpoint.
        var oldClaim = new ChatTurnClaim(runningExpired.Id, runningExpired.Conversation, Guid.Empty, 1, Owner, 3, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.False(await leases.RenewAsync(oldClaim, Ct));
        Assert.False(await leases.CompleteAsync(oldClaim, Guid.NewGuid(), Ct));
        Assert.False((await leases.SweepExpiredDocumentWorkAsync(true, true, true, Ct)).Any);   // idempotente
    }

    [Fact]
    public async Task The_sweep_closes_an_expired_parent_and_its_open_children_but_not_a_parent_still_alive()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var expired = await database.SeedParseAsync();
        var alive = await database.SeedParseAsync();
        var aggregator = Guid.NewGuid();
        await using (var context = database.NewContext())
        {
            context.EvidenceExtractionJobs.Add(new EvidenceExtractionJob
            {
                Id = aggregator, ProjectId = expired.Project, OriginKind = EvidenceOriginKind.Document, PreparationId = expired.Preparation, Reason = ExtractionJobReason.Initial,
                Status = ExtractionJobStatus.Queued, RequestedAt = DateTimeOffset.UtcNow, DeadlineAt = DateTimeOffset.UtcNow.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
            });
            await context.SaveChangesAsync(Ct);
        }

        await database.ExecuteAsync($"""
            UPDATE "DocumentJobs" SET "Status" = 'Running', "LeaseOwner" = '{Owner}', "LeaseUntil" = clock_timestamp() + interval '1 minute', "FencingToken" = 5, "AttemptCount" = 1 WHERE "Id" = '{expired.Job}';
            UPDATE "DocumentPreparations" SET "Status" = 'Extracting', "EffectiveDeadlineAt" = clock_timestamp() - interval '1 millisecond' WHERE "Id" = '{expired.Preparation}';
            """);
        await using var sweepContext = database.NewContext();

        var closed = await Leases(sweepContext).SweepExpiredDocumentWorkAsync(true, true, true, Ct);

        Assert.Equal(new[] { expired.Preparation }, closed.Preparations.ToArray());
        Assert.Equal(new[] { expired.Job }, closed.Jobs.ToArray());
        Assert.Equal(new[] { aggregator }, closed.Extractions.ToArray());
        Assert.Equal(1, await Value<int>(database, $"SELECT count(*) FROM \"DocumentPreparations\" WHERE \"Id\" = '{expired.Preparation}' AND \"Status\" = 'Failed' AND \"ErrorCode\" = 'deadline_exceeded'"));
        Assert.Equal(1, await Value<int>(database, $"""
            SELECT count(*) FROM "DocumentJobs" WHERE "Id" = '{expired.Job}' AND "Status" = 'Failed' AND "ErrorCode" = 'parent_deadline_exceeded'
              AND "LeaseOwner" IS NULL AND "LeaseUntil" IS NULL AND "FencingToken" = 6 AND "CompletedAt" IS NOT NULL
            """));
        Assert.Equal(1, await Value<int>(database, $"SELECT count(*) FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = '{aggregator}' AND \"Status\" = 'Failed' AND \"ErrorCode\" = 'parent_deadline_exceeded'"));
        // il padre ancora vivo e il suo figlio non si toccano
        Assert.Equal("Queued", await Value<string>(database, $"SELECT \"Status\" FROM \"DocumentJobs\" WHERE \"Id\" = '{alive.Job}'"));
        Assert.Equal("Queued", await Value<string>(database, $"SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = '{alive.Preparation}'"));
    }

    // ------------------------------------------------------------------ monitor nel dispatcher

    [Fact]
    public async Task The_monitor_keeps_closing_expired_roots_while_the_ai_slot_is_occupied_and_never_promotes_a_queued_root()
    {
        await using var rig = await NewRigAsync();
        var database = await DatabaseOf(rig);
        var occupant = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Extended, ElapsedSeconds = 300 });   // il più vecchio: prende lo slot
        var soon = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, EffectiveSql = "clock_timestamp() + interval '2 seconds'" });
        var queued = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, ElapsedSeconds = 120 });   // a 120 s, fermo in coda
        var before = await Value<string>(database, $"SELECT concat_ws('|', \"Regime\", \"PromotionCount\", \"DeadlineAt\", \"EffectiveDeadlineAt\", \"HardDeadlineAt\") FROM \"ChatTurns\" WHERE \"Id\" = '{queued.Id}'");

        await rig.Dispatcher.StartAsync(Ct);
        Assert.Equal(occupant.Id, await rig.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));   // lo slot è occupato e resta tale

        await WaitUntilAsync(async () => await Value<string>(database, $"SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = '{soon.Id}'") == "Failed", TimeSpan.FromSeconds(8), "il root scaduto va chiuso dal monitor con lo slot occupato");

        Assert.Equal("deadline_exceeded", await Value<string>(database, $"SELECT \"ErrorCode\" FROM \"ChatTurns\" WHERE \"Id\" = '{soon.Id}'"));
        Assert.Equal("Running", await Value<string>(database, $"SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = '{occupant.Id}'"));
        Assert.False(rig.Executor.Cancelled.Task.IsCompleted);
        // La coda non promuove: stesso regime, nessun conteggio, nessuna transizione, stesse scadenze.
        Assert.Equal(before, await Value<string>(database, $"SELECT concat_ws('|', \"Regime\", \"PromotionCount\", \"DeadlineAt\", \"EffectiveDeadlineAt\", \"HardDeadlineAt\") FROM \"ChatTurns\" WHERE \"Id\" = '{queued.Id}'"));
        Assert.Equal("Queued", await Value<string>(database, $"SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = '{queued.Id}'"));
        Assert.Equal(0L, await Value<long>(database, "SELECT count(*) FROM \"RegimeTransitions\""));
    }

    [Fact]
    public async Task An_old_timer_at_the_ordinary_deadline_does_not_close_or_cancel_a_root_promoted_in_time()
    {
        await using var rig = await NewRigAsync();
        var database = await DatabaseOf(rig);
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, EffectiveSql = "clock_timestamp() + interval '2 seconds'" });
        await rig.Dispatcher.StartAsync(Ct);
        await rig.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Promozione (come la CAS T047) prima della vecchia scadenza: DeadlineAt = EffectiveDeadlineAt = Hard.
        await database.ExecuteAsync($"""
            UPDATE "ChatTurns" SET "Regime" = 'Extended', "PromotionCount" = 1, "PromotedAt" = clock_timestamp(), "PromotedReason" = 'additional_work',
                "DeadlineAt" = "HardDeadlineAt", "EffectiveDeadlineAt" = "HardDeadlineAt" WHERE "Id" = '{root.Id}'
            """);
        await Task.Delay(TimeSpan.FromSeconds(4), Ct);   // la vecchia scadenza (2 s) è passata da 2 s; il monitor ha girato più volte

        Assert.Equal("Running", await Value<string>(database, $"SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = '{root.Id}'"));
        Assert.False(rig.Executor.Cancelled.Task.IsCompleted);
        Assert.Equal(1L, await Value<long>(database, $"SELECT \"FencingToken\" FROM \"ChatTurns\" WHERE \"Id\" = '{root.Id}'"));   // fence 1 dal claim: nessun bump da sweep
    }

    [Fact]
    public async Task An_expired_root_is_closed_the_running_execution_is_cancelled_and_a_late_commit_is_refused()
    {
        await using var rig = await NewRigAsync();
        var database = await DatabaseOf(rig);
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, EffectiveSql = "clock_timestamp() + interval '2 seconds'" });
        await rig.Dispatcher.StartAsync(Ct);
        await rig.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await rig.Executor.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(8), Ct);   // il timer root è letto dal database, non dal claim

        await WaitUntilAsync(async () => await Value<string>(database, $"SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = '{root.Id}'") == "Failed", TimeSpan.FromSeconds(5), "root chiuso");
        Assert.Equal("deadline_exceeded", await Value<string>(database, $"SELECT \"ErrorCode\" FROM \"ChatTurns\" WHERE \"Id\" = '{root.Id}'"));
        await using var context = database.NewContext();
        var stale = new ChatTurnClaim(root.Id, root.Conversation, Guid.Empty, 1, "x", 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.False(await Leases(context).CompleteAsync(stale, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task A_restart_reconciles_from_the_database_closing_expired_roots_at_once_and_never_recreates_the_budget()
    {
        await using var rig = await NewRigAsync();
        var database = await DatabaseOf(rig);
        // Il processo precedente è morto con un lease ancora valido: al riavvio la scadenza vale subito, senza attendere la fine del lease.
        var dead = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { EffectiveSql = "clock_timestamp() - interval '1 second'", LeaseUntilSql = "clock_timestamp() + interval '5 minutes'" });
        var survivor = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, ElapsedSeconds = 100 });
        var budget = $"SELECT concat_ws('|', \"HardDeadlineAt\", \"EffectiveDeadlineAt\", \"DeadlineAt\", \"RequestedAt\", \"Regime\", \"PromotionCount\", \"OrdinaryDeadlineAt\") FROM \"ChatTurns\" WHERE \"Id\" = '{survivor.Id}'";
        var before = await Value<string>(database, budget);

        var closed = await rig.Dispatcher.SweepDeadlinesOnceAsync(Ct);   // la riconciliazione del restart è il primo sweep

        Assert.Equal(new[] { dead.Id }, closed.Turns.ToArray());
        Assert.Equal("Failed", await Value<string>(database, $"SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = '{dead.Id}'"));
        await using var context = database.NewContext();
        var claim = (await Leases(context).ClaimNextWorkAsync("w-restart", turns: true, runs: false, Ct))!.Turn!;
        Assert.Equal(survivor.Id, claim.TurnId);
        Assert.Equal(before, await Value<string>(database, budget));   // il claim non sposta alcuna scadenza e non azzera il t0
    }

    [Fact]
    public async Task Automatic_retries_keep_the_same_deadline_with_backoff_2_then_4_seconds_and_stop_at_three_attempts()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, ElapsedSeconds = 10 });
        var caps = $"SELECT concat_ws('|', \"HardDeadlineAt\", \"EffectiveDeadlineAt\", \"DeadlineAt\", \"RequestedAt\", \"Regime\", \"PromotionCount\") FROM \"ChatTurns\" WHERE \"Id\" = '{root.Id}'";
        var before = await Value<string>(database, caps);
        await using var context = database.NewContext();
        var leases = Leases(context);
        var transient = new ModelFailure(true, "model_unreachable", null);

        var first = await leases.ClaimNextAsync("w1", Ct);
        var firstRelease = await leases.ReleaseAfterFailureAsync(first!, transient, "x", "x", Ct);
        Assert.Equal(TurnTransition.Requeued, firstRelease.Outcome);
        Assert.InRange((firstRelease.NextAttemptAt!.Value - DateTimeOffset.UtcNow).TotalSeconds, 0.5, 2.5);

        await database.ExecuteAsync($"UPDATE \"ChatTurns\" SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = '{root.Id}'");
        var second = await leases.ClaimNextAsync("w2", Ct);
        Assert.Equal(2, second!.AttemptCount);
        var secondRelease = await leases.ReleaseAfterFailureAsync(second, transient, "x", "x", Ct);
        Assert.InRange((secondRelease.NextAttemptAt!.Value - DateTimeOffset.UtcNow).TotalSeconds, 2.5, 4.5);

        await database.ExecuteAsync($"UPDATE \"ChatTurns\" SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = '{root.Id}'");
        var third = await leases.ClaimNextAsync("w3", Ct);
        Assert.Equal(3, third!.AttemptCount);
        var thirdRelease = await leases.ReleaseAfterFailureAsync(third, transient, "x", "x", Ct);
        Assert.Equal((TurnTransition.Failed, "attempts_exhausted"), (thirdRelease.Outcome, thirdRelease.ErrorCode));

        Assert.Equal(before, await Value<string>(database, caps));   // né reset né allungamento dei tentativi o delle scadenze
    }

    [Fact]
    public async Task The_sweep_interval_is_one_second_by_default_and_must_be_positive()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), new ExecutionOptions().DeadlineSweepInterval);
        var validator = new ExecutionOptionsValidator();
        Assert.True(validator.Validate(null, new ExecutionOptions()).Succeeded);
        Assert.False(validator.Validate(null, new ExecutionOptions { DeadlineSweepIntervalSeconds = 0 }).Succeeded);
        await Task.CompletedTask;
    }

    private static Task<ParsingDatabase> DatabaseOf(Rig rig) => Task.FromResult(rig.Database);
}
