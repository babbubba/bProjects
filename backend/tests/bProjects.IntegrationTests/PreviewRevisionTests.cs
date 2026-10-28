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
/// T032 — modifica (PUT preview) e conferma (POST confirm) dell'anteprima su PostgreSQL reale: If-Match, revisioni attribuite
/// al PM autenticato con fonte PmRevision assegnata dal server, AI originale intatta, conferma idempotente che congela il JSON,
/// conflitti Open o risolti con motivazione, edit dopo conferma e blocco durante una nuova Analysis. Nessuna chiamata AI.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class PreviewRevisionTests : IAsyncLifetime
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

    // Rimosso da T034, quando PUT preview e POST confirm esistono.
    private static readonly string? PendingReason = "PendingT033/T034: PUT preview e POST confirm non ancora implementati.";

    private ApiHarness App
    {
        get
        {
            Assert.SkipWhen(PendingReason is not null, PendingReason ?? string.Empty);
            TestEnvironment.RequirePostgres();
            return _app!;
        }
    }

    // ---------------------------------------------------------------- If-Match

    [Fact]
    public async Task A_missing_if_match_is_400_revision_required_and_creates_nothing()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client);

        var put = await Put(client, s, null, await Content(client, s));
        var confirm = await Confirm(client, s, null);

        foreach (var response in new[] { put, confirm })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("revision_required", (await Json(response)).GetProperty("code").GetString());
        }

        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
        Assert.Equal("Unvalidated", await Scalar<string>("SELECT \"ValidationState\" FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision)));
    }

    [Fact]
    public async Task A_stale_if_match_is_409_revision_conflict_with_the_current_revision_and_a_repeated_put_creates_no_other_revision()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client);
        var edited = await Content(client, s);
        edited["goals"]![0]!["text"] = "Obiettivo corretto dal PM";
        var first = await Put(client, s, s.Revision, edited);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var current = (await Json(first)).GetProperty("currentRevisionId").GetGuid();

        var repeated = await Put(client, s, s.Revision, edited); // vecchio If-Match
        var staleConfirm = await Confirm(client, s, s.Revision);
        var unknown = await Put(client, s, Guid.NewGuid(), edited);

        foreach (var response in new[] { repeated, staleConfirm, unknown })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await Json(response);
            Assert.Equal("revision_conflict", problem.GetProperty("code").GetString());
            Assert.Equal(current, problem.GetProperty("currentRevisionId").GetGuid());
        }

        Assert.Equal(2, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
        var view = await Json(await client.GetAsync(Url(s), Ct)); // riconciliazione via GET
        Assert.Equal(current, view.GetProperty("currentRevisionId").GetGuid());
    }

    [Fact]
    public async Task A_malformed_if_match_is_400_and_anonymous_calls_are_401()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client);
        var request = new HttpRequestMessage(HttpMethod.Post, Url(s, "/confirm"));
        request.Headers.TryAddWithoutValidation("If-Match", "non-un-guid");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request, Ct)).StatusCode);

        using var anonymous = App.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(anonymous, s, s.Revision, await Content(client, s))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Confirm(anonymous, s, s.Revision)).StatusCode);
    }

    // ---------------------------------------------------------------- revisioni attribuite e AI intatta

    [Fact]
    public async Task A_put_creates_an_attributed_unvalidated_revision_with_a_server_assigned_pm_source_and_keeps_the_ai_original_intact()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client);
        var originalJson = await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision));
        var content = await Content(client, s);
        var untouchedId = content["requirements"]![0]!["id"]!.GetValue<string>();
        content["goals"]![0]!["text"] = "Ridurre a un giorno i tempi di risposta";
        var added = Guid.NewGuid();
        content["constraints"]!.AsArray().Add(new JsonObject
        {
            ["id"] = added.ToString(),
            ["text"] = "Go-live entro giugno",
            ["classification"] = "Declared",
            ["evidenceRefs"] = new JsonArray(), // la fonte la assegna il server
        });

        var response = await Put(client, s, s.Revision, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await Json(response);
        var newRevision = view.GetProperty("currentRevisionId").GetGuid();
        Assert.NotEqual(s.Revision, newRevision);
        Assert.Equal($"\"{newRevision}\"", response.Headers.ETag!.ToString());
        Assert.Equal(2, view.GetProperty("version").GetInt32());
        Assert.Equal("PmEdit", view.GetProperty("origin").GetString());
        Assert.Equal("Unvalidated", view.GetProperty("validationState").GetString());
        Assert.Equal("Unvalidated", view.GetProperty("content").GetProperty("validationState").GetString());
        Assert.Equal(TestApp.PmUser, view.GetProperty("createdBy").GetString());
        Assert.True(view.GetProperty("canEdit").GetBoolean());
        Assert.True(view.GetProperty("canConfirm").GetBoolean());
        Assert.False(view.GetProperty("canGenerate").GetBoolean());

        // Una sola fonte PmRevision, legata alla nuova revisione; la citano solo gli elementi modificati/aggiunti.
        var pmSources = view.GetProperty("content").GetProperty("sources").EnumerateArray().Where(x => x.GetProperty("kind").GetString() == "PmRevision").ToList();
        var pm = Assert.Single(pmSources);
        Assert.Equal(newRevision, pm.GetProperty("revisionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, pm.GetProperty("messageId").ValueKind);
        var pmId = pm.GetProperty("id").GetGuid();
        var result = view.GetProperty("content");
        Assert.Contains(pmId, result.GetProperty("goals")[0].GetProperty("evidenceRefs").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal("Declared", result.GetProperty("constraints")[0].GetProperty("classification").GetString());
        Assert.Contains(pmId, result.GetProperty("constraints")[0].GetProperty("evidenceRefs").EnumerateArray().Select(e => e.GetGuid()));
        var untouched = result.GetProperty("requirements").EnumerateArray().Single(i => i.GetProperty("id").GetString() == untouchedId);
        Assert.DoesNotContain(pmId, untouched.GetProperty("evidenceRefs").EnumerateArray().Select(e => e.GetGuid()));
        // Le fonti originali non mutano.
        Assert.Equal(
            content["sources"]!.AsArray().Count,
            result.GetProperty("sources").GetArrayLength() - 1);

        // La revisione AI resta leggibile e byte-per-byte invariata; la nuova ha genitore e autore reali.
        Assert.Equal(originalJson, await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision)));
        var original = await Json(await client.GetAsync(Url(s, "/revisions/1"), Ct));
        Assert.Equal("Ai", original.GetProperty("origin").GetString());
        Assert.False(original.GetProperty("isCurrent").GetBoolean());
        Assert.DoesNotContain(original.GetProperty("content").GetProperty("sources").EnumerateArray(), x => x.GetProperty("kind").GetString() == "PmRevision");
        var second = await Json(await client.GetAsync(Url(s, "/revisions/2"), Ct));
        Assert.Equal(s.Revision, second.GetProperty("parentRevisionId").GetGuid());
        Assert.True(second.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(TestApp.PmUser, second.GetProperty("createdBy").GetString());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"Origin\" = 'Ai' AND \"AnalysisId\" = @a", ("a", s.Analysis)));
    }

    [Fact]
    public async Task Each_revision_is_attributed_to_the_real_author_and_earlier_pm_sources_are_preserved()
    {
        using var first = await App.LoginAsync();
        using var other = await App.LoginAsync(TestApp.SecondPmUser, TestApp.SecondPmPassword);
        var s = await Seed(first);
        var v2 = await Content(first, s);
        v2["goals"]![0]!["text"] = "Primo intervento di pm.uno";
        var r2 = await Json(await Put(first, s, s.Revision, v2));

        var v3 = JsonNode.Parse(r2.GetProperty("content").GetRawText())!;
        v3["requirements"]![0]!["text"] = "Secondo intervento di pm.due";
        var r3 = await Json(await Put(other, s, r2.GetProperty("currentRevisionId").GetGuid(), v3));

        Assert.Equal(3, r3.GetProperty("version").GetInt32());
        Assert.Equal(TestApp.SecondPmUser, r3.GetProperty("createdBy").GetString());
        var pm = r3.GetProperty("content").GetProperty("sources").EnumerateArray().Where(x => x.GetProperty("kind").GetString() == "PmRevision").ToList();
        Assert.Equal(2, pm.Count); // la fonte storica di v2 resta, ne arriva una nuova per v3
        Assert.Equal(
            new[] { r2.GetProperty("currentRevisionId").GetGuid(), r3.GetProperty("currentRevisionId").GetGuid() }.Order(),
            pm.Select(x => x.GetProperty("revisionId").GetGuid()).Order());
        var v2Row = await Json(await first.GetAsync(Url(s, "/revisions/2"), Ct));
        Assert.Equal(TestApp.PmUser, v2Row.GetProperty("createdBy").GetString());
    }

    [Fact]
    public async Task The_pm_cannot_attribute_text_to_the_model_a_message_or_a_forged_pm_revision_and_invalid_content_is_422_with_field_errors()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client);

        async Task<JsonElement> Rejected(Action<JsonNode> mutate)
        {
            var content = await Content(client, s);
            mutate(content);
            var response = await Put(client, s, s.Revision, content);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await Json(response);
            Assert.Equal("preview_invalid", problem.GetProperty("code").GetString());
            Assert.NotEmpty(problem.GetProperty("errors").EnumerateArray());
            Assert.All(problem.GetProperty("errors").EnumerateArray(), e => Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("field").GetString())));
            return problem;
        }

        var empty = await Rejected(c => c["goals"]![0]!["text"] = "  ");
        Assert.Contains(empty.GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString()!.StartsWith("goals[0].text", StringComparison.Ordinal));
        await Rejected(c => c["analysisId"] = Guid.NewGuid().ToString());
        await Rejected(c => c["projectId"] = Guid.NewGuid().ToString());
        await Rejected(c => c["sourceCutoffSequence"] = 99);
        await Rejected(c => c["schemaVersion"] = "2.0.0");
        await Rejected(c => c["markdown"] = "# non ammesso");
        await Rejected(c => c["goals"]![0]!["evidenceRefs"] = new JsonArray(Guid.NewGuid().ToString())); // riferimento non risolto
        // Fonte PmRevision forgiata dal client (il PM non conosce la revisione in creazione) o fonte Message nuova: non ammesse.
        await Rejected(c => c["sources"]!.AsArray().Add(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["kind"] = "PmRevision", ["messageId"] = null, ["messageRole"] = null, ["revisionId"] = s.Revision.ToString(),
        }));
        await Rejected(c => c["sources"]!.AsArray().Add(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["kind"] = "Message", ["messageId"] = Guid.NewGuid().ToString(), ["messageRole"] = "user", ["revisionId"] = null,
        }));
        // Una fonte storica non si rimuove né si muta.
        await Rejected(c => c["sources"]!.AsArray().RemoveAt(0));
        await Rejected(c => c["sources"]![0]!["messageRole"] = "assistant");

        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
    }

    [Fact]
    public async Task A_non_object_or_malformed_body_is_rejected_without_a_revision()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client);

        var malformed = new HttpRequestMessage(HttpMethod.Put, Url(s, "/preview")) { Content = new StringContent("{ non json", System.Text.Encoding.UTF8, "application/json") };
        malformed.Headers.TryAddWithoutValidation("If-Match", $"\"{s.Revision}\"");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(malformed, Ct)).StatusCode);

        var array = new HttpRequestMessage(HttpMethod.Put, Url(s, "/preview")) { Content = JsonContent.Create(new JsonArray()) };
        array.Headers.TryAddWithoutValidation("If-Match", $"\"{s.Revision}\"");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.SendAsync(array, Ct)).StatusCode);

        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
    }

    // ---------------------------------------------------------------- conflitti: aperti o risolti con motivazione

    [Fact]
    public async Task Alternatives_survive_edits_and_a_conflict_is_resolved_only_with_existing_alternatives_a_reason_and_the_pm_source()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client);

        // Resta Open: nessuna risoluzione richiesta, alternative e evidenze invariate.
        var open = await Content(client, s);
        open["goals"]![0]!["text"] = "Obiettivo rivisto";
        var afterOpen = await Json(await Put(client, s, s.Revision, open));
        var conflict = afterOpen.GetProperty("content").GetProperty("conflicts")[0];
        Assert.Equal("Open", conflict.GetProperty("status").GetString());
        Assert.Equal(2, conflict.GetProperty("alternatives").GetArrayLength());

        // Alternativa rimossa o scelta inesistente o motivo vuoto: rifiutato.
        var current = afterOpen.GetProperty("currentRevisionId").GetGuid();
        var alternativeIds = conflict.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("id").GetString()!).ToList();
        async Task<HttpStatusCode> TryResolve(Action<JsonNode> mutate)
        {
            var c = JsonNode.Parse(afterOpen.GetProperty("content").GetRawText())!;
            mutate(c);
            return (await Put(client, s, current, c)).StatusCode;
        }

        Action<JsonNode> Resolve(string choice, string reason) => c =>
        {
            c["conflicts"]![0]!["status"] = "Resolved";
            c["conflicts"]![0]!["resolution"] = new JsonObject
            {
                ["selectedAlternativeIds"] = new JsonArray(choice),
                ["reason"] = reason,
                ["evidenceRefs"] = new JsonArray(),
            };
        };

        Assert.Equal(HttpStatusCode.UnprocessableEntity, await TryResolve(c => c["conflicts"]![0]!["alternatives"]!.AsArray().RemoveAt(1)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await TryResolve(Resolve(Guid.NewGuid().ToString(), "Scelta inesistente")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await TryResolve(Resolve(alternativeIds[1], "   ")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await TryResolve(c =>
        {
            Resolve(alternativeIds[1], "Motivo")(c);
            c["conflicts"]![0]!["resolution"] = null; // Resolved senza risoluzione
        }));
        Assert.Equal(2, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));

        // Risoluzione valida: il server aggiunge la fonte PmRevision come evidenza; le alternative restano tutte.
        var resolvedBody = JsonNode.Parse(afterOpen.GetProperty("content").GetRawText())!;
        Resolve(alternativeIds[1], "Decisione della direzione: fatturazione fuori dal primo rilascio.")(resolvedBody);
        var resolvedResponse = await Put(client, s, current, resolvedBody);
        Assert.Equal(HttpStatusCode.OK, resolvedResponse.StatusCode);
        var resolved = (await Json(resolvedResponse)).GetProperty("content").GetProperty("conflicts")[0];
        Assert.Equal("Resolved", resolved.GetProperty("status").GetString());
        Assert.Equal(2, resolved.GetProperty("alternatives").GetArrayLength());
        var evidence = resolved.GetProperty("resolution").GetProperty("evidenceRefs").EnumerateArray().Select(e => e.GetGuid()).ToList();
        Assert.Single(evidence);
        Assert.Equal(3, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
        // La versione precedente conserva il conflitto Open con le sue alternative.
        var previous = await Json(await client.GetAsync(Url(s, "/revisions/2"), Ct));
        Assert.Equal("Open", previous.GetProperty("content").GetProperty("conflicts")[0].GetProperty("status").GetString());
    }

    // ---------------------------------------------------------------- conferma

    [Fact]
    public async Task Confirm_changes_only_state_and_metadata_freezes_the_json_leaves_open_points_open_and_generates_nothing()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client);
        var runsBefore = await Count("SELECT count(*) FROM \"ProcessingRuns\"");
        var before = JsonNode.Parse(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision)))!;

        var response = await Confirm(client, s, s.Revision);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await Json(response);
        Assert.Equal(s.Revision, view.GetProperty("currentRevisionId").GetGuid()); // stessa revisione, nessuna nuova versione
        Assert.Equal($"\"{s.Revision}\"", response.Headers.ETag!.ToString());
        Assert.Equal(1, view.GetProperty("version").GetInt32());
        Assert.Equal("PmConfirmed", view.GetProperty("validationState").GetString());
        Assert.Equal("PmConfirmed", view.GetProperty("content").GetProperty("validationState").GetString());
        Assert.Equal(TestApp.PmUser, view.GetProperty("confirmedBy").GetString());
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("confirmedAt").ValueKind);
        Assert.True(view.GetProperty("canEdit").GetBoolean());
        Assert.False(view.GetProperty("canConfirm").GetBoolean());
        Assert.True(view.GetProperty("canGenerate").GetBoolean());
        Assert.Equal("Open", view.GetProperty("content").GetProperty("conflicts")[0].GetProperty("status").GetString()); // i punti aperti restano aperti
        Assert.Equal(1, view.GetProperty("content").GetProperty("openQuestions").GetArrayLength());

        // Nel JSON cambia solo validationState.
        var after = JsonNode.Parse(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision)))!;
        Assert.Equal("PmConfirmed", after["validationState"]!.GetValue<string>());
        after["validationState"] = "Unvalidated";
        Assert.True(JsonNode.DeepEquals(before, after));

        // Nessuna generazione né run, lease o deadline in attesa del PM.
        Assert.Equal(runsBefore, await Count("SELECT count(*) FROM \"ProcessingRuns\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Kind\" = 'Document'"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Status\" IN ('Queued', 'Running') OR \"LeaseOwner\" IS NOT NULL"));
        Assert.Equal(JsonValueKind.Null, (await Json(await client.GetAsync($"/api/projects/{s.Project}", Ct))).GetProperty("activeProcessingRunId").ValueKind);
    }

    [Fact]
    public async Task A_repeated_confirm_on_the_same_current_confirmed_revision_is_an_idempotent_200_even_from_another_pm()
    {
        using var client = await App.LoginAsync();
        using var other = await App.LoginAsync(TestApp.SecondPmUser, TestApp.SecondPmPassword);
        var s = await Seed(client);
        var first = await Json(await Confirm(client, s, s.Revision));
        var frozen = await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision));

        var again = await Confirm(other, s, s.Revision);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var view = await Json(again);
        Assert.Equal(first.GetProperty("confirmedAt").GetDateTimeOffset(), view.GetProperty("confirmedAt").GetDateTimeOffset());
        Assert.Equal(TestApp.PmUser, view.GetProperty("confirmedBy").GetString()); // la conferma originale non cambia autore
        Assert.Equal(frozen, await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
    }

    [Fact]
    public async Task An_edit_after_confirm_creates_a_new_unvalidated_revision_and_leaves_the_confirmed_one_frozen()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client);
        await Confirm(client, s, s.Revision);
        var frozen = await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision));
        var content = await Content(client, s); // contiene validationState PmConfirmed: il server impone Unvalidated
        content["goals"]![0]!["text"] = "Obiettivo cambiato dopo la conferma";

        var response = await Put(client, s, s.Revision, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await Json(response);
        Assert.Equal(2, view.GetProperty("version").GetInt32());
        Assert.Equal("Unvalidated", view.GetProperty("validationState").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("confirmedBy").ValueKind);
        Assert.True(view.GetProperty("canConfirm").GetBoolean());
        Assert.False(view.GetProperty("canGenerate").GetBoolean());
        Assert.Equal(frozen, await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision)));
        Assert.Equal("PmConfirmed", await Scalar<string>("SELECT \"ValidationState\" FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision)));
        // La conferma precedente non vale più per la nuova revisione: serve il suo If-Match, e quello vecchio è obsoleto.
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(client, s, s.Revision)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Confirm(client, s, view.GetProperty("currentRevisionId").GetGuid())).StatusCode);
    }

    [Fact]
    public async Task A_conflict_resolved_in_a_revision_can_be_confirmed_and_a_tampered_current_revision_is_never_confirmed()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client);
        var content = await Content(client, s);
        var alternative = content["conflicts"]![0]!["alternatives"]![0]!["id"]!.GetValue<string>();
        content["conflicts"]![0]!["status"] = "Resolved";
        content["conflicts"]![0]!["resolution"] = new JsonObject
        {
            ["selectedAlternativeIds"] = new JsonArray(alternative), ["reason"] = "Scelta del PM", ["evidenceRefs"] = new JsonArray(),
        };
        var edited = await Json(await Put(client, s, s.Revision, content));
        var current = edited.GetProperty("currentRevisionId").GetGuid();

        var confirmed = await Json(await Confirm(client, s, current));
        Assert.Equal("Resolved", confirmed.GetProperty("content").GetProperty("conflicts")[0].GetProperty("status").GetString());

        // Revisione corrente corrotta nel database (JSON non più conforme): la conferma è rifiutata, nulla viene scritto.
        var second = await Seed(client);
        await Execute("UPDATE \"AnalysisRevisions\" SET \"ContentJson\" = jsonb_set(\"ContentJson\", '{goals,0,text}', '\"  \"') WHERE \"Id\" = @r", ("r", second.Revision));
        var rejected = await Confirm(client, second, second.Revision);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal("preview_invalid", (await Json(rejected)).GetProperty("code").GetString());
        Assert.Equal("Unvalidated", await Scalar<string>("SELECT \"ValidationState\" FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", second.Revision)));
    }

    // ---------------------------------------------------------------- analisi corrente e Analysis attiva

    [Fact]
    public async Task Edit_and_confirm_are_409_processing_active_during_a_new_analysis_while_the_old_preview_stays_readable()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client);
        var content = await Content(client, s);
        var analyze = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{s.Project}/analyses");
        analyze.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var started = await Json(await client.SendAsync(analyze, Ct));
        var runId = started.GetProperty("id").GetGuid();

        var put = await Put(client, s, s.Revision, content);
        var confirm = await Confirm(client, s, s.Revision);

        foreach (var response in new[] { put, confirm })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await Json(response);
            Assert.Equal("processing_active", problem.GetProperty("code").GetString());
            Assert.Equal(runId, problem.GetProperty("processingRunId").GetGuid());
        }

        var view = await Json(await client.GetAsync(Url(s), Ct)); // leggibile ma non modificabile
        Assert.False(view.GetProperty("canEdit").GetBoolean());
        Assert.False(view.GetProperty("canConfirm").GetBoolean());
        Assert.False(view.GetProperty("canGenerate").GetBoolean());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));

        // Quando il nuovo run termina in errore la vecchia anteprima torna modificabile.
        await Execute("UPDATE \"ProcessingRuns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x', \"CompletedAt\" = now() WHERE \"Id\" = @r", ("r", runId));
        Assert.Equal(HttpStatusCode.OK, (await Put(client, s, s.Revision, content)).StatusCode);
    }

    [Fact]
    public async Task Only_the_current_analysis_of_the_project_can_be_edited_or_confirmed()
    {
        using var client = await App.LoginAsync();
        var old = await Seed(client);
        var current = await Seed(client, old.Project); // seconda analisi: diventa la corrente
        var content = await Content(client, old);

        var put = await Put(client, old, old.Revision, content);
        var confirm = await Confirm(client, old, old.Revision);

        foreach (var response in new[] { put, confirm })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("analysis_not_current", (await Json(response)).GetProperty("code").GetString());
        }

        Assert.False((await Json(await client.GetAsync(Url(old), Ct))).GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Confirm(client, current, current.Revision)).StatusCode);
        // Analisi inesistente o di un altro progetto: 404.
        var otherProject = await Seed(client);
        Assert.Equal(HttpStatusCode.NotFound, (await Confirm(client, otherProject with { Analysis = Guid.NewGuid() }, otherProject.Revision)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Confirm(client, current with { Project = otherProject.Project }, current.Revision)).StatusCode);
    }

    // ---------------------------------------------------------------- concorrenza

    [Fact]
    public async Task Concurrent_edits_and_confirms_on_the_same_revision_serialize_to_exactly_one_winner()
    {
        using var client = await App.LoginAsync();
        var s = await Seed(client);
        var content = await Content(client, s);
        content["goals"]![0]!["text"] = "Modifica concorrente";

        var calls = Enumerable.Range(0, 4).Select(i => Task.Run(() => Put(client, s, s.Revision, content), Ct)).ToList();
        calls.Add(Task.Run(() => Confirm(client, s, s.Revision), Ct));
        calls.Add(Task.Run(() => Confirm(client, s, s.Revision), Ct));
        var results = await Task.WhenAll(calls);

        // I PUT con la stessa base sono serializzati: uno solo vince (anche se la conferma ha preceduto, il PUT è un edit valido
        // dopo conferma), gli altri vedono una base obsoleta. Le conferme sono idempotenti oppure obsolete.
        Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict));
        var puts = results.Take(4).Select(r => r.StatusCode).ToList();
        Assert.Single(puts, code => code == HttpStatusCode.OK);
        Assert.Equal(2, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
        Assert.Equal(2, await Count("SELECT count(DISTINCT \"Version\") FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
        // Mai una revisione confermata con contenuto diverso dalla base: se la v1 è confermata, il suo JSON è quello originale.
        var v1 = await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"Id\" = @r", ("r", s.Revision));
        Assert.DoesNotContain("Modifica concorrente", v1);
    }

    // ---------------------------------------------------------------- supporto

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

    /// <summary>
    /// Progetto con messaggi PM e un'analisi completata con la sola revisione AI (v1, Unvalidated): contenuto valido costruito
    /// sui messaggi reali, scritto come farebbe il commit del run (T029) senza invocare alcun modello.
    /// </summary>
    private async Task<Seeded> Seed(HttpClient client, Guid? existingProject = null)
    {
        var project = existingProject ?? (await Json(await client.PostAsJsonAsync("/api/projects", new { name = $"Anteprima {Guid.NewGuid():N}" }, Ct))).GetProperty("id").GetGuid();
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
        return new Seeded(project, run.Id, revision.Id);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();

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
