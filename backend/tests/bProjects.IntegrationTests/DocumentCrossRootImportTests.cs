using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace bProjects.IntegrationTests;

/// <summary>
/// T099 (PG, fixture dei root) — riuso cross-root dopo un retry PM: confronto esatto di manifesto, identità del batch e versioni PRIMA dell'import;
/// riga nel nuovo owner con DerivedFromOutputHash; mapping deterministico del solo id del root d'origine; payload non rimappabile mai importato;
/// output del vecchio root, hash e contatori intatti; nessuna chiamata AI; un output già presente non si sovrascrive; writer stale non scrive.
/// Nessun LLM, nessun DGX: lo store è esercitato direttamente con root costruiti a fixture (nessuna dipendenza da T097/UI).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentCrossRootImportTests : IAsyncLifetime
{
    private const string Lease = "worker-a";
    private const string Schema = "evidence-1.1.0";
    private const string Validation = "v1";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string H(char c) => new(c, 64);

    private ParsingDatabase? _database;
    private SeededParse _old = null!;
    private Guid _newPreparation;
    private Guid _newJob;
    private string _sandbox = null!;
    private DocumentFileStore _blobs = null!;

    private ParsingDatabase Db => _database ?? throw new InvalidOperationException();

    public async ValueTask InitializeAsync()
    {
        if (TestEnvironment.PostgresAdminConnectionString is null)
        {
            return;
        }

        _database = await ParsingDatabase.CreateAsync();
        _old = await _database.SeedParseAsync();
        _sandbox = Path.Combine(Path.GetTempPath(), "bprojects-xroot-" + Guid.NewGuid().ToString("N"));
        _blobs = new DocumentFileStore(Path.Combine(_sandbox, "store"), [Path.Combine(_sandbox, "wwwroot")], NullLogger<DocumentFileStore>.Instance);
        await Db.ExecuteAsync($"""
            UPDATE "DocumentJobs" SET "Status" = 'Running', "LeaseOwner" = '{Lease}', "LeaseUntil" = now() + interval '30 minutes', "FencingToken" = 5, "AttemptCount" = 1, "StartedAt" = now(),
                   "WorkflowDefinitionVersion" = 'doc-batch-1' WHERE "Id" = '{_old.Job}';
            UPDATE "DocumentPreparations" SET "Status" = 'Extracting' WHERE "Id" = '{_old.Preparation}';
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

    private static WorkManifestInput Manifest(char policy = 'd') => new(H('a'), H('b'), "fp-1", "doc-batch-1", "alg-1", H(policy), """{"nodes":["Load","PrepareBatch"]}""");

    private static DerivedBatchInput Batch(string id, string input, string schema = Schema, params string[] parents) =>
        new(id, "extract", null, 0, 0, 0, parents, """{"ranges":[[0,10]]}""", 120, "measured", input, schema, "p1");

    private static WorkOutputInput Inline(string batch, string input, string payload) =>
        new(batch, "InvokeBatch", input, CanonicalJson.Sha256Hex(payload), payload, null, null, """["u1"]""", """{"u1":"e1"}""", null, Schema, Validation, null);

    private (DocumentWorkStore Store, AppDbContext Context) Store(Guid job, long fence = 5)
    {
        var context = Db.NewContext();
        return (new DocumentWorkStore(context, CheckpointWriter.ForDocumentJob(job, Lease, fence, "doc-batch-1"), _blobs), context);
    }

    /// <summary>Scrive nel vecchio root manifesto, batch e output, poi lo porta in Failed come lo lascerebbe il worker.</summary>
    private async Task<WorkOutput> OldOutputAsync(string batch, string input, string payload, char policy = 'd', string batchSchema = Schema)
    {
        var (store, context) = Store(_old.Job);
        await using var _ = context;
        await store.PublishManifestAsync(Manifest(policy), Ct);
        await store.PublishBatchesAsync([Batch(batch, input, batchSchema)], Ct);
        var committed = (await store.CommitOutputAsync(Inline(batch, input, payload), null, Ct)).Output;
        await Db.ExecuteAsync($"""
            UPDATE "DocumentJobs" SET "Status" = 'Failed', "ErrorCode" = 'document_incomplete', "ErrorMessage" = 'x', "CompletedAt" = now(), "LeaseOwner" = NULL, "LeaseUntil" = NULL WHERE "Id" = '{_old.Job}';
            UPDATE "DocumentPreparations" SET "Status" = 'Failed', "ErrorCode" = 'document_incomplete' WHERE "Id" = '{_old.Preparation}';
            """);
        return committed;
    }

    /// <summary>Retry PM: nuova Preparation e nuovo job collegati al terminale (RetryOfId), in esecuzione con lease e fence propri.</summary>
    private async Task AddRetryRootAsync(bool linked = true)
    {
        _newPreparation = Guid.NewGuid();
        _newJob = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var hard = now.AddHours(1);
        await using var db = Db.NewContext();
        db.DocumentPreparations.Add(new DocumentPreparation
        {
            Id = _newPreparation, ProjectId = _old.Project, DocumentVersionId = _old.Version, Generation = 2, Status = PreparationStatus.Queued, RetryOfId = linked ? _old.Preparation : null,
            PolicyHash = H('e'), RequestedAt = now, HardDeadlineAt = hard, EffectiveDeadlineAt = hard,
        });
        await db.SaveChangesAsync(Ct);
        db.DocumentJobs.Add(new DocumentJob
        {
            Id = _newJob, ProjectId = _old.Project, PreparationId = _newPreparation, Kind = DocumentJobKind.Parse, Status = DocumentJobStatus.Queued, RequestKey = Guid.NewGuid(),
            RetryOfId = linked ? _old.Job : null, RequestedBy = _old.User, RequestedAt = now, DeadlineAt = hard, MaxAttempts = 3,
            ConfigJson = """{"policyHash":"x","ParseTimeoutSeconds":300,"MaxPages":1000,"MaxBlocks":100000,"MaxExpandedBytes":250000000,"MaxDerivedBytes":250000000}""",
        });
        await db.SaveChangesAsync(Ct);
        (await db.DocumentPreparations.SingleAsync(p => p.Id == _newPreparation, Ct)).ParseJobId = _newJob;
        await db.SaveChangesAsync(Ct);
        await Db.ExecuteAsync($"""
            UPDATE "DocumentJobs" SET "Status" = 'Running', "LeaseOwner" = '{Lease}', "LeaseUntil" = now() + interval '30 minutes', "FencingToken" = 7, "AttemptCount" = 1, "StartedAt" = now(),
                   "WorkflowDefinitionVersion" = 'doc-batch-1' WHERE "Id" = '{_newJob}';
            UPDATE "DocumentPreparations" SET "Status" = 'Extracting' WHERE "Id" = '{_newPreparation}';
            """);
    }

    private async Task PublishNewAsync(string batch, string input, char policy = 'd', string schema = Schema)
    {
        var (store, context) = Store(_newJob, 7);
        await using var _ = context;
        await store.PublishManifestAsync(Manifest(policy), Ct);
        await store.PublishBatchesAsync([Batch(batch, input, schema)], Ct);
    }

    private async Task<long> OutputsOfAsync(Guid owner) => await Db.ScalarAsync<long>($"""SELECT count(*) FROM "WorkOutputs" WHERE "OwnerId" = '{owner}'""");

    private static readonly WorkLookupExpectation Expected = new(Schema, Validation);

    [Fact]
    public async Task A_compatible_output_is_imported_into_the_new_owner_with_derived_hash_and_no_ai_and_the_old_root_is_untouched()
    {
        TestEnvironment.RequirePostgres();
        const string payload = """{"evidences":[],"noEvidenceReason":"nessuna"}""";
        var old = await OldOutputAsync("old-b", H('1'), payload);
        await AddRetryRootAsync();
        await PublishNewAsync("new-b", H('1'));
        var calls = await Db.ScalarAsync<long>("""SELECT count(*) FROM "InferenceCalls" """);

        var (store, context) = Store(_newJob, 7);
        await using var _ = context;
        var result = await store.TryImportAsync("new-b", Expected, Ct);

        Assert.Equal(WorkImportKind.Imported, result.Kind);
        Assert.Equal(_old.Job, result.SourceOwnerId);
        Assert.Equal(old.OutputHash, result.Output!.DerivedFromOutputHash);
        Assert.Equal(old.OutputHash, result.Output.OutputHash);   // stesso payload, stesso hash
        Assert.Equal(_newJob, result.Output.OwnerId);
        Assert.Equal(7L, result.Output.CommittedFence);
        Assert.Equal(await Db.ScalarAsync<string>($"""SELECT "Hash" FROM "WorkManifests" WHERE "OwnerId" = '{_newJob}'"""), result.Output.ManifestHash);
        Assert.Equal(calls, await Db.ScalarAsync<long>("""SELECT count(*) FROM "InferenceCalls" """));   // nessun invio AI né riserva
        Assert.Equal(WorkLookupKind.Hit, (await store.LookupAsync("new-b", H('1'), Expected, Ct)).Kind);
        var oldAfter = await Db.NewContext().Set<WorkOutput>().AsNoTracking().SingleAsync(o => o.OwnerId == _old.Job, Ct);
        Assert.Equal(old.Id, oldAfter.Id);
        Assert.Equal(old.OutputHash, oldAfter.OutputHash);
        Assert.Null(oldAfter.DerivedFromOutputHash);
        Assert.Equal(old.CommittedFence, oldAfter.CommittedFence);
        Assert.Equal(1L, await OutputsOfAsync(_old.Job));

        var replay = await store.TryImportAsync("new-b", Expected, Ct);   // seconda richiesta: nulla di nuovo
        Assert.Equal(WorkImportKind.Replayed, replay.Kind);
        Assert.Equal(result.Output.Id, replay.Output!.Id);
        Assert.Equal(1L, await OutputsOfAsync(_newJob));
    }

    [Fact]
    public async Task Only_the_origin_root_id_is_remapped_deterministically_and_the_hash_follows_the_new_payload()
    {
        TestEnvironment.RequirePostgres();
        var oldPayload = $$"""{"root":"{{_old.Job}}","source":"{{_old.Version}}"}""";
        var old = await OldOutputAsync("old-b", H('1'), oldPayload);
        await AddRetryRootAsync();
        await PublishNewAsync("new-b", H('1'));
        var (store, context) = Store(_newJob, 7);
        await using var _ = context;

        var result = await store.TryImportAsync("new-b", Expected, Ct);

        Assert.Equal(WorkImportKind.Imported, result.Kind);
        var expectedPayload = $$"""{"root":"{{_newJob}}","source":"{{_old.Version}}"}""";   // la fonte (versione) non cambia mai
        Assert.Equal(CanonicalJson.Canonicalize(expectedPayload), CanonicalJson.Canonicalize(result.Output!.PayloadJson!));
        Assert.Equal(CanonicalJson.Sha256Hex(expectedPayload), result.Output.OutputHash);
        Assert.Equal(old.OutputHash, result.Output.DerivedFromOutputHash);
        Assert.NotEqual(old.OutputHash, result.Output.OutputHash);
        Assert.Contains(_old.Job.ToString(), (await Db.NewContext().Set<WorkOutput>().AsNoTracking().SingleAsync(o => o.OwnerId == _old.Job, Ct)).PayloadJson!);   // vecchio payload intatto
    }

    [Fact]
    public async Task A_stored_payload_carrying_the_origin_run_is_not_remappable_and_is_never_imported()
    {
        TestEnvironment.RequirePostgres();
        var bytes = System.Text.Encoding.UTF8.GetBytes($$"""{"root":"{{_old.Job}}"}""");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var stored = await _blobs.WriteAsync(_old.Project, Guid.NewGuid(), new MemoryStream(bytes), bytes.Length + 1, Ct);
        var (oldStore, oldContext) = Store(_old.Job);
        await using (oldContext)
        {
            await oldStore.PublishManifestAsync(Manifest(), Ct);
            await oldStore.PublishBatchesAsync([Batch("old-b", H('1'))], Ct);
            await oldStore.CommitOutputAsync(new WorkOutputInput("old-b", "InvokeBatch", H('1'), stored.Sha256, null, stored.StorageKey, bytes.Length, """["u1"]""", null, null, Schema, Validation, null), null, Ct);
        }

        Assert.Equal(hash, stored.Sha256);
        await Db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "Status"='Failed',"ErrorCode"='x',"ErrorMessage"='x',"CompletedAt"=now(),"LeaseOwner"=NULL,"LeaseUntil"=NULL WHERE "Id"='{_old.Job}'; UPDATE "DocumentPreparations" SET "Status"='Failed',"ErrorCode"='x' WHERE "Id"='{_old.Preparation}';""");
        await AddRetryRootAsync();
        await PublishNewAsync("new-b", H('1'));
        var (store, context) = Store(_newJob, 7);
        await using var _ = context;

        var result = await store.TryImportAsync("new-b", Expected, Ct);

        Assert.Equal(WorkImportKind.NotRemappable, result.Kind);
        Assert.Equal(0L, await OutputsOfAsync(_newJob));
        Assert.Equal(1L, await OutputsOfAsync(_old.Job));
    }

    [Theory]
    [InlineData("policy")]    // budget/policy diverso
    [InlineData("schema")]    // versione schema diversa dall'atteso
    [InlineData("batch")]     // identità del batch (schema del batch) diversa
    public async Task An_incompatible_candidate_is_not_reused_not_overwritten_and_leaves_the_old_root_intact(string what)
    {
        TestEnvironment.RequirePostgres();
        var old = await OldOutputAsync("old-b", H('1'), """{"ok":true}""", batchSchema: what == "batch" ? "evidence-OLD" : Schema);
        await AddRetryRootAsync();
        await PublishNewAsync("new-b", H('1'), policy: what == "policy" ? 'f' : 'd');
        var (store, context) = Store(_newJob, 7);
        await using var _ = context;

        var result = await store.TryImportAsync("new-b", what == "schema" ? new WorkLookupExpectation("evidence-9.9.9", Validation) : Expected, Ct);

        Assert.Equal(WorkImportKind.Incompatible, result.Kind);
        Assert.NotNull(result.Reason);
        Assert.Equal(0L, await OutputsOfAsync(_newJob));
        var oldAfter = await Db.NewContext().Set<WorkOutput>().AsNoTracking().SingleAsync(o => o.OwnerId == _old.Job, Ct);
        Assert.Equal(old.OutputHash, oldAfter.OutputHash);
        Assert.Equal(old.Id, oldAfter.Id);
    }

    [Fact]
    public async Task A_different_input_or_a_root_without_retry_link_finds_no_candidate()
    {
        TestEnvironment.RequirePostgres();
        await OldOutputAsync("old-b", H('1'), """{"ok":true}""");
        await AddRetryRootAsync();
        await PublishNewAsync("new-b", H('2'));   // input diverso
        var (store, context) = Store(_newJob, 7);
        await using (context)
        {
            Assert.Equal(WorkImportKind.NoCandidate, (await store.TryImportAsync("new-b", Expected, Ct)).Kind);
        }

        // Nuovo root NON collegato al precedente (nessun RetryOfId): lo stesso input non è importabile.
        var (_, project) = (_old.User, _old.Project);
        var unlinkedOwner = await Db.SeedParseAsync(owner: (_old.User, project));
        await Db.ExecuteAsync($"""
            UPDATE "DocumentJobs" SET "Status" = 'Running', "LeaseOwner" = '{Lease}', "LeaseUntil" = now() + interval '30 minutes', "FencingToken" = 9, "AttemptCount" = 1, "StartedAt" = now(),
                   "WorkflowDefinitionVersion" = 'doc-batch-1' WHERE "Id" = '{unlinkedOwner.Job}';
            UPDATE "DocumentPreparations" SET "Status" = 'Extracting' WHERE "Id" = '{unlinkedOwner.Preparation}';
            """);
        var (other, otherContext) = Store(unlinkedOwner.Job, 9);
        await using (otherContext)
        {
            await other.PublishManifestAsync(Manifest(), Ct);
            await other.PublishBatchesAsync([Batch("x-b", H('1'))], Ct);
            Assert.Equal(WorkImportKind.NoCandidate, (await other.TryImportAsync("x-b", Expected, Ct)).Kind);
            Assert.Equal(0L, await OutputsOfAsync(unlinkedOwner.Job));
        }
    }

    [Fact]
    public async Task An_existing_output_is_never_overwritten_and_a_stale_fence_writes_nothing()
    {
        TestEnvironment.RequirePostgres();
        await OldOutputAsync("old-b", H('1'), """{"ok":true}""");
        await AddRetryRootAsync();
        await PublishNewAsync("new-b", H('1'));
        var (stale, staleContext) = Store(_newJob, 6);   // fence vecchio
        await using (staleContext)
        {
            await Assert.ThrowsAsync<CheckpointFencedException>(() => stale.TryImportAsync("new-b", Expected, Ct));
        }

        Assert.Equal(0L, await OutputsOfAsync(_newJob));

        var (store, context) = Store(_newJob, 7);
        await using var _ = context;
        var own = (await store.CommitOutputAsync(Inline("new-b", H('1'), """{"ok":true,"own":1}"""), null, Ct)).Output;   // output proprio già presente
        var result = await store.TryImportAsync("new-b", Expected, Ct);

        Assert.Equal(WorkImportKind.Replayed, result.Kind);
        Assert.Equal(own.OutputHash, result.Output!.OutputHash);
        Assert.Null(result.Output.DerivedFromOutputHash);
        Assert.Equal(1L, await OutputsOfAsync(_newJob));
    }
}
