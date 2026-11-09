using System.Globalization;
using bProjects.App.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003 T003 + T006 (PG): immagine con pgvector, estensione creata SOLO dal comando esplicito <c>migrate</c>, <c>vector(1024)</c> e operatore <c>&lt;=&gt;</c>,
/// tabelle dell'indice derivato (vincoli, indici), migrazione su database con dati 002, rollback e svuotamento dell'indice senza toccare i dati autorevoli.
/// Ogni prova ha un proprio database, creato ed eliminato qui con la connessione amministrativa.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class RetrievalIndexMigrationTests
{
    private const string Previous = "20261106120000_DocumentLimitsSnapshot";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string VectorLiteral(int dimension, float value = 0.5f) =>
        "[" + string.Join(',', Enumerable.Repeat(value.ToString(CultureInfo.InvariantCulture), dimension)) + "]";

    [Fact]
    public async Task The_test_image_provides_pgvector_with_a_1024_vector_type_and_the_cosine_operator()
    {
        await using var database = await TempDatabase.CreateAsync();

        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM pg_available_extensions WHERE name = 'vector'"));
        await database.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector");
        await database.ExecuteAsync("CREATE TABLE probe (v vector(1024) NOT NULL)");
        await database.ExecuteAsync($"INSERT INTO probe VALUES ('{VectorLiteral(1024)}')");

        var distance = (double)(await database.ScalarAsync($"SELECT v <=> '{VectorLiteral(1024)}'::vector(1024) FROM probe"))!;
        Assert.Equal(0.0, distance, 6);
        await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync($"INSERT INTO probe VALUES ('{VectorLiteral(768)}')"));
    }

    [Fact]
    public async Task The_extension_is_created_only_by_the_explicit_migrate_command_never_by_connecting_or_opening_the_context()
    {
        await using var database = await TempDatabase.CreateAsync();

        await using (var db = database.NewContext())
        {
            // Aprire il contesto e leggere lo stato delle migrazioni non crea nulla.
            Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync(Ct));
        }

        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM pg_extension WHERE extname = 'vector'"));

        RunMigrateCommand(database);

        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM pg_extension WHERE extname = 'vector'"));
        await using var after = database.NewContext();
        Assert.Empty(await after.Database.GetPendingMigrationsAsync(Ct));
        RunMigrateCommand(database);   // ripetibile: IF NOT EXISTS
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%RetrievalIndex'"));
    }

    [Fact]
    public async Task The_migration_on_an_empty_database_creates_the_derived_tables_with_the_documented_columns_and_indexes()
    {
        await using var database = await TempDatabase.CreateAsync(migrate: true);

        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexEntries\""));
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexStates\""));

        Assert.Equal("vector(1024)", await database.StringAsync(
            "SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a WHERE a.attrelid = '\"RetrievalIndexEntries\"'::regclass AND a.attname = 'Embedding'"));
        Assert.Equal("tsvector", await database.StringAsync(
            "SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a WHERE a.attrelid = '\"RetrievalIndexEntries\"'::regclass AND a.attname = 'Tsv'"));
        Assert.Equal(0L, await database.ScalarAsync(
            "SELECT count(*) FROM pg_attribute WHERE attrelid = '\"RetrievalIndexEntries\"'::regclass AND attname = 'Embedding' AND NOT attnotnull"));

        var indexes = (await database.StringAsync("SELECT string_agg(indexdef, ' | ') FROM pg_indexes WHERE tablename = 'RetrievalIndexEntries'")).ToLowerInvariant();
        Assert.Contains("unique index", indexes);
        Assert.Contains("(\"blockid\", \"indexconfighash\")", indexes);
        Assert.Contains("using gin (\"tsv\")", indexes);
        Assert.Contains("(\"projectid\", \"indexconfighash\")", indexes);
        Assert.Contains("(\"documentversionid\", \"indexconfighash\")", indexes);
        Assert.DoesNotContain("hnsw", indexes);   // HNSW solo se T019 lo giustifica
        Assert.Equal("(\"DocumentVersionId\", \"IndexConfigHash\")", ((string)(await database.ScalarAsync(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = '\"RetrievalIndexStates\"'::regclass AND contype = 'p'"))!).Replace("PRIMARY KEY ", string.Empty));
    }

    [Fact]
    public async Task Vectors_search_by_cosine_distance_and_lexical_italian_search_use_the_same_table_with_the_filter_before_ranking()
    {
        await using var database = await TempDatabase.CreateAsync(migrate: true);
        var project = Guid.NewGuid();
        var other = Guid.NewGuid();
        var near = Guid.NewGuid();
        var far = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await InsertEntryAsync(database, near, project, "Il termine di pagamento è di trenta giorni.", Basis(1024, 0));
        await InsertEntryAsync(database, far, project, "La penale per ritardo è del cinque per cento.", Basis(1024, 1));
        await InsertEntryAsync(database, foreign, other, "Il termine di pagamento è di sessanta giorni.", Basis(1024, 0));

        var query = Basis(1024, 0);
        var nearest = await database.StringAsync(
            $"SELECT \"BlockId\"::text FROM \"RetrievalIndexEntries\" WHERE \"ProjectId\" = '{project}' ORDER BY \"Embedding\" <=> '{query}'::vector(1024) LIMIT 1");
        Assert.Equal(near.ToString(), nearest);

        // Lessicale italiano: lo stemming collega «pagamenti» a «pagamento»; il filtro di progetto esclude il blocco estraneo PRIMA del ranking.
        var lexical = await database.StringAsync(
            $"SELECT string_agg(\"BlockId\"::text, ',') FROM \"RetrievalIndexEntries\" WHERE \"ProjectId\" = '{project}' AND \"Tsv\" @@ websearch_to_tsquery('italian', 'pagamenti')");
        Assert.Equal(near.ToString(), lexical);
    }

    [Fact]
    public async Task Table_constraints_reject_duplicates_a_ready_state_with_missing_entries_and_a_failure_without_cause()
    {
        await using var database = await TempDatabase.CreateAsync(migrate: true);
        var project = Guid.NewGuid();
        var block = Guid.NewGuid();
        await InsertEntryAsync(database, block, project, "testo", Basis(1024, 0));

        // Stesso blocco e stessa configurazione: unico. Altra configurazione: ammessa (nuove voci al cambio di configurazione).
        await Assert.ThrowsAsync<PostgresException>(() => InsertEntryAsync(database, block, project, "testo", Basis(1024, 0)));
        await InsertEntryAsync(database, block, project, "testo", Basis(1024, 0), configHash: new string('b', 64));
        await Assert.ThrowsAsync<PostgresException>(() => InsertEntryAsync(database, Guid.NewGuid(), project, "x", Basis(1024, 0), configHash: "NOT-A-HASH"));

        var version = Guid.NewGuid();
        string State(string status, int blocks, int entries, string builtAt, string error) =>
            $"INSERT INTO \"RetrievalIndexStates\" (\"DocumentVersionId\",\"IndexConfigHash\",\"ProjectId\",\"Status\",\"BlockCount\",\"EntryCount\",\"ErrorCode\",\"BuiltAt\",\"UpdatedAt\") VALUES ('{version}','{Hash}','{project}','{status}',{blocks},{entries},{error},{builtAt},now())";

        await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(State("Ready", 5, 4, "now()", "NULL")));          // Ready solo con EntryCount = BlockCount
        await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(State("Failed", 5, 0, "NULL", "NULL")));         // Failed solo con causa tipizzata
        await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(State("Pending", -1, 0, "NULL", "NULL")));
        await database.ExecuteAsync(State("Failed", 5, 0, "NULL", "'embedding_unavailable'"));
        await database.ExecuteAsync($"UPDATE \"RetrievalIndexStates\" SET \"Status\" = 'Building' WHERE \"DocumentVersionId\" = '{version}'");   // Failed → Building: riprova idempotente
        await database.ExecuteAsync($"UPDATE \"RetrievalIndexStates\" SET \"Status\" = 'Ready', \"EntryCount\" = 5, \"BuiltAt\" = now(), \"ErrorCode\" = NULL WHERE \"DocumentVersionId\" = '{version}'");
        await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(State("Pending", 0, 0, "NULL", "NULL")));          // PK (versione, configurazione)
    }

    [Fact]
    public async Task Upgrading_a_database_with_002_data_preserves_it_and_down_removes_only_the_derived_index()
    {
        await using var database = await TempDatabase.CreateAsync();
        await using (var db = database.NewContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(Previous, Ct);
        }

        var user = Guid.NewGuid();
        var project = Guid.NewGuid();
        await database.SeedUserAndProjectAsync(user, project, "Progetto 002");
        string[] authoritative = ["Projects", "AspNetUsers", "Conversations", "ProcessingRuns", "DocumentBlocks", "ProjectDocuments", "DocumentVersions", "SourceSnapshots"];
        var before = await SnapshotAsync(database, authoritative);

        RunMigrateCommand(database);   // il comando esplicito, come in produzione

        Assert.Equal(before, await SnapshotAsync(database, authoritative));
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM pg_extension WHERE extname = 'vector'"));
        await InsertEntryAsync(database, Guid.NewGuid(), project, "voce derivata", Basis(1024, 3));

        // Down: sparisce solo l'indice derivato; i dati autorevoli restano identici; l'estensione resta installata.
        await using (var db = database.NewContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(Previous, Ct);
        }

        Assert.Equal(before, await SnapshotAsync(database, authoritative));
        Assert.Null(await database.ScalarAsync("SELECT to_regclass('\"RetrievalIndexEntries\"')::text"));
        Assert.Null(await database.ScalarAsync("SELECT to_regclass('\"RetrievalIndexStates\"')::text"));
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM pg_extension WHERE extname = 'vector'"));

        // Risalita: ripetibile, indice di nuovo vuoto.
        RunMigrateCommand(database);
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexEntries\""));
    }

    [Fact]
    public async Task A_restore_with_an_empty_derived_index_leaves_authoritative_data_intact_and_the_index_can_be_rebuilt()
    {
        // Il «backup» è modellato come lo stato dei dati autorevoli con le tabelle dell'indice vuote (l'indice è derivato, ADR-0002).
        await using var database = await TempDatabase.CreateAsync(migrate: true);
        var user = Guid.NewGuid();
        var project = Guid.NewGuid();
        await database.SeedUserAndProjectAsync(user, project);
        string[] authoritative = ["Projects", "AspNetUsers", "ProcessingRuns", "DocumentBlocks"];
        await InsertEntryAsync(database, Guid.NewGuid(), project, "da ricostruire", Basis(1024, 5));
        var before = await SnapshotAsync(database, authoritative);

        await database.ExecuteAsync("TRUNCATE \"RetrievalIndexEntries\", \"RetrievalIndexStates\"");

        Assert.Equal(before, await SnapshotAsync(database, authoritative));
        var rebuilt = Guid.NewGuid();
        await InsertEntryAsync(database, rebuilt, project, "da ricostruire", Basis(1024, 5));
        Assert.Equal(1L, await database.ScalarAsync($"SELECT count(*) FROM \"RetrievalIndexEntries\" WHERE \"BlockId\" = '{rebuilt}'"));
    }

    // ================================================================ supporto

    private static string Basis(int dimension, int axis) =>
        "[" + string.Join(',', Enumerable.Range(0, dimension).Select(i => i == axis ? "1" : "0")) + "]";

    private static Task InsertEntryAsync(TempDatabase database, Guid block, Guid project, string text, string embedding, string? configHash = null) =>
        database.ExecuteAsync(
            $"INSERT INTO \"RetrievalIndexEntries\" (\"Id\",\"BlockId\",\"IndexConfigHash\",\"ProjectId\",\"DocumentId\",\"DocumentVersionId\",\"TextHash\",\"Tsv\",\"Embedding\",\"CreatedAt\") " +
            $"VALUES ('{Guid.NewGuid()}','{block}','{configHash ?? Hash}','{project}','{Guid.NewGuid()}','{Guid.NewGuid()}','{new string('a', 64)}',to_tsvector('italian','{text.Replace("'", "''")}'),'{embedding}'::vector(1024),now())");

    private static async Task<string> SnapshotAsync(TempDatabase database, string[] tables)
    {
        var parts = new List<string>();
        foreach (var table in tables)
        {
            parts.Add(table + "=" + await database.StringAsync($"SELECT coalesce(jsonb_agg(to_jsonb(t))::text, '[]') FROM \"{table}\" t"));
        }

        return string.Join('\n', parts);
    }

    /// <summary>Il comando `migrate` del server (mai eseguito all'avvio): argomenti da riga di comando, nessuna variabile d'ambiente.</summary>
    private static void RunMigrateCommand(TempDatabase database)
    {
        var entry = typeof(Program).Assembly.EntryPoint!;
        var args = new[]
        {
            "migrate",
            "--Auth:SigningKey=" + TestApp.SigningKey,
            "--ConnectionStrings:AppDb=" + database.ConnectionString,
            "--Ai:Endpoint=http://127.0.0.1:9/v1/", "--Ai:ModelId=test-model", "--Ai:ApiKey=test-key",
        };
        entry.Invoke(null, [args]);
    }

    /// <summary>Database di prova con nome univoco, creato sul PostgreSQL di test ed eliminato al termine.</summary>
    private sealed class TempDatabase : IAsyncDisposable
    {
        private readonly string _admin;
        private readonly string _name;

        private TempDatabase(string admin, string name)
        {
            _admin = admin;
            _name = name;
            ConnectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        }

        public string ConnectionString { get; }

        public static async Task<TempDatabase> CreateAsync(bool migrate = false)
        {
            var admin = TestEnvironment.RequirePostgres();
            var database = new TempDatabase(admin, $"bprojects_test_{Guid.NewGuid():N}");
            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync(Ct);
                await new NpgsqlCommand($"CREATE DATABASE \"{database._name}\"", connection).ExecuteNonQueryAsync(Ct);
            }

            if (migrate)
            {
                await using var db = database.NewContext();
                await db.Database.MigrateAsync(Ct);
            }

            return database;
        }

        public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

        public async Task SeedUserAndProjectAsync(Guid user, Guid project, string projectName = "Progetto")
        {
            await using (var db = NewContext())
            {
                db.Users.Add(new IdentityUser<Guid> { Id = user, UserName = "u" + user.ToString("N"), NormalizedUserName = "U" + user.ToString("N").ToUpperInvariant(), SecurityStamp = "s", ConcurrencyStamp = "c" });
                await db.SaveChangesAsync(Ct);
            }

            await ExecuteAsync($"INSERT INTO \"Projects\" (\"Id\",\"Name\",\"CreatedBy\",\"CreatedAt\") VALUES ('{project}','{projectName}','{user}',now())");
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            await new NpgsqlCommand(sql, connection).ExecuteNonQueryAsync(Ct);
        }

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            var value = await new NpgsqlCommand(sql, connection).ExecuteScalarAsync(Ct);
            return value is DBNull ? null : value;
        }

        public async Task<string> StringAsync(string sql) => (string)(await ScalarAsync(sql))!;

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(_admin);
            await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
        }
    }
}
