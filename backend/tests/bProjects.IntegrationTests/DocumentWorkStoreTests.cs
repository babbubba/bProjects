using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace bProjects.IntegrationTests;

/// <summary>
/// T041 (PG) — store dei dati di lavoro e checkpoint dei nuovi owner: output, mappe e figli nello stesso commit fenced, blob hash-validato
/// prima del riferimento DB, replay idempotente, input/output discordanti come errore (mai update), lookup con compatibilità, writer stale
/// e guard della deadline del padre. Nessun exactly-once dell'inferenza: si prova solo l'unicità degli effetti. Nessun AI, nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentWorkStoreTests : IAsyncLifetime
{
    private const string Lease = "worker-a";
    private const string Schema = "evidence-1.1.0";
    private const string Validation = "v1";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string H(char c) => new(c, 64);

    private ParsingDatabase? _database;
    private SeededParse _seed = null!;
    private string _sandbox = null!;
    private string _root = null!;
    private DocumentFileStore _blobs = null!;

    public async ValueTask InitializeAsync()
    {
        if (TestEnvironment.PostgresAdminConnectionString is null)
        {
            return;
        }

        _database = await ParsingDatabase.CreateAsync();
        _seed = await _database.SeedParseAsync();
        _sandbox = Path.Combine(Path.GetTempPath(), "bprojects-work-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "store");
        _blobs = new DocumentFileStore(_root, [Path.Combine(_sandbox, "wwwroot")], NullLogger<DocumentFileStore>.Instance);
        await _database.ExecuteAsync($"""
            UPDATE "DocumentJobs" SET "Status" = 'Running', "LeaseOwner" = '{Lease}', "LeaseUntil" = now() + interval '5 minutes', "FencingToken" = 5, "AttemptCount" = 1, "StartedAt" = now(),
                   "WorkflowDefinitionVersion" = 'doc-batch-1' WHERE "Id" = '{_seed.Job}';
            UPDATE "DocumentPreparations" SET "Status" = 'Extracting' WHERE "Id" = '{_seed.Preparation}';
            """);
    }

    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        if (_sandbox is not null && Directory.Exists(_sandbox))
        {
            foreach (var file in Directory.EnumerateFiles(_sandbox, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_sandbox, recursive: true);
        }
    }

    private CheckpointWriter Writer(long fence = 5) => CheckpointWriter.ForDocumentJob(_seed.Job, Lease, fence, "doc-batch-1");

    private ParsingDatabase Db => _database ?? throw new InvalidOperationException();

    private (DocumentWorkStore Store, AppDbContext Context) NewStore(long fence = 5)
    {
        var context = Db.NewContext();
        return (new DocumentWorkStore(context, Writer(fence), _blobs), context);
    }

    private static WorkManifestInput Manifest(string runtime = "fp-1") => new(H('a'), H('b'), runtime, "doc-batch-1", "alg-1", H('d'), """{"nodes":["Load","PrepareBatch","LookupOutput"]}""");

    private static DerivedBatchInput Batch(string id, string input, params string[] parents) =>
        new(id, "extract", null, 0, 0, 0, parents, """{"ranges":[[0,10]]}""", 120, "measured", input, Schema, "p1");

    private static WorkOutputInput Inline(string batch, string input, string payload = """{"evidences":[],"noEvidenceReason":"nessuna"}""") =>
        new(batch, "InvokeBatch", input, CanonicalJson.Sha256Hex(payload), payload, null, null, """["u1","u2"]""", """{"u1":"e1"}""", null, Schema, Validation, null);

    private async Task PublishManifestAndBatchAsync(string batch, string input)
    {
        var (store, context) = NewStore();
        await using (context)
        {
            await store.PublishManifestAsync(Manifest(), Ct);
            await store.PublishBatchesAsync([Batch(batch, input)], Ct);
        }
    }

    private async Task<long> CountAsync(string table) => await Db.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\"");

    // ------------------------------------------------------------------ canonicalizzazione

    [Fact]
    public void Canonical_json_hash_ignores_property_order_and_whitespace_but_not_content()
    {
        Assert.Equal(
            CanonicalJson.Sha256Hex("""{"b":[1,2,{"y":null,"x":"è ☃"}],"a":true}"""),
            CanonicalJson.Sha256Hex("""  { "a" : true , "b" : [ 1, 2, { "x":"è ☃", "y":null } ] }  """));
        Assert.Equal("""{"a":1,"b":[2,3]}""", CanonicalJson.Canonicalize("""{"b":[2,3],"a":1}"""));
        Assert.NotEqual(CanonicalJson.Sha256Hex("""{"a":[1,2]}"""), CanonicalJson.Sha256Hex("""{"a":[2,1]}"""));   // l'ordine degli array è significativo
        Assert.Equal(64, CanonicalJson.Sha256Hex("{}").Length);
    }

    // ------------------------------------------------------------------ manifest e batch

    [Fact]
    public async Task Manifest_is_published_once_computes_its_hash_and_a_different_one_is_an_integrity_conflict()
    {
        TestEnvironment.RequirePostgres();
        var (store, context) = NewStore();
        await using var _ = context;

        var first = await store.PublishManifestAsync(Manifest(), Ct);
        var replay = await store.PublishManifestAsync(Manifest(), Ct);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(64, first.Hash.Length);
        Assert.Equal(1L, await CountAsync("WorkManifests"));
        var conflict = await Assert.ThrowsAsync<DocumentWorkException>(() => store.PublishManifestAsync(Manifest(runtime: "fp-2"), Ct));
        Assert.Equal(DocumentWorkException.IntegrityConflict, conflict.Code);
        Assert.Equal(first.Hash, await Db.ScalarAsync<string>("SELECT \"Hash\" FROM \"WorkManifests\""));   // mai aggiornato
    }

    [Fact]
    public async Task Batches_are_idempotent_and_the_same_batch_id_with_other_input_or_parents_is_a_conflict()
    {
        TestEnvironment.RequirePostgres();
        var (store, context) = NewStore();
        await using var _ = context;
        await store.PublishManifestAsync(Manifest(), Ct);

        await store.PublishBatchesAsync([Batch("b-1", H('1')), Batch("b-2", H('2'), H('7'), H('8'))], Ct);
        await store.PublishBatchesAsync([Batch("b-1", H('1')), Batch("b-2", H('2'), H('7'), H('8'))], Ct);   // replay identico
        Assert.Equal(2L, await CountAsync("DerivedBatches"));

        Assert.Equal(DocumentWorkException.IntegrityConflict, (await Assert.ThrowsAsync<DocumentWorkException>(() => store.PublishBatchesAsync([Batch("b-1", H('9'))], Ct))).Code);
        Assert.Equal(DocumentWorkException.IntegrityConflict, (await Assert.ThrowsAsync<DocumentWorkException>(() => store.PublishBatchesAsync([Batch("b-2", H('2'), H('8'), H('7'))], Ct))).Code);   // genitori in altro ordine
        // il fallimento di un elemento non lascia pubblicati gli altri della stessa chiamata
        await Assert.ThrowsAsync<DocumentWorkException>(() => store.PublishBatchesAsync([Batch("b-3", H('3')), Batch("b-1", H('9'))], Ct));
        Assert.Equal(2L, await CountAsync("DerivedBatches"));
    }

    // ------------------------------------------------------------------ commit dell'output

    [Fact]
    public async Task Commit_persists_output_map_and_children_together_with_the_fence_and_leaves_checkpoints_alone()
    {
        TestEnvironment.RequirePostgres();
        await PublishManifestAndBatchAsync("b-1", H('1'));
        var (store, context) = NewStore();
        await using var _ = context;

        var result = await store.CommitOutputAsync(Inline("b-1", H('1')), [Batch("b-2", H('2'), CanonicalJson.Sha256Hex("""{"evidences":[],"noEvidenceReason":"nessuna"}"""))], Ct);

        Assert.False(result.Replayed);
        Assert.Equal((5L, WorkOutputStatus.Committed, "u1"), (result.Output.CommittedFence, result.Output.Status, JsonDocument.Parse(result.Output.CoveredIdsJson).RootElement[0].GetString()));
        Assert.Equal(H('b'), await Db.ScalarAsync<string>("SELECT \"SourceHash\" FROM \"WorkManifests\""));
        Assert.Equal(result.Output.ManifestHash, await Db.ScalarAsync<string>("SELECT \"Hash\" FROM \"WorkManifests\""));
        Assert.Equal(2L, await CountAsync("DerivedBatches"));   // il figlio dipendente è pubblicato nello stesso commit
        Assert.Equal("""{"u1": "e1"}""", await Db.ScalarAsync<string>("SELECT \"MapsJson\"::text FROM \"WorkOutputs\""));
        Assert.Equal(0L, await CountAsync("WorkflowCheckpoints"));   // il checkpoint nativo è una scrittura separata
        // una sola identità durevole: l'attempt/fence non entra nell'hash logico
        Assert.Equal(H('1'), await Db.ScalarAsync<string>("SELECT \"InputHash\" FROM \"WorkOutputs\""));
    }

    [Fact]
    public async Task Replay_with_identical_input_returns_the_same_output_even_under_a_newer_fence()
    {
        TestEnvironment.RequirePostgres();
        await PublishManifestAndBatchAsync("b-1", H('1'));
        var (store, context) = NewStore();
        await using var _ = context;
        var first = await store.CommitOutputAsync(Inline("b-1", H('1')), null, Ct);

        // nuovo lease (reclaim): fence 6; il vecchio output resta con CommittedFence 5
        await Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"FencingToken\" = 6, \"LeaseOwner\" = 'worker-b' WHERE \"Id\" = '{_seed.Job}'");
        await using var newContext = Db.NewContext();
        var newer = new DocumentWorkStore(newContext, CheckpointWriter.ForDocumentJob(_seed.Job, "worker-b", 6, "doc-batch-1"), _blobs);
        var replay = await newer.CommitOutputAsync(Inline("b-1", H('1')), null, Ct);

        Assert.True(replay.Replayed);
        Assert.Equal((first.Output.Id, 5L), (replay.Output.Id, replay.Output.CommittedFence));
        Assert.Equal(1L, await CountAsync("WorkOutputs"));

        // il vecchio writer, ormai stale, non può nemmeno rileggere-e-confermare
        await Assert.ThrowsAsync<CheckpointFencedException>(() => store.CommitOutputAsync(Inline("b-1", H('1')), null, Ct));
    }

    [Fact]
    public async Task A_batch_with_another_input_hash_or_another_output_is_an_integrity_error_and_never_an_update()
    {
        TestEnvironment.RequirePostgres();
        await PublishManifestAndBatchAsync("b-1", H('1'));
        var (store, context) = NewStore();
        await using var _ = context;
        var first = await store.CommitOutputAsync(Inline("b-1", H('1')), null, Ct);

        var otherInput = await Assert.ThrowsAsync<DocumentWorkException>(() => store.CommitOutputAsync(Inline("b-1", H('9')), [Batch("b-x", H('5'))], Ct));
        var otherOutput = await Assert.ThrowsAsync<DocumentWorkException>(() => store.CommitOutputAsync(Inline("b-1", H('1'), """{"evidences":[],"noEvidenceReason":"altro"}"""), null, Ct));

        Assert.Equal(DocumentWorkException.IntegrityConflict, otherInput.Code);
        Assert.Equal(DocumentWorkException.IntegrityConflict, otherOutput.Code);
        Assert.Equal(first.Output.OutputHash, await Db.ScalarAsync<string>("SELECT \"OutputHash\" FROM \"WorkOutputs\""));
        Assert.Equal(H('1'), await Db.ScalarAsync<string>("SELECT \"InputHash\" FROM \"WorkOutputs\""));
        Assert.Equal(1L, await CountAsync("DerivedBatches"));   // il figlio del tentativo rifiutato non è stato pubblicato
    }

    [Fact]
    public async Task A_commit_without_manifest_batch_or_with_a_wrong_inline_hash_is_rejected_and_writes_nothing()
    {
        TestEnvironment.RequirePostgres();
        var (store, context) = NewStore();
        await using var _ = context;

        Assert.Equal(DocumentWorkException.IntegrityConflict, (await Assert.ThrowsAsync<DocumentWorkException>(() => store.CommitOutputAsync(Inline("b-1", H('1')), [Batch("b-2", H('2'))], Ct))).Code);   // manca il manifest
        await store.PublishManifestAsync(Manifest(), Ct);
        Assert.Equal(DocumentWorkException.IntegrityConflict, (await Assert.ThrowsAsync<DocumentWorkException>(() => store.CommitOutputAsync(Inline("b-1", H('1')), null, Ct))).Code);   // manca il batch
        await store.PublishBatchesAsync([Batch("b-1", H('1'))], Ct);
        var corrupted = Inline("b-1", H('1')) with { OutputHash = H('0') };   // hash che non corrisponde al payload canonico
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, (await Assert.ThrowsAsync<DocumentWorkException>(() => store.CommitOutputAsync(corrupted, null, Ct))).Code);
        Assert.Equal(0L, await CountAsync("WorkOutputs"));
        Assert.Equal(1L, await CountAsync("DerivedBatches"));
    }

    // ------------------------------------------------------------------ blob

    [Fact]
    public async Task A_blob_output_is_hash_validated_before_the_database_reference()
    {
        TestEnvironment.RequirePostgres();
        await PublishManifestAndBatchAsync("b-1", H('1'));
        await PublishBatchAsync("b-2", H('2'));
        var (store, context) = NewStore();
        await using var _ = context;
        var bytes = Encoding.UTF8.GetBytes("""{"evidences":[{"key":"e1"}],"note":"payload voluminoso"}""");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        // blob assente: nessun riferimento DB e nessun Completed
        var missing = Blob("b-1", H('1'), sha, bytes.Length, DocumentFileStore.KeyFor(_seed.Project, Guid.NewGuid()));
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, (await Assert.ThrowsAsync<DocumentWorkException>(() => store.CommitOutputAsync(missing, null, Ct))).Code);

        // blob presente ma corrotto rispetto all'hash dichiarato
        var written = await _blobs.WriteAsync(_seed.Project, Guid.NewGuid(), new MemoryStream("altri byte".ToUpperInvariant().Select(c => (byte)c).ToArray()), 1000, Ct);
        var corrupt = Blob("b-1", H('1'), sha, bytes.Length, written.StorageKey);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, (await Assert.ThrowsAsync<DocumentWorkException>(() => store.CommitOutputAsync(corrupt, null, Ct))).Code);

        // chiave di un altro progetto: rifiutata
        var otherProject = (await Db.SeedProjectAsync()).Project;
        var foreign = await _blobs.WriteAsync(otherProject, Guid.NewGuid(), new MemoryStream(bytes), 1000, Ct);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, (await Assert.ThrowsAsync<DocumentWorkException>(() => store.CommitOutputAsync(Blob("b-1", H('1'), sha, bytes.Length, foreign.StorageKey), null, Ct))).Code);
        Assert.Equal(0L, await CountAsync("WorkOutputs"));

        // blob valido: commit, poi lookup che rilegge e riconvalida i byte
        var good = await _blobs.WriteAsync(_seed.Project, Guid.NewGuid(), new MemoryStream(bytes), 1000, Ct);
        var committed = await store.CommitOutputAsync(Blob("b-1", H('1'), sha, bytes.Length, good.StorageKey), null, Ct);
        Assert.Equal(good.StorageKey, committed.Output.StorageKey);
        Assert.Null(committed.Output.PayloadJson);
        var hit = await store.LookupAsync("b-1", H('1'), new WorkLookupExpectation(Schema, Validation), Ct);
        Assert.Equal(WorkLookupKind.Hit, hit.Kind);
        Assert.Equal(Encoding.UTF8.GetString(bytes), hit.Payload);

        // dopo il commit il blob sparisce o viene alterato: hit negato, nessuna nuova generazione nascosta
        var path = Path.Combine(_root, "objects", good.StorageKey.Replace('/', Path.DirectorySeparatorChar));
        File.SetAttributes(path, FileAttributes.Normal);   // lo store scrive i blob in sola lettura
        await File.WriteAllBytesAsync(path, "alterato"u8.ToArray(), Ct);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, (await Assert.ThrowsAsync<DocumentWorkException>(() => store.LookupAsync("b-1", H('1'), new WorkLookupExpectation(Schema, Validation), Ct))).Code);
        File.Delete(path);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, (await Assert.ThrowsAsync<DocumentWorkException>(() => store.LookupAsync("b-1", H('1'), new WorkLookupExpectation(Schema, Validation), Ct))).Code);
        Assert.Equal(1L, await CountAsync("WorkOutputs"));
    }

    private WorkOutputInput Blob(string batch, string input, string sha, long length, string key) =>
        new(batch, "InvokeBatch", input, sha, null, key, length, """["u1"]""", null, null, Schema, Validation, null);

    private async Task PublishBatchAsync(string batch, string input)
    {
        var (store, context) = NewStore();
        await using (context)
        {
            await store.PublishBatchesAsync([Batch(batch, input)], Ct);
        }
    }

    // ------------------------------------------------------------------ lookup e compatibilità

    [Fact]
    public async Task Lookup_misses_hits_and_refuses_incompatible_outputs_without_ever_updating()
    {
        TestEnvironment.RequirePostgres();
        await PublishManifestAndBatchAsync("b-1", H('1'));
        var (store, context) = NewStore();
        await using var _ = context;
        var expected = new WorkLookupExpectation(Schema, Validation);

        Assert.Equal(WorkLookupKind.Miss, (await store.LookupAsync("b-1", H('1'), expected, Ct)).Kind);
        Assert.Equal(WorkLookupKind.Miss, (await store.LookupAsync("assente", H('1'), expected, Ct)).Kind);

        await store.CommitOutputAsync(Inline("b-1", H('1')), null, Ct);
        var hit = await store.LookupAsync("b-1", H('1'), expected, Ct);
        Assert.Equal(WorkLookupKind.Hit, hit.Kind);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse("""{"noEvidenceReason":"nessuna","evidences":[]}""").RootElement, JsonDocument.Parse(hit.Payload!).RootElement));

        // stesso batch con input diverso, schema o validazione diversi: incompatibile, non Miss (niente reinferenza per sovrascrivere)
        foreach (var lookup in new Func<Task>[]
        {
            () => store.LookupAsync("b-1", H('9'), expected, Ct),
            () => store.LookupAsync("b-1", H('1'), expected with { SchemaVersion = "evidence-1.0.0" }, Ct),
            () => store.LookupAsync("b-1", H('1'), expected with { ValidationVersion = "v2" }, Ct),
        })
        {
            Assert.Equal(DocumentWorkException.CheckpointIncompatible, (await Assert.ThrowsAsync<DocumentWorkException>(lookup)).Code);
        }

        Assert.Equal(1L, await CountAsync("WorkOutputs"));
    }

    // ------------------------------------------------------------------ writer stale e deadline del padre

    [Fact]
    public async Task Stale_expired_or_parent_expired_writers_cannot_commit_manifest_batches_or_outputs()
    {
        TestEnvironment.RequirePostgres();
        await PublishManifestAndBatchAsync("b-1", H('1'));

        // fencing token diverso
        {
            var (stale, context) = NewStore(fence: 4);
            await using (context)
            {
                await Assert.ThrowsAsync<CheckpointFencedException>(() => stale.CommitOutputAsync(Inline("b-1", H('1')), null, Ct));
                await Assert.ThrowsAsync<CheckpointFencedException>(() => stale.PublishBatchesAsync([Batch("b-9", H('9'))], Ct));
            }
        }

        // padre scaduto sul clock del database: il lease del figlio non basta
        await Db.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"EffectiveDeadlineAt\" = now() - interval '1 second' WHERE \"Id\" = '{_seed.Preparation}'");
        {
            var (store, context) = NewStore();
            await using (context)
            {
                await Assert.ThrowsAsync<CheckpointFencedException>(() => store.CommitOutputAsync(Inline("b-1", H('1')), null, Ct));
            }
        }

        // padre non più attivo
        await Db.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"EffectiveDeadlineAt\" = \"HardDeadlineAt\", \"Status\" = 'Failed', \"ErrorCode\" = 'x' WHERE \"Id\" = '{_seed.Preparation}'");
        {
            var (store, context) = NewStore();
            await using (context)
            {
                await Assert.ThrowsAsync<CheckpointFencedException>(() => store.CommitOutputAsync(Inline("b-1", H('1')), null, Ct));
            }
        }

        // padre vivo ma deadline locale del figlio passata, poi lease scaduto
        await Db.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"Status\" = 'Extracting', \"ErrorCode\" = NULL WHERE \"Id\" = '{_seed.Preparation}'; UPDATE \"DocumentJobs\" SET \"DeadlineAt\" = now() - interval '1 second' WHERE \"Id\" = '{_seed.Job}'");
        {
            var (store, context) = NewStore();
            await using (context)
            {
                await Assert.ThrowsAsync<CheckpointFencedException>(() => store.CommitOutputAsync(Inline("b-1", H('1')), null, Ct));
            }
        }

        await Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"DeadlineAt\" = now() + interval '1 hour', \"LeaseUntil\" = now() - interval '1 second' WHERE \"Id\" = '{_seed.Job}'");
        {
            var (store, context) = NewStore();
            await using (context)
            {
                await Assert.ThrowsAsync<CheckpointFencedException>(() => store.CommitOutputAsync(Inline("b-1", H('1')), null, Ct));
            }
        }

        Assert.Equal(0L, await CountAsync("WorkOutputs"));
        Assert.Equal(1L, await CountAsync("DerivedBatches"));
    }

    [Fact]
    public async Task Concurrent_identical_commits_produce_exactly_one_output()
    {
        TestEnvironment.RequirePostgres();
        await PublishManifestAndBatchAsync("b-1", H('1'));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            var (store, context) = NewStore();
            await using (context)
            {
                return await store.CommitOutputAsync(Inline("b-1", H('1')), null, Ct);
            }
        }));

        Assert.Equal(1L, await CountAsync("WorkOutputs"));
        Assert.Single(results.Select(r => r.Output.Id).Distinct());
        Assert.Equal(1, results.Count(r => !r.Replayed));
    }

    // ------------------------------------------------------------------ checkpoint dei nuovi owner

    [Fact]
    public async Task Document_job_checkpoints_roundtrip_with_consecutive_sequences_and_are_fenced()
    {
        TestEnvironment.RequirePostgres();
        var payload = JsonDocument.Parse("""{"$type":"x","cursor":{"batch":"b-1"},"hashes":["a","b"]}""").RootElement;
        await using var context = Db.NewContext();
        var store = new PostgresCheckpointStore(context, Writer());

        var a = await store.CreateCheckpointAsync("s1", payload);
        var b = await store.CreateCheckpointAsync("s1", payload, a);
        Assert.Equal([a, b], await store.RetrieveIndexAsync("s1"));
        Assert.True(JsonElement.DeepEquals(payload, await store.RetrieveCheckpointAsync("s1", b)));
        Assert.Equal(["1", "2"], await Strings($"SELECT \"CommitSequence\"::text FROM \"WorkflowCheckpoints\" WHERE \"DocumentJobId\" = '{_seed.Job}' ORDER BY 1"));
        Assert.Equal(_seed.Project, Guid.Parse((await Strings($"SELECT DISTINCT \"ProjectId\"::text FROM \"WorkflowCheckpoints\" WHERE \"DocumentJobId\" = '{_seed.Job}'")).Single()));
        Assert.Equal(["doc-batch-1"], await Strings("SELECT DISTINCT \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\""));

        // un altro owner non vede i checkpoint di questo
        await using var otherContext = Db.NewContext();
        var other = new PostgresCheckpointStore(otherContext, CheckpointWriter.ForDocumentJob(Guid.NewGuid(), Lease, 5, "doc-batch-1"));
        Assert.Empty(await other.RetrieveIndexAsync("s1"));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await other.RetrieveCheckpointAsync("s1", a));

        // writer stale: nulla scritto
        await using var staleContext = Db.NewContext();
        await Assert.ThrowsAsync<CheckpointFencedException>(async () => await new PostgresCheckpointStore(staleContext, Writer(fence: 4)).CreateCheckpointAsync("s1", payload, b));
        Assert.Equal(2L, await CountAsync("WorkflowCheckpoints"));
    }

    [Fact]
    public async Task Concurrent_document_job_checkpoints_get_distinct_consecutive_sequences_under_the_owner_lock()
    {
        TestEnvironment.RequirePostgres();
        var json = JsonDocument.Parse("{}").RootElement;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var context = Db.NewContext();
            await new PostgresCheckpointStore(context, Writer()).CreateCheckpointAsync($"s{i % 2}", json);
        }));

        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8"], await Strings($"SELECT \"CommitSequence\"::text FROM \"WorkflowCheckpoints\" WHERE \"DocumentJobId\" = '{_seed.Job}' ORDER BY 1"));
    }

    [Fact]
    public async Task Document_job_checkpoint_writes_follow_the_current_parent_deadline()
    {
        TestEnvironment.RequirePostgres();
        var json = JsonDocument.Parse("{}").RootElement;
        await using var context = Db.NewContext();
        var store = new PostgresCheckpointStore(context, Writer());
        await store.CreateCheckpointAsync("s1", json);

        await Db.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"EffectiveDeadlineAt\" = now() - interval '1 second' WHERE \"Id\" = '{_seed.Preparation}'");
        await Assert.ThrowsAsync<CheckpointFencedException>(async () => await store.CreateCheckpointAsync("s1", json));
        await Db.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"EffectiveDeadlineAt\" = \"HardDeadlineAt\" WHERE \"Id\" = '{_seed.Preparation}'; UPDATE \"DocumentJobs\" SET \"DeadlineAt\" = now() - interval '1 second' WHERE \"Id\" = '{_seed.Job}'");
        await Assert.ThrowsAsync<CheckpointFencedException>(async () => await store.CreateCheckpointAsync("s1", json));
        Assert.Equal(1L, await CountAsync("WorkflowCheckpoints"));
        // la lettura per il resume non richiede autorità
        Assert.Single(await store.RetrieveIndexAsync("s1"));
    }

    [Fact]
    public async Task A_checkpoint_of_another_definition_is_incompatible_and_never_returned()
    {
        TestEnvironment.RequirePostgres();
        var json = JsonDocument.Parse("""{"k":1}""").RootElement;
        await using var context = Db.NewContext();
        var info = await new PostgresCheckpointStore(context, Writer()).CreateCheckpointAsync("s1", json);

        // un owner che si aspetta un'altra definizione/versione MAF non ripristina il vecchio checkpoint
        await using var otherContext = Db.NewContext();
        var other = new PostgresCheckpointStore(otherContext, CheckpointWriter.ForDocumentJob(_seed.Job, Lease, 5, "doc-batch-2"));
        var incompatible = await Assert.ThrowsAsync<CheckpointIncompatibleException>(async () => await other.RetrieveCheckpointAsync("s1", info));
        Assert.Equal(DocumentWorkException.CheckpointIncompatible, incompatible.Code);
        await using var mafContext = Db.NewContext();
        var maf = new PostgresCheckpointStore(mafContext, Writer() with { MafVersion = "9.9.9" });
        await Assert.ThrowsAsync<CheckpointIncompatibleException>(async () => await maf.RetrieveCheckpointAsync("s1", info));

        // stessa definizione: leggibile
        Assert.True(JsonElement.DeepEquals(json, await new PostgresCheckpointStore(context, Writer()).RetrieveCheckpointAsync("s1", info)));
    }

    [Fact]
    public async Task Chat_turn_checkpoints_use_the_turn_lease_and_the_conversation_project()
    {
        TestEnvironment.RequirePostgres();
        var (user, project) = await Db.SeedProjectAsync();
        var (conversation, turn, message) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Db.ExecuteAsync($"""
            INSERT INTO "Conversations" ("Id","ProjectId","CreatedAt","LastSequence","MafVersion","ContextThroughSequence","ChatSessionRevision") VALUES ('{conversation}','{project}',now(),1,'1.26.0',0,0);
            INSERT INTO "ConversationMessages" ("Id","ConversationId","Sequence","Role","Body","CreatedAt","ClientMessageId","AuthorId") VALUES ('{message}','{conversation}',1,'User','ciao',now(),'{Guid.NewGuid()}','{user}');
            INSERT INTO "ChatTurns" ("Id","ConversationId","TriggerMessageId","SourceCutoffSequence","Status","RequestedAt","DeadlineAt","AttemptCount","MaxAttempts","LeaseOwner","LeaseUntil","FencingToken","SessionRevisionAtStart")
                VALUES ('{turn}','{conversation}','{message}',1,'Running',now(),now() + interval '3 minutes',1,3,'{Lease}',now() + interval '1 minute',3,0);
            """);
        var json = JsonDocument.Parse("{}").RootElement;
        await using var context = Db.NewContext();
        var store = new PostgresCheckpointStore(context, CheckpointWriter.ForChatTurn(turn, Lease, 3, "chat-docs-1"));

        var info = await store.CreateCheckpointAsync("s1", json);

        Assert.Equal([info], await store.RetrieveIndexAsync("s1"));
        Assert.Equal(project, Guid.Parse((await Strings($"SELECT \"ProjectId\"::text FROM \"WorkflowCheckpoints\" WHERE \"ChatTurnId\" = '{turn}'")).Single()));
        await using var staleContext = Db.NewContext();
        await Assert.ThrowsAsync<CheckpointFencedException>(async () => await new PostgresCheckpointStore(staleContext, CheckpointWriter.ForChatTurn(turn, Lease, 2, "chat-docs-1")).CreateCheckpointAsync("s1", json));
    }

    // ------------------------------------------------------------------ telemetria

    [Fact]
    public void Inference_owner_carries_the_document_job()
    {
        var owner = new InferenceOwner(null, null, 1, 5, DocumentJobId: Guid.NewGuid());
        Assert.NotNull(owner.DocumentJobId);
    }

    private async Task<List<string>> Strings(string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var reader = await new Npgsql.NpgsqlCommand(sql, connection).ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
