using System.Net;
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
/// T040 — project-document-v1 nativo su PostgreSQL reale con un IChatClient finto (nessuna chiamata al DGX: la prova con il modello
/// reale è T043). Il grafo LoadConfirmedRevision → DocumentAgent → ValidatePersistDraft gira con WorkflowBuilder/AIAgentHostExecutor e
/// checkpoint nativi su PostgresCheckpointStore; il commit di bozza + Completed + LatestDraftId è atomico, idempotente e protetto da
/// owner/fencing/lease; il formato/le fonti/la revisione sono validati prima del commit e un output non valido non lascia bozze.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentWorkflowTests : IAsyncLifetime
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

    [Fact]
    public async Task A_valid_markdown_runs_the_three_real_phases_and_commits_draft_Completed_and_LatestDraftId_bound_to_the_input_revision()
    {
        var s = await SeedAsync();
        var model = new DocumentContractTests.DocumentModel();
        var stages = new List<string>();

        var result = await Workflow(model).RunAsync(s.Claim, stages.Add, Ct);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        Assert.NotNull(result.DraftId);
        Assert.Equal(["LoadConfirmedRevision", "DocumentAgent_document", "ValidatePersistDraft"], stages);
        Assert.Equal(1, model.Calls);

        // bozza Proposed/NotApproved legata al progetto, al run e alla revisione di input
        Assert.Equal([$"{result.DraftId}|{s.ProjectId}|{s.Claim.RunId}|{s.Revision}|Proposed|NotApproved"], await QueryAsync($"""
            SELECT "Id"::text || '|' || "ProjectId" || '|' || "ProcessingRunId" || '|' || "AnalysisRevisionId" || '|' || "Status" || '|' || "ApprovalStatus"
            FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'
            """));
        Assert.StartsWith("# Documento di progetto", (await QueryAsync($"""SELECT "Markdown" FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'""")).Single());

        // stato del run e del progetto, scritti nello stesso commit; nessun risultato di Analysis
        Assert.Equal([$"Completed|{result.DraftId}|-|-|-"], await QueryAsync($"""
            SELECT "Status" || '|' || "DraftId" || '|' || COALESCE("LeaseOwner", '-') || '|' || COALESCE("ErrorCode", '-') || '|' || COALESCE("ResultRevisionId"::text, '-')
            FROM "ProcessingRuns" WHERE "Id" = '{s.Claim.RunId}'
            """));
        Assert.Equal([result.DraftId.ToString()!], await QueryAsync($"""SELECT "LatestDraftId"::text FROM "Projects" WHERE "Id" = '{s.ProjectId}'"""));
        Assert.Equal(["ValidatePersistDraft"], await QueryAsync($"""SELECT "LastObservedStage" FROM "ProcessingRuns" WHERE "Id" = '{s.Claim.RunId}'"""));

        // sessione e checkpoint nativi propri del run Document; la revisione confermata non è stata toccata
        Assert.True(int.Parse((await QueryAsync($"""SELECT count(*)::text FROM "WorkflowCheckpoints" WHERE "ProcessingRunId" = '{s.Claim.RunId}'""")).Single()) >= 2);
        Assert.Equal(["0"], await QueryAsync($"""SELECT count(*)::text FROM "WorkflowCheckpoints" WHERE "ProcessingRunId" = '{s.AnalysisId}'"""));
        Assert.Equal(["PmConfirmed"], await QueryAsync($"""SELECT "ValidationState" FROM "AnalysisRevisions" WHERE "Id" = '{s.Revision}'"""));
    }

    [Fact]
    public async Task The_model_receives_the_whole_input_revision_and_never_a_later_one()
    {
        var s = await SeedAsync(laterEdit: true);
        var model = new DocumentContractTests.DocumentModel();

        await Workflow(model).RunAsync(s.Claim, null, Ct);

        var request = Assert.Single(Assert.Single(model.Requests), m => m.Role == ChatRole.User).Text;
        Assert.True(JsonNode.DeepEquals(s.Content, DocumentContractTests.DocumentModel.EmbeddedJson(request))); // l'intero JSON confermato
        Assert.DoesNotContain("Obiettivo successivo non confermato", request); // la revisione v2 non è l'input
        Assert.Contains($"revisionId: {s.Revision} (versione 1)", request);
        Assert.Equal(s.Revision.ToString(), (await QueryAsync($"""SELECT "AnalysisRevisionId"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'""")).Single());
    }

    // ---------------------------------------------------------------- output non valido: Failed, nessuna bozza, nessuna riparazione

    [Fact]
    public async Task Invalid_model_output_fails_the_run_without_draft_repair_or_change_to_the_latest_draft()
    {
        var invalid = new Dictionary<string, Func<string, string>>
        {
            ["fonte inventata"] = _ => "# Documento\n\nUn fatto [fonte:" + Guid.NewGuid() + "]",
            ["fonte malformata"] = _ => "# Documento\n\nUn fatto [fonte:non-un-uuid]",
            ["nessuna fonte"] = _ => "# Documento\n\nTesto senza alcuna citazione.",
            ["vuoto"] = _ => "   ",
            ["recinto"] = text => "```markdown\n# Documento [fonte:" + DocumentContractTests.DocumentModel.EmbeddedJson(text)["sources"]![0]!["id"] + "]\n```",
            ["json"] = text => DocumentContractTests.DocumentModel.EmbeddedJson(text).ToJsonString(),
            ["html"] = text => "# Documento [fonte:" + DocumentContractTests.DocumentModel.EmbeddedJson(text)["sources"]![0]!["id"] + "]\n\n<script>alert(1)</script>",
            ["immagine remota"] = text => "# Documento [fonte:" + DocumentContractTests.DocumentModel.EmbeddedJson(text)["sources"]![0]!["id"] + "]\n\n![x](http://example.test/a.png)",
        };

        foreach (var (name, output) in invalid)
        {
            var s = await SeedAsync();
            var previous = await SeedPreviousDraftAsync(s);
            var model = new DocumentContractTests.DocumentModel(output);

            var result = await Workflow(model).RunAsync(s.Claim, null, Ct);

            Assert.True(result.Status == ProcessingRunStatus.Failed, $"{name}: atteso Failed");
            Assert.Equal("invalid_agent_output", result.ErrorCode);
            Assert.Null(result.DraftId);
            Assert.Equal(1, model.Calls); // nessuna chiamata di riparazione
            Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'"""));
            Assert.Equal([previous.ToString()], await QueryAsync($"""SELECT "LatestDraftId"::text FROM "Projects" WHERE "Id" = '{s.ProjectId}'"""));
            Assert.Equal(["-|invalid_agent_output"], await QueryAsync($"""SELECT COALESCE("LeaseOwner", '-') || '|' || "ErrorCode" FROM "ProcessingRuns" WHERE "Id" = '{s.Claim.RunId}'"""));
        }
    }

    [Fact]
    public async Task A_content_over_the_token_budget_fails_with_context_overflow_before_any_model_call_and_no_partial_document()
    {
        var s = await SeedAsync();
        var model = new DocumentContractTests.DocumentModel();
        // il solo prompt (centinaia di parole) supera già questo budget: finestra 600 - output 50 - margine 50
        var options = new AiOptions { ContextWindowTokens = 600, MaxOutputTokens = 50, SafetyMarginTokens = 50 };

        var result = await Workflow(model, options: options).RunAsync(s.Claim, null, Ct);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal("context_overflow", result.ErrorCode);
        Assert.Equal(0, model.Calls);
        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'"""));
        Assert.Equal(["-"], await QueryAsync($"""SELECT COALESCE("LatestDraftId"::text, '-') FROM "Projects" WHERE "Id" = '{s.ProjectId}'"""));
    }

    [Fact]
    public async Task A_run_whose_input_revision_is_not_confirmed_fails_explicitly_without_calling_the_model()
    {
        var s = await SeedAsync();
        // corruzione dell'input: la revisione risulta non confermata (il vincolo CK ammette Unvalidated solo senza metadati di conferma)
        await ExecuteAsync($"""UPDATE "AnalysisRevisions" SET "ValidationState" = 'Unvalidated', "ConfirmedBy" = NULL, "ConfirmedAt" = NULL WHERE "Id" = '{s.Revision}'""");
        var model = new DocumentContractTests.DocumentModel();

        var result = await Workflow(model).RunAsync(s.Claim, null, Ct);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal("processing_failed", result.ErrorCode);
        Assert.Equal(0, model.Calls);
        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'"""));
    }

    // ---------------------------------------------------------------- checkpoint e resume nativi

    [Fact]
    public async Task After_a_model_failure_the_run_resumes_from_the_checkpoint_without_reloading_the_revision()
    {
        var s = await SeedAsync();
        var model = new DocumentContractTests.DocumentModel { FailFirst = new HttpRequestException("giù", null, HttpStatusCode.ServiceUnavailable) };

        var first = await Assert.ThrowsAsync<HttpRequestException>(() => Workflow(model).RunAsync(s.Claim, null, Ct));
        Assert.True(ModelFailure.Classify(first).IsTransient); // il retry è del dispatcher, non del workflow
        Assert.Equal(["Running"], await QueryAsync($"""SELECT "Status" FROM "ProcessingRuns" WHERE "Id" = '{s.Claim.RunId}'"""));
        var session = (await QueryAsync($"""SELECT "WorkflowSessionId" FROM "ProcessingRuns" WHERE "Id" = '{s.Claim.RunId}'""")).Single();
        Assert.True(int.Parse((await QueryAsync($"""SELECT count(*)::text FROM "WorkflowCheckpoints" WHERE "ProcessingRunId" = '{s.Claim.RunId}'""")).Single()) >= 1);

        // «nuovo processo»: servizi, workflow e CheckpointManager nuovi; l'unico stato condiviso è PostgreSQL
        var stages = new List<string>();
        var second = await Workflow(model).RunAsync(s.Claim, stages.Add, Ct);

        Assert.Equal(ProcessingRunStatus.Completed, second.Status);
        Assert.DoesNotContain("LoadConfirmedRevision", stages); // ripreso dopo il checkpoint, non riavviato
        Assert.Equal(2, model.Calls);                           // la prima chiamata è fallita, la seconda ha prodotto il Markdown
        Assert.Equal([session], await QueryAsync($"""SELECT "WorkflowSessionId" FROM "ProcessingRuns" WHERE "Id" = '{s.Claim.RunId}'"""));
        Assert.Single(await QueryAsync($"""SELECT "Id"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'"""));
    }

    [Fact]
    public async Task A_crash_before_the_commit_resumes_after_the_agent_without_calling_the_model_again()
    {
        var s = await SeedAsync();
        var model = new DocumentContractTests.DocumentModel();
        var flaky = new FlakyStore { FailNextCommit = true };

        await Assert.ThrowsAsync<IOException>(() => Workflow(model, flaky).RunAsync(s.Claim, null, Ct));
        Assert.Equal(1, model.Calls);
        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'"""));
        Assert.Equal(["Running"], await QueryAsync($"""SELECT "Status" FROM "ProcessingRuns" WHERE "Id" = '{s.Claim.RunId}'"""));

        var stages = new List<string>();
        var result = await Workflow(model, flaky).RunAsync(s.Claim, stages.Add, Ct);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        Assert.Equal(["ValidatePersistDraft"], stages); // solo l'ultima fase: l'output dell'agente era nel checkpoint
        Assert.Equal(1, model.Calls);                   // nessuna seconda inferenza
        Assert.Single(await QueryAsync($"""SELECT "Id"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'"""));
    }

    [Fact]
    public async Task A_completed_run_is_not_reprocessed_and_a_post_commit_checkpoint_failure_does_not_degrade_it()
    {
        var s = await SeedAsync();
        var model = new DocumentContractTests.DocumentModel();

        // il commit rilascia il lease: il checkpoint di fine superstep viene rifiutato, ma Completed prevale
        var first = await Workflow(model).RunAsync(s.Claim, null, Ct);
        Assert.Equal(ProcessingRunStatus.Completed, first.Status);

        var replay = await Workflow(model).RunAsync(s.Claim, null, Ct);
        Assert.Equal(first, replay);
        Assert.Equal(1, model.Calls);
        Assert.Single(await QueryAsync($"""SELECT "Id"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'"""));
    }

    [Fact]
    public async Task A_run_with_an_incompatible_definition_fails_explicitly_without_calling_the_model()
    {
        var s = await SeedAsync(definition: "project-document-v0");
        var model = new DocumentContractTests.DocumentModel();

        var result = await Workflow(model).RunAsync(s.Claim, null, Ct);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal("workflow_incompatible", result.ErrorCode);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_run_that_lost_its_lease_before_starting_does_not_call_the_model()
    {
        var s = await SeedAsync();
        var model = new DocumentContractTests.DocumentModel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Workflow(model).RunAsync(s.Claim with { FencingToken = Fence + 1 }, null, Ct));

        Assert.Equal(0, model.Calls);
        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'"""));
    }

    // ---------------------------------------------------------------- commit atomico, fencing e concorrenza

    [Fact]
    public async Task A_stale_owner_fencing_token_or_expired_lease_commits_nothing()
    {
        var s = await SeedAsync();

        foreach (var stale in new[] { s.Claim with { LeaseOwner = "worker-b" }, s.Claim with { FencingToken = Fence - 1 }, s.Claim with { FencingToken = Fence + 1 } })
        {
            Assert.Equal(DocumentCommitOutcome.Rejected, (await CommitAsync(stale, ValidMarkdown(s))).Outcome);
        }

        await ExecuteAsync($"""UPDATE "ProcessingRuns" SET "LeaseUntil" = now() - interval '1 second' WHERE "Id" = '{s.Claim.RunId}'""");
        Assert.Equal(DocumentCommitOutcome.Rejected, (await CommitAsync(s.Claim, ValidMarkdown(s))).Outcome);
        await ExecuteAsync($"""UPDATE "ProcessingRuns" SET "LeaseUntil" = now() + interval '1 minute', "DeadlineAt" = now() - interval '1 second' WHERE "Id" = '{s.Claim.RunId}'""");
        Assert.Equal(DocumentCommitOutcome.Rejected, (await CommitAsync(s.Claim, ValidMarkdown(s))).Outcome);

        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'"""));
        Assert.Equal(["Running|-"], await QueryAsync($"""SELECT "Status" || '|' || COALESCE("DraftId"::text, '-') FROM "ProcessingRuns" WHERE "Id" = '{s.Claim.RunId}'"""));
        Assert.Equal(["-"], await QueryAsync($"""SELECT COALESCE("LatestDraftId"::text, '-') FROM "Projects" WHERE "Id" = '{s.ProjectId}'"""));
    }

    [Fact]
    public async Task A_failure_in_the_middle_of_the_transaction_leaves_nothing_and_a_retry_commits_once()
    {
        var s = await SeedAsync();
        // il guasto arriva all'ultimo passo (LatestDraftId), quando bozza e Completed sono già scritti nella transazione
        await ExecuteAsync("""
            CREATE FUNCTION fail_latest_draft() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'guasto iniettato'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_latest_draft BEFORE UPDATE ON "Projects" FOR EACH ROW WHEN (NEW."LatestDraftId" IS NOT NULL) EXECUTE FUNCTION fail_latest_draft();
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => CommitAsync(s.Claim, ValidMarkdown(s)));

        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'"""));
        Assert.Equal(["Running|-"], await QueryAsync($"""SELECT "Status" || '|' || COALESCE("DraftId"::text, '-') FROM "ProcessingRuns" WHERE "Id" = '{s.Claim.RunId}'"""));

        await ExecuteAsync("""DROP TRIGGER fail_latest_draft ON "Projects"; DROP FUNCTION fail_latest_draft();""");
        var retry = await CommitAsync(s.Claim, ValidMarkdown(s));

        Assert.Equal(DocumentCommitOutcome.Committed, retry.Outcome);
        Assert.Single(await QueryAsync($"""SELECT "Id"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'"""));
    }

    [Fact]
    public async Task A_repeated_or_concurrent_commit_creates_a_single_draft_and_reconciles_the_rest()
    {
        var s = await SeedAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CommitAsync(s.Claim, ValidMarkdown(s))));

        Assert.Equal(1, results.Count(r => r.Outcome == DocumentCommitOutcome.Committed));
        Assert.Equal(3, results.Count(r => r.Outcome == DocumentCommitOutcome.AlreadyCompleted));
        var draft = Assert.Single(await QueryAsync($"""SELECT "Id"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'"""));
        Assert.All(results, r => Assert.Equal(Guid.Parse(draft), r.DraftId));
    }

    [Fact]
    public async Task A_commit_is_rejected_when_the_input_revision_is_no_longer_a_confirmed_revision_of_the_analysis()
    {
        var s = await SeedAsync();
        await ExecuteAsync($"""UPDATE "AnalysisRevisions" SET "ValidationState" = 'Unvalidated', "ConfirmedBy" = NULL, "ConfirmedAt" = NULL WHERE "Id" = '{s.Revision}'""");

        var result = await CommitAsync(s.Claim, ValidMarkdown(s));

        Assert.Equal(DocumentCommitOutcome.Rejected, result.Outcome);
        Assert.Equal([], await QueryAsync($"""SELECT "Id"::text FROM "Drafts" WHERE "ProcessingRunId" = '{s.Claim.RunId}'"""));
    }

    // ---------------------------------------------------------------- validatore (formato, fonti, revisione)

    [Fact]
    public async Task The_validator_accepts_a_cited_markdown_with_any_length_and_rejects_sources_outside_the_input_revision()
    {
        var s = await SeedAsync();
        var sources = s.Content["sources"]!.AsArray().Select(x => Guid.Parse(x!["id"]!.GetValue<string>())).ToHashSet();
        var context = new DocumentValidationContext(sources);
        var cited = sources.First();

        Assert.True(DocumentDraftValidator.Validate($"Breve [fonte:{cited}]", context).Accepted); // nessun titolo obbligatorio né limite di parole
        Assert.True(DocumentDraftValidator.Validate($"# Titolo\n\n{string.Join(' ', Enumerable.Repeat("parola", 20_000))} [fonte:{cited}]", context).Accepted);
        Assert.True(DocumentDraftValidator.Validate("# Titolo\n\nTesto con [un collegamento](https://example.test/doc) senza fonti.", new DocumentValidationContext(new HashSet<Guid>())).Accepted); // senza fonti disponibili nulla da citare
        Assert.False(DocumentDraftValidator.Validate($"# Titolo [fonte:{Guid.NewGuid()}]", context).Accepted);
        Assert.False(DocumentDraftValidator.Validate($"# Titolo [fonte:{cited}] [fonte:{Guid.NewGuid()}]", context).Accepted);
        Assert.False(DocumentDraftValidator.Validate(null, context).Accepted);
        Assert.False(DocumentDraftValidator.Validate($"# Titolo [fonte:{cited}] [x](javascript:alert(1))", context).Accepted);
    }

    // ---------------------------------------------------------------- supporto

    private sealed record Seeded(Guid ProjectId, Guid AnalysisId, Guid Revision, JsonObject Content, DocumentRunClaim Claim);

    private static string ValidMarkdown(Seeded s) => $"# Documento\n\n- Obiettivo [fonte:{s.Content["sources"]![0]!["id"]}]";

    private ServiceProvider Services(FlakyStore? flaky = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(_database.ConnectionString));
        services.AddScoped<DocumentRunCompletion>();
        services.AddScoped<IDocumentRunStore>(sp => flaky is null
            ? sp.GetRequiredService<DocumentRunCompletion>()
            : new FlakyStoreAdapter(sp.GetRequiredService<DocumentRunCompletion>(), flaky));
        return services.BuildServiceProvider();
    }

    private DocumentWorkflow Workflow(DocumentContractTests.DocumentModel model, FlakyStore? flaky = null, AiOptions? options = null)
    {
        var services = Services(flaky);
        var agent = new DocumentAgent(
            new AgentFactory(options ?? new AiOptions { ContextWindowTokens = 32768 }, model, (t, _) => new(t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)),
            new ChatOptions { ModelId = "fake" });
        return new DocumentWorkflow(services.GetRequiredService<IServiceScopeFactory>(), agent);
    }

    private async Task<DocumentCommitResult> CommitAsync(DocumentRunClaim claim, string markdown)
    {
        using var services = Services();
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentRunStore>().CommitAsync(claim, markdown, Ct);
    }

    /// <summary>
    /// Progetto con un'analisi completata e la revisione v1 confermata (contenuto nominale PmConfirmed), il run Document Running con
    /// lease sulla v1 e, con <paramref name="laterEdit"/>, una revisione v2 successiva non confermata che il run non deve vedere.
    /// </summary>
    private async Task<Seeded> SeedAsync(string definition = ProcessingRun.DocumentWorkflowVersion, bool laterEdit = false)
    {
        TestEnvironment.RequirePostgres();
        var world = new AnalysisWorld();
        var user = (await QueryAsync("""SELECT "Id"::text FROM "AspNetUsers" LIMIT 1""")).Single();
        var conversation = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var document = Guid.NewGuid();
        var content = AnalysisFixtures.NominalAi(world);
        content["validationState"] = "PmConfirmed";
        var contentJson = content.ToJsonString();

        await ExecuteAsync($"""
            INSERT INTO "Projects" ("Id","Name","CreatedBy","CreatedAt") VALUES ('{world.ProjectId}','Documento','{user}', now());
            INSERT INTO "Conversations" ("Id","ProjectId","CreatedAt","LastSequence","MafVersion","ContextThroughSequence","ChatSessionRevision")
                VALUES ('{conversation}','{world.ProjectId}', now(), 0, '1.26.0', 0, 0);
            INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","ConversationId","SourceCutoffSequence","RequestKey","RequestedBy","RequestedAt","Status","DeadlineAt",
                "WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","FencingToken","UpdatedAt")
                VALUES ('{world.AnalysisId}','{world.ProjectId}','Analysis','{conversation}',5,'{Guid.NewGuid()}','{user}',now(),'Running',now() + interval '3 minutes','project-analysis-v2','1.26.0',1,3,1,now());
            INSERT INTO "AnalysisRevisions" ("Id","AnalysisId","Version","Origin","CreatedBy","CreatedAt","SchemaVersion","ContentJson","ValidationState","ConfirmedBy","ConfirmedAt")
                VALUES ('{revision}','{world.AnalysisId}',1,'Ai','{user}',now(),'1.0.0','{contentJson.Replace("'", "''")}','PmConfirmed','{user}',now());
            UPDATE "ProcessingRuns" SET "Status" = 'Completed', "ResultRevisionId" = '{revision}', "CompletedAt" = now() WHERE "Id" = '{world.AnalysisId}';
            UPDATE "Projects" SET "CurrentAnalysisId" = '{world.AnalysisId}' WHERE "Id" = '{world.ProjectId}';
            INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","AnalysisId","InputRevisionId","RequestKey","RequestedBy","RequestedAt","Status","StartedAt","DeadlineAt",
                "WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","LeaseOwner","LeaseUntil","FencingToken","UpdatedAt")
            VALUES ('{document}','{world.ProjectId}','Document','{world.AnalysisId}','{revision}','{Guid.NewGuid()}','{user}',now(),'Running',now(),now() + interval '3 minutes',
                '{definition}','1.26.0',1,3,'{Owner}', now() + interval '10 minutes', {Fence}, now());
            """);

        if (laterEdit)
        {
            var later = JsonNode.Parse(contentJson)!;
            later["validationState"] = "Unvalidated";
            later["goals"]![0]!["text"] = "Obiettivo successivo non confermato";
            await ExecuteAsync($"""
                INSERT INTO "AnalysisRevisions" ("Id","AnalysisId","Version","ParentRevisionId","Origin","CreatedBy","CreatedAt","SchemaVersion","ContentJson","ValidationState")
                VALUES ('{Guid.NewGuid()}','{world.AnalysisId}',2,'{revision}','PmEdit','{user}',now(),'1.0.0','{later.ToJsonString().Replace("'", "''")}','Unvalidated');
                """);
        }

        return new Seeded(world.ProjectId, world.AnalysisId, revision, content, new DocumentRunClaim(document, Owner, Fence));
    }

    /// <summary>Una bozza riuscita precedente dello stesso progetto (run Document terminato) già indicata da LatestDraftId.</summary>
    private async Task<Guid> SeedPreviousDraftAsync(Seeded s)
    {
        var user = (await QueryAsync("""SELECT "Id"::text FROM "AspNetUsers" LIMIT 1""")).Single();
        var run = Guid.NewGuid();
        var draft = Guid.NewGuid();
        // il run corrente (Running) occupa l'indice «uno attivo per progetto»: la bozza precedente appartiene a un run già Completed
        await ExecuteAsync($"""
            UPDATE "ProcessingRuns" SET "Status" = 'Failed', "ErrorCode" = 'x' WHERE "Id" = '{s.Claim.RunId}';
            INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","AnalysisId","InputRevisionId","RequestKey","RequestedBy","RequestedAt","Status","DeadlineAt",
                "WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","FencingToken","UpdatedAt")
            VALUES ('{run}','{s.ProjectId}','Document','{s.AnalysisId}','{s.Revision}','{Guid.NewGuid()}','{user}',now() - interval '1 hour','Running',now() + interval '3 minutes','project-document-v1','1.26.0',1,3,1,now());
            INSERT INTO "Drafts" ("Id","ProjectId","ProcessingRunId","AnalysisRevisionId","Markdown","CreatedAt","Status","ApprovalStatus")
                VALUES ('{draft}','{s.ProjectId}','{run}','{s.Revision}','# Bozza precedente', now(), 'Proposed', 'NotApproved');
            UPDATE "ProcessingRuns" SET "Status" = 'Completed', "DraftId" = '{draft}', "CompletedAt" = now() WHERE "Id" = '{run}';
            UPDATE "Projects" SET "LatestDraftId" = '{draft}' WHERE "Id" = '{s.ProjectId}';
            UPDATE "ProcessingRuns" SET "Status" = 'Running', "ErrorCode" = NULL WHERE "Id" = '{s.Claim.RunId}';
            """);
        return draft;
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
    private sealed class FlakyStore
    {
        public bool FailNextCommit { get; set; }
    }

    private sealed class FlakyStoreAdapter(DocumentRunCompletion inner, FlakyStore flaky) : IDocumentRunStore
    {
        public Task<DocumentRunInput> LoadInputAsync(Guid runId, CancellationToken ct) => inner.LoadInputAsync(runId, ct);

        public Task<DocumentCommitResult> CommitAsync(DocumentRunClaim claim, string markdown, CancellationToken ct)
        {
            if (flaky.FailNextCommit)
            {
                flaky.FailNextCommit = false;
                throw new IOException("guasto iniettato prima del commit");
            }

            return inner.CommitAsync(claim, markdown, ct);
        }

        public Task<bool> FailAsync(DocumentRunClaim claim, string errorCode, string errorMessage, CancellationToken ct) => inner.FailAsync(claim, errorCode, errorMessage, ct);

        public Task<bool> RecordStageAsync(DocumentRunClaim claim, string stage, CancellationToken ct) => inner.RecordStageAsync(claim, stage, ct);
    }
}
