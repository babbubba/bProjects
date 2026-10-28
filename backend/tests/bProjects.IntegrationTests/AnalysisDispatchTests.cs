using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Persistence;
using bProjects.App.Projects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T030 — dispatch formale dei run di Analysis e letture (anteprima, revisioni, ProjectView, processing.state) su
/// PostgreSQL reale con un IChatClient finto: nessuna chiamata al DGX (la prova con il modello reale è T031).
/// Lo stesso dispatcher, la stessa coda e lo stesso limite AI globale dei ChatTurn; claim con idoneità ricontrollata
/// nella UPDATE; l'errore del modello è classificato come per i turni (retry transitorio, Failed permanente).
/// Ogni prova usa un database nuovo: il limite AI globale rende gli stati interdipendenti.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class AnalysisDispatchTests : IAsyncLifetime
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

    // ---------------------------------------------------------------- percorso nominale

    [Fact]
    public async Task A_queued_analysis_is_run_by_the_dispatcher_and_the_preview_and_project_state_are_readable()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Vogliamo un portale clienti.");
        var run = await Json(await Analyze(client, project, Guid.NewGuid()));
        var runId = run.GetProperty("id").GetGuid();
        using var subscription = App.Services.GetRequiredService<ProjectEventHub>().Subscribe(project);

        Assert.True(await Dispatcher(new ReplyModel()).RunOnceAsync(Ct));
        await Task.Delay(300, Ct); // le notifiche di fase sono effimere e non bloccano il grafo

        var done = await Json(await client.GetAsync($"/api/projects/{project}/processing-runs/{runId}", Ct));
        Assert.Equal("Completed", done.GetProperty("status").GetString());
        Assert.Equal(1, done.GetProperty("attemptCount").GetInt32());
        Assert.Equal("ValidatePersistAnalysis", done.GetProperty("lastObservedStage").GetString());
        var revisionId = done.GetProperty("resultRevisionId").GetGuid();
        Assert.Equal(JsonValueKind.Null, done.GetProperty("error").ValueKind);

        // ProjectView: run attivo azzerato, analisi corrente e revisione, nessuna bozza.
        var view = await Json(await client.GetAsync($"/api/projects/{project}", Ct));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("activeProcessingRunId").ValueKind);
        Assert.Equal(runId, view.GetProperty("latestAnalysisRunId").GetGuid());
        Assert.Equal(runId, view.GetProperty("currentAnalysisId").GetGuid());
        Assert.Equal(revisionId, view.GetProperty("currentRevisionId").GetGuid());
        Assert.Equal("Unvalidated", view.GetProperty("validationState").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("latestDraftId").ValueKind);

        // Anteprima: ETag = ID della revisione quotato; contenuto AI Unvalidated, nessun Markdown.
        var response = await client.GetAsync($"/api/projects/{project}/analyses/{runId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{revisionId}\"", response.Headers.ETag!.ToString());
        var preview = await Json(response);
        Assert.Equal(revisionId, preview.GetProperty("currentRevisionId").GetGuid());
        Assert.Equal(1, preview.GetProperty("version").GetInt32());
        Assert.Equal("1.0.0", preview.GetProperty("schemaVersion").GetString());
        Assert.Equal("Ai", preview.GetProperty("origin").GetString());
        Assert.Equal("Unvalidated", preview.GetProperty("validationState").GetString());
        Assert.Equal("Unvalidated", preview.GetProperty("content").GetProperty("validationState").GetString());
        Assert.Equal(1, preview.GetProperty("sourceCutoffSequence").GetInt64());
        Assert.False(preview.GetProperty("hasNewNotes").GetBoolean());
        Assert.True(preview.GetProperty("canEdit").GetBoolean());
        Assert.True(preview.GetProperty("canConfirm").GetBoolean());
        Assert.False(preview.GetProperty("canGenerate").GetBoolean()); // non è confermata
        Assert.False(preview.TryGetProperty("markdown", out _));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'"));

        // Una nuova nota PM oltre il cutoff attiva hasNewNotes; una risposta dell'assistente non basterebbe.
        await PostMessage(client, project, "Un'altra nota dopo Analizza.");
        var later = await Json(await client.GetAsync($"/api/projects/{project}/analyses/{runId}", Ct));
        Assert.True(later.GetProperty("hasNewNotes").GetBoolean());

        // Revisioni: per numero e per ID; solo la v1 esiste, ed è la corrente.
        foreach (var key in new[] { "1", revisionId.ToString() })
        {
            var revision = await Json(await client.GetAsync($"/api/projects/{project}/analyses/{runId}/revisions/{key}", Ct));
            Assert.Equal(revisionId, revision.GetProperty("revisionId").GetGuid());
            Assert.True(revision.GetProperty("isCurrent").GetBoolean());
            Assert.Equal("Ai", revision.GetProperty("origin").GetString());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{project}/analyses/{runId}/revisions/2", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{project}/analyses/{runId}/revisions/xyz", Ct)).StatusCode);

        // processing.state: Queued (alla richiesta, prima della sottoscrizione = assente), poi Running, poi Completed con le sole fasi reali.
        var states = new List<JsonElement>();
        while (subscription.Reader.TryRead(out var evt))
        {
            if (evt.Name != ProjectEventHub.ProcessingState)
            {
                continue; // chat.state della nota PM postata sopra
            }

            states.Add(JsonDocument.Parse(evt.Data).RootElement.Clone());
        }

        Assert.Equal("Running", states[0].GetProperty("status").GetString());
        Assert.Equal("Completed", states[^1].GetProperty("status").GetString());
        Assert.Equal(revisionId, states[^1].GetProperty("resultRevisionId").GetGuid());
        Assert.All(
            states.Select(s => s.GetProperty("lastObservedStage")).Where(s => s.ValueKind != JsonValueKind.Null),
            stage => Assert.Contains(stage.GetString(), AnalysisWorkflow.Stages));
    }

    [Fact]
    public async Task The_request_publishes_the_queued_state_and_freezes_the_configuration_and_prompt_versions()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        using var subscription = App.Services.GetRequiredService<ProjectEventHub>().Subscribe(project);

        var run = await Json(await Analyze(client, project, Guid.NewGuid()));

        Assert.True(subscription.Reader.TryRead(out var evt));
        Assert.Equal(ProjectEventHub.ProcessingState, evt!.Name);
        var state = JsonDocument.Parse(evt.Data).RootElement;
        Assert.Equal(run.GetProperty("id").GetGuid(), state.GetProperty("id").GetGuid());
        Assert.Equal("Queued", state.GetProperty("status").GetString());

        var id = run.GetProperty("id").GetGuid();
        var config = await Scalar<string>("SELECT \"AgentConfig\"::text FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", id));
        Assert.Contains("\"modelId\": \"test-model\"", config); // jsonb normalizza gli spazi
        Assert.Contains("\"maxAttempts\": 3", config);
        Assert.DoesNotContain("test-key", config);
        Assert.DoesNotContain("127.0.0.1", config);
        Assert.Contains(ConversationalAgent.ConsolidationPromptVersion, await Scalar<string>("SELECT \"PromptVersions\"::text FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", id)));
        Assert.Equal("project-analysis-v2", await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", id)));
    }

    // ---------------------------------------------------------------- fallimento non nasconde l'ultima analisi

    [Fact]
    public async Task A_failed_new_analysis_does_not_hide_the_last_successful_one()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var first = (await Json(await Analyze(client, project, Guid.NewGuid()))).GetProperty("id").GetGuid();
        Assert.True(await Dispatcher(new ReplyModel()).RunOnceAsync(Ct));

        var second = (await Json(await Analyze(client, project, Guid.NewGuid()))).GetProperty("id").GetGuid();
        Assert.True(await Dispatcher(new ReplyModel(_ => "Ecco l'analisi: non è JSON.")).RunOnceAsync(Ct));

        var failed = await Json(await client.GetAsync($"/api/projects/{project}/processing-runs/{second}", Ct));
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("invalid_agent_output", failed.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("resultRevisionId").ValueKind);
        var view = await Json(await client.GetAsync($"/api/projects/{project}", Ct));
        Assert.Equal(second, view.GetProperty("latestAnalysisRunId").GetGuid());
        Assert.Equal(first, view.GetProperty("currentAnalysisId").GetGuid());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("activeProcessingRunId").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/projects/{project}/analyses/{first}", Ct)).StatusCode);
        var problem = await Json(await client.GetAsync($"/api/projects/{project}/analyses/{second}", Ct));
        Assert.Equal("preview_not_available", problem.GetProperty("code").GetString());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", second)));
    }

    [Fact]
    public async Task A_transient_model_error_requeues_the_run_with_backoff_and_the_retry_resumes_to_completion()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var run = (await Json(await Analyze(client, project, Guid.NewGuid()))).GetProperty("id").GetGuid();
        var model = new ReplyModel { FailFirst = new HttpRequestException("giù", null, HttpStatusCode.ServiceUnavailable) };
        var dispatcher = Dispatcher(model);

        Assert.True(await dispatcher.RunOnceAsync(Ct));

        Assert.Equal("Queued", await RunStatus(run));
        Assert.Equal(1, await Scalar<int>("SELECT \"AttemptCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.NotNull(await Scalar<object?>("SELECT \"NextAttemptAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.False(await dispatcher.RunOnceAsync(Ct)); // ancora in backoff

        await Execute("UPDATE \"ProcessingRuns\" SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @r", ("r", run));
        Assert.True(await dispatcher.RunOnceAsync(Ct));

        Assert.Equal("Completed", await RunStatus(run));
        Assert.Equal(2, await Scalar<int>("SELECT \"AttemptCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_commit_rejected_because_the_lease_expired_is_not_a_model_error_and_the_run_is_reclaimed()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var run = (await Json(await Analyze(client, project, Guid.NewGuid()))).GetProperty("id").GetGuid();
        // Come dopo un'interruzione del database più lunga del lease: lease scaduto, fencing invariato (nessun reclaim è avvenuto).
        var model = new ReplyModel
        {
            BeforeReply = () => Execute("UPDATE \"ProcessingRuns\" SET \"LeaseUntil\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @r", ("r", run)).GetAwaiter().GetResult(),
        };

        Assert.True(await Dispatcher(model).RunOnceAsync(Ct));

        // Né Failed(model_error) né revisione: il run non è più dell'owner e lo gestisce il reclaim.
        Assert.Equal("Running", await RunStatus(run));
        Assert.Null(await Scalar<object?>("SELECT \"ErrorCode\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        var reclaimed = (await ClaimWork("new", turns: false))!.Run!;
        Assert.Equal(run, reclaimed.RunId);
        Assert.Equal(2, reclaimed.AttemptCount);
    }

    [Fact]
    public async Task A_commit_rejected_after_the_deadline_leaves_the_sweep_to_fail_the_run_as_deadline_exceeded()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var run = (await Json(await Analyze(client, project, Guid.NewGuid()))).GetProperty("id").GetGuid();
        var model = new ReplyModel
        {
            BeforeReply = () => Execute(
                "UPDATE \"ProcessingRuns\" SET \"LeaseUntil\" = clock_timestamp() - interval '1 second', \"DeadlineAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @r", ("r", run)).GetAwaiter().GetResult(),
        };

        Assert.True(await Dispatcher(model).RunOnceAsync(Ct));
        Assert.Equal("Running", await RunStatus(run));

        Assert.Null(await ClaimWork("w", turns: false));
        Assert.Equal("Failed", await RunStatus(run));
        Assert.Equal("deadline_exceeded", await Scalar<string>("SELECT \"ErrorCode\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
    }

    [Fact]
    public async Task The_run_sweep_only_touches_analysis_runs()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var run = (await Json(await Analyze(client, project, Guid.NewGuid()))).GetProperty("id").GetGuid();
        await Execute("UPDATE \"ChatTurns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x', \"CompletedAt\" = now()");
        await Execute("UPDATE \"ProcessingRuns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x', \"CompletedAt\" = now() WHERE \"Id\" = @r", ("r", run));
        var other = Guid.NewGuid();
        // Il vincolo dei campi per Kind richiederebbe un'analisi confermata: il database è privato della prova, lo si toglie.
        await Execute("ALTER TABLE \"ProcessingRuns\" DROP CONSTRAINT \"CK_ProcessingRuns_Kind_Fields\"");
        await Execute(
            """
            INSERT INTO "ProcessingRuns" SELECT (jsonb_populate_record(NULL::"ProcessingRuns",
                to_jsonb(r) || jsonb_build_object('Id', @o::text, 'Kind', 'Document', 'Status', 'Queued', 'ErrorCode', NULL, 'ErrorMessage', NULL,
                    'CompletedAt', NULL, 'RequestKey', gen_random_uuid()::text, 'DeadlineAt', to_char(now() - interval '1 hour', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')))).*
            FROM "ProcessingRuns" r WHERE r."Id" = @r
            """,
            ("o", other), ("r", run));

        Assert.Null(await ClaimWork("w", turns: false));

        Assert.Equal("Queued", await RunStatus(other)); // un run non-Analysis non è né reclamato né fallito dallo sweep di Analysis
    }

    [Fact]
    public async Task A_permanent_model_error_fails_the_run_without_leaking_the_exception_message()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var run = (await Json(await Analyze(client, project, Guid.NewGuid()))).GetProperty("id").GetGuid();

        Assert.True(await Dispatcher(new ReplyModel { FailFirst = new HttpRequestException("segreto-nel-messaggio", null, HttpStatusCode.Unauthorized) }).RunOnceAsync(Ct));

        Assert.Equal("Failed", await RunStatus(run));
        Assert.Equal("model_unauthorized", await Scalar<string>("SELECT \"ErrorCode\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.DoesNotContain("segreto", await Scalar<string>("SELECT \"ErrorMessage\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Null(await Scalar<object?>("SELECT \"CurrentAnalysisId\" FROM \"Projects\" WHERE \"Id\" = @p", ("p", project)));
        // Dopo Failed un nuovo comando con nuova chiave è ammesso e non riusa il run fallito.
        var retry = await Analyze(client, project, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
    }

    [Fact]
    public async Task Without_a_workflow_the_dispatcher_leaves_queued_analyses_untouched()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var run = (await Json(await Analyze(client, project, Guid.NewGuid()))).GetProperty("id").GetGuid();
        var dispatcher = new AiDispatchService(
            App.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new ExecutionOptions()), NullLogger<AiDispatchService>.Instance);

        Assert.False(await dispatcher.RunOnceAsync(Ct));

        Assert.Equal("Queued", await RunStatus(run));
        Assert.Equal(0, await Scalar<int>("SELECT \"AttemptCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
    }

    // ---------------------------------------------------------------- coda condivisa, FIFO e limite globale

    [Fact]
    public async Task Turns_and_runs_share_one_fifo_queue_and_the_global_ai_limit()
    {
        using var client = await App.LoginAsync();
        var projectA = await CreateProject(client);
        var projectB = await CreateProject(client);
        await PostMessage(client, projectA, "Messaggio A."); // turno A (più vecchio)
        var runA = (await Json(await Analyze(client, projectA, Guid.NewGuid()))).GetProperty("id").GetGuid(); // run di A
        await PostMessage(client, projectB, "Messaggio B."); // turno B (più recente del run)

        var first = await ClaimWork("w1");
        Assert.NotNull(first?.Turn); // il turno A è il più vecchio
        Assert.Null(await ClaimWork("w2")); // limite globale: un solo lavoro con lease valido, di qualunque tipo
        await Execute("UPDATE \"ChatTurns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x', \"CompletedAt\" = now(), \"LeaseOwner\" = NULL, \"LeaseUntil\" = NULL WHERE \"Id\" = @t",
            ("t", first!.Turn!.TurnId));

        var second = await ClaimWork("w3");
        Assert.Equal(runA, second?.Run?.RunId); // il run precede il turno B
        Assert.Equal(1, second!.Run!.AttemptCount);
        Assert.Equal(1, second.Run.FencingToken);
        Assert.Null(await ClaimWork("w4")); // il run Running blocca il turno B
        Assert.Equal("Running", await RunStatus(runA));
    }

    [Fact]
    public async Task The_chat_only_claim_never_takes_a_run()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var run = (await Json(await Analyze(client, project, Guid.NewGuid()))).GetProperty("id").GetGuid();
        await Execute("UPDATE \"ChatTurns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x', \"CompletedAt\" = now()");

        using var scope = App.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextAsync("chat-only", Ct));

        Assert.Equal("Queued", await RunStatus(run));
    }

    [Fact]
    public async Task Concurrent_claims_of_a_single_queued_run_yield_exactly_one_owner()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var run = (await Json(await Analyze(client, project, Guid.NewGuid()))).GetProperty("id").GetGuid();
        await Execute("UPDATE \"ChatTurns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x', \"CompletedAt\" = now()");

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => ClaimWork($"worker-{i}", turns: false), Ct)));

        Assert.Single(claims, c => c is not null);
        Assert.Equal(1, await Scalar<int>("SELECT \"AttemptCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(1L, await Scalar<long>("SELECT \"FencingToken\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_claim_waiting_on_the_row_lock_does_not_revive_a_run_that_became_terminal()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var run = (await Json(await Analyze(client, project, Guid.NewGuid()))).GetProperty("id").GetGuid();
        await Execute("UPDATE \"ChatTurns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x', \"CompletedAt\" = now()");

        await using var holder = new NpgsqlConnection(_database.ConnectionString);
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM \"ProcessingRuns\" WHERE \"Id\" = @r FOR UPDATE", holder, transaction))
        {
            lockRow.Parameters.AddWithValue("r", run);
            await lockRow.ExecuteScalarAsync(Ct);
        }

        var claimTask = Task.Run(() => ClaimWork("late-worker", turns: false), Ct);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (await Count("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE 'WITH clk%UPDATE \"ProcessingRuns\" t SET%'") == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Il claim non è entrato in attesa sul lock di riga.");
            Assert.False(claimTask.IsCompleted, "Il claim non doveva completarsi mentre la riga è bloccata.");
            await Task.Delay(50, Ct);
        }

        // Nel frattempo il run diventa terminale (come un commit/fail di un altro owner).
        await using (var fail = new NpgsqlCommand(
            "UPDATE \"ProcessingRuns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'processing_failed', \"CompletedAt\" = clock_timestamp() WHERE \"Id\" = @r", holder, transaction))
        {
            fail.Parameters.AddWithValue("r", run);
            await fail.ExecuteNonQueryAsync(Ct);
        }

        await transaction.CommitAsync(Ct);

        Assert.Null(await claimTask);
        Assert.Equal("Failed", await RunStatus(run));
        Assert.Equal(0, await Scalar<int>("SELECT \"AttemptCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
    }

    [Fact]
    public async Task An_expired_run_lease_is_reclaimed_with_a_new_fence_and_attempt_and_a_stale_owner_is_rejected()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var run = (await Json(await Analyze(client, project, Guid.NewGuid()))).GetProperty("id").GetGuid();
        await Execute("UPDATE \"ChatTurns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x', \"CompletedAt\" = now()");
        var stale = (await ClaimWork("old", turns: false))!.Run!;
        await Execute("UPDATE \"ProcessingRuns\" SET \"LeaseUntil\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @r", ("r", run));

        var current = (await ClaimWork("new", turns: false))!.Run!;

        Assert.Equal(2, current.AttemptCount);
        Assert.Equal(2, current.FencingToken);
        using var scope = App.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>();
        Assert.False(await store.RenewAsync(stale, Ct));
        Assert.Equal(TurnTransition.Rejected, (await store.ReleaseAfterFailureAsync(stale, new ModelFailure(true, "model_unreachable", null), "x", "x", Ct)).Outcome);
        Assert.False(await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().FailAsync(stale, "x", "x", Ct));
        Assert.True(await store.RenewAsync(current, Ct));
        Assert.Equal("Running", await RunStatus(run));
    }

    [Fact]
    public async Task A_run_past_its_deadline_or_attempts_is_failed_by_the_claim_sweep()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var run = (await Json(await Analyze(client, project, Guid.NewGuid()))).GetProperty("id").GetGuid();
        await Execute("UPDATE \"ChatTurns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x', \"CompletedAt\" = now()");
        await Execute("UPDATE \"ProcessingRuns\" SET \"DeadlineAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @r", ("r", run));

        Assert.Null(await ClaimWork("w", turns: false));

        Assert.Equal("Failed", await RunStatus(run));
        Assert.Equal("deadline_exceeded", await Scalar<string>("SELECT \"ErrorCode\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(HttpStatusCode.Accepted, (await Analyze(client, project, Guid.NewGuid())).StatusCode); // l'indice attivo è libero
    }

    // ---------------------------------------------------------------- supporto

    private AiDispatchService Dispatcher(ReplyModel model)
    {
        using var client = new InternalModelClient(new AiOptions { Endpoint = "http://localhost:1/v1/", ModelId = "fake", ApiKey = "non-usata" });
        var agent = new ConversationalAgent(
            new AgentFactory(new AiOptions { ContextWindowTokens = 32768 }, model, (t, _) => new(t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)),
            new ChatOptions { ModelId = "fake" });
        var scopes = App.Services.GetRequiredService<IServiceScopeFactory>();
        var workflow = new AnalysisWorkflow(scopes, agent, client.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0));
        return new AiDispatchService(
            scopes,
            Options.Create(new ExecutionOptions()),
            NullLogger<AiDispatchService>.Instance,
            executor: null,
            events: null,
            workflow,
            App.Services.GetRequiredService<ProcessingRunEvents>());
    }

    private async Task<WorkClaim?> ClaimWork(string owner, bool turns = true)
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextWorkAsync(owner, turns, runs: true, Ct);
    }

    /// <summary>
    /// Modello finto: costruisce un AnalysisContent valido dai soli dati della richiesta (id fidati del server e primo
    /// messaggio PM), oppure risponde con un testo fisso; <see cref="FailFirst"/> fa fallire la prima chiamata.
    /// </summary>
    private sealed class ReplyModel(Func<string, string>? reply = null) : IChatClient
    {
        private int _calls;

        public Exception? FailFirst { get; set; }

        /// <summary>Eseguito a ogni chiamata prima della risposta (es. per far scadere il lease durante la chiamata al modello).</summary>
        public Action? BeforeReply { get; set; }

        private string Reply(IEnumerable<ChatMessage> messages)
        {
            BeforeReply?.Invoke();
            var text = string.Join("\n", messages.Select(m => m.Text));
            if (Interlocked.Increment(ref _calls) == 1 && FailFirst is { } failure)
            {
                throw failure;
            }

            return reply is not null ? reply(text) : ValidContent(text);
        }

        private static string ValidContent(string request)
        {
            string Match(string pattern) => Regex.Match(request, pattern).Groups[1].Value;
            var message = Regex.Match(request, @"\[messaggio id=([0-9a-f-]{36}) sequenza=(\d+)\]");
            var source = Guid.NewGuid().ToString();
            return new JsonObject
            {
                ["schemaVersion"] = "1.0.0",
                ["analysisId"] = Match(@"analysisId: ([0-9a-f-]{36})"),
                ["projectId"] = Match(@"projectId: ([0-9a-f-]{36})"),
                ["sourceCutoffSequence"] = long.Parse(Match(@"sourceCutoffSequence: (\d+)")),
                ["validationState"] = "Unvalidated",
                ["sources"] = new JsonArray(new JsonObject
                {
                    ["id"] = source,
                    ["kind"] = "Message",
                    ["messageId"] = message.Groups[1].Value,
                    ["messageRole"] = "user",
                    ["revisionId"] = null,
                }),
                ["goals"] = new JsonArray(new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString(),
                    ["text"] = "Realizzare un portale clienti",
                    ["classification"] = "Declared",
                    ["evidenceRefs"] = new JsonArray(JsonValue.Create(source)),
                }),
                ["contextAndScope"] = new JsonObject { ["overview"] = new JsonArray(), ["included"] = new JsonArray(), ["excluded"] = new JsonArray() },
                ["requirements"] = new JsonArray(),
                ["operationalFlows"] = new JsonArray(),
                ["constraints"] = new JsonArray(),
                ["dependencies"] = new JsonArray(),
                ["assumptions"] = new JsonArray(),
                ["openQuestions"] = new JsonArray(),
                ["conflicts"] = new JsonArray(),
            }.ToJsonString();
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply(messages))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, Reply(messages));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static async Task<Guid> CreateProject(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name = $"Dispatch {Guid.NewGuid():N}" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private static async Task PostMessage(HttpClient client, Guid project, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static Task<HttpResponseMessage> Analyze(HttpClient client, Guid project, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", key.ToString());
        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();

    private Task<string> RunStatus(Guid run) => Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));

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
