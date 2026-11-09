using System.Net;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T050 (PG + esecutori finti) — le unità di estrazione documentale (DocumentJob ExtractUnit) entrano nel dispatcher e nel lease store
/// ESISTENTI: un solo slot AI globale condiviso con chat e run, rilascio dopo ogni unità, chat/run eleggibili prima della prossima unità
/// (nessuna preemption), CPU del parsing in una corsia separata, stesso advisory lock/owner/fence/heartbeat, FIFO e cutoff della 001
/// invariati. Nessuna connessione browser né HTTP: il dispatcher è un servizio di sfondo. Il routing dei workflow formali è T123.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentUnitSchedulingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Hash => new('9', 64);

    // ------------------------------------------------------------------ semina

    private sealed record Prep(SeededParse Seed, Guid[] Units);

    /// <summary>Preparazione Extracting (parse concluso) con <paramref name="units"/> unità Queued, la più vecchia per prima.</summary>
    private static async Task<Prep> SeedPreparationAsync(
        ParsingDatabase db, int units, int ageSeconds = 600, int maxAttempts = 3, string status = "Extracting", (Guid User, Guid Project)? owner = null)
    {
        var seed = await db.SeedParseAsync(owner: owner);
        await db.ExecuteAsync($"""
            UPDATE "DocumentJobs" SET "Status" = 'Completed', "ResultHash" = '{Hash}', "CompletedAt" = now() WHERE "Id" = '{seed.Job}';
            UPDATE "DocumentPreparations" SET "Status" = '{status}', "ErrorCode" = CASE WHEN '{status}' = 'Failed' THEN 'seed' END, "RequestedAt" = now() - interval '{ageSeconds} seconds' WHERE "Id" = '{seed.Preparation}';
            """);
        var ids = new List<Guid>();
        await using var context = db.NewContext();
        for (var i = 0; i < units; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            context.DocumentJobs.Add(new DocumentJob
            {
                Id = id, ProjectId = seed.Project, PreparationId = seed.Preparation, Kind = DocumentJobKind.ExtractUnit, UnitId = Guid.NewGuid(), Status = DocumentJobStatus.Queued,
                RequestedBy = seed.User, RequestedAt = DateTimeOffset.UtcNow.AddSeconds(-ageSeconds + i), DeadlineAt = DateTimeOffset.UtcNow.AddHours(1), MaxAttempts = maxAttempts,
            });
        }

        await context.SaveChangesAsync(Ct);
        return new(seed, [.. ids]);
    }

    private static Task<string> Status(ParsingDatabase db, Guid job) => db.ScalarAsync<string>($"""SELECT "Status" FROM "DocumentJobs" WHERE "Id" = '{job}'""");

    private static Task<string> Error(ParsingDatabase db, Guid job) => db.ScalarAsync<string>($"""SELECT COALESCE("ErrorCode", '') FROM "DocumentJobs" WHERE "Id" = '{job}'""");

    private static Task<string> PrepStatus(ParsingDatabase db, Guid preparation) => db.ScalarAsync<string>($"""SELECT "Status" FROM "DocumentPreparations" WHERE "Id" = '{preparation}'""");

    private static Task CompleteUnit(ParsingDatabase db, Guid job) =>
        db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "Status" = 'Completed', "ResultHash" = '{Hash}', "CompletedAt" = now(), "LeaseOwner" = NULL, "LeaseUntil" = NULL WHERE "Id" = '{job}'""");

    private static async Task<SeededRoot> QueuedTurn(ParsingDatabase db) =>
        await DocumentRegimeRoots.SeedAsync(db, RootKind.Turn, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, Legacy = true });

    private static ExecutionLeaseStore Leases(AppDbContext context, ExecutionOptions? options = null) => new(context, Options.Create(options ?? new ExecutionOptions()));

    private static Task<WorkClaim?> Claim(ExecutionLeaseStore leases, string owner = "w1", bool units = true) =>
        leases.ClaimNextWorkAsync(owner, turns: true, runs: true, documents: true, extractions: true, units: units, Ct);

    // ------------------------------------------------------------------ slot, FIFO e rilascio dopo ogni unità

    [Fact]
    public async Task Units_are_claimed_one_at_a_time_oldest_first_and_the_slot_is_free_again_after_each_unit()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var older = await SeedPreparationAsync(db, 2, ageSeconds: 900);
        var newer = await SeedPreparationAsync(db, 1, ageSeconds: 300);
        await using var context = db.NewContext();
        var leases = Leases(context);

        var first = (await Claim(leases))!.Unit!;
        Assert.Equal(older.Units[0], first.JobId);
        Assert.Equal(older.Seed.Preparation, first.PreparationId);
        Assert.Equal(older.Seed.Project, first.ProjectId);
        Assert.Equal(1, first.AttemptCount);
        Assert.Equal(1L, first.FencingToken);
        Assert.Equal("Running", await Status(db, first.JobId));
        Assert.Null(await Claim(leases, "w2"));   // lo slot è occupato: nessuna seconda esecuzione AI

        await CompleteUnit(db, first.JobId);
        var second = (await Claim(leases))!.Unit!;
        Assert.Equal(older.Units[1], second.JobId);   // stessa preparazione, ordine di richiesta

        await CompleteUnit(db, second.JobId);
        Assert.Equal(newer.Units[0], (await Claim(leases))!.Unit!.JobId);   // poi la preparazione più recente
    }

    [Fact]
    public async Task A_running_unit_holds_the_global_slot_also_against_chat_and_runs_and_vice_versa()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 1);
        var turn = await QueuedTurn(db);
        await using var context = db.NewContext();
        var leases = Leases(context);
        await db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "Status" = 'Running', "LeaseOwner" = 'other', "LeaseUntil" = now() + interval '1 minute', "FencingToken" = 1, "AttemptCount" = 1 WHERE "Id" = '{prep.Units[0]}'""");

        Assert.Null(await Claim(leases));   // né la chat né altro mentre l'unità è in lavorazione (anche di un'altra istanza)

        await CompleteUnit(db, prep.Units[0]);
        Assert.Equal(turn.Id, (await Claim(leases))!.Turn!.TurnId);

        // E all'inverso: un turno in lavorazione tiene lo slot contro le unità.
        var more = await SeedPreparationAsync(db, 1);
        Assert.Null(await Claim(leases, "w2"));
        Assert.Equal("Queued", await Status(db, more.Units[0]));
    }

    [Fact]
    public async Task Chat_and_runs_that_are_eligible_go_before_the_next_unit_even_when_the_unit_is_older_and_no_running_call_is_preempted()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 3, ageSeconds: 3000);
        await using var context = db.NewContext();
        var leases = Leases(context);

        var unit = (await Claim(leases))!.Unit!;   // nulla di più prioritario: parte la prima unità
        Assert.Equal(prep.Units[0], unit.JobId);
        var run = await DocumentRegimeRoots.SeedAsync(db, RootKind.Run, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, Legacy = true });   // arriva mentre l'unità gira
        Assert.Null(await Claim(leases, "w2"));   // la run in coda NON preempta la chiamata in corso

        await CompleteUnit(db, unit.JobId);
        var next = await Claim(leases);
        Assert.Equal(run.Id, next!.Run!.RunId);   // dopo l'unità il run eleggibile precede l'unità successiva, più vecchia di lui
        Assert.Null(next.Unit);

        await db.ExecuteAsync($"""UPDATE "ProcessingRuns" SET "Status" = 'Failed', "ErrorCode" = 'x', "CompletedAt" = now(), "LeaseOwner" = NULL, "LeaseUntil" = NULL WHERE "Id" = '{run.Id}'""");
        Assert.Equal(prep.Units[1], (await Claim(leases))!.Unit!.JobId);
    }

    [Fact]
    public async Task Instances_without_a_unit_executor_neither_claim_nor_touch_units_while_the_ones_with_it_do()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 1, ageSeconds: 3000);
        await using var context = db.NewContext();
        var leases = Leases(context);

        // Senza altro lavoro l'unità parte; il flag units=false (istanze senza esecutore documentale) non la prende né la sposta.
        Assert.Null(await Claim(leases, units: false));
        Assert.Equal("Queued", await Status(db, prep.Units[0]));
        Assert.Equal(0, await db.ScalarAsync<int>($"""SELECT "AttemptCount" FROM "DocumentJobs" WHERE "Id" = '{prep.Units[0]}'"""));
        Assert.Equal(prep.Units[0], (await Claim(leases))!.Unit!.JobId);
    }

    [Fact]
    public async Task A_unit_does_not_start_when_a_turn_is_about_to_retry_within_the_backoff_hold()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 1);
        var turn = await QueuedTurn(db);
        await db.ExecuteAsync($"""UPDATE "ChatTurns" SET "NextAttemptAt" = now() + interval '5 seconds', "AttemptCount" = 1 WHERE "Id" = '{turn.Id}'""");
        await using var context = db.NewContext();

        Assert.Null(await Claim(Leases(context)));   // il modello non va occupato un attimo prima del retry di una chat
        Assert.Equal("Queued", await Status(db, prep.Units[0]));
    }

    // ------------------------------------------------------------------ corsia CPU del parsing

    [Fact]
    public async Task Parsing_runs_in_its_own_lane_it_neither_takes_nor_waits_for_the_ai_slot()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 1);
        var parsing = await db.SeedParseAsync();   // Parse Queued di un'altra preparazione
        await using var context = db.NewContext();
        var leases = Leases(context);
        var jobs = new DocumentJobStore(db.NewContext(), Options.Create(new ExecutionOptions()));

        // Un Parse in lavorazione non conta nel limite AI: l'unità parte.
        await db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "Status" = 'Running', "LeaseOwner" = 'parser', "LeaseUntil" = now() + interval '1 minute', "FencingToken" = 1, "AttemptCount" = 1 WHERE "Id" = '{parsing.Job}'""");
        var unit = (await Claim(leases))!.Unit!;
        Assert.Equal(prep.Units[0], unit.JobId);

        // Un'unità in lavorazione (slot AI occupato) non impedisce di prendere in carico un altro Parse.
        var another = await db.SeedParseAsync();
        var parse = await jobs.ClaimNextParseAsync("parser-2", Ct);
        Assert.Equal(another.Job, parse!.JobId);
        Assert.Equal("Running", await Status(db, unit.JobId));
    }

    // ------------------------------------------------------------------ idoneità, scadenze e fallimento

    [Fact]
    public async Task Only_units_of_a_preparation_in_extraction_with_live_deadlines_and_attempts_left_are_eligible()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var parsing = await SeedPreparationAsync(db, 1, status: "Parsing");
        var failed = await SeedPreparationAsync(db, 1, status: "Failed");
        await using var context = db.NewContext();

        Assert.Null(await Claim(Leases(context)));

        Assert.Equal("Queued", await Status(db, parsing.Units[0]));
        Assert.Equal("Queued", await Status(db, failed.Units[0]));
    }

    [Fact]
    public async Task An_expired_parent_closes_the_unit_and_fails_the_preparation_at_claim_time_and_a_late_claim_never_runs()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 2);
        await db.ExecuteAsync($"""UPDATE "DocumentPreparations" SET "EffectiveDeadlineAt" = clock_timestamp() - interval '1 millisecond' WHERE "Id" = '{prep.Seed.Preparation}'""");
        await using var context = db.NewContext();

        Assert.Null(await Claim(Leases(context)));

        Assert.Equal("Failed", await PrepStatus(db, prep.Seed.Preparation));
        foreach (var unit in prep.Units)
        {
            Assert.Equal("Failed", await Status(db, unit));
        }

        Assert.Equal("parent_deadline_exceeded", await Error(db, prep.Units[0]));
    }

    [Fact]
    public async Task Exhausted_attempts_and_the_local_deadline_fail_the_unit_with_their_own_code_and_fail_the_preparation_and_its_siblings()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 3, maxAttempts: 2);
        await db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "AttemptCount" = 2 WHERE "Id" = '{prep.Units[0]}'""");
        await using var context = db.NewContext();

        Assert.Null(await Claim(Leases(context)));

        Assert.Equal("attempts_exhausted", await Error(db, prep.Units[0]));
        Assert.Equal("Failed", await PrepStatus(db, prep.Seed.Preparation));
        Assert.Equal("parent_failed", await Error(db, prep.Units[1]));   // le unità sorelle non restano in coda per una preparazione già fallita
        Assert.Equal("Failed", await Status(db, prep.Units[2]));

        var late = await SeedPreparationAsync(db, 1);
        await db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "DeadlineAt" = now() - interval '1 second' WHERE "Id" = '{late.Units[0]}'""");
        Assert.Null(await Claim(Leases(context)));
        Assert.Equal("deadline_exceeded", await Error(db, late.Units[0]));
        Assert.Equal("Failed", await PrepStatus(db, late.Seed.Preparation));
    }

    [Fact]
    public async Task Release_requeues_a_transient_failure_with_backoff_and_fails_a_permanent_one_with_the_preparation_and_the_siblings()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 3);
        await using var context = db.NewContext();
        var leases = Leases(context);

        var claim = (await Claim(leases))!.Unit!;
        var transient = await leases.ReleaseAfterFailureAsync(claim, new ModelFailure(true, "model_unreachable", null), "x", "x", Ct);
        Assert.Equal(TurnTransition.Requeued, transient.Outcome);
        Assert.InRange((transient.NextAttemptAt!.Value - DateTimeOffset.UtcNow).TotalSeconds, 0.5, 2.5);   // backoff 2 s
        Assert.Equal("Queued", await Status(db, claim.JobId));
        Assert.Equal("Extracting", await PrepStatus(db, prep.Seed.Preparation));
        Assert.Equal(TurnTransition.Rejected, (await leases.ReleaseAfterFailureAsync(claim, new ModelFailure(true, "model_unreachable", null), "x", "x", Ct)).Outcome);   // già rilasciato: il vecchio possessore non scrive

        // Il job in backoff non è eleggibile finché NextAttemptAt non è passato; le sorelle sì.
        var other = (await Claim(leases))!.Unit!;
        Assert.Equal(prep.Units[1], other.JobId);
        await CompleteUnit(db, other.JobId);
        await db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "NextAttemptAt" = now() - interval '1 second' WHERE "Id" = '{claim.JobId}'""");
        var retry = (await Claim(leases))!.Unit!;
        Assert.Equal(claim.JobId, retry.JobId);
        Assert.Equal(2, retry.AttemptCount);
        Assert.Equal(2L, retry.FencingToken);

        var permanent = await leases.ReleaseAfterFailureAsync(retry, new ModelFailure(false, "work_output_invalid", null), "work_output_invalid", "Output non valido.", Ct);
        Assert.Equal((TurnTransition.Failed, "work_output_invalid"), (permanent.Outcome, permanent.ErrorCode));
        Assert.Equal("Failed", await PrepStatus(db, prep.Seed.Preparation));
        Assert.Equal("work_output_invalid", await Error(db, retry.JobId));
        Assert.Equal("parent_failed", await Error(db, prep.Units[2]));
        Assert.Equal("Completed", await Status(db, other.JobId));   // un'unità già completata non si tocca
    }

    [Theory]
    [InlineData("document_incomplete", "Incomplete")]
    [InlineData("document_limit_exceeded", "Incomplete")]
    [InlineData("coverage_incomplete", "Incomplete")]
    [InlineData("runtime_unverified", "Failed")]
    [InlineData("work_output_invalid", "Failed")]
    public async Task A_permanent_limit_or_incomplete_error_leaves_the_preparation_Incomplete_and_any_other_one_Failed(string code, string expected)
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 1);
        await using var context = db.NewContext();
        var leases = Leases(context);
        var claim = (await Claim(leases))!.Unit!;

        await leases.ReleaseAfterFailureAsync(claim, new ModelFailure(false, code, null), code, "Errore.", Ct);

        Assert.Equal(expected, await PrepStatus(db, prep.Seed.Preparation));
        Assert.Equal(code, await db.ScalarAsync<string>($"""SELECT "ErrorCode" FROM "DocumentPreparations" WHERE "Id" = '{prep.Seed.Preparation}'"""));
    }

    [Fact]
    public async Task Renew_extends_the_lease_only_for_the_current_owner_and_fence_and_never_moves_a_deadline()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 1);
        await using var context = db.NewContext();
        var leases = Leases(context);
        var claim = (await Claim(leases))!.Unit!;
        var deadlines = $"""SELECT concat_ws('|', j."DeadlineAt", p."EffectiveDeadlineAt", p."HardDeadlineAt") FROM "DocumentJobs" j JOIN "DocumentPreparations" p ON p."Id" = j."PreparationId" WHERE j."Id" = '{claim.JobId}'""";
        var before = await db.ScalarAsync<string>(deadlines);
        var lease = await db.ScalarAsync<DateTime>($"""SELECT "LeaseUntil" FROM "DocumentJobs" WHERE "Id" = '{claim.JobId}'""");
        await Task.Delay(50, Ct);

        Assert.True(await leases.RenewAsync(claim, Ct));
        Assert.True(await db.ScalarAsync<DateTime>($"""SELECT "LeaseUntil" FROM "DocumentJobs" WHERE "Id" = '{claim.JobId}'""") > lease);
        Assert.Equal(before, await db.ScalarAsync<string>(deadlines));
        Assert.False(await leases.RenewAsync(claim with { FencingToken = claim.FencingToken + 1 }, Ct));
        Assert.False(await leases.RenewAsync(claim with { LeaseOwner = "altro" }, Ct));

        await db.ExecuteAsync($"""UPDATE "DocumentPreparations" SET "EffectiveDeadlineAt" = clock_timestamp() - interval '1 millisecond' WHERE "Id" = '{prep.Seed.Preparation}'""");
        Assert.False(await leases.RenewAsync(claim, Ct));   // padre scaduto: nessun prolungamento
    }

    // ------------------------------------------------------------------ dispatcher: slot, priorità, istanze, heartbeat

    private sealed class Recorder
    {
        public List<string> Order { get; } = [];
        private int _active;
        private int _max;
        public int Max => Volatile.Read(ref _max);

        public IDisposable Enter()
        {
            var now = Interlocked.Increment(ref _active);
            int seen;
            while ((seen = Volatile.Read(ref _max)) < now && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            return new Exit(this);
        }

        private sealed class Exit(Recorder owner) : IDisposable
        {
            public void Dispose() => Interlocked.Decrement(ref owner._active);
        }
    }

    private sealed class FakeTurns(Recorder recorder) : IChatTurnExecutor
    {
        public Task ExecuteAsync(ChatTurnClaim claim, CancellationToken ct)
        {
            using var _ = recorder.Enter();
            lock (recorder.Order)
            {
                recorder.Order.Add("turn");
            }

            return Task.CompletedTask;   // un esecutore che torna senza commit: il dispatcher chiude il turno Failed e libera lo slot
        }
    }

    private sealed class FakeUnits(ParsingDatabase db, Recorder recorder) : IDocumentUnitExecutor
    {
        public Func<DocumentUnitClaim, CancellationToken, Task>? Work { get; set; }
        public List<DocumentUnitClaim> Claims { get; } = [];
        public Exception? Fail { get; set; }

        public async Task ExecuteAsync(DocumentUnitClaim claim, CancellationToken ct)
        {
            using var _ = recorder.Enter();
            lock (recorder.Order)
            {
                recorder.Order.Add("unit:" + claim.JobId.ToString("N")[..6]);
                Claims.Add(claim);
            }

            if (Work is not null)
            {
                await Work(claim, ct);
            }

            if (Fail is not null)
            {
                throw Fail;
            }

            await db.ExecuteAsync($"""
                UPDATE "DocumentJobs" SET "Status" = 'Completed', "ResultHash" = '{Hash}', "CompletedAt" = now(), "LeaseOwner" = NULL, "LeaseUntil" = NULL
                WHERE "Id" = '{claim.JobId}' AND "Status" = 'Running' AND "FencingToken" = {claim.FencingToken}
                """);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        public Rig(ParsingDatabase database, Recorder recorder, ExecutionOptions? options = null, bool units = true, bool turns = true)
        {
            Database = database;
            Options = options ?? new ExecutionOptions();
            Units = new FakeUnits(database, recorder);
            _services = new ServiceCollection()
                .AddLogging()
                .AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString))
                .AddSingleton<IOptions<ExecutionOptions>>(Microsoft.Extensions.Options.Options.Create(Options))
                .AddScoped<ExecutionLeaseStore>()
                .BuildServiceProvider();
            Dispatcher = new AiDispatchService(
                _services.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(Options), NullLogger<AiDispatchService>.Instance,
                executor: turns ? new FakeTurns(recorder) : null, unitExecutor: units ? Units : null);
        }

        public ParsingDatabase Database { get; }
        public ExecutionOptions Options { get; }
        public FakeUnits Units { get; }
        public AiDispatchService Dispatcher { get; }

        public async ValueTask DisposeAsync()
        {
            await Dispatcher.StopAsync(CancellationToken.None);
            await _services.DisposeAsync();
        }
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail($"Condizione non raggiunta entro {timeout.TotalSeconds:0} s: {because}");
    }

    [Fact]
    public async Task A_preparation_does_not_monopolize_the_slot_a_chat_that_arrives_during_a_unit_runs_before_the_next_unit()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 3);
        var recorder = new Recorder();
        await using var rig = new Rig(db, recorder);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Units.Work = async (claim, ct) =>
        {
            if (claim.JobId == prep.Units[0])
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        };

        await rig.Dispatcher.StartAsync(Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        var turn = await QueuedTurn(db);   // più recente delle unità, arriva mentre la prima gira
        Assert.Equal("Queued", await db.ScalarAsync<string>($"""SELECT "Status" FROM "ChatTurns" WHERE "Id" = '{turn.Id}'"""));   // nessuna preemption
        release.SetResult();

        await WaitAsync(async () => await db.ScalarAsync<long>($"""SELECT count(*) FROM "DocumentJobs" WHERE "PreparationId" = '{prep.Seed.Preparation}' AND "Kind" = 'ExtractUnit' AND "Status" = 'Completed'""") == 3,
            TimeSpan.FromSeconds(30), "tutte le unità devono concludersi");

        var order = recorder.Order.ToList();
        Assert.Equal("unit:" + prep.Units[0].ToString("N")[..6], order[0]);
        Assert.Equal("turn", order[1]);   // la chat passa prima della seconda unità
        Assert.Equal(2, order.Count(o => o.StartsWith("unit:") && o != order[0]));
        Assert.Equal(1, recorder.Max);
    }

    [Fact]
    public async Task Two_dispatcher_instances_never_run_inference_together_and_every_unit_runs_exactly_once()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 14);
        var recorder = new Recorder();
        await using var a = new Rig(db, recorder, turns: false);
        await using var b = new Rig(db, recorder, turns: false);
        foreach (var rig in new[] { a, b })
        {
            rig.Units.Work = async (_, ct) => await Task.Delay(60, ct);
        }

        await Task.WhenAll(a.Dispatcher.StartAsync(Ct), b.Dispatcher.StartAsync(Ct));
        await WaitAsync(async () => await db.ScalarAsync<long>($"""SELECT count(*) FROM "DocumentJobs" WHERE "PreparationId" = '{prep.Seed.Preparation}' AND "Kind" = 'ExtractUnit' AND "Status" = 'Completed'""") == 14,
            TimeSpan.FromSeconds(90), "tutte le unità devono concludersi");

        Assert.Equal(1, recorder.Max);   // mai due esecuzioni AI contemporanee fra le due istanze
        Assert.Equal(14, recorder.Order.Count);
        Assert.Equal(14, recorder.Order.Distinct().Count());
        Assert.Equal(14L, await db.ScalarAsync<long>($"""SELECT count(*) FROM "DocumentJobs" WHERE "PreparationId" = '{prep.Seed.Preparation}' AND "AttemptCount" = 1 AND "FencingToken" = 1"""));
    }

    [Fact]
    public async Task Simultaneous_claims_from_two_stores_yield_a_single_running_unit()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 4);
        for (var round = 0; round < 4; round++)
        {
            await using var c1 = db.NewContext();
            await using var c2 = db.NewContext();
            var barrier = new Barrier(2);
            Task<WorkClaim?> Racer(AppDbContext context, string owner) => Task.Run(() =>
            {
                barrier.SignalAndWait(Ct);
                return Claim(Leases(context), owner);
            }, Ct);

            var claims = await Task.WhenAll(Racer(c1, "a"), Racer(c2, "b"));

            Assert.Single(claims, c => c is not null);
            Assert.Equal(1L, await db.ScalarAsync<long>($"""SELECT count(*) FROM "DocumentJobs" WHERE "PreparationId" = '{prep.Seed.Preparation}' AND "Status" = 'Running' AND "LeaseUntil" > now()"""));
            await CompleteUnit(db, claims.Single(c => c is not null)!.Unit!.JobId);
        }
    }

    [Fact]
    public async Task The_dispatcher_renews_the_unit_lease_and_cancels_the_execution_when_the_lease_is_lost_without_releasing_it()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 1);
        var recorder = new Recorder();
        await using var rig = new Rig(db, recorder, new ExecutionOptions { LeaseDurationSeconds = 3, RenewIntervalSeconds = 1 }, turns: false);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Units.Work = async (_, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        };

        await rig.Dispatcher.StartAsync(Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);   // oltre la durata del lease: senza heartbeat sarebbe scaduto e ripreso

        Assert.Equal("Running", await Status(db, prep.Units[0]));
        Assert.Equal(1, await db.ScalarAsync<int>($"""SELECT "AttemptCount" FROM "DocumentJobs" WHERE "Id" = '{prep.Units[0]}'"""));
        Assert.Equal(1L, await db.ScalarAsync<long>($"""SELECT "FencingToken" FROM "DocumentJobs" WHERE "Id" = '{prep.Units[0]}'"""));

        await db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "LeaseOwner" = 'altro', "FencingToken" = 7 WHERE "Id" = '{prep.Units[0]}'""");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);   // l'heartbeat vede il fence nuovo e annulla l'esecuzione

        Assert.Equal("Running", await Status(db, prep.Units[0]));   // il nuovo possessore decide: nessun rilascio da parte del vecchio
        Assert.Equal("altro", await db.ScalarAsync<string>($"""SELECT "LeaseOwner" FROM "DocumentJobs" WHERE "Id" = '{prep.Units[0]}'"""));
    }

    [Fact]
    public async Task A_transient_executor_error_requeues_the_unit_and_a_permanent_one_fails_it_with_its_own_code_through_the_dispatcher()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 2);
        var recorder = new Recorder();
        await using var rig = new Rig(db, recorder, turns: false);

        rig.Units.Fail = new HttpRequestException("giù", null, HttpStatusCode.ServiceUnavailable);
        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));
        Assert.Equal("Queued", await Status(db, prep.Units[0]));
        Assert.Equal("Extracting", await PrepStatus(db, prep.Seed.Preparation));
        Assert.True(await db.ScalarAsync<DateTime>($"""SELECT "NextAttemptAt" FROM "DocumentJobs" WHERE "Id" = '{prep.Units[0]}'""") > DateTime.UtcNow.AddSeconds(-1));

        // La prima unità è in backoff; la seconda parte e fallisce in modo permanente: il suo codice stabile arriva al job e alla preparazione.
        rig.Units.Fail = new DocumentWorkException(DocumentWorkException.DocumentIncomplete, "Unità irriducibile.");
        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));

        Assert.Equal("Failed", await Status(db, prep.Units[1]));
        Assert.Equal("document_incomplete", await Error(db, prep.Units[1]));
        Assert.Equal("Incomplete", await PrepStatus(db, prep.Seed.Preparation));
        Assert.Equal("parent_failed", await Error(db, prep.Units[0]));   // l'unità in backoff non riparte per una preparazione già chiusa
    }

    [Fact]
    public async Task The_dispatcher_does_not_claim_units_without_a_unit_executor_and_the_timer_sweep_still_closes_expired_ones()
    {
        await using var db = await ParsingDatabase.CreateAsync();
        var prep = await SeedPreparationAsync(db, 1);
        var recorder = new Recorder();
        await using var rig = new Rig(db, recorder, units: false, turns: true);

        Assert.False(await rig.Dispatcher.RunOnceAsync(Ct));
        Assert.Equal("Queued", await Status(db, prep.Units[0]));

        await db.ExecuteAsync($"""UPDATE "DocumentPreparations" SET "EffectiveDeadlineAt" = clock_timestamp() - interval '1 millisecond' WHERE "Id" = '{prep.Seed.Preparation}'""");
        var closed = await rig.Dispatcher.SweepDeadlinesOnceAsync(Ct);   // T119: indipendente dallo slot e da chi esegue le unità
        Assert.Contains(prep.Units[0], closed.Jobs);
        Assert.Equal("Failed", await PrepStatus(db, prep.Seed.Preparation));
    }
}
