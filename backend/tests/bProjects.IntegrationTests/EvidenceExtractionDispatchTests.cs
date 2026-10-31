using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using bProjects.App.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T060 — lavori di estrazione delle evidenze su PostgreSQL reale con l'estrattore reale (<see cref="EvidenceExtractor"/>, MAF) e un
/// IChatClient finto: stati Queued → Running → Completed/Failed e backoff, letti dalle API pubbliche e dal database; retry transitorio,
/// errore permanente e output non valido, tentativi esauriti, deadline, crash/reclaim con nuovo fence e worker obsoleto rifiutato,
/// FIFO per conversazione senza starvation fra conversazioni né busy polling durante il backoff, priorità della chat e delle estrazioni
/// che bloccano un'analisi, gate di Analizza sul cutoff e <c>evidence_unavailable</c> senza risultati parziali. Nessuna chiamata al DGX.
/// Ogni prova usa un database nuovo: il limite AI globale rende gli stati interdipendenti.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class EvidenceExtractionDispatchTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();
    private ApiHarness? _app;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            _app = await ApiHarness.StartAsync(_database.ConnectionString); // nessun esecutore registrato: il dispatcher in background resta inattivo
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

    // ---------------------------------------------------------------- stati nominali

    [Fact]
    public async Task An_initial_extraction_goes_Queued_Running_Completed_and_each_state_is_visible_in_the_status_events_and_telemetry()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        using var subscription = App.Services.GetRequiredService<ProjectEventHub>().Subscribe(project);
        await PostMessage(client, project, "Il portale deve inviare un promemoria via email entro 24 ore dalla scadenza.");

        // Queued: salvato con il messaggio, nessun tentativo, nessun lease; il modello non è stato chiamato (salvataggio a modello spento).
        var job = await SingleJob(project);
        var queued = await MessageStatus(client, project);
        Assert.Equal("pending", queued.GetProperty("state").GetString());
        Assert.Equal("Queued", queued.GetProperty("latestJob").GetProperty("status").GetString());
        Assert.Equal("Initial", queued.GetProperty("latestJob").GetProperty("reason").GetString());
        Assert.Equal(0, queued.GetProperty("latestJob").GetProperty("attemptCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, queued.GetProperty("currentVersion").ValueKind);
        Assert.Equal(1, (await Status(client, project)).GetProperty("queue").GetProperty("queued").GetInt32());

        // Running: osservato mentre il modello è in chiamata (lease dell'owner, primo tentativo).
        var model = new ExtractionModel(App.Services) { Hold = true };
        var dispatch = Dispatcher(model).RunOnceAsync(Ct);
        await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var running = await MessageStatus(client, project);
        Assert.Equal("running", running.GetProperty("state").GetString());
        Assert.Equal("Running", running.GetProperty("latestJob").GetProperty("status").GetString());
        Assert.Equal(1, running.GetProperty("latestJob").GetProperty("attemptCount").GetInt32());
        Assert.Equal(1, (await Status(client, project)).GetProperty("queue").GetProperty("running").GetInt32());
        Assert.NotNull(await Scalar<string>("SELECT \"LeaseOwner\" FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = @j", ("j", job)));
        model.Release.SetResult();
        Assert.True(await dispatch);

        // Completed: prima versione valida promossa a corrente, lavoro collegato alla versione, lease rilasciato.
        var completed = await MessageStatus(client, project);
        Assert.Equal("extracted", completed.GetProperty("state").GetString());
        Assert.Equal("Completed", completed.GetProperty("latestJob").GetProperty("status").GetString());
        var current = completed.GetProperty("currentVersion");
        Assert.Equal(1, current.GetProperty("version").GetInt32());
        Assert.Equal(1, current.GetProperty("evidenceCount").GetInt32());
        Assert.Equal(current.GetProperty("id").GetGuid(), completed.GetProperty("latestJob").GetProperty("resultVersionId").GetGuid());
        Assert.Null(await Scalar<string?>("SELECT \"LeaseOwner\" FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = @j", ("j", job)));
        Assert.Equal(1, model.Calls);

        // Eventi evidence.state nell'ordine degli stati; telemetria della fase Extraction legata al lavoro.
        Assert.Equal(["Queued", "Running", "Completed"], EvidenceStates(subscription, job).Distinct());
        using var scope = App.Services.CreateScope();
        var calls = await scope.ServiceProvider.GetRequiredService<AppDbContext>().InferenceCalls.AsNoTracking()
            .Where(c => c.EvidenceExtractionJobId == job).ToListAsync(Ct);
        Assert.NotEmpty(calls);
        Assert.All(calls, c => Assert.Equal(AiPhase.Extraction, c.Phase));
        Assert.All(calls, c => Assert.Null(c.ChatTurnId));
        Assert.All(calls, c => Assert.Null(c.ProcessingRunId));
    }

    [Fact]
    public async Task A_double_submit_of_the_same_message_creates_one_message_and_one_extraction_job()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        var key = Guid.NewGuid();
        var body = new { clientMessageId = key, text = "Il report mensile va inviato al direttore." };

        var first = await client.PostAsJsonAsync($"/api/projects/{project}/messages", body, Ct);
        var again = await client.PostAsJsonAsync($"/api/projects/{project}/messages", body, Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.True(again.IsSuccessStatusCode, $"il secondo invio idempotente deve riuscire, non {again.StatusCode}");
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'User'"));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"EvidenceExtractionJobs\" WHERE \"ProjectId\" = @p", ("p", project)));
    }

    // ---------------------------------------------------------------- backoff, retry, tentativi esauriti

    [Fact]
    public async Task An_offline_model_requeues_with_backoff_that_is_not_claimable_before_it_is_due_and_then_completes_on_the_next_attempt()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Le fatture vanno archiviate per dieci anni.");
        var job = await SingleJob(project);
        var model = new ExtractionModel(App.Services);
        model.Failures.Enqueue(new HttpRequestException("connessione rifiutata", new SocketException((int)SocketError.ConnectionRefused)));
        var dispatcher = Dispatcher(model);

        Assert.True(await dispatcher.RunOnceAsync(Ct));

        // Backoff: Queued con NextAttemptAt futuro, nessun errore terminale, nessun lease, un tentativo consumato.
        var backoff = await MessageStatus(client, project);
        Assert.Equal("pending", backoff.GetProperty("state").GetString());
        var latest = backoff.GetProperty("latestJob");
        Assert.Equal("Queued", latest.GetProperty("status").GetString());
        Assert.Equal(1, latest.GetProperty("attemptCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, latest.GetProperty("error").ValueKind);
        Assert.True(await Scalar<bool>(
            "SELECT \"NextAttemptAt\" > clock_timestamp() AND \"LeaseOwner\" IS NULL FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = @j", ("j", job)));

        // Nessun busy polling: finché il backoff non scade non c'è lavoro ammissibile, il modello non è richiamato e nulla cambia.
        var callsBefore = await Count("SELECT count(*) FROM \"InferenceCalls\" WHERE \"EvidenceExtractionJobId\" = @j", ("j", job));
        for (var i = 0; i < 5; i++)
        {
            Assert.False(await dispatcher.RunOnceAsync(Ct));
        }

        Assert.Equal(1, model.Calls);
        Assert.Equal(1, await Scalar<int>("SELECT \"AttemptCount\" FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = @j", ("j", job)));
        Assert.Equal(callsBefore, await Count("SELECT count(*) FROM \"InferenceCalls\" WHERE \"EvidenceExtractionJobId\" = @j", ("j", job)));

        // Backoff scaduto: secondo tentativo dello stesso lavoro, Completed con una sola versione.
        await Execute("UPDATE \"EvidenceExtractionJobs\" SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @j", ("j", job));
        Assert.True(await dispatcher.RunOnceAsync(Ct));
        var done = await MessageStatus(client, project);
        Assert.Equal("extracted", done.GetProperty("state").GetString());
        Assert.Equal(2, done.GetProperty("latestJob").GetProperty("attemptCount").GetInt32());
        Assert.Equal(job, done.GetProperty("latestJob").GetProperty("id").GetGuid());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"EvidenceExtractionVersions\" WHERE \"JobId\" = @j", ("j", job)));
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task Transient_failures_up_to_the_attempt_limit_fail_the_job_with_attempts_exhausted_and_it_never_restarts_by_itself()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Il servizio deve rispondere in meno di due secondi.");
        var job = await SingleJob(project);
        var model = new ExtractionModel(App.Services);
        var dispatcher = Dispatcher(model);
        var limit = new ExecutionOptions().MaxAttempts;

        for (var attempt = 1; attempt <= limit; attempt++)
        {
            model.Failures.Enqueue(new HttpRequestException("non disponibile", null, HttpStatusCode.ServiceUnavailable));
            Assert.True(await dispatcher.RunOnceAsync(Ct));
            await Execute("UPDATE \"EvidenceExtractionJobs\" SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @j AND \"Status\" = 'Queued'", ("j", job));
        }

        var failed = await MessageStatus(client, project);
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        Assert.Equal("Failed", failed.GetProperty("latestJob").GetProperty("status").GetString());
        Assert.Equal("attempts_exhausted", failed.GetProperty("latestJob").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(limit, failed.GetProperty("latestJob").GetProperty("attemptCount").GetInt32());
        Assert.Equal(1, (await Status(client, project)).GetProperty("queue").GetProperty("failed").GetInt32());
        Assert.False(await dispatcher.RunOnceAsync(Ct)); // nessun lavoro si rigenera da solo dopo Failed
        Assert.Equal(limit, model.Calls);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"EvidenceExtractionVersions\""));
    }

    [Fact]
    public async Task A_permanent_model_error_and_an_invalid_output_fail_at_the_first_attempt_without_versions_and_a_retry_creates_a_new_job()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Gli operatori accedono con le credenziali aziendali.");
        var first = await SingleJob(project);
        var model = new ExtractionModel(App.Services);
        model.Failures.Enqueue(new HttpRequestException("non autorizzato", null, HttpStatusCode.Unauthorized));
        var dispatcher = Dispatcher(model);

        Assert.True(await dispatcher.RunOnceAsync(Ct));
        var permanent = (await MessageStatus(client, project)).GetProperty("latestJob");
        Assert.Equal("Failed", permanent.GetProperty("status").GetString());
        Assert.Equal("model_unauthorized", permanent.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, permanent.GetProperty("attemptCount").GetInt32());

        // Retry autorizzato: un nuovo lavoro (RetryOf), l'originale resta Failed; ora il modello risponde con una citazione inesistente.
        var retry = await RetryJob(client, project, first);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var second = (await Json(retry)).GetProperty("id").GetGuid();
        Assert.NotEqual(first, second);
        Assert.Equal(first, await Scalar<Guid>("SELECT \"RetryOfJobId\" FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = @j", ("j", second)));
        Assert.Equal(HttpStatusCode.Conflict, (await RetryJob(client, project, first)).StatusCode); // già in corso un'estrazione del messaggio
        model.Reply = InvalidQuote;
        Assert.True(await dispatcher.RunOnceAsync(Ct));

        var invalid = (await MessageStatus(client, project)).GetProperty("latestJob");
        Assert.Equal(second, invalid.GetProperty("id").GetGuid());
        Assert.Equal("Failed", invalid.GetProperty("status").GetString());
        Assert.Equal(EvidenceExtractionValidator.InvalidAgentOutput, invalid.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, invalid.GetProperty("attemptCount").GetInt32()); // nessuna riparazione né seconda chiamata
        Assert.Equal("Failed", await Scalar<string>("SELECT \"Status\" FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = @j", ("j", first)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"EvidenceExtractionVersions\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"EvidenceMessageStates\""));
        Assert.Equal(2, model.Calls);
    }

    // ---------------------------------------------------------------- lease, fencing, recovery, deadline

    [Fact]
    public async Task A_crashed_extraction_is_reclaimed_with_a_new_fence_and_the_stale_worker_can_neither_renew_fail_nor_commit()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Il magazzino lavora su due turni.");
        var job = await SingleJob(project);

        var crashed = (await Claim("crashed"))!.Extraction!;
        Assert.Equal(job, crashed.JobId);
        Assert.Null(await Claim("other")); // limite AI globale: un'estrazione Running con lease valido occupa il modello
        await Execute("UPDATE \"EvidenceExtractionJobs\" SET \"LeaseUntil\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @j", ("j", job));

        var reclaimed = (await Claim("recovered"))!.Extraction!;
        Assert.Equal(job, reclaimed.JobId);
        Assert.Equal(2, reclaimed.AttemptCount);
        Assert.True(reclaimed.FencingToken > crashed.FencingToken);

        using var scope = App.Services.CreateScope();
        var leases = scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>();
        var ledger = new EvidenceLedgerStore(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        var context = await ledger.LoadContextAsync(job, Ct);
        var validation = EvidenceExtractionValidator.Validate(EvidenceTestSeed.Output(context), context);
        Assert.False(await leases.RenewAsync(crashed, Ct));
        Assert.False(await ledger.FailAsync(crashed, "x", "y", Ct));
        Assert.Equal(ExtractionCommitOutcome.Rejected, (await ledger.CommitAsync(crashed, context, validation, "fake", "{}", Ct)).Outcome);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"EvidenceExtractionVersions\""));

        Assert.True(await leases.RenewAsync(reclaimed, Ct));
        Assert.Equal(ExtractionCommitOutcome.Committed, (await ledger.CommitAsync(reclaimed, context, validation, "fake", "{}", Ct)).Outcome);
        Assert.Equal(ExtractionCommitOutcome.AlreadyCompleted, (await ledger.CommitAsync(reclaimed, context, validation, "fake", "{}", Ct)).Outcome);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"EvidenceExtractionVersions\""));
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = @j", ("j", job)));
    }

    [Fact]
    public async Task Concurrent_claims_take_one_extraction_only_and_an_expired_deadline_is_swept_to_Failed_without_calling_the_model()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Primo requisito.");
        await PostMessage(client, project, "Secondo requisito.");

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Claim($"concorrente-{i}")));
        var taken = Assert.Single(claims, c => c is not null);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"EvidenceExtractionJobs\" WHERE \"Status\" = 'Running'"));
        Assert.Equal(1L, await Scalar<long>("SELECT \"MessageSequence\" FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = @j", ("j", taken!.Extraction!.JobId)));

        // Deadline scaduta: lo sweep porta a Failed deadline_exceeded sia il Running scaduto sia il Queued, senza modello.
        await Execute("UPDATE \"EvidenceExtractionJobs\" SET \"DeadlineAt\" = clock_timestamp() - interval '1 second', \"LeaseUntil\" = CASE WHEN \"Status\" = 'Running' THEN clock_timestamp() - interval '1 second' END");
        var model = new ExtractionModel(App.Services);
        Assert.False(await Dispatcher(model).RunOnceAsync(Ct));
        Assert.Equal(2, await Count("SELECT count(*) FROM \"EvidenceExtractionJobs\" WHERE \"Status\" = 'Failed' AND \"ErrorCode\" = 'deadline_exceeded'"));
        Assert.Equal(0, model.Calls);
    }

    // ---------------------------------------------------------------- FIFO, starvation, priorità

    [Fact]
    public async Task Backoff_keeps_fifo_inside_its_conversation_without_starving_other_conversations()
    {
        using var client = await App.LoginAsync();
        var a = await CreateProject(client);
        await PostMessage(client, a, "Progetto A, primo messaggio.");
        await PostMessage(client, a, "Progetto A, secondo messaggio.");
        var b = await CreateProject(client);
        await PostMessage(client, b, "Progetto B, unico messaggio.");
        var a1 = await JobOf(a, 1);
        var a2 = await JobOf(a, 2);
        var b1 = await SingleJob(b);

        var model = new ExtractionModel(App.Services);
        model.Failures.Enqueue(new HttpRequestException("non disponibile", null, HttpStatusCode.ServiceUnavailable));
        var dispatcher = Dispatcher(model);

        Assert.True(await dispatcher.RunOnceAsync(Ct)); // A1 (il più vecchio) va in backoff
        Assert.Equal(("Queued", 1), await JobState(a1));

        Assert.True(await dispatcher.RunOnceAsync(Ct)); // A2 attende A1 (FIFO), B non è bloccato dal backoff di A
        Assert.Equal(("Completed", 1), await JobState(b1));
        Assert.Equal(("Queued", 0), await JobState(a2));

        Assert.False(await dispatcher.RunOnceAsync(Ct)); // nulla di ammissibile: A2 non scavalca A1
        Assert.Equal(("Queued", 0), await JobState(a2));

        await Execute("UPDATE \"EvidenceExtractionJobs\" SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @j", ("j", a1));
        Assert.True(await dispatcher.RunOnceAsync(Ct));
        Assert.Equal(("Completed", 2), await JobState(a1));
        Assert.True(await dispatcher.RunOnceAsync(Ct));
        Assert.Equal(("Completed", 1), await JobState(a2));
        Assert.False(await dispatcher.RunOnceAsync(Ct));
        Assert.Equal(4, model.Calls);
    }

    [Fact]
    public async Task Chat_turns_go_before_extractions_and_extractions_blocking_an_analysis_go_before_older_ones_within_its_cutoff_only()
    {
        using var client = await App.LoginAsync();
        var other = await CreateProject(client);
        await PostMessage(client, other, "Messaggio di un altro progetto, accodato per primo.");
        var project = await CreateProject(client);
        await PostMessage(client, project, "Il portale gestisce i resi.");
        var otherJob = await SingleJob(other);
        var blocking = await JobOf(project, 1);

        // Il turno di chat più vecchio vince sempre sull'estrazione; il limite globale è condiviso.
        var turn = (await Claim("chat", turns: true))!.Turn!;
        Assert.Null(await Claim("busy", turns: true));
        await ReleaseTurn(turn);
        await FailQueuedTurns(); // nessun altro turno ammissibile: da qui in poi contano solo run ed estrazioni

        // Analizza: il run attende l'estrazione entro il cutoff, che scavalca quella più vecchia dell'altro progetto.
        var run = (await Json(await Analyze(client, project))).GetProperty("id").GetGuid();
        await PostMessage(client, project, "Messaggio successivo al cutoff.");
        await FailQueuedTurns();
        var beyond = await Scalar<Guid>(
            "SELECT \"Id\" FROM \"EvidenceExtractionJobs\" WHERE \"ProjectId\" = @p ORDER BY \"MessageSequence\" DESC LIMIT 1", ("p", project));
        Assert.NotEqual(blocking, beyond);

        var first = (await Claim("w1", runs: true))!.Extraction!;
        Assert.Equal(blocking, first.JobId);
        Assert.Equal("Queued", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        await CommitSeed(first);

        // Estrazione entro il cutoff conclusa: il run diventa ammissibile e passa prima delle estrazioni restanti (anche quella oltre il cutoff).
        var next = await Claim("w2", runs: true);
        Assert.Equal(run, next!.Run!.RunId);
        Assert.Equal(("Queued", 0), await JobState(beyond));
        Assert.Equal(("Queued", 0), await JobState(otherJob));
    }

    // ---------------------------------------------------------------- Analizza e risultati incompleti

    [Fact]
    public async Task Analyze_never_runs_on_incomplete_evidence_and_fails_with_evidence_unavailable_without_calling_the_model_or_writing_results()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Il sistema registra ogni accesso.");
        var extraction = new ExtractionModel(App.Services) { Reply = InvalidQuote };
        var analysis = new AnalysisModel();
        var dispatcher = Dispatcher(extraction, analysis);

        Assert.True(await dispatcher.RunOnceAsync(Ct)); // Initial: output non valido → Failed
        var run = (await Json(await Analyze(client, project))).GetProperty("id").GetGuid();

        // Analizza accoda il backfill del messaggio senza versione: il run resta in coda finché l'estrazione è aperta.
        Assert.Equal(1, await Count("SELECT count(*) FROM \"EvidenceExtractionJobs\" WHERE \"Reason\" = 'Backfill' AND \"Status\" = 'Queued'"));
        Assert.True(await dispatcher.RunOnceAsync(Ct)); // il backfill fallisce di nuovo
        Assert.Equal(0, analysis.Calls);
        Assert.True(await dispatcher.RunOnceAsync(Ct)); // ora il run è ammissibile e fallisce subito, senza modello

        Assert.Equal("Failed", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(EvidenceLedgerStore.EvidenceUnavailable, await Scalar<string>("SELECT \"ErrorCode\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Contains("1", await Scalar<string>("SELECT \"ErrorMessage\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(0, analysis.Calls);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisEvidenceSelections\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\""));
        Assert.Null(await Scalar<Guid?>("SELECT \"ResultRevisionId\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Null(await Scalar<Guid?>("SELECT \"CurrentAnalysisId\" FROM \"Projects\" WHERE \"Id\" = @p", ("p", project)));

        // Con l'estrazione riuscita un nuovo Analizza completa e congela la selezione con la versione corrente.
        extraction.Reply = null;
        var failedJob = await Scalar<Guid>("SELECT \"Id\" FROM \"EvidenceExtractionJobs\" WHERE \"Reason\" = 'Backfill'");
        Assert.Equal(HttpStatusCode.Accepted, (await RetryJob(client, project, failedJob)).StatusCode);
        Assert.True(await dispatcher.RunOnceAsync(Ct));
        var second = (await Json(await Analyze(client, project))).GetProperty("id").GetGuid();
        Assert.True(await dispatcher.RunOnceAsync(Ct));
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", second)));
        Assert.Equal(1, analysis.Calls);
        Assert.Equal(
            await Scalar<Guid>("SELECT \"CurrentVersionId\" FROM \"EvidenceMessageStates\""),
            await Scalar<Guid>("SELECT \"VersionId\" FROM \"AnalysisEvidenceSelectionItems\" WHERE \"RunId\" = @r", ("r", second)));
    }

    // ---------------------------------------------------------------- supporto

    private static string InvalidQuote(Guid messageId) => EvidenceJson.Serialize(new ExtractionOutput
    {
        NoEvidenceReason = null,
        Evidences =
        [
            new ExtractedEvidence
            {
                Key = "e1",
                Sources = [new ExtractedSource { MessageId = messageId, Quote = "frase che non compare nel messaggio", Occurrence = 1, Role = EvidenceSourceRole.Primary }],
                Kind = EvidenceKind.Requirement,
                KindRationale = null,
                Statement = "Enunciato senza fonte reale.",
                Subject = null,
                Action = null,
                Object = null,
                Scope = null,
                Purpose = null,
                Instrument = null,
                Channel = null,
                Recipient = null,
                Qualifiers = [],
                Polarity = EvidencePolarity.Affirmed,
                Certainty = EvidenceCertainty.Declared,
                Relations = [],
            },
        ],
    });

    private static ValueTask<int> Words(string text, CancellationToken _) =>
        new(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

    private AiDispatchService Dispatcher(ExtractionModel extraction, AnalysisModel? analysis = null)
    {
        var scopes = App.Services.GetRequiredService<IServiceScopeFactory>();
        var extractor = new EvidenceExtractor(
            scopes, new AgentFactory(new AiOptions { ContextWindowTokens = 32768 }, extraction, Words), new ChatOptions { ModelId = "fake-extractor" },
            App.Services.GetRequiredService<EvidenceEvents>());
        AnalysisWorkflow? workflow = null;
        if (analysis is not null)
        {
            using var client = new InternalModelClient(new AiOptions { Endpoint = "http://localhost:1/v1/", ModelId = "fake", ApiKey = "non-usata" });
            var agent = new ConversationalAgent(new AgentFactory(new AiOptions { ContextWindowTokens = 32768 }, analysis, Words), new ChatOptions { ModelId = "fake" });
            workflow = new AnalysisWorkflow(scopes, agent, client.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0));
        }

        return new AiDispatchService(
            scopes,
            Options.Create(new ExecutionOptions()),
            NullLogger<AiDispatchService>.Instance,
            executor: null,
            events: null,
            workflow,
            App.Services.GetRequiredService<ProcessingRunEvents>(),
            documentWorkflow: null,
            extractor,
            App.Services.GetRequiredService<EvidenceEvents>());
    }

    private async Task<WorkClaim?> Claim(string owner, bool turns = false, bool runs = false)
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>()
            .ClaimNextWorkAsync(owner, turns, runs, documents: false, extractions: true, Ct);
    }

    private async Task ReleaseTurn(ChatTurnClaim turn)
    {
        using var scope = App.Services.CreateScope();
        var released = await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>()
            .ReleaseAfterFailureAsync(turn, ModelFailure.Classify(new InvalidOperationException()), "test", "turno chiuso dalla prova", Ct);
        Assert.Equal(TurnTransition.Failed, released.Outcome);
    }

    private Task FailQueuedTurns() =>
        Execute("UPDATE \"ChatTurns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'test', \"CompletedAt\" = now() WHERE \"Status\" = 'Queued'");

    private async Task CommitSeed(ExtractionJobClaim claim)
    {
        using var scope = App.Services.CreateScope();
        var ledger = new EvidenceLedgerStore(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        var context = await ledger.LoadContextAsync(claim.JobId, Ct);
        var result = await ledger.CommitAsync(claim, context, EvidenceExtractionValidator.Validate(EvidenceTestSeed.Output(context), context), "fake", "{}", Ct);
        Assert.Equal(ExtractionCommitOutcome.Committed, result.Outcome);
    }

    private static List<string> EvidenceStates(ProjectEventHub.Subscription subscription, Guid job)
    {
        var states = new List<string>();
        while (subscription.Reader.TryRead(out var evt))
        {
            if (evt.Name != EvidenceEvents.EvidenceState)
            {
                continue;
            }

            var data = JsonDocument.Parse(evt.Data).RootElement;
            if (data.GetProperty("id").GetGuid() == job)
            {
                states.Add(data.GetProperty("status").GetString()!);
            }
        }

        return states;
    }

    private static async Task<Guid> CreateProject(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name = $"Estrazione {Guid.NewGuid():N}" }, Ct);
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

    private static Task<HttpResponseMessage> RetryJob(HttpClient client, Guid project, Guid job)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/evidence/jobs/{job}/retry");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Status(HttpClient client, Guid project) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/evidence/status", Ct)).Clone();

    private static async Task<JsonElement> MessageStatus(HttpClient client, Guid project) =>
        Assert.Single((await Status(client, project)).GetProperty("messages").EnumerateArray().ToList());

    private Task<Guid> SingleJob(Guid project) =>
        Scalar<Guid>("SELECT \"Id\" FROM \"EvidenceExtractionJobs\" WHERE \"ProjectId\" = @p", ("p", project));

    private Task<Guid> JobOf(Guid project, long sequence) =>
        Scalar<Guid>("SELECT \"Id\" FROM \"EvidenceExtractionJobs\" WHERE \"ProjectId\" = @p AND \"MessageSequence\" = @s", ("p", project), ("s", sequence));

    private async Task<(string Status, int Attempts)> JobState(Guid job)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT \"Status\", \"AttemptCount\" FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = @j", connection);
        command.Parameters.AddWithValue("j", job);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetString(0), reader.GetInt32(1));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();

    private async Task<long> Count(string sql, params (string Name, object Value)[] parameters) => await Scalar<long>(sql, parameters);

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

    /// <summary>
    /// Modello di estrazione finto, solo per i test: errori programmati (uno per chiamata), risposta personalizzata o, per default,
    /// l'uscita deterministica di <see cref="EvidenceTestSeed.Output"/> per il messaggio indicato dal server nel prompt; con
    /// <see cref="Hold"/> la chiamata attende <see cref="Release"/> per osservare lo stato Running.
    /// </summary>
    private sealed class ExtractionModel(IServiceProvider services) : IChatClient
    {
        private int _calls;

        public Queue<Exception> Failures { get; } = new();

        public Func<Guid, string>? Reply { get; set; }

        public bool Hold { get; init; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => _calls;

        private async Task<string> RespondAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Hold)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }

            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            var request = string.Join("\n", messages.Select(m => m.Text));
            var messageId = Guid.Parse(Regex.Match(request, @"messaggio da estrarre: id ([0-9a-f-]{36})").Groups[1].Value);
            if (Reply is { } reply)
            {
                return reply(messageId);
            }

            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.EvidenceExtractionJobs.AsNoTracking()
                .Where(j => j.MessageId == messageId && j.Status == ExtractionJobStatus.Running).Select(j => j.Id).SingleAsync(ct);
            return EvidenceJson.Serialize(EvidenceTestSeed.Output(await new EvidenceLedgerStore(db).LoadContextAsync(job, ct)));
        }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            new(new ChatMessage(ChatRole.Assistant, await RespondAsync(messages, cancellationToken)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, await RespondAsync(messages, cancellationToken));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Modello di consolidamento finto, solo per i test: conta le chiamate e risponde con un AnalysisContent valido.</summary>
    private sealed class AnalysisModel : IChatClient
    {
        private int _calls;

        public int Calls => _calls;

        private string Respond(IEnumerable<ChatMessage> messages)
        {
            Interlocked.Increment(ref _calls);
            return WorkflowRecoveryTests.AnalysisReply(string.Join("\n", messages.Select(m => m.Text)));
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Respond(messages))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, Respond(messages));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
