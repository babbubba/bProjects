using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>Righe di un parsing in coda, pronte per i test PG di T028/T029: nessun AI, nessun parser reale.</summary>
public sealed record SeededParse(
    Guid User, Guid Project, Guid Document, Guid Version, Guid Preparation, Guid Job, string StorageKey, string Sha256, long ByteLength, byte[] Content);

/// <summary>
/// Database PostgreSQL isolato (creato con nome univoco, migrato con le migrazioni reali, eliminato al termine) e seed di un parsing in coda.
/// Usa solo la connessione amministrativa di <see cref="TestEnvironment"/>: Skipped se manca, mai un PASS.
/// </summary>
public sealed class ParsingDatabase : IAsyncDisposable
{
    private readonly string _admin;
    private readonly string _name;

    private ParsingDatabase(string admin, string name)
    {
        _admin = admin;
        _name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    public string ConnectionString { get; }

    public static async Task<ParsingDatabase> CreateAsync()
    {
        var admin = TestEnvironment.RequirePostgres();
        var database = new ParsingDatabase(admin, $"bprojects_test_{Guid.NewGuid():N}");
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await new NpgsqlCommand($"CREATE DATABASE \"{database._name}\"", connection).ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var db = database.NewContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return database;
    }

    public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>Utente e progetto minimi (come l'upload reale li presuppone).</summary>
    public async Task<(Guid User, Guid Project)> SeedProjectAsync()
    {
        var (user, project) = (Guid.NewGuid(), Guid.NewGuid());
        await using var db = NewContext();
        db.Users.Add(new IdentityUser<Guid> { Id = user, UserName = "u" + user.ToString("N"), NormalizedUserName = "U" + user.ToString("N").ToUpperInvariant(), SecurityStamp = "s", ConcurrencyStamp = "c" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync($"INSERT INTO \"Projects\" (\"Id\",\"Name\",\"CreatedBy\",\"CreatedAt\") VALUES ('{project}','Progetto','{user}',now())");
        return (user, project);
    }

    /// <summary>
    /// Come l'acquisizione reale: ricevuta, versione, preparazione Queued e job Parse Queued con t0 e cap sul clock del database. Se
    /// <paramref name="store"/> è dato, l'originale è scritto davvero sotto la chiave della versione.
    /// </summary>
    public async Task<SeededParse> SeedParseAsync(
        DocumentFileStore? store = null, byte[]? content = null, TimeSpan? hardWindow = null, int maxAttempts = 3, (Guid User, Guid Project)? owner = null)
    {
        content ??= "Titolo\n\nTesto del documento.\n"u8.ToArray();
        var (user, project) = owner ?? await SeedProjectAsync();
        var (document, receipt, version, preparation, job) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var key = DocumentFileStore.KeyFor(project, receipt);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        if (store is not null)
        {
            var stored = await store.WriteAsync(project, receipt, new MemoryStream(content), content.Length + 1, TestContext.Current.CancellationToken);
            Assert.Equal(key, stored.StorageKey);
            Assert.Equal(sha, stored.Sha256);
        }

        await using var db = NewContext();
        var t0 = DateTimeOffset.UtcNow;
        var hard = t0 + (hardWindow ?? TimeSpan.FromHours(1));
        db.ProjectDocuments.Add(new ProjectDocument { Id = document, ProjectId = project, DisplayName = "Nota", CreatedBy = user });
        db.UploadReceipts.Add(new UploadReceipt { Id = receipt, ProjectId = project, DocumentId = document, RequestKey = Guid.NewGuid(), InputHash = new string('d', 64), ReservedBytes = content.Length, Status = UploadReceiptStatus.Receiving, ExpiresAt = t0.AddHours(1) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.DocumentVersions.Add(new DocumentVersion { Id = version, ProjectId = project, DocumentId = document, Version = 1, OriginalName = "nota.txt", MediaType = "text/plain", ByteLength = content.Length, Sha256 = sha, StorageKey = key, UploadedBy = user, UploadReceiptId = receipt });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var committed = await db.UploadReceipts.SingleAsync(r => r.Id == receipt, TestContext.Current.CancellationToken);
        committed.Status = UploadReceiptStatus.Committed;
        committed.ResultVersionId = version;
        db.DocumentPreparations.Add(new DocumentPreparation { Id = preparation, ProjectId = project, DocumentVersionId = version, Generation = 1, Status = PreparationStatus.Queued, PolicyHash = new string('e', 64), RequestedAt = t0, HardDeadlineAt = hard, EffectiveDeadlineAt = hard });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.DocumentJobs.Add(new DocumentJob
        {
            Id = job, ProjectId = project, PreparationId = preparation, Kind = DocumentJobKind.Parse, Status = DocumentJobStatus.Queued,
            RequestedBy = user, RequestedAt = t0, DeadlineAt = hard, MaxAttempts = maxAttempts,
            ConfigJson = """{"policyHash":"x","ParseTimeoutSeconds":300,"MaxPages":1000,"MaxBlocks":100000,"MaxExpandedBytes":250000000,"MaxDerivedBytes":250000000}""",
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await db.DocumentPreparations.SingleAsync(p => p.Id == preparation, TestContext.Current.CancellationToken)).ParseJobId = job;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new(user, project, document, version, preparation, job, key, sha, content.Length, content);
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await new NpgsqlCommand(sql, connection).ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return (T)Convert.ChangeType((await new NpgsqlCommand(sql, connection).ExecuteScalarAsync(TestContext.Current.CancellationToken))!, typeof(T));
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
    }
}
