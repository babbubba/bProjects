using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using bProjects.App.Documents;

namespace bProjects.IntegrationTests;

/// <summary>
/// T010 (PG): fault injection e contese di receipt/quota dell'upload su PostgreSQL reale e filesystem isolato. Il DB non è transazionale
/// col filesystem: le prove non fingono atomicità, verificano che ogni guasto (crash prima del rename, dopo il rename e prima del commit DB)
/// non crei duplicati né un falso «acquisito» e che il replay con la stessa chiave converga su una sola versione. Il guasto è iniettato
/// con <see cref="DocumentFileStore.OnStage"/> attraverso l'API reale. Scritte prima dell'implementazione: Skipped finché T012–T013 non
/// esistono (<see cref="PendingGuard"/>); il PASS applicativo di quote e contese è richiesto nel gate T018, non nella migrazione T007.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentUploadRecoveryTests : ArchiveTestBase
{
    private const string PendingTasks = "T012–T013 (gate T018)";

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 800;
        options.ProjectQuotaBytes = 1_000;
        options.DerivedQuotaBytes = 1;   // la quota dei derivati è separata: non deve limitare gli originali
    }

    private static byte[] Sized(string seed, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(seed[i % seed.Length] % 94 + 33);
        }

        return bytes;
    }

    private static void FailAt(DocumentFileStore store, DocumentStoreStage stage) =>
        store.OnStage = current =>
        {
            if (current == stage)
            {
                throw new IOException("guasto iniettato dal test");
            }
        };

    // ================================================================ crash

    [Fact]
    public async Task A_crash_before_rename_acquires_nothing_leaves_no_original_and_a_replay_creates_exactly_one_version()
    {
        PendingGuard.Await(PendingTasks);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var key = Guid.NewGuid();
        var bytes = Sized("prima-rename", 300);
        FailAt(Store, DocumentStoreStage.BeforeRename);

        var failed = await Upload(client, project, bytes, key: key);

        Assert.True((int)failed.StatusCode >= 500, $"atteso errore server, ricevuto {(int)failed.StatusCode}");
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DocumentVersions\""));   // mai un falso acquisito
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DocumentJobs\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"UploadReceipts\" WHERE \"Status\" = 'Committed'"));
        Assert.Empty(StoredObjects());

        Store.OnStage = null;
        var replay = await Upload(client, project, bytes, key: key);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);   // l'acquisizione non era avvenuta: è un 201, non un 200
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentJobs\""));
        Assert.Single(StoredObjects());
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task A_crash_after_rename_before_the_database_commit_leaves_a_verifiable_orphan_and_the_replay_adopts_it_without_duplicates()
    {
        PendingGuard.Await(PendingTasks);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var key = Guid.NewGuid();
        var bytes = Sized("dopo-rename", 400);
        FailAt(Store, DocumentStoreStage.AfterRename);

        var failed = await Upload(client, project, bytes, key: key);

        Assert.True((int)failed.StatusCode >= 500);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DocumentVersions\""));   // l'originale è su disco ma NON è acquisito
        Assert.Equal(0, await Count("SELECT count(*) FROM \"UploadReceipts\" WHERE \"Status\" = 'Committed'"));
        var orphan = Assert.Single(StoredObjects());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(orphan, Ct));   // residuo verificabile per hash e lunghezza

        Store.OnStage = null;
        var replay = await Upload(client, project, bytes, key: key);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentJobs\""));
        Assert.Single(StoredObjects());   // lo stesso oggetto, non un secondo file
        var again = await Upload(client, project, bytes, key: key);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
    }

    [Fact]
    public async Task A_failed_upload_in_the_same_process_releases_its_quota_reservation()
    {
        PendingGuard.Await(PendingTasks);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        FailAt(Store, DocumentStoreStage.BeforeRename);
        Assert.True((int)(await Upload(client, project, Sized("a", 800))).StatusCode >= 500);
        Store.OnStage = null;

        // Se la prenotazione da 800 byte fosse rimasta, 800 + 800 supererebbe la quota di 1000.
        var retry = await Upload(client, project, Sized("b", 800));

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task An_abandoned_receipt_blocks_quota_until_it_expires_then_a_new_upload_proceeds()
    {
        PendingGuard.Await(PendingTasks);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        // Un processo morto lascia una ricevuta Receiving con 800 byte prenotati.
        async Task Abandon(DateTimeOffset expiresAt) => await Execute(
            "INSERT INTO \"UploadReceipts\" (\"Id\", \"ProjectId\", \"RequestKey\", \"InputHash\", \"ReservedBytes\", \"Status\", \"CreatedAt\", \"ExpiresAt\") " +
            "VALUES (@id, @p, @k, @h, 800, 'Receiving', now(), @e)",
            ("id", Guid.NewGuid()), ("p", project), ("k", Guid.NewGuid()), ("h", new string('c', 64)), ("e", expiresAt));
        await Abandon(DateTimeOffset.UtcNow.AddMinutes(10));

        var blocked = await Upload(client, project, Sized("c", 700));
        Assert.Equal(HttpStatusCode.InsufficientStorage, blocked.StatusCode);   // 507: la prenotazione viva conta

        await Execute("UPDATE \"UploadReceipts\" SET \"ExpiresAt\" = now() - interval '1 minute'");
        var proceeds = await Upload(client, project, Sized("d", 700));

        Assert.Equal(HttpStatusCode.Created, proceeds.StatusCode);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"UploadReceipts\" WHERE \"Status\" = 'Receiving' AND \"ExpiresAt\" < now()"));   // scaduta riconciliata
    }

    // ================================================================ replay e conflitti

    [Fact]
    public async Task Replaying_a_committed_upload_is_idempotent_across_file_receipt_version_and_job()
    {
        PendingGuard.Await(PendingTasks);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var key = Guid.NewGuid();
        var bytes = Sized("replay", 250);

        var first = await Upload(client, project, bytes, key: key);
        var replays = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Upload(client, project, bytes, key: key)));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.All(replays, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"UploadReceipts\""));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentJobs\""));
        Assert.Single(StoredObjects());
    }

    [Fact]
    public async Task The_same_key_with_different_input_is_a_conflict_that_never_stores_the_new_bytes()
    {
        PendingGuard.Await(PendingTasks);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var key = Guid.NewGuid();
        await Upload(client, project, Sized("uno", 250), key: key);

        var conflict = await Upload(client, project, Sized("due", 250), key: key);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", (await Json(conflict)).GetProperty("code").GetString());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Single(StoredObjects());
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task A_second_request_with_the_same_key_while_the_first_is_still_receiving_is_409_upload_in_progress_with_the_receipt()
    {
        PendingGuard.Await(PendingTasks);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var key = Guid.NewGuid();
        var bytes = Sized("lento", 500);
        var gate = new TaskCompletionSource();
        var slow = new GatedContent(bytes, gate.Task);
        var first = Upload(client, project, bytes, key: key, fileContent: slow);

        // Aspetta che il server abbia aperto la ricevuta: l'upload è «in ricezione», non ancora acquisito.
        var receipt = await WaitFor(async () => await Count("SELECT count(*) FROM \"UploadReceipts\" WHERE \"Status\" = 'Receiving'") == 1);
        Assert.True(receipt, "la ricevuta Receiving non è comparsa");
        var second = await Upload(client, project, bytes, key: key);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await Json(second);
        Assert.Equal("upload_in_progress", problem.GetProperty("code").GetString());
        Assert.NotEqual(Guid.Empty, problem.GetProperty("receiptId").GetGuid());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DocumentVersions\""));   // nessuna falsa versione acquisita

        gate.SetResult();
        Assert.Equal(HttpStatusCode.Created, (await first).StatusCode);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Equal(HttpStatusCode.OK, (await Upload(client, project, bytes, key: key)).StatusCode);
    }

    // ================================================================ contese

    [Fact]
    public async Task Simultaneous_uploads_with_different_keys_make_distinct_documents_versions_files_and_receipts()
    {
        PendingGuard.Await(PendingTasks);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var bytes = Sized("gemelli", 300);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Upload(client, project, bytes)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(2, await Count("SELECT count(*) FROM \"UploadReceipts\" WHERE \"Status\" = 'Committed'"));
        Assert.Equal(2, await Count("SELECT count(*) FROM \"ProjectDocuments\""));
        Assert.Equal(2, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Equal(2, await Count("SELECT count(*) FROM \"DocumentJobs\""));
        Assert.Equal(2, StoredObjects().Length);
    }

    [Fact]
    public async Task Simultaneous_versions_of_one_document_get_distinct_consecutive_numbers()
    {
        PendingGuard.Await(PendingTasks);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var document = (await Json(await Upload(client, project, Sized("base", 200)))).GetProperty("documentId").GetGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(i => Upload(client, project, Sized("v" + i, 220 + i), documentId: document)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var versions = new List<int>();
        foreach (var r in results)
        {
            versions.Add((await Json(r)).GetProperty("version").GetInt32());
        }

        Assert.Equal([2, 3, 4], versions.Order());
        Assert.Equal(4, await Count("SELECT count(*) FROM \"DocumentVersions\" WHERE \"DocumentId\" = @d", ("d", document)));
    }

    [Fact]
    public async Task The_same_key_sent_twice_at_once_commits_one_version_and_the_loser_is_a_replay_or_in_progress()
    {
        PendingGuard.Await(PendingTasks);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var key = Guid.NewGuid();
        var bytes = Sized("stessa-chiave", 350);

        var results = await Task.WhenAll(Upload(client, project, bytes, key: key), Upload(client, project, bytes, key: key));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"UploadReceipts\""));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentJobs\""));
        Assert.Single(StoredObjects());
    }

    // ================================================================ quota

    [Fact]
    public async Task Original_quota_is_enforced_at_and_over_the_limit_without_leaving_files_or_receipts()
    {
        PendingGuard.Await(PendingTasks);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);

        Assert.Equal(HttpStatusCode.Created, (await Upload(client, project, Sized("a", 600))).StatusCode);
        var over = await Upload(client, project, Sized("b", 600));   // 600 + 600 > 1000
        Assert.Equal(HttpStatusCode.InsufficientStorage, over.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Upload(client, project, Sized("c", 400))).StatusCode);   // esattamente al limite: 1000
        var past = await Upload(client, project, Sized("d", 1));
        Assert.Equal(HttpStatusCode.InsufficientStorage, past.StatusCode);

        Assert.Equal(2, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Equal(1_000, await Scalar<long>("SELECT sum(\"ByteLength\") FROM \"DocumentVersions\""));
        Assert.Equal(2, StoredObjects().Length);   // il rifiutato non ha lasciato file
        Assert.Empty(TemporaryFiles());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"UploadReceipts\" WHERE \"Status\" = 'Receiving'"));
        // Un altro progetto ha la propria quota intera.
        var other = await NewProject(client);
        Assert.Equal(HttpStatusCode.Created, (await Upload(client, other, Sized("e", 800))).StatusCode);
    }

    [Fact]
    public async Task Concurrent_uploads_that_do_not_both_fit_the_quota_are_serialised_one_wins_and_one_gets_507()
    {
        PendingGuard.Await(PendingTasks);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);

        var results = await Task.WhenAll(
            Upload(client, project, Sized("x", 700)),
            Upload(client, project, Sized("y", 700)));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.InsufficientStorage));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Single(StoredObjects());
        Assert.True(await Scalar<long>("SELECT coalesce(sum(\"ReservedBytes\"), 0) FROM \"UploadReceipts\" WHERE \"Status\" IN ('Receiving','Committed')") <= 1_000);
    }

    [Fact]
    public async Task The_derived_quota_is_separate_a_tiny_derived_limit_does_not_restrict_originals()
    {
        PendingGuard.Await(PendingTasks);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);

        // DerivedQuotaBytes = 1 (vedi Tune): se contasse sugli originali nessun upload passerebbe.
        var response = await Upload(client, project, Sized("derivati", 500));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(500, await Scalar<long>("SELECT sum(\"ReservedBytes\") FROM \"UploadReceipts\" WHERE \"Status\" = 'Committed'"));
    }

    // ================================================================ utilità

    private static async Task<bool> WaitFor(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100, Ct);
        }

        return false;
    }

    /// <summary>Corpo che invia metà dei byte e poi attende il via: tiene l'upload «in ricezione» in modo deterministico.</summary>
    private sealed class GatedContent(byte[] bytes, Task gate) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var half = bytes.Length / 2;
            await stream.WriteAsync(bytes.AsMemory(0, half), Ct);
            await stream.FlushAsync(Ct);
            await gate;
            await stream.WriteAsync(bytes.AsMemory(half), Ct);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }
    }
}
