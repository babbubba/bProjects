using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using bProjects.App.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T064 (seconda parte) — intervalli di coda persistiti, esposizione di «execution» nelle viste di turno/run/preparazione, produttore di
/// document.state e aggancio al dispatcher. PostgreSQL reale, nessun modello (DGX non impostato).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentTelemetryApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- intervalli di coda

    [Fact]
    public async Task The_first_claim_persists_one_queue_interval_and_queue_ms_is_no_longer_null()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { ElapsedSeconds = 50, AttemptCount = 1 });
        await DocumentExecutionIntervalsTests.SeedIntervalAsync(database, root, "Preflight", 1, 10, 12);   // misura da t0+10 s a t0+12 s

        await using var db = database.NewContext();
        await ExecutionIntervalStore.RecordQueueAsync(db, ClassificationOwnerKind.ChatTurn, root.Id, Ct);
        await ExecutionIntervalStore.RecordQueueAsync(db, ClassificationOwnerKind.ChatTurn, root.Id, Ct); // idempotente per (root, tipo, tentativo)

        Assert.Equal(1L, await database.ScalarAsync<long>($"""SELECT count(*) FROM "ExecutionIntervals" WHERE "RootId" = '{root.Id}' AND "Kind" = 'Queue' AND "Attempt" = 1"""));
        var view = (await ExecutionTelemetry.ForRootAsync(db, root.Project, ClassificationOwnerKind.ChatTurn, root.Id, Ct))!;
        // Il t0 è a -50 s sul clock del database: la coda è t0 -> inizio preflight (10 s) più fine preflight (t0+12 s) -> claim (38 s), mai il preflight né l'intero Running.
        Assert.InRange(view.Execution.QueueMs!.Value, 47_000, 52_000);
        Assert.Equal(2_000, view.Execution.PreflightMs);
        Assert.Null(view.Execution.ProcessingMs); // nessuna chiamata osservata: unavailable, non coda attribuita all'elaborazione
    }

    [Fact]
    public async Task Queue_is_unavailable_after_a_retry_and_for_legacy_roots_it_is_not_recorded()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var retried = await DocumentRegimeRoots.SeedAsync(database, RootKind.Run, new RootSpec { ElapsedSeconds = 50, AttemptCount = 2 });
        var legacy = await DocumentRegimeRoots.SeedAsync(database, RootKind.Run, new RootSpec { Legacy = true, AttemptCount = 1 });

        await using var db = database.NewContext();
        await ExecutionIntervalStore.RecordQueueAsync(db, ClassificationOwnerKind.ProcessingRun, retried.Id, Ct);
        await ExecutionIntervalStore.RecordQueueAsync(db, ClassificationOwnerKind.ProcessingRun, legacy.Id, Ct);

        Assert.Equal(0L, await database.ScalarAsync<long>("""SELECT count(*) FROM "ExecutionIntervals" """));
        var view = (await ExecutionTelemetry.ForRootAsync(db, retried.Project, ClassificationOwnerKind.ProcessingRun, retried.Id, Ct))!;
        Assert.Null(view.Execution.QueueMs); // backoff e recupero non sono persistiti: il totale non si finge
    }

    [Fact]
    public async Task The_real_dispatcher_records_the_queue_interval_at_claim_time()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec
        {
            Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, ElapsedSeconds = 20,
        });
        await DocumentExecutionIntervalsTests.SeedIntervalAsync(database, root, "Preflight", 1, 5, 6);   // la coda parte dalla fine del preflight (t0+6 s)
        var executor = new HoldingExecutor();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString))
            .AddSingleton<IOptions<ExecutionOptions>>(Options.Create(new ExecutionOptions()))
            .AddScoped<ExecutionLeaseStore>()
            .BuildServiceProvider();
        var dispatcher = new AiDispatchService(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new ExecutionOptions()), NullLogger<AiDispatchService>.Instance, executor: executor);

        var run = Task.Run(() => dispatcher.RunOnceAsync(Ct), Ct);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        try
        {
            Assert.Equal(1L, await database.ScalarAsync<long>($"""SELECT count(*) FROM "ExecutionIntervals" WHERE "RootId" = '{root.Id}' AND "Kind" = 'Queue' AND "Attempt" = 1"""));
            Assert.True(await database.ScalarAsync<bool>($"""SELECT "EndedAt" - "StartedAt" >= interval '13 seconds' FROM "ExecutionIntervals" WHERE "RootId" = '{root.Id}' AND "Kind" = 'Queue'"""));
        }
        finally
        {
            executor.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
    }

    private sealed class HoldingExecutor : IChatTurnExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(ChatTurnClaim claim, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }

    // ---------------------------------------------------------------- viste

    [Fact]
    public async Task Turn_run_and_preparation_views_expose_execution_and_legacy_roots_do_not()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var host = await MetricsHost.StartAsync(database.ConnectionString, new CannedLlamaServer(), MetricsHost.CannedOptions(), chat: false, formal: false);
        using var pm = await host.App.LoginAsync();

        var turn = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { ElapsedSeconds = 30, Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Ordinary, PromotionCount = 1 });
        var run = await DocumentRegimeRoots.SeedAsync(database, RootKind.Run, new RootSpec { ElapsedSeconds = 20 });
        var legacy = await DocumentRegimeRoots.SeedAsync(database, RootKind.Run, new RootSpec { Legacy = true });

        var turnView = await GetAsync(pm, $"/api/projects/{turn.Project}/chat-turns/{turn.Id}");
        var execution = turnView.GetProperty("execution");
        Assert.Equal("Extended", execution.GetProperty("regime").GetString());
        Assert.Equal("Ordinary", execution.GetProperty("initialRegime").GetString());
        Assert.Equal("additional_work", execution.GetProperty("promotionReason").GetString());
        Assert.InRange(execution.GetProperty("elapsedMs").GetInt64(), 30_000, 40_000);

        var runExecution = (await GetAsync(pm, $"/api/projects/{run.Project}/processing-runs/{run.Id}")).GetProperty("execution");
        Assert.Equal("Ordinary", runExecution.GetProperty("regime").GetString());
        Assert.Equal(JsonValueKind.Null, runExecution.GetProperty("promotedAt").ValueKind);

        // Ramo 001: nessun campo nuovo nelle viste dei root legacy.
        var legacyView = await GetAsync(pm, $"/api/projects/{legacy.Project}/processing-runs/{legacy.Id}");
        Assert.True(!legacyView.TryGetProperty("execution", out var legacyExecution) || legacyExecution.ValueKind == JsonValueKind.Null);

        // Preparazione: unità realmente pianificate e completate dai job ExtractUnit.
        var parse = await database.SeedParseAsync();
        await database.ExecuteAsync($"""
            INSERT INTO "DocumentJobs" ("Id","ProjectId","PreparationId","Kind","UnitId","Status","RequestedBy","RequestedAt","DeadlineAt","AttemptCount","MaxAttempts","FencingToken","ResultHash","CompletedAt")
            VALUES ('{Guid.NewGuid()}','{parse.Project}','{parse.Preparation}','ExtractUnit','{Guid.NewGuid()}','Completed','{parse.User}',now(),now() + interval '1 hour',1,3,1,'{new string('a', 64)}',now()),
                   ('{Guid.NewGuid()}','{parse.Project}','{parse.Preparation}','ExtractUnit','{Guid.NewGuid()}','Queued','{parse.User}',now(),now() + interval '1 hour',0,3,0,NULL,NULL)
            """);
        var preparation = (await GetAsync(pm, $"/api/projects/{parse.Project}/documents/{parse.Document}/versions/1/preparations/1")).GetProperty("execution");
        Assert.Equal(2, preparation.GetProperty("plannedUnits").GetInt32());
        Assert.Equal(1, preparation.GetProperty("completedUnits").GetInt32());
        Assert.Equal("Queued", preparation.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task A_late_completed_turn_exposes_only_measures_through_the_api_and_never_a_target_verdict_or_unmeasured_zero()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var host = await MetricsHost.StartAsync(database.ConnectionString, new CannedLlamaServer(), MetricsHost.CannedOptions(), chat: false, formal: false);
        using var pm = await host.App.LoginAsync();
        var turn = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { ElapsedSeconds = 200, Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Ordinary, PromotionCount = 1, AttemptCount = 1 });
        await database.ExecuteAsync($"""UPDATE "ChatTurns" SET "Status" = 'Completed', "CompletedAt" = clock_timestamp(), "ResultMessageId" = "TriggerMessageId", "LeaseOwner" = NULL, "LeaseUntil" = NULL WHERE "Id" = '{turn.Id}'""");
        await DocumentExecutionIntervalsTests.SeedIntervalAsync(database, turn, "Preflight", 1, 1, 2);
        await DocumentExecutionIntervalsTests.SeedIntervalAsync(database, turn, "Queue", 1, 2, 8);

        var execution = (await GetAsync(pm, $"/api/projects/{turn.Project}/chat-turns/{turn.Id}")).GetProperty("execution");

        // Il turno è finito dopo oltre 180 s (200 s): la vista riporta i tempi misurati e nessun verdetto sul target (Completed non certifica il profilo N).
        Assert.InRange(execution.GetProperty("elapsedMs").GetInt64(), 200_000, 206_000);
        Assert.Equal(1_000, execution.GetProperty("preflightMs").GetInt64());
        Assert.Equal(1_000 + 6_000, execution.GetProperty("queueMs").GetInt64());
        Assert.Equal(0, execution.GetProperty("backoffMs").GetInt64());                            // un solo tentativo: nessun retry
        Assert.Equal(0, execution.GetProperty("recoveryMs").GetInt64());                                // un solo tentativo: nessun recupero
        Assert.Equal(JsonValueKind.Null, execution.GetProperty("processingMs").ValueKind);         // nessuna chiamata osservata
        string[] forbidden = ["target", "nominal", "certif", "within", "respect", "complian", "sla", "met", "ok"];
        Assert.DoesNotContain(execution.EnumerateObject().Select(p => p.Name), name => forbidden.Any(f => string.Equals(name, f, StringComparison.OrdinalIgnoreCase) || (f.Length > 3 && name.Contains(f, StringComparison.OrdinalIgnoreCase))));
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();
    }

    // ---------------------------------------------------------------- produttore di document.state

    [Fact]
    public async Task The_document_state_publisher_emits_only_real_status_changes_after_the_baseline()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var parse = await database.SeedParseAsync();
        var hub = new ProjectEventHub();
        using var subscription = hub.Subscribe(parse.Project);
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString))
            .BuildServiceProvider();
        var publisher = new DocumentStateEvents(hub, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<DocumentStateEvents>.Instance);

        await publisher.PollAsync(Ct);   // baseline: lo stato corrente non è un cambiamento
        await publisher.PollAsync(Ct);
        Assert.False(subscription.Reader.TryRead(out _));

        await database.ExecuteAsync($"""UPDATE "DocumentPreparations" SET "Status" = 'Extracting' WHERE "Id" = '{parse.Preparation}'""");
        await publisher.PollAsync(Ct);
        await publisher.PollAsync(Ct);   // nessuna ripetizione senza nuovo cambiamento

        Assert.True(subscription.Reader.TryRead(out var evt));
        Assert.Equal(ProjectEventHub.DocumentState, evt.Name);
        using var json = JsonDocument.Parse(evt.Data);
        Assert.Equal("Extracting", json.RootElement.GetProperty("state").GetString());
        Assert.Equal(parse.Preparation, json.RootElement.GetProperty("preparationId").GetGuid());
        Assert.Equal(parse.Version, json.RootElement.GetProperty("versionId").GetGuid());
        Assert.False(subscription.Reader.TryRead(out _));

        await database.ExecuteAsync($"""UPDATE "DocumentPreparations" SET "Status" = 'Failed', "ErrorCode" = 'document_incomplete' WHERE "Id" = '{parse.Preparation}'""");
        await publisher.PollAsync(Ct);
        Assert.True(subscription.Reader.TryRead(out var failed));
        Assert.Contains("Failed", failed.Data);
        await publisher.PollAsync(Ct);
        Assert.False(subscription.Reader.TryRead(out _));   // terminale: non più seguita, nessuna ripetizione
    }
}
