using System.Net;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T064 (chiusura) — intervalli di attesa persistiti e mutuamente esclusivi di un root documentale: Preflight scritto nella stessa transazione della
/// classificazione, coda che parte dalla fine del preflight (e dalla fine del backoff dopo un retry), Backoff scritto al rilascio con retry; il recupero resta
/// non prodotto (T102). PostgreSQL reale, sonda e esecutore finti: nessun modello, nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentExecutionIntervalsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string PolicyHash => DocumentRegimePolicy.ComputePolicyHash(180, 3_600);
    private static RegimeMeasurement Fit => DocumentRootClassificationInputTests.Fit;

    private sealed class SlowProbe(RegimeMeasurement measurement, int delayMs) : IDocumentRegimeProbe
    {
        public async Task<RegimeMeasurement> MeasureAsync(ClassificationOwnerKind kind, Guid rootId, CancellationToken ct)
        {
            await Task.Delay(delayMs, ct);
            return measurement;
        }
    }

    /// <summary>Esecutore dei turni finto: il primo tentativo fallisce in modo transitorio (503), i successivi terminano senza commit.</summary>
    private sealed class FlakyExecutor : IChatTurnExecutor
    {
        public int Calls { get; private set; }

        public Task ExecuteAsync(ChatTurnClaim claim, CancellationToken ct)
        {
            Calls++;
            return claim.AttemptCount == 1 ? throw new HttpRequestException("giu", null, HttpStatusCode.ServiceUnavailable) : Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILogger<AiDispatchService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required ServiceProvider Services { get; init; }
        public required DocumentRootClassifier Classifier { get; init; }
        public required AiDispatchService Dispatcher { get; init; }

        public static Rig Create(ParsingDatabase database, IDocumentRegimeProbe probe, IChatTurnExecutor executor, ILogger<AiDispatchService>? logger = null)
        {
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString));
            services.AddSingleton(Options.Create(new ExecutionOptions()));
            services.AddSingleton(Options.Create(new DocumentOptions()));
            services.AddScoped<ExecutionLeaseStore>();
            services.AddScoped<DocumentPolicyService>();
            var provider = services.BuildServiceProvider();
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var classifier = new DocumentRootClassifier(scopes, probe);
            var dispatcher = new AiDispatchService(
                scopes, Options.Create(new ExecutionOptions()), logger ?? NullLogger<AiDispatchService>.Instance, executor: executor, classifier: classifier);
            return new Rig { Services = provider, Classifier = classifier, Dispatcher = dispatcher };
        }

        public async ValueTask DisposeAsync() => await Services.DisposeAsync();
    }

    private static async Task<SeededRoot> SeedPendingAsync(ParsingDatabase database, RootKind kind, double elapsed = 5)
    {
        var root = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, Legacy = true });
        var seconds = elapsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await database.ExecuteAsync($"""
            UPDATE "{root.Table}" x SET "RequestedAt" = v.r, "ClassificationStatus" = 'Pending', "Regime" = 'Extended', "ClassificationVersion" = 'document-regime-v2',
                "ClassificationReason" = 'preflight_pending', "PolicyHash" = '{PolicyHash}', "HardDeadlineAt" = v.h, "EffectiveDeadlineAt" = v.h, "DeadlineAt" = v.h,
                "PromotionCount" = 0, "CallCount" = 0
            FROM (SELECT q.r, q.r + interval '3600 seconds' AS h FROM (SELECT clock_timestamp() - interval '{seconds} seconds' AS r) q) v
            WHERE x."Id" = '{root.Id}'
            """);
        return root;
    }

    private static ClassificationOwnerKind OwnerOf(SeededRoot root) => root.Kind == RootKind.Turn ? ClassificationOwnerKind.ChatTurn : ClassificationOwnerKind.ProcessingRun;

    /// <summary>Intervallo seminato con estremi espressi in secondi rispetto al t0 del root (clock del database).</summary>
    internal static Task SeedIntervalAsync(ParsingDatabase database, SeededRoot root, string kind, int attempt, double fromSeconds, double toSeconds)
    {
        var from = fromSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var to = toSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return database.ExecuteAsync($"""
            INSERT INTO "ExecutionIntervals" ("Id","ProjectId","RootKind","RootId","Kind","Attempt","StartedAt","EndedAt")
            SELECT gen_random_uuid(), '{root.Project}', '{(root.Kind == RootKind.Turn ? "ChatTurn" : "ProcessingRun")}', x."Id", '{kind}', {attempt},
                   x."RequestedAt" + make_interval(secs => {from}), x."RequestedAt" + make_interval(secs => {to})
            FROM "{root.Table}" x WHERE x."Id" = '{root.Id}'
            """);
    }

    private static Task<long> Count(ParsingDatabase db, SeededRoot root, string kind) =>
        db.ScalarAsync<long>($"""SELECT count(*) FROM "ExecutionIntervals" WHERE "RootId" = '{root.Id}' AND "Kind" = '{kind}'""");

    private static async Task<RootTelemetryView> ViewAsync(ParsingDatabase database, SeededRoot root)
    {
        await using var db = database.NewContext();
        return (await ExecutionTelemetry.ForRootAsync(db, root.Project, OwnerOf(root), root.Id, Ct))!;
    }

    // ================================================================ Preflight

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task The_classification_transaction_writes_one_preflight_interval_ending_at_the_classification_record(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var rig = Rig.Create(database, new SlowProbe(Fit, 400), new FlakyExecutor());
        var root = await SeedPendingAsync(database, kind, elapsed: 5);

        var result = await rig.Classifier.ClassifyAsync(OwnerOf(root), root.Id, Ct);
        var again = await rig.Classifier.ClassifyAsync(OwnerOf(root), root.Id, Ct);

        Assert.Equal(ClassificationAttempt.Classified, result.Attempt);
        Assert.Equal(ClassificationAttempt.NotPending, again.Attempt);
        Assert.Equal(1L, await Count(database, root, "Preflight"));
        Assert.True(await database.ScalarAsync<bool>($"""
            SELECT i."Attempt" = 1 AND i."StartedAt" >= x."RequestedAt" AND i."EndedAt" = c."ObservedAt" AND i."EndedAt" - i."StartedAt" >= interval '350 milliseconds'
            FROM "ExecutionIntervals" i JOIN "ClassificationRecords" c ON c."OwnerId" = i."RootId" JOIN "{root.Table}" x ON x."Id" = i."RootId"
            WHERE i."RootId" = '{root.Id}' AND i."Kind" = 'Preflight'
            """));
    }

    [Fact]
    public async Task A_root_failed_by_the_classification_still_records_its_preflight_in_the_same_transaction()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var rig = Rig.Create(database, new SlowProbe(new RegimeMeasurement(), 50), new FlakyExecutor());   // nessuna impronta: runtime_unverified
        var root = await SeedPendingAsync(database, RootKind.Turn);

        var result = await rig.Classifier.ClassifyAsync(OwnerOf(root), root.Id, Ct);

        Assert.Equal(ClassificationAttempt.Failed, result.Attempt);
        Assert.Equal(1L, await Count(database, root, "Preflight"));
        Assert.Equal(0L, await Count(database, root, "Queue"));   // mai claimato: nessuna coda inventata
    }

    [Fact]
    public async Task Two_workers_measuring_the_same_root_leave_exactly_one_preflight_interval()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var rig = Rig.Create(database, new SlowProbe(Fit, 200), new FlakyExecutor());
        var root = await SeedPendingAsync(database, RootKind.Run);

        var results = await Task.WhenAll(
            rig.Classifier.ClassifyAsync(OwnerOf(root), root.Id, Ct), rig.Classifier.ClassifyAsync(OwnerOf(root), root.Id, Ct));

        Assert.Single(results, r => r.Attempt == ClassificationAttempt.Classified);
        Assert.Equal(1L, await Count(database, root, "Preflight"));
    }

    // ================================================================ Esclusività e totale autorevole

    [Fact]
    public async Task Queue_starts_when_the_preflight_ends_and_the_exclusive_intervals_never_overlap()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var executor = new FlakyExecutor();
        await using var rig = Rig.Create(database, new SlowProbe(Fit, 400), executor);
        var root = await SeedPendingAsync(database, RootKind.Turn, elapsed: 5);

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));    // classificazione
        await Task.Delay(300, Ct);                             // attesa in coda con il root già classificato
        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));    // claim (primo tentativo: fallisce transitorio, backoff)

        Assert.True(await database.ScalarAsync<bool>($"""
            SELECT q."StartedAt" = p."EndedAt" AND q."EndedAt" >= q."StartedAt" AND q."EndedAt" - q."StartedAt" >= interval '250 milliseconds'
            FROM "ExecutionIntervals" p JOIN "ExecutionIntervals" q ON q."RootId" = p."RootId"
            WHERE p."RootId" = '{root.Id}' AND p."Kind" = 'Preflight' AND q."Kind" = 'Queue' AND q."Attempt" = 1
            """));

        var overlapping = await database.ScalarAsync<long>($"""
            SELECT count(*) FROM "ExecutionIntervals" a JOIN "ExecutionIntervals" b ON a."RootId" = b."RootId" AND a."Id" < b."Id"
            WHERE a."RootId" = '{root.Id}' AND a."StartedAt" < b."EndedAt" AND b."StartedAt" < a."EndedAt"
            """);
        Assert.Equal(0L, overlapping);

        var view = await ViewAsync(database, root);
        var expectedQueueMs = await database.ScalarAsync<long>($"""
            SELECT round(extract(epoch FROM (p."StartedAt" - x."RequestedAt")) * 1000 + extract(epoch FROM (q."EndedAt" - q."StartedAt")) * 1000)::bigint
            FROM "ExecutionIntervals" p JOIN "ExecutionIntervals" q ON q."RootId" = p."RootId" JOIN "ChatTurns" x ON x."Id" = p."RootId"
            WHERE p."RootId" = '{root.Id}' AND p."Kind" = 'Preflight' AND q."Kind" = 'Queue'
            """);
        // La coda comprende l'attesa prima del preflight (t0 -> inizio misura) e quella dopo (fine preflight -> claim), mai il preflight stesso.
        Assert.InRange(view.Execution.QueueMs!.Value, expectedQueueMs - 2, expectedQueueMs + 2);
        Assert.InRange(view.Execution.PreflightMs!.Value, 350, 5_000);
        Assert.True(view.Execution.QueueMs.Value + view.Execution.PreflightMs.Value <= view.Execution.ElapsedMs);   // nessun doppio conteggio
        Assert.Equal(0, view.Execution.RecoveryMs);                                                                // nessun tentativo raggiunto per recupero: zero osservato
    }

    [Fact]
    public async Task A_retry_records_the_backoff_and_the_second_queue_starts_when_the_backoff_ends()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var executor = new FlakyExecutor();
        await using var rig = Rig.Create(database, new SlowProbe(Fit, 50), executor);
        var root = await SeedPendingAsync(database, RootKind.Turn, elapsed: 1);

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));    // classificazione
        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));    // tentativo 1: 503 -> Queued con backoff
        Assert.Equal(1L, await Count(database, root, "Backoff"));
        await Task.Delay(2_300, Ct);                           // backoff 2 s
        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));    // tentativo 2

        Assert.True(await database.ScalarAsync<bool>($"""
            SELECT b."Attempt" = 1 AND b."EndedAt" - b."StartedAt" BETWEEN interval '1900 milliseconds' AND interval '2100 milliseconds'
               AND b."StartedAt" >= q1."EndedAt" AND q2."StartedAt" = b."EndedAt" AND q2."EndedAt" >= q2."StartedAt"
            FROM "ExecutionIntervals" b
            JOIN "ExecutionIntervals" q1 ON q1."RootId" = b."RootId" AND q1."Kind" = 'Queue' AND q1."Attempt" = 1
            JOIN "ExecutionIntervals" q2 ON q2."RootId" = b."RootId" AND q2."Kind" = 'Queue' AND q2."Attempt" = 2
            WHERE b."RootId" = '{root.Id}' AND b."Kind" = 'Backoff'
            """));
        Assert.Equal(0L, await database.ScalarAsync<long>($"""
            SELECT count(*) FROM "ExecutionIntervals" a JOIN "ExecutionIntervals" b ON a."RootId" = b."RootId" AND a."Id" < b."Id"
            WHERE a."RootId" = '{root.Id}' AND a."StartedAt" < b."EndedAt" AND b."StartedAt" < a."EndedAt"
            """));

        var view = await ViewAsync(database, root);
        Assert.InRange(view.Execution.BackoffMs!.Value, 1_900, 2_100);
        Assert.NotNull(view.Execution.QueueMs);   // ogni tentativo ha la sua coda e il backoff che lo precede: il totale è completo
        Assert.True(view.Execution.QueueMs.Value + view.Execution.PreflightMs!.Value + view.Execution.BackoffMs.Value <= view.Execution.ElapsedMs);
        Assert.Equal(0, view.Execution.RecoveryMs);
    }

    [Fact]
    public async Task A_missing_queue_interval_of_any_attempt_leaves_the_queue_total_unavailable_but_not_the_others()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { ElapsedSeconds = 60, AttemptCount = 3 });
        await SeedIntervalAsync(database, root, "Preflight", 1, 1, 2);
        await SeedIntervalAsync(database, root, "Queue", 1, 2, 5);
        await SeedIntervalAsync(database, root, "Backoff", 1, 6, 8);
        await SeedIntervalAsync(database, root, "Queue", 2, 8, 9);
        // attempt 3 raggiunto per scadenza del lease (recupero): nessun backoff, nessuna coda registrata.

        var view = await ViewAsync(database, root);

        Assert.Null(view.Execution.QueueMs);      // la coda del tentativo 3 non è osservabile
        Assert.Null(view.Execution.BackoffMs);    // manca il backoff dopo il tentativo 2: il tempo non osservato è recupero (T102), non backoff
        Assert.Equal(1_000, view.Execution.PreflightMs);
        Assert.Null(view.Execution.RecoveryMs);
    }

    [Fact]
    public async Task A_root_that_never_retried_has_no_backoff_and_an_unclaimed_root_has_no_queue_total_yet()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var first = await DocumentRegimeRoots.SeedAsync(database, RootKind.Run, new RootSpec { ElapsedSeconds = 30, AttemptCount = 1 });
        await SeedIntervalAsync(database, first, "Preflight", 1, 4, 5);
        await SeedIntervalAsync(database, first, "Queue", 1, 5, 12);
        var waiting = await DocumentRegimeRoots.SeedAsync(database, RootKind.Run, new RootSpec { ElapsedSeconds = 30, AttemptCount = 0, Status = "Queued", LeaseOwner = null, Fence = 0 });
        await SeedIntervalAsync(database, waiting, "Preflight", 1, 4, 5);

        var done = await ViewAsync(database, first);
        var pending = await ViewAsync(database, waiting);

        Assert.Equal(0, done.Execution.BackoffMs);              // un solo tentativo: nessun retry
        Assert.Equal(4_000 + 7_000, done.Execution.QueueMs);    // t0 -> inizio preflight + fine preflight -> claim
        Assert.Null(pending.Execution.QueueMs);                 // coda ancora in corso: nessun totale parziale presentato come intero
    }

    // ================================================================ Backoff al rilascio

    private static ModelFailure Transient => new(true, "model_unreachable", null);

    /// <summary>Rilascio dello store seguito dalla registrazione del backoff che il dispatcher fa dopo il commit (stessa chiamata, stessi valori).</summary>
    private static async Task<TurnRelease> ReleaseAsync(ParsingDatabase database, SeededRoot root, ModelFailure failure, int attempt)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = new ExecutionLeaseStore(scope.ServiceProvider.GetRequiredService<AppDbContext>(), Options.Create(new ExecutionOptions()));
        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);
        var released = root.Kind == RootKind.Turn
            ? await store.ReleaseAfterFailureAsync(new ChatTurnClaim(root.Id, root.Conversation, Guid.NewGuid(), 1, "worker-a", 3, attempt, deadline, deadline), failure, "model_error", "x", Ct)
            : await store.ReleaseAfterFailureAsync(new AnalysisRunClaim(root.Id, "worker-a", 3, attempt), failure, "model_error", "x", Ct);
        if (released is { Outcome: TurnTransition.Requeued, NextAttemptAt: { } next, Delay: { } delay })
        {
            await ExecutionIntervalStore.RecordBackoffAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), OwnerOf(root), root.Id, attempt, next, delay, Ct);
        }

        return released;
    }

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task A_transient_release_of_a_classified_root_yields_a_backoff_from_the_release_to_the_next_attempt(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { ElapsedSeconds = 10, AttemptCount = 1 });

        var released = await ReleaseAsync(database, root, Transient, attempt: 1);

        Assert.Equal(TurnTransition.Requeued, released.Outcome);
        Assert.Equal(1L, await Count(database, root, "Backoff"));
        Assert.True(await database.ScalarAsync<bool>($"""
            SELECT i."Attempt" = 1 AND i."EndedAt" = x."NextAttemptAt" AND i."EndedAt" - i."StartedAt" = interval '2 seconds'
            FROM "ExecutionIntervals" i JOIN "{root.Table}" x ON x."Id" = i."RootId" WHERE i."RootId" = '{root.Id}' AND i."Kind" = 'Backoff'
            """));
    }

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task No_backoff_is_recorded_for_legacy_roots_permanent_failures_or_exhausted_attempts(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var legacy = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { Legacy = true, AttemptCount = 1 });
        var permanent = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { AttemptCount = 1 });
        var exhausted = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { AttemptCount = 3 });

        Assert.Equal(TurnTransition.Requeued, (await ReleaseAsync(database, legacy, Transient, 1)).Outcome);
        Assert.Equal(TurnTransition.Failed, (await ReleaseAsync(database, permanent, new ModelFailure(false, "model_unauthorized", null), 1)).Outcome);
        Assert.Equal(TurnTransition.Failed, (await ReleaseAsync(database, exhausted, Transient, 3)).Outcome);

        Assert.Equal(0L, await database.ScalarAsync<long>("""SELECT count(*) FROM "ExecutionIntervals" """));
    }

    // ================================================================ Recupero del lease (T102)

    private static string LeaseExpiredSql => "clock_timestamp() - interval '3 seconds'";

    private sealed class NoopExecutor : IChatTurnExecutor
    {
        public Task ExecuteAsync(ChatTurnClaim claim, CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task<WorkClaim?> ClaimAsync(ParsingDatabase database)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = new ExecutionLeaseStore(scope.ServiceProvider.GetRequiredService<AppDbContext>(), Options.Create(new ExecutionOptions()));
        return await store.ClaimNextWorkAsync("worker-b", turns: true, runs: true, documents: true, Ct);
    }

    private static LeaseRecovery? RecoveryOf(WorkClaim claim) => claim.Turn?.Recovery ?? claim.Run?.Recovery ?? claim.Document?.Recovery;

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task Reclaiming_an_expired_lease_reports_the_recovery_from_the_lease_expiry_to_the_claim_and_a_queued_claim_reports_none(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var expired = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { ElapsedSeconds = 10, AttemptCount = 1, LeaseUntilSql = LeaseExpiredSql });
        var oldLease = new DateTimeOffset(DateTime.SpecifyKind(await database.ScalarAsync<DateTime>($"""SELECT "LeaseUntil" FROM "{expired.Table}" WHERE "Id" = '{expired.Id}'"""), DateTimeKind.Utc));

        var recovery = RecoveryOf((await ClaimAsync(database))!);

        Assert.NotNull(recovery);
        Assert.Equal(oldLease, recovery.Value.From);
        Assert.True(recovery.Value.To > recovery.Value.From);

        await using var other = await ParsingDatabase.CreateAsync();
        await DocumentRegimeRoots.SeedAsync(other, kind, new RootSpec { ElapsedSeconds = 10, AttemptCount = 0, Status = "Queued", LeaseOwner = null, Fence = 0 });
        Assert.Null(RecoveryOf((await ClaimAsync(other))!));
    }

    [Fact]
    public async Task The_dispatcher_records_exactly_one_recovery_interval_for_the_crashed_attempt_without_overlapping_the_other_intervals()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var rig = Rig.Create(database, new SlowProbe(Fit, 10), new NoopExecutor());
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { ElapsedSeconds = 20, AttemptCount = 1, LeaseUntilSql = LeaseExpiredSql });
        await SeedIntervalAsync(database, root, "Preflight", 1, 1, 2);
        await SeedIntervalAsync(database, root, "Queue", 1, 2, 3);

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));

        Assert.Equal(1L, await Count(database, root, "Recovery"));
        Assert.True(await database.ScalarAsync<bool>($"""
            SELECT i."Attempt" = 1 AND i."EndedAt" > i."StartedAt" AND i."StartedAt" > x."RequestedAt" + interval '3 seconds'
            FROM "ExecutionIntervals" i JOIN "ChatTurns" x ON x."Id" = i."RootId" WHERE i."RootId" = '{root.Id}' AND i."Kind" = 'Recovery'
            """));
        Assert.Equal(0L, await database.ScalarAsync<long>($"""
            SELECT count(*) FROM "ExecutionIntervals" a JOIN "ExecutionIntervals" b ON a."RootId" = b."RootId" AND a."Id" < b."Id"
            WHERE a."RootId" = '{root.Id}' AND a."StartedAt" < b."EndedAt" AND b."StartedAt" < a."EndedAt"
            """));
        Assert.Equal(1L, await Count(database, root, "Queue"));   // nessuna coda inventata per il tentativo raggiunto per recupero

        var view = await ViewAsync(database, root);
        Assert.True(view.Execution.RecoveryMs > 0);
        Assert.NotNull(view.Execution.QueueMs);     // il tentativo recuperato non ha coda: l'attesa è recupero
        Assert.NotNull(view.Execution.BackoffMs);
    }

    [Fact]
    public async Task A_failure_writing_the_recovery_never_blocks_the_reclaim_and_is_logged_without_the_exception()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var logger = new CapturingLogger();
        await using var rig = Rig.Create(database, new SlowProbe(Fit, 10), new NoopExecutor(), logger);
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { ElapsedSeconds = 20, AttemptCount = 1, LeaseUntilSql = LeaseExpiredSql });
        await database.ExecuteAsync("""DROP TABLE "ExecutionIntervals" """);

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));

        Assert.Equal(2, await database.ScalarAsync<int>($"""SELECT "AttemptCount" FROM "ChatTurns" WHERE "Id" = '{root.Id}'"""));
        Assert.Contains(logger.Entries, e => e.Message.Contains("Intervallo di recupero non registrato", StringComparison.Ordinal));
        Assert.All(logger.Entries, e => Assert.Null(e.Exception));
    }

    [Fact]
    public async Task Recovery_time_is_the_union_of_recovery_intervals_and_explains_the_attempts_without_backoff_or_queue()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { ElapsedSeconds = 60, AttemptCount = 3 });
        await SeedIntervalAsync(database, root, "Preflight", 1, 1, 2);
        await SeedIntervalAsync(database, root, "Queue", 1, 2, 5);
        await SeedIntervalAsync(database, root, "Backoff", 1, 6, 8);
        await SeedIntervalAsync(database, root, "Queue", 2, 8, 9);
        await SeedIntervalAsync(database, root, "Recovery", 2, 20, 27);   // attempt 3 raggiunto per scadenza del lease: nessun backoff, nessuna coda

        var view = await ViewAsync(database, root);

        Assert.Equal(7_000, view.Execution.RecoveryMs);
        Assert.Equal(2_000, view.Execution.BackoffMs);
        Assert.Equal(1_000 + 3_000 + 1_000, view.Execution.QueueMs);
        Assert.True(view.Execution.QueueMs!.Value + view.Execution.PreflightMs!.Value + view.Execution.BackoffMs!.Value + view.Execution.RecoveryMs!.Value <= view.Execution.ElapsedMs);
    }

    [Fact]
    public async Task Without_a_recorded_recovery_for_an_attempt_reached_without_backoff_the_recovery_stays_unavailable_never_zero()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { ElapsedSeconds = 60, AttemptCount = 2 });
        await SeedIntervalAsync(database, root, "Preflight", 1, 1, 2);
        await SeedIntervalAsync(database, root, "Queue", 1, 2, 5);

        var view = await ViewAsync(database, root);

        Assert.Null(view.Execution.RecoveryMs);
        Assert.Null(view.Execution.BackoffMs);
        Assert.Null(view.Execution.QueueMs);
    }

    [Fact]
    public async Task Legacy_roots_have_no_recovery_intervals()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var legacy = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { Legacy = true, AttemptCount = 1, LeaseUntilSql = LeaseExpiredSql });
        var recovery = RecoveryOf((await ClaimAsync(database))!)!.Value;

        await using (var db = database.NewContext())
        {
            await ExecutionIntervalStore.RecordRecoveryAsync(db, ClassificationOwnerKind.ChatTurn, legacy.Id, 1, recovery, Ct);
        }

        Assert.Equal(0L, await Count(database, legacy, "Recovery"));
    }

    // ================================================================ Log

    [Fact]
    public async Task Logs_of_failed_executions_and_failed_interval_writes_never_carry_exception_text_prompts_documents_or_secrets()
    {
        const string secret = "http://dgx.interno:8080/v1 Bearer sk-segretissimo prompt: DOCUMENTO RISERVATO";
        await using var database = await ParsingDatabase.CreateAsync();
        var logger = new CapturingLogger();
        var executor = new LeakyExecutor(secret);
        await using var rig = Rig.Create(database, new SlowProbe(Fit, 10), executor, logger);
        var root = await SeedPendingAsync(database, RootKind.Turn);
        await rig.Dispatcher.RunOnceAsync(Ct);                                       // classificazione
        await database.ExecuteAsync("""DROP TABLE "ExecutionIntervals" """);         // la scrittura dell'intervallo di coda fallirà

        await rig.Dispatcher.RunOnceAsync(Ct);                                       // claim: coda non registrabile + esecutore che solleva il segreto

        Assert.NotEmpty(logger.Entries);
        foreach (var (_, message, exception) in logger.Entries)
        {
            Assert.Null(exception);                                                  // mai l'eccezione intera (messaggio e stack)
            foreach (var forbidden in new[] { "sk-segretissimo", "Bearer", "RISERVATO", "prompt:", "dgx.interno", "ExecutionIntervals" })
            {
                Assert.DoesNotContain(forbidden, message);
            }
        }

        Assert.Contains(logger.Entries, e => e.Message.Contains("Intervallo di coda non registrato", StringComparison.Ordinal));
        Assert.NotEqual(Guid.Empty, root.Id);
    }

    [Fact]
    public async Task A_failure_writing_the_backoff_never_blocks_the_retry_and_is_logged_without_the_exception()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var logger = new CapturingLogger();
        await using var rig = Rig.Create(database, new SlowProbe(Fit, 10), new FlakyExecutor(), logger);
        var root = await SeedPendingAsync(database, RootKind.Turn);
        await rig.Dispatcher.RunOnceAsync(Ct);                                       // classificazione
        await database.ExecuteAsync("""DROP TABLE "ExecutionIntervals" """);         // coda e backoff non registrabili

        await rig.Dispatcher.RunOnceAsync(Ct);                                       // claim: 503 transitorio

        Assert.Equal("Queued", await database.ScalarAsync<string>($"""SELECT "Status" FROM "ChatTurns" WHERE "Id" = '{root.Id}'"""));
        Assert.True(await database.ScalarAsync<bool>($"""SELECT "NextAttemptAt" IS NOT NULL FROM "ChatTurns" WHERE "Id" = '{root.Id}'"""));   // il retry è stato programmato
        Assert.Contains(logger.Entries, e => e.Message.Contains("Intervallo di backoff non registrato", StringComparison.Ordinal));
        Assert.All(logger.Entries, e => Assert.Null(e.Exception));
    }

    private sealed class LeakyExecutor(string secret) : IChatTurnExecutor
    {
        public Task ExecuteAsync(ChatTurnClaim claim, CancellationToken ct) => throw new InvalidOperationException(secret);
    }
}
