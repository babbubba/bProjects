using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
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
/// T037 — contratto HTTP della generazione esplicita del documento (contracts/http-api.md, «Generazione esplicita del
/// documento», e ai-workflow §5-§6) su PostgreSQL reale con un IChatClient finto: nessuna chiamata al DGX. Copre il 409 senza
/// revisione corrente confermata con zero invocazioni dell'agente, replay/conflitto della chiave, snapshot immutabile,
/// bozza Proposed/NotApproved, edit durante il run e conservazione dell'ultima bozza in caso d'errore.
/// Test-first (convenzione T010/T024): scritte prima dell'implementazione e Skipped con motivo finché i compiti che le rendono possibili
/// non esistevano, sono verdi da T041 (con T038 Draft e T039/T040 agente e workflow). Ogni prova usa un database nuovo: il
/// limite AI globale rende gli stati interdipendenti.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentContractTests : IAsyncLifetime
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

    // ================================================================ nominali

    [Fact]
    public async Task Generating_from_the_confirmed_current_revision_is_202_with_a_queued_document_run_frozen_on_that_revision()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'")); // la conferma non avvia nulla

        var response = await Generate(client, s, Guid.NewGuid(), s.Revision);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = await Json(response);
        var runId = run.GetProperty("id").GetGuid();
        Assert.EndsWith($"/api/projects/{s.Project}/processing-runs/{runId}", response.Headers.Location!.ToString());
        Assert.Equal("Document", run.GetProperty("kind").GetString());
        Assert.Equal("Queued", run.GetProperty("status").GetString());
        Assert.Equal(s.Analysis, run.GetProperty("analysisId").GetGuid());
        Assert.Equal(s.Revision, run.GetProperty("inputRevisionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("sourceCutoffSequence").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("resultRevisionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("draftId").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("error").ValueKind);
        Assert.Equal("project-document-v1", await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", runId)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));

        var project = await Json(await client.GetAsync($"/api/projects/{s.Project}", Ct));
        Assert.Equal(runId, project.GetProperty("activeProcessingRunId").GetGuid());
        Assert.Equal(JsonValueKind.Null, project.GetProperty("latestDraftId").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{s.Project}/draft", Ct)).StatusCode);
        var view = await Json(await client.GetAsync(Url(s), Ct));
        Assert.False(view.GetProperty("canGenerate").GetBoolean()); // un run formale attivo impedisce un secondo comando
    }

    [Fact]
    public async Task A_dispatched_run_commits_a_proposed_not_approved_draft_bound_to_the_input_revision_and_it_is_readable()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        var run = await Json(await Generate(client, s, Guid.NewGuid(), s.Revision));
        var runId = run.GetProperty("id").GetGuid();
        var model = new DocumentModel();

        Assert.True(await Dispatcher(model).RunOnceAsync(Ct));

        Assert.Equal(1, model.Calls);
        var done = await Json(await client.GetAsync($"/api/projects/{s.Project}/processing-runs/{runId}", Ct));
        Assert.Equal("Completed", done.GetProperty("status").GetString());
        Assert.Equal("ValidatePersistDraft", done.GetProperty("lastObservedStage").GetString());
        var draftId = done.GetProperty("draftId").GetGuid();
        Assert.Equal(JsonValueKind.Null, done.GetProperty("resultRevisionId").ValueKind);

        var response = await client.GetAsync($"/api/projects/{s.Project}/draft", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var draft = await Json(response);
        Assert.Equal(draftId, draft.GetProperty("id").GetGuid());
        Assert.Equal(s.Project, draft.GetProperty("projectId").GetGuid());
        Assert.Equal(runId, draft.GetProperty("processingRunId").GetGuid());
        Assert.Equal(s.Analysis, draft.GetProperty("analysisId").GetGuid());
        Assert.Equal(s.Revision, draft.GetProperty("analysisRevisionId").GetGuid());
        Assert.Equal(1, draft.GetProperty("analysisVersion").GetInt32());
        Assert.Equal("Proposed", draft.GetProperty("status").GetString());
        Assert.Equal("NotApproved", draft.GetProperty("approvalStatus").GetString());
        Assert.Equal("Bozza AI — da revisionare", draft.GetProperty("label").GetString());
        Assert.False(string.IsNullOrWhiteSpace(draft.GetProperty("markdown").GetString()));
        Assert.False(draft.GetProperty("hasNewNotes").GetBoolean());
        Assert.False(draft.GetProperty("hasNewRevision").GetBoolean());
        Assert.True(draft.TryGetProperty("createdAt", out _));

        var project = await Json(await client.GetAsync($"/api/projects/{s.Project}", Ct));
        Assert.Equal(draftId, project.GetProperty("latestDraftId").GetGuid());
        Assert.Equal(JsonValueKind.Null, project.GetProperty("activeProcessingRunId").ValueKind);

        // Nessuna approvazione cliente, nessuna modifica della revisione confermata, una sola bozza legata al run.
        Assert.Equal("PmConfirmed", await Scalar<string>("SELECT \"ValidationState\" FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r AND \"AnalysisRevisionId\" = @v", ("r", runId), ("v", s.Revision)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
    }

    [Fact]
    public async Task The_model_receives_the_whole_confirmed_json_of_the_input_revision_without_history_or_dialogue()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        var confirmed = await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision));
        await Generate(client, s, Guid.NewGuid(), s.Revision);
        var model = new DocumentModel();

        Assert.True(await Dispatcher(model).RunOnceAsync(Ct));

        var request = Assert.Single(model.Requests);
        var users = request.Where(m => m.Role == ChatRole.User).ToList();
        Assert.Single(users); // nessuna history, nessun dialogo, nessuna sintesi
        Assert.DoesNotContain(request, m => m.Role == ChatRole.Assistant);
        Assert.DoesNotContain("Vogliamo un portale clienti.", string.Join("\n", request.Select(m => m.Text))); // il messaggio originale PM non è input
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(confirmed), DocumentModel.EmbeddedJson(users[0].Text))); // l'intero JSON, non un riassunto
        Assert.Contains(s.Revision.ToString(), users[0].Text);
        var options = Assert.Single(model.Options);
        Assert.Null(options.ResponseFormat as ChatResponseFormatJson); // Markdown libero, non output strutturato
    }

    [Fact]
    public async Task An_edit_during_the_document_run_is_allowed_and_leaves_its_input_snapshot_unchanged()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        var run = await Json(await Generate(client, s, Guid.NewGuid(), s.Revision));
        var runId = run.GetProperty("id").GetGuid();

        // Durante Document attiva modifica e conferma sono ammesse, ma non cambiano l'input già congelato.
        var content = await Content(client, s);
        content["goals"]![0]!["text"] = "Obiettivo modificato mentre la bozza è in generazione";
        var put = await Put(client, s, s.Revision, content);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var edited = await Json(put);
        var v2 = edited.GetProperty("currentRevisionId").GetGuid();
        Assert.True(edited.GetProperty("canEdit").GetBoolean());
        Assert.False(edited.GetProperty("canGenerate").GetBoolean());
        Assert.Equal(s.Revision, await Scalar<Guid>("SELECT \"InputRevisionId\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", runId)));

        var model = new DocumentModel();
        Assert.True(await Dispatcher(model).RunOnceAsync(Ct));

        var request = Assert.Single(model.Requests).Single(m => m.Role == ChatRole.User).Text;
        Assert.DoesNotContain("Obiettivo modificato mentre", request); // l'input resta la v1
        Assert.Contains("Realizzare un portale clienti", request);
        var draft = await Json(await client.GetAsync($"/api/projects/{s.Project}/draft", Ct));
        Assert.Equal(s.Revision, draft.GetProperty("analysisRevisionId").GetGuid());
        Assert.Equal(1, draft.GetProperty("analysisVersion").GetInt32());
        Assert.True(draft.GetProperty("hasNewRevision").GetBoolean()); // esiste una revisione dell'analisi corrente più recente
        Assert.Equal("Unvalidated", await Scalar<string>("SELECT \"ValidationState\" FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", v2)));

        // Una futura generazione richiede la conferma della revisione ora corrente.
        var unconfirmed = await Generate(client, s, Guid.NewGuid(), v2);
        Assert.Equal("analysis_not_confirmed", (await AssertProblem(unconfirmed, HttpStatusCode.Conflict)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Confirm(client, s, v2)).StatusCode);
        var ready = await Json(await client.GetAsync(Url(s), Ct));
        Assert.True(ready.GetProperty("canGenerate").GetBoolean());
        Assert.Equal(HttpStatusCode.Accepted, (await Generate(client, s, Guid.NewGuid(), v2)).StatusCode);
    }

    [Fact]
    public async Task Only_new_pm_notes_beyond_the_cutoff_set_hasNewNotes_on_the_draft()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        await Generate(client, s, Guid.NewGuid(), s.Revision);
        Assert.True(await Dispatcher(new DocumentModel()).RunOnceAsync(Ct));
        Assert.False((await Json(await client.GetAsync($"/api/projects/{s.Project}/draft", Ct))).GetProperty("hasNewNotes").GetBoolean());

        var post = await client.PostAsJsonAsync($"/api/projects/{s.Project}/messages", new { clientMessageId = Guid.NewGuid(), text = "Una nuova nota dopo il cutoff." }, Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var draft = await Json(await client.GetAsync($"/api/projects/{s.Project}/draft", Ct));
        Assert.True(draft.GetProperty("hasNewNotes").GetBoolean());
        Assert.False(draft.GetProperty("hasNewRevision").GetBoolean()); // le note non sono una revisione dell'analisi
    }

    // ================================================================ idempotenza

    [Fact]
    public async Task The_same_key_and_input_replays_the_same_run_202_while_active_and_200_when_terminal()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        var key = Guid.NewGuid();
        var first = await Json(await Generate(client, s, key, s.Revision));

        var active = await Generate(client, s, key, s.Revision);

        Assert.Equal(HttpStatusCode.Accepted, active.StatusCode);
        Assert.Equal(first.GetProperty("id").GetGuid(), (await Json(active)).GetProperty("id").GetGuid());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'"));

        Assert.True(await Dispatcher(new DocumentModel()).RunOnceAsync(Ct));
        var terminal = await Generate(client, s, key, s.Revision);

        Assert.Equal(HttpStatusCode.OK, terminal.StatusCode);
        var view = await Json(terminal);
        Assert.Equal(first.GetProperty("id").GetGuid(), view.GetProperty("id").GetGuid());
        Assert.Equal("Completed", view.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("draftId").ValueKind); // stesso risultato
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'"));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\""));
        Assert.Equal(1, await Scalar<int>("SELECT \"AttemptCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", first.GetProperty("id").GetGuid()))); // il replay non è un nuovo attempt
    }

    [Fact]
    public async Task The_same_key_with_a_different_input_is_409_idempotency_conflict_and_with_the_same_input_replays_even_if_no_longer_current()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        var key = Guid.NewGuid();
        var first = await Json(await Generate(client, s, key, s.Revision));
        Assert.True(await Dispatcher(new DocumentModel()).RunOnceAsync(Ct));

        var content = await Content(client, s);
        content["goals"]![0]!["text"] = "Nuovo obiettivo";
        var v2 = (await Json(await Put(client, s, s.Revision, content))).GetProperty("currentRevisionId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Confirm(client, s, v2)).StatusCode);

        var conflict = await Generate(client, s, key, v2); // stessa chiave, revisione corrente e confermata ma diversa dall'input originale
        var replay = await Generate(client, s, key, s.Revision); // stessa chiave e stesso input: la v1 non è più corrente

        Assert.Equal("idempotency_conflict", (await AssertProblem(conflict, HttpStatusCode.Conflict)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(first.GetProperty("id").GetGuid(), (await Json(replay)).GetProperty("id").GetGuid());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'"));
    }

    [Fact]
    public async Task Another_key_while_a_formal_run_is_active_is_409_processing_active_pointing_to_the_existing_run()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        var active = await Json(await Generate(client, s, Guid.NewGuid(), s.Revision));

        var response = await Generate(client, s, Guid.NewGuid(), s.Revision);

        var problem = await AssertProblem(response, HttpStatusCode.Conflict);
        Assert.Equal("processing_active", problem.GetProperty("code").GetString());
        Assert.Equal(active.GetProperty("id").GetGuid(), problem.GetProperty("processingRunId").GetGuid());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'"));
    }

    [Fact]
    public async Task A_new_analysis_that_is_active_blocks_generation_with_processing_active()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        var analyze = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{s.Project}/analyses");
        analyze.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var queued = await Json(await client.SendAsync(analyze, Ct));

        var problem = await AssertProblem(await Generate(client, s, Guid.NewGuid(), s.Revision), HttpStatusCode.Conflict);

        Assert.Equal("processing_active", problem.GetProperty("code").GetString());
        Assert.Equal(queued.GetProperty("id").GetGuid(), problem.GetProperty("processingRunId").GetGuid());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'"));
    }

    [Fact]
    public async Task After_a_failed_run_a_new_key_starts_a_new_run_and_the_old_run_stays_readable()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        var failed = await Json(await Generate(client, s, Guid.NewGuid(), s.Revision));
        Assert.True(await Dispatcher(new DocumentModel(_ => "Documento senza fonti valide [fonte:" + Guid.NewGuid() + "]")).RunOnceAsync(Ct));
        Assert.Equal("Failed", await RunStatus(failed.GetProperty("id").GetGuid()));

        var retry = await Generate(client, s, Guid.NewGuid(), s.Revision);

        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.NotEqual(failed.GetProperty("id").GetGuid(), (await Json(retry)).GetProperty("id").GetGuid());
        var old = await Json(await client.GetAsync($"/api/projects/{s.Project}/processing-runs/{failed.GetProperty("id").GetGuid()}", Ct));
        Assert.Equal("Failed", old.GetProperty("status").GetString());
        Assert.Equal("invalid_agent_output", old.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, old.GetProperty("draftId").ValueKind); // un Failed non presenta un risultato
    }

    [Fact]
    public async Task Concurrent_commands_create_a_single_run_and_the_same_key_replays_it()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);

        var distinct = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Generate(client, s, Guid.NewGuid(), s.Revision)));

        Assert.Equal(1, distinct.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(5, distinct.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'"));

        var other = await Seed(client, confirm: true);
        var key = Guid.NewGuid();
        var same = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Generate(client, other, key, other.Revision)));

        Assert.All(same, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Single((await Task.WhenAll(same.Select(Json))).Select(v => v.GetProperty("id").GetGuid()).Distinct());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document' AND \"ProjectId\" = @p", ("p", other.Project)));
    }

    [Fact]
    public async Task A_generation_racing_an_edit_either_freezes_the_confirmed_revision_or_is_refused_never_an_unconfirmed_input()
    {
        for (var round = 0; round < 3; round++)
        {
            using var client = await App.LoginAsync();
            var s = await Seed(client, confirm: true);
            var content = await Content(client, s);
            content["goals"]![0]!["text"] = $"Obiettivo concorrente {round}";

            var generate = Generate(client, s, Guid.NewGuid(), s.Revision);
            var edit = Put(client, s, s.Revision, content);
            var responses = await Task.WhenAll(generate, edit);

            var runs = await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document' AND \"ProjectId\" = @p", ("p", s.Project));
            if (responses[0].StatusCode == HttpStatusCode.Accepted)
            {
                // Il comando ha preso il lock per primo: input congelato sulla revisione confermata, l'edit è ammesso (Document attivo).
                Assert.Equal(1, runs);
                Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);
                Assert.Equal(s.Revision, await Scalar<Guid>("SELECT \"InputRevisionId\" FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document' AND \"ProjectId\" = @p", ("p", s.Project)));
            }
            else
            {
                // L'edit ha vinto: la v1 non è più la corrente, nessun run e nessuna invocazione.
                Assert.Equal(HttpStatusCode.Conflict, responses[0].StatusCode);
                Assert.Equal("revision_conflict", (await Json(responses[0])).GetProperty("code").GetString());
                Assert.Equal(0, runs);
            }

            Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a AND \"ValidationState\" = 'PmConfirmed'", ("a", s.Analysis)));
        }
    }

    // ================================================================ guardie: nessuna invocazione dell'agente

    [Fact]
    public async Task Without_a_confirmed_current_revision_it_is_409_analysis_not_confirmed_creates_no_run_and_invokes_no_agent()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: false); // Unvalidated
        var model = new DocumentModel();

        var problem = await AssertProblem(await Generate(client, s, Guid.NewGuid(), s.Revision), HttpStatusCode.Conflict);

        Assert.Equal("analysis_not_confirmed", problem.GetProperty("code").GetString());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'"));
        Assert.False(await Dispatcher(model).RunOnceAsync(Ct)); // niente da prendere in carico
        Assert.Equal(0, model.Calls); // zero invocazioni dell'agente
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
        var project = await Json(await client.GetAsync($"/api/projects/{s.Project}", Ct));
        Assert.Equal(JsonValueKind.Null, project.GetProperty("activeProcessingRunId").ValueKind);
    }

    [Fact]
    public async Task A_revision_that_is_not_the_current_one_is_409_revision_conflict_with_the_current_revision_and_invokes_no_agent()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        var content = await Content(client, s);
        content["goals"]![0]!["text"] = "Obiettivo rivisto dal PM";
        var v2 = (await Json(await Put(client, s, s.Revision, content))).GetProperty("currentRevisionId").GetGuid();
        var model = new DocumentModel();

        // v1 era confermata ma non è più la corrente; un ID sconosciuto non è la corrente.
        foreach (var stale in new[] { s.Revision, Guid.NewGuid() })
        {
            var problem = await AssertProblem(await Generate(client, s, Guid.NewGuid(), stale), HttpStatusCode.Conflict);
            Assert.Equal("revision_conflict", problem.GetProperty("code").GetString());
            Assert.Equal(v2, problem.GetProperty("currentRevisionId").GetGuid());
        }

        var unconfirmed = await AssertProblem(await Generate(client, s, Guid.NewGuid(), v2), HttpStatusCode.Conflict);
        Assert.Equal("analysis_not_confirmed", unconfirmed.GetProperty("code").GetString());
        Assert.False(await Dispatcher(model).RunOnceAsync(Ct));
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'"));
    }

    [Fact]
    public async Task Only_the_current_analysis_of_the_project_can_generate_and_unknown_or_foreign_analyses_are_404()
    {
        using var client = await App.LoginAsync();
        var old = await Seed(client, confirm: true);
        var current = await Seed(client, confirm: true, existingProject: old.Project); // la seconda analisi diventa la corrente
        var other = await Seed(client, confirm: true);
        var model = new DocumentModel();

        var notCurrent = await AssertProblem(await Generate(client, old, Guid.NewGuid(), old.Revision), HttpStatusCode.Conflict);
        Assert.Equal("analysis_not_current", notCurrent.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Generate(client, current with { Analysis = Guid.NewGuid() }, Guid.NewGuid(), current.Revision)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Generate(client, current with { Analysis = other.Analysis }, Guid.NewGuid(), other.Revision)).StatusCode); // analisi di un altro progetto
        Assert.Equal(HttpStatusCode.NotFound, (await Generate(client, current with { Project = Guid.NewGuid() }, Guid.NewGuid(), current.Revision)).StatusCode);
        Assert.False(await Dispatcher(model).RunOnceAsync(Ct));
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("non-un-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_missing_or_malformed_idempotency_key_is_400_and_creates_no_run(string? key)
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(s, "/document-generations")) { Content = JsonContent.Create(new { revisionId = s.Revision }) };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        await AssertProblem(await client.SendAsync(request, Ct), HttpStatusCode.BadRequest);

        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"revisionId\":\"non-un-uuid\"}")]
    [InlineData("{\"revisionId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("")]
    public async Task A_missing_or_malformed_revision_id_is_400_revision_required_and_creates_no_run(string body)
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(s, "/document-generations"))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var problem = await AssertProblem(await client.SendAsync(request, Ct), HttpStatusCode.BadRequest);

        Assert.Contains(problem.GetProperty("code").GetString(), new[] { "revision_required", "malformed_request" });
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'"));
    }

    [Fact]
    public async Task Document_endpoints_require_authentication_and_the_pm_role_and_another_pm_can_read_the_draft()
    {
        var project = Guid.NewGuid();
        var analysis = Guid.NewGuid();
        using var anonymous = App.Anonymous();
        using var withoutRole = App.WithoutPmRole();

        foreach (var client in new[] { anonymous, withoutRole })
        {
            var expected = ReferenceEquals(client, anonymous) ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            var s = new Seeded(project, analysis, Guid.NewGuid());
            Assert.Equal(expected, (await Generate(client, s, Guid.NewGuid(), s.Revision)).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/projects/{project}/draft", Ct)).StatusCode);
        }

        using var first = await App.LoginAsync();
        using var second = await App.LoginAsync(TestApp.SecondPmUser, TestApp.SecondPmPassword);
        var seeded = await Seed(first, confirm: true);
        await Generate(first, seeded, Guid.NewGuid(), seeded.Revision);
        Assert.True(await Dispatcher(new DocumentModel()).RunOnceAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync($"/api/projects/{seeded.Project}/draft", Ct)).StatusCode);
    }

    [Fact]
    public async Task Reading_the_draft_of_a_project_without_one_or_of_an_unknown_project_is_404()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{s.Project}/draft", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/draft", Ct)).StatusCode);
    }

    // ================================================================ errore: l'ultima bozza resta

    [Fact]
    public async Task A_failed_generation_keeps_the_last_successful_draft_and_latest_draft_id_and_creates_no_new_draft()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        await Generate(client, s, Guid.NewGuid(), s.Revision);
        Assert.True(await Dispatcher(new DocumentModel()).RunOnceAsync(Ct));
        var first = await Json(await client.GetAsync($"/api/projects/{s.Project}/draft", Ct));
        var firstId = first.GetProperty("id").GetGuid();

        var invalid = new Dictionary<string, string>
        {
            ["fonte inventata"] = "# Documento\n\nUn fatto [fonte:" + Guid.NewGuid() + "]",
            ["vuoto"] = "   ",
            ["recinto"] = "```markdown\n# Documento\n```",
            ["html"] = "# Documento\n\n<script>alert(1)</script>",
        };
        foreach (var (name, markdown) in invalid)
        {
            var run = await Json(await Generate(client, s, Guid.NewGuid(), s.Revision));
            var model = new DocumentModel(_ => markdown);

            Assert.True(await Dispatcher(model).RunOnceAsync(Ct));

            var failed = await Json(await client.GetAsync($"/api/projects/{s.Project}/processing-runs/{run.GetProperty("id").GetGuid()}", Ct));
            Assert.True(failed.GetProperty("status").GetString() == "Failed", $"{name}: atteso Failed");
            Assert.Equal("invalid_agent_output", failed.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, failed.GetProperty("draftId").ValueKind);
            Assert.Equal(1, model.Calls); // nessuna riparazione
            var still = await Json(await client.GetAsync($"/api/projects/{s.Project}/draft", Ct));
            Assert.Equal(firstId, still.GetProperty("id").GetGuid()); // l'ultima bozza resta quella riuscita
            Assert.Equal(first.GetProperty("markdown").GetString(), still.GetProperty("markdown").GetString());
            Assert.Equal(firstId, (await Json(await client.GetAsync($"/api/projects/{s.Project}", Ct))).GetProperty("latestDraftId").GetGuid());
            Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\""));
        }
    }

    [Fact]
    public async Task A_model_outage_keeps_the_last_draft_a_transient_error_is_retried_and_a_permanent_one_is_failed()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        await Generate(client, s, Guid.NewGuid(), s.Revision);
        Assert.True(await Dispatcher(new DocumentModel()).RunOnceAsync(Ct));
        var firstId = (await Json(await client.GetAsync($"/api/projects/{s.Project}/draft", Ct))).GetProperty("id").GetGuid();

        var run = (await Json(await Generate(client, s, Guid.NewGuid(), s.Revision))).GetProperty("id").GetGuid();
        var transient = new DocumentModel { FailFirst = new HttpRequestException("giù", null, HttpStatusCode.ServiceUnavailable) };
        Assert.True(await Dispatcher(transient).RunOnceAsync(Ct));

        Assert.Equal("Queued", await RunStatus(run)); // ritentabile entro tentativi e deadline, mai un Failed prematuro
        Assert.Equal(firstId, (await Json(await client.GetAsync($"/api/projects/{s.Project}/draft", Ct))).GetProperty("id").GetGuid());

        await Execute("UPDATE \"ProcessingRuns\" SET \"NextAttemptAt\" = now() - interval '1 second' WHERE \"Id\" = @r", ("r", run));
        var permanent = new DocumentModel { FailFirst = new HttpRequestException("segreto-nel-messaggio", null, HttpStatusCode.Unauthorized) };
        Assert.True(await Dispatcher(permanent).RunOnceAsync(Ct));

        var failed = await Json(await client.GetAsync($"/api/projects/{s.Project}/processing-runs/{run}", Ct));
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("model_unauthorized", failed.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("segreto-nel-messaggio", failed.ToString());
        Assert.Equal(firstId, (await Json(await client.GetAsync($"/api/projects/{s.Project}/draft", Ct))).GetProperty("id").GetGuid());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task A_new_successful_generation_replaces_the_latest_draft_and_keeps_the_previous_one_consultable()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        await Generate(client, s, Guid.NewGuid(), s.Revision);
        Assert.True(await Dispatcher(new DocumentModel()).RunOnceAsync(Ct));
        var firstId = (await Json(await client.GetAsync($"/api/projects/{s.Project}/draft", Ct))).GetProperty("id").GetGuid();

        var second = await Json(await Generate(client, s, Guid.NewGuid(), s.Revision));
        Assert.True(await Dispatcher(new DocumentModel(text => "# Seconda versione\n\nTesto rigenerato [fonte:" + DocumentModel.EmbeddedJson(text)["sources"]![0]!["id"] + "]")).RunOnceAsync(Ct));

        var latest = await Json(await client.GetAsync($"/api/projects/{s.Project}/draft", Ct));
        Assert.NotEqual(firstId, latest.GetProperty("id").GetGuid());
        Assert.Equal(second.GetProperty("id").GetGuid(), latest.GetProperty("processingRunId").GetGuid());
        Assert.Contains("Seconda versione", latest.GetProperty("markdown").GetString());
        Assert.Equal(2, await Count("SELECT count(*) FROM \"Drafts\" WHERE \"ProjectId\" = @p", ("p", s.Project))); // la precedente resta consultabile
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\" WHERE \"Id\" = @d", ("d", firstId)));
    }

    // ================================================================ stessa coda e stesso dispatch dell'Analysis

    [Fact]
    public async Task A_claimer_without_a_document_workflow_neither_claims_nor_sweeps_a_document_run_and_one_with_it_does()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        var document = (await Json(await Generate(client, s, Guid.NewGuid(), s.Revision))).GetProperty("id").GetGuid();
        await Execute("UPDATE \"ProcessingRuns\" SET \"DeadlineAt\" = now() - interval '1 second' WHERE \"Id\" = @r", ("r", document));

        // chi esegue solo Analysis non legge né fa fallire per scadenza un run Document: resta Queued
        Assert.Null(await Claim("analysis-only", turns: false, analyses: true, documents: false));
        Assert.Equal("Queued", await RunStatus(document));
        Assert.Null(await Claim("nobody", turns: false, analyses: false, documents: false));
        Assert.Equal("Queued", await RunStatus(document));

        // con il workflow di Document lo sweep lo porta a Failed con il codice di scadenza
        Assert.Null(await Claim("document", turns: false, analyses: false, documents: true));
        Assert.Equal("Failed", await RunStatus(document));
        Assert.Equal("deadline_exceeded", await Scalar<string>("SELECT \"ErrorCode\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", document)));
    }

    [Fact]
    public async Task Analysis_and_document_runs_share_one_fifo_queue_and_the_global_ai_limit_and_never_cross_kinds()
    {
        using var client = await App.LoginAsync();
        var first = await Seed(client, confirm: true);
        var document = (await Json(await Generate(client, first, Guid.NewGuid(), first.Revision))).GetProperty("id").GetGuid();
        var second = await Seed(client, confirm: true);
        var analyze = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{second.Project}/analyses");
        analyze.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var analysis = (await Json(await client.SendAsync(analyze, Ct))).GetProperty("id").GetGuid();

        // chi esegue solo Document prende il Document, mai l'Analysis in coda
        var claimed = await Claim("worker", turns: false, analyses: false, documents: true);
        Assert.Null(claimed!.Run);
        Assert.Equal(document, claimed.Document!.RunId);
        Assert.Equal(1, claimed.Document.AttemptCount);
        Assert.Equal("Running", await RunStatus(document));
        Assert.Equal("Queued", await RunStatus(analysis));

        // limite AI globale: finché il lease è valido nessun altro lavoro, di nessun tipo
        Assert.Null(await Claim("other", turns: false, analyses: true, documents: true));

        // lease scaduto: chi esegue solo Analysis prende l'Analysis e non fa il reclaim del Document
        await Execute("UPDATE \"ProcessingRuns\" SET \"LeaseUntil\" = now() - interval '1 second' WHERE \"Id\" = @r", ("r", document));
        var onlyAnalysis = await Claim("analysis-worker", turns: false, analyses: true, documents: false);
        Assert.Null(onlyAnalysis!.Document);
        Assert.Equal(analysis, onlyAnalysis.Run!.RunId);
        Assert.Equal("Running", await RunStatus(document)); // scaduto ma intatto, non riassegnato

        // scade anche l'Analysis: con entrambi i tipi vince la richiesta più vecchia (il Document) con reclaim, nuovo owner/fence/attempt
        await Execute("UPDATE \"ProcessingRuns\" SET \"LeaseUntil\" = now() - interval '1 second' WHERE \"Id\" = @r", ("r", analysis));
        var reclaimed = await Claim("both", turns: false, analyses: true, documents: true);
        Assert.Equal(document, reclaimed!.Document!.RunId);
        Assert.Equal("both", reclaimed.Document.LeaseOwner);
        Assert.Equal(2, reclaimed.Document.AttemptCount);
        Assert.True(reclaimed.Document.FencingToken > claimed.Document.FencingToken);
    }

    [Fact]
    public async Task A_stale_document_claim_cannot_renew_and_a_transient_failure_requeues_with_backoff_like_the_analysis()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        var document = (await Json(await Generate(client, s, Guid.NewGuid(), s.Revision))).GetProperty("id").GetGuid();
        var claim = (await Claim("worker", turns: false, analyses: false, documents: true))!.Document!;
        using var scope = App.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>();

        Assert.True(await store.RenewAsync(claim, Ct));
        Assert.False(await store.RenewAsync(claim with { FencingToken = claim.FencingToken + 1 }, Ct));
        Assert.False(await store.RenewAsync(claim with { LeaseOwner = "altro" }, Ct));

        var released = await store.ReleaseAfterFailureAsync(claim, ModelFailure.Classify(new HttpRequestException("giù", null, HttpStatusCode.ServiceUnavailable)), "x", "y", Ct);
        Assert.Equal(TurnTransition.Requeued, released.Outcome);
        Assert.Equal("Queued", await RunStatus(document));
        Assert.Equal(TurnTransition.Rejected, (await store.ReleaseAfterFailureAsync(claim, ModelFailure.Classify(new InvalidOperationException()), "x", "y", Ct)).Outcome); // lease già rilasciato
    }

    [Fact]
    public async Task The_document_run_publishes_processing_state_Queued_Running_and_Completed_with_only_real_stages_and_the_draft_id()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, confirm: true);
        using var subscription = App.Services.GetRequiredService<ProjectEventHub>().Subscribe(s.Project);

        var run = await Json(await Generate(client, s, Guid.NewGuid(), s.Revision));
        Assert.True(await Dispatcher(new DocumentModel()).RunOnceAsync(Ct));
        await Task.Delay(300, Ct); // le notifiche di fase sono effimere e non bloccano il grafo

        var states = new List<JsonElement>();
        while (subscription.Reader.TryRead(out var evt))
        {
            if (evt.Name == ProjectEventHub.ProcessingState)
            {
                states.Add(JsonDocument.Parse(evt.Data).RootElement.Clone());
            }
        }

        Assert.Equal("Queued", states[0].GetProperty("status").GetString()); // alla richiesta, post-commit
        Assert.Equal("Document", states[0].GetProperty("kind").GetString());
        Assert.Contains(states, x => x.GetProperty("status").GetString() == "Running");
        var done = states[^1];
        Assert.Equal("Completed", done.GetProperty("status").GetString());
        Assert.Equal(run.GetProperty("id").GetGuid(), done.GetProperty("id").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, done.GetProperty("draftId").ValueKind);
        Assert.All(
            states.Select(x => x.GetProperty("lastObservedStage")).Where(x => x.ValueKind != JsonValueKind.Null),
            stage => Assert.Contains(stage.GetString(), DocumentWorkflow.Stages));
    }

    private async Task<WorkClaim?> Claim(string owner, bool turns, bool analyses, bool documents)
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextWorkAsync(owner, turns, analyses, documents, Ct);
    }

    // ================================================================ supporto

    private sealed record Seeded(Guid Project, Guid Analysis, Guid Revision);

    private static string Url(Seeded s, string suffix = "") => $"/api/projects/{s.Project}/analyses/{s.Analysis}{suffix}";

    private static Task<HttpResponseMessage> Generate(HttpClient client, Seeded s, Guid key, Guid revisionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(s, "/document-generations")) { Content = JsonContent.Create(new { revisionId }) };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonNode> Content(HttpClient client, Seeded s)
    {
        var view = await Json(await client.GetAsync(Url(s), Ct));
        return JsonNode.Parse(view.GetProperty("content").GetRawText())!;
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, Seeded s, Guid ifMatch, JsonNode content)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Url(s, "/preview")) { Content = JsonContent.Create(content) };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient client, Seeded s, Guid ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(s, "/confirm"));
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        return client.SendAsync(request, Ct);
    }

    /// <summary>
    /// Dispatcher con il DocumentWorkflow sul modello finto: lo stesso dispatcher, la stessa coda e lo stesso limite AI globale dei run
    /// di Analysis (T030), ora anche per Kind=Document. Nessun esecutore chat né workflow di analisi: prende solo run Document.
    /// </summary>
    private AiDispatchService Dispatcher(DocumentModel model)
    {
        var scopes = App.Services.GetRequiredService<IServiceScopeFactory>();
        var agent = new DocumentAgent(
            new AgentFactory(new AiOptions { ContextWindowTokens = 32768 }, model, (t, _) => new(t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)),
            new ChatOptions { ModelId = "fake" });
        return new AiDispatchService(
            scopes,
            Options.Create(new ExecutionOptions()),
            NullLogger<AiDispatchService>.Instance,
            runEvents: App.Services.GetRequiredService<ProcessingRunEvents>(),
            documentWorkflow: new DocumentWorkflow(scopes, agent));
    }

    /// <summary>
    /// Progetto con un messaggio PM e un'analisi completata con la sola revisione AI v1 (come farebbe il commit di T029), poi,
    /// con <paramref name="confirm"/>, confermata dal PM tramite l'endpoint reale: nessun modello invocato.
    /// </summary>
    private async Task<Seeded> Seed(HttpClient client, bool confirm, Guid? existingProject = null)
    {
        var project = existingProject ?? (await Json(await client.PostAsJsonAsync("/api/projects", new { name = $"Documento {Guid.NewGuid():N}" }, Ct))).GetProperty("id").GetGuid();
        var post = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text = "Vogliamo un portale clienti." }, Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var analyze = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        analyze.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var queued = await client.SendAsync(analyze, Ct);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var runId = (await Json(queued)).GetProperty("id").GetGuid();

        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ProcessingRuns.SingleAsync(r => r.Id == runId, Ct);
        var message = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == run.ConversationId && m.Role == MessageRole.User && m.Sequence <= run.SourceCutoffSequence)
            .OrderBy(m => m.Sequence).FirstAsync(Ct);

        var user = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["kind"] = "Message", ["messageId"] = message.Id.ToString(), ["messageRole"] = "user", ["revisionId"] = null,
        };
        var sourceId = user["id"]!.GetValue<string>();
        JsonObject Item(string text, string classification, params string[] evidence) => new()
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["text"] = text,
            ["classification"] = classification,
            ["evidenceRefs"] = new JsonArray(evidence.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
        };

        var content = new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["analysisId"] = run.Id.ToString(),
            ["projectId"] = project.ToString(),
            ["sourceCutoffSequence"] = run.SourceCutoffSequence,
            ["validationState"] = "Unvalidated",
            ["sources"] = new JsonArray(user),
            ["goals"] = new JsonArray(Item("Realizzare un portale clienti", "Declared", sourceId)),
            ["contextAndScope"] = new JsonObject { ["overview"] = new JsonArray(), ["included"] = new JsonArray(), ["excluded"] = new JsonArray() },
            ["requirements"] = new JsonArray(Item("Il cliente vede lo stato dei propri ticket", "Declared", sourceId)),
            ["operationalFlows"] = new JsonArray(),
            ["constraints"] = new JsonArray(),
            ["dependencies"] = new JsonArray(),
            ["assumptions"] = new JsonArray(Item("I clienti usano un browser desktop", "Assumed")),
            ["openQuestions"] = new JsonArray(Item("Quanti clienti useranno il portale?", "Inferred")),
            ["conflicts"] = new JsonArray(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["description"] = "La fatturazione è dentro o fuori dal perimetro?",
                ["status"] = "Open",
                ["alternatives"] = new JsonArray(Item("Fatturazione nel portale", "Declared", sourceId), Item("Fatturazione fuori perimetro", "Declared", sourceId)),
                ["resolution"] = null,
            }),
        };

        var revision = new AnalysisRevision
        {
            Id = Guid.NewGuid(),
            AnalysisId = run.Id,
            Version = 1,
            Origin = AnalysisRevisionOrigin.Ai,
            CreatedBy = run.RequestedBy,
            CreatedAt = DateTimeOffset.UtcNow,
            SchemaVersion = AnalysisRevision.CurrentSchemaVersion,
            ContentJson = content.ToJsonString(),
            ValidationState = AnalysisValidationState.Unvalidated,
        };
        db.AnalysisRevisions.Add(revision);
        run.Status = ProcessingRunStatus.Completed;
        run.ResultRevisionId = revision.Id;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(Ct);
        await db.Projects.Where(p => p.Id == project).ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentAnalysisId, run.Id), Ct);

        var seeded = new Seeded(project, run.Id, revision.Id);
        if (confirm)
        {
            Assert.Equal(HttpStatusCode.OK, (await Confirm(client, seeded, revision.Id)).StatusCode);
        }

        return seeded;
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

    /// <summary>
    /// Modello finto del DocumentAgent: ricava dalla richiesta l'intero JSON confermato e scrive un Markdown che cita la prima
    /// fonte con il marcatore <c>[fonte:&lt;id&gt;]</c>, oppure risponde con un testo fisso; <see cref="FailFirst"/> fa fallire
    /// la prima chiamata. Registra richieste e opzioni per verificare l'input e il numero di invocazioni.
    /// </summary>
    internal sealed class DocumentModel(Func<string, string>? reply = null) : IChatClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public List<List<ChatMessage>> Requests { get; } = [];

        public List<ChatOptions> Options { get; } = [];

        public Exception? FailFirst { get; set; }

        /// <summary>Il JSON dell'analisi incorporato nel messaggio utente (dal primo «{» all'ultimo «}»).</summary>
        public static JsonNode EmbeddedJson(string text)
        {
            var start = text.IndexOf('{', StringComparison.Ordinal);
            var end = text.LastIndexOf('}');
            return JsonNode.Parse(text[start..(end + 1)])!;
        }

        private string Reply(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            var list = messages.ToList();
            Requests.Add(list);
            Options.Add(options ?? new ChatOptions());
            if (Interlocked.Increment(ref _calls) == 1 && FailFirst is { } failure)
            {
                throw failure;
            }

            var text = string.Join("\n", list.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
            return reply is not null ? reply(text) : Nominal(text);
        }

        private static string Nominal(string request)
        {
            var json = EmbeddedJson(request);
            var source = json["sources"]![0]!["id"]!.GetValue<string>();
            return $"# Documento di progetto\n\n## Obiettivi\n\n- {json["goals"]![0]!["text"]!.GetValue<string>()} [fonte:{source}]\n";
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply(messages, options))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, Reply(messages, options));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
