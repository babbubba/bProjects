using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T116 (API + PostgreSQL): retry manuale del parsing. POST /document-jobs/{j}/retry crea una nuova richiesta (Preparation + job Parse Queued)
/// collegata al terminale, con nuovo t0 e cap congelato; il vecchio root, il suo errore, i suoi tempi e l originale restano immutati. Replay con
/// la stessa chiave = stessi ID; input diverso, lavoro attivo o azione non ammessa = 409 con codice stabile. GET non ritenta mai. Qui il worker
/// del parsing non è configurato: le righe terminali sono portate nello stato voluto e il lavoro resta Queued (nessun DGX, nessun parser).
/// </summary>
public abstract class ParseRetryTestBase : ArchiveTestBase
{
    protected sealed record Target(Guid Project, Guid Document, Guid Version, Guid Preparation, Guid Job);

    protected async Task<Target> NewTarget(HttpClient client, Guid? project = null, string name = "nota.txt", string contentType = "text/plain", byte[]? bytes = null)
    {
        var projectId = project ?? await NewProject(client);
        var created = await Json(await Upload(client, projectId, bytes ?? Text("Contenuto"), name, contentType));
        var version = created.GetProperty("id").GetGuid();
        var preparation = await Scalar<Guid>("SELECT \"Id\" FROM \"DocumentPreparations\" WHERE \"DocumentVersionId\" = @v", ("v", version));
        var job = await Scalar<Guid>("SELECT \"ParseJobId\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", preparation));
        return new(projectId, created.GetProperty("documentId").GetGuid(), version, preparation, job);
    }

    /// <summary>Porta il terminale nello stato dato, come lo lascerebbe il worker (job Failed con errore, oppure Completed con risultato).</summary>
    protected async Task Terminal(Target t, string jobStatus, string preparationStatus, string error = "attempts_exhausted")
    {
        if (jobStatus == "Failed")
        {
            await Execute(
                "UPDATE \"DocumentJobs\" SET \"Status\"='Failed',\"ErrorCode\"=@e,\"ErrorMessage\"='Parsing non riuscito.',\"CompletedAt\"=now(),\"AttemptCount\"=3,\"StartedAt\"=now(),\"FencingToken\"=3 WHERE \"Id\"=@j",
                ("e", error), ("j", t.Job));
        }
        else if (jobStatus == "Completed")
        {
            await Execute("UPDATE \"DocumentJobs\" SET \"Status\"='Completed',\"ResultHash\"=@h,\"CompletedAt\"=now(),\"AttemptCount\"=1,\"StartedAt\"=now(),\"FencingToken\"=1 WHERE \"Id\"=@j", ("h", new string('a', 64)), ("j", t.Job));
        }
        else if (jobStatus == "Running")
        {
            await Execute("UPDATE \"DocumentJobs\" SET \"Status\"='Running',\"LeaseOwner\"='w',\"LeaseUntil\"=now()+interval '1 hour',\"AttemptCount\"=1,\"StartedAt\"=now(),\"FencingToken\"=1 WHERE \"Id\"=@j", ("j", t.Job));
        }

        var failed = preparationStatus == "Failed";
        await Execute(
            "UPDATE \"DocumentPreparations\" SET \"Status\"=@s,\"ErrorCode\"=@e,\"ErrorMessage\"=@m WHERE \"Id\"=@p",
            ("s", preparationStatus), ("e", failed ? error : DBNull.Value), ("m", failed ? "Parsing non riuscito." : DBNull.Value), ("p", t.Preparation));
    }

    protected Task<HttpResponseMessage> Retry(HttpClient client, Guid project, Guid job, Guid? key = null, bool withKey = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/document-jobs/{job}/retry");
        if (withKey)
        {
            request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        }

        return client.SendAsync(request, Ct);
    }

    protected async Task<string> RowJson(string table, Guid id) => await Scalar<string>($"SELECT row_to_json(t)::text FROM \"{table}\" t WHERE \"Id\" = @i", ("i", id));

    protected static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await Json(response)).GetProperty("code").GetString());
    }

}

