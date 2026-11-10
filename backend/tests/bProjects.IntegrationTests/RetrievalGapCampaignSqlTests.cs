using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T040 — PostgreSQL reale ma VUOTO (schema migrato, nessun dato, nessun DGX): ogni interrogazione della prova reale si esegue davvero, quindi tabelle, colonne e tipi esistono. Rileva errori di nome
/// (es. una colonna inesistente) prima della campagna.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class RetrievalGapCampaignSqlTests : ArchiveTestBase
{
    [Fact]
    public async Task Every_sql_statement_of_the_real_campaign_runs_against_the_migrated_schema()
    {
        _ = App;   // Skipped senza PostgreSQL di test
        Assert.Equal(14, CampaignSql.All.Count());
        await using var connection = await OpenConnection();
        foreach (var sql in CampaignSql.All)
        {
            await using var command = new NpgsqlCommand(sql, connection);

            if (sql.Contains("@p", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue("p", Guid.NewGuid());
            }

            if (sql.Contains("@r", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue("r", Guid.NewGuid());
            }

            if (sql.Contains("@s", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue("s", "gap");
            }

            if (sql.Contains("@d", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue("d", new[] { Guid.NewGuid() });
            }

            var ex = await Record.ExceptionAsync(async () =>
            {
                await using var reader = await command.ExecuteReaderAsync(Ct);
                while (await reader.ReadAsync(Ct))
                {
                }
            });
            Assert.True(ex is null, $"SQL non eseguibile: {sql}\n{ex?.Message}");
        }
    }
}
