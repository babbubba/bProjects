using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T024 — contratto HTTP del comando Analizza (contracts/http-api.md, «Richiesta di analisi formale») su PostgreSQL
/// reale: cutoff fissato sotto lock, chiave di idempotenza, no_pm_input, nessuna bozza Markdown. Nessuna prova
/// invoca il modello: l'harness non registra né esecutore chat né worker, quindi i run restano Queued.
/// Test-first (convenzione T010): le prove sono Skipped con motivo finché l'endpoint non esiste e diventano verdi con
/// T025 (ProcessingRun) e T030 (comandi/letture analisi). I casi nominali (202/200, run Queued, cutoff) sono tenuti
/// distinti da quelli di fallimento (422/409/400/404/401/403).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class AnalysisContractTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private const string PendingT030 = "In attesa di T025/T030: POST /analyses e ProcessingRun non esistono ancora.";

    private ApiHarness? _app;

    public async ValueTask InitializeAsync()
    {
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            _app = await ApiHarness.StartAsync(database.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private ApiHarness App
    {
        get
        {
            TestEnvironment.RequirePostgres();
            return _app!;
        }
    }

    // ================================================================ nominali

    [Fact(Skip = PendingT030)]
    public async Task Analyze_returns_202_location_and_a_queued_analysis_run_fixed_at_the_last_pm_message()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Voglio un portale clienti.");
        var last = await PostMessage(client, project, "Il login avviene con email.");

        var response = await Analyze(client, project, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = await Json(response);
        var runId = run.GetProperty("id").GetGuid();
        Assert.EndsWith($"/api/projects/{project}/processing-runs/{runId}", response.Headers.Location!.ToString());
        Assert.Equal("Analysis", run.GetProperty("kind").GetString());
        Assert.Equal("Queued", run.GetProperty("status").GetString());
        Assert.Equal(last.Sequence, run.GetProperty("sourceCutoffSequence").GetInt64());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("resultRevisionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("error").ValueKind);
    }

    [Fact(Skip = PendingT030)]
    public async Task Analyze_does_not_wait_for_pending_chat_turns_and_does_not_include_later_messages()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        var before = await PostMessage(client, project, "Messaggio prima di Analizza.");
        // Il turno chat del messaggio resta Queued (nessun worker): Analizza non deve attenderlo.
        var run = await Json(await Analyze(client, project, Guid.NewGuid()));
        await PostMessage(client, project, "Messaggio dopo Analizza.");

        var reread = await Json(await client.GetAsync($"/api/projects/{project}/processing-runs/{run.GetProperty("id").GetGuid()}", Ct));

        Assert.Equal(before.Sequence, run.GetProperty("sourceCutoffSequence").GetInt64());
        Assert.Equal(before.Sequence, reread.GetProperty("sourceCutoffSequence").GetInt64()); // il cutoff è immutabile
    }

    [Fact(Skip = PendingT030)]
    public async Task The_same_key_and_input_replays_the_same_run_without_creating_another()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var key = Guid.NewGuid();

        var first = await Json(await Analyze(client, project, key));
        var replay = await Analyze(client, project, key);

        // Run ancora attivo: 202 con il medesimo run (200 sarebbe per un run terminale).
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(first.GetProperty("id").GetGuid(), (await Json(replay)).GetProperty("id").GetGuid());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p AND \"Kind\" = 'Analysis'", ("p", project)));
    }

    [Fact(Skip = PendingT030)]
    public async Task A_terminal_run_replayed_with_the_same_key_returns_200_and_the_same_run()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var key = Guid.NewGuid();
        var first = await Json(await Analyze(client, project, key));
        await SetRunStatus(first.GetProperty("id").GetGuid(), "Failed");

        var replay = await Analyze(client, project, key);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var view = await Json(replay);
        Assert.Equal(first.GetProperty("id").GetGuid(), view.GetProperty("id").GetGuid());
        Assert.Equal("Failed", view.GetProperty("status").GetString()); // un Failed non si presenta come successo
    }

    [Fact(Skip = PendingT030)]
    public async Task After_a_failed_run_a_new_key_starts_a_new_run_and_the_old_run_stays_readable()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var failed = await Json(await Analyze(client, project, Guid.NewGuid()));
        await SetRunStatus(failed.GetProperty("id").GetGuid(), "Failed");

        var retry = await Analyze(client, project, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.NotEqual(failed.GetProperty("id").GetGuid(), (await Json(retry)).GetProperty("id").GetGuid());
        var old = await client.GetAsync($"/api/projects/{project}/processing-runs/{failed.GetProperty("id").GetGuid()}", Ct);
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        Assert.Equal("Failed", (await Json(old)).GetProperty("status").GetString());
    }

    [Fact(Skip = PendingT030)]
    public async Task Analyze_creates_no_markdown_draft_and_no_analysis_result()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");

        var run = await Json(await Analyze(client, project, Guid.NewGuid()));

        // La risposta descrive soltanto il run: nessun Markdown, nessun draftId, nessuna analisi già pronta.
        Assert.False(run.TryGetProperty("markdown", out _));
        Assert.False(run.TryGetProperty("draftId", out var draftId) && draftId.ValueKind != JsonValueKind.Null);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{project}/draft", Ct)).StatusCode);
        var view = await Json(await client.GetAsync($"/api/projects/{project}", Ct));
        Assert.Equal(run.GetProperty("id").GetGuid(), view.GetProperty("activeProcessingRunId").GetGuid());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("currentAnalysisId").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("latestDraftId").ValueKind);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p AND \"Kind\" = 'Document'", ("p", project)));
    }

    [Fact(Skip = PendingT030)]
    public async Task The_preview_is_not_available_before_the_first_valid_revision()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var run = await Json(await Analyze(client, project, Guid.NewGuid()));

        var response = await client.GetAsync($"/api/projects/{project}/analyses/{run.GetProperty("id").GetGuid()}", Ct);

        var problem = await AssertProblem(response, HttpStatusCode.NotFound);
        Assert.Equal("preview_not_available", problem.GetProperty("code").GetString());
    }

    [Fact(Skip = PendingT030)]
    public async Task Another_pm_can_read_the_run_of_a_shared_project()
    {
        using var first = await App.LoginAsync();
        using var second = await App.LoginAsync(TestApp.SecondPmUser, TestApp.SecondPmPassword);
        var project = await CreateProject(first);
        await PostMessage(first, project, "Un messaggio PM.");
        var run = await Json(await Analyze(first, project, Guid.NewGuid()));

        var response = await second.GetAsync($"/api/projects/{project}/processing-runs/{run.GetProperty("id").GetGuid()}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ================================================================ fallimenti

    [Fact(Skip = PendingT030)]
    public async Task Analyze_without_any_pm_message_is_422_no_pm_input_and_creates_no_run()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);

        var problem = await AssertProblem(await Analyze(client, project, Guid.NewGuid()), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("no_pm_input", problem.GetProperty("code").GetString());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p", ("p", project)));
        var view = await Json(await client.GetAsync($"/api/projects/{project}", Ct));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("activeProcessingRunId").ValueKind);
    }

    [Fact(Skip = PendingT030)]
    public async Task The_same_key_with_a_different_cutoff_is_409_idempotency_conflict()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Primo messaggio.");
        var key = Guid.NewGuid();
        var first = await Json(await Analyze(client, project, key));
        await PostMessage(client, project, "Secondo messaggio: cambia l'input di Analizza.");

        var problem = await AssertProblem(await Analyze(client, project, key), HttpStatusCode.Conflict);

        Assert.Equal("idempotency_conflict", problem.GetProperty("code").GetString());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p AND \"Kind\" = 'Analysis'", ("p", project)));
        Assert.Equal(first.GetProperty("sourceCutoffSequence").GetInt64(),
            (await Json(await client.GetAsync($"/api/projects/{project}/processing-runs/{first.GetProperty("id").GetGuid()}", Ct))).GetProperty("sourceCutoffSequence").GetInt64());
    }

    [Fact(Skip = PendingT030)]
    public async Task Another_key_while_a_formal_run_is_active_is_409_processing_active_pointing_to_the_existing_run()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        var active = await Json(await Analyze(client, project, Guid.NewGuid()));

        var response = await Analyze(client, project, Guid.NewGuid());

        var problem = await AssertProblem(response, HttpStatusCode.Conflict);
        Assert.Equal("processing_active", problem.GetProperty("code").GetString());
        Assert.Contains(active.GetProperty("id").GetGuid().ToString(), problem.ToString());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p AND \"Kind\" = 'Analysis'", ("p", project)));
    }

    [Theory(Skip = PendingT030)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("non-un-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_missing_or_malformed_idempotency_key_is_400_and_creates_no_run(string? key)
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Un messaggio PM.");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        var response = await client.SendAsync(request, Ct);

        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p", ("p", project)));
    }

    [Fact(Skip = PendingT030)]
    public async Task Analyze_on_an_unknown_project_is_404()
    {
        using var client = await App.LoginAsync();

        await AssertProblem(await Analyze(client, Guid.NewGuid(), Guid.NewGuid()), HttpStatusCode.NotFound);
    }

    [Fact(Skip = PendingT030)]
    public async Task Analysis_endpoints_require_authentication_and_the_pm_role()
    {
        var project = Guid.NewGuid();
        var run = Guid.NewGuid();
        using var anonymous = App.Anonymous();
        using var withoutRole = App.WithoutPmRole();

        foreach (var client in new[] { anonymous, withoutRole })
        {
            var expected = ReferenceEquals(client, anonymous) ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            Assert.Equal(expected, (await Analyze(client, project, Guid.NewGuid())).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/projects/{project}/processing-runs/{run}", Ct)).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/projects/{project}/analyses/{run}", Ct)).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/projects/{project}/analyses/{run}/revisions/{run}", Ct)).StatusCode);
        }
    }

    [Fact(Skip = PendingT030)]
    public async Task A_run_of_another_project_is_not_readable_through_this_project()
    {
        using var client = await App.LoginAsync();
        var projectA = await CreateProject(client);
        var projectB = await CreateProject(client);
        await PostMessage(client, projectA, "Un messaggio PM.");
        var run = await Json(await Analyze(client, projectA, Guid.NewGuid()));

        await AssertProblem(await client.GetAsync($"/api/projects/{projectB}/processing-runs/{run.GetProperty("id").GetGuid()}", Ct), HttpStatusCode.NotFound);
    }

    // ================================================================ supporto

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Posted(Guid Id, long Sequence);

    private static async Task<Guid> CreateProject(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name = $"Analisi {Guid.NewGuid():N}" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Posted> PostMessage(HttpClient client, Guid project, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = (await Json(response)).GetProperty("message");
        return new Posted(message.GetProperty("id").GetGuid(), message.GetProperty("sequence").GetInt64());
    }

    private static Task<HttpResponseMessage> Analyze(HttpClient client, Guid project, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", key.ToString());
        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await Json(response);
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        return body;
    }

    private async Task SetRunStatus(Guid runId, string status) =>
        await Execute("UPDATE \"ProcessingRuns\" SET \"Status\" = @s, \"CompletedAt\" = now() WHERE \"Id\" = @r", ("s", status), ("r", runId));

    private async Task<long> Count(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, sql, parameters);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task Execute(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
