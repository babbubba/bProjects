using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T025 — ProcessingRun/AnalysisRevision su PostgreSQL reale: vincoli di Kind, idempotenza, run attivo unico,
/// prima revisione AI unica, FK Project.CurrentAnalysisId e upgrade da T011 con dati esistenti invariati.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class AnalysisSchemaTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private const string PreviousMigration = "20261028025228_ChatTurnDispatchIndexes";

    private const string EmptyJson = "{}";

    private ApiHarness? _app;

    private sealed record Seed(string User, Guid Project, Guid Conversation);

    public async ValueTask InitializeAsync()
    {
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            _app = await ApiHarness.StartAsync(database.ConnectionString);
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
    public async Task Processing_run_enforces_kind_fields_idempotency_and_single_active_run_per_project()
    {
        TestEnvironment.RequirePostgres();
        var cs = database.ConnectionString;
        var seed = await SeedProject(cs);

        var analysis = Guid.NewGuid();
        await Execute(AnalysisRun(seed, analysis, Guid.NewGuid(), "Queued"), cs);

        // un solo run formale attivo per progetto, di qualsiasi Kind
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => Execute(AnalysisRun(seed, Guid.NewGuid(), Guid.NewGuid(), "Running"), cs))).SqlState);

        // terminato il primo, il secondo è ammesso; stessa (Progetto, Kind, RequestKey) è rifiutata
        await Execute($"UPDATE \"ProcessingRuns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x' WHERE \"Id\" = '{analysis}'", cs);
        var key = Guid.NewGuid();
        await Execute(AnalysisRun(seed, Guid.NewGuid(), key, "Failed", error: true), cs);
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => Execute(AnalysisRun(seed, Guid.NewGuid(), key, "Failed", error: true), cs))).SqlState);

        // Analysis richiede ConversationId e cutoff; Document richiede analisi e revisione di input
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Execute(
            RunInsert(seed.Project, Guid.NewGuid(), "Analysis", Guid.NewGuid(), "Failed", extraColumns: "", extraValues: "", error: true), cs))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Execute(
            RunInsert(seed.Project, Guid.NewGuid(), "Document", Guid.NewGuid(), "Failed", extraColumns: "", extraValues: "", error: true), cs))).SqlState);
        // Completed senza risultato e Failed senza errore
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Execute(AnalysisRun(seed, Guid.NewGuid(), Guid.NewGuid(), "Completed"), cs))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Execute(AnalysisRun(seed, Guid.NewGuid(), Guid.NewGuid(), "Failed"), cs))).SqlState);
    }

    [Fact]
    public async Task Analysis_revision_enforces_single_ai_revision_versions_and_confirmation_fields()
    {
        TestEnvironment.RequirePostgres();
        var cs = database.ConnectionString;
        var seed = await SeedProject(cs);
        var analysis = Guid.NewGuid();
        await Execute(AnalysisRun(seed, analysis, Guid.NewGuid(), "Failed", error: true), cs);

        var first = Guid.NewGuid();
        await Execute(Revision(seed, analysis, first, 1, "Ai", null, "Unvalidated"), cs);

        // la prima revisione AI è univoca per analisi (replay non ne crea una seconda)
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => Execute(Revision(seed, analysis, Guid.NewGuid(), 1, "Ai", null, "Unvalidated"), cs))).SqlState);
        // Version >= 1 (e univoca per analisi, verificato in fondo); Ai non ha parent; PmEdit ha parent e Version >= 2
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Execute(Revision(seed, analysis, Guid.NewGuid(), 1, "PmEdit", first, "Unvalidated"), cs))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Execute(Revision(seed, analysis, Guid.NewGuid(), 0, "PmEdit", first, "Unvalidated"), cs))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Execute(Revision(seed, analysis, Guid.NewGuid(), 2, "PmEdit", null, "Unvalidated"), cs))).SqlState);
        // coerenza stato di conferma / metadati
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Execute(Revision(seed, analysis, Guid.NewGuid(), 2, "PmEdit", first, "PmConfirmed"), cs))).SqlState);
        await Execute(Revision(seed, analysis, Guid.NewGuid(), 2, "PmEdit", first, "PmConfirmed", confirmed: true), cs);
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => Execute(Revision(seed, analysis, Guid.NewGuid(), 2, "PmEdit", first, "Unvalidated"), cs))).SqlState);
    }

    [Fact]
    public async Task Project_current_analysis_must_reference_an_existing_processing_run()
    {
        TestEnvironment.RequirePostgres();
        var cs = database.ConnectionString;
        var seed = await SeedProject(cs);
        var analysis = Guid.NewGuid();
        await Execute(AnalysisRun(seed, analysis, Guid.NewGuid(), "Failed", error: true), cs);

        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => Execute(
            $"UPDATE \"Projects\" SET \"CurrentAnalysisId\" = '{Guid.NewGuid()}' WHERE \"Id\" = '{seed.Project}'", cs))).SqlState);
        await Execute($"UPDATE \"Projects\" SET \"CurrentAnalysisId\" = '{analysis}' WHERE \"Id\" = '{seed.Project}'", cs);
        // LatestDraftId resta senza FK fino a T038
        await Execute($"UPDATE \"Projects\" SET \"LatestDraftId\" = '{Guid.NewGuid()}' WHERE \"Id\" = '{seed.Project}'", cs);
    }

    [Fact]
    public async Task Upgrading_from_the_chat_schema_keeps_existing_projects_and_messages()
    {
        var admin = TestEnvironment.RequirePostgres();
        var name = $"bprojects_t025_{Guid.NewGuid():N}";
        var cs = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        await Execute($"CREATE DATABASE \"{name}\"", admin);
        try
        {
            await using var harness = await ApiHarness.StartAsync(cs, prepare: false);
            using var scope = harness.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("ProcessingRuns", await Query("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'", cs));

            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            var conversation = Guid.NewGuid();
            await Execute($"""
                INSERT INTO "AspNetUsers" ("Id","AccessFailedCount","EmailConfirmed","LockoutEnabled","PhoneNumberConfirmed","TwoFactorEnabled") VALUES ('{user}',0,false,false,false,false);
                INSERT INTO "Projects" ("Id","Name","CreatedBy","CreatedAt") VALUES ('{project}','Esistente','{user}', now());
                INSERT INTO "Conversations" ("Id","ProjectId","CreatedAt","LastSequence","MafVersion","ContextThroughSequence","ChatSessionRevision")
                    VALUES ('{conversation}','{project}', now(), 1, '1.26.0', 0, 0);
                INSERT INTO "ConversationMessages" ("Id","ConversationId","Sequence","Role","Body","CreatedAt","ClientMessageId","AuthorId")
                    VALUES ('{Guid.NewGuid()}','{conversation}', 1, 'User', 'originale', now(), '{Guid.NewGuid()}', '{user}');
                """, cs);

            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            Assert.Equal(["Esistente"], await Query("SELECT \"Name\" FROM \"Projects\"", cs));
            Assert.Equal(["originale"], await Query("SELECT \"Body\" FROM \"ConversationMessages\"", cs));
            Assert.Equal([""], await Query("SELECT COALESCE(\"CurrentAnalysisId\"::text, '') FROM \"Projects\"", cs));
            var tables = await Query("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'", cs);
            Assert.Contains("ProcessingRuns", tables);
            Assert.Contains("AnalysisRevisions", tables);
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Execute($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
        }
    }

    private static string AnalysisRun(Seed seed, Guid id, Guid key, string status, bool error = false) =>
        RunInsert(seed.Project, id, "Analysis", key, status,
            extraColumns: ",\"ConversationId\",\"SourceCutoffSequence\"", extraValues: $",'{seed.Conversation}',1", error: error, user: seed.User);

    private static string RunInsert(Guid project, Guid id, string kind, Guid key, string status, string extraColumns, string extraValues, bool error, string? user = null) =>
        $"""
        INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","RequestKey","RequestedBy","RequestedAt","Status","DeadlineAt","WorkflowDefinitionVersion","MafVersion",
            "AttemptCount","MaxAttempts","FencingToken","UpdatedAt"{(error ? ",\"ErrorCode\"" : "")}{extraColumns})
        VALUES ('{id}','{project}','{kind}','{key}','{user ?? Guid.NewGuid().ToString()}',now(),'{status}',now() + interval '3 minutes','project-analysis-v2','1.26.0',0,3,0,now(){(error ? ",'model_failure'" : "")}{extraValues})
        """;

    private static string Revision(Seed seed, Guid analysis, Guid id, int version, string origin, Guid? parent, string state, bool confirmed = false) =>
        $"""
        INSERT INTO "AnalysisRevisions" ("Id","AnalysisId","Version","ParentRevisionId","Origin","CreatedBy","CreatedAt","SchemaVersion","ContentJson","ValidationState","ConfirmedBy","ConfirmedAt")
        VALUES ('{id}','{analysis}',{version},{(parent is null ? "NULL" : $"'{parent}'")},'{origin}','{seed.User}',now(),'1.0.0','{EmptyJson}','{state}',{(confirmed ? $"'{seed.User}'" : "NULL")},{(confirmed ? "now()" : "NULL")})
        """;

    private static async Task<Seed> SeedProject(string connectionString)
    {
        var user = (await Query("SELECT \"Id\"::text FROM \"AspNetUsers\" LIMIT 1", connectionString)).Single();
        var project = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        await Execute($"""
            INSERT INTO "Projects" ("Id","Name","CreatedBy","CreatedAt") VALUES ('{project}','Analisi','{user}', now());
            INSERT INTO "Conversations" ("Id","ProjectId","CreatedAt","LastSequence","MafVersion","ContextThroughSequence","ChatSessionRevision")
                VALUES ('{conversation}','{project}', now(), 0, '1.26.0', 0, 0);
            """, connectionString);
        return new Seed(user, project, conversation);
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
