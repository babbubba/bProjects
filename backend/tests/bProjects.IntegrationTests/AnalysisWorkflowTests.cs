using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T029 — project-analysis-v2 nativo su PostgreSQL reale con un IChatClient finto (nessuna chiamata al DGX: la prova
/// con il modello reale è <see cref="AnalysisWorkflowRealTests"/>). Il grafo LoadDialog → ConversationalAgent →
/// ValidatePersistAnalysis gira con WorkflowBuilder/AIAgentHostExecutor e checkpoint nativi su PostgresCheckpointStore;
/// il commit di revisione AI + Completed + CurrentAnalysisId è atomico e protetto da owner/fencing/lease.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class AnalysisWorkflowTests : IAsyncLifetime
{
    private const string Owner = "worker-a";
    private const long Fence = 7;

    private readonly PostgresDatabaseFixture _database = new();
    private ApiHarness? _app;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            _app = await ApiHarness.StartAsync(_database.ConnectionString); // applica le migrazioni e crea i PM di test
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

    // ---------------------------------------------------------------- percorso nominale

    [Theory]
    [MemberData(nameof(AnalysisContentTests.Modes), MemberType = typeof(AnalysisContentTests))]
    public async Task A_valid_json_runs_the_three_real_phases_and_commits_revision_Completed_and_CurrentAnalysisId(StructuredOutputMode mode)
    {
        var (world, claim) = await SeedAsync();
        var model = new ScriptedModel(AnalysisFixtures.NominalAi(world).ToJsonString());
        var stages = new List<string>();

        var result = await Workflow(model, mode: mode).RunAsync(claim, stages.Add, Ct);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        Assert.NotNull(result.ResultRevisionId);
        Assert.Equal(["LoadDialog", "ConversationalAgent_consolidation", "ValidatePersistAnalysis"], stages);
        Assert.Equal(1, model.Calls);

        // revisione AI Unvalidated v1, senza parent, attribuita al richiedente, contenuto che ripassa la validazione
        var revisions = await QueryAsync($"""
            SELECT "Id"::text || '|' || "Version" || '|' || "Origin" || '|' || "ValidationState" || '|' || COALESCE("ParentRevisionId"::text, '-') || '|' || "SchemaVersion"
            FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'
            """);
        Assert.Equal([$"{result.ResultRevisionId}|1|Ai|Unvalidated|-|1.0.0"], revisions);
        var stored = (await QueryAsync($"""SELECT "ContentJson"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'""")).Single();
        Assert.True(AnalysisGateway.Validate((JsonObject)JsonNode.Parse(stored)!, world.Trusted, AnalysisActor.Ai).Accepted);

        // stato del run e del progetto, scritti nello stesso commit
        Assert.Equal([$"Completed|{result.ResultRevisionId}|-|-"], await QueryAsync($"""
            SELECT "Status" || '|' || "ResultRevisionId" || '|' || COALESCE("LeaseOwner", '-') || '|' || COALESCE("ErrorCode", '-') FROM "ProcessingRuns" WHERE "Id" = '{claim.RunId}'
            """));
        Assert.Equal([claim.RunId.ToString()], await QueryAsync($"""SELECT "CurrentAnalysisId"::text FROM "Projects" WHERE "Id" = '{world.ProjectId}'"""));
        // fase osservata reale e checkpoint nativi presenti; nessuna fase documentale
        Assert.Equal(["ValidatePersistAnalysis"], await QueryAsync($"""SELECT "LastObservedStage" FROM "ProcessingRuns" WHERE "Id" = '{claim.RunId}'"""));
        Assert.True(int.Parse((await QueryAsync($"""SELECT count(*)::text FROM "WorkflowCheckpoints" WHERE "ProcessingRunId" = '{claim.RunId}'""")).Single()) >= 2);
        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "ProcessingRuns" WHERE "Kind" = 'Document'"""));
        Assert.DoesNotContain("DocumentAgent", AnalysisWorkflow.Stages);
    }

    [Theory]
    [MemberData(nameof(AnalysisContentTests.Modes), MemberType = typeof(AnalysisContentTests))]
    public async Task The_model_receives_only_the_dialog_up_to_the_cutoff_and_the_trusted_identities(StructuredOutputMode mode)
    {
        var (world, claim) = await SeedAsync();
        var model = new ScriptedModel(AnalysisFixtures.NominalAi(world).ToJsonString());

        await Workflow(model, mode: mode).RunAsync(claim, null, Ct);

        var request = model.Requests.Single();
        var text = string.Join("\n", request.Select(m => m.Text));
        Assert.Contains($"[messaggio id={world.U1.Id} sequenza=1]", text);
        Assert.Contains($"[messaggio id={world.A1.Id} sequenza=2]", text);
        Assert.Contains($"[messaggio id={world.U2.Id} sequenza=3]", text);
        Assert.DoesNotContain(world.BeyondCutoff.Id.ToString(), text); // il messaggio oltre il cutoff non arriva mai
        Assert.DoesNotContain("successivo al cutoff", text);
        Assert.Contains($"analysisId: {world.AnalysisId}", text);
        Assert.Contains($"projectId: {world.ProjectId}", text);
        Assert.Contains("sourceCutoffSequence: 5", text);
        // ruoli originali preservati; il prompt di consolidamento è nelle istruzioni, separato da quello del dialogo
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.User], request.Where(m => m.Role != ChatRole.System).Select(m => m.Role));
        Assert.Contains(ConversationalAgent.ConsolidationInstructions, model.Options.Single().Instructions ?? string.Join("\n", request.Where(m => m.Role == ChatRole.System).Select(m => m.Text)));

        var format = model.Options.Single().ResponseFormat as ChatResponseFormatJson;
        Assert.Equal(mode == StructuredOutputMode.JsonSchema, format?.Schema is not null);
    }

    // ---------------------------------------------------------------- output non valido: Failed, nessuna revisione, nessuna riparazione

    [Fact]
    public async Task Invalid_model_output_fails_the_run_without_revision_repair_or_CurrentAnalysisId()
    {
        var invalid = new Dictionary<string, Func<AnalysisWorld, string>>
        {
            ["schema"] = w => AnalysisFixtures.Mutated(w, AnalysisFixtures.AiInvalid["manca_conflicts"].Mutate).ToJsonString(),
            ["semantica"] = w => AnalysisFixtures.Mutated(w, AnalysisFixtures.AiInvalid["evidenza_non_risolta"].Mutate).ToJsonString(),
            ["markdown"] = w => "```json\n" + AnalysisFixtures.NominalAi(w).ToJsonString() + "\n```",
            ["prosa"] = w => "Ecco l'analisi richiesta:\n" + AnalysisFixtures.NominalAi(w).ToJsonString(),
            ["vuoto"] = _ => "   ",
        };

        foreach (var (name, output) in invalid)
        {
            var (world, claim) = await SeedAsync();
            var model = new ScriptedModel(output(world));

            var result = await Workflow(model).RunAsync(claim, null, Ct);

            Assert.True(result.Status == ProcessingRunStatus.Failed, $"{name}: atteso Failed");
            Assert.Equal("invalid_agent_output", result.ErrorCode);
            Assert.Null(result.ResultRevisionId);
            Assert.Equal(1, model.Calls); // nessuna chiamata di riparazione
            Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'"""));
            Assert.Equal(["-"], await QueryAsync($"""SELECT COALESCE("CurrentAnalysisId"::text, '-') FROM "Projects" WHERE "Id" = '{world.ProjectId}'"""));
            Assert.Equal(["-|invalid_agent_output"], await QueryAsync($"""SELECT COALESCE("LeaseOwner", '-') || '|' || "ErrorCode" FROM "ProcessingRuns" WHERE "Id" = '{claim.RunId}'"""));
        }
    }

    [Fact]
    public async Task A_source_beyond_the_cutoff_is_rejected_because_the_cutoff_is_immutable()
    {
        var (world, claim) = await SeedAsync();
        var output = AnalysisFixtures.NominalAi(world);
        ((JsonArray)output["sources"]!).Add(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["kind"] = "Message", ["messageId"] = world.BeyondCutoff.Id.ToString(), ["messageRole"] = "user", ["revisionId"] = null,
        });

        var result = await Workflow(new ScriptedModel(output.ToJsonString())).RunAsync(claim, null, Ct);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal("invalid_agent_output", result.ErrorCode);
        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'"""));
    }

    // ---------------------------------------------------------------- checkpoint e resume nativi

    [Fact]
    public async Task After_a_model_failure_the_run_resumes_from_the_checkpoint_without_reloading_the_dialog()
    {
        var (world, claim) = await SeedAsync();
        var model = new ScriptedModel(AnalysisFixtures.NominalAi(world).ToJsonString()) { FailFirst = new HttpRequestException("giù", null, HttpStatusCode.ServiceUnavailable) };

        var first = await Assert.ThrowsAsync<HttpRequestException>(() => Workflow(model).RunAsync(claim, null, Ct));
        Assert.True(ModelFailure.Classify(first).IsTransient); // il retry è del dispatcher, non del workflow
        Assert.Equal(["Running"], await QueryAsync($"""SELECT "Status" FROM "ProcessingRuns" WHERE "Id" = '{claim.RunId}'"""));
        var session = (await QueryAsync($"""SELECT "WorkflowSessionId" FROM "ProcessingRuns" WHERE "Id" = '{claim.RunId}'""")).Single();
        Assert.True(int.Parse((await QueryAsync($"""SELECT count(*)::text FROM "WorkflowCheckpoints" WHERE "ProcessingRunId" = '{claim.RunId}'""")).Single()) >= 1);

        // «nuovo processo»: servizi, workflow e CheckpointManager nuovi; l'unico stato condiviso è PostgreSQL
        var stages = new List<string>();
        var second = await Workflow(model).RunAsync(claim, stages.Add, Ct);

        Assert.Equal(ProcessingRunStatus.Completed, second.Status);
        Assert.DoesNotContain("LoadDialog", stages);   // ripreso dopo il checkpoint, non riavviato
        Assert.Equal(2, model.Calls);                  // la prima chiamata è fallita, la seconda ha prodotto il JSON
        Assert.Equal([session], await QueryAsync($"""SELECT "WorkflowSessionId" FROM "ProcessingRuns" WHERE "Id" = '{claim.RunId}'"""));
        Assert.Single(await QueryAsync($"""SELECT "Id"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'"""));
    }

    [Fact]
    public async Task A_crash_before_the_commit_resumes_after_the_agent_without_calling_the_model_again()
    {
        var (world, claim) = await SeedAsync();
        var model = new ScriptedModel(AnalysisFixtures.NominalAi(world).ToJsonString());
        var flaky = new FlakyStore { FailNextCommit = true };

        await Assert.ThrowsAsync<IOException>(() => Workflow(model, flaky).RunAsync(claim, null, Ct));
        Assert.Equal(1, model.Calls);
        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'"""));
        Assert.Equal(["Running"], await QueryAsync($"""SELECT "Status" FROM "ProcessingRuns" WHERE "Id" = '{claim.RunId}'"""));

        var stages = new List<string>();
        var result = await Workflow(model, flaky).RunAsync(claim, stages.Add, Ct);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        Assert.Equal(["ValidatePersistAnalysis"], stages); // solo l'ultima fase: l'output dell'agente era nel checkpoint
        Assert.Equal(1, model.Calls);                      // nessuna seconda inferenza
        Assert.Single(await QueryAsync($"""SELECT "Id"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'"""));
    }

    [Fact]
    public async Task A_completed_run_is_not_reprocessed_and_a_post_commit_checkpoint_failure_does_not_degrade_it()
    {
        var (world, claim) = await SeedAsync();
        var model = new ScriptedModel(AnalysisFixtures.NominalAi(world).ToJsonString());

        // il commit rilascia il lease: il checkpoint di fine superstep viene rifiutato, ma Completed prevale
        var first = await Workflow(model).RunAsync(claim, null, Ct);
        Assert.Equal(ProcessingRunStatus.Completed, first.Status);

        var replay = await Workflow(model).RunAsync(claim, null, Ct);
        Assert.Equal(first, replay);
        Assert.Equal(1, model.Calls);
        Assert.Single(await QueryAsync($"""SELECT "Id"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'"""));
    }

    [Fact]
    public async Task A_run_with_an_incompatible_definition_fails_explicitly_without_calling_the_model()
    {
        var (_, claim) = await SeedAsync(definition: "project-analysis-v1");
        var model = new ScriptedModel("{}");

        var result = await Workflow(model).RunAsync(claim, null, Ct);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal("workflow_incompatible", result.ErrorCode);
        Assert.Equal(0, model.Calls);
    }

    // ---------------------------------------------------------------- commit atomico, fencing e concorrenza

    [Fact]
    public async Task A_stale_owner_fencing_token_or_expired_lease_commits_nothing()
    {
        var (world, claim) = await SeedAsync();
        var content = ParsedNominal(world);

        foreach (var stale in new[] { claim with { LeaseOwner = "worker-b" }, claim with { FencingToken = Fence - 1 }, claim with { FencingToken = Fence + 1 } })
        {
            Assert.Equal(AnalysisCommitOutcome.Rejected, (await CommitAsync(stale, content)).Outcome);
        }

        await ExecuteAsync($"""UPDATE "ProcessingRuns" SET "LeaseUntil" = now() - interval '1 second' WHERE "Id" = '{claim.RunId}'""");
        Assert.Equal(AnalysisCommitOutcome.Rejected, (await CommitAsync(claim, content)).Outcome);
        await ExecuteAsync($"""UPDATE "ProcessingRuns" SET "LeaseUntil" = now() + interval '1 minute', "DeadlineAt" = now() - interval '1 second' WHERE "Id" = '{claim.RunId}'""");
        Assert.Equal(AnalysisCommitOutcome.Rejected, (await CommitAsync(claim, content)).Outcome);

        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'"""));
        Assert.Equal(["Running|-"], await QueryAsync($"""SELECT "Status" || '|' || COALESCE("ResultRevisionId"::text, '-') FROM "ProcessingRuns" WHERE "Id" = '{claim.RunId}'"""));
        Assert.Equal(["-"], await QueryAsync($"""SELECT COALESCE("CurrentAnalysisId"::text, '-') FROM "Projects" WHERE "Id" = '{world.ProjectId}'"""));
    }

    [Fact]
    public async Task A_failure_in_the_middle_of_the_transaction_leaves_nothing_and_a_retry_commits_once()
    {
        var (world, claim) = await SeedAsync();
        var content = ParsedNominal(world);
        // il guasto arriva all'ultimo passo (CurrentAnalysisId), quando revisione e Completed sono già scritti nella transazione
        await ExecuteAsync("""
            CREATE FUNCTION fail_current_analysis() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'guasto iniettato'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_current_analysis BEFORE UPDATE ON "Projects" FOR EACH ROW WHEN (NEW."CurrentAnalysisId" IS NOT NULL) EXECUTE FUNCTION fail_current_analysis();
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => CommitAsync(claim, content));

        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'"""));
        Assert.Equal(["Running|-"], await QueryAsync($"""SELECT "Status" || '|' || COALESCE("ResultRevisionId"::text, '-') FROM "ProcessingRuns" WHERE "Id" = '{claim.RunId}'"""));

        await ExecuteAsync("""DROP TRIGGER fail_current_analysis ON "Projects"; DROP FUNCTION fail_current_analysis();""");
        var retry = await CommitAsync(claim, content);

        Assert.Equal(AnalysisCommitOutcome.Committed, retry.Outcome);
        Assert.Single(await QueryAsync($"""SELECT "Id"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'"""));
    }

    [Fact]
    public async Task A_repeated_or_concurrent_commit_creates_a_single_revision_and_reconciles_the_rest()
    {
        var (world, claim) = await SeedAsync();
        var content = ParsedNominal(world);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CommitAsync(claim, content)));

        Assert.Equal(1, results.Count(r => r.Outcome == AnalysisCommitOutcome.Committed));
        Assert.Equal(3, results.Count(r => r.Outcome == AnalysisCommitOutcome.AlreadyCompleted));
        var revision = Assert.Single(await QueryAsync($"""SELECT "Id"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'"""));
        Assert.All(results, r => Assert.Equal(Guid.Parse(revision), r.RevisionId));
    }

    [Fact]
    public async Task A_commit_whose_content_is_not_bound_to_the_run_is_rejected()
    {
        var (world, claim) = await SeedAsync();
        var other = AnalysisFixtures.NominalAi(world);
        other["analysisId"] = Guid.NewGuid().ToString();

        var result = await CommitAsync(claim, AnalysisContentJson.Deserialize(other.ToJsonString()));

        Assert.Equal(AnalysisCommitOutcome.Rejected, result.Outcome);
        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'"""));
    }

    [Fact]
    public async Task A_run_that_lost_its_lease_before_starting_does_not_call_the_model()
    {
        var (world, claim) = await SeedAsync();
        var model = new ScriptedModel(AnalysisFixtures.NominalAi(world).ToJsonString());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Workflow(model).RunAsync(claim with { FencingToken = Fence + 1 }, null, Ct));

        Assert.Equal(0, model.Calls);
        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '{claim.RunId}'"""));
    }

    [Fact]
    public async Task A_checkpoint_payload_comes_back_with_the_type_discriminator_first_even_though_jsonb_reorders_properties()
    {
        var (_, claim) = await SeedAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_database.ConnectionString).Options);
        var store = new PostgresCheckpointStore(db, new CheckpointWriter(claim.RunId, claim.LeaseOwner, claim.FencingToken));
        var info = await store.CreateCheckpointAsync("s1", JsonDocument.Parse("""{"workflow":{"edges":{"a":[{"zeta":1,"$type":"direct","alfa":{"zz":1,"$type":"x"}}]}}}""").RootElement);

        var read = await store.RetrieveCheckpointAsync("s1", info);

        var edge = read.GetProperty("workflow").GetProperty("edges").GetProperty("a")[0];
        Assert.Equal("$type", edge.EnumerateObject().First().Name);
        Assert.Equal("$type", edge.GetProperty("alfa").EnumerateObject().First().Name);
    }

    // ---------------------------------------------------------------- supporto

    private static AnalysisContent ParsedNominal(AnalysisWorld world) => AnalysisContentJson.Deserialize(AnalysisFixtures.NominalAi(world).ToJsonString());

    private ServiceProvider Services(FlakyStore? flaky = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(_database.ConnectionString));
        services.AddScoped<AnalysisRunCompletion>();
        services.AddScoped<IAnalysisRunStore>(sp => flaky is null
            ? sp.GetRequiredService<AnalysisRunCompletion>()
            : new FlakyStoreAdapter(sp.GetRequiredService<AnalysisRunCompletion>(), flaky));
        return services.BuildServiceProvider();
    }

    private AnalysisWorkflow Workflow(ScriptedModel model, FlakyStore? flaky = null, StructuredOutputMode mode = StructuredOutputMode.JsonSchema)
    {
        var services = Services(flaky);
        using var client = new InternalModelClient(new AiOptions { Endpoint = "http://localhost:1/v1/", ModelId = "fake", ApiKey = "non-usata", StructuredOutputMode = mode });
        var agent = new ConversationalAgent(
            new AgentFactory(new AiOptions { ContextWindowTokens = 32768 }, model, (t, _) => new(t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)),
            new ChatOptions { ModelId = "fake" });
        return new AnalysisWorkflow(
            services.GetRequiredService<IServiceScopeFactory>(), agent, client.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0));
    }

    private async Task<AnalysisCommitResult> CommitAsync(AnalysisRunClaim claim, AnalysisContent content)
    {
        using var services = Services();
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().CommitAsync(claim, content, Ct);
    }

    /// <summary>Progetto, conversazione (U1, A1, U2 entro il cutoff 5; un messaggio al 9 dopo il cutoff) e run Analysis Running con lease.</summary>
    private async Task<(AnalysisWorld World, AnalysisRunClaim Claim)> SeedAsync(string definition = ProcessingRun.AnalysisWorkflowVersion)
    {
        TestEnvironment.RequirePostgres();
        var world = new AnalysisWorld();
        var user = (await QueryAsync("""SELECT "Id"::text FROM "AspNetUsers" LIMIT 1""")).Single();
        var conversation = Guid.NewGuid();
        var turn = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO "Projects" ("Id","Name","CreatedBy","CreatedAt") VALUES ('{world.ProjectId}','Analisi','{user}', now());
            INSERT INTO "Conversations" ("Id","ProjectId","CreatedAt","LastSequence","MafVersion","ContextThroughSequence","ChatSessionRevision")
                VALUES ('{conversation}','{world.ProjectId}', now(), 9, '1.26.0', 0, 0);
            INSERT INTO "ConversationMessages" ("Id","ConversationId","Sequence","Role","Body","CreatedAt","ClientMessageId","AuthorId")
                VALUES ('{world.U1.Id}','{conversation}', 1, 'User', 'Vogliamo un portale clienti per ridurre i tempi di risposta ai ticket.', now(), '{Guid.NewGuid()}', '{user}');
            INSERT INTO "ChatTurns" ("Id","ConversationId","TriggerMessageId","SourceCutoffSequence","Status","RequestedAt","DeadlineAt","AttemptCount","MaxAttempts","FencingToken","SessionRevisionAtStart")
                VALUES ('{turn}','{conversation}','{world.U1.Id}', 1, 'Queued', now(), now() + interval '3 minutes', 0, 3, 0, 0);
            INSERT INTO "ConversationMessages" ("Id","ConversationId","Sequence","Role","Body","CreatedAt","AgentId","ReplyToMessageId","ChatTurnId")
                VALUES ('{world.A1.Id}','{conversation}', 2, 'Assistant', 'Chi userà il portale?', now(), 'ConversationalAgent', '{world.U1.Id}', '{turn}');
            UPDATE "ChatTurns" SET "Status" = 'Completed', "ResultMessageId" = '{world.A1.Id}', "CompletedAt" = now() WHERE "Id" = '{turn}';
            INSERT INTO "ConversationMessages" ("Id","ConversationId","Sequence","Role","Body","CreatedAt","ClientMessageId","AuthorId")
                VALUES ('{world.U2.Id}','{conversation}', 3, 'User', 'Gli agenti del supporto, da browser desktop.', now(), '{Guid.NewGuid()}', '{user}');
            INSERT INTO "ConversationMessages" ("Id","ConversationId","Sequence","Role","Body","CreatedAt","ClientMessageId","AuthorId")
                VALUES ('{world.BeyondCutoff.Id}','{conversation}', 9, 'User', 'Messaggio successivo al cutoff.', now(), '{Guid.NewGuid()}', '{user}');
            INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","ConversationId","SourceCutoffSequence","RequestKey","RequestedBy","RequestedAt","Status","StartedAt","DeadlineAt",
                "WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","LeaseOwner","LeaseUntil","FencingToken","UpdatedAt")
            VALUES ('{world.AnalysisId}','{world.ProjectId}','Analysis','{conversation}',5,'{Guid.NewGuid()}','{user}',now(),'Running',now(),now() + interval '3 minutes',
                '{definition}','1.26.0',1,3,'{Owner}', now() + interval '10 minutes', {Fence}, now());
            """);
        return (world, new AnalysisRunClaim(world.AnalysisId, Owner, Fence));
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Guasto iniettato al commit: simula un crollo fra l'agente e la persistenza.</summary>
    internal sealed class FlakyStore
    {
        public bool FailNextCommit { get; set; }
    }

    private sealed class FlakyStoreAdapter(AnalysisRunCompletion inner, FlakyStore flaky) : IAnalysisRunStore
    {
        public Task<AnalysisRunInput> LoadInputAsync(Guid runId, CancellationToken ct) => inner.LoadInputAsync(runId, ct);

        public Task<AnalysisCommitResult> CommitAsync(AnalysisRunClaim claim, AnalysisContent content, CancellationToken ct)
        {
            if (flaky.FailNextCommit)
            {
                flaky.FailNextCommit = false;
                throw new IOException("guasto iniettato prima del commit");
            }

            return inner.CommitAsync(claim, content, ct);
        }

        public Task<bool> FailAsync(AnalysisRunClaim claim, string errorCode, string errorMessage, CancellationToken ct) => inner.FailAsync(claim, errorCode, errorMessage, ct);

        public Task<bool> RecordStageAsync(AnalysisRunClaim claim, string stage, CancellationToken ct) => inner.RecordStageAsync(claim, stage, ct);
    }

    /// <summary>LLM finto: risponde con un testo fisso (in streaming) e registra richieste e opzioni.</summary>
    internal sealed class ScriptedModel(string reply) : IChatClient
    {
        public int Calls { get; private set; }

        public List<List<ChatMessage>> Requests { get; } = [];

        public List<ChatOptions> Options { get; } = [];

        public Exception? FailFirst { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Record(messages, options);
            if (FailFirst is { } failure && Calls == 1)
            {
                throw failure;
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Record(messages, options);
            await Task.Yield();
            if (FailFirst is { } failure && Calls == 1)
            {
                throw failure;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        }

        private void Record(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            Calls++;
            Requests.Add([.. messages]);
            Options.Add(options ?? new ChatOptions());
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

/// <summary>T029 con il modello reale su DGX Spark: JSON prodotto, validato e persistito (MafReal + PostgreSQL); Skipped senza configurazione.</summary>
[Trait("Category", TestCategories.MafReal)]
public sealed class AnalysisWorkflowRealTests : IAsyncLifetime
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

    [Theory]
    [MemberData(nameof(AnalysisContentTests.Modes), MemberType = typeof(AnalysisContentTests))]
    public async Task The_real_model_produces_a_json_that_is_validated_and_persisted_as_an_unvalidated_ai_revision(StructuredOutputMode mode)
    {
        TestEnvironment.RequirePostgres();
        var options = TestEnvironment.RequireDgxOptions();
        options.StructuredOutputMode = mode;
        using var model = new InternalModelClient(options);

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(_database.ConnectionString));
        services.AddScoped<IAnalysisRunStore, AnalysisRunCompletion>();
        using var provider = services.BuildServiceProvider();
        var workflow = new AnalysisWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConversationalAgent(AgentFactory.Create(options, model), model.CreateChatOptions(temperature: 0.2f)),
            model.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0));

        var claim = await SeedRealRunAsync();
        var result = await workflow.RunAsync(claim, null, Ct);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        Assert.NotNull(result.ResultRevisionId);
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""SELECT "ValidationState", "Origin", "ContentJson"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = @id""", connection);
        command.Parameters.AddWithValue("id", claim.RunId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal("Unvalidated", reader.GetString(0));
        Assert.Equal("Ai", reader.GetString(1));
        using var json = JsonDocument.Parse(reader.GetString(2));
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind); // puro JSON, mai Markdown
    }

    private async Task<AnalysisRunClaim> SeedRealRunAsync()
    {
        var project = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var run = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "Projects" ("Id","Name","CreatedBy","CreatedAt") SELECT '{project}','Portale clienti', u."Id", now() FROM "AspNetUsers" u LIMIT 1;
            INSERT INTO "Conversations" ("Id","ProjectId","CreatedAt","LastSequence","MafVersion","ContextThroughSequence","ChatSessionRevision")
                VALUES ('{conversation}','{project}', now(), 1, '1.26.0', 0, 0);
            INSERT INTO "ConversationMessages" ("Id","ConversationId","Sequence","Role","Body","CreatedAt","ClientMessageId","AuthorId")
                SELECT '{Guid.NewGuid()}','{conversation}', 1, 'User',
                  'Vogliamo un portale web dove i clienti aprono ticket di assistenza e ne vedono lo stato. Non è chiaro se serva integrare il gestionale. Il go-live è previsto per giugno.',
                  now(), '{Guid.NewGuid()}', u."Id" FROM "AspNetUsers" u LIMIT 1;
            INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","ConversationId","SourceCutoffSequence","RequestKey","RequestedBy","RequestedAt","Status","StartedAt","DeadlineAt",
                "WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","LeaseOwner","LeaseUntil","FencingToken","UpdatedAt")
            SELECT '{run}','{project}','Analysis','{conversation}',1,'{Guid.NewGuid()}',u."Id",now(),'Running',now(),now() + interval '3 minutes',
                'project-analysis-v2','1.26.0',1,3,'real-worker', now() + interval '10 minutes', 1, now() FROM "AspNetUsers" u LIMIT 1;
            """, connection);
        await command.ExecuteNonQueryAsync(Ct);
        return new AnalysisRunClaim(run, "real-worker", 1);
    }
}
