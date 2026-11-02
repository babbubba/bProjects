using bProjects.App.AI;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T040 (PG) — dati di lavoro del workflow documentale: WorkManifest, DerivedBatch (append-only), WorkOutput Committed, checkpoint con tre
/// owner (XOR e FK/progetto), InferenceCalls con il quarto owner DocumentJob. Vincoli verificati su PostgreSQL reale; nessun AI, nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentWorkSchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string H(char c) => new(c, 64);

    // ------------------------------------------------------------------ roundtrip

    [Fact]
    public async Task Manifest_batch_and_output_roundtrip_with_hashes_and_jsonb()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var seed = await database.SeedParseAsync();

        await using (var db = database.NewContext())
        {
            db.Add(Manifest(seed));
            await db.SaveChangesAsync(Ct);
            db.Add(Batch(seed, "b-1", H('1'), parents: [H('9'), H('8')]));
            await db.SaveChangesAsync(Ct);
            db.Add(Output(seed, "b-1", H('1'), H('2')));
            await db.SaveChangesAsync(Ct);
        }

        await using var read = database.NewContext();
        var manifest = await read.Set<WorkManifest>().SingleAsync(Ct);
        Assert.Equal((WorkOwnerKind.DocumentJob, seed.Job), (manifest.OwnerKind, manifest.OwnerId));
        Assert.Equal([H('a'), H('b'), "fp", "def-1", "alg-1", H('d'), H('e')],
            [manifest.ConfigHash, manifest.SourceHash, manifest.RuntimeFingerprint, manifest.DefinitionVersion, manifest.AlgorithmVersion, manifest.PolicyHash, manifest.Hash]);
        Assert.Equal("""{"nodes": ["Load", "PrepareBatch"]}""", await database.ScalarAsync<string>($"SELECT \"PlanJson\"::text FROM \"WorkManifests\" WHERE \"Id\" = '{manifest.Id}'"));

        var batch = await read.Set<DerivedBatch>().SingleAsync(Ct);
        Assert.Equal("b-1", batch.BatchId);
        Assert.Equal($"[\"{H('9')}\", \"{H('8')}\"]", await database.ScalarAsync<string>("SELECT \"ParentOutputHashes\"::text FROM \"DerivedBatches\""));   // l'ordine dei genitori è significativo

        var output = await read.Set<WorkOutput>().SingleAsync(Ct);
        Assert.Equal(WorkOutputStatus.Committed, output.Status);
        Assert.Equal((H('1'), H('2'), H('e')), (output.InputHash, output.OutputHash, output.ManifestHash));
        Assert.Equal("Committed", await database.ScalarAsync<string>("SELECT \"Status\" FROM \"WorkOutputs\""));
        Assert.True(output.CommittedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    // ------------------------------------------------------------------ unique e vincoli

    [Fact]
    public async Task Manifest_is_unique_per_owner_and_hashes_are_validated()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var seed = await database.SeedParseAsync();
        await using var db = database.NewContext();
        db.Add(Manifest(seed));
        await db.SaveChangesAsync(Ct);

        await using var other = database.NewContext();
        other.Add(Manifest(seed));
        Assert.Equal("23505", (await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync(Ct))).SqlState());

        var badHash = Manifest(seed);
        badHash.OwnerId = Guid.NewGuid();
        badHash.Hash = "NOT-A-HASH";
        await using var third = database.NewContext();
        third.Add(badHash);
        Assert.Equal("23514", (await Assert.ThrowsAsync<DbUpdateException>(() => third.SaveChangesAsync(Ct))).SqlState());
    }

    [Fact]
    public async Task Derived_batch_is_unique_per_owner_and_batch_id_and_needs_a_manifest()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var seed = await database.SeedParseAsync();

        await using (var orphan = database.NewContext())
        {
            orphan.Add(Batch(seed, "senza-manifest", H('1')));
            Assert.Equal("23503", (await Assert.ThrowsAsync<DbUpdateException>(() => orphan.SaveChangesAsync(Ct))).SqlState());
        }

        await using (var db = database.NewContext())
        {
            db.Add(Manifest(seed));
            await db.SaveChangesAsync(Ct);
            db.Add(Batch(seed, "b-1", H('1')));
            await db.SaveChangesAsync(Ct);
        }

        await using var duplicate = database.NewContext();
        duplicate.Add(Batch(seed, "b-1", H('7')));   // stesso batch, input diverso: non è un nuovo batch
        Assert.Equal("23505", (await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync(Ct))).SqlState());
    }

    [Fact]
    public async Task Output_is_unique_per_batch_even_with_a_different_input_hash_and_must_be_committed()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var seed = await database.SeedParseAsync();
        await SeedManifestAndBatchAsync(database, seed, "b-1", H('1'));

        await using (var db = database.NewContext())
        {
            db.Add(Output(seed, "b-1", H('1'), H('2')));
            await db.SaveChangesAsync(Ct);
        }

        // stessa chiave (owner,batch), altro inputHash e altro output: la unique(owner,batchId) lo rifiuta, nessun upsert
        await using (var other = database.NewContext())
        {
            other.Add(Output(seed, "b-1", H('5'), H('6')));
            Assert.Equal("23505", (await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync(Ct))).SqlState());
        }

        // l'inputHash di un output deve coincidere con quello del batch (FK composta)
        await SeedBatchAsync(database, seed, "b-2", H('3'));
        await using (var mismatched = database.NewContext())
        {
            mismatched.Add(Output(seed, "b-2", H('4'), H('2')));
            Assert.Equal("23503", (await Assert.ThrowsAsync<DbUpdateException>(() => mismatched.SaveChangesAsync(Ct))).SqlState());
        }

        // solo Committed; payload inline oppure su storage; hash esadecimali
        await database.ExecuteAsync("ALTER TABLE \"WorkOutputs\" DISABLE TRIGGER USER");
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync("UPDATE \"WorkOutputs\" SET \"Status\" = 'Partial'"))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync("UPDATE \"WorkOutputs\" SET \"PayloadJson\" = NULL, \"StorageKey\" = NULL"))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync("UPDATE \"WorkOutputs\" SET \"OutputHash\" = 'xyz'"))).SqlState);
    }

    [Fact]
    public async Task Output_manifest_hash_must_match_the_owner_manifest()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var seed = await database.SeedParseAsync();
        await SeedManifestAndBatchAsync(database, seed, "b-1", H('1'));

        await using var db = database.NewContext();
        var output = Output(seed, "b-1", H('1'), H('2'));
        output.ManifestHash = H('f');
        db.Add(output);
        Assert.Equal("23503", (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct))).SqlState());
    }

    [Fact]
    public async Task Work_rows_are_append_only()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var seed = await database.SeedParseAsync();
        await SeedManifestAndBatchAsync(database, seed, "b-1", H('1'));
        await using (var db = database.NewContext())
        {
            db.Add(Output(seed, "b-1", H('1'), H('2')));
            await db.SaveChangesAsync(Ct);
        }

        foreach (var sql in new[]
        {
            "UPDATE \"WorkManifests\" SET \"PolicyHash\" = '" + H('0') + "'",
            "DELETE FROM \"WorkManifests\"",
            "UPDATE \"DerivedBatches\" SET \"Ordinal\" = 9",
            "DELETE FROM \"DerivedBatches\"",
            "UPDATE \"WorkOutputs\" SET \"OutputHash\" = '" + H('0') + "'",
            "DELETE FROM \"WorkOutputs\"",
        })
        {
            Assert.Equal("23001", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(sql))).SqlState);   // restrict_violation dal trigger append-only
        }
    }

    // ------------------------------------------------------------------ checkpoint: owner

    [Fact]
    public async Task Checkpoint_owner_is_exclusive_and_new_owners_carry_a_project_consistent_with_the_owner()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var seed = await database.SeedParseAsync();
        var otherProject = (await database.SeedProjectAsync()).Project;

        // DocumentJob: ammesso, con il proprio progetto
        await database.ExecuteAsync(InsertCheckpoint("DocumentJobId", seed.Job, seed.Project, "s1", "k1", 1));
        // progetto di un altro owner: rifiutato dalla FK composta (DocumentJobId, ProjectId)
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(InsertCheckpoint("DocumentJobId", seed.Job, otherProject, "s1", "k2", 2)))).SqlState);
        // senza progetto per i nuovi owner: rifiutato
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(InsertCheckpoint("DocumentJobId", seed.Job, null, "s1", "k3", 3)))).SqlState);
        // nessun owner, oppure due owner: rifiutato dal CHECK XOR
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(InsertCheckpoint(null, null, seed.Project, "s1", "k4", 4)))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            InsertCheckpoint("DocumentJobId", seed.Job, seed.Project, "s1", "k5", 5).Replace("\"DocumentJobId\"", "\"DocumentJobId\",\"ChatTurnId\"").Replace($"'{seed.Job}'", $"'{seed.Job}','{Guid.NewGuid()}'")))).SqlState);
        // unique per owner: stessa (sessione, chiave) e stessa CommitSequence
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(InsertCheckpoint("DocumentJobId", seed.Job, seed.Project, "s1", "k1", 9)))).SqlState);
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(InsertCheckpoint("DocumentJobId", seed.Job, seed.Project, "s1", "k6", 1)))).SqlState);
        // il parent è un checkpoint dello stesso owner e sessione
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(InsertCheckpoint("DocumentJobId", seed.Job, seed.Project, "s1", "k7", 7, parent: "assente")))).SqlState);
        await database.ExecuteAsync(InsertCheckpoint("DocumentJobId", seed.Job, seed.Project, "s1", "k8", 8, parent: "k1"));
    }

    [Fact]
    public async Task Chat_turn_checkpoints_are_supported_and_legacy_run_checkpoints_stay_without_project()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var (user, project) = await database.SeedProjectAsync();
        var (conversation, turn, message) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await database.ExecuteAsync($"""
            INSERT INTO "Conversations" ("Id","ProjectId","CreatedAt","LastSequence","MafVersion","ContextThroughSequence","ChatSessionRevision") VALUES ('{conversation}','{project}',now(),1,'1.26.0',0,0);
            INSERT INTO "ConversationMessages" ("Id","ConversationId","Sequence","Role","Body","CreatedAt","ClientMessageId","AuthorId") VALUES ('{message}','{conversation}',1,'User','ciao',now(),'{Guid.NewGuid()}','{user}');
            INSERT INTO "ChatTurns" ("Id","ConversationId","TriggerMessageId","SourceCutoffSequence","Status","RequestedAt","DeadlineAt","AttemptCount","MaxAttempts","FencingToken","SessionRevisionAtStart")
                VALUES ('{turn}','{conversation}','{message}',1,'Running',now(),now() + interval '3 minutes',1,3,1,0);
            """);
        await database.ExecuteAsync(InsertCheckpoint("ChatTurnId", turn, project, "s1", "k1", 1));
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(InsertCheckpoint("ChatTurnId", turn, project, "s1", "k1", 2)))).SqlState);

        // owner storico: ProcessingRunId senza ProjectId resta valido
        var run = Guid.NewGuid();
        await database.ExecuteAsync($"""
            INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","ConversationId","SourceCutoffSequence","RequestKey","RequestedBy","RequestedAt","Status","DeadlineAt","WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","FencingToken","UpdatedAt")
            VALUES ('{run}','{project}','Analysis','{conversation}',1,'{Guid.NewGuid()}','{user}',now(),'Queued',now() + interval '3 minutes','project-analysis-v3','1.26.0',0,3,0,now());
            """);
        await database.ExecuteAsync(InsertCheckpoint("ProcessingRunId", run, null, "s1", "k1", 1));
        Assert.Equal(1L, await database.ScalarAsync<long>($"SELECT count(*) FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = '{run}' AND \"ProjectId\" IS NULL"));
    }

    // ------------------------------------------------------------------ InferenceCalls

    [Fact]
    public async Task Inference_calls_accept_a_document_job_as_the_fourth_exclusive_owner()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var seed = await database.SeedParseAsync();

        await database.ExecuteAsync(InsertCall(seed.Project, seed.Job, isFinal: false));
        await database.ExecuteAsync(InsertCall(seed.Project, seed.Job, isFinal: true));
        // una sola chiamata finale per DocumentJob
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(InsertCall(seed.Project, seed.Job, isFinal: true)))).SqlState);
        // owner esclusivo: nessuno oppure due
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(InsertCall(seed.Project, null, isFinal: false)))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            InsertCall(seed.Project, seed.Job, isFinal: false).Replace("\"DocumentJobId\"", "\"DocumentJobId\",\"ProcessingRunId\"").Replace($"'{seed.Job}'", $"'{seed.Job}','{Guid.NewGuid()}'")))).SqlState);
        // il progetto della chiamata è quello del job (FK composta)
        var otherProject = (await database.SeedProjectAsync()).Project;
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(InsertCall(otherProject, seed.Job, isFinal: false)))).SqlState);
    }

    // ------------------------------------------------------------------ storico e modello

    [Fact]
    public async Task There_is_no_next_executor_table_and_the_model_matches_the_migration()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var db = database.NewContext();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        Assert.Equal(0L, await database.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND (table_name ILIKE '%NextExecutor%' OR column_name ILIKE '%NextExecutor%' OR column_name ILIKE '%NextNode%')"));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%DocumentWork'"));
    }

    // ------------------------------------------------------------------ supporto

    internal static WorkManifest Manifest(SeededParse seed) => new()
    {
        Id = Guid.NewGuid(), ProjectId = seed.Project, OwnerKind = WorkOwnerKind.DocumentJob, OwnerId = seed.Job,
        ConfigHash = H('a'), SourceHash = H('b'), RuntimeFingerprint = "fp", DefinitionVersion = "def-1", AlgorithmVersion = "alg-1", PolicyHash = H('d'),
        PlanJson = """{"nodes":["Load","PrepareBatch"]}""", Hash = H('e'),
    };

    internal static DerivedBatch Batch(SeededParse seed, string batchId, string inputHash, string[]? parents = null) => new()
    {
        Id = Guid.NewGuid(), ProjectId = seed.Project, OwnerKind = WorkOwnerKind.DocumentJob, OwnerId = seed.Job, BatchId = batchId,
        Stage = "extract", View = null, Pass = 0, Level = 0, Ordinal = 0,
        ParentOutputHashes = System.Text.Json.JsonSerializer.Serialize(parents ?? []), InputIdsJson = """{"ranges":[[0,10]]}""",
        MeasuredTokens = 120, TokenProvenance = "measured", InputHash = inputHash, SchemaVersion = "evidence-1.1.0", PromptVersion = "p1",
    };

    internal static WorkOutput Output(SeededParse seed, string batchId, string inputHash, string outputHash) => new()
    {
        Id = Guid.NewGuid(), ProjectId = seed.Project, OwnerKind = WorkOwnerKind.DocumentJob, OwnerId = seed.Job, BatchId = batchId, NodeId = "InvokeBatch",
        InputHash = inputHash, OutputHash = outputHash, PayloadJson = """{"evidences":[]}""", CoveredIdsJson = """["u1"]""", SchemaVersion = "evidence-1.1.0",
        ManifestHash = H('e'), Status = WorkOutputStatus.Committed, ValidationVersion = "v1", CommittedFence = 5,
    };

    private static async Task SeedManifestAndBatchAsync(ParsingDatabase database, SeededParse seed, string batchId, string inputHash)
    {
        await using var db = database.NewContext();
        db.Add(Manifest(seed));
        await db.SaveChangesAsync(Ct);
        db.Add(Batch(seed, batchId, inputHash));
        await db.SaveChangesAsync(Ct);
    }

    private static async Task SeedBatchAsync(ParsingDatabase database, SeededParse seed, string batchId, string inputHash)
    {
        await using var db = database.NewContext();
        db.Add(Batch(seed, batchId, inputHash));
        await db.SaveChangesAsync(Ct);
    }

    private static string InsertCheckpoint(string? ownerColumn, Guid? owner, Guid? project, string session, string key, int sequence, string? parent = null) =>
        "INSERT INTO \"WorkflowCheckpoints\" (" + (ownerColumn is null ? "" : $"\"{ownerColumn}\",") +
        "\"ProjectId\",\"Id\",\"WorkflowSessionId\",\"CheckpointKey\",\"ParentCheckpointKey\",\"CheckpointInfo\",\"Payload\",\"CommitSequence\",\"CreatedAt\",\"MafVersion\",\"WorkflowDefinitionVersion\") VALUES (" +
        (owner is null ? "" : $"'{owner}',") +
        $"{(project is null ? "NULL" : $"'{project}'")},'{Guid.NewGuid()}','{session}','{key}',{(parent is null ? "NULL" : $"'{parent}'")},'{{}}','{{}}',{sequence},now(),'1.26.0','def-1')";

    private static string InsertCall(Guid project, Guid? job, bool isFinal) =>
        "INSERT INTO \"InferenceCalls\" (" + (job is null ? "" : "\"DocumentJobId\",") +
        "\"Id\",\"ProjectId\",\"Attempt\",\"FencingToken\",\"Phase\",\"ModelId\",\"StartedAt\",\"ReasoningRequested\",\"ReasoningEffective\",\"State\",\"IsFinal\",\"Config\") VALUES (" +
        (job is null ? "" : $"'{job}',") +
        $"'{Guid.NewGuid()}','{project}',1,1,'Extraction','m',now(),false,'off','Completed',{(isFinal ? "true" : "false")},'{{}}')";
}

internal static class DbUpdateExceptionExtensions
{
    public static string? SqlState(this DbUpdateException exception) => (exception.InnerException as PostgresException)?.SqlState;
}
