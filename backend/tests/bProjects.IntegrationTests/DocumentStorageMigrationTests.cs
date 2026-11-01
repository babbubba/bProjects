using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T007 (PG): la migrazione dei modelli T006/T115 su PostgreSQL reale. Verifica solo invarianti strutturali: unique identità/versione/receipt,
/// FK di progetto e parent, size &gt; 0, stati/nullable e completed-result. Le quote aggregate non sono vincoli di riga (T012–T013, gate T018).
/// Ogni prova usa un proprio database, creato ed eliminato qui con la connessione amministrativa della fixture.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentStorageMigrationTests
{
    private const string Previous = "20261031210114_EvidenceLedger";

    [Fact]
    public async Task The_explicit_migrate_command_creates_the_schema_from_scratch_and_is_repeatable()
    {
        await using var database = await TempDatabase.CreateAsync();

        // Il comando `migrate` del server (mai eseguito all'avvio): argomenti da riga di comando, nessuna variabile d'ambiente.
        for (var run = 0; run < 2; run++)
        {
            var entry = typeof(Program).Assembly.EntryPoint!;
            var args = new[]
            {
                "migrate",
                "--Auth:SigningKey=" + TestApp.SigningKey,
                "--ConnectionStrings:AppDb=" + database.ConnectionString,
                "--Ai:Endpoint=http://127.0.0.1:9/v1/", "--Ai:ModelId=test-model", "--Ai:ApiKey=test-key",
            };
            await Task.Run(() => entry.Invoke(null, [args]), TestContext.Current.CancellationToken);   // il punto d'ingresso sintetizzato attende il Main asincrono
        }

        await using var db = database.NewContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        foreach (var table in new[] { "DocumentPolicies", "ProjectDocuments", "DocumentVersions", "UploadReceipts", "ProjectSourceSelections", "ProjectSourceSelectionItems", "DocumentPreparations", "DocumentJobs", "SourceSnapshots", "SourceSnapshotItems" })
        {
            Assert.Equal(0L, await database.ScalarAsync($"SELECT count(*) FROM \"{table}\""));
        }

        // Il server non applica migrazioni all'avvio: DocumentPolicies resta vuota finché nessuno legge i limiti.
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%DocumentStorage'"));
    }

    [Fact]
    public async Task Upgrading_a_001_database_preserves_every_row_and_canonical_json_and_down_is_non_destructive()
    {
        await using var database = await TempDatabase.CreateAsync();
        await using (var db = database.NewContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(Previous, TestContext.Current.CancellationToken);
        }

        var user = Guid.NewGuid();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var run = Guid.NewGuid();
        await SeedLegacyAsync(database, user, projectA, projectB, run);
        string[] legacyTables = ["Projects", "Conversations", "ConversationMessages", "ChatTurns", "ProcessingRuns", "AnalysisRevisions"];
        var before = new Dictionary<string, string>();
        foreach (var table in legacyTables)
        {
            before[table] = await Snapshot(database, table);
        }

        await using (var db = database.NewContext())
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        foreach (var table in legacyTables)
        {
            Assert.Equal(before[table], await Snapshot(database, table));   // jsonb canonico, UUID, conferme: byte-logicamente invariati
        }

        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM \"ChatTurns\" WHERE \"SourceSnapshotId\" IS NOT NULL"));
        Assert.Equal("Confirmed", await database.ScalarAsync("SELECT CASE WHEN \"ValidationState\" = 'PmConfirmed' AND \"ConfirmedBy\" IS NOT NULL THEN 'Confirmed' ELSE 'No' END FROM \"AnalysisRevisions\" WHERE \"Version\" = 2") as string);

        // Il run legacy accetta solo uno snapshot del proprio progetto (FK composta).
        await using var context = database.NewContext();
        var own = new SourceSnapshot { Id = Guid.NewGuid(), ProjectId = projectA, SelectionRevision = 0, Hash = new string('a', 64) };
        var foreign = new SourceSnapshot { Id = Guid.NewGuid(), ProjectId = projectB, SelectionRevision = 0, Hash = new string('b', 64) };
        context.SourceSnapshots.AddRange(own, foreign);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await AssertSqlState(database, $"UPDATE \"ProcessingRuns\" SET \"SourceSnapshotId\" = '{foreign.Id}' WHERE \"Id\" = '{run}'", PostgresErrorCodes.ForeignKeyViolation);
        await database.ExecuteAsync($"UPDATE \"ProcessingRuns\" SET \"SourceSnapshotId\" = '{own.Id}' WHERE \"Id\" = '{run}'");
        await database.ExecuteAsync($"UPDATE \"ProcessingRuns\" SET \"SourceSnapshotId\" = NULL WHERE \"Id\" = '{run}'");
        await database.ExecuteAsync($"DELETE FROM \"SourceSnapshots\"");

        // Down senza dati documentali: riporta al modello 001 lasciando intatti i dati storici.
        await using (var db = database.NewContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(Previous, TestContext.Current.CancellationToken);
        }

        foreach (var table in legacyTables)
        {
            Assert.Equal(before[table], await Snapshot(database, table, dropSnapshotColumn: false));
        }

        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_name = 'ProjectDocuments'"));
    }

    [Fact]
    public async Task Down_refuses_to_run_once_documentary_data_exists()
    {
        await using var database = await TempDatabase.CreateAsync();
        await using (var db = database.NewContext())
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        var user = Guid.NewGuid();
        var project = Guid.NewGuid();
        await database.SeedUserAndProjectAsync(user, project);
        await using (var db = database.NewContext())
        {
            db.ProjectDocuments.Add(new ProjectDocument { Id = Guid.NewGuid(), ProjectId = project, DisplayName = "Capitolato", CreatedBy = user });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var down = database.NewContext();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => down.GetService<IMigrator>().MigrateAsync(Previous, TestContext.Current.CancellationToken));

        Assert.Contains("Down rifiutata", failure.ToString());
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM \"ProjectDocuments\""));   // nulla è stato cancellato
    }

    [Fact]
    public async Task The_archive_preparation_and_an_unavailable_snapshot_round_trip()
    {
        await using var database = await TempDatabase.CreateAsync(migrate: true);
        var ids = await SeedArchiveAsync(database);

        await using var read = database.NewContext();
        var document = await read.ProjectDocuments.AsNoTracking().SingleAsync(d => d.Id == ids.Document, TestContext.Current.CancellationToken);
        var version = await read.DocumentVersions.AsNoTracking().SingleAsync(v => v.Id == ids.Version, TestContext.Current.CancellationToken);
        var receipt = await read.UploadReceipts.AsNoTracking().SingleAsync(r => r.Id == ids.Receipt, TestContext.Current.CancellationToken);
        var preparation = await read.DocumentPreparations.AsNoTracking().SingleAsync(p => p.Id == ids.Preparation, TestContext.Current.CancellationToken);
        var job = await read.DocumentJobs.AsNoTracking().SingleAsync(j => j.Id == ids.Job, TestContext.Current.CancellationToken);
        var item = await read.SourceSnapshotItems.AsNoTracking().SingleAsync(i => i.SnapshotId == ids.Snapshot, TestContext.Current.CancellationToken);

        Assert.Equal("Capitolato", document.DisplayName);
        Assert.NotEqual(default, document.CreatedAt);   // timestamp UTC assegnato dal DB
        Assert.True(DateTimeSpanUtc(version.UploadedAt));
        Assert.Equal(1, version.Version);
        Assert.Equal(UploadReceiptStatus.Committed, receipt.Status);
        Assert.Equal(version.Id, receipt.ResultVersionId);
        Assert.Equal(PreparationStatus.Queued, preparation.Status);
        Assert.Equal(job.Id, preparation.ParseJobId);
        Assert.Equal(DocumentJobStatus.Queued, job.Status);
        Assert.Null(document.LatestReadyVersionId);   // finché non pronta nessuna versione è applicabile
        Assert.Equal(SourceAvailability.Unavailable, item.Availability);
        Assert.Null(item.EvidenceVersionId);
        Assert.Equal(preparation.Id, item.PreparationId);
    }

    public static TheoryData<string> InvalidRows() =>
        [
            "duplicate-version-number", "duplicate-storage-key", "zero-size", "zero-version", "duplicate-receipt-key", "committed-receipt-without-version",
            "version-in-other-project", "latest-ready-of-another-document", "pinned-without-version", "pinned-other-document", "duplicate-selection-item",
            "second-open-generation", "duplicate-generation", "deadline-after-hard-cap", "ready-without-evidence", "parse-job-of-other-preparation",
            "completed-job-without-result", "failed-job-without-error", "duplicate-parse-job", "job-in-other-project", "ready-snapshot-item-without-evidence",
            "excluded-but-included", "evidence-on-unavailable-item",
        ];

    [Theory]
    [MemberData(nameof(InvalidRows))]
    public async Task Structural_constraints_reject_invalid_rows(string caseName)
    {
        await using var database = await TempDatabase.CreateAsync(migrate: true);
        var ids = await SeedArchiveAsync(database);
        var other = await SeedSecondProjectAsync(database, ids);
        var (sql, state) = caseName switch
        {
            "duplicate-version-number" => (Version(ids.Document, ids.Project, 1, "k-dup-1", 5, ids.Receipt2), PostgresErrorCodes.UniqueViolation),
            "duplicate-storage-key" => (Version(ids.Document, ids.Project, 2, "storage/" + ids.Version, 5, ids.Receipt2), PostgresErrorCodes.UniqueViolation),
            "zero-size" => (Version(ids.Document, ids.Project, 2, "k-zero", 0, ids.Receipt2), PostgresErrorCodes.CheckViolation),
            "zero-version" => (Version(ids.Document, ids.Project, 0, "k-v0", 5, ids.Receipt2), PostgresErrorCodes.CheckViolation),
            "duplicate-receipt-key" => (Receipt(Guid.NewGuid(), ids.Project, null, ids.ReceiptKey, "Receiving", null), PostgresErrorCodes.UniqueViolation),
            "committed-receipt-without-version" => (Receipt(Guid.NewGuid(), ids.Project, null, Guid.NewGuid(), "Committed", null), PostgresErrorCodes.CheckViolation),
            "version-in-other-project" => (Version(ids.Document, other.Project, 2, "k-xproj", 5, ids.Receipt2), PostgresErrorCodes.ForeignKeyViolation),
            "latest-ready-of-another-document" => ($"UPDATE \"ProjectDocuments\" SET \"LatestReadyVersionId\" = '{other.Version}' WHERE \"Id\" = '{ids.Document}'", PostgresErrorCodes.ForeignKeyViolation),
            "pinned-without-version" => (Item(Guid.NewGuid(), ids.Project, other.Document2, "Pinned", null), PostgresErrorCodes.CheckViolation),
            "pinned-other-document" => (Item(Guid.NewGuid(), ids.Project, other.Document2, "Pinned", other.Version), PostgresErrorCodes.ForeignKeyViolation),
            "duplicate-selection-item" => (Item(Guid.NewGuid(), ids.Project, ids.Document, "LatestReady", null), PostgresErrorCodes.UniqueViolation),
            "second-open-generation" => (Preparation(Guid.NewGuid(), ids.Project, ids.Version, 2, "Parsing", "now() + interval '1 hour'", "now() + interval '1 hour'", null), PostgresErrorCodes.UniqueViolation),
            "duplicate-generation" => (Preparation(Guid.NewGuid(), ids.Project, ids.Version, 1, "Incomplete", "now() + interval '1 hour'", "now() + interval '1 hour'", null), PostgresErrorCodes.UniqueViolation),
            "deadline-after-hard-cap" => (Preparation(Guid.NewGuid(), ids.Project, other.Version2, 9, "Queued", "now() + interval '1 hour'", "now() + interval '2 hours'", null), PostgresErrorCodes.CheckViolation),
            "ready-without-evidence" => (Preparation(Guid.NewGuid(), ids.Project, other.Version2, 9, "Ready", "now() + interval '1 hour'", "now() + interval '1 hour'", null), PostgresErrorCodes.CheckViolation),
            "parse-job-of-other-preparation" => ($"UPDATE \"DocumentPreparations\" SET \"ParseJobId\" = '{other.Job}' WHERE \"Id\" = '{ids.Preparation}'", PostgresErrorCodes.ForeignKeyViolation),
            "completed-job-without-result" => (Job(Guid.NewGuid(), ids.Project, ids.Preparation, "ExtractUnit", Guid.NewGuid(), "Completed", null, null), PostgresErrorCodes.CheckViolation),
            "failed-job-without-error" => (Job(Guid.NewGuid(), ids.Project, ids.Preparation, "ExtractUnit", Guid.NewGuid(), "Failed", null, null), PostgresErrorCodes.CheckViolation),
            "duplicate-parse-job" => (Job(Guid.NewGuid(), ids.Project, ids.Preparation, "Parse", null, "Queued", null, null), PostgresErrorCodes.UniqueViolation),
            "job-in-other-project" => (Job(Guid.NewGuid(), other.Project, ids.Preparation, "ExtractUnit", Guid.NewGuid(), "Queued", null, null), PostgresErrorCodes.ForeignKeyViolation),
            "ready-snapshot-item-without-evidence" => (SnapshotItem(Guid.NewGuid(), ids.Snapshot, ids.Project, other.Document2, "true", "Ready", other.Version2, other.Preparation2), PostgresErrorCodes.CheckViolation),
            "excluded-but-included" => (SnapshotItem(Guid.NewGuid(), ids.Snapshot, ids.Project, other.Document2, "true", "Excluded", null, null), PostgresErrorCodes.CheckViolation),
            "evidence-on-unavailable-item" => (SnapshotItem(Guid.NewGuid(), ids.Snapshot, ids.Project, other.Document2, "true", "Unavailable", null, null, Guid.NewGuid()), PostgresErrorCodes.CheckViolation),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };

        await AssertSqlState(database, sql, state);
    }

    // ---- SQL grezzo: scrive esattamente le colonne dello schema, senza passare dalle regole dell'applicazione ----

    private static string Version(Guid document, Guid project, int number, string key, long size, Guid receipt) =>
        $"INSERT INTO \"DocumentVersions\" (\"Id\",\"ProjectId\",\"DocumentId\",\"Version\",\"OriginalName\",\"MediaType\",\"ByteLength\",\"Sha256\",\"StorageKey\",\"UploadedBy\",\"UploadReceiptId\") " +
        $"SELECT '{Guid.NewGuid()}','{project}','{document}',{number},'a.pdf','application/pdf',{size},'{new string('c', 64)}','{key}',\"UploadedBy\",'{receipt}' FROM \"DocumentVersions\" LIMIT 1";

    private static string Receipt(Guid id, Guid project, Guid? document, Guid key, string status, Guid? result) =>
        $"INSERT INTO \"UploadReceipts\" (\"Id\",\"ProjectId\",\"DocumentId\",\"RequestKey\",\"InputHash\",\"ReservedBytes\",\"Status\",\"ExpiresAt\",\"ResultVersionId\") VALUES " +
        $"('{id}','{project}',{Nullable(document)},'{key}','{new string('d', 64)}',0,'{status}',now() + interval '1 hour',{Nullable(result)})";

    private static string Item(Guid id, Guid project, Guid document, string mode, Guid? pinned) =>
        $"INSERT INTO \"ProjectSourceSelectionItems\" (\"Id\",\"ProjectId\",\"DocumentId\",\"Included\",\"Mode\",\"PinnedVersionId\") VALUES ('{id}','{project}','{document}',true,'{mode}',{Nullable(pinned)})";

    private static string Preparation(Guid id, Guid project, Guid version, int generation, string status, string hard, string effective, Guid? evidence) =>
        $"INSERT INTO \"DocumentPreparations\" (\"Id\",\"ProjectId\",\"DocumentVersionId\",\"Generation\",\"Status\",\"CurrentEvidenceVersionId\",\"PolicyHash\",\"HardDeadlineAt\",\"EffectiveDeadlineAt\",\"CallCount\") VALUES " +
        $"('{id}','{project}','{version}',{generation},'{status}',{Nullable(evidence)},'{new string('e', 64)}',{hard},{effective},0)";

    private static string Job(Guid id, Guid project, Guid preparation, string kind, Guid? unit, string status, string? resultHash, string? error) =>
        $"INSERT INTO \"DocumentJobs\" (\"Id\",\"ProjectId\",\"PreparationId\",\"Kind\",\"UnitId\",\"Status\",\"ResultHash\",\"ErrorCode\",\"DeadlineAt\",\"AttemptCount\",\"MaxAttempts\",\"FencingToken\") VALUES " +
        $"('{id}','{project}','{preparation}','{kind}',{Nullable(unit)},'{status}',{(resultHash is null ? "NULL" : $"'{resultHash}'")},{(error is null ? "NULL" : $"'{error}'")},now() + interval '1 hour',0,3,0)";

    private static string SnapshotItem(Guid id, Guid snapshot, Guid project, Guid document, string included, string availability, Guid? version, Guid? preparation, Guid? evidence = null) =>
        $"INSERT INTO \"SourceSnapshotItems\" (\"Id\",\"SnapshotId\",\"ProjectId\",\"DocumentId\",\"Included\",\"Availability\",\"DocumentVersionId\",\"PreparationId\",\"EvidenceVersionId\") VALUES " +
        $"('{id}','{snapshot}','{project}','{document}',{included},'{availability}',{Nullable(version)},{Nullable(preparation)},{Nullable(evidence)})";

    private static string Nullable(Guid? id) => id is null ? "NULL" : $"'{id}'";

    private static bool DateTimeSpanUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value > DateTimeOffset.UtcNow.AddMinutes(-5);

    private static async Task AssertSqlState(TempDatabase database, string sql, string expectedState)
    {
        var failure = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(sql));
        Assert.Equal(expectedState, failure.SqlState);
    }

    private sealed record ArchiveIds(Guid User, Guid Project, Guid Document, Guid Version, Guid Receipt, Guid Receipt2, Guid ReceiptKey, Guid Preparation, Guid Job, Guid Snapshot);

    private sealed record OtherIds(Guid Project, Guid Document, Guid Version, Guid Receipt, Guid Preparation, Guid Job, Guid Document2, Guid Version2, Guid Preparation2);

    /// <summary>Progetto con un documento, una versione Committed, una preparazione Queued con job Parse, una selezione e uno snapshot indisponibile.</summary>
    private static async Task<ArchiveIds> SeedArchiveAsync(TempDatabase database)
    {
        var ids = new ArchiveIds(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await database.SeedUserAndProjectAsync(ids.User, ids.Project);
        await using var db = database.NewContext();
        db.ProjectDocuments.Add(new ProjectDocument { Id = ids.Document, ProjectId = ids.Project, DisplayName = "Capitolato", CreatedBy = ids.User });
        db.UploadReceipts.Add(new UploadReceipt { Id = ids.Receipt, ProjectId = ids.Project, DocumentId = ids.Document, RequestKey = ids.ReceiptKey, InputHash = new string('d', 64), ReservedBytes = 10, Status = UploadReceiptStatus.Receiving, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        db.UploadReceipts.Add(new UploadReceipt { Id = ids.Receipt2, ProjectId = ids.Project, DocumentId = ids.Document, RequestKey = Guid.NewGuid(), InputHash = new string('d', 64), ReservedBytes = 10, Status = UploadReceiptStatus.Receiving, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.DocumentVersions.Add(new DocumentVersion { Id = ids.Version, ProjectId = ids.Project, DocumentId = ids.Document, Version = 1, OriginalName = "capitolato.pdf", MediaType = "application/pdf", ByteLength = 1234, Sha256 = new string('c', 64), StorageKey = "storage/" + ids.Version, UploadedBy = ids.User, UploadReceiptId = ids.Receipt });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await db.UploadReceipts.SingleAsync(r => r.Id == ids.Receipt, TestContext.Current.CancellationToken)).Status = UploadReceiptStatus.Committed;
        (await db.UploadReceipts.SingleAsync(r => r.Id == ids.Receipt, TestContext.Current.CancellationToken)).ResultVersionId = ids.Version;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var t0 = DateTimeOffset.UtcNow;
        db.DocumentPreparations.Add(new DocumentPreparation { Id = ids.Preparation, ProjectId = ids.Project, DocumentVersionId = ids.Version, Generation = 1, Status = PreparationStatus.Queued, PolicyHash = new string('e', 64), RequestedAt = t0, HardDeadlineAt = t0.AddHours(1), EffectiveDeadlineAt = t0.AddHours(1) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.DocumentJobs.Add(new DocumentJob { Id = ids.Job, ProjectId = ids.Project, PreparationId = ids.Preparation, Kind = DocumentJobKind.Parse, Status = DocumentJobStatus.Queued, RequestedAt = t0, DeadlineAt = t0.AddHours(1), MaxAttempts = 3 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await db.DocumentPreparations.SingleAsync(p => p.Id == ids.Preparation, TestContext.Current.CancellationToken)).ParseJobId = ids.Job;
        db.ProjectSourceSelections.Add(new ProjectSourceSelection { ProjectId = ids.Project, Revision = 1, UpdatedBy = ids.User });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ProjectSourceSelectionItems.Add(new ProjectSourceSelectionItem { Id = Guid.NewGuid(), ProjectId = ids.Project, DocumentId = ids.Document, Included = true, Mode = SourceMode.LatestReady });
        db.SourceSnapshots.Add(new SourceSnapshot { Id = ids.Snapshot, ProjectId = ids.Project, SelectionRevision = 1, Hash = new string('f', 64) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.SourceSnapshotItems.Add(new SourceSnapshotItem { Id = Guid.NewGuid(), SnapshotId = ids.Snapshot, ProjectId = ids.Project, DocumentId = ids.Document, Included = true, Availability = SourceAvailability.Unavailable, DocumentVersionId = ids.Version, PreparationId = ids.Preparation });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return ids;
    }

    /// <summary>Secondo progetto con documento, versione, preparazione e job propri; più un secondo documento (senza preparazione aperta) del primo progetto.</summary>
    private static async Task<OtherIds> SeedSecondProjectAsync(TempDatabase database, ArchiveIds first)
    {
        var project = Guid.NewGuid();
        var other = new OtherIds(project, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await database.ExecuteAsync($"INSERT INTO \"Projects\" (\"Id\",\"Name\",\"CreatedBy\",\"CreatedAt\") VALUES ('{project}','Altro','{first.User}',now())");
        await using var db = database.NewContext();
        var t0 = DateTimeOffset.UtcNow;
        db.ProjectDocuments.Add(new ProjectDocument { Id = other.Document, ProjectId = project, DisplayName = "Altro documento", CreatedBy = first.User });
        db.ProjectDocuments.Add(new ProjectDocument { Id = other.Document2, ProjectId = first.Project, DisplayName = "Secondo documento", CreatedBy = first.User });
        db.UploadReceipts.Add(new UploadReceipt { Id = other.Receipt, ProjectId = project, RequestKey = Guid.NewGuid(), InputHash = new string('d', 64), ReservedBytes = 1, Status = UploadReceiptStatus.Receiving, ExpiresAt = t0.AddHours(1) });
        var receipt2 = Guid.NewGuid();
        db.UploadReceipts.Add(new UploadReceipt { Id = receipt2, ProjectId = first.Project, RequestKey = Guid.NewGuid(), InputHash = new string('d', 64), ReservedBytes = 1, Status = UploadReceiptStatus.Receiving, ExpiresAt = t0.AddHours(1) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.DocumentVersions.Add(new DocumentVersion { Id = other.Version, ProjectId = project, DocumentId = other.Document, Version = 1, OriginalName = "b.pdf", MediaType = "application/pdf", ByteLength = 5, Sha256 = new string('c', 64), StorageKey = "storage/" + other.Version, UploadedBy = first.User, UploadReceiptId = other.Receipt });
        db.DocumentVersions.Add(new DocumentVersion { Id = other.Version2, ProjectId = first.Project, DocumentId = other.Document2, Version = 1, OriginalName = "c.pdf", MediaType = "application/pdf", ByteLength = 5, Sha256 = new string('c', 64), StorageKey = "storage/" + other.Version2, UploadedBy = first.User, UploadReceiptId = receipt2 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.DocumentPreparations.Add(new DocumentPreparation { Id = other.Preparation, ProjectId = project, DocumentVersionId = other.Version, Generation = 1, Status = PreparationStatus.Queued, PolicyHash = new string('e', 64), RequestedAt = t0, HardDeadlineAt = t0.AddHours(1), EffectiveDeadlineAt = t0.AddHours(1) });
        db.DocumentPreparations.Add(new DocumentPreparation { Id = other.Preparation2, ProjectId = first.Project, DocumentVersionId = other.Version2, Generation = 1, Status = PreparationStatus.Failed, ErrorCode = "parse_failed", PolicyHash = new string('e', 64), RequestedAt = t0, HardDeadlineAt = t0.AddHours(1), EffectiveDeadlineAt = t0.AddHours(1) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.DocumentJobs.Add(new DocumentJob { Id = other.Job, ProjectId = project, PreparationId = other.Preparation, Kind = DocumentJobKind.Parse, Status = DocumentJobStatus.Queued, RequestedAt = t0, DeadlineAt = t0.AddHours(1), MaxAttempts = 3 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return other;
    }

    /// <summary>Righe 001 con colonne esatte dello schema precedente; JSON con spazi e ordine di chiavi non canonici di proposito.</summary>
    private static async Task SeedLegacyAsync(TempDatabase database, Guid user, Guid projectA, Guid projectB, Guid run)
    {
        var conversation = Guid.NewGuid();
        var message = Guid.NewGuid();
        var turn = Guid.NewGuid();
        var revision1 = Guid.NewGuid();
        var revision2 = Guid.NewGuid();
        await database.SeedUserAndProjectAsync(user, projectA, projectName: "Legacy A");
        await database.ExecuteAsync($"INSERT INTO \"Projects\" (\"Id\",\"Name\",\"CreatedBy\",\"CreatedAt\") VALUES ('{projectB}','Legacy B','{user}',now())");
        await database.ExecuteAsync($"INSERT INTO \"Conversations\" (\"Id\",\"ProjectId\",\"CreatedAt\",\"LastSequence\",\"MafVersion\",\"ContextThroughSequence\",\"ChatSessionRevision\") VALUES ('{conversation}','{projectA}',now(),1,'1.26.0',0,0)");
        await database.ExecuteAsync($"INSERT INTO \"ConversationMessages\" (\"Id\",\"ConversationId\",\"Sequence\",\"Role\",\"Body\",\"CreatedAt\",\"ClientMessageId\",\"AuthorId\") VALUES ('{message}','{conversation}',1,'User','Il portale supporta 500 utenti.',now(),'{Guid.NewGuid()}','{user}')");
        await database.ExecuteAsync($"INSERT INTO \"ChatTurns\" (\"Id\",\"ConversationId\",\"TriggerMessageId\",\"SourceCutoffSequence\",\"Status\",\"RequestedAt\",\"DeadlineAt\",\"AttemptCount\",\"MaxAttempts\",\"FencingToken\",\"AgentConfig\",\"SessionRevisionAtStart\") VALUES ('{turn}','{conversation}','{message}',1,'Queued',now(),now() + interval '3 minutes',0,3,0,'{{ \"b\": 2,   \"a\": [1, 2, {{\"z\": null}}] }}',0)");
        await database.ExecuteAsync($"INSERT INTO \"ProcessingRuns\" (\"Id\",\"ProjectId\",\"Kind\",\"ConversationId\",\"SourceCutoffSequence\",\"RequestKey\",\"RequestedBy\",\"RequestedAt\",\"Status\",\"DeadlineAt\",\"WorkflowDefinitionVersion\",\"MafVersion\",\"AgentConfig\",\"PromptVersions\",\"AttemptCount\",\"MaxAttempts\",\"FencingToken\",\"UpdatedAt\") VALUES ('{run}','{projectA}','Analysis','{conversation}',1,'{Guid.NewGuid()}','{user}',now(),'Queued',now() + interval '20 minutes','project-analysis-v3','1.26.0','{{\"model\" :  \"m\", \"thinking\": false}}','{{\"analysis\":\"v3\"}}',0,3,0,now())");
        await database.ExecuteAsync($"INSERT INTO \"AnalysisRevisions\" (\"Id\",\"AnalysisId\",\"Version\",\"ParentRevisionId\",\"Origin\",\"CreatedBy\",\"CreatedAt\",\"SchemaVersion\",\"ContentJson\",\"ValidationState\") VALUES ('{revision1}','{run}',1,NULL,'Ai','{user}',now(),'1.0.0','{{\"schemaVersion\":\"1.0.0\",  \"goals\":[]}}','Unvalidated')");
        await database.ExecuteAsync($"INSERT INTO \"AnalysisRevisions\" (\"Id\",\"AnalysisId\",\"Version\",\"ParentRevisionId\",\"Origin\",\"CreatedBy\",\"CreatedAt\",\"SchemaVersion\",\"ContentJson\",\"ValidationState\",\"ConfirmedBy\",\"ConfirmedAt\") VALUES ('{revision2}','{run}',2,'{revision1}','PmEdit','{user}',now(),'1.0.0','{{\"schemaVersion\":\"1.0.0\",\"goals\":[ ]}}','PmConfirmed','{user}',now())");
    }

    /// <summary>Contenuto della tabella come jsonb canonico ordinato per Id; il nuovo campo di snapshot è escluso per il confronto pre/post.</summary>
    private static Task<string> Snapshot(TempDatabase database, string table, bool dropSnapshotColumn = true) =>
        database.StringAsync($"SELECT coalesce(jsonb_agg(to_jsonb(t){(dropSnapshotColumn ? " - 'SourceSnapshotId'" : string.Empty)} ORDER BY t.\"Id\")::text, '[]') FROM \"{table}\" t");

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
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await new NpgsqlCommand($"CREATE DATABASE \"{database._name}\"", connection).ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            if (migrate)
            {
                await using var db = database.NewContext();
                await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            }

            return database;
        }

        public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

        public async Task SeedUserAndProjectAsync(Guid user, Guid project, string projectName = "Progetto")
        {
            await using (var db = NewContext())
            {
                db.Users.Add(new IdentityUser<Guid> { Id = user, UserName = "u" + user.ToString("N"), NormalizedUserName = "U" + user.ToString("N").ToUpperInvariant(), SecurityStamp = "s", ConcurrencyStamp = "c" });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await ExecuteAsync($"INSERT INTO \"Projects\" (\"Id\",\"Name\",\"CreatedBy\",\"CreatedAt\") VALUES ('{project}','{projectName}','{user}',now())");
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await new NpgsqlCommand(sql, connection).ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            return await new NpgsqlCommand(sql, connection).ExecuteScalarAsync(TestContext.Current.CancellationToken);
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
