using bProjects.App.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T011 — schema PostgreSQL reale: tabelle, vincoli di unicità, FK differite, nessuna tabella futura,
/// creazione da zero e upgrade dalla migrazione iniziale senza perdere gli account Identity.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ProjectSchemaTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private const string InitialMigration = "20261028014623_InitialCreate";

    private ApiHarness? _app;

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
    public async Task Migrated_from_scratch_the_conversation_tables_exist()
    {
        TestEnvironment.RequirePostgres();

        var tables = await Query("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'", database.ConnectionString);

        Assert.Contains("Projects", tables);
        Assert.Contains("Conversations", tables);
        Assert.Contains("ConversationMessages", tables);
        Assert.Contains("ChatTurns", tables);
    }

    [Fact]
    public async Task No_table_of_future_features_is_created()
    {
        TestEnvironment.RequirePostgres();

        var tables = await Query("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'", database.ConnectionString);

        Assert.DoesNotContain(tables, t => t.Contains("Analys", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Draft", StringComparison.OrdinalIgnoreCase)
            || t.Contains("ProcessingRun", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Knowledge", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Conversations", "ProjectId")]
    [InlineData("ConversationMessages", "ConversationId,Sequence")]
    [InlineData("ConversationMessages", "ConversationId,ClientMessageId")]
    [InlineData("ConversationMessages", "ReplyToMessageId")]
    [InlineData("ChatTurns", "TriggerMessageId")]
    public async Task Normative_unique_constraints_exist(string table, string columns)
    {
        TestEnvironment.RequirePostgres();

        var unique = await UniqueIndexColumns(table);

        Assert.Contains(columns, unique);
    }

    [Fact]
    public async Task Project_references_to_future_tables_are_nullable_uuid_columns_without_foreign_keys()
    {
        TestEnvironment.RequirePostgres();

        var columns = await Query(
            "SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns WHERE table_name = 'Projects' AND column_name IN ('CurrentAnalysisId','LatestDraftId')",
            database.ConnectionString);
        var foreignKeyColumns = await Query(
            """
            SELECT a.attname FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(c.conkey)
            WHERE c.contype = 'f' AND t.relname = 'Projects'
            """,
            database.ConnectionString);

        Assert.Equivalent(new[] { "CurrentAnalysisId:uuid:YES", "LatestDraftId:uuid:YES" }, columns);
        Assert.DoesNotContain("CurrentAnalysisId", foreignKeyColumns);
        Assert.DoesNotContain("LatestDraftId", foreignKeyColumns);
    }

    [Fact]
    public async Task Relations_reference_existing_tables_only()
    {
        TestEnvironment.RequirePostgres();

        var foreignKeys = await Query(
            """
            SELECT t.relname || '->' || r.relname FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid JOIN pg_class r ON r.oid = c.confrelid
            WHERE c.contype = 'f' AND t.relname IN ('Projects','Conversations','ConversationMessages','ChatTurns')
            """,
            database.ConnectionString);

        Assert.Contains("Conversations->Projects", foreignKeys);
        Assert.Contains("ConversationMessages->Conversations", foreignKeys);
        Assert.Contains("ChatTurns->Conversations", foreignKeys);
        Assert.Contains("ChatTurns->ConversationMessages", foreignKeys);
        Assert.Contains("ConversationMessages->ChatTurns", foreignKeys);
        Assert.Contains("Projects->AspNetUsers", foreignKeys);
        Assert.Contains("ConversationMessages->AspNetUsers", foreignKeys);
    }

    [Fact]
    public async Task The_model_has_no_pending_changes_with_respect_to_the_migrations()
    {
        TestEnvironment.RequirePostgres();
        using var scope = _app!.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Upgrading_from_the_initial_migration_keeps_the_identity_accounts()
    {
        var admin = TestEnvironment.RequirePostgres();
        var name = $"bprojects_upgrade_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        await Execute($"CREATE DATABASE \"{name}\"", admin);
        try
        {
            await using (var initial = await ApiHarness.StartAsync(connectionString, prepare: false))
            {
                using var scope = initial.Services.CreateScope();
                var migrator = scope.ServiceProvider.GetRequiredService<AppDbContext>().GetService<IMigrator>();
                await migrator.MigrateAsync(InitialMigration, TestContext.Current.CancellationToken);
                await Bootstrap(initial);
                Assert.DoesNotContain("Projects", await Query("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'", connectionString));
            }

            await using var upgraded = await ApiHarness.StartAsync(connectionString, prepare: false);
            using (var scope = upgraded.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync(TestContext.Current.CancellationToken);
                var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
                Assert.NotNull(await users.FindByNameAsync(TestApp.PmUser));
                Assert.NotNull(await users.FindByNameAsync(TestApp.SecondPmUser));
            }

            using var client = await upgraded.LoginAsync();
            Assert.Contains("Projects", await Query("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'", connectionString));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Execute($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
        }
    }

    private static Task Bootstrap(ApiHarness harness) =>
        bProjects.App.Identity.AccountBootstrap.RunAsync(harness.Services, harness.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>());

    private async Task<List<string>> UniqueIndexColumns(string table)
    {
        var rows = await Query(
            $"""
            SELECT string_agg(a.attname, ',' ORDER BY k.ord)
            FROM pg_index x
            JOIN pg_class t ON t.oid = x.indrelid
            CROSS JOIN LATERAL unnest(x.indkey) WITH ORDINALITY AS k(attnum, ord)
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            WHERE x.indisunique AND t.relname = '{table}'
            GROUP BY x.indexrelid
            """,
            database.ConnectionString);
        return rows;
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
