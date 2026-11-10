using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T150 — controllo offline dello schema: ogni SQL della prova reale T150 (<see cref="GapStageRealSql"/>) si esegue su PostgreSQL migrato e VUOTO, senza DGX. Rileva nomi di colonna errati
/// (es. <c>"Payload"</c> al posto di <c>"PayloadJson"</c>) prima di una campagna.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class GapStageRealSqlTests : ArchiveTestBase
{
    [Fact]
    public async Task Every_sql_statement_of_the_t150_real_test_runs_against_the_migrated_schema()
    {
        _ = App;   // Skipped senza PostgreSQL di test
        Assert.Equal(5, GapStageRealSql.All.Count());
        await using var connection = await OpenConnection();
        foreach (var sql in GapStageRealSql.All)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("r", Guid.NewGuid());
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

    [Fact]
    public void The_payload_column_is_payload_json_not_payload()
    {
        Assert.Contains("\"PayloadJson\"", GapStageRealSql.StagePayload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Payload\"", GapStageRealSql.StagePayload, StringComparison.Ordinal);
    }
}
