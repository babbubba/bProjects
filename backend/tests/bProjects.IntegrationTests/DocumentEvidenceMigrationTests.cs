using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T037 (PG) — upgrade da un database 001 con run, bozze, Evidence, citazioni e checkpoint verso il ledger documentale (migrazione
/// DocumentEvidence): righe e JSON storici invariati (hash canonici), backfill solo OriginKind=Message, XOR/FK/progetto sul ramo
/// documentale, liste miste decodificate per versione di schema, Down non distruttivo. Nessun LLM, nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentEvidenceMigrationTests
{
    private const string LastMigration001 = "20261031210114_EvidenceLedger";
    private const string Previous = "20261101223202_DocumentParsing";

    // Tabelle storiche di ledger, run, bozze e checkpoint: tutte byte-logicamente identiche dopo l'upgrade (colonne preesistenti).
    private static readonly string[] LegacyTables =
    [
        "Projects", "Conversations", "ConversationMessages", "ChatTurns", "ProcessingRuns", "AnalysisRevisions", "Drafts", "WorkflowCheckpoints", "InferenceCalls",
        "EvidenceExtractionJobs", "EvidenceExtractionVersions", "Evidences", "EvidenceRelations", "EvidenceMessageStates", "EvidencePromotions",
        "AnalysisEvidenceSelections", "AnalysisEvidenceSelectionItems", "AnalysisEvidenceLinks", "EvidenceCoverageReports",
    ];

    // ================== Test ==================

    [Fact]
    public async Task The_explicit_migration_from_scratch_matches_the_model_and_has_no_pending_changes()
    {
        await using var database = await TempDatabase.CreateAsync(migrate: true);
        await using var db = database.NewContext();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.False(db.Database.HasPendingModelChanges());   // snapshot e modello coincidono
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%DocumentEvidence'"));
        foreach (var table in new[] { "EvidenceDocumentStates", "DocumentPromotions" })
        {
            Assert.Equal(0L, await database.ScalarAsync($"SELECT count(*) FROM \"{table}\""));
        }
    }

    [Fact]
    public async Task Upgrading_a_001_database_keeps_every_row_json_and_canonical_hash_and_backfills_only_the_message_origin()
    {
        await using var database = await TempDatabase.CreateAsync();
        await database.MigrateToAsync(LastMigration001);
        var ids = Legacy.New();
        await SeedLegacyAsync(database, ids);
        var (oldColumns, before) = await CaptureAsync(database);
        var contentHash = await database.HashAsync("SELECT coalesce(string_agg(\"Content\"::text, '|' ORDER BY \"Id\"), '') FROM \"Evidences\"");
        var revisionHash = await database.HashAsync("SELECT coalesce(string_agg(\"ContentJson\"::text, '|' ORDER BY \"Id\"), '') FROM \"AnalysisRevisions\"");
        var draftHash = await database.HashAsync("SELECT coalesce(string_agg(\"Markdown\", '|' ORDER BY \"Id\"), '') FROM \"Drafts\"");
        var checkpointHash = await database.HashAsync("SELECT coalesce(string_agg(\"Payload\"::text || \"CheckpointInfo\"::text, '|' ORDER BY \"Id\"), '') FROM \"WorkflowCheckpoints\"");

        await database.MigrateToAsync(null);

        foreach (var table in LegacyTables)
        {
            Assert.Equal(before[table], await database.SnapshotAsync(table, oldColumns[table]));   // JSONB, ID, span, conferme, checkpoint: nulla riscritto
        }

        Assert.Equal(contentHash, await database.HashAsync("SELECT coalesce(string_agg(\"Content\"::text, '|' ORDER BY \"Id\"), '') FROM \"Evidences\""));
        Assert.Equal(revisionHash, await database.HashAsync("SELECT coalesce(string_agg(\"ContentJson\"::text, '|' ORDER BY \"Id\"), '') FROM \"AnalysisRevisions\""));
        Assert.Equal(draftHash, await database.HashAsync("SELECT coalesce(string_agg(\"Markdown\", '|' ORDER BY \"Id\"), '') FROM \"Drafts\""));
        Assert.Equal(checkpointHash, await database.HashAsync("SELECT coalesce(string_agg(\"Payload\"::text || \"CheckpointInfo\"::text, '|' ORDER BY \"Id\"), '') FROM \"WorkflowCheckpoints\""));

        // Backfill: solo OriginKind=Message, mai un dato documentale inventato.
        foreach (var table in new[] { "Evidences", "EvidenceExtractionVersions", "EvidenceExtractionJobs", "AnalysisEvidenceSelectionItems" })
        {
            Assert.True((long)(await database.ScalarAsync($"SELECT count(*) FROM \"{table}\""))! > 0);
            Assert.Equal(0L, await database.ScalarAsync($"SELECT count(*) FROM \"{table}\" WHERE \"OriginKind\" <> 'Message' OR \"PreparationId\" IS NOT NULL"));
        }

        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM \"EvidenceExtractionVersions\" WHERE \"ManifestHash\" IS NOT NULL"));
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM \"EvidenceDocumentStates\""));
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM \"DocumentPromotions\""));
        // Il default 'Message' resta per gli scrittori storici; un ramo documentale non può nascondersi dietro di esso (CK_*_Origin).
        Assert.Equal(4L, await database.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE column_name = 'OriginKind' AND column_default LIKE '%Message%'"));

        // I decoder per versione leggono lo storico come 1.0 e rifiutano di reinterpretarlo come 1.1.
        await using var read = database.NewContext();
        var versions = await read.EvidenceExtractionVersions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        var items = await read.Evidences.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, items.Count);
        foreach (var item in items)
        {
            var version = versions.Single(v => v.Id == item.VersionId);
            Assert.Equal(EvidenceOriginKind.Message, item.OriginKind);
            Assert.Equal("evidence-1.0.0", version.SchemaVersion);
            var decoded = EvidenceSchemaDispatch.DecodeContent(version.SchemaVersion, item.Content);
            Assert.NotNull(decoded.Legacy);
            Assert.Single(decoded.Legacy!.Sources);
            Assert.ThrowsAny<JsonException>(() => DocumentEvidenceCodec.DecodeContent(item.Content));
        }

        foreach (var revision in await read.AnalysisRevisions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal("1.0.0", revision.SchemaVersion);
            var decoded = AnalysisSchemaDispatch.Decode(revision.ContentJson);
            Assert.NotNull(decoded.V10);
            Assert.Null(decoded.V11);
            Assert.Throws<JsonException>(() => DocumentAnalysisContentJson.Deserialize(revision.ContentJson));
        }

        // La selezione storica resta leggibile per versione e per messaggio; bozza e report conservano citazioni e ID.
        var selected = await read.AnalysisEvidenceSelectionItems.AsNoTracking().OrderBy(i => i.MessageSequence).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([ids.M1, ids.M3], selected.Select(i => i.MessageId!.Value));
        Assert.All(selected, i => Assert.Equal(EvidenceOriginKind.Message, i.OriginKind));
        var draft = await read.Drafts.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Contains($"[fonte:{ids.SourceM1}]", draft.Markdown, StringComparison.Ordinal);
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM \"EvidenceCoverageReports\" WHERE \"CheckerVersion\" = 'coverage-v1'"));
    }

    [Fact]
    public async Task Down_returns_to_the_previous_schema_without_touching_history_and_refuses_once_document_evidence_exists()
    {
        await using var database = await TempDatabase.CreateAsync();
        await database.MigrateToAsync(LastMigration001);
        var ids = Legacy.New();
        await SeedLegacyAsync(database, ids);
        var (oldColumns, before) = await CaptureAsync(database);
        await database.MigrateToAsync(null);

        // Senza dati documentali la Down è non distruttiva: stesso schema precedente, stesse righe.
        await database.MigrateToAsync(Previous);
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name = 'Evidences' AND column_name = 'OriginKind'"));
        foreach (var table in LegacyTables)
        {
            Assert.Equal(before[table], await database.SnapshotAsync(table, oldColumns[table]));
        }

        // Riapplicata, e con una riga documentale, la Down è rifiutata e non cancella nulla.
        await database.MigrateToAsync(null);
        var d = await SeedDocumentsAsync(database, ids.User, ids.Project);
        await database.ExecuteAsync(Job(Guid.NewGuid(), d.Project, d.PreparationA1));
        await using var down = database.NewContext();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => down.GetService<IMigrator>().MigrateAsync(Previous, TestContext.Current.CancellationToken));

        Assert.Contains("Down rifiutata", failure.ToString());
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM \"EvidenceExtractionJobs\" WHERE \"OriginKind\" = 'Document'"));
        Assert.Equal(2L, await database.ScalarAsync("SELECT count(*) FROM \"Evidences\""));
    }

    [Fact]
    public async Task A_valid_document_branch_coexists_with_the_legacy_rows_and_mixed_lists_decode_by_schema_version()
    {
        await using var database = await TempDatabase.CreateAsync(migrate: true);
        var ids = Legacy.New();
        await SeedLegacyAsync(database, ids);
        var d = await SeedDocumentEvidenceAsync(database, ids);

        await using var read = database.NewContext();
        var versions = await read.EvidenceExtractionVersions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        var items = await read.Evidences.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2 + 3, versions.Count);   // due storiche + tre documentali (A1, A2, progetto estraneo)
        Assert.Equal(3, items.Count);          // due storiche + una documentale

        var decoded = new List<string>();
        foreach (var item in items)
        {
            var version = versions.Single(v => v.Id == item.VersionId);
            var content = EvidenceSchemaDispatch.DecodeContent(version.SchemaVersion, item.Content);
            decoded.Add(content.Legacy is not null ? "1.0" : "1.1");
            Assert.Equal(item.OriginKind == EvidenceOriginKind.Document, content.Document is not null);
        }

        Assert.Equal(["1.0", "1.0", "1.1"], decoded.Order());
        var documentItem = items.Single(i => i.OriginKind == EvidenceOriginKind.Document);
        Assert.Null(documentItem.MessageId);
        Assert.Equal(d.PreparationA1, documentItem.PreparationId);
        Assert.Equal(EvidenceSourceKind.DocumentPassage, Assert.Single(DocumentEvidenceCodec.DecodeContent(documentItem.Content).Sources).Kind);

        // Versione ignota o decoder sbagliato: rifiuto esplicito, nessuna reinterpretazione.
        var legacyItem = items.First(i => i.OriginKind == EvidenceOriginKind.Message);
        Assert.Throws<JsonException>(() => EvidenceSchemaDispatch.DecodeContent("evidence-1.1.0", legacyItem.Content));
        Assert.Throws<JsonException>(() => EvidenceSchemaDispatch.DecodeContent("evidence-1.0.0", documentItem.Content));
        Assert.Throws<NotSupportedException>(() => EvidenceSchemaDispatch.DecodeContent("evidence-9.9.9", documentItem.Content));

        // Stato corrente e promozione del documento: stessa semantica del messaggio, altro discriminante.
        await database.ExecuteAsync(State(d.Version1, d.Project, d.PreparationA1, d.EvidenceVersionA1, 1));
        await database.ExecuteAsync(Promotion(d.Project, d.Version1, null, null, d.PreparationA1, d.EvidenceVersionA1));
        // Il ramo 001 non vede le righe documentali.
        Assert.Equal(2L, await database.ScalarAsync("SELECT count(*) FROM \"EvidenceMessageStates\""));
        Assert.Equal(2L, await database.ScalarAsync("SELECT count(*) FROM \"EvidenceExtractionJobs\" WHERE \"OriginKind\" = 'Message'"));
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM \"AnalysisEvidenceSelectionItems\" WHERE \"OriginKind\" = 'Document'"));
    }

    public static TheoryData<string> InvalidRows() =>
    [
        "job-document-with-message", "job-message-without-message", "job-both-message-and-preparation", "job-document-other-project-preparation",
        "job-second-aggregator-for-preparation", "job-document-completed-without-result",
        "version-document-without-manifest", "version-document-legacy-schema", "version-document-with-message-fields", "version-message-with-manifest",
        "version-duplicate-number-for-preparation", "version-second-for-job", "version-document-other-project-preparation",
        "item-document-with-message", "item-document-without-preparation", "item-message-with-preparation", "item-document-other-project-preparation", "item-duplicate-key-in-version",
        "selection-document-with-message", "selection-without-origin-fields", "selection-duplicate-version", "selection-duplicate-preparation", "selection-message-with-preparation",
        "state-half-pointers", "state-preparation-of-another-version", "state-evidence-of-another-project", "state-negative-revision", "state-project-mismatch", "state-duplicate-version",
        "promotion-from-equals-to", "promotion-half-from", "promotion-preparation-of-another-version", "promotion-evidence-of-another-project",
    ];

    [Theory]
    [MemberData(nameof(InvalidRows))]
    public async Task Structural_constraints_reject_invalid_document_rows(string caseName)
    {
        await using var database = await TempDatabase.CreateAsync(migrate: true);
        var ids = Legacy.New();
        await SeedLegacyAsync(database, ids);
        var d = await SeedDocumentEvidenceAsync(database, ids);
        var id = Guid.NewGuid();
        var anyMessage = new JobOrigin(ids.Conversation, ids.M1, 1);

        if (caseName == "state-duplicate-version")
        {
            await database.ExecuteAsync(State(d.Version2, d.Project, null, null, 0));
        }

        var (sql, state) = caseName switch
        {
            "job-document-with-message" => (Job(id, d.Project, d.PreparationA2, "Document", anyMessage), PostgresErrorCodes.CheckViolation),
            "job-message-without-message" => (Job(id, d.Project, null, "Message"), PostgresErrorCodes.CheckViolation),
            "job-both-message-and-preparation" => (Job(id, d.Project, d.PreparationA2, "Message", anyMessage), PostgresErrorCodes.CheckViolation),
            "job-document-other-project-preparation" => (Job(id, d.Project, d.PreparationOtherFree), PostgresErrorCodes.ForeignKeyViolation),
            "job-second-aggregator-for-preparation" => (Job(id, d.Project, d.PreparationA1), PostgresErrorCodes.UniqueViolation),
            "job-document-completed-without-result" => (Job(id, d.Project, d.PreparationB, status: "Completed"), PostgresErrorCodes.CheckViolation),
            "version-document-without-manifest" => (DocVersion(id, d.Project, d.PreparationB, 1, d.JobB, manifest: null), PostgresErrorCodes.CheckViolation),
            "version-document-legacy-schema" => (DocVersion(id, d.Project, d.PreparationB, 1, d.JobB, schema: "evidence-1.0.0"), PostgresErrorCodes.CheckViolation),
            "version-document-with-message-fields" => (DocVersion(id, d.Project, d.PreparationB, 1, d.JobB, message: anyMessage), PostgresErrorCodes.CheckViolation),
            "version-message-with-manifest" => (MessageVersion(id, d.Project, ids, 9, ids.Job1, manifest: new string('a', 64)), PostgresErrorCodes.CheckViolation),
            "version-duplicate-number-for-preparation" => (DocVersion(id, d.Project, d.PreparationA1, 1, d.JobB), PostgresErrorCodes.UniqueViolation),
            "version-second-for-job" => (DocVersion(id, d.Project, d.PreparationA1, 2, d.JobA1), PostgresErrorCodes.UniqueViolation),
            "version-document-other-project-preparation" => (DocVersion(id, d.Project, d.PreparationOtherFree, 1, d.JobB), PostgresErrorCodes.ForeignKeyViolation),
            "item-document-with-message" => (Item(id, d.EvidenceVersionA1, d.Project, "Document", ids.M1, d.PreparationA1, "9e1"), PostgresErrorCodes.CheckViolation),
            "item-document-without-preparation" => (Item(id, d.EvidenceVersionA1, d.Project, "Document", null, null, "9e1"), PostgresErrorCodes.CheckViolation),
            "item-message-with-preparation" => (Item(id, ids.Version1, d.Project, "Message", ids.M1, d.PreparationA1, "e9"), PostgresErrorCodes.CheckViolation),
            "item-document-other-project-preparation" => (Item(id, d.EvidenceVersionA1, d.Project, "Document", null, d.PreparationOtherFree, "9e1"), PostgresErrorCodes.ForeignKeyViolation),
            "item-duplicate-key-in-version" => (Item(id, d.EvidenceVersionA1, d.Project, "Document", null, d.PreparationA1, "1e1"), PostgresErrorCodes.UniqueViolation),
            "selection-document-with-message" => (Selection(ids.Run, "Document", ids.M1, 1, d.PreparationA2, d.EvidenceVersionA2), PostgresErrorCodes.CheckViolation),
            "selection-without-origin-fields" => (Selection(ids.Run, "Document", null, null, null, d.EvidenceVersionA2), PostgresErrorCodes.CheckViolation),
            "selection-duplicate-version" => (Selection(ids.Run, "Message", ids.M2, 2, null, ids.Version1), PostgresErrorCodes.UniqueViolation),
            "selection-duplicate-preparation" => (Selection(ids.Run, "Document", null, null, d.PreparationA1, d.EvidenceVersionA2), PostgresErrorCodes.UniqueViolation),
            "selection-message-with-preparation" => (Selection(ids.Run, "Message", ids.M2, 2, d.PreparationA2, d.EvidenceVersionA2), PostgresErrorCodes.CheckViolation),
            "state-half-pointers" => (State(d.Version2, d.Project, d.PreparationB, null, 0), PostgresErrorCodes.CheckViolation),
            "state-preparation-of-another-version" => (State(d.Version2, d.Project, d.PreparationA1, d.EvidenceVersionA1, 1), PostgresErrorCodes.ForeignKeyViolation),
            "state-evidence-of-another-project" => (State(d.Version2, d.Project, d.PreparationB, d.EvidenceVersionOther, 1), PostgresErrorCodes.ForeignKeyViolation),
            "state-negative-revision" => (State(d.Version2, d.Project, null, null, -1), PostgresErrorCodes.CheckViolation),
            "state-project-mismatch" => (State(d.Version2, d.OtherProject, null, null, 0), PostgresErrorCodes.ForeignKeyViolation),
            "state-duplicate-version" => (State(d.Version2, d.Project, null, null, 1), PostgresErrorCodes.UniqueViolation),
            "promotion-from-equals-to" => (Promotion(d.Project, d.Version1, d.PreparationA1, d.EvidenceVersionA1, d.PreparationA1, d.EvidenceVersionA1), PostgresErrorCodes.CheckViolation),
            "promotion-half-from" => (Promotion(d.Project, d.Version1, d.PreparationA2, null, d.PreparationA1, d.EvidenceVersionA1), PostgresErrorCodes.CheckViolation),
            "promotion-preparation-of-another-version" => (Promotion(d.Project, d.Version2, null, null, d.PreparationA1, d.EvidenceVersionA1), PostgresErrorCodes.ForeignKeyViolation),
            "promotion-evidence-of-another-project" => (Promotion(d.Project, d.Version1, null, null, d.PreparationA1, d.EvidenceVersionOther), PostgresErrorCodes.ForeignKeyViolation),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };

        var failure = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(sql));
        Assert.Equal(state, failure.SqlState);
    }

    // ================== Dati e SQL grezzo ==================

    private sealed record Legacy(
        Guid User, Guid Project, Guid Conversation, Guid M1, Guid M2, Guid M3, Guid Job1, Guid Job2, Guid Version1, Guid Version2,
        Guid Evidence1, Guid Evidence2, Guid Run, Guid DocRun, Guid Rev1, Guid Rev2, Guid DraftId, Guid Item1, Guid Item2,
        Guid SourceM1, Guid SourceM3, Guid SourceRevision)
    {
        public static Legacy New() => new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    }

    private sealed record DocSet(
        Guid Project, Guid OtherProject, Guid Version1, Guid Version2, Guid PreparationA1, Guid PreparationA2, Guid PreparationB, Guid PreparationOtherFree, Guid PreparationOther,
        Guid JobA1 = default, Guid JobA2 = default, Guid JobB = default, Guid JobOther = default,
        Guid EvidenceVersionA1 = default, Guid EvidenceVersionA2 = default, Guid EvidenceVersionOther = default);

    private sealed record JobOrigin(Guid Conversation, Guid Message, long Sequence);

    private sealed record Raw(string Sql);

    private static string Lit(object? value) => value switch
    {
        null => "NULL",
        Raw raw => raw.Sql,
        Guid g => $"'{g}'",
        bool b => b ? "true" : "false",
        int or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Insert(string table, params (string Column, object? Value)[] columns) =>
        $"INSERT INTO \"{table}\" ({string.Join(",", columns.Select(c => $"\"{c.Column}\""))}) VALUES ({string.Join(",", columns.Select(c => Lit(c.Value)))})";

    private static string Update(string table, string where, params (string Column, object? Value)[] columns) =>
        $"UPDATE \"{table}\" SET {string.Join(",", columns.Select(c => $"\"{c.Column}\" = {Lit(c.Value)}"))} WHERE {where}";

    private static readonly Raw Now = new("now()");
    private static readonly Raw InOneHour = new("now() + interval '1 hour'");
    private static readonly string Hash = new('a', 64);

    private static string Job(Guid id, Guid project, Guid? preparation, string origin = "Document", JobOrigin? message = null, string status = "Queued") =>
        Insert("EvidenceExtractionJobs",
            ("Id", id), ("ProjectId", project), ("OriginKind", origin),
            ("ConversationId", message?.Conversation), ("MessageId", message?.Message), ("MessageSequence", message?.Sequence), ("PreparationId", preparation),
            ("Reason", "Initial"), ("Status", status), ("RequestedAt", Now), ("DeadlineAt", InOneHour),
            ("AttemptCount", 0), ("MaxAttempts", 3), ("FencingToken", 0), ("ExtractorConfig", "{}"));

    private static string DocVersion(Guid id, Guid project, Guid preparation, int number, Guid job, string? manifest = "", string schema = "evidence-1.1.0", JobOrigin? message = null) =>
        Insert("EvidenceExtractionVersions",
            ("Id", id), ("ProjectId", project), ("OriginKind", "Document"),
            ("ConversationId", message?.Conversation), ("MessageId", message?.Message), ("MessageSequence", message?.Sequence), ("PreparationId", preparation),
            ("ManifestHash", manifest == "" ? Hash : manifest), ("Version", number), ("JobId", job), ("SchemaVersion", schema),
            ("PromptVersion", "document-extraction-v2"), ("ModelId", "m"), ("Parameters", "{}"), ("EvidenceCount", 1), ("CreatedAt", Now));

    private static string MessageVersion(Guid id, Guid project, Legacy ids, int number, Guid job, string? manifest = null) =>
        Insert("EvidenceExtractionVersions",
            ("Id", id), ("ProjectId", project), ("OriginKind", "Message"), ("ConversationId", ids.Conversation), ("MessageId", ids.M1), ("MessageSequence", 1L),
            ("ManifestHash", manifest), ("Version", number), ("JobId", job), ("SchemaVersion", "evidence-1.0.0"),
            ("PromptVersion", "evidence-extraction-v1"), ("ModelId", "m"), ("Parameters", "{}"), ("EvidenceCount", 1), ("CreatedAt", Now));

    private static string Item(Guid id, Guid version, Guid project, string origin, Guid? message, Guid? preparation, string key) =>
        Insert("Evidences",
            ("Id", id), ("VersionId", version), ("ProjectId", project), ("OriginKind", origin), ("MessageId", message), ("PreparationId", preparation),
            ("Ordinal", 2), ("Key", key), ("Kind", "Requirement"), ("Polarity", "Affirmed"), ("Certainty", "Declared"), ("Statement", "Altro requisito."), ("Content", "{}"));

    private static string Selection(Guid run, string origin, Guid? message, long? sequence, Guid? preparation, Guid version) =>
        Insert("AnalysisEvidenceSelectionItems",
            ("RunId", run), ("OriginKind", origin), ("MessageId", message), ("MessageSequence", sequence), ("PreparationId", preparation), ("VersionId", version));

    private static string State(Guid documentVersion, Guid project, Guid? preparation, Guid? evidenceVersion, int revision) =>
        Insert("EvidenceDocumentStates",
            ("DocumentVersionId", documentVersion), ("ProjectId", project), ("CurrentPreparationId", preparation), ("CurrentEvidenceVersionId", evidenceVersion),
            ("Revision", revision), ("UpdatedAt", Now));

    private static string Promotion(Guid project, Guid documentVersion, Guid? fromPreparation, Guid? fromEvidence, Guid toPreparation, Guid toEvidence) =>
        Insert("DocumentPromotions",
            ("Id", Guid.NewGuid()), ("ProjectId", project), ("DocumentVersionId", documentVersion), ("FromPreparationId", fromPreparation), ("FromEvidenceVersionId", fromEvidence),
            ("ToPreparationId", toPreparation), ("ToEvidenceVersionId", toEvidence), ("PromotedAt", Now), ("Reason", "prima estrazione valida"));

    private static string DocumentContentJson(Guid preparation, Guid version, Guid artifact, Guid block) =>
        DocumentEvidenceCodec.EncodeContent(new DocumentEvidenceContent
        {
            SchemaVersion = "evidence-1.1.0", KindRationale = null,
            Structure = new DocumentEvidenceStructure { Subject = "Cliente A", Action = null, Object = null, Scope = null, Purpose = null, Instrument = null, Channel = null, Recipient = null },
            Qualifiers = [],
            Sources =
            [
                new PersistedSource
                {
                    SourceId = Guid.NewGuid(), Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Guid.NewGuid(), Part = 0,
                    MessageId = null, Sequence = null, DocumentId = Guid.NewGuid(), DocumentVersionId = version, PreparationId = preparation, ArtifactId = artifact, BlockId = block,
                    Start = 0, End = 20, Quote = "Cliente A | 5 ordini",
                },
            ],
        });

    /// <summary>Colonne e istantanea canonica per tabella prima dell'upgrade (le colonne nuove sono escluse dal confronto).</summary>
    private static async Task<(Dictionary<string, string[]> Columns, Dictionary<string, string> Snapshots)> CaptureAsync(TempDatabase database)
    {
        var columns = new Dictionary<string, string[]>();
        var snapshots = new Dictionary<string, string>();
        foreach (var table in LegacyTables)
        {
            columns[table] = await database.ColumnsAsync(table);
            snapshots[table] = await database.SnapshotAsync(table, columns[table]);
            Assert.NotEqual("[]", snapshots[table]);   // il dataset popola davvero ogni tabella controllata
        }

        return (columns, snapshots);
    }

    /// <summary>
    /// Database 001 completo: due messaggi PM e una risposta, due estrazioni (con una correzione), versioni correnti e promozioni, un'analisi
    /// completata con revisione AI e revisione PM confermata, selezione congelata e link, bozza con citazioni, report coverage-v1, checkpoint e
    /// telemetria. JSON volutamente non canonici; le righe sono scritte con le colonne esatte dello schema 001.
    /// </summary>
    private static async Task SeedLegacyAsync(TempDatabase database, Legacy x)
    {
        await database.SeedUserAndProjectAsync(x.User, x.Project, projectName: "Legacy");
        // Sullo schema 001 il discriminante non esiste; sullo schema nuovo ogni scrittore lo dichiara (nessun default).
        var hasOrigin = (long)(await database.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name = 'Evidences' AND column_name = 'OriginKind'"))! > 0;
        (string Column, object? Value)[] Origin(params (string Column, object? Value)[] columns) => hasOrigin ? [.. columns, ("OriginKind", "Message")] : columns;
        await database.ExecuteAsync(Insert("Conversations", ("Id", x.Conversation), ("ProjectId", x.Project), ("CreatedAt", Now), ("LastSequence", 3L), ("MafVersion", "1.26.0"), ("ContextThroughSequence", 0L), ("ChatSessionRevision", 0L)));
        await database.ExecuteAsync(Insert("ConversationMessages", ("Id", x.M1), ("ConversationId", x.Conversation), ("Sequence", 1L), ("Role", "User"), ("Body", "Il portale supporta 500 utenti."), ("CreatedAt", Now), ("ClientMessageId", Guid.NewGuid()), ("AuthorId", x.User)));
        var firstTurn = Guid.NewGuid();
        await database.ExecuteAsync(Insert("ChatTurns", ("Id", firstTurn), ("ConversationId", x.Conversation), ("TriggerMessageId", x.M1), ("SourceCutoffSequence", 1L), ("Status", "Queued"), ("RequestedAt", Now),
            ("DeadlineAt", new Raw("now() + interval '3 minutes'")), ("AttemptCount", 0), ("MaxAttempts", 3), ("FencingToken", 0L), ("AgentConfig", "{}"), ("SessionRevisionAtStart", 0L)));
        await database.ExecuteAsync(Insert("ConversationMessages", ("Id", x.M2), ("ConversationId", x.Conversation), ("Sequence", 2L), ("Role", "Assistant"), ("Body", "Intendi 500 utenti simultanei?"), ("CreatedAt", Now),
            ("AgentId", "ConversationalAgent"), ("ReplyToMessageId", x.M1), ("ChatTurnId", firstTurn)));
        await database.ExecuteAsync(Insert("ConversationMessages", ("Id", x.M3), ("ConversationId", x.Conversation), ("Sequence", 3L), ("Role", "User"), ("Body", "Intendevo 800 utenti."), ("CreatedAt", Now), ("ClientMessageId", Guid.NewGuid()), ("AuthorId", x.User)));
        await database.ExecuteAsync(Insert("ChatTurns", ("Id", Guid.NewGuid()), ("ConversationId", x.Conversation), ("TriggerMessageId", x.M3), ("SourceCutoffSequence", 3L), ("Status", "Queued"), ("RequestedAt", Now),
            ("DeadlineAt", new Raw("now() + interval '3 minutes'")), ("AttemptCount", 0), ("MaxAttempts", 3), ("FencingToken", 0L), ("AgentConfig", "{ \"b\": 2,   \"a\": [1, 2, {\"z\": null}] }"), ("SessionRevisionAtStart", 0L)));

        // Estrazioni: job → versione → job completato (FK circolare).
        foreach (var (job, version, message, sequence, evidence, statement, quote) in new[]
        {
            (x.Job1, x.Version1, x.M1, 1L, x.Evidence1, "Il portale supporta 500 utenti.", "Il portale supporta 500 utenti."),
            (x.Job2, x.Version2, x.M3, 3L, x.Evidence2, "Servono 800 utenti.", "Intendevo 800 utenti."),
        })
        {
            await database.ExecuteAsync(Insert("EvidenceExtractionJobs", Origin(("Id", job), ("ProjectId", x.Project), ("ConversationId", x.Conversation), ("MessageId", message), ("MessageSequence", sequence),
                ("Reason", "Initial"), ("Status", "Queued"), ("RequestedAt", Now), ("DeadlineAt", InOneHour), ("AttemptCount", 1), ("MaxAttempts", 3), ("FencingToken", 1L), ("ExtractorConfig", "{\"model\" :  \"m\"}"))));
            await database.ExecuteAsync(Insert("EvidenceExtractionVersions", Origin(("Id", version), ("ProjectId", x.Project), ("ConversationId", x.Conversation), ("MessageId", message), ("MessageSequence", sequence),
                ("Version", 1), ("JobId", job), ("SchemaVersion", "evidence-1.0.0"), ("PromptVersion", "evidence-extraction-v1"), ("ModelId", "m"), ("Parameters", "{\"seq\" : [3,  1]}"), ("EvidenceCount", 1), ("CreatedAt", Now))));
            await database.ExecuteAsync(Update("EvidenceExtractionJobs", $"\"Id\" = '{job}'", ("Status", "Completed"), ("ResultVersionId", version)));
            var content = "{ \"sources\": [ {\"role\":\"Primary\", \"quote\": " + JsonSerializer.Serialize(quote) + ", \"start\": 0, \"end\": " + quote.Length + ", \"sequence\": " + sequence + ", \"messageId\": \"" + message + "\"} ], " +
                "\"qualifiers\": [], \"kindRationale\": null, \"structure\": {\"subject\":null,\"action\":null,\"object\":null,\"scope\":null,\"purpose\":null,\"instrument\":null,\"channel\":null,\"recipient\":null} }";
            await database.ExecuteAsync(Insert("Evidences", Origin(("Id", evidence), ("VersionId", version), ("ProjectId", x.Project), ("MessageId", message), ("Ordinal", 1), ("Key", "e1"),
                ("Kind", "Requirement"), ("Polarity", "Affirmed"), ("Certainty", "Declared"), ("Statement", statement), ("Content", content))));
            await database.ExecuteAsync(Insert("EvidenceMessageStates", ("MessageId", message), ("ProjectId", x.Project), ("CurrentVersionId", version), ("Revision", 1), ("UpdatedAt", Now)));
            await database.ExecuteAsync(Insert("EvidencePromotions", ("Id", Guid.NewGuid()), ("ProjectId", x.Project), ("MessageId", message), ("ToVersionId", version), ("PromotedAt", Now), ("Reason", "prima estrazione valida"), ("RelationImpact", "{\"remapped\": 0,  \"stale\": 0}")));
        }

        await database.ExecuteAsync(Insert("EvidenceRelations", ("Id", Guid.NewGuid()), ("ProjectId", x.Project), ("VersionId", x.Version2), ("FromEvidenceId", x.Evidence2), ("ToEvidenceId", x.Evidence1), ("Type", "Corrects"), ("Basis", "Declared")));

        // Analisi completata: run → revisioni → run completato; la sintesi cita i messaggi tramite fonti 1.0.0.
        await database.ExecuteAsync(Insert("ProcessingRuns", ("Id", x.Run), ("ProjectId", x.Project), ("Kind", "Analysis"), ("ConversationId", x.Conversation), ("SourceCutoffSequence", 3L),
            ("RequestKey", Guid.NewGuid()), ("RequestedBy", x.User), ("RequestedAt", Now), ("Status", "Queued"), ("DeadlineAt", new Raw("now() + interval '20 minutes'")),
            ("WorkflowDefinitionVersion", "project-analysis-v3"), ("MafVersion", "1.26.0"), ("AgentConfig", "{\"model\" :  \"m\", \"thinking\": false}"), ("PromptVersions", "{\"analysis\":\"v3\"}"),
            ("AttemptCount", 0), ("MaxAttempts", 3), ("FencingToken", 0L), ("UpdatedAt", Now)));
        var ai = LegacyAnalysis(x, AnalysisContentState.Unvalidated);
        var confirmed = LegacyAnalysis(x, AnalysisContentState.PmConfirmed);
        await database.ExecuteAsync(Insert("AnalysisRevisions", ("Id", x.Rev1), ("AnalysisId", x.Run), ("Version", 1), ("Origin", "Ai"), ("CreatedBy", x.User), ("CreatedAt", Now),
            ("SchemaVersion", "1.0.0"), ("ContentJson", "  " + ai.Replace(",\"", ",  \"", StringComparison.Ordinal)), ("ValidationState", "Unvalidated")));
        await database.ExecuteAsync(Insert("AnalysisRevisions", ("Id", x.Rev2), ("AnalysisId", x.Run), ("Version", 2), ("ParentRevisionId", x.Rev1), ("Origin", "PmEdit"), ("CreatedBy", x.User), ("CreatedAt", Now),
            ("SchemaVersion", "1.0.0"), ("ContentJson", confirmed), ("ValidationState", "PmConfirmed"), ("ConfirmedBy", x.User), ("ConfirmedAt", Now)));
        await database.ExecuteAsync(Update("ProcessingRuns", $"\"Id\" = '{x.Run}'", ("Status", "Completed"), ("ResultRevisionId", x.Rev1)));
        await database.ExecuteAsync(Insert("AnalysisEvidenceSelections", ("RunId", x.Run), ("ProjectId", x.Project), ("CreatedAt", Now), ("ViewHash", Hash), ("View", "{\"items\": [ ]}")));
        await database.ExecuteAsync(Insert("AnalysisEvidenceSelectionItems", Origin(("RunId", x.Run), ("MessageId", x.M1), ("MessageSequence", 1L), ("VersionId", x.Version1))));
        await database.ExecuteAsync(Insert("AnalysisEvidenceSelectionItems", Origin(("RunId", x.Run), ("MessageId", x.M3), ("MessageSequence", 3L), ("VersionId", x.Version2))));
        await database.ExecuteAsync(Insert("AnalysisEvidenceLinks", ("RunId", x.Run), ("ItemId", x.Item1), ("EvidenceId", x.Evidence1), ("ProjectId", x.Project)));
        await database.ExecuteAsync(Insert("AnalysisEvidenceLinks", ("RunId", x.Run), ("ItemId", x.Item2), ("EvidenceId", x.Evidence2), ("ProjectId", x.Project)));
        await database.ExecuteAsync(Insert("WorkflowCheckpoints", ("Id", Guid.NewGuid()), ("ProcessingRunId", x.Run), ("WorkflowSessionId", "s1"), ("CheckpointKey", "k1"),
            ("CheckpointInfo", "{ \"k\" : 1 }"), ("Payload", "{\"b\": [3,  2, {\"x\":null}], \"a\": \"é\"}"), ("CommitSequence", 1L), ("CreatedAt", Now), ("MafVersion", "1.26.0"), ("WorkflowDefinitionVersion", "project-analysis-v3")));
        await database.ExecuteAsync(Insert("InferenceCalls", ("Id", Guid.NewGuid()), ("ProjectId", x.Project), ("ProcessingRunId", x.Run), ("Attempt", 1), ("FencingToken", 0L), ("Phase", "Analysis"),
            ("ModelId", "m"), ("StartedAt", Now), ("ReasoningRequested", false), ("ReasoningEffective", "off"), ("State", "Completed"), ("IsFinal", true), ("Config", "{}")));
        await database.ExecuteAsync(Insert("EvidenceCoverageReports", ("Id", Guid.NewGuid()), ("ProjectId", x.Project), ("AnalysisId", x.Run), ("RevisionId", x.Rev2), ("CheckerVersion", "coverage-v1"),
            ("InputHash", Hash), ("CreatedBy", x.User), ("CreatedAt", Now), ("DeterministicErrors", 0), ("SemanticRisks", 0), ("PmAssessments", 0), ("Report", "{\"checkerVersion\" : \"coverage-v1\", \"findings\": []}")));

        // Bozza dalla revisione confermata: run Document → bozza → run completato; la bozza cita le fonti della revisione.
        await database.ExecuteAsync(Insert("ProcessingRuns", ("Id", x.DocRun), ("ProjectId", x.Project), ("Kind", "Document"), ("AnalysisId", x.Run), ("InputRevisionId", x.Rev2),
            ("RequestKey", Guid.NewGuid()), ("RequestedBy", x.User), ("RequestedAt", Now), ("Status", "Queued"), ("DeadlineAt", new Raw("now() + interval '20 minutes'")),
            ("WorkflowDefinitionVersion", "project-document-v2"), ("MafVersion", "1.26.0"), ("AgentConfig", "{}"), ("PromptVersions", "{\"document\":\"v2\"}"),
            ("AttemptCount", 0), ("MaxAttempts", 3), ("FencingToken", 0L), ("UpdatedAt", Now)));
        await database.ExecuteAsync(Insert("Drafts", ("Id", x.DraftId), ("ProjectId", x.Project), ("ProcessingRunId", x.DocRun), ("AnalysisRevisionId", x.Rev2),
            ("Markdown", $"# Bozza\n\nIl portale supporta 800 utenti [fonte:{x.SourceM3}] e in origine 500 [fonte:{x.SourceM1}].\n"), ("CreatedAt", Now), ("Status", "Proposed"), ("ApprovalStatus", "NotApproved")));
        await database.ExecuteAsync(Update("ProcessingRuns", $"\"Id\" = '{x.DocRun}'", ("Status", "Completed"), ("DraftId", x.DraftId)));
    }

    private static string LegacyAnalysis(Legacy x, AnalysisContentState state)
    {
        AnalysisItem Goal(Guid id, string text, params Guid[] refs) => new() { Id = id, Text = text, Classification = AnalysisClassification.Declared, EvidenceRefs = refs };
        var content = new AnalysisContent
        {
            SchemaVersion = "1.0.0", AnalysisId = x.Run, ProjectId = x.Project, SourceCutoffSequence = 3, ValidationState = state,
            Sources =
            [
                new AnalysisSource { Id = x.SourceM1, Kind = AnalysisSourceKind.Message, MessageId = x.M1, MessageRole = AnalysisMessageRole.User, RevisionId = null },
                new AnalysisSource { Id = x.SourceM3, Kind = AnalysisSourceKind.Message, MessageId = x.M3, MessageRole = AnalysisMessageRole.User, RevisionId = null },
                new AnalysisSource { Id = x.SourceRevision, Kind = AnalysisSourceKind.PmRevision, MessageId = null, MessageRole = null, RevisionId = x.Rev2 },
            ],
            Goals = [Goal(x.Item1, "Servire 800 utenti.", x.SourceM3), Goal(x.Item2, "In origine 500 utenti.", x.SourceM1)],
            ContextAndScope = new AnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = [], OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = [],
        };
        return AnalysisContentJson.Serialize(content);
    }

    /// <summary>
    /// Due progetti con file, versioni e preparazioni: progetto principale (documento 1 con A1 Incomplete e A2 Extracting; documento 2 con B
    /// Extracting) e progetto estraneo (una preparazione libera e una che ospita lavoro e versione Evidence documentale).
    /// </summary>
    private static async Task<DocSet> SeedDocumentsAsync(TempDatabase database, Guid user, Guid project)
    {
        var other = Guid.NewGuid();
        await database.ExecuteAsync(Insert("Projects", ("Id", other), ("Name", "Altro"), ("CreatedBy", user), ("CreatedAt", Now)));
        var d = new DocSet(project, other, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var db = database.NewContext();
        var t0 = DateTimeOffset.UtcNow;
        var files = new[] { (project, Guid.NewGuid(), d.Version1), (project, Guid.NewGuid(), d.Version2), (other, Guid.NewGuid(), Guid.NewGuid()) };
        foreach (var (p, document, version) in files)
        {
            var receipt = Guid.NewGuid();
            db.ProjectDocuments.Add(new ProjectDocument { Id = document, ProjectId = p, DisplayName = "Doc " + document.ToString("N")[..6], CreatedBy = user });
            db.UploadReceipts.Add(new UploadReceipt { Id = receipt, ProjectId = p, DocumentId = document, RequestKey = Guid.NewGuid(), InputHash = new string('d', 64), ReservedBytes = 1, Status = UploadReceiptStatus.Receiving, ExpiresAt = t0.AddHours(1) });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.DocumentVersions.Add(new DocumentVersion { Id = version, ProjectId = p, DocumentId = document, Version = 1, OriginalName = "a.pdf", MediaType = "application/pdf", ByteLength = 5, Sha256 = new string('c', 64), StorageKey = "storage/" + version, UploadedBy = user, UploadReceiptId = receipt });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        DocumentPreparation Preparation(Guid id, Guid p, Guid version, int generation, PreparationStatus status) => new()
        {
            Id = id, ProjectId = p, DocumentVersionId = version, Generation = generation, Status = status, PolicyHash = new string('e', 64),
            RequestedAt = t0, HardDeadlineAt = t0.AddHours(1), EffectiveDeadlineAt = t0.AddHours(1),
        };

        var otherVersion = files[2].Item3;
        db.DocumentPreparations.AddRange(
            Preparation(d.PreparationA1, project, d.Version1, 1, PreparationStatus.Incomplete),
            Preparation(d.PreparationA2, project, d.Version1, 2, PreparationStatus.Extracting),
            Preparation(d.PreparationB, project, d.Version2, 1, PreparationStatus.Extracting),
            Preparation(d.PreparationOtherFree, other, otherVersion, 1, PreparationStatus.Incomplete),
            Preparation(d.PreparationOther, other, otherVersion, 2, PreparationStatus.Extracting));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return d;
    }

    /// <summary>Come <see cref="SeedDocumentsAsync"/> più aggregatori documentali, versioni Evidence 1.1, una evidenza con fonte DocumentPassage e una selezione di run.</summary>
    private static async Task<DocSet> SeedDocumentEvidenceAsync(TempDatabase database, Legacy ids)
    {
        var d = await SeedDocumentsAsync(database, ids.User, ids.Project);
        d = d with { JobA1 = Guid.NewGuid(), JobA2 = Guid.NewGuid(), JobB = Guid.NewGuid(), JobOther = Guid.NewGuid(), EvidenceVersionA1 = Guid.NewGuid(), EvidenceVersionA2 = Guid.NewGuid(), EvidenceVersionOther = Guid.NewGuid() };
        await database.ExecuteAsync(Job(d.JobA1, d.Project, d.PreparationA1));
        await database.ExecuteAsync(Job(d.JobA2, d.Project, d.PreparationA2));
        await database.ExecuteAsync(Job(d.JobB, d.Project, d.PreparationB));
        await database.ExecuteAsync(Job(d.JobOther, d.OtherProject, d.PreparationOther));
        await database.ExecuteAsync(DocVersion(d.EvidenceVersionA1, d.Project, d.PreparationA1, 1, d.JobA1));
        await database.ExecuteAsync(DocVersion(d.EvidenceVersionA2, d.Project, d.PreparationA2, 1, d.JobA2));
        await database.ExecuteAsync(DocVersion(d.EvidenceVersionOther, d.OtherProject, d.PreparationOther, 1, d.JobOther));
        await database.ExecuteAsync(Insert("Evidences",
            ("Id", Guid.NewGuid()), ("VersionId", d.EvidenceVersionA1), ("ProjectId", d.Project), ("OriginKind", "Document"), ("MessageId", null), ("PreparationId", d.PreparationA1),
            ("Ordinal", 1), ("Key", "1e1"), ("Kind", "Constraint"), ("Polarity", "Affirmed"), ("Certainty", "Declared"), ("Statement", "Massimo 5 ordini al giorno per il cliente A."),
            ("Content", DocumentContentJson(d.PreparationA1, d.Version1, Guid.NewGuid(), Guid.NewGuid()))));
        await database.ExecuteAsync(Selection(ids.Run, "Document", null, null, d.PreparationA1, d.EvidenceVersionA1));
        return d;
    }

    // ================== Database temporaneo ==================

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
                await database.MigrateToAsync(null);
            }

            return database;
        }

        public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

        /// <summary>Applica le migrazioni fino a quella indicata (tutte se null); un obiettivo precedente esegue le Down.</summary>
        public async Task MigrateToAsync(string? target)
        {
            await using var db = NewContext();
            await db.GetService<IMigrator>().MigrateAsync(target, TestContext.Current.CancellationToken);
        }

        public async Task SeedUserAndProjectAsync(Guid user, Guid project, string projectName = "Progetto")
        {
            await using (var db = NewContext())
            {
                db.Users.Add(new IdentityUser<Guid> { Id = user, UserName = "u" + user.ToString("N"), NormalizedUserName = "U" + user.ToString("N").ToUpperInvariant(), SecurityStamp = "s", ConcurrencyStamp = "c" });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await ExecuteAsync(Insert("Projects", ("Id", project), ("Name", projectName), ("CreatedBy", user), ("CreatedAt", Now)));
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

        public async Task<string[]> ColumnsAsync(string table) =>
            (await StringAsync($"SELECT string_agg(column_name, ',' ORDER BY column_name) FROM information_schema.columns WHERE table_name = '{table}'")).Split(',');

        /// <summary>Contenuto della tabella come jsonb canonico, limitato alle colonne preesistenti, ordinato per testo della riga.</summary>
        public Task<string> SnapshotAsync(string table, string[] columns) =>
            StringAsync(
                $"SELECT coalesce(jsonb_agg(r ORDER BY r::text), '[]')::text FROM (SELECT (SELECT jsonb_object_agg(k, v) FROM jsonb_each(to_jsonb(t)) AS e(k, v) " +
                $"WHERE k = ANY(ARRAY[{string.Join(",", columns.Select(c => "'" + c + "'"))}])) AS r FROM \"{table}\" t) q");

        public async Task<string> HashAsync(string sql) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(await StringAsync(sql))));

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
