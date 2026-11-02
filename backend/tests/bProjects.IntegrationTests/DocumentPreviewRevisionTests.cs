using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T060 (I4) — version dispatch di modifica (PUT preview) e conferma (POST confirm) su revisioni AnalysisContent 1.0.0 e 1.1.0, su PostgreSQL
/// reale e senza modello: lo schema della revisione si conserva (nessuna conversione), le fonti documentali sono immutabili per il PM, una
/// modifica aggiunge soltanto una fonte PmRevision, Documented non diventa mai Declared/confermato dal solo passaggio di conferma, i conflitti
/// si risolvono solo con motivo e fonte PmRevision, If-Match/409 restano quelli della 001 e la generazione richiede la conferma.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentPreviewRevisionTests : IAsyncLifetime
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

    // ---------------------------------------------------------------- 1.1: modifica

    [Fact]
    public async Task A_put_on_a_1_1_revision_creates_a_1_1_pm_revision_with_one_server_pm_source_and_immutable_document_sources()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, "1.1.0");
        var originalJson = await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision));
        var content = await Content(client, s);
        var documentedId = content["requirements"]![0]!["id"]!.GetValue<string>();
        content["goals"]![0]!["text"] = "Realizzare il portale clienti entro l'anno";

        var response = await Put(client, s, s.Revision, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await Json(response);
        var newRevision = view.GetProperty("currentRevisionId").GetGuid();
        Assert.Equal("1.1.0", view.GetProperty("schemaVersion").GetString());
        Assert.Equal("1.1.0", view.GetProperty("content").GetProperty("schemaVersion").GetString());
        Assert.Equal(2, view.GetProperty("version").GetInt32());
        Assert.Equal("PmEdit", view.GetProperty("origin").GetString());
        Assert.Equal("Unvalidated", view.GetProperty("validationState").GetString());
        Assert.Equal("1.1.0", await Scalar<string>("SELECT \"SchemaVersion\" FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", newRevision)));

        var sources = view.GetProperty("content").GetProperty("sources").EnumerateArray().ToList();
        var pm = Assert.Single(sources, x => x.GetProperty("kind").GetString() == "PmRevision");
        Assert.Equal(newRevision, pm.GetProperty("revisionId").GetGuid());
        var pmId = pm.GetProperty("id").GetGuid();
        var result = view.GetProperty("content");
        Assert.Contains(pmId, result.GetProperty("goals")[0].GetProperty("evidenceRefs").EnumerateArray().Select(e => e.GetGuid()));

        // La fonte documentale resta identica, e l'item Documented non toccato non cita il PM né cambia classificazione.
        var passage = Assert.Single(sources, x => x.GetProperty("kind").GetString() == "DocumentPassage");
        Assert.Equal(content["sources"]!.AsArray().Single(x => x!["kind"]!.GetValue<string>() == "DocumentPassage")!.ToJsonString(), JsonNode.Parse(passage.GetRawText())!.ToJsonString());
        var untouched = result.GetProperty("requirements").EnumerateArray().Single(i => i.GetProperty("id").GetString() == documentedId);
        Assert.Equal("Documented", untouched.GetProperty("classification").GetString());
        Assert.DoesNotContain(pmId, untouched.GetProperty("evidenceRefs").EnumerateArray().Select(e => e.GetGuid()));

        // L'originale AI resta intatto e leggibile come 1.1.0.
        Assert.Equal(originalJson, await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision)));
        var original = await Json(await client.GetAsync(Url(s, "/revisions/1"), Ct));
        Assert.Equal("1.1.0", original.GetProperty("schemaVersion").GetString());
        Assert.Equal("Ai", original.GetProperty("origin").GetString());
    }

    [Fact]
    public async Task Editing_a_documented_item_keeps_documented_and_its_passage_and_adds_the_pm_source_without_declaring_it()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, "1.1.0");
        var content = await Content(client, s);
        content["requirements"]![0]!["text"] = "Il portale mostra lo stato e la priorita dei ticket";

        var view = await Json(await Put(client, s, s.Revision, content));

        var item = view.GetProperty("content").GetProperty("requirements")[0];
        Assert.Equal("Documented", item.GetProperty("classification").GetString());
        var refs = item.GetProperty("evidenceRefs").EnumerateArray().Select(e => e.GetGuid()).ToList();
        var sources = view.GetProperty("content").GetProperty("sources").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid(), x => x.GetProperty("kind").GetString());
        Assert.Contains(refs, r => sources[r] == "DocumentPassage");
        Assert.Contains(refs, r => sources[r] == "PmRevision");
    }

    [Fact]
    public async Task A_pm_cannot_add_alter_or_remove_document_sources_nor_claim_documented_without_a_passage_nor_change_the_schema()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, "1.1.0");

        async Task Rejected(Action<JsonNode> mutate, string fieldPrefix)
        {
            var content = await Content(client, s);
            mutate(content);
            var response = await Put(client, s, s.Revision, content);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await Json(response);
            Assert.Equal("preview_invalid", problem.GetProperty("code").GetString());
            Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString()!.StartsWith(fieldPrefix, StringComparison.Ordinal));
        }

        JsonNode Passage(JsonNode c) => c["sources"]!.AsArray().Single(x => x!["kind"]!.GetValue<string>() == "DocumentPassage")!;

        await Rejected(c => Passage(c)["quote"] = "testo inventato", "sources");
        await Rejected(c => Passage(c)["blockId"] = Guid.NewGuid().ToString(), "sources");
        await Rejected(c => c["sources"]!.AsArray().Remove(Passage(c)), "sources");
        await Rejected(c => c["sources"]!.AsArray().Add(NewPassage()), "sources");
        await Rejected(c => c["schemaVersion"] = "1.0.0", "schemaVersion");
        await Rejected(c => c["schemaVersion"] = "2.0.0", "schemaVersion");
        // Documented senza alcun passaggio fra le fonti dell'item: il PM non lo eleva.
        await Rejected(c =>
        {
            var goal = c["goals"]![0]!;
            goal["classification"] = "Documented";
        }, "goals[0]");
        await Rejected(c => c["sourceCutoffSequence"] = 99, "sourceCutoffSequence");
        await Rejected(c => c["markdown"] = "# non ammesso", "JSON non valido per AnalysisContent 1.1.0");

        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
    }

    [Fact]
    public async Task A_1_0_body_on_a_1_1_revision_and_a_1_1_body_on_a_1_0_revision_are_rejected_and_never_converted()
    {
        using var client = await App.LoginAsync();
        var v11 = await Seed(client, "1.1.0");
        var v10 = await Seed(client, "1.0.0");

        var wrong10 = await Content(client, v10);
        wrong10["schemaVersion"] = "1.1.0";
        var wrong11 = await Content(client, v11);
        wrong11["schemaVersion"] = "1.0.0";

        var a = await Put(client, v10, v10.Revision, wrong10);
        var b = await Put(client, v11, v11.Revision, wrong11);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, a.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, b.StatusCode);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", v10.Analysis)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", v11.Analysis)));
    }

    [Fact]
    public async Task A_legacy_1_0_revision_is_still_edited_and_confirmed_as_1_0_0()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, "1.0.0");
        var content = await Content(client, s);
        content["goals"]![0]!["text"] = "Obiettivo corretto";
        var edited = await Json(await Put(client, s, s.Revision, content));
        Assert.Equal("1.0.0", edited.GetProperty("schemaVersion").GetString());
        var confirmed = await Json(await Confirm(client, s, edited.GetProperty("currentRevisionId").GetGuid()));
        Assert.Equal("1.0.0", confirmed.GetProperty("schemaVersion").GetString());
        Assert.Equal("PmConfirmed", confirmed.GetProperty("validationState").GetString());
    }

    // ---------------------------------------------------------------- 1.1: conflitti

    [Fact]
    public async Task A_1_1_conflict_is_resolved_only_with_a_reason_a_selected_alternative_and_the_server_pm_source()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, "1.1.0");

        async Task<HttpResponseMessage> Resolve(Action<JsonObject> tweak)
        {
            var content = await Content(client, s);
            var conflict = content["conflicts"]![0]!;
            var chosen = conflict["alternatives"]![0]!["id"]!.GetValue<string>();
            conflict["status"] = "Resolved";
            var resolution = new JsonObject
            {
                ["selectedAlternativeIds"] = new JsonArray(chosen),
                ["reason"] = "Il documento e piu recente",
                ["evidenceRefs"] = new JsonArray(),
            };
            tweak(resolution);
            conflict["resolution"] = resolution;
            return await Put(client, s, s.Revision, content);
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Resolve(r => r["reason"] = "  ")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Resolve(r => r["selectedAlternativeIds"] = new JsonArray())).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Resolve(r => r["selectedAlternativeIds"] = new JsonArray(Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));

        var ok = await Resolve(_ => { });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var view = await Json(ok);
        var resolution = view.GetProperty("content").GetProperty("conflicts")[0].GetProperty("resolution");
        var sources = view.GetProperty("content").GetProperty("sources").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid(), x => x.GetProperty("kind").GetString());
        Assert.Contains(resolution.GetProperty("evidenceRefs").EnumerateArray().Select(e => e.GetGuid()), r => sources[r] == "PmRevision");
        Assert.Equal(2, view.GetProperty("content").GetProperty("conflicts")[0].GetProperty("alternatives").GetArrayLength());
    }

    [Fact]
    public async Task A_1_1_conflict_alternative_cannot_be_removed_by_the_pm()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, "1.1.0");
        var content = await Content(client, s);
        content["conflicts"]![0]!["alternatives"]!.AsArray().RemoveAt(0);

        var response = await Put(client, s, s.Revision, content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
    }

    // ---------------------------------------------------------------- 1.1: conferma

    [Fact]
    public async Task Confirming_a_1_1_revision_changes_only_the_state_keeps_documented_items_documented_and_is_idempotent()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, "1.1.0");
        var before = await Content(client, s);

        var response = await Confirm(client, s, s.Revision);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await Json(response);
        Assert.Equal("PmConfirmed", view.GetProperty("validationState").GetString());
        Assert.Equal("1.1.0", view.GetProperty("schemaVersion").GetString());
        Assert.Equal(s.Revision, view.GetProperty("currentRevisionId").GetGuid());
        Assert.True(view.GetProperty("canGenerate").GetBoolean());
        var after = JsonNode.Parse(view.GetProperty("content").GetRawText())!;
        Assert.Equal("PmConfirmed", after["validationState"]!.GetValue<string>());
        before["validationState"] = "PmConfirmed";
        Assert.True(JsonNode.DeepEquals(before, after), "la conferma può cambiare soltanto validationState");
        Assert.Equal("Documented", after["requirements"]![0]!["classification"]!.GetValue<string>());
        Assert.DoesNotContain(after["sources"]!.AsArray(), x => x!["kind"]!.GetValue<string>() == "PmRevision");
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));

        var again = await Confirm(client, s, s.Revision);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
    }

    [Fact]
    public async Task An_edit_after_confirming_a_1_1_revision_creates_a_new_unconfirmed_1_1_revision_and_the_old_confirmation_is_stale()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, "1.1.0");
        Assert.Equal(HttpStatusCode.OK, (await Confirm(client, s, s.Revision)).StatusCode);
        var content = await Content(client, s);
        content["goals"]![0]!["text"] = "Obiettivo rivisto dopo la conferma";

        var edited = await Put(client, s, s.Revision, content);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var view = await Json(edited);
        Assert.Equal("Unvalidated", view.GetProperty("validationState").GetString());
        Assert.Equal("1.1.0", view.GetProperty("schemaVersion").GetString());
        Assert.False(view.GetProperty("canGenerate").GetBoolean());
        Assert.Equal("PmConfirmed", await Scalar<string>("SELECT \"ValidationState\" FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision)));
        var stale = await Confirm(client, s, s.Revision);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("revision_conflict", (await Json(stale)).GetProperty("code").GetString());
    }

    // ---------------------------------------------------------------- concorrenza e generazione

    [Fact]
    public async Task Concurrent_puts_with_the_same_if_match_on_a_1_1_revision_create_exactly_one_revision()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, "1.1.0");
        var a = await Content(client, s);
        a["goals"]![0]!["text"] = "Variante A";
        var b = await Content(client, s);
        b["goals"]![0]!["text"] = "Variante B";

        var results = await Task.WhenAll(Put(client, s, s.Revision, a), Put(client, s, s.Revision, b));

        var statuses = results.Select(r => r.StatusCode).Order().ToList();
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], statuses);
        Assert.Equal(2, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
    }

    [Fact]
    public async Task Concurrent_edit_and_confirm_on_a_1_1_revision_leave_one_consistent_outcome()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, "1.1.0");
        var edit = await Content(client, s);
        edit["goals"]![0]!["text"] = "Variante concorrente";

        var results = await Task.WhenAll(Put(client, s, s.Revision, edit), Confirm(client, s, s.Revision));

        Assert.All(results, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        var revisions = await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis));
        var currentState = await Scalar<string>(
            "SELECT \"ValidationState\" FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a ORDER BY \"Version\" DESC LIMIT 1", ("a", s.Analysis));
        // O la conferma ha vinto sulla v1 e poi la modifica ha creato la v2 non confermata, o la modifica ha creato la v2 e la conferma (If-Match v1) è stantia.
        Assert.Equal(2, revisions);
        Assert.Equal("Unvalidated", currentState);
    }

    [Fact]
    public async Task Generation_from_an_unconfirmed_1_1_revision_is_409_and_nothing_is_queued_while_the_confirmed_revision_enables_it()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client, "1.1.0");

        var rejected = await Generate(client, s, s.Revision);

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("analysis_not_confirmed", (await Json(rejected)).GetProperty("code").GetString());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p AND \"Kind\" = 'Document'", ("p", s.Project)));
        var view = await Json(await client.GetAsync(Url(s), Ct));
        Assert.False(view.GetProperty("canGenerate").GetBoolean());

        await Confirm(client, s, s.Revision);
        var confirmed = await Json(await client.GetAsync(Url(s), Ct));
        Assert.True(confirmed.GetProperty("canGenerate").GetBoolean());
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Seeded(Guid Project, Guid Analysis, Guid Revision);

    private static string Url(Seeded s, string suffix = "") => $"/api/projects/{s.Project}/analyses/{s.Analysis}{suffix}";

    private static async Task<JsonNode> Content(HttpClient client, Seeded s)
    {
        var view = await Json(await client.GetAsync(Url(s), Ct));
        return JsonNode.Parse(view.GetProperty("content").GetRawText())!;
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, Seeded s, Guid? ifMatch, JsonNode content)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Url(s, "/preview")) { Content = JsonContent.Create(content) };
        if (ifMatch is { } id)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{id}\"");
        }

        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient client, Seeded s, Guid? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(s, "/confirm"));
        if (ifMatch is { } id)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{id}\"");
        }

        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> Generate(HttpClient client, Seeded s, Guid revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(s, "/document-generations")) { Content = JsonContent.Create(new { revisionId = revision }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request, Ct);
    }

    private static JsonObject NewPassage() => Passage(Guid.NewGuid());

    private static JsonObject Passage(Guid id) => new()
    {
        ["id"] = id.ToString(),
        ["kind"] = "DocumentPassage",
        ["messageId"] = null,
        ["messageRole"] = null,
        ["revisionId"] = null,
        ["documentId"] = Guid.NewGuid().ToString(),
        ["documentVersionId"] = Guid.NewGuid().ToString(),
        ["preparationId"] = Guid.NewGuid().ToString(),
        ["artifactId"] = Guid.NewGuid().ToString(),
        ["blockId"] = Guid.NewGuid().ToString(),
        ["start"] = 10,
        ["end"] = 24,
        ["quote"] = "quattordici ch",
    };

    private static JsonObject MessageSource(Guid id, Guid message) => new()
    {
        ["id"] = id.ToString(),
        ["kind"] = "Message",
        ["messageId"] = message.ToString(),
        ["messageRole"] = "user",
        ["revisionId"] = null,
        ["documentId"] = null,
        ["documentVersionId"] = null,
        ["preparationId"] = null,
        ["artifactId"] = null,
        ["blockId"] = null,
        ["start"] = null,
        ["end"] = null,
        ["quote"] = null,
    };

    /// <summary>
    /// Progetto con un messaggio PM e un'analisi completata con la sola revisione AI (v1, Unvalidated) scritta come farebbe il commit del run,
    /// nello schema richiesto: 1.1.0 con fonte Message, fonte DocumentPassage e item Documented/Declared/Assumed/Inferred, oppure 1.0.0.
    /// </summary>
    private async Task<Seeded> Seed(HttpClient client, string schemaVersion)
    {
        var project = (await Json(await client.PostAsJsonAsync("/api/projects", new { name = $"Anteprima doc {Guid.NewGuid():N}" }, Ct))).GetProperty("id").GetGuid();
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

        var documentary = schemaVersion == "1.1.0";
        var user = Guid.NewGuid();
        var passage = Guid.NewGuid();
        var sourceId = user.ToString();
        JsonObject Item(string text, string classification, params string[] evidence) => new()
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["text"] = text,
            ["classification"] = classification,
            ["evidenceRefs"] = new JsonArray(evidence.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
        };

        var sources = new JsonArray(documentary ? MessageSource(user, message.Id) : LegacyMessageSource(user, message.Id));
        if (documentary)
        {
            sources.Add(Passage(passage));
        }

        var content = new JsonObject
        {
            ["schemaVersion"] = schemaVersion,
            ["analysisId"] = run.Id.ToString(),
            ["projectId"] = project.ToString(),
            ["sourceCutoffSequence"] = run.SourceCutoffSequence,
            ["validationState"] = "Unvalidated",
            ["sources"] = sources,
            ["goals"] = new JsonArray(Item("Realizzare un portale clienti", "Declared", sourceId)),
            ["contextAndScope"] = new JsonObject { ["overview"] = new JsonArray(), ["included"] = new JsonArray(), ["excluded"] = new JsonArray() },
            ["requirements"] = new JsonArray(documentary
                ? Item("Il portale mostra lo stato dei ticket", "Documented", passage.ToString())
                : Item("Il cliente vede lo stato dei propri ticket", "Declared", sourceId)),
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
                ["alternatives"] = new JsonArray(
                    Item("Fatturazione nel portale", "Declared", sourceId),
                    documentary ? Item("Fatturazione fuori perimetro", "Documented", passage.ToString()) : Item("Fatturazione fuori perimetro", "Declared", sourceId)),
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
            SchemaVersion = schemaVersion,
            ContentJson = content.ToJsonString(),
            ValidationState = AnalysisValidationState.Unvalidated,
        };
        db.AnalysisRevisions.Add(revision);
        run.Status = ProcessingRunStatus.Completed;
        run.ResultRevisionId = revision.Id;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(Ct);
        await db.Projects.Where(p => p.Id == project).ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentAnalysisId, run.Id), Ct);
        return new Seeded(project, run.Id, revision.Id);
    }

    private static JsonObject LegacyMessageSource(Guid id, Guid message) => new()
    {
        ["id"] = id.ToString(), ["kind"] = "Message", ["messageId"] = message.ToString(), ["messageRole"] = "user", ["revisionId"] = null,
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();

    private async Task<long> Count(string sql, params (string Name, object Value)[] parameters) => await Scalar<long>(sql, parameters);

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