/// <summary>Le prove di T116 su API + PostgreSQL senza worker attivo (vedi i commenti di ogni classe per le varianti con worker).</summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentParseRetryTests : ParseRetryTestBase
{
    // ---- percorso felice ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_parse_job_is_retried_into_a_new_linked_request_with_a_new_t0_and_the_old_root_untouched()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        await Terminal(t, "Failed", "Failed");
        await Execute("UPDATE \"DocumentPreparations\" SET \"EffectiveDeadlineAt\" = now() - interval '1 minute' WHERE \"Id\" = @p", ("p", t.Preparation));   // root vecchio scaduto
        var (oldJob, oldPrep) = (await RowJson("DocumentJobs", t.Job), await RowJson("DocumentPreparations", t.Preparation));
        var download = await client.GetByteArrayAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1/download", Ct);
        var key = Guid.NewGuid();

        var response = await Retry(client, t.Project, t.Job, key);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await Json(response);
        var (newJob, newPrep) = (body.GetProperty("jobId").GetGuid(), body.GetProperty("preparationId").GetGuid());
        Assert.NotEqual(t.Job, newJob);
        Assert.Equal(t.Job, body.GetProperty("retryOfJobId").GetGuid());
        Assert.Equal(t.Preparation, body.GetProperty("retryOfPreparationId").GetGuid());
        Assert.Equal(2, body.GetProperty("generation").GetInt32());
        Assert.Equal("Queued", body.GetProperty("status").GetString());
        Assert.Equal(newJob, await Scalar<Guid>("SELECT \"ParseJobId\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", newPrep)));
        Assert.Equal(t.Job, await Scalar<Guid>("SELECT \"RetryOfId\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", newJob)));
        Assert.Equal(key, await Scalar<Guid>("SELECT \"RequestKey\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", newJob)));
        Assert.Equal(t.Preparation, await Scalar<Guid>("SELECT \"RetryOfId\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", newPrep)));
        Assert.Equal(t.Version, await Scalar<Guid>("SELECT \"DocumentVersionId\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", newPrep)));

        // Nuovo t0 esplicito e cap congelato adesso: il nuovo root non eredita la scadenza (già passata) del vecchio.
        Assert.True(await Scalar<bool>("SELECT \"RequestedAt\" > (SELECT \"RequestedAt\" FROM \"DocumentPreparations\" WHERE \"Id\" = @o) FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("o", t.Preparation), ("p", newPrep)));
        Assert.True(await Scalar<bool>("SELECT \"EffectiveDeadlineAt\" > now() + interval '30 minutes' AND \"EffectiveDeadlineAt\" = \"HardDeadlineAt\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", newPrep)));
        Assert.True(await Scalar<bool>("SELECT j.\"DeadlineAt\" = p.\"HardDeadlineAt\" AND j.\"AttemptCount\" = 0 AND j.\"MaxAttempts\" = 3 AND j.\"Status\" = 'Queued' FROM \"DocumentJobs\" j JOIN \"DocumentPreparations\" p ON p.\"Id\" = j.\"PreparationId\" WHERE j.\"Id\" = @j", ("j", newJob)));
        Assert.Equal("Queued", await Scalar<string>("SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", newPrep)));

        // Vecchio root, errore, tempi e originale immutati.
        Assert.Equal(oldJob, await RowJson("DocumentJobs", t.Job));
        Assert.Equal(oldPrep, await RowJson("DocumentPreparations", t.Preparation));
        Assert.Equal(download, await client.GetByteArrayAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1/download", Ct));
        Assert.Equal(1L, await Count("SELECT count(*) FROM \"DocumentVersions\" WHERE \"DocumentId\" = @d", ("d", t.Document)));
        Assert.Single(StoredObjects());
    }

    [Fact]
    public async Task A_completed_job_whose_preparation_is_incomplete_can_be_retried()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        await Terminal(t, "Completed", "Incomplete");

        var response = await Retry(client, t.Project, t.Job);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2L, await Count("SELECT count(*) FROM \"DocumentPreparations\" WHERE \"DocumentVersionId\" = @v", ("v", t.Version)));
        Assert.Equal("Incomplete", await Scalar<string>("SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", t.Preparation)));   // il vecchio resta Incomplete
    }

    // ---- idempotenza ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_key_and_job_replay_the_same_ids_and_never_queue_a_second_request()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        await Terminal(t, "Failed", "Failed");
        var key = Guid.NewGuid();

        var first = await Json(await Retry(client, t.Project, t.Job, key));
        var replay = await Retry(client, t.Project, t.Job, key);

        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var second = await Json(replay);
        Assert.Equal(first.GetProperty("jobId").GetGuid(), second.GetProperty("jobId").GetGuid());
        Assert.Equal(first.GetProperty("preparationId").GetGuid(), second.GetProperty("preparationId").GetGuid());
        Assert.Equal(2L, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"ProjectId\" = @p", ("p", t.Project)));
        Assert.Equal(2L, await Count("SELECT count(*) FROM \"DocumentPreparations\" WHERE \"ProjectId\" = @p", ("p", t.Project)));
    }

    [Fact]
    public async Task The_same_key_for_a_different_job_is_an_idempotency_conflict_and_queues_nothing()
    {
        using var client = await App.LoginAsync();
        var a = await NewTarget(client);
        var b = await NewTarget(client, a.Project, "altro.txt");
        await Terminal(a, "Failed", "Failed");
        await Terminal(b, "Failed", "Failed");
        var key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await Retry(client, a.Project, a.Job, key)).StatusCode);

        await AssertProblem(await Retry(client, a.Project, b.Job, key), HttpStatusCode.Conflict, "idempotency_conflict");

        Assert.Equal(3L, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"ProjectId\" = @p", ("p", a.Project)));   // due originali + un solo retry
    }

    [Theory]
    [InlineData(null)]
    [InlineData("non-un-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_missing_or_malformed_idempotency_key_is_rejected_before_any_change(string? key)
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        await Terminal(t, "Failed", "Failed");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{t.Project}/document-jobs/{t.Job}/retry");
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        await AssertProblem(await client.SendAsync(request, Ct), HttpStatusCode.BadRequest, "idempotency_key_required");

        Assert.Equal(1L, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"ProjectId\" = @p", ("p", t.Project)));
    }

    // ---- conflitti -------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Queued", "Queued", "job_active")]
    [InlineData("Running", "Parsing", "job_active")]
    [InlineData("Completed", "Extracting", "retry_not_allowed")]
    public async Task A_job_that_is_active_or_not_retryable_conflicts_without_creating_anything(string jobStatus, string preparationStatus, string code)
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        if (jobStatus != "Queued")
        {
            await Terminal(t, jobStatus, preparationStatus);
        }

        await AssertProblem(await Retry(client, t.Project, t.Job), HttpStatusCode.Conflict, code);

        Assert.Equal(1L, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"ProjectId\" = @p", ("p", t.Project)));
        Assert.Equal(1L, await Count("SELECT count(*) FROM \"DocumentPreparations\" WHERE \"ProjectId\" = @p", ("p", t.Project)));
    }

    [Fact]
    public async Task A_retry_while_the_new_request_is_open_conflicts_and_a_superseded_terminal_cannot_be_retried_again()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        await Terminal(t, "Failed", "Failed");
        var created = await Json(await Retry(client, t.Project, t.Job));
        var newPrep = created.GetProperty("preparationId").GetGuid();

        await AssertProblem(await Retry(client, t.Project, t.Job), HttpStatusCode.Conflict, "preparation_active");   // nuova chiave: la richiesta aperta esiste già

        // Il nuovo root fallisce a sua volta: il vecchio terminale e ormai superato e non riparte.
        await Execute("UPDATE \"DocumentJobs\" SET \"Status\"='Failed',\"ErrorCode\"='attempts_exhausted',\"ErrorMessage\"='x',\"CompletedAt\"=now(),\"AttemptCount\"=3 WHERE \"PreparationId\"=@p", ("p", newPrep));
        await Execute("UPDATE \"DocumentPreparations\" SET \"Status\"='Failed',\"ErrorCode\"='attempts_exhausted',\"ErrorMessage\"='x' WHERE \"Id\"=@p", ("p", newPrep));
        await AssertProblem(await Retry(client, t.Project, t.Job), HttpStatusCode.Conflict, "job_superseded");
        Assert.Equal(HttpStatusCode.Accepted, (await Retry(client, t.Project, created.GetProperty("jobId").GetGuid())).StatusCode);   // l ultimo terminale si può ritentare
        Assert.Equal(3L, await Count("SELECT count(*) FROM \"DocumentPreparations\" WHERE \"DocumentVersionId\" = @v", ("v", t.Version)));
    }

    [Fact]
    public async Task Two_concurrent_retries_with_different_keys_queue_exactly_one_request()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        await Terminal(t, "Failed", "Failed");

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Retry(client, t.Project, t.Job)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(2L, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"ProjectId\" = @p", ("p", t.Project)));
    }

    [Fact]
    public async Task Concurrent_replays_of_the_same_key_return_the_same_request()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        await Terminal(t, "Failed", "Failed");
        var key = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Retry(client, t.Project, t.Job, key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var ids = (await Task.WhenAll(responses.Select(Json))).Select(b => b.GetProperty("jobId").GetGuid()).Distinct().ToList();
        Assert.Single(ids);
        Assert.Equal(2L, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"ProjectId\" = @p", ("p", t.Project)));
    }

    // ---- autorizzazione e confini di progetto ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Other_projects_unknown_jobs_and_anonymous_callers_get_404_or_401_with_no_effect()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        var other = await NewTarget(client);
        await Terminal(t, "Failed", "Failed");

        Assert.Equal(HttpStatusCode.NotFound, (await Retry(client, other.Project, t.Job)).StatusCode);   // job di un altro progetto
        Assert.Equal(HttpStatusCode.NotFound, (await Retry(client, t.Project, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Retry(client, Guid.NewGuid(), t.Job)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Retry(App.Anonymous(), t.Project, t.Job)).StatusCode);
        using var noRole = App.WithoutPmRole();
        Assert.Equal(HttpStatusCode.Forbidden, (await Retry(noRole, t.Project, t.Job)).StatusCode);

        Assert.Equal(1L, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", t.Job)));
        Assert.Equal(2L, await Count("SELECT count(*) FROM \"DocumentJobs\""));   // solo i due job di acquisizione
    }

    // ---- GET non ritenta; allowedActions -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reading_a_terminal_never_retries_and_exposes_retry_only_where_allowed()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        var url = $"/api/projects/{t.Project}/documents/{t.Document}/versions/1/preparations/1";

        var active = await Json(await client.GetAsync(url, Ct));
        Assert.DoesNotContain("retry", active.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(t.Job, active.GetProperty("parseJobId").GetGuid());

        await Terminal(t, "Failed", "Failed");
        for (var i = 0; i < 3; i++)
        {
            var failed = await Json(await client.GetAsync(url, Ct));
            Assert.Contains("retry", failed.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
            Assert.Contains("download", failed.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
        }

        Assert.Equal(1L, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"ProjectId\" = @p", ("p", t.Project)));   // GET (e polling) non ritenta

        await Retry(client, t.Project, t.Job);
        var superseded = await Json(await client.GetAsync(url, Ct));
        Assert.DoesNotContain("retry", superseded.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));   // il terminale e superato: nessuna azione
    }

    [Fact]
    public async Task A_retry_of_an_exhausted_transient_error_gets_fresh_bounded_attempts_while_the_old_counters_stay()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        await Terminal(t, "Failed", "Failed", "attempts_exhausted");

        var created = await Json(await Retry(client, t.Project, t.Job));

        var job = created.GetProperty("jobId").GetGuid();
        Assert.Equal(0, await Scalar<int>("SELECT \"AttemptCount\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", job)));
        Assert.Equal(3, await Scalar<int>("SELECT \"MaxAttempts\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", job)));   // limite di tentativi invariato, non infinito
        Assert.Equal(3, await Scalar<int>("SELECT \"AttemptCount\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", t.Job)));
        Assert.Equal("attempts_exhausted", await Scalar<string>("SELECT \"ErrorCode\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", t.Job)));
        Assert.Equal(await Scalar<string>("SELECT \"PolicyHash\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", t.Preparation)),
            await Scalar<string>("SELECT \"PolicyHash\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", created.GetProperty("preparationId").GetGuid())));   // stessa policy: cap congelato dalla policy corrente
    }
}

