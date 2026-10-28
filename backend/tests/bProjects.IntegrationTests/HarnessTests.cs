using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>Verifica l'avvio dell'applicazione reale in processo, senza servizi esterni.</summary>
public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Health_returns_ok_without_authentication()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>Prova del solo harness PostgreSQL: database reale e isolato. Skipped senza BPROJECTS_TEST_POSTGRES.</summary>
[Trait("Category", TestCategories.Integration)]
public sealed class PostgresHarnessTests : IClassFixture<PostgresDatabaseFixture>
{
    private readonly PostgresDatabaseFixture _database;

    public PostgresHarnessTests(PostgresDatabaseFixture database) => _database = database;

    [Fact]
    public async Task Isolated_database_accepts_queries()
    {
        TestEnvironment.RequirePostgres();

        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT current_database()", connection);

        var name = (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("bprojects_test_", name);
    }
}

/// <summary>Prova del solo harness DGX: raggiungibilità reale dell'endpoint interno. Skipped senza BPROJECTS_TEST_DGX_ENDPOINT.</summary>
[Trait("Category", TestCategories.MafReal)]
public sealed class DgxHarnessTests
{
    [Fact]
    public async Task Internal_model_endpoint_lists_models()
    {
        var endpoint = TestEnvironment.RequireDgx();
        using var client = new HttpClient { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(30) };

        var response = await client.GetAsync("models", TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, $"GET {endpoint}models → {(int)response.StatusCode}");
    }
}
