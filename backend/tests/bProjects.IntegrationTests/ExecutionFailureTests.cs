using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T046 - guasti e retry, separatamente per chat, Analysis e Document, con PostgreSQL reale e l'applicazione completa
/// (endpoint, dispatcher, esecutore/workflow, store e commit reali). L'unico punto di iniezione e' il server del modello:
/// un <see cref="HttpMessageHandler"/> dietro il vero <see cref="InternalModelClient"/> (stesso SDK, stessa pipeline e stessa
/// configurazione di Program.cs), cosi' le prove vedono quante richieste HTTP partono davvero per ogni tentativo applicativo:
/// se l'SDK ritentasse da solo, il budget si moltiplicherebbe in layer diversi e le prove lo rileverebbero.
/// Il dispatcher e' pilotato a mano (RunOnceAsync) e il tempo si governa dal database (NextAttemptAt/LeaseUntil/DeadlineAt),
/// quindi nessuna prova dorme per il backoff. Nessuna prova MafReal: il task non la prevede e il modello e' solo la sorgente di errori.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ExecutionFailureTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly PostgresDatabaseFixture _database = new();
    private readonly List<Rig> _rigs = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Kind
    {
        Chat,
        Analysis,
        Document,
    }

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var rig in _rigs)
        {
            await rig.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    // ---------------------------------------------------------------- politica nominale: 30 / 10 / 3 / 2-4 / 180

    [Theory]
    [InlineData(Kind.Chat)]
    [InlineData(Kind.Analysis)]
    [InlineData(Kind.Document)]
    public async Task The_default_policy_is_30_10_3_backoff_2_4_and_deadline_180_and_is_frozen_on_each_new_request(Kind kind)
    {
        var rig = await StartRig(kind);
        var policy = rig.App.Services.GetRequiredService<IOptions<ExecutionOptions>>().Value;
        Assert.Equal((30, 10, 3, 2, 10, 180), (policy.LeaseDurationSeconds, policy.RenewIntervalSeconds, policy.MaxAttempts,
            policy.RetryBaseDelaySeconds, policy.RetryMaxDelaySeconds, policy.RunDeadlineSeconds));
        Assert.Equal([2d, 4d], [policy.BackoffAfterAttempt(1).TotalSeconds, policy.BackoffAfterAttempt(2).TotalSeconds]);

        var work = await rig.Command();
        var queued = await rig.RowOf(work);
        Assert.Equal(("Queued", 0, 3, 0L), (queued.Status, queued.Attempts, queued.MaxAttempts, queued.Fence));
        Assert.Equal(180, (queued.Deadline - queued.Requested).TotalSeconds, 0.5);

        Row? claimed = null;
        rig.Dgx.OnCompletion = async _ => claimed = await rig.RowOf(work);
        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));

        Assert.Equal(("Running", 1, 1L), (claimed!.Status, claimed.Attempts, claimed.Fence));
        Assert.Equal(30, (claimed.LeaseUntil!.Value - claimed.Started!.Value).TotalSeconds, 0.5);
        Assert.Equal(queued.Deadline, claimed.Deadline); // la deadline non si sposta con il claim
        Assert.Equal("Completed", (await rig.RowOf(work)).Status);
    }

    [Theory]
    [InlineData(Kind.Chat)]
    [InlineData(Kind.Analysis)]
    [InlineData(Kind.Document)]
    public async Task The_owner_renews_the_lease_during_the_inference_without_moving_the_deadline(Kind kind)
    {
        var rig = await StartRig(kind, o => o.RenewIntervalSeconds = 1); // solo il ritmo del rinnovo e' accorciato; lease 30 s invariato
        var work = await rig.Command();
        Row? first = null, renewed = null;
        rig.Dgx.OnCompletion = async _ =>
        {
            first = await rig.RowOf(work);
            await Eventually(async () => (renewed = await rig.RowOf(work)).LeaseUntil > first.LeaseUntil!.Value.AddSeconds(0.5), "lease rinnovato");
        };

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));

        Assert.True(renewed!.LeaseUntil > first!.LeaseUntil);
        Assert.InRange((renewed.LeaseUntil!.Value - first.Started!.Value).TotalSeconds, 30.5, 40); // il rinnovo riporta il lease a 30 s da adesso
        Assert.Equal(first.Deadline, renewed.Deadline);
        Assert.Equal((first.Attempts, first.Fence, first.Owner), (renewed.Attempts, renewed.Fence, renewed.Owner));
        Assert.Equal("Completed", (await rig.RowOf(work)).Status);
    }

    // ---------------------------------------------------------------- transitori: 408/429/5xx

    [Theory]
    [InlineData(Kind.Chat, 408)]
    [InlineData(Kind.Chat, 429)]
    [InlineData(Kind.Chat, 500)]
    [InlineData(Kind.Chat, 503)]
    [InlineData(Kind.Analysis, 408)]
    [InlineData(Kind.Analysis, 429)]
    [InlineData(Kind.Analysis, 500)]
    [InlineData(Kind.Analysis, 503)]
    [InlineData(Kind.Document, 408)]
    [InlineData(Kind.Document, 429)]
    [InlineData(Kind.Document, 500)]
    [InlineData(Kind.Document, 503)]
    public async Task A_transient_http_status_requeues_with_a_persisted_backoff_and_the_retry_completes(Kind kind, int status)
    {
        var rig = await StartRig(kind);
        var work = await rig.Command();
        var before = await rig.Snapshot();
        rig.Dgx.Faults.Enqueue(FakeDgx.Status(status));

        var failed = await FailOnce(rig, work, 2);

        Assert.Equal(1, rig.Dgx.Completions); // una sola richiesta HTTP per il tentativo: nessun retry dell'SDK
        Assert.Equal(("Queued", 1, 1L), (failed.Row.Status, failed.Row.Attempts, failed.Row.Fence));
        Assert.Null(failed.Row.Owner);
        Assert.Null(failed.Row.LeaseUntil);
        Assert.Null(failed.Row.Code);
        Assert.Null(failed.Row.Completed);
        Assert.Equal(before, await rig.Snapshot());
        Assert.False(await rig.Dispatcher.RunOnceAsync(Ct)); // backoff non trascorso: nessun claim anticipato
        Assert.Equal(1, rig.Dgx.Completions);

        await rig.ExpireBackoff(work);
        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));

        var done = await rig.RowOf(work);
        Assert.Equal(("Completed", 2, 2L), (done.Status, done.Attempts, done.Fence));
        Assert.Equal(2, rig.Dgx.Completions);
        Assert.Equal(1, await rig.ResultCount(work));
    }

    [Theory]
    [InlineData(Kind.Chat)]
    [InlineData(Kind.Analysis)]
    [InlineData(Kind.Document)]
    public async Task A_dropped_connection_and_a_network_timeout_are_transient_and_each_sent_once(Kind kind)
    {
        var rig = await StartRig(kind);
        var work = await rig.Command();

        rig.Dgx.Faults.Enqueue(_ => throw new HttpRequestException("connessione rifiutata"));
        var dropped = await FailOnce(rig, work, 2);
        Assert.Equal(("Queued", 1), (dropped.Row.Status, dropped.Row.Attempts));
        Assert.Equal(1, rig.Dgx.Completions);

        await rig.ExpireBackoff(work);
        rig.Dgx.Faults.Enqueue(_ => throw new TaskCanceledException("timeout di rete", new TimeoutException()));
        var timedOut = await FailOnce(rig, work, 4);
        Assert.Equal(("Queued", 2), (timedOut.Row.Status, timedOut.Row.Attempts));
        Assert.Equal(2, rig.Dgx.Completions); // un tentativo applicativo = una richiesta, anche per il trasporto

        await rig.ExpireBackoff(work);
        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));
        Assert.Equal(("Completed", 3), ((await rig.RowOf(work)).Status, (await rig.RowOf(work)).Attempts));
    }

    // ---------------------------------------------------------------- Retry-After e deadline

    [Theory]
    [InlineData(Kind.Chat)]
    [InlineData(Kind.Analysis)]
    [InlineData(Kind.Document)]
    public async Task Retry_After_longer_than_the_backoff_defers_the_next_attempt_and_a_shorter_one_does_not_anticipate_it(Kind kind)
    {
        var rig = await StartRig(kind);
        var work = await rig.Command();

        rig.Dgx.Faults.Enqueue(FakeDgx.Status(429, retryAfter: "7"));
        var longer = await FailOnce(rig, work, delaySeconds: 7); // il backoff sarebbe 2 s
        Assert.Equal(("Queued", 1), (longer.Row.Status, longer.Row.Attempts));
        Assert.Equal(1, rig.Dgx.Completions);

        await rig.ExpireBackoff(work);
        rig.Dgx.Faults.Enqueue(FakeDgx.Status(503, retryAfter: "1"));
        var shorter = await FailOnce(rig, work, delaySeconds: 4); // il backoff del secondo tentativo (4 s) prevale su Retry-After 1 s
        Assert.Equal(("Queued", 2), (shorter.Row.Status, shorter.Row.Attempts));
        Assert.Equal(2, rig.Dgx.Completions);
    }

    [Theory]
    [InlineData(Kind.Chat)]
    [InlineData(Kind.Analysis)]
    [InlineData(Kind.Document)]
    public async Task A_Retry_After_beyond_the_deadline_fails_the_work_as_deadline_exceeded_keeping_the_previous_results(Kind kind)
    {
        var rig = await StartRig(kind);
        var work = await rig.Command();
        var before = await rig.Snapshot();
        await rig.Exec($"""UPDATE "{rig.Table}" SET "DeadlineAt" = clock_timestamp() + interval '10 seconds' WHERE "Id" = @id""", ("id", work));
        rig.Dgx.Faults.Enqueue(FakeDgx.Status(503, retryAfter: "30"));

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));

        var failed = await rig.RowOf(work);
        Assert.Equal(("Failed", 1, "deadline_exceeded"), (failed.Status, failed.Attempts, failed.Code));
        AssertFailedTerminal(failed);
        Assert.Equal(1, rig.Dgx.Completions);
        Assert.Equal(before, await rig.Snapshot());
        Assert.False(await rig.Dispatcher.RunOnceAsync(Ct));
        Assert.Equal(1, rig.Dgx.Completions);
    }

    [Theory]
    [InlineData(Kind.Chat)]
    [InlineData(Kind.Analysis)]
    [InlineData(Kind.Document)]
    public async Task A_deadline_that_passes_while_in_backoff_fails_the_work_without_another_attempt(Kind kind)
    {
        var rig = await StartRig(kind);
        var work = await rig.Command();
        var before = await rig.Snapshot();
        rig.Dgx.Faults.Enqueue(FakeDgx.Status(503));
        await FailOnce(rig, work, 2);
        await rig.Exec($"""UPDATE "{rig.Table}" SET "NextAttemptAt" = clock_timestamp() - interval '1 second', "DeadlineAt" = clock_timestamp() - interval '1 second' WHERE "Id" = @id""", ("id", work));

        Assert.False(await rig.Dispatcher.RunOnceAsync(Ct)); // lo sweep lo fa fallire e non c'e' nulla da claimare

        var failed = await rig.RowOf(work);
        Assert.Equal(("Failed", 1, "deadline_exceeded"), (failed.Status, failed.Attempts, failed.Code));
        AssertFailedTerminal(failed);
        Assert.Equal(1, rig.Dgx.Completions);
        Assert.Equal(before, await rig.Snapshot());
    }

    [Theory]
    [InlineData(Kind.Chat, true)]
    [InlineData(Kind.Analysis, true)]
    [InlineData(Kind.Document, true)]
    [InlineData(Kind.Chat, false)]
    [InlineData(Kind.Analysis, false)]
    [InlineData(Kind.Document, false)]
    public async Task A_commit_refused_because_the_deadline_or_the_lease_ran_out_is_not_a_model_error(Kind kind, bool pastDeadline)
    {
        var rig = await StartRig(kind);
        var work = await rig.Command();
        var before = await rig.Snapshot();
        // Come dopo un'inferenza piu' lunga del lease (e della deadline): il modello risponde, ma il commit non e' piu' dovuto.
        rig.Dgx.OnCompletion = _ => rig.Exec(
            $"""UPDATE "{rig.Table}" SET "LeaseUntil" = clock_timestamp() - interval '1 second'{(pastDeadline ? ", \"DeadlineAt\" = clock_timestamp() - interval '1 second'" : "")} WHERE "Id" = @id""",
            ("id", work));

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));

        // Ne' risultato ne' Failed(processing_failed): il lavoro non e' piu' dell'owner e decidono sweep e reclaim.
        var after = await rig.RowOf(work);
        Assert.Equal("Running", after.Status);
        Assert.Null(after.Code);
        Assert.Equal(before, await rig.Snapshot());
        rig.Dgx.OnCompletion = null;

        if (pastDeadline)
        {
            Assert.False(await rig.Dispatcher.RunOnceAsync(Ct));
            var failed = await rig.RowOf(work);
            Assert.Equal(("Failed", 1, "deadline_exceeded"), (failed.Status, failed.Attempts, failed.Code));
            AssertFailedTerminal(failed);
            Assert.Equal(1, rig.Dgx.Completions);
            Assert.Equal(before, await rig.Snapshot());
        }
        else
        {
            Assert.True(await rig.Dispatcher.RunOnceAsync(Ct)); // reclaim: nuovo owner, nuovo fence, nuovo tentativo
            var done = await rig.RowOf(work);
            Assert.Equal(("Completed", 2, 2L), (done.Status, done.Attempts, done.Fence));
            Assert.Equal(1, await rig.ResultCount(work));
        }
    }

    // ---------------------------------------------------------------- permanenti

    [Theory]
    [InlineData(Kind.Chat, 400, "model_rejected")]
    [InlineData(Kind.Chat, 401, "model_unauthorized")]
    [InlineData(Kind.Chat, 404, "model_rejected")]
    [InlineData(Kind.Analysis, 400, "model_rejected")]
    [InlineData(Kind.Analysis, 401, "model_unauthorized")]
    [InlineData(Kind.Analysis, 404, "model_rejected")]
    [InlineData(Kind.Document, 400, "model_rejected")]
    [InlineData(Kind.Document, 401, "model_unauthorized")]
    [InlineData(Kind.Document, 404, "model_rejected")]
    public async Task A_permanent_http_error_fails_at_once_without_retry_keeping_originals_preview_and_draft(Kind kind, int status, string code)
    {
        var rig = await StartRig(kind);
        var work = await rig.Command();
        var before = await rig.Snapshot();
        rig.Dgx.Faults.Enqueue(FakeDgx.Status(status));

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));

        var failed = await rig.RowOf(work);
        Assert.Equal(("Failed", 1, 1L, code), (failed.Status, failed.Attempts, failed.Fence, failed.Code));
        AssertFailedTerminal(failed);
        Assert.DoesNotContain(FakeDgx.Secret, failed.Message); // il corpo dell'errore del server non esce mai
        Assert.Equal(1, rig.Dgx.Completions);
        Assert.Equal(0, await rig.ResultCount(work));
        Assert.Equal(before, await rig.Snapshot());
        Assert.False(await rig.Dispatcher.RunOnceAsync(Ct)); // niente retry, ne' ora ne' dopo il backoff
        Assert.Equal(1, rig.Dgx.Completions);
        Assert.Equal("Failed", await rig.PublicStatus(work));
    }

    // ---------------------------------------------------------------- limite di tentativi e persistenza

    [Theory]
    [InlineData(Kind.Chat)]
    [InlineData(Kind.Analysis)]
    [InlineData(Kind.Document)]
    public async Task Three_transient_failures_exhaust_the_attempts_with_backoff_2_then_4_and_the_counter_survives_a_restart(Kind kind)
    {
        var rig = await StartRig(kind);
        var work = await rig.Command();
        var before = await rig.Snapshot();
        for (var i = 0; i < 6; i++)
        {
            rig.Dgx.Faults.Enqueue(FakeDgx.Status(503)); // piu' errori del necessario: il quarto non deve mai essere consumato
        }

        var first = await FailOnce(rig, work, 2);
        Assert.Equal(("Queued", 1), (first.Row.Status, first.Row.Attempts));

        // Un nuovo processo sullo stesso database: il contatore non si azzera e il backoff gia' scritto resta valido.
        await rig.Restart();
        Assert.False(await rig.Dispatcher.RunOnceAsync(Ct));
        Assert.Equal(1, await Attempts(rig, work));
        await rig.ExpireBackoff(work);

        var second = await FailOnce(rig, work, 4);
        Assert.Equal(("Queued", 2, 2L), (second.Row.Status, second.Row.Attempts, second.Row.Fence));
        await rig.ExpireBackoff(work);

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));

        var failed = await rig.RowOf(work);
        Assert.Equal(("Failed", 3, 3, 3L, "attempts_exhausted"), (failed.Status, failed.Attempts, failed.MaxAttempts, failed.Fence, failed.Code));
        AssertFailedTerminal(failed);
        Assert.Equal(3, rig.Dgx.Completions); // tre tentativi applicativi, tre richieste: nessun retry dell'SDK li moltiplica
        Assert.Equal(3, rig.Dgx.Faults.Count);
        Assert.Equal(before, await rig.Snapshot());
        Assert.False(await rig.Dispatcher.RunOnceAsync(Ct));
        Assert.Equal(3, rig.Dgx.Completions);
        Assert.Equal("Failed", await rig.PublicStatus(work));
    }

    [Theory]
    [InlineData(Kind.Chat)]
    [InlineData(Kind.Analysis)]
    [InlineData(Kind.Document)]
    public async Task A_custom_policy_governs_lease_attempts_backoff_and_deadline_and_is_persisted_with_the_request(Kind kind)
    {
        var rig = await StartRig(kind, o =>
        {
            o.LeaseDurationSeconds = 20;
            o.RenewIntervalSeconds = 3;
            o.MaxAttempts = 5;
            o.RetryBaseDelaySeconds = 3;
            o.RetryMaxDelaySeconds = 7;
            o.RunDeadlineSeconds = 90;
        });
        var work = await rig.Command();
        var before = await rig.Snapshot();
        var queued = await rig.RowOf(work);
        Assert.Equal(5, queued.MaxAttempts);
        Assert.Equal(90, (queued.Deadline - queued.Requested).TotalSeconds, 0.5);

        Row? claimed = null;
        rig.Dgx.OnCompletion = async _ => claimed ??= await rig.RowOf(work);
        for (var i = 0; i < 5; i++)
        {
            rig.Dgx.Faults.Enqueue(FakeDgx.Status(500));
        }

        // Backoff min(7, 3 * 2^(n-1)): 3, 6, poi il tetto di 7 s.
        foreach (var (attempt, delay) in new[] { (1, 3), (2, 6), (3, 7), (4, 7) })
        {
            var step = await FailOnce(rig, work, delay);
            Assert.Equal(("Queued", attempt), (step.Row.Status, step.Row.Attempts));
            await rig.ExpireBackoff(work);
        }

        Assert.Equal(20, (claimed!.LeaseUntil!.Value - claimed.Started!.Value).TotalSeconds, 0.5);
        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));
        var failed = await rig.RowOf(work);
        Assert.Equal(("Failed", 5, "attempts_exhausted"), (failed.Status, failed.Attempts, failed.Code));
        Assert.Equal(5, rig.Dgx.Completions);
        Assert.Equal(before, await rig.Snapshot());
    }

    // ---------------------------------------------------------------- Failed e nuovo comando esplicito

    [Theory]
    [InlineData(Kind.Chat)]
    [InlineData(Kind.Analysis)]
    [InlineData(Kind.Document)]
    public async Task After_Failed_a_replay_returns_the_same_work_and_only_a_new_explicit_command_creates_a_new_one(Kind kind)
    {
        var rig = await StartRig(kind);
        var work = await rig.Command();
        var key = rig.LastKey;
        rig.Dgx.Faults.Enqueue(FakeDgx.Status(401));
        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));
        var failed = await rig.RowOf(work);
        Assert.Equal("Failed", failed.Status);
        var before = await rig.Snapshot();

        // La lettura e il replay con la stessa chiave non ritentano: stessa richiesta, nessun nuovo tentativo.
        Assert.Equal("Failed", await rig.PublicStatus(work));
        var replay = await rig.Replay(key);
        Assert.Equal(work, replay);
        Assert.Equal(failed, await rig.RowOf(work));
        Assert.False(await rig.Dispatcher.RunOnceAsync(Ct));
        Assert.Equal(1, rig.Dgx.Completions);

        // Un nuovo comando esplicito del PM e' un'altra richiesta, con contatori e deadline propri, che non riusa la fallita.
        var fresh = await rig.Command();
        Assert.NotEqual(work, fresh);
        var queued = await rig.RowOf(fresh);
        Assert.Equal(("Queued", 0, 0L, 3), (queued.Status, queued.Attempts, queued.Fence, queued.MaxAttempts));
        Assert.True(queued.Deadline > failed.Deadline);
        Assert.Equal(failed, await rig.RowOf(work)); // la fallita resta com'e'
        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));
        var done = await rig.RowOf(fresh);
        Assert.Equal(("Completed", 1), (done.Status, done.Attempts));
        Assert.Equal(2, rig.Dgx.Completions);
        Assert.Equal("Failed", (await rig.RowOf(work)).Status);
        Assert.NotEqual(before, await rig.Snapshot()); // solo il nuovo comando ha prodotto un risultato
    }

    // ================================================================ supporto

    private static void AssertFailedTerminal(Row failed)
    {
        Assert.NotNull(failed.Completed);
        Assert.Null(failed.Owner);
        Assert.Null(failed.LeaseUntil);
        Assert.False(string.IsNullOrWhiteSpace(failed.Message));
        Assert.DoesNotContain(FakeDgx.Secret, failed.Message);
    }

    /// <summary>Un tentativo che fallisce: il dispatcher lo esegue e il lavoro torna in coda con il backoff atteso (secondi, calcolato dal tempo del database).</summary>
    private static async Task<(Row Row, DateTimeOffset Before, DateTimeOffset After)> FailOnce(Rig rig, Guid work, int delaySeconds)
    {
        if (rig.Dgx.Faults.IsEmpty)
        {
            rig.Dgx.Faults.Enqueue(FakeDgx.Status(503));
        }

        var before = await rig.DatabaseNow();
        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));
        var after = await rig.DatabaseNow();
        var row = await rig.RowOf(work);
        Assert.Equal("Queued", row.Status);
        Assert.NotNull(row.Next);
        // Il rilascio avviene fra before e after sul clock del database: NextAttemptAt = rilascio + max(backoff, Retry-After).
        Assert.True(row.Next >= before.AddSeconds(delaySeconds), $"NextAttemptAt {row.Next:O} precede il backoff di {delaySeconds} s.");
        Assert.True(row.Next <= after.AddSeconds(delaySeconds), $"NextAttemptAt {row.Next:O} supera il backoff di {delaySeconds} s.");
        return (row, before, after);
    }

    private static async Task<int> Attempts(Rig rig, Guid work) => (await rig.RowOf(work)).Attempts;

    private static async Task Eventually(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, Ct);
        }

        Assert.Fail($"Condizione non raggiunta entro il tempo previsto: {what}.");
    }

    /// <summary>
    /// Applicazione completa di un tipo di lavoro, gia' con i risultati precedenti da conservare: per la chat un turno completato,
    /// per Analysis un'anteprima completata, per Document una bozza (su analisi confermata). Ogni prova parte poi da un nuovo
    /// comando esplicito, che e' quello che i guasti colpiscono.
    /// </summary>
    private async Task<Rig> StartRig(Kind kind, Action<ExecutionOptions>? policy = null)
    {
        TestEnvironment.RequirePostgres();
        var rig = new Rig(kind, _database.ConnectionString, policy);
        _rigs.Add(rig);
        await rig.StartAsync(prepare: true);
        await rig.SeedPreviousResultsAsync();
        rig.Dgx.ResetCount();
        rig.Dgx.OnCompletion = null;
        return rig;
    }

    private sealed record Row(
        string Status, int Attempts, int MaxAttempts, long Fence, string? Owner, DateTimeOffset? LeaseUntil, DateTimeOffset? Next,
        DateTimeOffset Deadline, DateTimeOffset Requested, DateTimeOffset? Started, DateTimeOffset? Completed, string? Code, string? Message);

    private sealed class Rig(Kind kind, string connectionString, Action<ExecutionOptions>? policy) : IAsyncDisposable
    {
        private ApiHarness? _app;
        private Guid _project;
        private Guid _analysis;
        private Guid _revision;
        private int _messages;

        public FakeDgx Dgx { get; } = new();

        public ApiHarness App => _app!;

        public AiDispatchService Dispatcher { get; private set; } = null!;

        public Guid LastKey { get; private set; }

        public string Table => kind == Kind.Chat ? "ChatTurns" : "ProcessingRuns";

        public async Task StartAsync(bool prepare)
        {
            _app = await ApiHarness.StartAsync(connectionString, prepare, Configure);
            Assert.DoesNotContain(App.Services.GetServices<IHostedService>(), s => s is AiDispatchService); // il dispatcher e' pilotato dalla prova
            Dispatcher = ActivatorUtilities.CreateInstance<AiDispatchService>(App.Services);
        }

        /// <summary>Lo stesso database con un nuovo processo applicativo: nessuna memoria in comune oltre a PostgreSQL.</summary>
        public async Task Restart()
        {
            await _app!.DisposeAsync();
            await StartAsync(prepare: false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }

            Dgx.Dispose();
        }

        /// <summary>Come Program.cs: stesso <see cref="InternalModelClient"/> (SDK senza retry) e stesso cablaggio di agenti e workflow, sul solo server finto.</summary>
        private void Configure(IServiceCollection services)
        {
            if (policy is not null)
            {
                services.Configure(policy);
                services.Configure<AiOptions>(o => o.RequestTimeoutSeconds = 60); // deve restare sotto la deadline scelta
            }

            foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AiDispatchService)).ToList())
            {
                services.Remove(hosted);
            }

            services.RemoveAll<InternalModelClient>();
            services.AddSingleton(sp => new InternalModelClient(sp.GetRequiredService<IOptions<AiOptions>>().Value, Dgx));
            services.AddSingleton(sp =>
            {
                var model = sp.GetRequiredService<InternalModelClient>();
                return new ConversationalAgent(AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model), model.CreateChatOptions(temperature: 0.2f));
            });
            if (kind == Kind.Chat)
            {
                services.AddSingleton<IChatTurnExecutor, ConversationalTurnExecutor>();
                return;
            }

            services.AddSingleton(sp => new AnalysisWorkflow(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ConversationalAgent>(),
                sp.GetRequiredService<InternalModelClient>().CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0)));
            if (kind == Kind.Document)
            {
                services.AddSingleton(sp =>
                {
                    var model = sp.GetRequiredService<InternalModelClient>();
                    return new DocumentAgent(AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model), model.CreateChatOptions(temperature: 0.2f));
                });
                services.AddSingleton(sp => new DocumentWorkflow(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<DocumentAgent>()));
            }
        }

        // ------------------------------------------------------------ risultati precedenti

        public async Task SeedPreviousResultsAsync()
        {
            using var pm = await App.LoginAsync();
            var created = await pm.PostAsJsonAsync("/api/projects", new { name = $"Guasti {Guid.NewGuid():N}" }, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            _project = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

            if (kind == Kind.Chat)
            {
                Dgx.Reply = _ => "Risposta precedente";
                var turn = await Command();
                Assert.True(await Dispatcher.RunOnceAsync(Ct));
                Assert.Equal("Completed", (await RowOf(turn)).Status);
                Assert.Equal(1, await Count("""SELECT count(*) FROM "ConversationMessages" WHERE "Role" = 'Assistant'"""));
                return;
            }

            // Il dispatcher della prova non ha un esecutore di chat: i turni creati dai messaggi restano Queued e non interferiscono.
            foreach (var text in new[] { "Vogliamo un portale clienti per ridurre i tempi di risposta ai ticket.", "Lo useranno gli agenti del supporto." })
            {
                await PostMessage(pm, text);
            }

            Dgx.Reply = WorkflowRecoveryTests.AnalysisReply;
            _analysis = await Command(Kind.Analysis);
            Assert.True(await Dispatcher.RunOnceAsync(Ct));
            Assert.Equal("Completed", (await RowOf(_analysis)).Status);
            _revision = await Scalar<Guid>("""SELECT "ResultRevisionId" FROM "ProcessingRuns" WHERE "Id" = @id""", ("id", _analysis));
            if (kind == Kind.Analysis)
            {
                return;
            }

            using (var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{_project}/analyses/{_analysis}/confirm"))
            {
                confirm.Headers.Add("If-Match", $"\"{_revision}\"");
                Assert.Equal(HttpStatusCode.OK, (await pm.SendAsync(confirm, Ct)).StatusCode);
            }

            Dgx.Reply = WorkflowRecoveryTests.DocumentReply;
            var draft = await Command();
            Assert.True(await Dispatcher.RunOnceAsync(Ct));
            Assert.Equal("Completed", (await RowOf(draft)).Status);
            Assert.Equal(1, await Count("""SELECT count(*) FROM "Drafts" """));
        }

        /// <summary>Un nuovo comando esplicito del PM del tipo della prova: messaggio (turno), Analizza o Genera documento.</summary>
        public Task<Guid> Command() => Command(kind);

        private async Task<Guid> Command(Kind what)
        {
            using var pm = await App.LoginAsync();
            LastKey = Guid.NewGuid();
            switch (what)
            {
                case Kind.Chat:
                    return await PostMessage(pm, $"Messaggio {++_messages}", LastKey);
                case Kind.Analysis:
                    return await Formal(pm, $"/api/projects/{_project}/analyses", null, LastKey, HttpStatusCode.Accepted);
                default:
                    return await Formal(pm, $"/api/projects/{_project}/analyses/{_analysis}/document-generations", new { revisionId = _revision }, LastKey, HttpStatusCode.Accepted);
            }
        }

        /// <summary>Stesso comando con la stessa chiave: lo stesso lavoro, senza un nuovo tentativo.</summary>
        public async Task<Guid> Replay(Guid key)
        {
            using var pm = await App.LoginAsync();
            return kind switch
            {
                Kind.Chat => await PostMessage(pm, $"Messaggio {_messages}", key, HttpStatusCode.OK),
                Kind.Analysis => await Formal(pm, $"/api/projects/{_project}/analyses", null, key, HttpStatusCode.OK),
                _ => await Formal(pm, $"/api/projects/{_project}/analyses/{_analysis}/document-generations", new { revisionId = _revision }, key, HttpStatusCode.OK),
            };
        }

        private async Task<Guid> PostMessage(HttpClient pm, string text, Guid? clientId = null, HttpStatusCode expected = HttpStatusCode.Created)
        {
            var response = await pm.PostAsJsonAsync($"/api/projects/{_project}/messages", new { clientMessageId = clientId ?? Guid.NewGuid(), text }, Ct);
            Assert.Equal(expected, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("chatTurnId").GetGuid();
        }

        private static async Task<Guid> Formal(HttpClient pm, string url, object? body, Guid key, HttpStatusCode expected)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            request.Headers.Add("Idempotency-Key", key.ToString());
            var response = await pm.SendAsync(request, Ct);
            Assert.Equal(expected, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        }

        /// <summary>Quello che l'utente vede: lo stato letto dalle API pubbliche (turno o run).</summary>
        public async Task<string> PublicStatus(Guid work)
        {
            using var pm = await App.LoginAsync();
            var url = kind == Kind.Chat ? $"/api/projects/{_project}/chat-turns/{work}" : $"/api/projects/{_project}/processing-runs/{work}";
            var response = await pm.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString()!;
        }

        // ------------------------------------------------------------ database

        public async Task<Row> RowOf(Guid work)
        {
            await using var connection = await Open();
            await using var command = new NpgsqlCommand(
                $"""
                SELECT "Status", "AttemptCount", "MaxAttempts", "FencingToken", "LeaseOwner", "LeaseUntil", "NextAttemptAt", "DeadlineAt",
                       "RequestedAt", "StartedAt", "CompletedAt", "ErrorCode", "ErrorMessage"
                FROM "{Table}" WHERE "Id" = @id
                """, connection);
            command.Parameters.AddWithValue("id", work);
            await using var r = await command.ExecuteReaderAsync(Ct);
            Assert.True(await r.ReadAsync(Ct));
            return new(
                r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4), Time(r, 5), Time(r, 6),
                r.GetFieldValue<DateTimeOffset>(7), r.GetFieldValue<DateTimeOffset>(8), Time(r, 9), Time(r, 10),
                r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12));

            static DateTimeOffset? Time(NpgsqlDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetFieldValue<DateTimeOffset>(i);
        }

        public Task ExpireBackoff(Guid work) =>
            Exec($"""UPDATE "{Table}" SET "NextAttemptAt" = clock_timestamp() - interval '1 second' WHERE "Id" = @id""", ("id", work));

        public async Task<DateTimeOffset> DatabaseNow() =>
            new(DateTime.SpecifyKind(await Scalar<DateTime>("SELECT clock_timestamp() AT TIME ZONE 'UTC'"), DateTimeKind.Utc));

        /// <summary>Risultati prodotti dal lavoro: risposta assistant del turno, revisione dell'analisi, bozza del run.</summary>
        public Task<long> ResultCount(Guid work) => kind switch
        {
            Kind.Chat => Scalar<long>("""SELECT count(*) FROM "ConversationMessages" WHERE "ChatTurnId" = @id""", ("id", work)),
            Kind.Analysis => Scalar<long>("""SELECT count(*) FROM "AnalysisRevisions" WHERE "AnalysisId" = @id""", ("id", work)),
            _ => Scalar<long>("""SELECT count(*) FROM "Drafts" WHERE "ProcessingRunId" = @id""", ("id", work)),
        };

        /// <summary>
        /// Tutto cio' che un fallimento non deve toccare: messaggi originali e risposte, revisioni (anteprima), bozze e puntatori del
        /// progetto, come JSON dei record veri.
        /// </summary>
        public Task<string> Snapshot() => Scalar<string>(
            """
            SELECT (SELECT coalesce(jsonb_agg(to_jsonb(m) ORDER BY m."Sequence"), '[]') FROM "ConversationMessages" m)::text
                || (SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY r."AnalysisId", r."Version"), '[]') FROM "AnalysisRevisions" r)::text
                || (SELECT coalesce(jsonb_agg(to_jsonb(d) ORDER BY d."CreatedAt", d."Id"), '[]') FROM "Drafts" d)::text
                || (SELECT coalesce(jsonb_agg(jsonb_build_array(p."CurrentAnalysisId", p."LatestDraftId") ORDER BY p."Id"), '[]') FROM "Projects" p)::text
            """);

        public Task<long> Count(string sql) => Scalar<long>(sql);

        public async Task Exec(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await Open();
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync(Ct);
        }

        public async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await Open();
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return (T)(await command.ExecuteScalarAsync(Ct))!;
        }

        private async Task<NpgsqlConnection> Open()
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            return connection;
        }
    }

    // ---------------------------------------------------------------- server del modello finto

    /// <summary>
    /// Il server interno (llama.cpp OpenAI-compatibile) finto, dietro il vero client: risposte nominali costruite dalla richiesta,
    /// errori HTTP/di trasporto in coda (<see cref="Faults"/>) e conteggio delle richieste di completamento realmente arrivate.
    /// </summary>
    private sealed class FakeDgx : HttpMessageHandler
    {
        public const string Secret = "segreto-del-server";

        private int _completions;

        public ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> Faults { get; } = new();

        public Func<string, string> Reply { get; set; } = _ => "Risposta del modello";

        /// <summary>Eseguito a ogni richiesta di completamento, prima dell'errore in coda o della risposta.</summary>
        public Func<CancellationToken, Task>? OnCompletion { get; set; }

        /// <summary>Richieste di completamento ricevute dopo la preparazione dei risultati precedenti.</summary>
        public int Completions => Volatile.Read(ref _completions);

        public void ResetCount() => Volatile.Write(ref _completions, 0);

        public static Func<HttpRequestMessage, HttpResponseMessage> Status(int status, string? retryAfter = null) => _ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("{\"error\":{\"message\":\"" + Secret + "\",\"type\":\"server_error\"}}", Encoding.UTF8, "application/json"),
            };
            if (retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return response;
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/tokenize", StringComparison.Ordinal))
            {
                return Json("""{"tokens":[1,2,3,4,5]}""");
            }

            Interlocked.Increment(ref _completions);
            if (OnCompletion is not null)
            {
                await OnCompletion(cancellationToken);
            }

            if (Faults.TryDequeue(out var fault))
            {
                return fault(request);
            }

            var text = Reply(UserText(body));
            return Regex.IsMatch(body, "\"stream\"\\s*:\\s*true") ? Stream(text) : Json(Completion(text));
        }

        private static string UserText(string body)
        {
            using var json = JsonDocument.Parse(body);
            var parts = new List<string>();
            foreach (var message in json.RootElement.GetProperty("messages").EnumerateArray())
            {
                if (message.GetProperty("role").GetString() is "system" or "developer")
                {
                    continue;
                }

                var content = message.GetProperty("content");
                parts.Add(content.ValueKind == JsonValueKind.String
                    ? content.GetString()!
                    : string.Concat(content.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : "")));
            }

            return string.Join("\n", parts);
        }

        private static string Completion(string text) => JsonSerializer.Serialize(new
        {
            id = "chatcmpl-test",
            @object = "chat.completion",
            created = 1,
            model = "test-model",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
        });

        private static HttpResponseMessage Stream(string text)
        {
            string Chunk(object delta, string? finish) => "data: " + JsonSerializer.Serialize(new
            {
                id = "chatcmpl-test",
                @object = "chat.completion.chunk",
                created = 1,
                model = "test-model",
                choices = new[] { new { index = 0, delta, finish_reason = finish } },
            }) + "\n\n";

            var sse = Chunk(new { role = "assistant", content = text }, null) + Chunk(new { }, "stop") + "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
