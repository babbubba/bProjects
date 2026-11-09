using System.Data;
using System.Data.Common;
using System.Globalization;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace bProjects.App.Retrieval;

/// <summary>
/// SQL esplicito dell'indice derivato (003): la colonna <c>Embedding vector(1024)</c> non è mappata in EF, quindi scritture e ricerche passano da qui con parametri
/// (mai concatenazione di testi) sulla connessione del contesto e nella sua eventuale transazione corrente.
/// </summary>
internal static class RetrievalSql
{
    /// <summary>Letterale pgvector <c>[x1,x2,...]</c> con cultura invariante (round-trip «R»).</summary>
    public static string VectorLiteral(IReadOnlyList<float> vector) =>
        "[" + string.Join(',', vector.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";

    public static async Task<int> ExecuteAsync(AppDbContext db, string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        var opened = await EnsureOpenAsync(connection, ct);
        try
        {
            await using var command = Create(db, connection, sql, parameters);
            return await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    public static async Task<List<T>> QueryAsync<T>(AppDbContext db, string sql, Func<DbDataReader, T> read, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        var opened = await EnsureOpenAsync(connection, ct);
        try
        {
            await using var command = Create(db, connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<T>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(read(reader));
            }

            return rows;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<bool> EnsureOpenAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection.State == ConnectionState.Open)
        {
            return false;
        }

        await connection.OpenAsync(ct);
        return true;
    }

    private static DbCommand Create(AppDbContext db, DbConnection connection, string sql, NpgsqlParameter[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
