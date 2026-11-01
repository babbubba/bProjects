using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// Database PostgreSQL isolato per classe di test: creato con nome univoco all'inizializzazione
/// ed eliminato al termine, così le prove non condividono dati.
/// </summary>
public sealed class PostgresDatabaseFixture : IAsyncLifetime
{
    private string? _adminConnectionString;
    private string? _databaseName;

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _adminConnectionString = TestEnvironment.PostgresAdminConnectionString;
        if (_adminConnectionString is null)
        {
            return; // I test dipendenti chiamano TestEnvironment.RequirePostgres() e risultano Skipped.
        }

        _databaseName = $"bprojects_test_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", connection);
        await create.ExecuteNonQueryAsync();

        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = _databaseName }.ConnectionString;
        MafDiagnostics.AnnounceDatabase(ConnectionString);   // snapshot ed export delle evidenze se il test usa il modello reale
    }

    public async ValueTask DisposeAsync()
    {
        if (_adminConnectionString is null || _databaseName is null)
        {
            return;
        }

        await MafDiagnostics.FinalizeAsync(ConnectionString);   // export delle evidenze PRIMA di eliminare il database
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }
}
