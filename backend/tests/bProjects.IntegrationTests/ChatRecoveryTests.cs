using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T044 - recovery della chat ai confini di commit, con PostgreSQL reale e piu' "processi" applicativi distinti.
/// Ogni processo e' un'applicazione completa (dispatcher ospitato, esecutore e commit reali, owner di lease proprio,
/// connessioni proprie) che condivide soltanto il database; il crash e' lo smaltimento dell'applicazione a meta'
/// esecuzione, quindi il nuovo processo puo' leggere solo PostgreSQL. Il modello e' un fake scriptabile: l'unico
/// punto di iniezione e' il modello (e, per fermare il lavoro ai confini di commit, un decoratore del committer
/// registrato solo nell'harness). Una chiamata remota interrotta puo' ripetersi: non si pretende ripresa dal token
/// ne' alcun checkpoint intra-chiamata, e le prove lo verificano (nessun WorkflowCheckpoint per i ChatTurn).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ChatRecoveryTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly PostgresDatabaseFixture _database = new();
    private readonly List<ApiHarness> _processes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            // Migrazioni e account una sola volta; i "processi" delle prove partono poi senza preparare nulla.
            await using var setup = await ApiHarness.StartAsync(_database.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            await process.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    // ---------------------------------------------------------------- crash prima del commit della risposta

    [Fact]
    public async Task A_crash_before_the_reply_commit_is_recovered_by_a_new_process_with_one_reply_and_untouched_originals()
    {
        TestEnvironment.RequirePostgres();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dying = new ScriptedModel(_ => "Risposta del processo morto")
        {
            Before = async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // inferenza in corso quando il processo muore
            },
        };
        var first = await StartProcess(dying);
        var projectId = await CreateProject(first);
        var turn = await Post(first, projectId, "Voglio un portale clienti");
        await entered.Task.WaitAsync(Patience, Ct);
        var originals = await Originals();
        var running = await TurnOf(turn);
        Assert.Equal("Running", running.Status);
        Assert.Equal((1, 1L), (running.Attempts, running.Fence));

        await Kill(first);

        // Nulla e' stato pubblicato e il turno e' rimasto abbandonato con il lease del morto.
        Assert.Empty(await Assistants());
        Assert.Equal(running.Owner, (await TurnOf(turn)).Owner);
        await ExpireLease(turn);

        var recovering = new ScriptedModel(_ => "Risposta dopo il recovery");
        await StartProcess(recovering);
        await WaitCompleted(turn);

        var done = await TurnOf(turn);
        Assert.Equal((2, 2L), (done.Attempts, done.Fence)); // reclaim: nuovo attempt e nuovo fence
        Assert.Null(done.ErrorCode);
        var reply = Assert.Single(await Assistants());
        Assert.Equal("Risposta dopo il recovery", reply.Body);
        Assert.Equal(done.ResultMessageId, reply.Id);
        Assert.Equal(1, recovering.Calls); // l'inferenza interrotta si ripete una volta, senza ripresa dal token
        await AssertOriginalsUnchanged(originals);
        await AssertNoChatCheckpoints();
    }

    [Fact]
    public async Task A_worker_whose_lease_was_reclaimed_cannot_commit_even_if_its_model_call_finishes()
    {
        TestEnvironment.RequirePostgres();
        var oldReachedCommit = new TaskCompletionSource<ChatTurnClaim>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldMayCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldHooks = new CommitHooks
        {
            // Il vecchio worker ha gia' la risposta ma e' fermo prima del commit; non onora la cancellazione di proposito:
            // la chiamata remota puo' finire comunque dopo la perdita del lease.
            Before = async (claim, _) =>
            {
                oldReachedCommit.TrySetResult(claim);
                await oldMayCommit.Task;
            },
            CommitIgnoresCancellation = true,
        };
        var old = await StartProcess(new ScriptedModel(_ => "Risposta del vecchio worker"), oldHooks);
        var projectId = await CreateProject(old);
        var turn = await Post(old, projectId, "Voglio un portale clienti");
        var oldClaim = await oldReachedCommit.Task.WaitAsync(Patience, Ct);
        var originals = await Originals();

        await ExpireLease(turn); // il lease del primo owner scade: un secondo processo lo riacquisisce
        var newReachedCommit = new TaskCompletionSource<ChatTurnClaim>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newMayCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartProcess(new ScriptedModel(_ => "Risposta del nuovo worker"), new CommitHooks
        {
            Before = async (claim, ct) =>
            {
                newReachedCommit.TrySetResult(claim);
                await newMayCommit.Task.WaitAsync(ct);
            },
        });
        var newClaim = await newReachedCommit.Task.WaitAsync(Patience, Ct);

        Assert.NotEqual(oldClaim.LeaseOwner, newClaim.LeaseOwner);
        Assert.Equal(oldClaim.FencingToken + 1, newClaim.FencingToken);
        Assert.Equal(oldClaim.AttemptCount + 1, newClaim.AttemptCount);

        // Il vecchio worker termina dopo il reclaim con il vecchio fence: il commit e' rifiutato e non scrive nulla.
        oldMayCommit.SetResult();
        var stale = await FirstResult(oldHooks);
        Assert.Equal(ChatTurnCommitOutcome.Rejected, stale.Outcome);
        Assert.Equal("lease_lost", stale.Reason);
        Assert.Empty(await Assistants());
        var stillRunning = await TurnOf(turn);
        Assert.Equal("Running", stillRunning.Status);
        Assert.Equal(newClaim.LeaseOwner, stillRunning.Owner);
        Assert.Equal(newClaim.FencingToken, stillRunning.Fence);

        // Il worker corrente committa: una sola risposta, quella del fence valido; il vecchio worker non cambia l'esito.
        newMayCommit.SetResult();
        await WaitCompleted(turn);
        var reply = Assert.Single(await Assistants());
        Assert.Equal("Risposta del nuovo worker", reply.Body);
        var done = await TurnOf(turn);
        Assert.Equal(reply.Id, done.ResultMessageId);
        Assert.Null(done.ErrorCode);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct); // il dispatcher vecchio tenta Failed con fence obsoleto: deve restare rifiutato
        Assert.Equal("Completed", (await TurnOf(turn)).Status);
        Assert.Single(await Assistants());
        await AssertOriginalsUnchanged(originals);
    }

    // ---------------------------------------------------------------- lease rinnovato e lease scaduto

    [Fact]
    public async Task A_renewed_lease_is_not_reclaimed_by_another_process_until_it_expires()
    {
        TestEnvironment.RequirePostgres();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await StartProcess(new ScriptedModel(_ => "mai pubblicata")
        {
            Before = async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
        });
        var projectId = await CreateProject(first);
        var turn = await Post(first, projectId, "Voglio un portale clienti");
        await entered.Task.WaitAsync(Patience, Ct);
        var claimedUntil = await LeaseUntil(turn);
        var deadline = (await TurnOf(turn)).DeadlineAt;

        // Il rinnovo del dispatcher (1 s nell'harness) estende il lease oltre quello iniziale, mai la deadline.
        await Eventually(async () => await LeaseUntil(turn) > claimedUntil.AddSeconds(2), "lease rinnovato");
        Assert.Equal(deadline, (await TurnOf(turn)).DeadlineAt);

        var secondModel = new ScriptedModel(_ => "Risposta del secondo processo");
        var second = await StartProcess(secondModel);
        var secondDispatcher = Dispatcher(second);
        Assert.False(await secondDispatcher.RunOnceAsync(Ct)); // lease valido: nessun reclaim
        var owned = await TurnOf(turn);
        Assert.Equal(("Running", 1, 1L), (owned.Status, owned.Attempts, owned.Fence));

        await Kill(first);
        Assert.False(await secondDispatcher.RunOnceAsync(Ct)); // il lease del morto protegge il turno finche' non scade
        Assert.Equal(0, secondModel.Calls);

        await ExpireLease(turn);
        await WaitCompleted(turn);
        var done = await TurnOf(turn);
        Assert.Equal((2, 2L), (done.Attempts, done.Fence));
        Assert.Single(await Assistants());
    }

    // ---------------------------------------------------------------- crash dopo il commit

    [Fact]
    public async Task A_crash_after_the_commit_keeps_the_same_reply_and_a_new_process_regenerates_nothing()
    {
        TestEnvironment.RequirePostgres();
        var committed = new TaskCompletionSource<(ChatTurnClaim Claim, ConversationalReply Reply, ChatTurnCommitResult Result)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dyingModel = new ScriptedModel(_ => "Chi lo usera?");
        var first = await StartProcess(dyingModel, new CommitHooks
        {
            CommitIgnoresCancellation = true,
            After = async (claim, reply, result, ct) =>
            {
                committed.TrySetResult((claim, reply, result));
                await Task.Delay(Timeout.Infinite, ct); // il processo muore prima di notificare l'esito (SSE) o di tornare al dispatcher
            },
        });
        var projectId = await CreateProject(first);
        var turn = await Post(first, projectId, "Voglio un portale clienti");
        var (oldClaim, oldReply, result) = await committed.Task.WaitAsync(Patience, Ct);
        Assert.Equal(ChatTurnCommitOutcome.Committed, result.Outcome);
        var published = Assert.Single(await Assistants());

        await Kill(first);

        var secondModel = new ScriptedModel(_ => "Rigenerata");
        var second = await StartProcess(secondModel);
        Assert.False(await Dispatcher(second).RunOnceAsync(Ct)); // nessun lavoro da riprendere
        var done = await TurnOf(turn);
        Assert.Equal(("Completed", 1, published.Id), (done.Status, done.Attempts, done.ResultMessageId));
        Assert.Equal(0, secondModel.Calls);

        // Il nuovo processo riconcilia dal solo database lo stesso ResultMessageId e anche un commit ripetuto e' idempotente.
        using (var scope = second.Services.CreateScope())
        {
            var completion = (ChatTurnCompletion)scope.ServiceProvider.GetRequiredService<IChatTurnCommitter>();
            var reconciled = await completion.ReconcileAsync(turn, Ct);
            Assert.Equal((ChatTurnCommitOutcome.AlreadyCompleted, (Guid?)published.Id), (reconciled!.Outcome, reconciled.ResultMessageId));
            var replay = await completion.CommitAsync(oldClaim, oldReply, Ct);
            Assert.Equal((ChatTurnCommitOutcome.AlreadyCompleted, (Guid?)published.Id), (replay.Outcome, replay.ResultMessageId));
        }

        using var client = await second.LoginAsync();
        var view = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/chat-turns/{turn}", Ct);
        Assert.Equal("Completed", view.GetProperty("status").GetString());
        Assert.Equal(published.Id, view.GetProperty("resultMessageId").GetGuid());
        var after = Assert.Single(await Assistants());
        Assert.Equal(published, after);
        await AssertNoChatCheckpoints();
    }

    // ---------------------------------------------------------------- crash dopo il commit del PM, prima dell'inferenza

    [Fact]
    public async Task The_original_and_the_turn_survive_an_offline_model_and_a_new_process_retries_only_after_the_backoff()
    {
        TestEnvironment.RequirePostgres();
        var offline = new ScriptedModel(_ => "mai")
        {
            Before = (_, _) => throw new HttpRequestException("modello non raggiungibile"),
        };
        var first = await StartProcess(offline, retryBaseSeconds: 8);
        var projectId = await CreateProject(first);
        var turn = await Post(first, projectId, "Voglio un portale clienti");
        await Eventually(async () => (await TurnOf(turn)) is { Status: "Queued", Attempts: 1, NextAttemptAt: not null }, "turno rimesso in coda con backoff");
        await Kill(first);
        var originals = await Originals();

        // Dopo il crash: originale e ChatTurn persistono, nessuna risposta, contatore e backoff scritti nel database.
        Assert.Single(originals);
        Assert.Empty(await Assistants());
        var queued = await TurnOf(turn);
        Assert.Equal(("Queued", 1), (queued.Status, queued.Attempts));
        Assert.True(queued.NextAttemptAt > await DatabaseNow(), "il backoff non e' ancora trascorso");

        var online = new ScriptedModel(_ => "Risposta dopo il retry");
        var second = await StartProcess(online);
        Assert.False(await Dispatcher(second).RunOnceAsync(Ct)); // backoff non trascorso: nessun claim anticipato
        Assert.Equal(0, online.Calls);
        await WaitCompleted(turn);

        var done = await TurnOf(turn);
        Assert.Equal(2, done.Attempts); // il contatore non si e' azzerato con il restart
        Assert.True(done.CompletedAt >= queued.NextAttemptAt, "il retry non puo' concludersi prima di NextAttemptAt");
        Assert.Equal("Risposta dopo il retry", Assert.Single(await Assistants()).Body);
        await AssertOriginalsUnchanged(originals);
        await AssertNoChatCheckpoints();
    }

    // ---------------------------------------------------------------- FIFO in backoff, cutoff U1/U2/A1

    [Fact]
    public async Task A_turn_in_backoff_blocks_the_next_one_and_after_a_restart_U2_is_rebuilt_from_U1_and_U2_without_A1()
    {
        TestEnvironment.RequirePostgres();
        var offline = new ScriptedModel(_ => "mai") { Before = (_, _) => throw new HttpRequestException("modello non raggiungibile") };
        var first = await StartProcess(offline, retryBaseSeconds: 8);
        var projectId = await CreateProject(first);
        var t1 = await Post(first, projectId, "U1: vorrei un portale clienti");
        var t2 = await Post(first, projectId, "U2: lo useranno gli agenti");
        await Eventually(async () => (await TurnOf(t1)) is { Status: "Queued", Attempts: 1, NextAttemptAt: not null }, "T1 in backoff");

        // T2 e' piu' recente ma non sorpassa T1 in backoff: nemmeno un altro processo puo' prenderlo.
        Assert.Equal(("Queued", 0), ((await TurnOf(t2)).Status, (await TurnOf(t2)).Attempts));
        using (var scope = first.Services.CreateScope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextAsync("sonda", Ct));
        }

        await Kill(first);
        var originals = await Originals();
        var model = new ScriptedModel(n => n == 1 ? "A1: chi sono gli agenti?" : "A2: grazie");
        var second = await StartProcess(model);
        Assert.False(await Dispatcher(second).RunOnceAsync(Ct)); // T1 ancora in backoff, T2 bloccato dalla FIFO
        Assert.Equal(0, model.Calls);
        await WaitCompleted(t2);

        var turn1 = await TurnOf(t1);
        var turn2 = await TurnOf(t2);
        Assert.Equal((2, 1), (turn1.Attempts, turn2.Attempts));
        Assert.Equal((1L, 2L), (turn1.Cutoff, turn2.Cutoff)); // cutoff = sequenza del trigger
        Assert.True(turn1.CompletedAt <= turn2.StartedAt, "T2 parte dopo la conclusione di T1");

        var replies = await Assistants();
        Assert.Equal([3L, 4L], replies.Select(r => r.Sequence).ToArray()); // U1=1, U2=2, A1=3, A2=4
        Assert.Equal(["A1: chi sono gli agenti?", "A2: grazie"], replies.Select(r => r.Body).ToArray());
        Assert.Equal([originals[0].Id, originals[1].Id], replies.Select(r => r.ReplyTo).ToArray());

        // Quello che il modello ha davvero ricevuto: T1 solo U1; T2 solo U1 e U2 (trigger una volta), mai A1.
        var requests = model.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(["U1: vorrei un portale clienti"], requests[0]);
        Assert.Equal(["U1: vorrei un portale clienti", "U2: lo useranno gli agenti"], requests[1]);
        Assert.DoesNotContain(requests[1], text => text.Contains("A1", StringComparison.Ordinal));

        // La sessione committata da T2 e' derivata dagli originali ammessi: U1, U2 e la risposta A2 (A1 e' esclusa).
        var snapshot = await Snapshot();
        Assert.Equal([1L, 2L, 4L], snapshot.Messages.Select(m => m.Sequence).ToArray());
        await AssertOriginalsUnchanged(originals);
        await AssertNoChatCheckpoints();
    }

    // ---------------------------------------------------------------- snapshot incompatibile dopo il restart

    [Theory]
    [InlineData("maf_version")]
    [InlineData("wrong_reference")]
    [InlineData("unreadable")]
    public async Task An_incompatible_snapshot_found_by_a_new_process_is_rebuilt_from_the_originals_with_one_reply(string defect)
    {
        TestEnvironment.RequirePostgres();
        var first = await StartProcess(new ScriptedModel(_ => "A1: prima risposta"));
        var projectId = await CreateProject(first);
        var t1 = await Post(first, projectId, "U1");
        await WaitCompleted(t1);
        await Kill(first);
        await CorruptSnapshot(defect);
        var beforeT2 = await Originals();

        var model = new ScriptedModel(_ => "A2: seconda risposta");
        var second = await StartProcess(model);
        var t2 = await Post(second, projectId, "U2");
        await WaitCompleted(t2);

        // Ricostruita dagli originali ammessi dal cutoff (U1, A1, U2), senza usare nulla dello snapshot non valido.
        Assert.Equal(["U1", "A1: prima risposta", "U2"], Assert.Single(model.Requests));
        var replies = await Assistants();
        Assert.Equal(["A1: prima risposta", "A2: seconda risposta"], replies.Select(r => r.Body).ToArray());
        var snapshot = await Snapshot();
        Assert.Equal([1L, 2L, 3L, 4L], snapshot.Messages.Select(m => m.Sequence).ToArray());
        Assert.Equal("1.26.0", await Scalar<string>("""SELECT "MafVersion" FROM "Conversations" """));
        await AssertOriginalsUnchanged(beforeT2);
        await AssertNoChatCheckpoints();
    }

    // ---------------------------------------------------------------- due processi concorrenti

    [Fact]
    public async Task Two_live_processes_on_one_database_publish_one_reply_per_turn_in_fifo_order()
    {
        TestEnvironment.RequirePostgres();
        var modelA = new ScriptedModel(n => $"Risposta A{n}") { Before = (_, _) => Task.Delay(300, Ct) };
        var modelB = new ScriptedModel(n => $"Risposta B{n}") { Before = (_, _) => Task.Delay(300, Ct) };
        var a = await StartProcess(modelA);
        await StartProcess(modelB);
        var projectId = await CreateProject(a);
        var turns = new List<Guid>();
        for (var i = 1; i <= 4; i++)
        {
            turns.Add(await Post(a, projectId, $"Messaggio {i}"));
        }

        await WaitCompleted(turns[^1]);

        var views = new List<TurnRow>();
        foreach (var turn in turns)
        {
            await WaitCompleted(turn);
            views.Add(await TurnOf(turn));
        }

        Assert.All(views, v => Assert.Equal(1, v.Attempts)); // nessun doppio claim
        Assert.Equal(4, modelA.Calls + modelB.Calls);        // una sola inferenza per turno
        var replies = await Assistants();
        Assert.Equal(4, replies.Count);
        Assert.Equal(turns.Select(t => (Guid?)t).ToArray(), replies.Select(r => r.ChatTurnId).ToArray()); // una risposta per turno, nello stesso ordine
        Assert.Equal(views.Select(v => v.ResultMessageId).ToArray(), replies.Select(r => (Guid?)r.Id).ToArray());
        for (var i = 1; i < views.Count; i++)
        {
            Assert.True(views[i - 1].CompletedAt <= views[i].StartedAt, "mai due turni della stessa chat sovrapposti");
        }
    }

    // ================================================================ supporto

    private async Task<ApiHarness> StartProcess(ScriptedModel model, CommitHooks? hooks = null, int retryBaseSeconds = 3)
    {
        var process = await ApiHarness.StartAsync(_database.ConnectionString, prepare: false, configure: services =>
        {
            services.AddSingleton(ConversationalAgentTests.Agent(model));
            services.AddSingleton<IChatTurnExecutor, ConversationalTurnExecutor>();
            services.Configure<ExecutionOptions>(o =>
            {
                o.RenewIntervalSeconds = 1;                // il rinnovo reale e' piu' frequente solo per accorciare le prove
                o.RetryBaseDelaySeconds = retryBaseSeconds;
            });
            if (hooks is not null)
            {
                services.AddScoped<ChatTurnCompletion>();
                services.RemoveAll<IChatTurnCommitter>();
                services.AddScoped<IChatTurnCommitter>(sp => new HookedCommitter(sp.GetRequiredService<ChatTurnCompletion>(), hooks));
            }
        });
        _processes.Add(process);
        _ = process.Services; // avvia il dispatcher ospitato
        return process;
    }

    /// <summary>Il "crash": l'applicazione sparisce a meta' esecuzione senza rilasciare nulla; il database conserva lo stato.</summary>
    private async Task Kill(ApiHarness process)
    {
        _processes.Remove(process);
        await process.DisposeAsync();
    }

    private static AiDispatchService Dispatcher(ApiHarness process) =>
        process.Services.GetServices<IHostedService>().OfType<AiDispatchService>().Single();

    private sealed class CommitHooks
    {
        public Func<ChatTurnClaim, CancellationToken, Task>? Before { get; init; }

        public Func<ChatTurnClaim, ConversationalReply, ChatTurnCommitResult, CancellationToken, Task>? After { get; init; }

        public bool CommitIgnoresCancellation { get; init; }

        public ConcurrentQueue<ChatTurnCommitResult> Results { get; } = new();
    }

    /// <summary>Decoratore del committer reale (stessa transazione, stessi controlli): ferma il worker prima o dopo il commit.</summary>
    private sealed class HookedCommitter(ChatTurnCompletion inner, CommitHooks hooks) : IChatTurnCommitter
    {
        public async Task<ChatTurnCommitResult> CommitAsync(ChatTurnClaim claim, ConversationalReply reply, CancellationToken ct)
        {
            if (hooks.Before is not null)
            {
                await hooks.Before(claim, ct);
            }

            var result = await inner.CommitAsync(claim, reply, hooks.CommitIgnoresCancellation ? CancellationToken.None : ct);
            hooks.Results.Enqueue(result);
            if (hooks.After is not null)
            {
                await hooks.After(claim, reply, result, ct);
            }

            return result;
        }
    }

    private static async Task<ChatTurnCommitResult> FirstResult(CommitHooks hooks)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (hooks.Results.TryPeek(out var result))
            {
                return result;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("Il commit del worker non e' terminato.");
    }

    /// <summary>Modello scriptabile: registra ogni richiesta (senza il prompt di sistema) e risponde in streaming.</summary>
    private sealed class ScriptedModel(Func<int, string> reply) : IChatClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ConcurrentQueue<string[]> Requests { get; } = new();

        public Func<int, CancellationToken, Task>? Before { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("La chat usa solo lo streaming.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            Requests.Enqueue(messages.Where(m => m.Role != ChatRole.System).Select(m => m.Text).ToArray());
            await Task.Yield();
            if (Before is not null)
            {
                await Before(call, cancellationToken);
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, reply(call));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    // ---------------------------------------------------------------- HTTP

    private static async Task<Guid> CreateProject(ApiHarness process)
    {
        using var client = await process.LoginAsync();
        var created = await client.PostAsJsonAsync("/api/projects", new { name = $"Recovery {Guid.NewGuid():N}" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> Post(ApiHarness process, Guid projectId, string text)
    {
        using var client = await process.LoginAsync();
        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("chatTurnId").GetGuid();
    }

    // ---------------------------------------------------------------- database

    private sealed record TurnRow(
        string Status, int Attempts, long Fence, string? Owner, long Cutoff, DateTimeOffset DeadlineAt,
        DateTimeOffset? NextAttemptAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, Guid? ResultMessageId, string? ErrorCode);

    private sealed record OriginalRow(Guid Id, long Sequence, string Role, string Body, DateTimeOffset CreatedAt, Guid? ReplyTo, Guid? ChatTurnId);

    private async Task<TurnRow> TurnOf(Guid turn)
    {
        await using var connection = await Open();
        await using var command = new NpgsqlCommand(
            """
            SELECT "Status", "AttemptCount", "FencingToken", "LeaseOwner", "SourceCutoffSequence", "DeadlineAt", "NextAttemptAt",
                   "StartedAt", "CompletedAt", "ResultMessageId", "ErrorCode"
            FROM "ChatTurns" WHERE "Id" = @t
            """, connection);
        command.Parameters.AddWithValue("t", turn);
        await using var r = await command.ExecuteReaderAsync(Ct);
        Assert.True(await r.ReadAsync(Ct));
        return new(
            r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4),
            r.GetFieldValue<DateTimeOffset>(5), Nullable<DateTimeOffset>(r, 6), Nullable<DateTimeOffset>(r, 7), Nullable<DateTimeOffset>(r, 8),
            r.IsDBNull(9) ? null : r.GetGuid(9), r.IsDBNull(10) ? null : r.GetString(10));

        static DateTimeOffset? Nullable<T>(NpgsqlDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetFieldValue<DateTimeOffset>(i);
    }

    private async Task<List<OriginalRow>> Messages(string? role)
    {
        await using var connection = await Open();
        await using var command = new NpgsqlCommand(
            """
            SELECT "Id", "Sequence", "Role", "Body", "CreatedAt", "ReplyToMessageId", "ChatTurnId"
            FROM "ConversationMessages" WHERE (@role::text IS NULL OR "Role" = @role) ORDER BY "Sequence"
            """, connection);
        command.Parameters.AddWithValue("role", (object?)role ?? DBNull.Value);
        var rows = new List<OriginalRow>();
        await using var r = await command.ExecuteReaderAsync(Ct);
        while (await r.ReadAsync(Ct))
        {
            rows.Add(new(r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4),
                r.IsDBNull(5) ? null : r.GetGuid(5), r.IsDBNull(6) ? null : r.GetGuid(6)));
        }

        return rows;
    }

    private Task<List<OriginalRow>> Originals() => Messages("User");

    private Task<List<OriginalRow>> Assistants() => Messages("Assistant");

    /// <summary>Gli originali (utente e risposte gia' pubblicate) non sono mai modificati, riordinati o cancellati da recovery e retry.</summary>
    private async Task AssertOriginalsUnchanged(List<OriginalRow> before)
    {
        var now = await Messages(null);
        foreach (var original in before)
        {
            Assert.Contains(original, now);
        }
    }

    private async Task AssertNoChatCheckpoints() =>
        Assert.Equal(0L, await Scalar<long>("""SELECT count(*) FROM "WorkflowCheckpoints" """));

    private async Task<ChatSessionEnvelope> Snapshot() =>
        JsonSerializer.Deserialize<ChatSessionEnvelope>(await Scalar<string>("""SELECT "MafChatSessionState"::text FROM "Conversations" """))!;

    private async Task CorruptSnapshot(string defect)
    {
        switch (defect)
        {
            case "maf_version":
                await Exec("""UPDATE "Conversations" SET "MafVersion" = '0.0.1'""");
                break;
            case "wrong_reference":
                var envelope = await Snapshot();
                envelope.Messages[0] = envelope.Messages[0] with { Id = Guid.NewGuid() };
                await Exec("""UPDATE "Conversations" SET "MafChatSessionState" = CAST(@s AS jsonb)""", ("s", JsonSerializer.Serialize(envelope)));
                break;
            case "unreadable":
                await Exec("""UPDATE "Conversations" SET "MafChatSessionState" = '{"nope":1}'::jsonb""");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }
    }

    private Task ExpireLease(Guid turn) =>
        Exec("""UPDATE "ChatTurns" SET "LeaseUntil" = clock_timestamp() - interval '1 second' WHERE "Id" = @t""", ("t", turn));

    private async Task<DateTimeOffset> LeaseUntil(Guid turn) =>
        new(DateTime.SpecifyKind(await Scalar<DateTime>("""SELECT "LeaseUntil" AT TIME ZONE 'UTC' FROM "ChatTurns" WHERE "Id" = @t""", ("t", turn)), DateTimeKind.Utc));

    private async Task<DateTimeOffset> DatabaseNow() =>
        new(DateTime.SpecifyKind(await Scalar<DateTime>("SELECT clock_timestamp() AT TIME ZONE 'UTC'"), DateTimeKind.Utc));

    private async Task WaitCompleted(Guid turn)
    {
        await Eventually(async () =>
        {
            var row = await TurnOf(turn);
            Assert.NotEqual("Failed", row.Status);
            return row.Status == "Completed";
        }, "turno Completed");
    }

    private static async Task Eventually(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(200, Ct);
        }

        Assert.Fail($"Condizione non raggiunta entro il tempo previsto: {what}.");
    }

    private async Task<NpgsqlConnection> Open()
    {
        var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task Exec(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Open();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Open();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
