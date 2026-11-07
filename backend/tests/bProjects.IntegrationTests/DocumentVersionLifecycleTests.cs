using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T093 (contratto HTTP + PostgreSQL, nessun DGX né modello) — tabella di lifecycle F6 delle versioni file: rielaborazione candidata
/// (POST .../preparations), retry terminale oltre il solo parsing, promozione esplicita del bundle con expectedRevision (200 anche in replay, 409 su
/// CAS, 422 se la preparazione non è promovibile), nessuna promozione dell'incompleto, esclusione persistente e «ultima versione pronta» distinta dalla
/// massima generazione Ready. Gli stati intermedi sono scritti con le stesse entità dell'aggregatore (T051): il worker del parsing non è configurato,
/// quindi il lavoro accodato resta Queued. Le letture (GET) non ritentano né accodano mai nulla.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentVersionLifecycleTests : ParseRetryTestBase
{
    private sealed record Generation(Guid Preparation, Guid ParseJob, Guid? EvidenceVersion, int Number);

    private static string PrepUrl(Target t, string suffix = "") => $"/api/projects/{t.Project}/documents/{t.Document}/versions/1/preparations{suffix}";

    private Task<HttpResponseMessage> PostPreparations(HttpClient client, Target t, Guid? key = null, bool withKey = true, Guid? project = null, Guid? document = null, string version = "1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project ?? t.Project}/documents/{document ?? t.Document}/versions/{version}/preparations");
        if (withKey)
        {
            request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        }

        return client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> Promote(HttpClient client, Target t, string generation, object? body, Guid? project = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project ?? t.Project}/documents/{t.Document}/versions/1/preparations/{generation}/promote");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request, Ct);
    }

    private static object Expect(int revision, string? reason = null) => new { expectedRevision = revision, reason };

    // ---- seed ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Porta in una generazione dello stato dato. La 1 esiste già (acquisizione); le successive sono inserite come le lascerebbe il servizio.
    /// Ready scrive aggregatore, versione Evidence e collega la preparazione; Failed/Incomplete/Extracting restano senza versione Evidence.
    /// </summary>
    private async Task<Generation> SeedGenerationAsync(Target t, int number, PreparationStatus status, bool issue = false)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        DocumentPreparation preparation;
        DocumentJob parse;
        if (number == 1)
        {
            preparation = await db.DocumentPreparations.SingleAsync(p => p.Id == t.Preparation, Ct);
            parse = await db.DocumentJobs.SingleAsync(j => j.Id == t.Job, Ct);
        }
        else
        {
            preparation = new DocumentPreparation
            {
                Id = Guid.NewGuid(), ProjectId = t.Project, DocumentVersionId = t.Version, Generation = number, Status = PreparationStatus.Queued,
                PolicyHash = new string('e', 64), RequestedAt = now, HardDeadlineAt = now.AddHours(1), EffectiveDeadlineAt = now.AddHours(1), CreatedAt = now,
            };
            db.DocumentPreparations.Add(preparation);
            await db.SaveChangesAsync(Ct);
            parse = new DocumentJob
            {
                Id = Guid.NewGuid(), ProjectId = t.Project, PreparationId = preparation.Id, Kind = DocumentJobKind.Parse, Status = DocumentJobStatus.Queued,
                RequestKey = Guid.NewGuid(), RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3,
            };
            db.DocumentJobs.Add(parse);
            await db.SaveChangesAsync(Ct);
            preparation.ParseJobId = parse.Id;
            await db.SaveChangesAsync(Ct);
        }

        if (status != PreparationStatus.Queued)
        {
            parse.Status = DocumentJobStatus.Completed;
            parse.ResultHash = new string('a', 64);
            parse.StartedAt = now;
            parse.CompletedAt = now;
            parse.AttemptCount = 1;
            parse.FencingToken = 1;
            await db.SaveChangesAsync(Ct);
        }

        Guid? evidenceVersion = null;
        if (status == PreparationStatus.Ready)
        {
            var job = new EvidenceExtractionJob
            {
                Id = Guid.NewGuid(), ProjectId = t.Project, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Reason = ExtractionJobReason.Initial,
                Status = ExtractionJobStatus.Queued, RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
            };
            db.EvidenceExtractionJobs.Add(job);
            await db.SaveChangesAsync(Ct);
            var version = new EvidenceExtractionVersion
            {
                Id = Guid.NewGuid(), ProjectId = t.Project, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, ManifestHash = new string((char)('0' + number), 64),
                Version = 1, JobId = job.Id, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}",
                EvidenceCount = 0, NoEvidenceReason = "documento senza evidenze", CreatedAt = now,
            };
            db.EvidenceExtractionVersions.Add(version);
            await db.SaveChangesAsync(Ct);
            evidenceVersion = version.Id;
            preparation.CurrentEvidenceVersionId = version.Id;
        }

        preparation.Status = status;
        if (issue)
        {
            db.PreparationIssues.Add(new PreparationIssue
            {
                Id = Guid.NewGuid(), ProjectId = t.Project, PreparationId = preparation.Id, Code = "image_unprocessed", Severity = IssueSeverity.Blocking,
                Reason = "Immagine non trattata", AffectsCompleteness = true,
            });
        }

        if (status is PreparationStatus.Failed or PreparationStatus.Incomplete)
        {
            preparation.ErrorCode = status == PreparationStatus.Failed ? "extraction_failed" : "incomplete_coverage";
            preparation.ErrorMessage = "Estrazione non riuscita.";
        }

        await db.SaveChangesAsync(Ct);
        return new(preparation.Id, parse.Id, evidenceVersion, number);
    }

    /// <summary>Stato corrente della versione file, come lo lascia la pubblicazione automatica della prima preparazione completa (revisione 1).</summary>
    private async Task MakeCurrentAsync(Target t, Generation g)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = t.Version, ProjectId = t.Project, CurrentPreparationId = g.Preparation, CurrentEvidenceVersionId = g.EvidenceVersion, Revision = 1, UpdatedAt = now,
        });
        db.DocumentPromotions.Add(new DocumentPromotion
        {
            Id = Guid.NewGuid(), ProjectId = t.Project, DocumentVersionId = t.Version, FromPreparationId = null, FromEvidenceVersionId = null,
            ToPreparationId = g.Preparation, ToEvidenceVersionId = g.EvidenceVersion!.Value, PromotedBy = null, PromotedAt = now, Reason = EvidencePromotion.FirstValidVersionReason,
        });
        await db.SaveChangesAsync(Ct);
        await Execute(
            "UPDATE \"ProjectDocuments\" SET \"LatestReadyVersionId\" = @v WHERE \"Id\" = @d", ("v", t.Version), ("d", t.Document));
    }

    /// <summary>v1 con g1 Ready e corrente (revisione 1) e le generazioni successive indicate come candidate.</summary>
    private async Task<(Target Target, Generation[] Generations)> CurrentWithCandidatesAsync(HttpClient client, params PreparationStatus[] candidates)
    {
        var t = await NewTarget(client);
        var first = await SeedGenerationAsync(t, 1, PreparationStatus.Ready);
        await MakeCurrentAsync(t, first);
        var all = new List<Generation> { first };
        for (var i = 0; i < candidates.Length; i++)
        {
            all.Add(await SeedGenerationAsync(t, i + 2, candidates[i], issue: false));
        }

        return (t, [.. all]);
    }

    private async Task<(Guid? Preparation, Guid? Evidence, int Revision)> State(Target t)
    {
        var revision = await Scalar<int>("SELECT COALESCE((SELECT \"Revision\" FROM \"EvidenceDocumentStates\" WHERE \"DocumentVersionId\" = @v), -1)", ("v", t.Version));
        var prep = await Scalar<string>("SELECT COALESCE((SELECT \"CurrentPreparationId\"::text FROM \"EvidenceDocumentStates\" WHERE \"DocumentVersionId\" = @v), '')", ("v", t.Version));
        var evidence = await Scalar<string>("SELECT COALESCE((SELECT \"CurrentEvidenceVersionId\"::text FROM \"EvidenceDocumentStates\" WHERE \"DocumentVersionId\" = @v), '')", ("v", t.Version));
        return (prep.Length == 0 ? null : Guid.Parse(prep), evidence.Length == 0 ? null : Guid.Parse(evidence), revision);
    }

    private Task<string> StateRowJson(Target t) => Scalar<string>("SELECT row_to_json(t)::text FROM \"EvidenceDocumentStates\" t WHERE \"DocumentVersionId\" = @v", ("v", t.Version));

    private Task<long> Promotions(Target t) => Count("SELECT count(*) FROM \"DocumentPromotions\" WHERE \"DocumentVersionId\" = @v", ("v", t.Version));

    private Task<long> Preparations(Target t) => Count("SELECT count(*) FROM \"DocumentPreparations\" WHERE \"DocumentVersionId\" = @v", ("v", t.Version));

    private Task<long> Jobs(Target t) => Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"ProjectId\" = @p", ("p", t.Project));

    // ================================================================ POST preparations: rielaborazione candidata

    [Fact]
    public async Task Reprocessing_a_ready_version_queues_a_distinct_candidate_and_leaves_the_current_bundle_and_every_old_row_untouched()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client);
        var before = (await RowJson("DocumentPreparations", g[0].Preparation), await RowJson("DocumentJobs", g[0].ParseJob), await StateRowJson(t));
        var download = await client.GetByteArrayAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1/download", Ct);
        var key = Guid.NewGuid();

        var response = await PostPreparations(client, t, key);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await Json(response);
        var (newJob, newPrep) = (body.GetProperty("jobId").GetGuid(), body.GetProperty("preparationId").GetGuid());
        Assert.NotEqual(g[0].Preparation, newPrep);
        Assert.Equal(2, body.GetProperty("generation").GetInt32());
        Assert.Equal("Queued", body.GetProperty("status").GetString());
        Assert.Equal(g[0].Preparation, body.GetProperty("retryOfPreparationId").GetGuid());
        Assert.Equal(g[0].ParseJob, body.GetProperty("retryOfJobId").GetGuid());
        Assert.Equal(t.Version, await Scalar<Guid>("SELECT \"DocumentVersionId\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", newPrep)));
        Assert.Equal(newJob, await Scalar<Guid>("SELECT \"ParseJobId\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", newPrep)));
        Assert.Equal(key, await Scalar<Guid>("SELECT \"RequestKey\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", newJob)));
        Assert.True(await Scalar<bool>("SELECT j.\"Status\" = 'Queued' AND j.\"Kind\" = 'Parse' AND j.\"AttemptCount\" = 0 AND j.\"MaxAttempts\" = 3 AND j.\"DeadlineAt\" = p.\"HardDeadlineAt\" AND p.\"Status\" = 'Queued' AND p.\"RequestedAt\" > (SELECT \"RequestedAt\" FROM \"DocumentPreparations\" WHERE \"Id\" = @o) FROM \"DocumentJobs\" j JOIN \"DocumentPreparations\" p ON p.\"Id\" = j.\"PreparationId\" WHERE j.\"Id\" = @j", ("j", newJob), ("o", g[0].Preparation)));   // nuovo t0

        // Candidata, non corrente: puntatori, revisione e promozioni invariati; la vecchia riga è identica; l'originale non cambia.
        var state = await State(t);
        Assert.Equal((g[0].Preparation, g[0].EvidenceVersion, 1), state);
        Assert.Equal(1L, await Promotions(t));
        Assert.Equal(before.Item1, await RowJson("DocumentPreparations", g[0].Preparation));
        Assert.Equal(before.Item2, await RowJson("DocumentJobs", g[0].ParseJob));
        Assert.Equal(before.Item3, await StateRowJson(t));
        Assert.Equal(download, await client.GetByteArrayAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1/download", Ct));
        Assert.Equal(1L, await Count("SELECT count(*) FROM \"DocumentVersions\" WHERE \"DocumentId\" = @d", ("d", t.Document)));
        Assert.Single(StoredObjects());
    }

    [Fact]
    public async Task The_new_generation_freezes_the_current_policy_hash_and_a_cap_that_starts_now()
    {
        using var client = await App.LoginAsync();
        var (t, _) = await CurrentWithCandidatesAsync(client);
        await Execute("UPDATE \"DocumentPreparations\" SET \"EffectiveDeadlineAt\" = now() - interval '1 minute' WHERE \"DocumentVersionId\" = @v", ("v", t.Version));   // il root vecchio è scaduto
        string currentHash;
        using (var scope = App.Services.CreateScope())
        {
            currentHash = (await scope.ServiceProvider.GetRequiredService<DocumentPolicyService>().GetAsync(Ct)).Snapshot.PolicyHash;
        }

        var created = await Json(await PostPreparations(client, t));

        var prep = created.GetProperty("preparationId").GetGuid();
        Assert.Equal(currentHash, await Scalar<string>("SELECT \"PolicyHash\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", prep)));
        Assert.True(await Scalar<bool>("SELECT \"EffectiveDeadlineAt\" > now() + interval '30 minutes' AND \"EffectiveDeadlineAt\" = \"HardDeadlineAt\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", prep)));
    }

    [Fact]
    public async Task The_same_key_replays_the_same_candidate_and_never_queues_a_second_one()
    {
        using var client = await App.LoginAsync();
        var (t, _) = await CurrentWithCandidatesAsync(client);
        var key = Guid.NewGuid();
        var first = await Json(await PostPreparations(client, t, key));
        var jobs = await Jobs(t);

        var replay = await PostPreparations(client, t, key);

        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var second = await Json(replay);
        Assert.Equal(first.GetProperty("jobId").GetGuid(), second.GetProperty("jobId").GetGuid());
        Assert.Equal(first.GetProperty("preparationId").GetGuid(), second.GetProperty("preparationId").GetGuid());
        Assert.Equal(2, second.GetProperty("generation").GetInt32());
        Assert.Equal(jobs, await Jobs(t));
        Assert.Equal(2L, await Preparations(t));
    }

    [Fact]
    public async Task A_key_already_used_for_another_version_or_for_the_upload_is_an_idempotency_conflict()
    {
        using var client = await App.LoginAsync();
        var (t, _) = await CurrentWithCandidatesAsync(client);
        var other = await NewTarget(client, t.Project, "altro.txt");
        await SeedGenerationAsync(other, 1, PreparationStatus.Ready);
        var key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await PostPreparations(client, t, key)).StatusCode);
        var jobs = await Jobs(t);

        await AssertProblem(await PostPreparations(client, t, key, document: other.Document), HttpStatusCode.Conflict, "idempotency_conflict");   // altra versione, stessa chiave
        var uploadKey = await Scalar<Guid>("SELECT \"RequestKey\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", other.Job));
        await AssertProblem(await PostPreparations(client, other, uploadKey), HttpStatusCode.Conflict, "idempotency_conflict");   // la chiave dell'acquisizione non è un comando di rielaborazione

        Assert.Equal(jobs, await Jobs(t));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("non-un-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_missing_or_malformed_idempotency_key_is_rejected_before_any_change(string? key)
    {
        using var client = await App.LoginAsync();
        var (t, _) = await CurrentWithCandidatesAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Post, PrepUrl(t));
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        await AssertProblem(await client.SendAsync(request, Ct), HttpStatusCode.BadRequest, "idempotency_key_required");

        Assert.Equal(1L, await Preparations(t));
    }

    [Theory]
    [InlineData(PreparationStatus.Queued)]
    [InlineData(PreparationStatus.Parsing)]
    [InlineData(PreparationStatus.Extracting)]
    public async Task A_version_with_an_open_preparation_conflicts_and_creates_nothing(PreparationStatus open)
    {
        using var client = await App.LoginAsync();
        var (t, _) = await CurrentWithCandidatesAsync(client, open);
        var jobs = await Jobs(t);

        await AssertProblem(await PostPreparations(client, t), HttpStatusCode.Conflict, "preparation_active");

        Assert.Equal(jobs, await Jobs(t));
        Assert.Equal(2L, await Preparations(t));
    }

    [Fact]
    public async Task The_candidate_just_queued_blocks_a_second_command_with_a_new_key()
    {
        using var client = await App.LoginAsync();
        var (t, _) = await CurrentWithCandidatesAsync(client);
        Assert.Equal(HttpStatusCode.Accepted, (await PostPreparations(client, t)).StatusCode);

        await AssertProblem(await PostPreparations(client, t), HttpStatusCode.Conflict, "preparation_active");

        Assert.Equal(2L, await Preparations(t));
    }

    [Fact]
    public async Task Concurrent_commands_with_different_keys_queue_exactly_one_candidate()
    {
        using var client = await App.LoginAsync();
        var (t, _) = await CurrentWithCandidatesAsync(client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostPreparations(client, t)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(2L, await Preparations(t));
    }

    [Fact]
    public async Task Concurrent_replays_of_the_same_key_return_the_same_candidate()
    {
        using var client = await App.LoginAsync();
        var (t, _) = await CurrentWithCandidatesAsync(client);
        var key = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostPreparations(client, t, key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Single((await Task.WhenAll(responses.Select(Json))).Select(b => b.GetProperty("preparationId").GetGuid()).Distinct());
        Assert.Equal(2L, await Preparations(t));
    }

    [Theory]
    [InlineData(PreparationStatus.Failed)]
    [InlineData(PreparationStatus.Incomplete)]
    public async Task A_terminal_preparation_failed_after_parsing_is_retried_into_a_new_generation_and_keeps_its_own_history(PreparationStatus terminal)
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        var g = await SeedGenerationAsync(t, 1, terminal, issue: terminal == PreparationStatus.Incomplete);   // parsing Completed: l'errore è nell'estrazione/aggregazione
        var before = (await RowJson("DocumentPreparations", g.Preparation), await RowJson("DocumentJobs", g.ParseJob));

        var response = await PostPreparations(client, t);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(2, body.GetProperty("generation").GetInt32());
        Assert.Equal(g.Preparation, body.GetProperty("retryOfPreparationId").GetGuid());
        Assert.Equal(before.Item1, await RowJson("DocumentPreparations", g.Preparation));
        Assert.Equal(before.Item2, await RowJson("DocumentJobs", g.ParseJob));
        Assert.Equal(0L, await Promotions(t));   // nessun bundle corrente fittizio
        Assert.Null((await State(t)).Preparation);
    }

    [Fact]
    public async Task The_existing_job_retry_accepts_a_parse_that_completed_but_whose_preparation_failed_afterwards_with_one_shared_service()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        await SeedGenerationAsync(t, 1, PreparationStatus.Failed);

        var response = await Retry(client, t.Project, t.Job);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(2, body.GetProperty("generation").GetInt32());
        Assert.Equal(t.Preparation, body.GetProperty("retryOfPreparationId").GetGuid());
        await AssertProblem(await PostPreparations(client, t), HttpStatusCode.Conflict, "preparation_active");   // l'endpoint di rielaborazione vede il lavoro aperto dal retry
        Assert.Equal(2L, await Preparations(t));
    }

    [Fact]
    public async Task A_key_used_by_the_job_retry_replays_on_the_same_version_without_a_second_generation()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        await SeedGenerationAsync(t, 1, PreparationStatus.Failed);
        var key = Guid.NewGuid();
        var first = await Json(await Retry(client, t.Project, t.Job, key));

        var replay = await Json(await PostPreparations(client, t, key));

        Assert.Equal(first.GetProperty("preparationId").GetGuid(), replay.GetProperty("preparationId").GetGuid());
        Assert.Equal(2L, await Preparations(t));
    }

    [Fact]
    public async Task Reading_never_retries_or_reprocesses_and_a_new_generation_does_not_appear_on_its_own()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        var g = await SeedGenerationAsync(t, 1, PreparationStatus.Failed);
        var jobs = await Jobs(t);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(PrepUrl(t, "/1"), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(PrepUrl(t, "/latest"), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/projects/{t.Project}/documents/{t.Document}", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/projects/{t.Project}/documents", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1/evidence/versions", Ct)).StatusCode);
        }

        Assert.Equal(jobs, await Jobs(t));
        Assert.Equal(1L, await Preparations(t));
        Assert.Equal("Failed", await Scalar<string>("SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", g.Preparation)));
    }

    [Fact]
    public async Task Other_projects_unknown_versions_and_anonymous_callers_get_404_or_401_with_no_effect()
    {
        using var client = await App.LoginAsync();
        var (t, _) = await CurrentWithCandidatesAsync(client);
        var other = await NewTarget(client);

        Assert.Equal(HttpStatusCode.NotFound, (await PostPreparations(client, t, project: other.Project)).StatusCode);   // documento di un altro progetto
        Assert.Equal(HttpStatusCode.NotFound, (await PostPreparations(client, t, version: "9")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostPreparations(client, t, document: Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostPreparations(client, t, project: Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostPreparations(App.Anonymous(), t)).StatusCode);
        using var noRole = App.WithoutPmRole();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostPreparations(noRole, t)).StatusCode);

        Assert.Equal(1L, await Preparations(t));
    }

    // ================================================================ promozione del bundle

    [Fact]
    public async Task Promoting_a_ready_candidate_moves_both_pointers_and_the_revision_together_and_records_the_promotion()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
        var (oldPrep, oldJob, oldState) = (await RowJson("DocumentPreparations", g[0].Preparation), await RowJson("DocumentPreparations", g[1].Preparation), await State(t));

        var response = await Promote(client, t, "2", Expect(1, "  versione corretta  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(2, body.GetProperty("bundleRevision").GetInt32());
        Assert.Equal(g[1].Preparation, body.GetProperty("currentPreparationId").GetGuid());
        Assert.Equal(g[1].EvidenceVersion, body.GetProperty("currentEvidenceVersionId").GetGuid());
        var promotion = body.GetProperty("promotion");
        Assert.Equal(g[0].Preparation, promotion.GetProperty("fromPreparationId").GetGuid());
        Assert.Equal(g[0].EvidenceVersion, promotion.GetProperty("fromEvidenceVersionId").GetGuid());
        Assert.Equal(g[1].Preparation, promotion.GetProperty("toPreparationId").GetGuid());
        Assert.Equal(g[1].EvidenceVersion, promotion.GetProperty("toEvidenceVersionId").GetGuid());
        Assert.Equal("versione corretta", promotion.GetProperty("reason").GetString());
        Assert.NotEqual(JsonValueKind.Null, promotion.GetProperty("promotedBy").ValueKind);

        Assert.Equal((g[1].Preparation, g[1].EvidenceVersion, 2), await State(t));
        Assert.Equal(2L, await Promotions(t));   // prima estrazione valida + promozione esplicita
        Assert.Equal(1L, await Count("SELECT count(*) FROM \"DocumentPromotions\" WHERE \"DocumentVersionId\" = @v AND \"PromotedBy\" IS NOT NULL AND \"FromPreparationId\" = @f AND \"ToPreparationId\" = @t AND \"Reason\" = 'versione corretta'", ("v", t.Version), ("f", g[0].Preparation), ("t", g[1].Preparation)));
        Assert.Equal(oldPrep, await RowJson("DocumentPreparations", g[0].Preparation));   // dati storici immutati: Superseded è solo proiezione
        Assert.Equal(oldJob, await RowJson("DocumentPreparations", g[1].Preparation));
        Assert.NotEqual(oldState.Revision, (await State(t)).Revision);
        Assert.Equal(2L, await Count("SELECT count(*) FROM \"EvidenceExtractionVersions\" WHERE \"PreparationId\" = ANY(@p)", ("p", new[] { g[0].Preparation, g[1].Preparation })));

        // Letture coerenti: la vista della versione e l'elenco dei bundle mostrano il nuovo corrente e la revisione.
        var view = await Json(await client.GetAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1", Ct));
        Assert.Equal(g[1].Preparation, view.GetProperty("currentPreparationId").GetGuid());
        Assert.Equal(2, view.GetProperty("bundleRevision").GetInt32());
        Assert.Empty(view.GetProperty("candidatePreparations").EnumerateArray());
        var bundles = await Json(await client.GetAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1/evidence/versions", Ct));
        Assert.Equal(2, bundles.GetProperty("bundleRevision").GetInt32());
        Assert.Equal(g[1].EvidenceVersion, bundles.GetProperty("currentEvidenceVersionId").GetGuid());
    }

    [Fact]
    public async Task Promoting_by_the_preparation_id_is_equivalent_to_the_generation_number()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);

        var response = await Promote(client, t, g[1].Preparation.ToString(), Expect(1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(g[1].Preparation, (await State(t)).Preparation);
    }

    [Fact]
    public async Task A_replay_on_the_bundle_that_is_already_current_returns_200_without_a_second_promotion_even_with_the_stale_revision()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
        var first = await Json(await Promote(client, t, "2", Expect(1, "motivo")));
        var promotions = await Promotions(t);

        var replay = await Promote(client, t, "2", Expect(1, "motivo"));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var body = await Json(replay);
        Assert.Equal(2, body.GetProperty("bundleRevision").GetInt32());
        Assert.Equal(g[1].Preparation, body.GetProperty("currentPreparationId").GetGuid());
        Assert.Equal(first.GetProperty("promotion").GetProperty("id").GetGuid(), body.GetProperty("promotion").GetProperty("id").GetGuid());
        Assert.Equal(promotions, await Promotions(t));
        Assert.Equal((g[1].Preparation, g[1].EvidenceVersion, 2), await State(t));

        // Anche la preparazione che era già corrente prima di ogni promozione risponde 200 senza scrivere.
        var (u, h) = await CurrentWithCandidatesAsync(client);
        var current = await Promote(client, u, "1", Expect(1));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(1, (await Json(current)).GetProperty("bundleRevision").GetInt32());
        Assert.Equal(1L, await Promotions(u));
        Assert.Equal((h[0].Preparation, h[0].EvidenceVersion, 1), await State(u));
    }

    [Fact]
    public async Task A_stale_expected_revision_on_a_different_candidate_is_a_conflict_that_changes_nothing()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready, PreparationStatus.Ready);
        Assert.Equal(HttpStatusCode.OK, (await Promote(client, t, "2", Expect(1))).StatusCode);
        var promotions = await Promotions(t);

        var response = await Promote(client, t, "3", Expect(1));

        await AssertProblem(response, HttpStatusCode.Conflict, "bundle_revision_conflict");
        var body = await Json(response);
        Assert.Equal(2, body.GetProperty("currentRevision").GetInt32());
        Assert.Equal(g[1].Preparation, body.GetProperty("currentPreparationId").GetGuid());
        Assert.Equal((g[1].Preparation, g[1].EvidenceVersion, 2), await State(t));
        Assert.Equal(promotions, await Promotions(t));

        // Con la revisione letta correttamente la stessa candidata si promuove.
        Assert.Equal(HttpStatusCode.OK, (await Promote(client, t, "3", Expect(2))).StatusCode);
        Assert.Equal((g[2].Preparation, g[2].EvidenceVersion, 3), await State(t));
    }

    [Fact]
    public async Task Two_concurrent_promotions_of_different_candidates_have_exactly_one_winner()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready, PreparationStatus.Ready);

        var responses = await Task.WhenAll(Promote(client, t, "2", Expect(1)), Promote(client, t, "3", Expect(1)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var winner = (await State(t)).Preparation;
        Assert.Contains(winner, new Guid?[] { g[1].Preparation, g[2].Preparation });
        Assert.Equal(2, (await State(t)).Revision);
        Assert.Equal(2L, await Promotions(t));
        Assert.Equal(1L, await Count("SELECT count(*) FROM \"DocumentPromotions\" WHERE \"DocumentVersionId\" = @v AND \"PromotedBy\" IS NOT NULL", ("v", t.Version)));
        Assert.Equal(await Scalar<Guid>("SELECT \"ToPreparationId\" FROM \"DocumentPromotions\" WHERE \"DocumentVersionId\" = @v AND \"PromotedBy\" IS NOT NULL", ("v", t.Version)), winner);
    }

    [Fact]
    public async Task Concurrent_promotions_of_the_same_candidate_promote_once_and_replay_the_rest()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Promote(client, t, "2", Expect(1))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(2L, await Promotions(t));
        Assert.Equal((g[1].Preparation, g[1].EvidenceVersion, 2), await State(t));
        Assert.Single((await Task.WhenAll(responses.Select(Json))).Select(b => b.GetProperty("promotion").GetProperty("id").GetGuid()).Distinct());
    }

    [Theory]
    [InlineData(PreparationStatus.Queued)]
    [InlineData(PreparationStatus.Parsing)]
    [InlineData(PreparationStatus.Extracting)]
    [InlineData(PreparationStatus.Incomplete)]
    [InlineData(PreparationStatus.Failed)]
    public async Task A_preparation_that_is_not_ready_is_never_promoted(PreparationStatus status)
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, status);
        var promotions = await Promotions(t);

        await AssertProblem(await Promote(client, t, "2", Expect(1)), HttpStatusCode.UnprocessableEntity, "preparation_not_promotable");

        Assert.Equal((g[0].Preparation, g[0].EvidenceVersion, 1), await State(t));
        Assert.Equal(promotions, await Promotions(t));
    }

    [Fact]
    public async Task A_ready_preparation_with_an_open_completeness_issue_is_not_promotable()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
        using (var scope = App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PreparationIssues.Add(new PreparationIssue
            {
                Id = Guid.NewGuid(), ProjectId = t.Project, PreparationId = g[1].Preparation, Code = "image_unprocessed", Severity = IssueSeverity.Blocking,
                Reason = "Immagine non trattata", AffectsCompleteness = true,
            });
            await db.SaveChangesAsync(Ct);
        }

        await AssertProblem(await Promote(client, t, "2", Expect(1)), HttpStatusCode.UnprocessableEntity, "preparation_not_promotable");

        Assert.Equal((g[0].Preparation, g[0].EvidenceVersion, 1), await State(t));
    }

    [Fact]
    public async Task A_failed_candidate_does_not_cost_the_previous_current_bundle()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Failed);
        var promotions = await Promotions(t);

        var view = await Json(await client.GetAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1", Ct));

        Assert.Equal(g[0].Preparation, view.GetProperty("currentPreparationId").GetGuid());
        Assert.Equal(g[0].EvidenceVersion, view.GetProperty("currentEvidenceVersionId").GetGuid());
        Assert.Equal(1, view.GetProperty("bundleRevision").GetInt32());
        Assert.Empty(view.GetProperty("candidatePreparations").EnumerateArray());   // la fallita non è una candidata promovibile
        Assert.Equal((g[0].Preparation, g[0].EvidenceVersion, 1), await State(t));
        Assert.Equal(promotions, await Promotions(t));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"expectedRevision\":-1}")]
    [InlineData("{\"expectedRevision\":\"uno\"}")]
    [InlineData("{\"expectedRevision\":1.5}")]
    [InlineData("{\"expectedRevision\":null}")]
    [InlineData("[]")]
    [InlineData("non json")]
    public async Task A_missing_or_malformed_expected_revision_is_a_400_before_any_change(string body)
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
        var request = new HttpRequestMessage(HttpMethod.Post, PrepUrl(t, "/2/promote")) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((g[0].Preparation, g[0].EvidenceVersion, 1), await State(t));
    }

    [Fact]
    public async Task A_request_without_a_body_is_a_400()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);

        var response = await Promote(client, t, "2", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((g[0].Preparation, g[0].EvidenceVersion, 1), await State(t));
    }

    [Fact]
    public async Task Unknown_generations_other_projects_and_unauthorised_callers_cannot_promote()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
        var other = await NewTarget(client);

        Assert.Equal(HttpStatusCode.NotFound, (await Promote(client, t, "9", Expect(1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Promote(client, t, Guid.NewGuid().ToString(), Expect(1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Promote(client, t, "2", Expect(1), project: other.Project)).StatusCode);   // documento di un altro progetto
        Assert.Equal(HttpStatusCode.NotFound, (await Promote(client, t, g[1].EvidenceVersion!.Value.ToString(), Expect(1))).StatusCode);   // l'estratto non si promuove da solo
        Assert.Equal(HttpStatusCode.Unauthorized, (await Promote(App.Anonymous(), t, "2", Expect(1))).StatusCode);
        using var noRole = App.WithoutPmRole();
        Assert.Equal(HttpStatusCode.Forbidden, (await Promote(noRole, t, "2", Expect(1))).StatusCode);

        Assert.Equal((g[0].Preparation, g[0].EvidenceVersion, 1), await State(t));
    }

    [Fact]
    public async Task A_preparation_of_another_version_of_the_same_document_cannot_be_promoted_onto_this_one()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client);
        var second = await Json(await Upload(client, t.Project, Text("Seconda"), "nota.txt", "text/plain", documentId: t.Document));
        var v2 = second.GetProperty("id").GetGuid();
        var v2Prep = await Scalar<Guid>("SELECT \"Id\" FROM \"DocumentPreparations\" WHERE \"DocumentVersionId\" = @v", ("v", v2));
        var v2Target = t with { Version = v2, Preparation = v2Prep, Job = await Scalar<Guid>("SELECT \"ParseJobId\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", v2Prep)) };
        var other = await SeedGenerationAsync(v2Target, 1, PreparationStatus.Ready);

        // Per id di preparazione sotto la versione 1: appartiene alla 2, quindi non esiste sotto questa versione.
        Assert.Equal(HttpStatusCode.NotFound, (await Promote(client, t, other.Preparation.ToString(), Expect(1))).StatusCode);

        Assert.Equal((g[0].Preparation, g[0].EvidenceVersion, 1), await State(t));
        Assert.Equal(-1, (await State(v2Target)).Revision);   // nessuno stato creato per la versione 2
    }

    // ================================================================ lifecycle: current/candidate, LatestReady, esclusione

    [Fact]
    public async Task The_version_view_reports_the_current_bundle_and_the_ready_candidates_not_the_highest_ready_generation()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready, PreparationStatus.Ready, PreparationStatus.Failed);

        var view = await Json(await client.GetAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1", Ct));

        Assert.Equal(g[0].Preparation, view.GetProperty("currentPreparationId").GetGuid());   // g1, non la g3 Ready con generazione massima
        Assert.Equal(g[0].EvidenceVersion, view.GetProperty("currentEvidenceVersionId").GetGuid());
        Assert.Equal(1, view.GetProperty("bundleRevision").GetInt32());
        Assert.Equal(new[] { g[1].Preparation, g[2].Preparation }, view.GetProperty("candidatePreparations").EnumerateArray().Select(e => e.GetGuid()).ToArray());
        var detail = await Json(await client.GetAsync($"/api/projects/{t.Project}/documents/{t.Document}", Ct));
        Assert.Equal(g[0].Preparation, detail.GetProperty("versions")[0].GetProperty("currentPreparationId").GetGuid());
    }

    [Fact]
    public async Task A_ready_candidate_alone_never_makes_a_version_applicable_and_the_latest_ready_pointer_ignores_candidates()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        await SeedGenerationAsync(t, 1, PreparationStatus.Failed);
        await SeedGenerationAsync(t, 2, PreparationStatus.Ready);   // Ready, ma nessun bundle corrente: solo una candidata

        var view = await Json(await client.GetAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1", Ct));

        Assert.Equal(JsonValueKind.Null, view.GetProperty("currentPreparationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("currentEvidenceVersionId").ValueKind);
        Assert.False(view.GetProperty("isApplicable").GetBoolean());
        Assert.Equal(1L, await Count("SELECT count(*) FROM \"ProjectDocuments\" WHERE \"Id\" = @d AND \"LatestReadyVersionId\" IS NULL", ("d", t.Document)));
        var list = await Json(await client.GetAsync($"/api/projects/{t.Project}/documents", Ct));
        Assert.Equal(JsonValueKind.Null, list.GetProperty("items")[0].GetProperty("applicableVersionId").ValueKind);
    }

    [Fact]
    public async Task A_promotion_keeps_the_latest_ready_pointer_on_the_version_and_a_later_file_version_does_not_need_a_promotion()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
        Assert.Equal(HttpStatusCode.OK, (await Promote(client, t, "2", Expect(1))).StatusCode);

        // La promozione cambia il bundle della versione 1, non la versione file «ultima pronta» del documento.
        Assert.Equal(t.Version, await Scalar<Guid>("SELECT \"LatestReadyVersionId\" FROM \"ProjectDocuments\" WHERE \"Id\" = @d", ("d", t.Document)));

        // Nuova versione file v2 non ancora pronta: v1 resta la risolta e il suo bundle corrente non si muove.
        var second = await Json(await Upload(client, t.Project, Text("Seconda"), "nota.txt", "text/plain", documentId: t.Document));
        Assert.Equal(2, second.GetProperty("version").GetInt32());
        Assert.Equal(t.Version, await Scalar<Guid>("SELECT \"LatestReadyVersionId\" FROM \"ProjectDocuments\" WHERE \"Id\" = @d", ("d", t.Document)));
        Assert.Equal((g[1].Preparation, g[1].EvidenceVersion, 2), await State(t));
        var v1 = await Json(await client.GetAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1", Ct));
        Assert.Equal(g[1].Preparation, v1.GetProperty("currentPreparationId").GetGuid());
    }

    [Fact]
    public async Task An_exclusion_survives_new_versions_reprocessing_and_promotions()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
        var revision = (await Json(await client.GetAsync($"/api/projects/{t.Project}/source-selection", Ct))).GetProperty("revision").GetInt64();
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{t.Project}/source-selection")
        {
            Content = JsonContent.Create(new { items = new[] { new { documentId = t.Document, included = false, mode = "LatestReady", versionId = (Guid?)null } } }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(put, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Promote(client, t, "2", Expect(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostPreparations(client, t)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Upload(client, t.Project, Text("Terza"), "nota.txt", "text/plain", documentId: t.Document)).StatusCode);

        var selection = await Json(await client.GetAsync($"/api/projects/{t.Project}/source-selection", Ct));
        var item = selection.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("documentId").GetGuid() == t.Document);
        Assert.False(item.GetProperty("included").GetBoolean());
        Assert.Equal((g[1].Preparation, g[1].EvidenceVersion, 2), await State(t));   // l'esclusione non cancella storia né bundle
        Assert.Equal(3L, await Preparations(t));
    }
}

/// <summary>
/// T093 (worker reale, parser non raggiungibile): il servizio mancante lascia un errore iniziale tracciabile (job Failed con codice, preparazione
/// Failed) che non viene ritentato da nessuna lettura; la rielaborazione manuale crea una generazione collegata che fallisce a sua volta in modo
/// esplicito e limitato, senza toccare la prima.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentVersionLifecycleMissingServiceTests : ParseRetryTestBase
{
    protected override void ConfigureServices(IServiceCollection services) =>
        services.Configure<ParserOptions>(o => { o.BaseUrl = "http://127.0.0.1:9/"; o.Secret = "non-usato"; o.PollSeconds = 1; });

    [Fact]
    public async Task The_initial_error_of_a_missing_service_is_traceable_and_a_manual_reprocess_is_the_only_way_to_try_again()
    {
        using var client = await App.LoginAsync();
        var t = await NewTarget(client);
        await WaitFor("SELECT \"Status\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", t.Job, "Failed", TimeSpan.FromSeconds(40));
        var url = $"/api/projects/{t.Project}/documents/{t.Document}/versions/1/preparations";

        // Errore iniziale: stato, codice e tentativi sono sul job e sulla preparazione, e la lettura lo mostra senza ritentare.
        Assert.Equal("attempts_exhausted", await Scalar<string>("SELECT \"ErrorCode\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", t.Job)));
        Assert.Equal(3, await Scalar<int>("SELECT \"AttemptCount\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", t.Job)));
        var view = await Json(await client.GetAsync(url + "/1", Ct));
        Assert.Equal("Failed", view.GetProperty("processingStatus").GetString());
        Assert.Equal("attempts_exhausted", view.GetProperty("issuesSummary").GetProperty("code").GetString());
        await Task.Delay(3_000, Ct);
        Assert.Equal(1L, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"ProjectId\" = @p", ("p", t.Project)));
        Assert.Equal(3, await Scalar<int>("SELECT \"AttemptCount\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", t.Job)));   // nessun ritentativo spontaneo
        var old = (await RowJson("DocumentJobs", t.Job), await RowJson("DocumentPreparations", t.Preparation));

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await Json(response);
        var job = body.GetProperty("jobId").GetGuid();
        await WaitFor("SELECT \"Status\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", job, "Failed", TimeSpan.FromSeconds(40));
        Assert.Equal(3, await Scalar<int>("SELECT \"AttemptCount\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", job)));
        Assert.Equal("attempts_exhausted", await Scalar<string>("SELECT \"ErrorCode\" FROM \"DocumentJobs\" WHERE \"Id\" = @j", ("j", job)));
        Assert.Equal(old.Item1, await RowJson("DocumentJobs", t.Job));
        Assert.Equal(old.Item2, await RowJson("DocumentPreparations", t.Preparation));
        Assert.Equal(0L, await Count("SELECT count(*) FROM \"EvidenceDocumentStates\""));   // nessun bundle corrente da un parsing mancato
        Assert.Equal(0L, await Count("SELECT count(*) FROM \"ParsedArtifacts\""));
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
