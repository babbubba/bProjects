using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T010 — contratto HTTP di progetti e conversazioni su PostgreSQL reale (nessun mock del database).
/// Le prove del catalogo progetti (T012) sono eseguibili subito; quelle di messaggi e turni chat
/// sono verdi dopo T013 (messaggi, turni, replay); nessuna prova qui invoca il modello.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ProjectChatContractTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private const int MaxMessageLength = 65_536;

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

    // ---------------------------------------------------------------- autorizzazione

    [Fact]
    public async Task Projects_endpoints_require_authentication()
    {
        using var client = App.Anonymous();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/projects", new { name = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/projects/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/projects/{id}/messages", Ct)).StatusCode);
    }

    [Fact]
    public async Task Projects_endpoints_reject_authenticated_users_without_the_pm_role()
    {
        using var client = App.WithoutPmRole();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/projects", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/projects", new { name = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Every_pm_sees_and_reopens_the_projects_of_the_other_pm()
    {
        using var first = await App.LoginAsync();
        using var second = await App.LoginAsync(TestApp.SecondPmUser, TestApp.SecondPmPassword);
        var created = await CreateProject(first, $"Condiviso {Guid.NewGuid():N}");

        var reopened = await second.GetAsync($"/api/projects/{created.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.Equal(created.Id, (await Json(reopened)).GetProperty("id").GetGuid());
        Assert.Contains(await ListAll(second), p => p.GetProperty("id").GetGuid() == created.Id);
    }

    // ---------------------------------------------------------------- creazione e lettura

    [Fact]
    public async Task Create_returns_201_location_and_a_complete_project_view()
    {
        using var client = await App.LoginAsync();

        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Portale clienti", description = "Descrizione del portale" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var view = await Json(response);
        var id = view.GetProperty("id").GetGuid();
        Assert.EndsWith($"/api/projects/{id}", response.Headers.Location!.ToString());
        Assert.NotEqual(Guid.Empty, view.GetProperty("conversationId").GetGuid());
        Assert.Equal("Portale clienti", view.GetProperty("name").GetString());
        Assert.Equal("Descrizione del portale", view.GetProperty("description").GetString());
        AssertEmptyProjectState(view);
    }

    [Fact]
    public async Task Reopening_a_project_returns_the_same_data_without_starting_any_processing()
    {
        using var client = await App.LoginAsync();
        var created = await CreateProject(client, $"Riapertura {Guid.NewGuid():N}", "con descrizione");

        var response = await client.GetAsync($"/api/projects/{created.Id}", Ct);

        // Il modello interno non è raggiungibile e il client AI fallirebbe se costruito: la lettura funziona comunque.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await Json(response);
        Assert.Equal(created.Name, view.GetProperty("name").GetString());
        Assert.Equal("con descrizione", view.GetProperty("description").GetString());
        Assert.Equal(created.ConversationId, view.GetProperty("conversationId").GetGuid());
        AssertEmptyProjectState(view);
        // Il database è condiviso dai test della classe: si verifica la conversazione di questo progetto.
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ChatTurns\" WHERE \"ConversationId\" = @c", ("c", created.ConversationId)));
    }

    [Fact]
    public async Task Create_persists_the_project_and_exactly_one_conversation_in_postgresql()
    {
        using var client = await App.LoginAsync();
        var name = $"Persistito {Guid.NewGuid():N}";

        var created = await CreateProject(client, name, "d");

        Assert.Equal(1, await Count("SELECT count(*) FROM \"Projects\" WHERE \"Id\" = @p AND \"Name\" = @n", ("p", created.Id), ("n", name)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Conversations\" WHERE \"ProjectId\" = @p AND \"Id\" = @c AND \"LastSequence\" = 0", ("p", created.Id), ("c", created.ConversationId)));
    }

    [Fact]
    public async Task Projects_with_the_same_name_are_distinct_and_both_listed()
    {
        using var client = await App.LoginAsync();
        var name = $"Omonimo {Guid.NewGuid():N}";

        var first = await CreateProject(client, name);
        var second = await CreateProject(client, name);

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.ConversationId, second.ConversationId);
        var homonyms = (await ListAll(client)).Where(p => p.GetProperty("name").GetString() == name).ToList();
        Assert.Equal(2, homonyms.Count);
        Assert.Equal(2, homonyms.Select(p => p.GetProperty("id").GetGuid()).Distinct().Count());
        Assert.All(homonyms, p => Assert.True(p.TryGetProperty("createdAt", out _), "createdAt serve a distinguere i progetti omonimi"));
    }

    [Fact]
    public async Task Resources_are_isolated_between_projects()
    {
        using var client = await App.LoginAsync();
        var a = await CreateProject(client, $"Isolamento A {Guid.NewGuid():N}");
        var b = await CreateProject(client, $"Isolamento B {Guid.NewGuid():N}");

        Assert.NotEqual(a.ConversationId, b.ConversationId);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Conversations\" WHERE \"ProjectId\" = @p", ("p", a.Id)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Conversations\" WHERE \"ProjectId\" = @p", ("p", b.Id)));
        Assert.Equal(a.Name, (await Json(await client.GetAsync($"/api/projects/{a.Id}", Ct))).GetProperty("name").GetString());
        Assert.Equal(b.Name, (await Json(await client.GetAsync($"/api/projects/{b.Id}", Ct))).GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("non-un-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Unknown_project_is_not_found(string id)
    {
        using var client = await App.LoginAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_project_does_not_expose_messages()
    {
        using var client = await App.LoginAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/messages", Ct)).StatusCode);
    }

    [Fact]
    public async Task Projects_survive_an_application_restart()
    {
        var connectionString = database.ConnectionString;
        TestEnvironment.RequirePostgres();
        Guid id;
        var name = $"Riavvio {Guid.NewGuid():N}";
        await using (var first = await ApiHarness.StartAsync(connectionString))
        {
            using var client = await first.LoginAsync();
            id = (await CreateProject(client, name, "dopo il riavvio")).Id;
        }

        await using var restarted = await ApiHarness.StartAsync(connectionString, prepare: false);
        using var reopened = await restarted.LoginAsync();
        var view = await Json(await reopened.GetAsync($"/api/projects/{id}", Ct));

        Assert.Equal(name, view.GetProperty("name").GetString());
        Assert.Equal("dopo il riavvio", view.GetProperty("description").GetString());
    }

    // ---------------------------------------------------------------- validazione

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n ")]
    public async Task Create_rejects_a_blank_name_and_persists_nothing(string? name)
    {
        using var client = await App.LoginAsync();
        var before = await Count("SELECT count(*) FROM \"Projects\"");

        var response = await client.PostAsJsonAsync("/api/projects", new { name }, Ct);

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(before, await Count("SELECT count(*) FROM \"Projects\""));
    }

    [Fact]
    public async Task Create_enforces_the_name_and_description_limits()
    {
        using var client = await App.LoginAsync();

        await AssertProblem(await client.PostAsJsonAsync("/api/projects", new { name = new string('n', 201) }, Ct), HttpStatusCode.UnprocessableEntity);
        await AssertProblem(await client.PostAsJsonAsync("/api/projects", new { name = "ok", description = new string('d', 4001) }, Ct), HttpStatusCode.UnprocessableEntity);

        var atLimit = await client.PostAsJsonAsync("/api/projects", new { name = new string('n', 200), description = new string('d', 4000) }, Ct);
        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
    }

    [Fact]
    public async Task Create_treats_a_missing_or_blank_description_as_absent()
    {
        using var client = await App.LoginAsync();

        var without = await Json(await client.PostAsJsonAsync("/api/projects", new { name = "Senza descrizione" }, Ct));
        var blank = await Json(await client.PostAsJsonAsync("/api/projects", new { name = "Descrizione vuota", description = "  " }, Ct));

        Assert.Equal(JsonValueKind.Null, without.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, blank.GetProperty("description").ValueKind);
    }

    [Fact]
    public async Task Create_rejects_a_malformed_body()
    {
        using var client = await App.LoginAsync();

        var response = await client.PostAsync("/api/projects", new StringContent("{ non json", System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------- paginazione

    [Fact]
    public async Task List_is_paginated_newest_first_without_gaps_or_duplicates()
    {
        using var client = await App.LoginAsync();
        var tag = Guid.NewGuid().ToString("N");
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            created.Add((await CreateProject(client, $"Pagina {tag} {i}")).Id);
        }

        var first = await Json(await client.GetAsync("/api/projects?limit=2", Ct));
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, first.GetProperty("nextCursor").ValueKind);

        var all = await ListAll(client, pageSize: 2);
        var ids = all.Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(created, id => Assert.Contains(id, ids));
        var times = all.Select(p => p.GetProperty("createdAt").GetDateTimeOffset()).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);
        Assert.True(ids.IndexOf(created[4]) < ids.IndexOf(created[0]), "il progetto più recente precede il più vecchio");
    }

    [Fact]
    public async Task Last_page_has_no_next_cursor()
    {
        using var client = await App.LoginAsync();
        await CreateProject(client, $"Ultima {Guid.NewGuid():N}");

        var page = await Json(await client.GetAsync("/api/projects?limit=100", Ct));

        Assert.True(page.GetProperty("items").GetArrayLength() <= 100);
        if (page.GetProperty("items").GetArrayLength() < 100)
        {
            Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        }
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=-1")]
    public async Task List_rejects_an_out_of_range_limit(string query)
    {
        using var client = await App.LoginAsync();

        await AssertProblem(await client.GetAsync($"/api/projects?{query}", Ct), HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task List_rejects_a_malformed_cursor()
    {
        using var client = await App.LoginAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/projects?cursor=%25%25non-valido", Ct)).StatusCode);
    }

    // ---------------------------------------------------------------- messaggi (T013)

    [Fact]
    public async Task Pm_message_is_saved_exactly_with_server_side_role_and_author_while_the_model_is_offline()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client, $"Chat {Guid.NewGuid():N}");
        var text = "  Ciao,\n\tvorrei un portale ✓ 日本語  \n";
        var clientMessageId = Guid.NewGuid();

        var response = await PostMessage(client, project.Id, clientMessageId, text, extra: new { role = "assistant", authorId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        var message = body.GetProperty("message");
        Assert.Equal(text, message.GetProperty("text").GetString());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal(1, message.GetProperty("sequence").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, message.GetProperty("authorId").ValueKind);
        Assert.NotEqual(Guid.Empty, body.GetProperty("chatTurnId").GetGuid());
        Assert.Equal(text, await Scalar<string>("SELECT \"Body\" FROM \"ConversationMessages\" WHERE \"ClientMessageId\" = @c", ("c", clientMessageId)));
    }

    [Fact]
    public async Task Saving_a_message_queues_exactly_one_chat_turn_with_the_cutoff_of_the_trigger()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client, $"Turno {Guid.NewGuid():N}");
        var first = await Json(await PostMessage(client, project.Id, Guid.NewGuid(), "uno"));
        var second = await Json(await PostMessage(client, project.Id, Guid.NewGuid(), "due"));

        var turnId = second.GetProperty("chatTurnId").GetGuid();
        var turn = await Json(await client.GetAsync($"/api/projects/{project.Id}/chat-turns/{turnId}", Ct));

        Assert.Equal(2, turn.GetProperty("sourceCutoffSequence").GetInt64());
        Assert.Equal(second.GetProperty("message").GetProperty("id").GetGuid(), turn.GetProperty("triggerMessageId").GetGuid());
        Assert.Equal("Queued", turn.GetProperty("status").GetString());
        Assert.Equal(0, turn.GetProperty("attemptCount").GetInt32());
        Assert.Equal(3, turn.GetProperty("maxAttempts").GetInt32());
        Assert.NotEqual(first.GetProperty("chatTurnId").GetGuid(), turnId);
        Assert.Equal(2, await Count("SELECT count(*) FROM \"ChatTurns\" WHERE \"ConversationId\" = @c", ("c", project.ConversationId)));
    }

    [Fact]
    public async Task A_pm_message_exposes_the_chat_turn_it_triggered_both_on_save_and_on_listing()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client, $"Collegamento {Guid.NewGuid():N}");
        var body = await Json(await PostMessage(client, project.Id, Guid.NewGuid(), "uno"));
        var turnId = body.GetProperty("chatTurnId").GetGuid();

        Assert.Equal(turnId, body.GetProperty("message").GetProperty("chatTurnId").GetGuid());
        var page = await Json(await client.GetAsync($"/api/projects/{project.Id}/messages", Ct));
        Assert.Equal(turnId, page.GetProperty("items")[0].GetProperty("chatTurnId").GetGuid());
    }

    [Fact]
    public async Task Replaying_the_same_client_message_id_returns_the_same_message_and_turn_without_enqueueing()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client, $"Replay {Guid.NewGuid():N}");
        var clientMessageId = Guid.NewGuid();

        var first = await PostMessage(client, project.Id, clientMessageId, "stesso testo");
        var replay = await PostMessage(client, project.Id, clientMessageId, "stesso testo");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var a = await Json(first);
        var b = await Json(replay);
        Assert.Equal(a.GetProperty("message").GetProperty("id").GetGuid(), b.GetProperty("message").GetProperty("id").GetGuid());
        Assert.Equal(a.GetProperty("chatTurnId").GetGuid(), b.GetProperty("chatTurnId").GetGuid());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"ConversationId\" = @c", ("c", project.ConversationId)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ChatTurns\" WHERE \"ConversationId\" = @c", ("c", project.ConversationId)));
    }

    [Fact]
    public async Task Same_client_message_id_with_different_text_is_an_idempotency_conflict()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client, $"Conflitto {Guid.NewGuid():N}");
        var clientMessageId = Guid.NewGuid();
        await PostMessage(client, project.Id, clientMessageId, "originale");

        var conflict = await PostMessage(client, project.Id, clientMessageId, "testo diverso");

        var problem = await AssertProblem(conflict, HttpStatusCode.Conflict);
        Assert.Equal("idempotency_conflict", problem.GetProperty("code").GetString());
        Assert.Equal("originale", await Scalar<string>("SELECT \"Body\" FROM \"ConversationMessages\" WHERE \"ClientMessageId\" = @c", ("c", clientMessageId)));
    }

    [Fact]
    public async Task Concurrent_posts_of_the_same_client_message_id_create_a_single_message()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client, $"Concorrenza {Guid.NewGuid():N}");
        var clientMessageId = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostMessage(client, project.Id, clientMessageId, "una volta sola")));

        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"ConversationId\" = @c", ("c", project.ConversationId)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ChatTurns\" WHERE \"ConversationId\" = @c", ("c", project.ConversationId)));
    }

    [Fact]
    public async Task Message_validation_rejects_blank_oversized_and_unidentified_input()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client, $"Validazione {Guid.NewGuid():N}");

        await AssertProblem(await PostMessage(client, project.Id, Guid.NewGuid(), "   \n\t"), HttpStatusCode.UnprocessableEntity);
        await AssertProblem(await PostMessage(client, project.Id, Guid.NewGuid(), new string('x', MaxMessageLength + 1)), HttpStatusCode.UnprocessableEntity);
        await AssertProblem(await client.PostAsJsonAsync($"/api/projects/{project.Id}/messages", new { text = "senza id" }, Ct), HttpStatusCode.UnprocessableEntity);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"ConversationId\" = @c", ("c", project.ConversationId)));

        var atLimit = await PostMessage(client, project.Id, Guid.NewGuid(), new string('x', MaxMessageLength));
        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
    }

    [Fact]
    public async Task Messages_are_listed_in_order_with_cursors_and_only_for_their_own_project()
    {
        using var client = await App.LoginAsync();
        var a = await CreateProject(client, $"Cursori A {Guid.NewGuid():N}");
        var b = await CreateProject(client, $"Cursori B {Guid.NewGuid():N}");
        for (var i = 1; i <= 5; i++)
        {
            await PostMessage(client, a.Id, Guid.NewGuid(), $"a{i}");
        }

        await PostMessage(client, b.Id, Guid.NewGuid(), "solo b");

        var firstPage = await Json(await client.GetAsync($"/api/projects/{a.Id}/messages?limit=2", Ct));
        Assert.Equal(["a1", "a2"], firstPage.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("text").GetString()).ToArray());
        var next = firstPage.GetProperty("nextAfterSequence").GetInt64();
        Assert.Equal(2, next);

        var rest = await Json(await client.GetAsync($"/api/projects/{a.Id}/messages?afterSequence={next}&limit=100", Ct));
        Assert.Equal(["a3", "a4", "a5"], rest.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("text").GetString()).ToArray());

        var other = await Json(await client.GetAsync($"/api/projects/{b.Id}/messages", Ct));
        Assert.Equal(["solo b"], other.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("text").GetString()).ToArray());
    }

    [Fact]
    public async Task A_chat_turn_is_not_readable_through_another_project()
    {
        using var client = await App.LoginAsync();
        var a = await CreateProject(client, $"Turno A {Guid.NewGuid():N}");
        var b = await CreateProject(client, $"Turno B {Guid.NewGuid():N}");
        var turnId = (await Json(await PostMessage(client, a.Id, Guid.NewGuid(), "ciao"))).GetProperty("chatTurnId").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/projects/{a.Id}/chat-turns/{turnId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{b.Id}/chat-turns/{turnId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_dialogue_message_never_starts_a_formal_command()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client, $"Dialogo {Guid.NewGuid():N}");

        var response = await PostMessage(client, project.Id, Guid.NewGuid(), "Analizza il progetto e genera subito il documento Markdown");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var view = await Json(await client.GetAsync($"/api/projects/{project.Id}", Ct));
        Assert.Equal(1, view.GetProperty("latestSequence").GetInt64());
        Assert.Equal(1, view.GetProperty("latestUserSequence").GetInt64());
        Assert.Equal(1, view.GetProperty("pendingChatTurnIds").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("activeProcessingRunId").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("latestAnalysisRunId").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("latestDraftId").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{project.Id}/draft", Ct)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_posts_with_different_client_message_ids_get_unique_contiguous_sequences()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client, $"Sequenze {Guid.NewGuid():N}");

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => PostMessage(client, project.Id, Guid.NewGuid(), $"m{i}")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var sequences = (await Task.WhenAll(responses.Select(async r => (await Json(r)).GetProperty("message").GetProperty("sequence").GetInt64())))
            .Order().ToArray();
        Assert.Equal(Enumerable.Range(1, 12).Select(i => (long)i).ToArray(), sequences);
        Assert.Equal(12, await Scalar<long>("SELECT \"LastSequence\" FROM \"Conversations\" WHERE \"Id\" = @c", ("c", project.ConversationId)));
        // Ogni turno ha come cutoff la sequenza del proprio trigger, anche con invii simultanei.
        Assert.Equal(0, await Count(
            "SELECT count(*) FROM \"ChatTurns\" t JOIN \"ConversationMessages\" m ON m.\"Id\" = t.\"TriggerMessageId\" WHERE t.\"ConversationId\" = @c AND t.\"SourceCutoffSequence\" <> m.\"Sequence\"",
            ("c", project.ConversationId)));
    }

    [Fact]
    public async Task A_turn_cutoff_does_not_move_when_later_messages_arrive()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client, $"Cutoff {Guid.NewGuid():N}");
        var first = await Json(await PostMessage(client, project.Id, Guid.NewGuid(), "uno"));
        await PostMessage(client, project.Id, Guid.NewGuid(), "due");
        await PostMessage(client, project.Id, Guid.NewGuid(), "tre");

        var turn = await Json(await client.GetAsync($"/api/projects/{project.Id}/chat-turns/{first.GetProperty("chatTurnId").GetGuid()}", Ct));

        Assert.Equal(1, turn.GetProperty("sourceCutoffSequence").GetInt64());
    }

    [Fact]
    public async Task Message_endpoints_require_authentication_and_the_pm_role()
    {
        using var anonymous = App.Anonymous();
        using var noRole = App.WithoutPmRole();
        var id = Guid.NewGuid();
        var body = new { clientMessageId = Guid.NewGuid(), text = "ciao" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/projects/{id}/messages", body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/projects/{id}/chat-turns/{Guid.NewGuid()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRole.PostAsJsonAsync($"/api/projects/{id}/messages", body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRole.GetAsync($"/api/projects/{id}/messages", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRole.GetAsync($"/api/projects/{id}/chat-turns/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Posting_to_an_unknown_project_is_not_found_and_persists_nothing()
    {
        using var client = await App.LoginAsync();
        var before = await Count("SELECT count(*) FROM \"ConversationMessages\"");

        var response = await PostMessage(client, Guid.NewGuid(), Guid.NewGuid(), "nessun progetto");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await Count("SELECT count(*) FROM \"ConversationMessages\""));
    }

    [Fact]
    public async Task Another_pm_can_read_the_dialogue_and_the_author_is_the_sender()
    {
        using var first = await App.LoginAsync();
        using var second = await App.LoginAsync(TestApp.SecondPmUser, TestApp.SecondPmPassword);
        var project = await CreateProject(first, $"Autore {Guid.NewGuid():N}");
        await PostMessage(first, project.Id, Guid.NewGuid(), "dal primo");
        await PostMessage(second, project.Id, Guid.NewGuid(), "dal secondo");

        var page = await Json(await second.GetAsync($"/api/projects/{project.Id}/messages", Ct));

        var authors = page.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("authorDisplayName").GetString()).ToArray();
        Assert.Equal([TestApp.PmUser, TestApp.SecondPmUser], authors);
    }

    [Fact]
    public async Task Messages_and_turns_survive_an_application_restart_without_changing_state()
    {
        TestEnvironment.RequirePostgres();
        Guid projectId;
        Guid turnId;
        var text = "  prima del riavvio \n";
        await using (var first = await ApiHarness.StartAsync(database.ConnectionString))
        {
            using var client = await first.LoginAsync();
            projectId = (await CreateProject(client, $"Riavvio chat {Guid.NewGuid():N}")).Id;
            turnId = (await Json(await PostMessage(client, projectId, Guid.NewGuid(), text))).GetProperty("chatTurnId").GetGuid();
        }

        await using var restarted = await ApiHarness.StartAsync(database.ConnectionString, prepare: false);
        using var reopened = await restarted.LoginAsync();
        var messages = await Json(await reopened.GetAsync($"/api/projects/{projectId}/messages", Ct));
        var turn = await Json(await reopened.GetAsync($"/api/projects/{projectId}/chat-turns/{turnId}", Ct));

        Assert.Equal(text, messages.GetProperty("items")[0].GetProperty("text").GetString());
        // Nessun worker AI registrato: il turno resta Queued, con zero tentativi consumati.
        Assert.Equal("Queued", turn.GetProperty("status").GetString());
        Assert.Equal(0, turn.GetProperty("attemptCount").GetInt32());
    }

    // ---------------------------------------------------------------- supporto

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Created(Guid Id, Guid ConversationId, string Name);

    private static async Task<Created> CreateProject(HttpClient client, string name, string? description = null)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name, description }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var view = await Json(response);
        return new Created(view.GetProperty("id").GetGuid(), view.GetProperty("conversationId").GetGuid(), name);
    }

    private static Task<HttpResponseMessage> PostMessage(HttpClient client, Guid projectId, Guid clientMessageId, string text, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["clientMessageId"] = clientMessageId, ["text"] = text };
        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                body[property.Name] = property.GetValue(extra);
            }
        }

        return client.PostAsJsonAsync($"/api/projects/{projectId}/messages", body, Ct);
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

    private static void AssertEmptyProjectState(JsonElement view)
    {
        Assert.Equal(0, view.GetProperty("latestSequence").GetInt64());
        Assert.Equal(0, view.GetProperty("latestUserSequence").GetInt64());
        Assert.Equal(0, view.GetProperty("pendingChatTurnIds").GetArrayLength());
        foreach (var field in new[] { "activeProcessingRunId", "latestAnalysisRunId", "currentAnalysisId", "currentRevisionId", "validationState", "latestDraftId" })
        {
            Assert.Equal(JsonValueKind.Null, view.GetProperty(field).ValueKind);
        }
    }

    private static async Task<List<JsonElement>> ListAll(HttpClient client, int pageSize = 100)
    {
        var items = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var url = $"/api/projects?limit={pageSize}" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await Json(await client.GetAsync(url, Ct));
            items.AddRange(page.GetProperty("items").EnumerateArray());
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        return items;
    }

    private async Task<long> Count(string sql, params (string Name, object Value)[] parameters) => await Scalar<long>(sql, parameters);

    private async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