/// <summary>
/// T116 (worker reale, nessun parser raggiungibile): i tentativi del nuovo job sono limitati a 3 con backoff 2/4 s e il vecchio root non cambia;
/// il retry non è un secondo scheduler, il worker del parsing è lo stesso di T029.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentParseRetryBoundedTests : ParseRetryTestBase
{
    protected override void ConfigureServices(IServiceCollection services) =>
        services.Configure<ParserOptions>(o => { o.BaseUrl = "http://127.0.0.1:9/"; o.Secret = "non-usato"; o.PollSeconds = 1; });

    [Fact]
    public async Task The_new_request_uses_at_most_three_attempts_and_fails_explicitly_while_the_old_root_stays_untouched()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        // Il worker potrebbe aver gia preso in carico il job di acquisizione (parser irraggiungibile): attendo che sia terminale per davvero.
        await WaitFor("SELECT \"Status\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", t.Job, "Failed", TimeSpan.FromSeconds(40));
        var oldJob = await RowJson("DocumentJobs", t.Job);
        var oldPrep = await RowJson("DocumentPreparations", t.Preparation);

        var created = await Json(await Retry(client, t.Project, t.Job));
        var job = created.GetProperty("jobId").GetGuid();
        await WaitFor("SELECT \"Status\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", job, "Failed", TimeSpan.FromSeconds(40));

        Assert.Equal(3, await Scalar<int>("SELECT \"AttemptCount\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", job)));
        Assert.Equal("attempts_exhausted", await Scalar<string>("SELECT \"ErrorCode\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", job)));
        Assert.Equal("Failed", await Scalar<string>("SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", created.GetProperty("preparationId").GetGuid())));
        Assert.Equal(oldJob, await RowJson("DocumentJobs", t.Job));
        Assert.Equal(oldPrep, await RowJson("DocumentPreparations", t.Preparation));
        Assert.Equal(0L, await Count("SELECT count(*) FROM \"ParsedArtifacts\""));
        Assert.Single(StoredObjects());
    }

    private async Task WaitFor(string sql, Guid id, string expected, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (await Scalar<string>(sql, ("j", id)) == expected)
            {
                return;
            }

            await Task.Delay(500, Ct);
        }

        Assert.Equal(expected, await Scalar<string>(sql, ("j", id)));
    }
}

