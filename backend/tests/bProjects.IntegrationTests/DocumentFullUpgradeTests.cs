using System.Security.Cryptography;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Db = bProjects.IntegrationTests.DocumentEvidenceMigrationTests;

namespace bProjects.IntegrationTests;

/// <summary>
/// T105 (PG) — upgrade/restore completo DB+volume: app ferma, backup coerente (database copiato con TEMPLATE a nessuna sessione aperta e
/// directory del volume copiata), tutte le migrazioni 002 esplicite su un DB 001 con run, bozze, checkpoint, Evidence e coverage, crash a metà
/// upgrade e ripresa, nessuna migrazione all'avvio del server, rollback binario 001 solo prima dei dati 1.1 (Down non distruttiva) e altrimenti
/// restore isolato, download SHA256 identici. Nessun LLM, nessun DGX. Gli strumenti di snapshot del volume dello stack reale non sono usati:
/// il backup è una copia consistente a database e volume fermi.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentFullUpgradeTests : IDisposable
{
    private const string MidUpgrade = "20261101223202_DocumentParsing";

    private static readonly string[] DocumentTables =
    [
        "ProjectDocuments", "UploadReceipts", "DocumentVersions", "DocumentPreparations", "EvidenceExtractionJobs", "EvidenceExtractionVersions",
        "Evidences", "AnalysisEvidenceSelectionItems", "EvidenceDocumentStates",
    ];

    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "bprojects-upgrade-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.Exists(_sandbox) ? Directory.EnumerateFiles(_sandbox, "*", SearchOption.AllDirectories) : [])
            {
                File.SetAttributes(file, FileAttributes.Normal);   // il negozio rende i file di sola lettura
            }

            Directory.Delete(_sandbox, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort: cartella temporanea del test
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private DocumentFileStore Store(string root) => new(root, [Path.Combine(_sandbox, "wwwroot")], NullLogger<DocumentFileStore>.Instance);

    private string NewVolume(string name)
    {
        var root = Path.Combine(_sandbox, name);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }
    }

    private static async Task<Dictionary<string, string>> SnapshotAsync(Db.TempDatabase database, IEnumerable<string> tables, Dictionary<string, string[]>? columns = null)
    {
        var result = new Dictionary<string, string>();
        foreach (var table in tables)
        {
            result[table] = await database.SnapshotAsync(table, columns?[table] ?? await database.ColumnsAsync(table));
        }

        return result;
    }

    private static async Task AssertLegacyUnchangedAsync(Db.TempDatabase database, Dictionary<string, string[]> columns, Dictionary<string, string> before)
    {
        foreach (var table in Db.LegacyTables)
        {
            Assert.Equal(before[table], await database.SnapshotAsync(table, columns[table]));   // ID, JSONB, conferme, checkpoint, bozze: nulla riscritto
        }
    }

    private static async Task<string> LegacyHashesAsync(Db.TempDatabase database) =>
        await database.HashAsync(
            "SELECT (SELECT coalesce(string_agg(\"ContentJson\"::text, '|' ORDER BY \"Id\"), '') FROM \"AnalysisRevisions\") || " +
            "(SELECT coalesce(string_agg(\"Markdown\", '|' ORDER BY \"Id\"), '') FROM \"Drafts\") || " +
            "(SELECT coalesce(string_agg(\"Payload\"::text || \"CheckpointInfo\"::text, '|' ORDER BY \"Id\"), '') FROM \"WorkflowCheckpoints\") || " +
            "(SELECT coalesce(string_agg(\"Content\"::text, '|' ORDER BY \"Id\"), '') FROM \"Evidences\" WHERE \"OriginKind\" = 'Message')");

    private static async Task<string> LegacyHashesBeforeAsync(Db.TempDatabase database) =>
        await database.HashAsync(
            "SELECT (SELECT coalesce(string_agg(\"ContentJson\"::text, '|' ORDER BY \"Id\"), '') FROM \"AnalysisRevisions\") || " +
            "(SELECT coalesce(string_agg(\"Markdown\", '|' ORDER BY \"Id\"), '') FROM \"Drafts\") || " +
            "(SELECT coalesce(string_agg(\"Payload\"::text || \"CheckpointInfo\"::text, '|' ORDER BY \"Id\"), '') FROM \"WorkflowCheckpoints\") || " +
            "(SELECT coalesce(string_agg(\"Content\"::text, '|' ORDER BY \"Id\"), '') FROM \"Evidences\")");

    [Fact]
    public async Task Backup_upgrade_crash_resume_restart_rollback_and_isolated_restore_keep_database_and_volume_identical()
    {
        // ---- DB 001 con run, bozze, checkpoint, Evidence e coverage; volume del pilota 001 ----------------------------------------------
        await using var live = await Db.TempDatabase.CreateAsync();
        await live.MigrateToAsync(Db.LastMigration001);
        var ids = Db.Legacy.New();
        await Db.SeedLegacyAsync(live, ids);
        var (columns, before) = await Db.CaptureAsync(live);
        var hashBefore = await LegacyHashesBeforeAsync(live);
        var volume = NewVolume("volume");

        // ---- app ferma, backup coerente di DB e volume (B1: ultimo stato compatibile col binario 001) ------------------------------------
        await using var backup001 = await live.CloneAsync();
        var backup001Volume = Path.Combine(_sandbox, "backup001-volume");
        CopyDirectory(volume, backup001Volume);
        var migrationsHistory001 = await live.StringAsync("SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM \"__EFMigrationsHistory\"");

        // ---- il server non migra all'avvio: avviato su una copia 001 lascia lo schema e la cronologia esattamente com'erano -------------
        await using (var startup = await backup001.CloneAsync())
        {
            await using (var harness = await ApiHarness.StartAsync(startup.ConnectionString, prepare: false))
            {
                _ = harness.Services.GetRequiredService<DocumentFileStore>();
                await using var probe = startup.NewContext();
                Assert.NotEmpty(await probe.Database.GetPendingMigrationsAsync(Ct));
            }

            Assert.Equal(migrationsHistory001, await startup.StringAsync("SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM \"__EFMigrationsHistory\""));
            Assert.Equal(0L, await startup.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_name = 'DocumentVersions'"));
        }

        // ---- crash a metà upgrade (processo interrotto dopo alcune migrazioni), poi ripresa esplicita ------------------------------------
        await live.MigrateToAsync(MidUpgrade);
        await AssertLegacyUnchangedAsync(live, columns, before);
        NpgsqlConnection.ClearAllPools();   // nuovo processo
        await using (var resumed = live.NewContext())
        {
            Assert.NotEmpty(await resumed.Database.GetPendingMigrationsAsync(Ct));
        }

        await live.MigrateToAsync(null);
        await using (var upgraded = live.NewContext())
        {
            Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync(Ct));
            Assert.False(upgraded.Database.HasPendingModelChanges());
        }

        await AssertLegacyUnchangedAsync(live, columns, before);
        Assert.Equal(hashBefore, await LegacyHashesAsync(live));   // gli hash canonici delle righe 001 non cambiano; le colonne nuove non li toccano

        // ---- rollback del binario 001 prima di qualunque dato 1.1: le Down non distruttive riportano allo schema 001 ---------------------
        await live.MigrateToAsync(Db.LastMigration001);
        Assert.Equal(migrationsHistory001, await live.StringAsync("SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM \"__EFMigrationsHistory\""));
        await AssertLegacyUnchangedAsync(live, columns, before);
        Assert.Equal(hashBefore, await LegacyHashesBeforeAsync(live));
        await live.MigrateToAsync(null);   // e di nuovo avanti: l'upgrade è ripetibile

        // ---- dati 1.1: documenti reali sul volume, preparazioni, Evidence documentale, selezione di run ---------------------------------
        var documents = await Db.SeedDocumentEvidenceAsync(live, ids);
        var store = Store(volume);
        var bytes = new byte[200_017];
        new Random(105).NextBytes(bytes);
        var stored = await store.WriteAsync(ids.Project, documents.Version1, new MemoryStream(bytes), 10_000_000, Ct);
        await live.ExecuteAsync($"UPDATE \"DocumentVersions\" SET \"StorageKey\" = '{stored.StorageKey}', \"Sha256\" = '{stored.Sha256}', \"ByteLength\" = {stored.ByteLength} WHERE \"Id\" = '{documents.Version1}'");
        Assert.Equal(Hex(bytes), stored.Sha256);
        var documentColumns = new Dictionary<string, string[]>();
        foreach (var table in DocumentTables)
        {
            documentColumns[table] = await live.ColumnsAsync(table);
        }

        var legacyWith11 = new Dictionary<string, string>();   // le tabelle 001 ora contengono anche le righe documentali inserite: nuova base di confronto
        foreach (var table in Db.LegacyTables)
        {
            legacyWith11[table] = await live.SnapshotAsync(table, columns[table]);
        }

        var documentsBefore = await SnapshotAsync(live, DocumentTables, documentColumns);
        Assert.NotEqual("[]", documentsBefore["DocumentVersions"]);
        Assert.NotEqual("[]", documentsBefore["DocumentPreparations"]);
        Assert.Equal(1L, await live.ScalarAsync("SELECT count(*) FROM \"Evidences\" WHERE \"OriginKind\" = 'Document'"));

        // ---- app ferma, backup B2 (con dati 1.1): da qui il rollback del binario 001 non è più ammesso ------------------------------------
        await using var backup11 = await live.CloneAsync();
        var backup11Volume = Path.Combine(_sandbox, "backup11-volume");
        CopyDirectory(volume, backup11Volume);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => live.MigrateToAsync(Db.LastMigration001));
        Assert.Contains("Down rifiutata", failure.ToString());
        await AssertLegacyUnchangedAsync(live, columns, legacyWith11);
        Assert.Equal(documentsBefore, await SnapshotAsync(live, DocumentTables, documentColumns));   // il rifiuto non cancella nulla

        // ---- riavvio: nuovo store sullo stesso volume, download verificato byte per byte ----------------------------------------------------
        NpgsqlConnection.ClearAllPools();
        await AssertDownloadAsync(Store(volume), live, documents.Version1, bytes);

        // ---- restore isolato di B2: database e volume nuovi, identici al vivo ---------------------------------------------------------------
        await using (var restored = await backup11.CloneAsync())
        {
            var restoredVolume = Path.Combine(_sandbox, "restored11-volume");
            CopyDirectory(backup11Volume, restoredVolume);

            await using (var db = restored.NewContext())
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
            }

            await AssertLegacyUnchangedAsync(restored, columns, legacyWith11);
            Assert.Equal(documentsBefore, await SnapshotAsync(restored, DocumentTables, documentColumns));
            Assert.Equal(hashBefore, await LegacyHashesAsync(restored));
            await AssertDownloadAsync(Store(restoredVolume), restored, documents.Version1, bytes);
            await AssertDecodersAsync(restored);
        }

        // ---- restore isolato di B1 (rollback oltre i dati 1.1): schema 001 esatto, volume 001, upgrade ripetibile ---------------------------
        await using (var rolledBack = await backup001.CloneAsync())
        {
            await AssertLegacyUnchangedAsync(rolledBack, columns, before);
            Assert.Equal(hashBefore, await LegacyHashesBeforeAsync(rolledBack));
            Assert.Equal(0L, await rolledBack.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_name = 'DocumentVersions'"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(backup001Volume));
            await rolledBack.MigrateToAsync(null);
            await AssertLegacyUnchangedAsync(rolledBack, columns, before);
            await AssertDecodersAsync(rolledBack);
        }

        // ---- il vivo resta decodificabile da nuovo e vecchio decoder dopo tutto -----------------------------------------------------------
        await AssertDecodersAsync(live);
    }

    [Fact]
    public async Task A_volume_whose_file_changed_after_the_backup_is_refused_by_the_verified_download_instead_of_served()
    {
        var root = NewVolume("tampered");
        var store = Store(root);
        var (project, obj) = (Guid.NewGuid(), Guid.NewGuid());
        var bytes = new byte[4_096];
        new Random(7).NextBytes(bytes);
        var stored = await store.WriteAsync(project, obj, new MemoryStream(bytes), 1_000_000, Ct);

        var copy = Path.Combine(_sandbox, "tampered-copy");
        CopyDirectory(root, copy);
        var file = Directory.EnumerateFiles(copy, "*", SearchOption.AllDirectories).Single(f => !f.Contains($"{Path.DirectorySeparatorChar}tmp{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        File.SetAttributes(file, FileAttributes.Normal);   // il negozio rende i file di sola lettura: la manomissione è esplicita
        await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write))
        {
            stream.WriteByte((byte)(bytes[0] ^ 0xFF));
        }

        await Assert.ThrowsAsync<DocumentStoreException>(() => Store(copy).OpenVerifiedAsync(stored.StorageKey, stored.Sha256, stored.ByteLength, Ct));
        await using var good = await Store(root).OpenVerifiedAsync(stored.StorageKey, stored.Sha256, stored.ByteLength, Ct);
        Assert.NotNull(good);
    }

    private static async Task AssertDownloadAsync(DocumentFileStore store, Db.TempDatabase database, Guid version, byte[] expected)
    {
        var key = await database.StringAsync($"SELECT \"StorageKey\" FROM \"DocumentVersions\" WHERE \"Id\" = '{version}'");
        var sha = await database.StringAsync($"SELECT \"Sha256\" FROM \"DocumentVersions\" WHERE \"Id\" = '{version}'");
        var length = (long)(await database.ScalarAsync($"SELECT \"ByteLength\" FROM \"DocumentVersions\" WHERE \"Id\" = '{version}'"))!;
        await using var stream = await store.OpenVerifiedAsync(key, sha, length, Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        Assert.Equal(Hex(expected), Hex(copy.ToArray()));
        Assert.Equal(sha, Hex(copy.ToArray()));
    }

    /// <summary>Vecchio e nuovo decoder: lo storico resta 1.0 e non è reinterpretabile come 1.1; il ramo documentale (se presente) è 1.1.</summary>
    private static async Task AssertDecodersAsync(Db.TempDatabase database)
    {
        await using var read = database.NewContext();
        var versions = await read.EvidenceExtractionVersions.AsNoTracking().ToListAsync(Ct);
        var items = await read.Evidences.AsNoTracking().ToListAsync(Ct);
        var messageItems = items.Where(i => i.OriginKind == EvidenceOriginKind.Message).ToList();
        Assert.Equal(2, messageItems.Count);
        foreach (var item in messageItems)
        {
            var version = versions.Single(v => v.Id == item.VersionId);
            Assert.Equal("evidence-1.0.0", version.SchemaVersion);
            Assert.NotNull(EvidenceSchemaDispatch.DecodeContent(version.SchemaVersion, item.Content).Legacy);
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => DocumentEvidenceCodec.DecodeContent(item.Content));
        }

        foreach (var item in items.Where(i => i.OriginKind == EvidenceOriginKind.Document))
        {
            var version = versions.Single(v => v.Id == item.VersionId);
            Assert.NotNull(EvidenceSchemaDispatch.DecodeContent(version.SchemaVersion, item.Content).Document);
        }
    }
}
