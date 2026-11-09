using System.Text;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T054 (PG) — completamento di una preparazione documentale: dopo il commit del parse nascono le unità e UN solo aggregatore Queued non eleggibile
/// finché le unità non sono valide; l'aggregatore pubblica versione Evidence, Completed, Ready e primo bundle corrente in una transazione sola,
/// senza tenere slot AI. Parser e modello sono finti (nessun DGX); l'agente, il dispatcher, il lease store e l'aggregatore sono quelli reali.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentPreparationCompletionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Words(int count, string prefix) => string.Join(' ', Enumerable.Range(0, count).Select(i => $"{prefix}{i}"));

    private sealed class Events : IDocumentPreparationEvents
    {
        public List<(Guid Preparation, PreparationStatus Status)> Published { get; } = [];

        public Task PublishStateAsync(Guid projectId, Guid preparationId, PreparationStatus status, CancellationToken ct)
        {
            lock (Published)
            {
                Published.Add((preparationId, status));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Env : IAsyncDisposable
    {
        private readonly ExecutionOptions _execution = new();
        private ServiceProvider _services = null!;

        public ParseRig Parse { get; private set; } = null!;
        public ParsingDatabase Db => Parse.Db;
        public Events Notifications { get; } = new();

        public static async Task<Env> CreateAsync(bool aggregate)
        {
            TestEnvironment.RequirePostgres();
            var env = new Env { Parse = await ParseRig.CreateAsync() };
            var services = new ServiceCollection().AddLogging().AddDbContext<AppDbContext>(o => o.UseNpgsql(env.Db.ConnectionString))
                .AddSingleton<IOptions<ExecutionOptions>>(Options.Create(env._execution)).AddScoped<ExecutionLeaseStore>().AddSingleton<IDocumentPreparationEvents>(env.Notifications);
            if (aggregate)
            {
                services.AddScoped<DocumentBundleStore>().AddScoped<DocumentAggregationRunner>();
            }

            env._services = services.BuildServiceProvider();
            return env;
        }

        public async Task<SeededParse> ParseAsync(params string[] paragraphs)
        {
            var seed = await Db.SeedParseAsync(Parse.Store, Encoding.UTF8.GetBytes(string.Join("\n\n", paragraphs)));
            await ParseSeededAsync(seed, paragraphs);
            return seed;
        }

        public async Task ParseSeededAsync(SeededParse seed, string[] paragraphs, string status = "Complete")
        {
            var manifest = Manifest(seed, paragraphs, status);
            Parse.OnConvert = (_, _) => Task.FromResult(ParseRig.Ok(manifest));
            var outcome = await Parse.RunAsync();
            Assert.Equal(status == "Complete" ? ParseRunOutcome.Published : ParseRunOutcome.PublishedIncomplete, outcome);
        }

        public AppDbContext NewContext() => Db.NewContext();

        public DocumentBundleStore Bundles(AppDbContext context) => new(context, Options.Create(_execution));

        public async Task<AggregationClaim> ClaimAsync(string owner = "agg-1")
        {
            await using var context = NewContext();
            return (await Bundles(context).ClaimNextAsync(owner, Ct))!;
        }

        public async Task<AggregationResult> PublishAsync(AggregationClaim claim)
        {
            await using var context = NewContext();
            return await Bundles(context).PublishAsync(claim, Ct);
        }

        /// <summary>Un ciclo del dispatcher esistente con l'agente reale e il modello finto (con l'aggregatore solo se registrato).</summary>
        public async Task<bool> DispatchOnceAsync(DocumentEvidenceAgentTests.FakeUnitModel model)
        {
            var options = new AiOptions
            {
                Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = DocumentEvidenceAgentTests.FakeUnitModel.Window,
                MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
            };
            using var client = new InternalModelClient(options, model);
            var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
            var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
            var scopes = _services.GetRequiredService<IServiceScopeFactory>();
            var agent = new DocumentEvidenceAgent(scopes, factory, client, Options.Create(new DocumentOptions()));
            var dispatcher = new AiDispatchService(scopes, Options.Create(_execution), NullLogger<AiDispatchService>.Instance, unitExecutor: agent);
            return await dispatcher.RunOnceAsync(Ct);
        }

        public async Task RunUntilIdleAsync(DocumentEvidenceAgentTests.FakeUnitModel model, int max = 60)
        {
            for (var i = 0; i < max; i++)
            {
                if (!await DispatchOnceAsync(model))
                {
                    return;
                }
            }

            Assert.Fail("Il dispatcher non è andato a riposo.");
        }

        public Task<long> Count(string table, string where = "TRUE") => Db.ScalarAsync<long>($"""SELECT count(*) FROM "{table}" WHERE {where}""");

        public Task<string> Str(string sql) => Db.ScalarAsync<string>(sql);

        public Task<string> PrepStatus(Guid preparation) => Str($"""SELECT "Status" FROM "DocumentPreparations" WHERE "Id" = '{preparation}'""");

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await Parse.DisposeAsync();
        }
    }

    private static string Manifest(SeededParse seed, string[] paragraphs, string status = "Complete")
    {
        JsonObject Block(int i) => new()
        {
            ["ref"] = $"b{i}", ["order"] = i, ["parentRef"] = null, ["type"] = "paragraph", ["text"] = paragraphs[i],
            ["headingPath"] = new JsonArray(), ["locator"] = new JsonObject { ["lineStart"] = 1 + 2 * i, ["lineEnd"] = 1 + 2 * i },
        };
        JsonObject Range(int i) => new()
        {
            ["rangeKey"] = $"r{i}", ["blockRef"] = $"b{i}", ["start"] = 0, ["end"] = paragraphs[i].Length, ["ownership"] = "Primary", ["sourceTextSha256"] = ParseRig.Sha(paragraphs[i]),
        };
        var indexes = Enumerable.Range(0, paragraphs.Length).ToArray();
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "document-parse-1", ["originalSha256"] = seed.Sha256, ["parserVersion"] = "1.0.0", ["dependencyLockHash"] = "lock",
            ["modelArtifactsHash"] = "model", ["configHash"] = "cfg-hash-1", ["status"] = status, ["originFormat"] = "txt",
            ["originalByteLength"] = seed.ByteLength, ["pageCount"] = null, ["offsetConvention"] = "unicode-codepoint",
            ["document"] = new JsonObject { ["serialization"] = "text-1", ["content"] = Encoding.UTF8.GetString(seed.Content) },
            ["blocks"] = new JsonArray([.. indexes.Select(i => (JsonNode)Block(i))]),
            ["inventory"] = new JsonArray(new JsonObject { ["kind"] = "paragraph", ["state"] = "represented", ["blockRefs"] = new JsonArray([.. indexes.Select(i => (JsonNode)JsonValue.Create($"b{i}")!)]), ["issueRefs"] = new JsonArray() }),
            ["chunks"] = new JsonArray([.. indexes.Select(i => (JsonNode)new JsonObject { ["ranges"] = new JsonArray(Range(i)) })]),
            ["issues"] = new JsonArray(),
        };
        if (status == "Incomplete")
        {
            manifest["issues"] = new JsonArray(new JsonObject { ["issueRef"] = "i1", ["code"] = "image_unprocessed", ["severity"] = "blocking", ["blockRef"] = "b0", ["reason"] = "Immagine non trattata", ["affectsCompleteness"] = true });
            manifest["inventory"]!.AsArray().Add(new JsonObject { ["kind"] = "item", ["state"] = "unprocessed", ["blockRefs"] = new JsonArray(), ["issueRefs"] = new JsonArray("i1") });
        }

        return manifest.ToJsonString();
    }

    /// <summary>24 paragrafi da circa 340 caratteri: oltre il tetto di testo per unità (6000), quindi due unità, ciascuna con più batch.</summary>
    private static string[] TwoUnits() => [.. Enumerable.Range(0, 24).Select(i => Words(70, $"p{i}x"))];

    // ------------------------------------------------------------------ GAP: parse → unità → aggregatore

    [Fact]
    public async Task The_parse_commit_creates_the_units_assigns_every_portion_and_one_queued_aggregator_that_is_not_eligible_while_units_are_pending()
    {
        await using var env = await Env.CreateAsync(aggregate: true);
        var seed = await env.ParseAsync(TwoUnits());
        var prep = seed.Preparation;

        Assert.Equal("Extracting", await env.PrepStatus(prep));
        Assert.Equal(2L, await env.Count("DocumentJobs", $"\"PreparationId\" = '{prep}' AND \"Kind\" = 'ExtractUnit' AND \"Status\" = 'Queued' AND \"MaxAttempts\" = 3"));
        Assert.Equal(24L, await env.Count("DocumentPortions", $"\"PreparationId\" = '{prep}' AND \"JobId\" IS NOT NULL AND \"Status\" = 'Pending'"));
        Assert.Equal(2L, await env.Db.ScalarAsync<long>($"""SELECT count(*) FROM "DocumentJobs" j WHERE j."PreparationId" = '{prep}' AND j."Kind" = 'ExtractUnit' AND EXISTS (SELECT 1 FROM "DocumentPortions" p WHERE p."JobId" = j."Id" AND p."Id" = j."UnitId" AND p."Ordinal" = (SELECT min(q."Ordinal") FROM "DocumentPortions" q WHERE q."JobId" = j."Id"))"""));
        Assert.Equal(2L, await env.Count("DocumentJobs", $"\"PreparationId\" = '{prep}' AND \"Kind\" = 'ExtractUnit' AND \"ConfigJson\"::text ILIKE '%MaxAiCallsPerPreparation%' AND \"DeadlineAt\" = (SELECT \"HardDeadlineAt\" FROM \"DocumentPreparations\" WHERE \"Id\" = '{prep}') AND \"WorkflowDefinitionVersion\" IS NOT NULL"));

        // Un solo aggregatore Queued, senza lease né fence, e il suo claim non è possibile.
        Assert.Equal(1L, await env.Count("EvidenceExtractionJobs", $"\"PreparationId\" = '{prep}' AND \"OriginKind\" = 'Document' AND \"Status\" = 'Queued' AND \"LeaseOwner\" IS NULL AND \"FencingToken\" = 0 AND \"AttemptCount\" = 0"));
        await using (var context = env.NewContext())
        {
            Assert.Null(await env.Bundles(context).ClaimNextAsync("agg", Ct));
        }

        // Le unità sono compiute una alla volta: finché ne manca una (o una porzione), l'aggregatore non è eleggibile.
        var units = (await env.Db.ScalarAsync<string>($"""SELECT string_agg("Id"::text, ',' ORDER BY "RequestedAt", "Id") FROM "DocumentJobs" WHERE "PreparationId" = '{prep}' AND "Kind" = 'ExtractUnit'""")).Split(',');
        await env.Db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "Status" = 'Completed', "ResultHash" = '{new string('1', 64)}' WHERE "Id" = '{units[0]}'""");
        await env.Db.ExecuteAsync($"""UPDATE "DocumentPortions" SET "Status" = 'Completed', "ResultHash" = '{new string('1', 64)}' WHERE "JobId" = '{units[0]}'""");
        await using (var context = env.NewContext())
        {
            Assert.Null(await env.Bundles(context).ClaimNextAsync("agg", Ct));
        }

        await env.Db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "Status" = 'Completed', "ResultHash" = '{new string('1', 64)}' WHERE "Id" = '{units[1]}'""");
        await using (var context = env.NewContext())
        {
            Assert.Null(await env.Bundles(context).ClaimNextAsync("agg", Ct));   // unità Completed ma porzione ancora Pending: ancora non eleggibile
        }

        await env.Db.ExecuteAsync($"""UPDATE "DocumentPortions" SET "Status" = 'Completed', "ResultHash" = '{new string('1', 64)}' WHERE "JobId" = '{units[1]}'""");
        var claim = await env.ClaimAsync();
        Assert.NotNull(claim);
        Assert.Equal(prep, claim.PreparationId);
        Assert.Equal(1L, claim.FencingToken);
    }

    [Fact]
    public async Task A_second_aggregator_for_the_same_preparation_is_refused_and_a_replayed_parse_creates_no_duplicates()
    {
        await using var env = await Env.CreateAsync(aggregate: true);
        var seed = await env.ParseAsync(TwoUnits());

        await Assert.ThrowsAnyAsync<Exception>(() => env.Db.ExecuteAsync($"""
            INSERT INTO "EvidenceExtractionJobs" ("Id","ProjectId","OriginKind","PreparationId","Reason","Status","RequestedAt","DeadlineAt","MaxAttempts","AttemptCount","FencingToken","ExtractorConfig")
            VALUES (gen_random_uuid(),'{seed.Project}','Document','{seed.Preparation}','Initial','Queued',now(),now() + interval '1 hour',3,0,0,jsonb_build_object('kind','dup'))
            """));

        Assert.Null(await env.Parse.RunAsync());   // il Parse è Completed: nulla da rifare
        Assert.Equal(1L, await env.Count("EvidenceExtractionJobs", $"\"PreparationId\" = '{seed.Preparation}'"));
        Assert.Equal(2L, await env.Count("DocumentJobs", $"\"PreparationId\" = '{seed.Preparation}' AND \"Kind\" = 'ExtractUnit'"));
    }

    [Fact]
    public async Task An_incomplete_manifest_creates_neither_units_nor_aggregator()
    {
        await using var env = await Env.CreateAsync(aggregate: true);
        var paragraphs = TwoUnits();
        var seed = await env.Db.SeedParseAsync(env.Parse.Store, Encoding.UTF8.GetBytes(string.Join("\n\n", paragraphs)));
        await env.ParseSeededAsync(seed, paragraphs, status: "Incomplete");

        Assert.Equal("Incomplete", await env.PrepStatus(seed.Preparation));
        Assert.Equal(0L, await env.Count("DocumentJobs", $"\"PreparationId\" = '{seed.Preparation}' AND \"Kind\" = 'ExtractUnit'"));
        Assert.Equal(0L, await env.Count("EvidenceExtractionJobs"));
        Assert.Equal(0L, await env.Count("DocumentPortions", "\"JobId\" IS NOT NULL"));
    }

    // ------------------------------------------------------------------ pubblicazione

    [Fact]
    public async Task Units_then_the_aggregator_publish_one_version_make_the_preparation_Ready_and_the_first_current_bundle_and_a_replay_returns_the_same_version()
    {
        await using var env = await Env.CreateAsync(aggregate: false);
        var seed = await env.ParseAsync(TwoUnits());
        var model = new DocumentEvidenceAgentTests.FakeUnitModel();

        await env.RunUntilIdleAsync(model);

        Assert.Equal(0L, await env.Count("DocumentJobs", "\"Kind\" = 'ExtractUnit' AND \"Status\" <> 'Completed'"));
        Assert.Equal("Extracting", await env.PrepStatus(seed.Preparation));   // le unità non rendono mai Ready: lo fa solo l'aggregatore
        Assert.Equal(0L, await env.Count("EvidenceExtractionVersions"));
        var claim = await env.ClaimAsync();
        Assert.Equal(1L, await env.Count("EvidenceExtractionJobs", "\"Status\" = 'Running'"));

        var result = await env.PublishAsync(claim);

        Assert.Equal(AggregationOutcome.Published, result.Outcome);
        Assert.True(result.BecameCurrent);
        Assert.Equal(PreparationStatus.Ready, result.PreparationStatus);
        Assert.Equal("Ready", await env.PrepStatus(seed.Preparation));
        Assert.Equal(1L, await env.Count("EvidenceExtractionVersions"));
        var evidences = await env.Count("Evidences");
        Assert.True(evidences >= 2);
        Assert.Equal(evidences, await env.Db.ScalarAsync<long>("""SELECT "EvidenceCount" FROM "EvidenceExtractionVersions" """));
        Assert.Equal(evidences, await env.Count("Evidences", $"\"OriginKind\" = 'Document' AND \"PreparationId\" = '{seed.Preparation}' AND \"VersionId\" = '{result.EvidenceVersionId}'"));
        Assert.Equal(1, await env.Db.ScalarAsync<int>("""SELECT "Version" FROM "EvidenceExtractionVersions" """));
        Assert.Equal("Completed", await env.Str($"""SELECT "Status" FROM "EvidenceExtractionJobs" WHERE "Id" = '{claim.JobId}'"""));
        Assert.Equal(result.EvidenceVersionId.ToString(), await env.Str($"""SELECT "ResultVersionId"::text FROM "EvidenceExtractionJobs" WHERE "Id" = '{claim.JobId}'"""));
        Assert.Equal(result.EvidenceVersionId.ToString(), await env.Str($"""SELECT "CurrentEvidenceVersionId"::text FROM "DocumentPreparations" WHERE "Id" = '{seed.Preparation}'"""));
        Assert.Equal(1L, await env.Count("EvidenceDocumentStates", $"\"DocumentVersionId\" = '{seed.Version}' AND \"CurrentPreparationId\" = '{seed.Preparation}' AND \"CurrentEvidenceVersionId\" = '{result.EvidenceVersionId}' AND \"Revision\" = 1"));
        Assert.Equal(1L, await env.Count("DocumentPromotions", $"\"DocumentVersionId\" = '{seed.Version}' AND \"ToEvidenceVersionId\" = '{result.EvidenceVersionId}' AND \"FromEvidenceVersionId\" IS NULL"));
        Assert.Equal(seed.Version.ToString(), await env.Str($"""SELECT "LatestReadyVersionId"::text FROM "ProjectDocuments" WHERE "Id" = '{seed.Document}'"""));
        Assert.Equal(0L, await env.Count("EvidenceExtractionJobs", "\"LeaseOwner\" IS NOT NULL"));   // nessun lease tenuto a lavoro finito

        // Replay: stesso risultato, nessuna scrittura, nessun evento definitivo da un owner non autorevole.
        var replay = await env.PublishAsync(claim);
        Assert.Equal(AggregationOutcome.AlreadyPublished, replay.Outcome);
        Assert.Equal(result.EvidenceVersionId, replay.EvidenceVersionId);
        Assert.Equal(1L, await env.Count("EvidenceExtractionVersions"));
        Assert.Equal(evidences, await env.Count("Evidences"));
        Assert.Equal(1L, await env.Count("DocumentPromotions"));
        await using var context = env.NewContext();
        Assert.Null(await env.Bundles(context).ClaimNextAsync("agg-2", Ct));   // niente da rifare
    }

    [Fact]
    public async Task Zero_evidences_with_a_reason_for_every_portion_is_a_Ready_noEvidence_version_distinct_from_a_loss()
    {
        await using var env = await Env.CreateAsync(aggregate: false);
        var seed = await env.ParseAsync(TwoUnits());
        var model = new DocumentEvidenceAgentTests.FakeUnitModel { Responder = _ => """{"schemaVersion":"evidence-1.1.0","evidences":[],"noEvidenceReason":"Solo numerazione di pagina."}""" };

        await env.RunUntilIdleAsync(model);
        var result = await env.PublishAsync(await env.ClaimAsync());

        Assert.Equal(AggregationOutcome.Published, result.Outcome);
        Assert.Equal("Ready", await env.PrepStatus(seed.Preparation));
        Assert.Equal(0L, await env.Count("Evidences"));
        Assert.Equal(0, await env.Db.ScalarAsync<int>("""SELECT "EvidenceCount" FROM "EvidenceExtractionVersions" """));
        Assert.Equal("Solo numerazione di pagina.", await env.Str("""SELECT "NoEvidenceReason" FROM "EvidenceExtractionVersions" """));
    }

    [Fact]
    public async Task The_last_unit_failing_prevents_Ready_closes_the_aggregator_and_leaves_no_version_or_current_bundle()
    {
        await using var env = await Env.CreateAsync(aggregate: true);
        var seed = await env.ParseAsync(TwoUnits());
        // La prima unità (ordine di richiesta) è valida; sulla seconda il modello restituisce un output non valido: l'ultima unità fallisce in modo permanente.
        await env.Db.ExecuteAsync($"""
            UPDATE "DocumentJobs" j SET "RequestedAt" = now() - interval '1 minute'
            WHERE j."PreparationId" = '{seed.Preparation}' AND j."Kind" = 'ExtractUnit'
              AND EXISTS (SELECT 1 FROM "DocumentPortions" p WHERE p."Id" = j."UnitId" AND p."Ordinal" = 0)
            """);
        var model = new DocumentEvidenceAgentTests.FakeUnitModel
        {
            Responder = r => !r.UserText.Contains("p20x0 ", StringComparison.Ordinal)
                ? null!
                : """{"schemaVersion":"evidence-1.1.0","evidences":[],"noEvidenceReason":null}""",
        };

        await env.RunUntilIdleAsync(model);

        Assert.Equal("Failed", await env.PrepStatus(seed.Preparation));
        Assert.Equal(1L, await env.Count("DocumentJobs", "\"Kind\" = 'ExtractUnit' AND \"Status\" = 'Completed'"));
        Assert.Equal(1L, await env.Count("DocumentJobs", "\"Kind\" = 'ExtractUnit' AND \"Status\" = 'Failed'"));
        Assert.Equal("Failed", await env.Str("""SELECT "Status" FROM "EvidenceExtractionJobs" """));
        Assert.Equal("parent_failed", await env.Str("""SELECT "ErrorCode" FROM "EvidenceExtractionJobs" """));
        Assert.Equal(0L, await env.Count("EvidenceExtractionVersions"));
        Assert.Equal(0L, await env.Count("EvidenceDocumentStates"));
        Assert.Empty(env.Notifications.Published);   // nessuna notifica di Ready
        await using var context = env.NewContext();
        Assert.Null(await env.Bundles(context).ClaimNextAsync("agg", Ct));
    }

    [Theory]
    [InlineData("payload", "Failed", "work_output_invalid")]
    [InlineData("missing-output", "Incomplete", "coverage_incomplete")]
    public async Task An_aggregate_that_fails_integrity_or_coverage_closes_the_aggregator_and_the_preparation_with_the_stable_code_and_no_version(
        string tamper, string expectedStatus, string expectedCode)
    {
        await using var env = await Env.CreateAsync(aggregate: false);
        var seed = await env.ParseAsync(TwoUnits());
        await env.RunUntilIdleAsync(new DocumentEvidenceAgentTests.FakeUnitModel());
        if (tamper == "payload")
        {
            await env.Db.ExecuteAsync("""SET session_replication_role = replica; UPDATE "WorkOutputs" SET "PayloadJson" = '{"schemaVersion":"evidence-1.1.0","unitOrdinal":0,"evidences":[],"relations":[]}'::jsonb WHERE ctid = (SELECT ctid FROM "WorkOutputs" LIMIT 1); SET session_replication_role = DEFAULT""");
        }
        else
        {
            await env.Db.ExecuteAsync("""SET session_replication_role = replica; DELETE FROM "WorkOutputs" WHERE ctid = (SELECT ctid FROM "WorkOutputs" LIMIT 1); SET session_replication_role = DEFAULT""");
        }

        var claim = await env.ClaimAsync();
        var result = await env.PublishAsync(claim);

        Assert.Equal(AggregationOutcome.Failed, result.Outcome);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Equal(expectedStatus, await env.PrepStatus(seed.Preparation));
        Assert.Equal("Failed", await env.Str($"""SELECT "Status" FROM "EvidenceExtractionJobs" WHERE "Id" = '{claim.JobId}'"""));
        Assert.Equal(0L, await env.Count("EvidenceExtractionVersions"));
        Assert.Equal(0L, await env.Count("Evidences"));
        Assert.Equal(0L, await env.Count("EvidenceDocumentStates"));
    }

    // ------------------------------------------------------------------ concorrenza, fence, eventi

    [Fact]
    public async Task Two_simultaneous_publishes_yield_a_single_version_and_a_single_winner()
    {
        await using var env = await Env.CreateAsync(aggregate: false);
        await env.ParseAsync(TwoUnits());
        await env.RunUntilIdleAsync(new DocumentEvidenceAgentTests.FakeUnitModel());
        var claim = await env.ClaimAsync();
        var barrier = new Barrier(2);

        Task<AggregationResult> Racer() => Task.Run(() =>
        {
            barrier.SignalAndWait(Ct);
            return env.PublishAsync(claim);
        }, Ct);

        var results = await Task.WhenAll(Racer(), Racer());

        Assert.Single(results, r => r.Outcome == AggregationOutcome.Published);
        Assert.Single(results, r => r.Outcome == AggregationOutcome.AlreadyPublished);
        Assert.Equal(results[0].EvidenceVersionId, results[1].EvidenceVersionId);
        Assert.Equal(1L, await env.Count("EvidenceExtractionVersions"));
        Assert.Equal(1L, await env.Count("DocumentPromotions"));
    }

    [Fact]
    public async Task Simultaneous_claims_yield_a_single_running_aggregator()
    {
        await using var env = await Env.CreateAsync(aggregate: false);
        await env.ParseAsync(TwoUnits());
        await env.RunUntilIdleAsync(new DocumentEvidenceAgentTests.FakeUnitModel());
        var barrier = new Barrier(2);

        Task<AggregationClaim?> Racer(string owner) => Task.Run(async () =>
        {
            await using var context = env.NewContext();
            barrier.SignalAndWait(Ct);
            return await env.Bundles(context).ClaimNextAsync(owner, Ct);
        }, Ct);

        var claims = await Task.WhenAll(Racer("a"), Racer("b"));

        Assert.Single(claims, c => c is not null);
        Assert.Equal(1L, await env.Count("EvidenceExtractionJobs", "\"Status\" = 'Running' AND \"AttemptCount\" = 1 AND \"FencingToken\" = 1"));
    }

    [Fact]
    public async Task A_stale_owner_cannot_publish_nor_notify_while_the_new_owner_publishes_once_and_a_replay_stays_silent()
    {
        await using var env = await Env.CreateAsync(aggregate: false);
        var seed = await env.ParseAsync(TwoUnits());
        await env.RunUntilIdleAsync(new DocumentEvidenceAgentTests.FakeUnitModel());
        var stale = await env.ClaimAsync("agg-old");
        await env.Db.ExecuteAsync($"""UPDATE "EvidenceExtractionJobs" SET "LeaseUntil" = now() - interval '1 second' WHERE "Id" = '{stale.JobId}'""");
        var fresh = await env.ClaimAsync("agg-new");
        Assert.Equal(stale.JobId, fresh.JobId);
        Assert.Equal(stale.FencingToken + 1, fresh.FencingToken);

        {
            await using var context = env.NewContext();
            var runner = new DocumentAggregationRunner(env.Bundles(context), NullLogger<DocumentAggregationRunner>.Instance, env.Notifications);

            var late = await runner.RunClaimAsync(stale, Ct);   // il vecchio owner non scrive e non emette eventi definitivi
            Assert.Equal(AggregationOutcome.Rejected, late.Outcome);
            Assert.Empty(env.Notifications.Published);
            Assert.Equal(0L, await env.Count("EvidenceExtractionVersions"));

            var published = await runner.RunClaimAsync(fresh, Ct);
            Assert.Equal(AggregationOutcome.Published, published.Outcome);
            Assert.Equal([(seed.Preparation, PreparationStatus.Ready)], env.Notifications.Published);

            Assert.Equal(AggregationOutcome.AlreadyPublished, (await runner.RunClaimAsync(fresh, Ct)).Outcome);
            Assert.Single(env.Notifications.Published);   // il replay non rinotifica
        }

        Assert.Equal(1L, await env.Count("EvidenceExtractionVersions"));
    }

    [Fact]
    public async Task A_deadline_passed_before_publish_is_rejected_and_the_sweep_fails_the_aggregator_and_the_preparation()
    {
        await using var env = await Env.CreateAsync(aggregate: false);
        var seed = await env.ParseAsync(TwoUnits());
        await env.RunUntilIdleAsync(new DocumentEvidenceAgentTests.FakeUnitModel());
        var claim = await env.ClaimAsync();
        await env.Db.ExecuteAsync($"""UPDATE "DocumentPreparations" SET "EffectiveDeadlineAt" = clock_timestamp() - interval '1 millisecond' WHERE "Id" = '{seed.Preparation}'""");

        Assert.Equal(AggregationOutcome.Rejected, (await env.PublishAsync(claim)).Outcome);
        Assert.Equal(0L, await env.Count("EvidenceExtractionVersions"));
        Assert.Equal("Extracting", await env.PrepStatus(seed.Preparation));   // sotto lock, senza autorità: nessuna scrittura

        await env.Db.ExecuteAsync($"""UPDATE "EvidenceExtractionJobs" SET "LeaseUntil" = now() - interval '1 second' WHERE "Id" = '{claim.JobId}'""");
        await using var context = env.NewContext();
        Assert.Null(await env.Bundles(context).ClaimNextAsync("agg-2", Ct));   // lo sweep chiude

        Assert.Equal("Failed", await env.PrepStatus(seed.Preparation));
        Assert.Equal("deadline_exceeded", await env.Str("""SELECT "ErrorCode" FROM "EvidenceExtractionJobs" """));
    }

    // ------------------------------------------------------------------ corrente e candidata, slot AI

    [Fact]
    public async Task A_candidate_preparation_becomes_Ready_on_its_own_without_replacing_the_current_bundle()
    {
        await using var env = await Env.CreateAsync(aggregate: false);
        var paragraphs = TwoUnits();
        var seed = await env.ParseAsync(paragraphs);
        var model = new DocumentEvidenceAgentTests.FakeUnitModel();
        await env.RunUntilIdleAsync(model);
        var first = await env.PublishAsync(await env.ClaimAsync());
        Assert.True(first.BecameCurrent);

        // Seconda generazione della STESSA versione file (retry/ricalcolo): stessi byte, nuovo parse.
        var (preparation2, job2) = (Guid.NewGuid(), Guid.NewGuid());
        await using (var context = env.NewContext())
        {
            var hard = DateTimeOffset.UtcNow.AddHours(1);
            context.DocumentPreparations.Add(new DocumentPreparation
            {
                Id = preparation2, ProjectId = seed.Project, DocumentVersionId = seed.Version, Generation = 2, Status = PreparationStatus.Queued,
                PolicyHash = new string('e', 64), RequestedAt = DateTimeOffset.UtcNow, HardDeadlineAt = hard, EffectiveDeadlineAt = hard,
            });
            await context.SaveChangesAsync(Ct);
            context.DocumentJobs.Add(new DocumentJob
            {
                Id = job2, ProjectId = seed.Project, PreparationId = preparation2, Kind = DocumentJobKind.Parse, Status = DocumentJobStatus.Queued,
                RequestedBy = seed.User, RequestedAt = DateTimeOffset.UtcNow, DeadlineAt = hard, MaxAttempts = 3,
                ConfigJson = """{"policyHash":"x","ParseTimeoutSeconds":300,"MaxPages":1000,"MaxBlocks":100000,"MaxExpandedBytes":250000000,"MaxDerivedBytes":250000000}""",
            });
            await context.SaveChangesAsync(Ct);
            (await context.DocumentPreparations.SingleAsync(p => p.Id == preparation2, Ct)).ParseJobId = job2;
            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal(ParseRunOutcome.Published, await env.Parse.RunAsync());
        await env.RunUntilIdleAsync(model);
        var second = await env.PublishAsync(await env.ClaimAsync());

        Assert.Equal(AggregationOutcome.Published, second.Outcome);
        Assert.False(second.BecameCurrent);
        Assert.NotEqual(first.EvidenceVersionId, second.EvidenceVersionId);
        Assert.Equal("Ready", await env.PrepStatus(preparation2));
        Assert.Equal(2L, await env.Count("EvidenceExtractionVersions"));
        Assert.Equal(1L, await env.Count("EvidenceDocumentStates", $"\"CurrentPreparationId\" = '{seed.Preparation}' AND \"CurrentEvidenceVersionId\" = '{first.EvidenceVersionId}' AND \"Revision\" = 1"));
        Assert.Equal(1L, await env.Count("DocumentPromotions"));
        Assert.Equal(seed.Version.ToString(), await env.Str($"""SELECT "LatestReadyVersionId"::text FROM "ProjectDocuments" WHERE "Id" = '{seed.Document}'"""));
        Assert.Equal(1L, await env.Count("EvidenceExtractionVersions", $"\"PreparationId\" = '{seed.Preparation}'"));   // la versione corrente non è stata toccata
    }

    [Fact]
    public async Task A_running_aggregator_holds_no_ai_slot_and_units_of_other_preparations_keep_being_claimed()
    {
        await using var env = await Env.CreateAsync(aggregate: false);
        await env.ParseAsync(TwoUnits());
        await env.RunUntilIdleAsync(new DocumentEvidenceAgentTests.FakeUnitModel());
        var claim = await env.ClaimAsync();
        var other = await env.ParseAsync([Words(30, "z")]);   // un'altra preparazione con una unità Queued
        Assert.Equal(1L, await env.Count("EvidenceExtractionJobs", "\"Status\" = 'Running'"));

        await using var context = env.NewContext();
        var work = await new ExecutionLeaseStore(context, Options.Create(new ExecutionOptions()))
            .ClaimNextWorkAsync("w", turns: true, runs: true, documents: true, extractions: true, units: true, Ct);

        Assert.NotNull(work?.Unit);   // l'aggregatore Running non occupa lo slot globale
        Assert.Equal(other.Preparation, work!.Unit!.PreparationId);
        Assert.Equal("Running", await env.Str($"""SELECT "Status" FROM "EvidenceExtractionJobs" WHERE "Id" = '{claim.JobId}'"""));
    }
}