/// <summary>
/// T116 (API + PostgreSQL + parser REALI, DGX assente): da un fallimento recuperabile il PM ritenta, il worker (lo stesso di T029) converte con
/// Docling, la nuova generazione e leggibile ma non Ready, lo storico del fallimento e l originale restano. Skipped senza il parser.
/// </summary>
[Trait("Category", TestCategories.Integration)]
[Trait("Category", DocumentTestCategories.Parser)]
[Collection(RealParser.CollectionName)]
public sealed class DocumentParseRetryRealParserTests : ParseRetryTestBase
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        var endpoint = Environment.GetEnvironmentVariable(DocumentTestEnvironment.ParserEndpointVariable);
        var secret = Environment.GetEnvironmentVariable(RealParser.SecretVariable);
        if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(secret))
        {
            services.Configure<ParserOptions>(o => { o.BaseUrl = endpoint; o.Secret = secret; o.PollSeconds = 1; });
        }
    }

    [Theory]
    [InlineData("pdf-mixed.pdf", "application/pdf")]
    [InlineData("docx-image.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public async Task A_manual_retry_of_an_incomplete_real_parse_creates_one_linked_request_converted_again_keeping_history_and_original(string fixture, string contentType)
    {
        RealParser.Options();   // Skipped senza parser reale
        using var client = await App.LoginAsync();
        var bytes = RealParser.Fixture(fixture);
        var t = await NewTarget(client, null, fixture, contentType, bytes);

        // Il worker reale (T029) converte l acquisizione: job Completed ma preparazione Incomplete (immagine informativa non trattata).
        await WaitStatus(t.Job, ["Completed"]);
        Assert.Equal("Incomplete", await Scalar<string>("SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", t.Preparation)));
        var url = $"/api/projects/{t.Project}/documents/{t.Document}/versions/1/preparations";
        var first = await Json(await client.GetAsync(url + "/1", Ct));
        Assert.Contains("retry", first.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
        Assert.False(first.GetProperty("isReady").GetBoolean());
        var (oldJob, oldPrep) = (await RowJson("DocumentJobs", t.Job), await RowJson("DocumentPreparations", t.Preparation));
        var oldBlocks = await Count("SELECT count(*) FROM \"DocumentBlocks\" b JOIN \"ParsedArtifacts\" a ON a.\"Id\" = b.\"ArtifactId\" WHERE a.\"PreparationId\" = @p", ("p", t.Preparation));

        var key = Guid.NewGuid();
        var response = await Retry(client, t.Project, t.Job, key);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await Json(response);
        var (newJob, newPrep) = (body.GetProperty("jobId").GetGuid(), body.GetProperty("preparationId").GetGuid());
        Assert.Equal(HttpStatusCode.Accepted, (await Retry(client, t.Project, t.Job, key)).StatusCode);   // replay mentre e in coda o in corso: stessi ID

        await WaitStatus(newJob, ["Completed"]);
        Assert.Equal("Incomplete", await Scalar<string>("SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", newPrep)));   // stesso input, stesso esito: mai Ready
        Assert.Equal(1L, await Count("SELECT count(*) FROM \"ParsedArtifacts\" WHERE \"PreparationId\" = @p", ("p", newPrep)));
        Assert.Equal(oldBlocks, await Count("SELECT count(*) FROM \"DocumentBlocks\" b JOIN \"ParsedArtifacts\" a ON a.\"Id\" = b.\"ArtifactId\" WHERE a.\"PreparationId\" = @p", ("p", newPrep)));
        Assert.True(await Count("SELECT count(*) FROM \"PreparationIssues\" WHERE \"PreparationId\" = @p AND \"AffectsCompleteness\"", ("p", newPrep)) > 0);
        Assert.Equal(oldJob, await RowJson("DocumentJobs", t.Job));
        Assert.Equal(oldPrep, await RowJson("DocumentPreparations", t.Preparation));   // lo storico del primo root resta immutato
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1/download", Ct));   // byte dell originale
        Assert.Equal(RealParser.Sha256(bytes), await Scalar<string>("SELECT \"Sha256\" FROM \"DocumentVersions\" WHERE \"Id\" = @v", ("v", t.Version)));

        var view = await Json(await client.GetAsync(url + "/2", Ct));
        Assert.Equal("Incomplete", view.GetProperty("processingStatus").GetString());
        Assert.Equal(t.Preparation, view.GetProperty("retryOfId").GetGuid());
        Assert.Contains("retry", view.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));   // l ultimo terminale e di nuovo ritentabile
        Assert.DoesNotContain("retry", (await Json(await client.GetAsync(url + "/1", Ct))).GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));   // il primo e superato

        var replay = await Json(await Retry(client, t.Project, t.Job, key));   // replay dopo la conversione: stessi ID, nulla in coda
        Assert.Equal(newJob, replay.GetProperty("jobId").GetGuid());
        Assert.Equal(0L, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"Status\" IN ('Queued','Running')"));
        Assert.Equal(2L, await Count("SELECT count(*) FROM \"DocumentPreparations\" WHERE \"DocumentVersionId\" = @v", ("v", t.Version)));
    }

    private async Task WaitStatus(Guid job, string[] expected)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        string status = "";
        while (DateTime.UtcNow < end)
        {
            status = await Scalar<string>("SELECT \"Status\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", job));
            if (expected.Contains(status))
            {
                return;
            }

            await Task.Delay(500, Ct);
        }

        Assert.Contains(status, expected);
    }
}
