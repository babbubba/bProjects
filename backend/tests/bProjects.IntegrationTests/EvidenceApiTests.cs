using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T065/T067 — API del registro (contracts/http-api.md §Registro) su PostgreSQL reale: autenticazione e ruolo, ID di un altro progetto
/// sempre 404, Idempotency-Key obbligatoria e rispettata, codici di errore documentati, viste senza prompt, ragionamento o output non
/// validato; rianalisi e backfill idempotenti senza duplicati e con avanzamento, artefatti storici invariati. Estrazioni con l'uscita
/// deterministica dei test attraverso claim e commit reali; nessun modello.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class EvidenceApiTests : IAsyncLifetime
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

    [Fact]
    public async Task Every_registry_route_needs_a_pm_and_ids_of_another_project_are_404()
    {
        using var client = await App.LoginAsync();
        var a = await Seeded(client);
        var b = await CreateProject(client);
        await PostMessage(client, b, "Messaggio del progetto B.");

        string[] reads =
        [
            "evidence/status", "evidence", $"evidence/{a.Evidence}", $"messages/{a.Message}/evidence/versions", $"evidence/versions/{a.Version}",
            $"evidence/versions/{a.Version}/compare/{a.Version}", $"evidence/jobs/{a.Job}",
        ];
        foreach (var path in reads)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await App.Anonymous().GetAsync($"/api/projects/{a.Project}/{path}", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await App.WithoutPmRole().GetAsync($"/api/projects/{a.Project}/{path}", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/projects/{a.Project}/{path}", Ct)).StatusCode);
        }

        // Gli ID di A sotto il progetto B non esistono: 404, mai il dato di A.
        foreach (var path in reads.Skip(2))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{b}/{path}", Ct)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await Command(client, $"/api/projects/{b}/messages/{a.Message}/evidence/reanalyses")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Command(client, $"/api/projects/{b}/evidence/jobs/{a.Job}/retry")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/projects/{b}/messages/{a.Message}/evidence/promotions",
            new { versionId = a.Version, expectedStateRevision = 1 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/evidence/status", Ct)).StatusCode);
    }

    [Fact]
    public async Task Commands_require_an_idempotency_key_replay_the_same_resource_and_report_the_documented_conflicts()
    {
        using var client = await App.LoginAsync();
        var s = await Seeded(client);
        var url = $"/api/projects/{s.Project}/messages/{s.Message}/evidence/reanalyses";

        var missing = await client.PostAsync(url, null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("idempotency_key_required", await Code(missing));

        var key = Guid.NewGuid();
        var first = await Command(client, url, key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var job = (await Json(first)).GetProperty("id").GetGuid();
        var replay = await Command(client, url, key);
        Assert.True(replay.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK, $"replay {replay.StatusCode}");
        Assert.Equal(job, (await Json(replay)).GetProperty("id").GetGuid());       // stessa risorsa

        var active = await Command(client, url);
        Assert.Equal(HttpStatusCode.Conflict, active.StatusCode);
        Assert.Equal("extraction_active", await Code(active));
        var notFailed = await Command(client, $"/api/projects/{s.Project}/evidence/jobs/{job}/retry");
        Assert.Equal(HttpStatusCode.Conflict, notFailed.StatusCode);
        Assert.Equal("job_not_failed", await Code(notFailed));

        // Messaggio dell'assistente o inesistente: non è un messaggio PM del progetto.
        Assert.Equal(HttpStatusCode.NotFound, (await Command(client, $"/api/projects/{s.Project}/messages/{Guid.NewGuid()}/evidence/reanalyses")).StatusCode);

        // Promozione: input obbligatorio, versione di un altro messaggio, revisione attesa obsoleta.
        var promotions = $"/api/projects/{s.Project}/messages/{s.Message}/evidence/promotions";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(promotions, new { versionId = (Guid?)null, expectedStateRevision = (int?)null }, Ct)).StatusCode);
        var other = await Scalar<Guid>("""SELECT "Id" FROM "EvidenceExtractionVersions" WHERE "MessageId" <> @m LIMIT 1""", ("m", s.Message));
        var notOfMessage = await client.PostAsJsonAsync(promotions, new { versionId = other, expectedStateRevision = 1 }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notOfMessage.StatusCode);
        Assert.Equal("version_not_of_message", await Code(notOfMessage));
        var same = await client.PostAsJsonAsync(promotions, new { versionId = s.Version, expectedStateRevision = 1 }, Ct);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);                          // già corrente: nessuna nuova promozione
        Assert.Equal(1L, await Scalar<long>("""SELECT count(*) FROM "EvidencePromotions" WHERE "MessageId" = @m""", ("m", s.Message)));
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        var candidate = await Scalar<Guid>("""SELECT "Id" FROM "EvidenceExtractionVersions" WHERE "MessageId" = @m AND "Version" = 2""", ("m", s.Message));
        var stale = await client.PostAsJsonAsync(promotions, new { versionId = candidate, expectedStateRevision = 7 }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var conflict = await Json(stale);
        Assert.Equal("promotion_conflict", conflict.GetProperty("code").GetString());

        // Confronto fra versioni di messaggi diversi e selezione di un run inesistente.
        var notComparable = await client.GetAsync($"/api/projects/{s.Project}/evidence/versions/{s.Version}/compare/{other}", Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notComparable.StatusCode);
        Assert.Equal("versions_not_comparable", await Code(notComparable));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{s.Project}/analyses/{Guid.NewGuid()}/evidence-selection", Ct)).StatusCode);
    }

    [Fact]
    public async Task Backfill_and_project_reanalysis_are_idempotent_report_progress_and_never_duplicate_or_rewrite_history()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        for (var i = 1; i <= 3; i++)
        {
            await PostMessage(client, project, $"Requisito numero {i} del portale.");
        }

        // Un messaggio senza versione (lavoro iniziale fallito) e due con versione: il backfill accoda solo il primo.
        await Execute("""UPDATE "EvidenceExtractionJobs" SET "Status" = 'Failed', "ErrorCode" = 'test', "CompletedAt" = now() WHERE "MessageSequence" = 1""");
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct, enqueueMissing: false);
        var history = await Rows();

        var backfill = await Command(client, $"/api/projects/{project}/evidence/backfills");
        Assert.Equal(HttpStatusCode.Accepted, backfill.StatusCode);
        var batch = await Json(backfill);
        Assert.Equal("Backfill", batch.GetProperty("kind").GetString());
        Assert.Equal(1, batch.GetProperty("jobCount").GetInt32());
        Assert.Equal(2, batch.GetProperty("skippedCount").GetInt32());
        var again = await Json(await Command(client, $"/api/projects/{project}/evidence/backfills")); // nuova chiave: niente da accodare
        Assert.Equal(0, again.GetProperty("jobCount").GetInt32());
        Assert.Equal(3, again.GetProperty("skippedCount").GetInt32());

        var batchId = batch.GetProperty("id").GetGuid();
        Assert.Equal(1, (await Batch(client, project, batchId)).GetProperty("progress").GetProperty("queued").GetInt32());
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        Assert.Equal(1, (await Batch(client, project, batchId)).GetProperty("progress").GetProperty("completed").GetInt32());

        // Rianalisi del progetto: un lavoro per ogni messaggio PM; le versioni esistenti non cambiano, le nuove sono candidate.
        var reanalysis = await Json(await Command(client, $"/api/projects/{project}/evidence/reanalyses"));
        Assert.Equal("Reanalysis", reanalysis.GetProperty("kind").GetString());
        Assert.Equal(3, reanalysis.GetProperty("jobCount").GetInt32());
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        Assert.All(history, row => Assert.Contains(row, Rows().Result));
        var status = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/evidence/status", Ct);
        Assert.All(status.GetProperty("messages").EnumerateArray(), m => Assert.Equal(1, m.GetProperty("candidateCount").GetInt32()));
        Assert.Equal(0L, await Scalar<long>("""SELECT count(*) - count(DISTINCT ("MessageId", "Version")) FROM "EvidenceExtractionVersions" """));
    }

    [Fact]
    public async Task Views_expose_the_documented_fields_and_never_prompts_reasoning_or_unvalidated_output()
    {
        using var client = await App.LoginAsync();
        var s = await Seeded(client);

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.Project}/evidence", Ct);
        var evidence = list.GetProperty("items").EnumerateArray().First(e => e.GetProperty("messageId").GetGuid() == s.Message);
        foreach (var field in new[] { "id", "versionId", "version", "isCurrent", "messageSequence", "ordinal", "key", "kind", "statement", "polarity", "certainty", "sources" })
        {
            Assert.True(evidence.TryGetProperty(field, out _), $"EvidenceView senza {field}");
        }

        var source = evidence.GetProperty("sources")[0];
        var text = await Scalar<string>("""SELECT "Body" FROM "ConversationMessages" WHERE "Id" = @m""", ("m", s.Message));
        Assert.Equal(source.GetProperty("quote").GetString(), text[source.GetProperty("start").GetInt32()..source.GetProperty("end").GetInt32()]);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.Project}/evidence/{s.Evidence}", Ct);
        Assert.Equal(text, detail.GetProperty("messages")[0].GetProperty("text").GetString());
        var versions = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.Project}/messages/{s.Message}/evidence/versions", Ct);
        Assert.Equal(s.Version, versions.GetProperty("currentVersionId").GetGuid());
        Assert.Single(versions.GetProperty("promotions").EnumerateArray());
        var job = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.Project}/evidence/jobs/{s.Job}", Ct);
        Assert.Equal(s.Version, job.GetProperty("resultVersionId").GetGuid());

        var raw = string.Join('\n', await Task.WhenAll(
            client.GetStringAsync($"/api/projects/{s.Project}/evidence/versions/{s.Version}", Ct),
            client.GetStringAsync($"/api/projects/{s.Project}/evidence/{s.Evidence}", Ct),
            client.GetStringAsync($"/api/projects/{s.Project}/evidence/jobs/{s.Job}", Ct)));
        foreach (var forbidden in new[] { "reasoning", "\"prompt\"", "instructions", "rawOutput", "\"request\"", "\"response\"" })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- supporto

    private sealed record Seed(Guid Project, Guid Message, Guid Version, Guid Evidence, Guid Job);

    /// <summary>Progetto con due messaggi PM estratti (prima versione corrente di ciascuno).</summary>
    private async Task<Seed> Seeded(HttpClient client)
    {
        var project = await CreateProject(client);
        await PostMessage(client, project, "Il portale invia un promemoria via email entro 24 ore.");
        await PostMessage(client, project, "Le fatture restano fuori perimetro.");
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        var message = await Scalar<Guid>("""
            SELECT m."Id" FROM "ConversationMessages" m JOIN "Conversations" c ON c."Id" = m."ConversationId"
            WHERE c."ProjectId" = @p AND m."Sequence" = 1
            """, ("p", project));
        var version = await Scalar<Guid>("""SELECT "Id" FROM "EvidenceExtractionVersions" WHERE "MessageId" = @m""", ("m", message));
        return new(project, message, version,
            await Scalar<Guid>("""SELECT "Id" FROM "Evidences" WHERE "VersionId" = @v""", ("v", version)),
            await Scalar<Guid>("""SELECT "JobId" FROM "EvidenceExtractionVersions" WHERE "Id" = @v""", ("v", version)));
    }

    private Task<List<string>> Rows() => List("""
        SELECT v."Id"::text || '|' || v."Version" || '|' || v."CreatedAt" || '|' || coalesce(string_agg(e."Id"::text || ':' || e."Statement", ';'), '')
        FROM "EvidenceExtractionVersions" v LEFT JOIN "Evidences" e ON e."VersionId" = v."Id" GROUP BY v."Id"
        """);

    private static async Task<JsonElement> Batch(HttpClient client, Guid project, Guid batch) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/evidence/batches/{batch}", Ct);

    private static async Task<Guid> CreateProject(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name = $"API {Guid.NewGuid():N}" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private static async Task PostMessage(HttpClient client, Guid project, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static Task<HttpResponseMessage> Command(HttpClient client, string url, Guid? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await Json(response)).GetProperty("code").GetString();

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
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

    private async Task<List<string>> List(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }
}
