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
public sealed class DocumentVersionLifecycleTests : LifecycleTestBase
{

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
    public async Task A_key_already_used_for_another_version_is_an_idempotency_conflict()
    {
        using var client = await App.LoginAsync();
        var (t, _) = await CurrentWithCandidatesAsync(client);
        var other = await NewTarget(client, t.Project, "altro.txt");
        await SeedGenerationAsync(other, 1, PreparationStatus.Ready);
        var key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await PostPreparations(client, t, key)).StatusCode);
        var jobs = await Jobs(t);

        await AssertProblem(await PostPreparations(client, t, key, document: other.Document), HttpStatusCode.Conflict, "idempotency_conflict");   // altra versione, stessa chiave

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

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("bundle_revision_conflict", body.GetProperty("code").GetString());
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

    [Fact]
    public async Task A_reason_over_the_limit_is_rejected_and_a_superseded_bundle_can_be_promoted_back_without_becoming_a_candidate_again()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Promote(client, t, "2", Expect(1, new string('x', 1_001)))).StatusCode);
        Assert.Equal((g[0].Preparation, g[0].EvidenceVersion, 1), await State(t));

        Assert.Equal(HttpStatusCode.OK, (await Promote(client, t, "2", Expect(1, new string('x', 1_000)))).StatusCode);
        var view = await Json(await client.GetAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1", Ct));
        Assert.Empty(view.GetProperty("candidatePreparations").EnumerateArray());   // g1 è Superseded: storico, non candidata

        Assert.Equal(HttpStatusCode.OK, (await Promote(client, t, "1", Expect(2, "ripristino"))).StatusCode);   // ritorno esplicito al bundle precedente
        Assert.Equal((g[0].Preparation, g[0].EvidenceVersion, 3), await State(t));
        Assert.Equal(3L, await Promotions(t));
        Assert.Equal(1L, await Count("SELECT count(*) FROM \"EvidenceExtractionVersions\" WHERE \"Id\" = @e", ("e", g[1].EvidenceVersion!.Value)));   // nulla di storico cancellato
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

    // ================================================================ T096: risoluzione della selezione sul bundle corrente

    [Fact]
    public async Task Selection_and_snapshot_resolve_the_current_bundle_never_the_highest_ready_generation()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);   // g2 Ready con generazione maggiore, ma solo candidata

        var item = await SelectionItemAsync(client, t);
        Assert.Equal("Ready", item.GetProperty("availability").GetString());
        Assert.Equal(g[0].Preparation, item.GetProperty("preparationId").GetGuid());

        // Lo snapshot fissa il bundle corrente (preparazione + versione Evidence), non la candidata.
        using (var scope = App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var evaluation = await scope.ServiceProvider.GetRequiredService<SourceSelectionService>().EvaluateAsync(t.Project, Ct);
            var snapshot = scope.ServiceProvider.GetRequiredService<SourceSnapshotStore>().Add(t.Project, evaluation, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
            var row = await db.SourceSnapshotItems.AsNoTracking().SingleAsync(i => i.SnapshotId == snapshot.Id, Ct);
            Assert.Equal(g[0].Preparation, row.PreparationId);
            Assert.Equal(g[0].EvidenceVersion, row.EvidenceVersionId);
        }

        // Dopo la promozione esplicita la risoluzione segue il nuovo bundle corrente.
        Assert.Equal(HttpStatusCode.OK, (await Promote(client, t, "2", Expect(1))).StatusCode);
        Assert.Equal(g[1].Preparation, (await SelectionItemAsync(client, t)).GetProperty("preparationId").GetGuid());
    }

    [Fact]
    public async Task A_pin_on_a_file_version_without_a_current_bundle_blocks_while_the_default_keeps_the_previous_ready_version()
    {
        using var client = await App.LoginAsync();
        var (t, _) = await CurrentWithCandidatesAsync(client);
        var v2 = await UploadNextVersionAsync(client, t, "Seconda");
        await SeedGenerationAsync(v2, 1, PreparationStatus.Ready);   // Ready ma nessun bundle corrente: non applicabile

        var defaultItem = await SelectionItemAsync(client, t);
        Assert.Equal("Ready", defaultItem.GetProperty("availability").GetString());
        Assert.Equal(t.Version, defaultItem.GetProperty("resolvedVersionId").GetGuid());   // v2 incompleta non rimuove v1 di default

        Assert.Equal(HttpStatusCode.OK, (await PutSelectionAsync(client, t, new { documentId = t.Document, included = true, mode = "Pinned", versionId = v2.Version })).StatusCode);
        var pinned = await Json(await client.GetAsync($"/api/projects/{t.Project}/source-selection", Ct));
        var item = pinned.GetProperty("items")[0];
        Assert.Equal("Unavailable", item.GetProperty("availability").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("preparationId").ValueKind);
        Assert.Equal(t.Document, pinned.GetProperty("blockingSources")[0].GetProperty("documentId").GetGuid());
    }

    [Fact]
    public async Task A_pin_on_an_older_file_version_uses_its_own_current_bundle_and_exclusion_persists_on_a_newer_version()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await PutSelectionAsync(client, t, new { documentId = t.Document, included = true, mode = "Pinned", versionId = t.Version })).StatusCode);
        var v2 = await UploadNextVersionAsync(client, t, "Seconda");
        var second = await SeedGenerationAsync(v2, 1, PreparationStatus.Ready);
        await MakeCurrentAsync(v2, second);   // v2 diventa l'ultima pronta, ma il PM ha fissato v1

        var item = await SelectionItemAsync(client, t);
        Assert.Equal(t.Version, item.GetProperty("resolvedVersionId").GetGuid());
        Assert.Equal(g[0].Preparation, item.GetProperty("preparationId").GetGuid());

        Assert.Equal(HttpStatusCode.OK, (await PutSelectionAsync(client, t, new { documentId = t.Document, included = false, mode = "LatestReady", versionId = (Guid?)null })).StatusCode);
        await UploadNextVersionAsync(client, t, "Terza");
        Assert.False((await SelectionItemAsync(client, t)).GetProperty("included").GetBoolean());
    }

    // ================================================================ T097: consultazione e confronto storico

    private const string ViewQuery = "?viewVersion=1.1.0";

    [Fact]
    public async Task A_superseded_bundle_original_and_evidence_stay_consultable_after_a_new_file_version_and_a_promotion()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
        var original = await client.GetByteArrayAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1/download", Ct);
        var v2 = await UploadNextVersionAsync(client, t, "Seconda versione diversa");
        var secondReady = await SeedGenerationAsync(v2, 1, PreparationStatus.Ready);
        await MakeCurrentAsync(v2, secondReady);
        Assert.Equal(HttpStatusCode.OK, (await Promote(client, t, "2", Expect(1))).StatusCode);   // g1 diventa Superseded

        // L'originale della v1 non cambia dopo v2/g2/promozione e il download della v2 è un altro file.
        Assert.Equal(original, await client.GetByteArrayAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1/download", Ct));
        Assert.NotEqual(original, await client.GetByteArrayAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/2/download", Ct));

        // Il bundle sostituito è ancora risolto per id (citazioni storiche) e dichiarato non corrente; il corrente è g2.
        var superseded = await Json(await client.GetAsync($"/api/projects/{t.Project}/evidence/versions/{g[0].EvidenceVersion}{ViewQuery}", Ct));
        Assert.Equal(g[0].EvidenceVersion, superseded.GetProperty("version").GetProperty("id").GetGuid());
        Assert.False(superseded.GetProperty("version").GetProperty("isCurrent").GetBoolean());
        Assert.Equal(t.Version, superseded.GetProperty("documentVersionId").GetGuid());
        var versions = await Json(await client.GetAsync($"/api/projects/{t.Project}/documents/{t.Document}/versions/1/evidence/versions{ViewQuery}", Ct));
        Assert.Equal(g[1].EvidenceVersion, versions.GetProperty("currentEvidenceVersionId").GetGuid());
        Assert.Equal(2, versions.GetProperty("versions").GetArrayLength());

    }

    [Fact]
    public async Task Evidence_versions_compare_only_on_the_same_original_and_parse_never_across_files_by_words_or_filename()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
        // Altro file con lo stesso nome, lo stesso contenuto e lo stesso manifest: mai fuso per parole o filename.
        var twin = await NewTarget(client, t.Project, "nota.txt");
        var twinGeneration = await SeedGenerationAsync(twin, 1, PreparationStatus.Ready);
        var v2 = await UploadNextVersionAsync(client, t, "Contenuto");
        var secondReady = await SeedGenerationAsync(v2, 1, PreparationStatus.Ready);
        await Execute("UPDATE \"EvidenceExtractionVersions\" SET \"ManifestHash\" = @h", ("h", new string('7', 64)));   // stesso manifest ovunque
        string Compare(Guid a, Guid b) => $"/api/projects/{t.Project}/evidence/versions/{a}/compare/{b}{ViewQuery}";

        // Stessa versione file e stesso manifest (stesso originale/parse): confrontabili.
        var same = await client.GetAsync(Compare(g[0].EvidenceVersion!.Value, g[1].EvidenceVersion!.Value), Ct);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(t.Version, (await Json(same)).GetProperty("documentVersionId").GetGuid());

        // Altro file (stesso nome/contenuto) o altra versione file: 422, anche con manifest identico.
        await AssertProblem(await client.GetAsync(Compare(g[0].EvidenceVersion!.Value, twinGeneration.EvidenceVersion!.Value), Ct), (HttpStatusCode)422, "versions_not_comparable");
        await AssertProblem(await client.GetAsync(Compare(g[0].EvidenceVersion!.Value, secondReady.EvidenceVersion!.Value), Ct), (HttpStatusCode)422, "versions_not_comparable");

        // Manifest strutturale diverso sulla stessa versione file: non verificabile.
        await Execute("UPDATE \"EvidenceExtractionVersions\" SET \"ManifestHash\" = @h WHERE \"Id\" = @e", ("h", new string('8', 64)), ("e", g[1].EvidenceVersion!.Value));
        await AssertProblem(await client.GetAsync(Compare(g[0].EvidenceVersion!.Value, g[1].EvidenceVersion!.Value), Ct), (HttpStatusCode)422, "versions_not_comparable");
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
