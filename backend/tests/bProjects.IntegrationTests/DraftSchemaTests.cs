using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T038 — Draft su PostgreSQL reale: una bozza per run Document, legata al progetto e alla revisione di input del run, Markdown non
/// vuoto, Proposed/NotApproved; FK Project.LatestDraftId e ProcessingRun.DraftId; database da zero (catena completa) e upgrade da
/// T028 con progetti, chat, analisi e checkpoint precedenti invariati.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DraftSchemaTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private const string PreviousMigration = "20261028083612_WorkflowCheckpoints"; // T028: ultima prima di Draft

    private const string EmptyJson = "{}";
    private const string KvJson = "{\"k\":\"v\"}";
    private const string XJson = "{\"x\":1}";

    private ApiHarness? _app;

    private sealed record World(string User, Guid Project, Guid Conversation, Guid Analysis, Guid Revision);

    public async ValueTask InitializeAsync()
    {
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            _app = await ApiHarness.StartAsync(database.ConnectionString); // catena completa da zero
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task From_scratch_the_full_migration_chain_matches_the_model_and_creates_the_drafts_table()
    {
        TestEnvironment.RequirePostgres();
        var cs = database.ConnectionString;
        using var scope = _app!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("Drafts", await Query("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'", cs));
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_draft_belongs_to_one_document_run_of_the_same_project_and_to_the_run_input_revision()
    {
        TestEnvironment.RequirePostgres();
        var cs = database.ConnectionString;
        var w = await SeedWorld(cs);
        var run = await InsertDocumentRun(cs, w, w.Revision);
        var draft = Guid.NewGuid();

        await Execute(DraftInsert(draft, w.Project, run, w.Revision), cs);

        // una sola bozza per run
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => Execute(DraftInsert(Guid.NewGuid(), w.Project, run, w.Revision), cs))).SqlState);
        // il run di Analysis non può avere una bozza (Kind=Document)
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => Execute(DraftInsert(Guid.NewGuid(), w.Project, w.Analysis, w.Revision), cs))).SqlState);
        // la revisione deve coincidere con InputRevisionId del run, anche se esiste ed è della stessa analisi
        var other = Guid.NewGuid();
        await Execute(RevisionInsert(w, other, 2), cs);
        var secondRun = await InsertDocumentRun(cs, w, other, terminal: true);
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => Execute(DraftInsert(Guid.NewGuid(), w.Project, secondRun, w.Revision), cs))).SqlState);
        // progetto della bozza = progetto del run
        var foreign = await InsertProject(cs, w.User);
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => Execute(DraftInsert(Guid.NewGuid(), foreign, secondRun, other), cs))).SqlState);
        // run e revisione devono esistere
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => Execute(DraftInsert(Guid.NewGuid(), w.Project, Guid.NewGuid(), w.Revision), cs))).SqlState);
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => Execute(DraftInsert(Guid.NewGuid(), w.Project, secondRun, Guid.NewGuid()), cs))).SqlState);
        Assert.Equal(1, long.Parse((await Query($"SELECT count(*)::text FROM \"Drafts\" WHERE \"ProjectId\" = '{w.Project}'", cs)).Single()));
    }

    [Fact]
    public async Task A_draft_has_non_blank_markdown_and_only_the_proposed_not_approved_states_and_no_length_limit()
    {
        TestEnvironment.RequirePostgres();
        var cs = database.ConnectionString;
        var w = await SeedWorld(cs);
        var run = await InsertDocumentRun(cs, w, w.Revision);

        foreach (var blank in new[] { "", "   ", "\n\t " })
        {
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Execute(DraftInsert(Guid.NewGuid(), w.Project, run, w.Revision, markdown: blank), cs))).SqlState);
        }

        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Execute(DraftInsert(Guid.NewGuid(), w.Project, run, w.Revision, status: "Approved"), cs))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Execute(DraftInsert(Guid.NewGuid(), w.Project, run, w.Revision, approval: "Approved"), cs))).SqlState);

        // nessun limite di parole: un documento molto lungo è ammesso
        await Execute(DraftInsert(Guid.NewGuid(), w.Project, run, w.Revision, markdown: string.Join(' ', Enumerable.Repeat("parola", 50_000))), cs);
        Assert.Equal(["Proposed|NotApproved"], await Query($"SELECT \"Status\" || '|' || \"ApprovalStatus\" FROM \"Drafts\" WHERE \"ProjectId\" = '{w.Project}'", cs));
    }

    [Fact]
    public async Task Latest_draft_and_run_draft_references_must_exist_and_a_referenced_draft_cannot_be_deleted()
    {
        TestEnvironment.RequirePostgres();
        var cs = database.ConnectionString;
        var w = await SeedWorld(cs);
        var run = await InsertDocumentRun(cs, w, w.Revision);
        var draft = Guid.NewGuid();
        await Execute(DraftInsert(draft, w.Project, run, w.Revision), cs);

        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => Execute(
            $"UPDATE \"Projects\" SET \"LatestDraftId\" = '{Guid.NewGuid()}' WHERE \"Id\" = '{w.Project}'", cs))).SqlState);
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => Execute(
            $"UPDATE \"ProcessingRuns\" SET \"DraftId\" = '{Guid.NewGuid()}' WHERE \"Id\" = '{run}'", cs))).SqlState);

        // commit atomico come farà il workflow: run Completed con DraftId + LatestDraftId
        await Execute($"""
            UPDATE "ProcessingRuns" SET "Status" = 'Completed', "DraftId" = '{draft}', "CompletedAt" = now() WHERE "Id" = '{run}';
            UPDATE "Projects" SET "LatestDraftId" = '{draft}' WHERE "Id" = '{w.Project}';
            """, cs);
        Assert.Equal([draft.ToString()], await Query($"SELECT \"LatestDraftId\"::text FROM \"Projects\" WHERE \"Id\" = '{w.Project}'", cs));
        // ON DELETE RESTRICT: SQLSTATE 23001 (restrict_violation)
        Assert.Equal("23001", (await Assert.ThrowsAsync<PostgresException>(() => Execute($"DELETE FROM \"Drafts\" WHERE \"Id\" = '{draft}'", cs))).SqlState);
        // una Completed di tipo Document senza bozza resta impossibile
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Execute(
            $"UPDATE \"ProcessingRuns\" SET \"DraftId\" = NULL WHERE \"Id\" = '{run}'", cs))).SqlState);
    }

    [Fact]
    public async Task Upgrading_from_the_workflow_checkpoint_schema_applies_the_whole_chain_and_keeps_projects_chat_analyses_and_checkpoints()
    {
        var admin = TestEnvironment.RequirePostgres();
        var name = $"bprojects_t038_{Guid.NewGuid():N}";
        var cs = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        await Execute($"CREATE DATABASE \"{name}\"", admin);
        try
        {
            await using var harness = await ApiHarness.StartAsync(cs, prepare: false);
            using var scope = harness.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("Drafts", await Query("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'", cs));

            var user = Guid.NewGuid().ToString();
            var project = Guid.NewGuid();
            var conversation = Guid.NewGuid();
            var analysis = Guid.NewGuid();
            var revision = Guid.NewGuid();
            await Execute($"""
                INSERT INTO "AspNetUsers" ("Id","AccessFailedCount","EmailConfirmed","LockoutEnabled","PhoneNumberConfirmed","TwoFactorEnabled") VALUES ('{user}',0,false,false,false,false);
                INSERT INTO "Projects" ("Id","Name","CreatedBy","CreatedAt") VALUES ('{project}','Esistente','{user}', now());
                INSERT INTO "Conversations" ("Id","ProjectId","CreatedAt","LastSequence","MafVersion","ContextThroughSequence","ChatSessionRevision")
                    VALUES ('{conversation}','{project}', now(), 1, '1.26.0', 0, 0);
                INSERT INTO "ConversationMessages" ("Id","ConversationId","Sequence","Role","Body","CreatedAt","ClientMessageId","AuthorId")
                    VALUES ('{Guid.NewGuid()}','{conversation}', 1, 'User', 'originale', now(), '{Guid.NewGuid()}', '{user}');
                INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","ConversationId","SourceCutoffSequence","RequestKey","RequestedBy","RequestedAt","Status","DeadlineAt",
                    "WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","FencingToken","UpdatedAt","ErrorCode")
                    VALUES ('{analysis}','{project}','Analysis','{conversation}',1,'{Guid.NewGuid()}','{user}',now(),'Failed',now() + interval '3 minutes','project-analysis-v2','1.26.0',1,3,1,now(),'model_failure');
                INSERT INTO "AnalysisRevisions" ("Id","AnalysisId","Version","Origin","CreatedBy","CreatedAt","SchemaVersion","ContentJson","ValidationState")
                    VALUES ('{revision}','{analysis}',1,'Ai','{user}',now(),'1.0.0','{KvJson}','Unvalidated');
                INSERT INTO "WorkflowCheckpoints" ("Id","ProcessingRunId","WorkflowSessionId","CheckpointKey","CheckpointInfo","Payload","CommitSequence","CreatedAt","MafVersion","WorkflowDefinitionVersion")
                    VALUES ('{Guid.NewGuid()}','{analysis}','s1','c1','{EmptyJson}','{XJson}',1,now(),'1.26.0','project-analysis-v2');
                UPDATE "Projects" SET "CurrentAnalysisId" = '{analysis}' WHERE "Id" = '{project}';
                """, cs);

            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            Assert.Equal(["Esistente"], await Query("SELECT \"Name\" FROM \"Projects\"", cs));
            Assert.Equal(["originale"], await Query("SELECT \"Body\" FROM \"ConversationMessages\"", cs));
            Assert.Equal([analysis.ToString()], await Query("SELECT \"CurrentAnalysisId\"::text FROM \"Projects\"", cs));
            Assert.Equal([""], await Query("SELECT COALESCE(\"LatestDraftId\"::text, '') FROM \"Projects\"", cs));
            Assert.Equal(["Failed|model_failure"], await Query("SELECT \"Status\" || '|' || \"ErrorCode\" FROM \"ProcessingRuns\"", cs));
            Assert.Equal(["{\"k\": \"v\"}"], await Query("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\"", cs));
            Assert.Equal(["s1|c1"], await Query("SELECT \"WorkflowSessionId\" || '|' || \"CheckpointKey\" FROM \"WorkflowCheckpoints\"", cs));
            Assert.Equal(["0"], await Query("SELECT count(*)::text FROM \"Drafts\"", cs));
            Assert.False(db.Database.HasPendingModelChanges());

            // la catena completa è applicata e il nuovo schema è utilizzabile sui dati preesistenti
            var run = await InsertDocumentRun(cs, new World(user, project, conversation, analysis, revision), revision);
            await Execute(DraftInsert(Guid.NewGuid(), project, run, revision), cs);
            Assert.Equal(["1"], await Query("SELECT count(*)::text FROM \"Drafts\"", cs));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Execute($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
        }
    }

    // ---------------------------------------------------------------- supporto

    private static string DraftInsert(Guid id, Guid project, Guid run, Guid revision, string markdown = "# Bozza\n\nTesto.", string status = "Proposed", string approval = "NotApproved") =>
        $"""
        INSERT INTO "Drafts" ("Id","ProjectId","ProcessingRunId","AnalysisRevisionId","Markdown","CreatedAt","Status","ApprovalStatus")
        VALUES ('{id}','{project}','{run}','{revision}','{markdown.Replace("'", "''")}', now(), '{status}', '{approval}')
        """;

    private static string RevisionInsert(World w, Guid id, int version) =>
        $"""
        INSERT INTO "AnalysisRevisions" ("Id","AnalysisId","Version","ParentRevisionId","Origin","CreatedBy","CreatedAt","SchemaVersion","ContentJson","ValidationState","ConfirmedBy","ConfirmedAt")
        VALUES ('{id}','{w.Analysis}',{version},'{w.Revision}','PmEdit','{w.User}',now(),'1.0.0','{EmptyJson}','PmConfirmed','{w.User}',now())
        """;

    /// <summary>Run Document con la revisione di input data; con <paramref name="terminal"/> è Failed (lascia libero l'indice del run attivo).</summary>
    private static async Task<Guid> InsertDocumentRun(string cs, World w, Guid inputRevision, bool terminal = false)
    {
        var id = Guid.NewGuid();
        await Execute($"""
            INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","AnalysisId","InputRevisionId","RequestKey","RequestedBy","RequestedAt","Status","DeadlineAt",
                "WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","FencingToken","UpdatedAt"{(terminal ? ",\"ErrorCode\"" : "")})
            VALUES ('{id}','{w.Project}','Document','{w.Analysis}','{inputRevision}','{Guid.NewGuid()}','{w.User}',now(),'{(terminal ? "Failed" : "Running")}',now() + interval '3 minutes',
                'project-document-v1','1.26.0',0,3,0,now(){(terminal ? ",'model_failure'" : "")})
            """, cs);
        return id;
    }

    private static async Task<Guid> InsertProject(string cs, string user)
    {
        var project = Guid.NewGuid();
        await Execute($"""
            INSERT INTO "Projects" ("Id","Name","CreatedBy","CreatedAt") VALUES ('{project}','Altro','{user}', now());
            INSERT INTO "Conversations" ("Id","ProjectId","CreatedAt","LastSequence","MafVersion","ContextThroughSequence","ChatSessionRevision")
                VALUES ('{Guid.NewGuid()}','{project}', now(), 0, '1.26.0', 0, 0);
            """, cs);
        return project;
    }

    /// <summary>Progetto con un'analisi terminata (Failed) e la sua revisione AI v1.</summary>
    private static async Task<World> SeedWorld(string cs)
    {
        var user = (await Query("SELECT \"Id\"::text FROM \"AspNetUsers\" LIMIT 1", cs)).Single();
        var project = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var analysis = Guid.NewGuid();
        var revision = Guid.NewGuid();
        await Execute($"""
            INSERT INTO "Projects" ("Id","Name","CreatedBy","CreatedAt") VALUES ('{project}','Bozza','{user}', now());
            INSERT INTO "Conversations" ("Id","ProjectId","CreatedAt","LastSequence","MafVersion","ContextThroughSequence","ChatSessionRevision")
                VALUES ('{conversation}','{project}', now(), 0, '1.26.0', 0, 0);
            INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","ConversationId","SourceCutoffSequence","RequestKey","RequestedBy","RequestedAt","Status","DeadlineAt",
                "WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","FencingToken","UpdatedAt","ErrorCode")
                VALUES ('{analysis}','{project}','Analysis','{conversation}',1,'{Guid.NewGuid()}','{user}',now(),'Failed',now() + interval '3 minutes','project-analysis-v2','1.26.0',1,3,1,now(),'model_failure');
            INSERT INTO "AnalysisRevisions" ("Id","AnalysisId","Version","Origin","CreatedBy","CreatedAt","SchemaVersion","ContentJson","ValidationState","ConfirmedBy","ConfirmedAt")
                VALUES ('{revision}','{analysis}',1,'Ai','{user}',now(),'1.0.0','{EmptyJson}','PmConfirmed','{user}',now());
            """, cs);
        return new World(user, project, conversation, analysis, revision);
    }

    private static async Task<List<string>> Query(string sql, string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task Execute(string sql, string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
