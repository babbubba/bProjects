using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// Pianificazione delle richieste AI con un solo slot (<c>parallel=1</c>), su PostgreSQL reale e senza modello: la chat precede le
/// estrazioni automatiche a ogni claim; un lavoro già avviato non viene mai interrotto; un'estrazione non prende il posto di un turno
/// o run in backoff di retry imminente; FIFO per conversazione; il gate di Analizza guarda solo la propria conversazione e il proprio
/// cutoff e non attende oltre la deadline del run; il segnale di risveglio del dispatcher non è né una coda né un polling.
/// Ogni prova usa un database nuovo: il limite AI globale rende gli stati interdipendenti.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class AiSchedulingTests : IAsyncLifetime
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

    // ---------------------------------------------------------------- segnale di risveglio

    [Fact]
    public async Task The_wakeup_signal_is_consumed_once_coalesces_repeated_notifications_and_times_out_without_a_signal()
    {
        var wakeup = new DispatchWakeup();
        Assert.False(await wakeup.WaitAsync(TimeSpan.FromMilliseconds(20), Ct)); // nessun segnale: attesa limitata, mai bloccante

        wakeup.Notify();
        wakeup.Notify();
        wakeup.Notify();
        Assert.True(await wakeup.WaitAsync(TimeSpan.FromSeconds(5), Ct));          // tre notifiche = un solo risveglio
        Assert.False(await wakeup.WaitAsync(TimeSpan.FromMilliseconds(20), Ct));   // niente coda di segnali

        var waiting = wakeup.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(waiting.IsCompleted);
        wakeup.Notify();
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct));          // risveglio immediato di chi attende
    }

    [Fact]
    public async Task Saving_a_message_signals_the_dispatcher_without_waiting_for_the_extractor()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        var wakeup = App.Services.GetRequiredService<DispatchWakeup>();
        Assert.False(await wakeup.WaitAsync(TimeSpan.Zero, Ct));

        await PostMessage(client, project, "Il portale invia un promemoria.");

        Assert.True(await wakeup.WaitAsync(TimeSpan.Zero, Ct));                     // segnale già arrivato alla risposta 201
        Assert.Equal("Queued", await Scalar<string>("SELECT \"Status\" FROM \"EvidenceExtractionJobs\" WHERE \"ProjectId\" = @p", ("p", project)));
        Assert.Equal("Queued", await Scalar<string>("""
            SELECT t."Status" FROM "ChatTurns" t JOIN "Conversations" c ON c."Id" = t."ConversationId" WHERE c."ProjectId" = @p
            """, ("p", project)));
    }

    // ---------------------------------------------------------------- priorità e finestra di backoff

    [Fact]
    public async Task An_extraction_never_takes_the_slot_of_a_turn_whose_retry_is_imminent_and_never_interrupts_running_work()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Il portale gestisce i resi.");
        var job = await Scalar<Guid>("SELECT \"Id\" FROM \"EvidenceExtractionJobs\" WHERE \"ProjectId\" = @p", ("p", project));

        // Turno in attesa di retry fra 5 s: l'estrazione non occupa il modello nella finestra, quindi il turno lo trova libero.
        await Execute("""UPDATE "ChatTurns" SET "AttemptCount" = 1, "NextAttemptAt" = clock_timestamp() + interval '5 seconds' WHERE "Status" = 'Queued'""");
        Assert.Null(await Claim("w1"));
        Assert.Equal("Queued", await JobStatus(job));

        // Backoff lontano oltre la finestra (es. Retry-After lungo): l'estrazione non è trattenuta oltre il necessario.
        await Execute("""UPDATE "ChatTurns" SET "NextAttemptAt" = clock_timestamp() + interval '10 minutes' WHERE "Status" = 'Queued'""");
        var extraction = (await Claim("w2"))!.Extraction!;
        Assert.Equal(job, extraction.JobId);

        // Il lavoro già avviato non viene toccato da un turno diventato eleggibile: il fence resta quello del claim.
        await Execute("""UPDATE "ChatTurns" SET "NextAttemptAt" = clock_timestamp() - interval '1 second' WHERE "Status" = 'Queued'""");
        Assert.Null(await Claim("w3", turns: true));
        Assert.Equal("Running", await JobStatus(job));
        Assert.Equal(extraction.FencingToken, await Scalar<long>("SELECT \"FencingToken\" FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = @j", ("j", job)));
    }

    [Fact]
    public async Task A_run_in_short_backoff_holds_extractions_back_and_goes_before_them_when_the_backoff_ends()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Il portale gestisce i resi.");
        var job = await Scalar<Guid>("SELECT \"Id\" FROM \"EvidenceExtractionJobs\" WHERE \"ProjectId\" = @p", ("p", project));
        await FailQueuedTurns();
        await CompleteExtraction((await Claim("seed"))!.Extraction!);               // versione corrente vera: il run non accoda un backfill
        Assert.Equal(job, await Scalar<Guid>("SELECT \"JobId\" FROM \"EvidenceExtractionVersions\" WHERE \"MessageId\" IN (SELECT \"Id\" FROM \"ConversationMessages\" WHERE \"ConversationId\" IN (SELECT \"Id\" FROM \"Conversations\" WHERE \"ProjectId\" = @p))", ("p", project)));

        var other = await CreateProject(client);
        await PostMessage(client, other, "Messaggio di un altro progetto.");
        await FailQueuedTurns();
        var otherJob = await Scalar<Guid>("SELECT \"Id\" FROM \"EvidenceExtractionJobs\" WHERE \"ProjectId\" = @p", ("p", other));

        var run = (await Json(await Analyze(client, project))).GetProperty("id").GetGuid();
        await Execute("""UPDATE "ProcessingRuns" SET "AttemptCount" = 1, "NextAttemptAt" = clock_timestamp() + interval '4 seconds' WHERE "Id" = @r""", ("r", run));
        Assert.Null(await Claim("w1", runs: true));                                  // l'estrazione dell'altro progetto aspetta il retry del run
        Assert.Equal("Queued", await JobStatus(otherJob));

        await Execute("""UPDATE "ProcessingRuns" SET "NextAttemptAt" = clock_timestamp() - interval '1 second' WHERE "Id" = @r""", ("r", run));
        Assert.Equal(run, (await Claim("w2", runs: true))!.Run!.RunId);              // scaduto il backoff il run precede l'estrazione più vecchia
    }

    // ---------------------------------------------------------------- Analizza: gate senza starvation

    [Fact]
    public async Task The_analyze_gate_ignores_other_projects_and_later_messages_and_does_not_wait_past_the_run_deadline()
    {
        using var client = await App.LoginAsync();
        var other = await CreateProject(client);
        await PostMessage(client, other, "Messaggio di un altro progetto, accodato per primo.");
        var project = await CreateProject(client);
        await PostMessage(client, project, "Il portale gestisce i resi.");
        await FailQueuedTurns();
        var otherJob = await Scalar<Guid>("SELECT \"Id\" FROM \"EvidenceExtractionJobs\" WHERE \"ProjectId\" = @p", ("p", other));
        var blocking = await Scalar<Guid>("SELECT \"Id\" FROM \"EvidenceExtractionJobs\" WHERE \"ProjectId\" = @p", ("p", project));

        var run = (await Json(await Analyze(client, project))).GetProperty("id").GetGuid();
        await PostMessage(client, project, "Messaggio dopo il cutoff.");
        await FailQueuedTurns();

        // L'estrazione che serve al cutoff precede quella più vecchia dell'altro progetto e quella oltre il cutoff.
        var first = (await Claim("w1", runs: true))!.Extraction!;
        Assert.Equal(blocking, first.JobId);
        await CompleteExtraction(first);

        // Concluse le estrazioni entro il cutoff il run parte, anche se l'altro progetto e il messaggio successivo hanno lavori aperti.
        Assert.Equal(run, (await Claim("w2", runs: true))!.Run!.RunId);
        Assert.Equal("Queued", await JobStatus(otherJob));

        // Gate chiuso da un'estrazione in backoff: il run non attende per sempre, la deadline lo chiude esplicitamente.
        await Execute("""UPDATE "ProcessingRuns" SET "Status" = 'Failed', "ErrorCode" = 'test', "CompletedAt" = now(), "LeaseOwner" = NULL, "LeaseUntil" = NULL WHERE "Id" = @r""", ("r", run));
        var blockedRun = (await Json(await Analyze(client, other))).GetProperty("id").GetGuid();
        await Execute("""UPDATE "EvidenceExtractionJobs" SET "AttemptCount" = 1, "NextAttemptAt" = clock_timestamp() + interval '1 hour' WHERE "Id" = @j""", ("j", otherJob));
        Assert.Null((await Claim("w3", runs: true))?.Run);                        // al più un'estrazione (oltre il cutoff), mai il run
        Assert.Equal("Queued", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", blockedRun)));
        await Execute("""UPDATE "ProcessingRuns" SET "DeadlineAt" = clock_timestamp() - interval '1 second' WHERE "Id" = @r""", ("r", blockedRun));
        Assert.Null((await Claim("w4", runs: true))?.Run);
        Assert.Equal("Failed", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", blockedRun)));
        Assert.Equal("deadline_exceeded", await Scalar<string>("SELECT \"ErrorCode\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", blockedRun)));
    }

    [Fact]
    public async Task Consecutive_messages_keep_fifo_per_conversation_and_chat_of_any_project_precedes_every_extraction()
    {
        using var client = await App.LoginAsync();
        var a = await CreateProject(client);
        await PostMessage(client, a, "Progetto A, primo.");
        await PostMessage(client, a, "Progetto A, secondo.");
        var b = await CreateProject(client);
        await PostMessage(client, b, "Progetto B, unico.");

        // Tre turni in coda e tre estrazioni: prima tutti i turni (A1, A2 in ordine, poi B1), nessuna estrazione li sorpassa.
        var order = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var turn = (await Claim($"chat{i}", turns: true))!.Turn!;
            order.Add(turn.SourceCutoffSequence);
            await CompleteTurn(turn.TurnId);
        }

        Assert.Equal([1L, 2L, 1L], order);                                           // A1, A2 (FIFO per conversazione) e B1
        var extractions = new List<(Guid Project, long Sequence)>();
        while (await Claim("x") is { Extraction: { } e })
        {
            extractions.Add((await Scalar<Guid>("SELECT \"ProjectId\" FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = @j", ("j", e.JobId)),
                await Scalar<long>("SELECT \"MessageSequence\" FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = @j", ("j", e.JobId))));
            await CompleteExtraction(e);
        }

        Assert.Equal(3, extractions.Count);
        Assert.True(extractions.IndexOf((a, 1)) < extractions.IndexOf((a, 2)));      // il secondo di A mai prima del primo di A
        Assert.Equal(0, await Scalar<long>("SELECT count(*) FROM \"EvidenceExtractionJobs\" WHERE \"Status\" IN ('Queued', 'Running')"));
    }

    // ---------------------------------------------------------------- supporto

    private async Task<WorkClaim?> Claim(string owner, bool turns = false, bool runs = false)
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>()
            .ClaimNextWorkAsync(owner, turns, runs, documents: false, extractions: true, Ct);
    }

    /// <summary>Chiude un lavoro senza passare dal commit del registro (le prove di scheduling non leggono le evidenze).</summary>
    private Task CompleteExtraction(Guid job) =>
        Execute("""UPDATE "EvidenceExtractionJobs" SET "Status" = 'Failed', "ErrorCode" = 'test', "CompletedAt" = now(), "LeaseOwner" = NULL, "LeaseUntil" = NULL WHERE "Id" = @j""", ("j", job));

    private async Task CompleteExtraction(ExtractionJobClaim claim)
    {
        using var scope = App.Services.CreateScope();
        var ledger = new EvidenceLedgerStore(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        var context = await ledger.LoadContextAsync(claim.JobId, Ct);
        var result = await ledger.CommitAsync(claim, context, EvidenceExtractionValidator.Validate(EvidenceTestSeed.Output(context), context), "fake", "{}", Ct);
        Assert.Equal(ExtractionCommitOutcome.Committed, result.Outcome);
    }

    private Task CompleteTurn(Guid turn) =>
        Execute("""UPDATE "ChatTurns" SET "Status" = 'Failed', "ErrorCode" = 'test', "CompletedAt" = now(), "LeaseOwner" = NULL, "LeaseUntil" = NULL WHERE "Id" = @t""", ("t", turn));

    private Task FailQueuedTurns() =>
        Execute("""UPDATE "ChatTurns" SET "Status" = 'Failed', "ErrorCode" = 'test', "CompletedAt" = now() WHERE "Status" = 'Queued'""");

    private Task<string> JobStatus(Guid job) => Scalar<string>("SELECT \"Status\" FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = @j", ("j", job));

    private static async Task<Guid> CreateProject(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name = $"Schedulazione {Guid.NewGuid():N}" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private static async Task PostMessage(HttpClient client, Guid project, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> Analyze(HttpClient client, Guid project)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return response;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();

    private async Task Execute(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default! : (T)result;
    }
}
