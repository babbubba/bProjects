using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using bProjects.App.Documents;
using Microsoft.Extensions.Logging;

namespace bProjects.IntegrationTests;

/// <summary>
/// T009 (DET): lo storage degli originali su filesystem reale temporaneo. Ogni prova usa una radice propria sotto la cartella temporanea
/// del sistema e una cartella «fuori» con un sentinella: nessun test legge o scrive altrove, e il sentinella deve restare intatto.
/// Il negozio non riceve mai il nome del file: le prove con nomi ostili verificano che chiave e percorso non ne derivano e che chiavi
/// o percorsi ostili passati direttamente vengono rifiutati. Nessun contenuto né percorso nei log o nei messaggi d'errore.
/// </summary>
public sealed class DocumentFileStoreTests : IDisposable
{
    private const string Marker = "CONTENUTO-RISERVATO-NON-NEI-LOG";

    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "bprojects-store-" + Guid.NewGuid().ToString("N"));
    private readonly CollectingLogger _logger = new();
    private readonly string _root;
    private readonly string _outside;
    private readonly string _sentinel;
    private readonly DocumentFileStore _store;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DocumentFileStoreTests()
    {
        _root = Path.Combine(_sandbox, "storage");
        _outside = Path.Combine(_sandbox, "outside");
        Directory.CreateDirectory(_outside);
        _sentinel = Path.Combine(_outside, "sentinel.txt");
        File.WriteAllText(_sentinel, "intatto");
        _store = new DocumentFileStore(_root, [Path.Combine(_sandbox, "wwwroot")], _logger);
    }

    public void Dispose() => SafeDelete(_sandbox);

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private async Task<StoredDocumentFile> Store(Guid project, Guid obj, byte[] bytes, long max = 10_000_000) =>
        await _store.WriteAsync(project, obj, new MemoryStream(bytes), max, Ct);

    private string[] Tree(string directory) =>
        Directory.Exists(directory) ? [.. Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(directory, p))] : [];

    private void AssertSentinelIntact()
    {
        Assert.Equal("intatto", File.ReadAllText(_sentinel));
        Assert.Equal([_sentinel], Directory.EnumerateFileSystemEntries(_outside).ToArray());
    }

    // ---- scrittura, hash, lunghezza ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Write_streams_to_the_server_key_with_real_length_and_sha256_and_keeps_the_exact_bytes()
    {
        var bytes = RandomBytes(300_001);   // più di un buffer interno
        var (project, obj) = (Guid.NewGuid(), Guid.NewGuid());

        var stored = await Store(project, obj, bytes);

        Assert.Equal(DocumentFileStore.KeyFor(project, obj), stored.StorageKey);
        Assert.Equal(bytes.Length, stored.ByteLength);
        Assert.Equal(Hex(bytes), stored.Sha256);
        await using var read = await _store.OpenVerifiedAsync(stored.StorageKey, stored.Sha256, stored.ByteLength, Ct);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, Ct);
        Assert.Equal(bytes, copy.ToArray());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "tmp")));   // nessun temporaneo residuo
    }

    [Fact]
    public async Task Same_content_in_two_projects_gets_distinct_keys_and_distinct_files_without_global_deduplication()
    {
        var bytes = RandomBytes(2048);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        var first = await Store(a, Guid.NewGuid(), bytes);
        var second = await Store(b, Guid.NewGuid(), bytes);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.NotEqual(first.StorageKey, second.StorageKey);
        Assert.StartsWith(a.ToString("N") + "/", first.StorageKey);
        Assert.StartsWith(b.ToString("N") + "/", second.StorageKey);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "objects"), "*", SearchOption.AllDirectories).Length);
        // Una chiave di un progetto non è risolvibile sotto l'altro: la chiave è l'unica identità.
        var crossed = $"{b:N}/{first.StorageKey.Split('/')[1]}";
        Assert.Null(await _store.InspectAsync(crossed, Ct));
    }

    [Fact]
    public async Task Keys_never_derive_from_hostile_client_file_names()
    {
        // Lo store non riceve il nome del file: qualunque nome ostile resta un'etichetta dei metadati. La chiave è solo guid:guid.
        string[] hostileNames = ["../../../etc/passwd", "..\\..\\Windows\\win.ini", "con.txt", "a:b.pdf", "report.pdf\0.exe", new string('x', 400) + ".docx"];
        foreach (var name in hostileNames)
        {
            var stored = await Store(Guid.NewGuid(), Guid.NewGuid(), Encoding.UTF8.GetBytes(name));
            Assert.Matches("^[0-9a-f]{32}/[0-9a-f]{32}$", stored.StorageKey);
            Assert.DoesNotContain("..", stored.StorageKey);
        }

        AssertSentinelIntact();
        Assert.All(Tree(_root), entry => Assert.Matches(@"^(objects|tmp)([\\/][0-9a-f]{32}){0,2}$", entry));
    }

    [Fact]
    public async Task The_byte_limit_is_hard_it_reads_at_most_one_byte_past_the_limit_and_leaves_nothing_behind()
    {
        var probe = new EndlessStream();
        var ex = await Assert.ThrowsAsync<DocumentStoreException>(() => _store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), probe, 1_000, Ct));

        Assert.Equal(DocumentStoreFailure.TooLarge, ex.Reason);
        Assert.True(probe.BytesRead <= 1_001, $"letti {probe.BytesRead} byte oltre il limite");
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_file_exactly_at_the_limit_is_accepted()
    {
        var stored = await Store(Guid.NewGuid(), Guid.NewGuid(), RandomBytes(1_000), max: 1_000);
        Assert.Equal(1_000, stored.ByteLength);
    }

    [Fact]
    public async Task An_empty_stream_is_rejected_and_stores_nothing()
    {
        var ex = await Assert.ThrowsAsync<DocumentStoreException>(() => Store(Guid.NewGuid(), Guid.NewGuid(), []));

        Assert.Equal(DocumentStoreFailure.Empty, ex.Reason);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Active_content_is_stored_as_inert_bytes_never_fetched_linked_or_executed()
    {
        // HTML con risorse remote, link, macro e DDE: lo store non interpreta niente, non apre rete, non crea file oltre all'originale.
        var bytes = Encoding.UTF8.GetBytes("<html><script src=\"http://127.0.0.1:9/x.js\"></script><a href=\"file:///c:/secret\">x</a></html>\n=cmd|'/c calc'!A1\nSub AutoOpen()\nEnd Sub");
        var stored = await Store(Guid.NewGuid(), Guid.NewGuid(), bytes);

        await using var read = await _store.OpenVerifiedAsync(stored.StorageKey, stored.Sha256, stored.ByteLength, Ct);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, Ct);
        Assert.Equal(bytes, copy.ToArray());
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "objects"), "*", SearchOption.AllDirectories));
    }

    // ---- chiavi e path traversal --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("../../outside/sentinel.txt")]
    [InlineData("..\\..\\outside\\sentinel.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("00000000000000000000000000000000/../../outside/sentinel.txt")]
    [InlineData("00000000000000000000000000000000/00000000000000000000000000000000/..")]
    [InlineData("00000000000000000000000000000000/00000000000000000000000000000000/")]
    [InlineData("00000000000000000000000000000000\\00000000000000000000000000000000")]
    [InlineData("0000000000000000000000000000000G/00000000000000000000000000000000")]
    [InlineData("ABCDEF00000000000000000000000000/00000000000000000000000000000000")]
    [InlineData(" 00000000000000000000000000000000/00000000000000000000000000000000")]
    [InlineData("")]
    [InlineData("objects")]
    public async Task Hostile_or_malformed_keys_are_rejected_by_every_operation_without_touching_the_disk_outside(string key)
    {
        await Store(Guid.NewGuid(), Guid.NewGuid(), RandomBytes(10));   // radice inizializzata

        var open = await Assert.ThrowsAsync<DocumentStoreException>(() => _store.OpenVerifiedAsync(key, new string('0', 64), 1, Ct));
        var inspect = await Assert.ThrowsAsync<DocumentStoreException>(() => _store.InspectAsync(key, Ct));
        var delete = await Assert.ThrowsAsync<DocumentStoreException>(() => _store.DeleteOrphanAsync(key, (_, _) => Task.FromResult(false), Ct));

        Assert.All(new[] { open, inspect, delete }, e => Assert.Equal(DocumentStoreFailure.InvalidKey, e.Reason));
        AssertSentinelIntact();
        Assert.DoesNotContain(_root, open.Message + inspect.Message + delete.Message);
    }

    // ---- symlink / reparse point ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_project_directory_replaced_by_a_link_is_never_followed_for_write_read_or_delete()
    {
        var project = Guid.NewGuid();
        var obj = Guid.NewGuid();
        var first = await Store(project, Guid.NewGuid(), RandomBytes(50));
        // L'attaccante (o un errore operativo) sostituisce la cartella del progetto con un collegamento verso una cartella esterna.
        var projectDirectory = Path.Combine(_root, "objects", project.ToString("N"));
        Directory.Move(projectDirectory, projectDirectory + "-moved");
        CreateDirectoryLink(projectDirectory, _outside);

        var write = await Assert.ThrowsAsync<DocumentStoreException>(() => Store(project, obj, RandomBytes(10)));
        var read = await Assert.ThrowsAsync<DocumentStoreException>(() => _store.OpenVerifiedAsync(first.StorageKey, first.Sha256, first.ByteLength, Ct));
        var delete = await Assert.ThrowsAsync<DocumentStoreException>(() => _store.DeleteOrphanAsync(first.StorageKey, (_, _) => Task.FromResult(false), Ct));

        Assert.All(new[] { write, read, delete }, e => Assert.Equal(DocumentStoreFailure.InvalidKey, e.Reason));
        AssertSentinelIntact();   // nulla scritto, letto o cancellato oltre il collegamento
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "tmp")));
    }

    [Fact]
    public async Task A_file_symlink_inside_the_store_is_refused_on_read()
    {
        var project = Guid.NewGuid();
        var first = await Store(project, Guid.NewGuid(), RandomBytes(20));
        var forged = Path.Combine(_root, "objects", project.ToString("N"), Guid.NewGuid().ToString("N"));
        try
        {
            File.CreateSymbolicLink(forged, _sentinel);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Creazione di symlink su file non consentita a questo utente (serve Developer Mode o privilegio); il caso directory/junction è coperto.");
        }

        var key = $"{project:N}/{Path.GetFileName(forged)}";
        var ex2 = await Assert.ThrowsAsync<DocumentStoreException>(() => _store.OpenVerifiedAsync(key, new string('0', 64), 7, Ct));
        Assert.Equal(DocumentStoreFailure.InvalidKey, ex2.Reason);
        Assert.Equal("intatto", File.ReadAllText(_sentinel));
        Assert.NotNull(first);
    }

    [Fact]
    public async Task A_managed_subdirectory_that_is_a_link_makes_the_store_refuse_to_write()
    {
        Directory.CreateDirectory(_root);
        CreateDirectoryLink(Path.Combine(_root, "objects"), _outside);

        var ex = await Assert.ThrowsAsync<DocumentStoreException>(() => Store(Guid.NewGuid(), Guid.NewGuid(), RandomBytes(10)));

        Assert.Equal(DocumentStoreFailure.InvalidKey, ex.Reason);
        AssertSentinelIntact();
    }

    // ---- integrità e download hash-validato -------------------------------------------------------------------------------------

    [Fact]
    public async Task Download_validates_the_recorded_hash_and_length_before_returning_any_byte()
    {
        var bytes = RandomBytes(5_000);
        var stored = await Store(Guid.NewGuid(), Guid.NewGuid(), bytes);
        var path = Path.Combine(_root, "objects", stored.StorageKey.Replace('/', Path.DirectorySeparatorChar));

        // hash atteso errato, lunghezza attesa errata
        var wrongHash = await Assert.ThrowsAsync<DocumentStoreException>(() => _store.OpenVerifiedAsync(stored.StorageKey, new string('a', 64), stored.ByteLength, Ct));
        var wrongLength = await Assert.ThrowsAsync<DocumentStoreException>(() => _store.OpenVerifiedAsync(stored.StorageKey, stored.Sha256, stored.ByteLength + 1, Ct));
        Assert.Equal(DocumentStoreFailure.IntegrityFailure, wrongHash.Reason);
        Assert.Equal(DocumentStoreFailure.IntegrityFailure, wrongLength.Reason);

        // byte alterati su disco, stessa lunghezza
        File.SetAttributes(path, FileAttributes.Normal);
        var tampered = (byte[])bytes.Clone();
        tampered[17] ^= 0xFF;
        File.WriteAllBytes(path, tampered);
        var altered = await Assert.ThrowsAsync<DocumentStoreException>(() => _store.OpenVerifiedAsync(stored.StorageKey, stored.Sha256, stored.ByteLength, Ct));
        Assert.Equal(DocumentStoreFailure.IntegrityFailure, altered.Reason);
    }

    [Fact]
    public async Task A_missing_original_is_NotFound_for_download_and_null_for_inspection()
    {
        var key = DocumentFileStore.KeyFor(Guid.NewGuid(), Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<DocumentStoreException>(() => _store.OpenVerifiedAsync(key, new string('0', 64), 1, Ct));

        Assert.Equal(DocumentStoreFailure.NotFound, ex.Reason);
        Assert.Null(await _store.InspectAsync(key, Ct));
    }

    // ---- guasti: disco, crash, immutabilità ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_full_disk_while_writing_is_InsufficientSpace_and_leaves_no_temporary_or_original()
    {
        var diskFull = OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28;
        _store.OnStage = stage =>
        {
            if (stage == DocumentStoreStage.TempWritten)
            {
                throw new IOException("disco pieno simulato", diskFull);
            }
        };

        var ex = await Assert.ThrowsAsync<DocumentStoreException>(() => Store(Guid.NewGuid(), Guid.NewGuid(), RandomBytes(4_000)));

        Assert.Equal(DocumentStoreFailure.InsufficientSpace, ex.Reason);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_failure_of_the_source_stream_propagates_unmapped_and_removes_the_temporary()
    {
        var ex = await Assert.ThrowsAsync<IOException>(() => _store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), new FailingStream(RandomBytes(100_000), failAfter: 70_000), 1_000_000, Ct));

        Assert.Equal("client interrotto", ex.Message);   // non è un guasto del volume
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_crash_before_rename_publishes_nothing_and_leaves_only_a_reconcilable_temporary()
    {
        var (project, obj) = (Guid.NewGuid(), Guid.NewGuid());
        // Un crash vero non pulisce: lo simuliamo con un'eccezione e poi ricreando il residuo come lo lascerebbe un processo morto.
        _store.OnStage = stage =>
        {
            if (stage == DocumentStoreStage.BeforeRename)
            {
                throw new IOException("crash simulato");
            }
        };
        await Assert.ThrowsAsync<IOException>(() => Store(project, obj, RandomBytes(500)));
        Assert.Null(await _store.InspectAsync(DocumentFileStore.KeyFor(project, obj), Ct));   // nessun originale visibile

        var stale = Path.Combine(_root, "tmp", obj.ToString("N") + ".part");
        File.WriteAllBytes(stale, RandomBytes(300));
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));
        var fresh = Path.Combine(_root, "tmp", Guid.NewGuid().ToString("N") + ".part");
        File.WriteAllBytes(fresh, [1, 2, 3]);

        var removed = _store.SweepTemporaries(TimeSpan.FromHours(1));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));   // troppo recente: potrebbe essere una scrittura viva
    }

    [Fact]
    public async Task Sweeping_temporaries_never_removes_a_referenced_original_nor_a_temporary_still_being_written()
    {
        var stored = await Store(Guid.NewGuid(), Guid.NewGuid(), RandomBytes(2_000));
        var originalPath = Path.Combine(_root, "objects", stored.StorageKey.Replace('/', Path.DirectorySeparatorChar));
        File.SetLastWriteTimeUtc(originalPath, DateTime.UtcNow.AddDays(-30));   // vecchio quanto vuoi: non è un temporaneo
        var live = Path.Combine(_root, "tmp", Guid.NewGuid().ToString("N") + ".part");
        await using (var open = new FileStream(live, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            File.SetLastWriteTimeUtc(live, DateTime.UtcNow.AddDays(-1));

            var removed = _store.SweepTemporaries(TimeSpan.Zero);

            Assert.Equal(0, removed);   // in uso: sharing violation, resta
            Assert.True(File.Exists(live));
        }

        Assert.Equal(stored.Sha256, (await _store.InspectAsync(stored.StorageKey, Ct))!.Sha256);
    }

    [Fact]
    public async Task A_crash_after_rename_leaves_a_verifiable_original_and_replaying_the_same_input_is_idempotent()
    {
        var (project, obj) = (Guid.NewGuid(), Guid.NewGuid());
        var bytes = RandomBytes(9_000);
        _store.OnStage = stage =>
        {
            if (stage == DocumentStoreStage.AfterRename)
            {
                throw new IOException("crash prima del commit DB simulato");
            }
        };
        await Assert.ThrowsAsync<IOException>(() => Store(project, obj, bytes));

        // Il residuo è riconciliabile dal solo oggetto: lunghezza e hash dicono se è quello atteso.
        var key = DocumentFileStore.KeyFor(project, obj);
        var inspected = await _store.InspectAsync(key, Ct);
        Assert.Equal(new StoredDocumentFile(key, bytes.Length, Hex(bytes)), inspected);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "tmp")));

        _store.OnStage = null;
        var replay = await Store(project, obj, bytes);
        Assert.Equal(inspected, replay);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "objects"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_original_is_immutable_a_different_input_for_the_same_object_fails_and_preserves_the_first_bytes()
    {
        var (project, obj) = (Guid.NewGuid(), Guid.NewGuid());
        var original = RandomBytes(1_234);
        var stored = await Store(project, obj, original);
        var path = Path.Combine(_root, "objects", stored.StorageKey.Replace('/', Path.DirectorySeparatorChar));
        Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);   // immutabile anche per il processo

        var ex = await Assert.ThrowsAsync<DocumentStoreException>(() => Store(project, obj, RandomBytes(1_235)));

        Assert.Equal(DocumentStoreFailure.IntegrityFailure, ex.Reason);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "tmp")));
    }

    [Fact]
    public async Task A_new_version_gets_its_own_object_and_never_replaces_the_previous_original()
    {
        var project = Guid.NewGuid();
        var v1 = RandomBytes(100);
        var v2 = RandomBytes(101);

        var first = await Store(project, Guid.NewGuid(), v1);
        var second = await Store(project, Guid.NewGuid(), v2);

        Assert.NotEqual(first.StorageKey, second.StorageKey);
        await using var read = await _store.OpenVerifiedAsync(first.StorageKey, first.Sha256, first.ByteLength, Ct);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, Ct);
        Assert.Equal(v1, copy.ToArray());
    }

    [Fact]
    public async Task Orphan_deletion_refuses_a_referenced_original_and_removes_an_unreferenced_one()
    {
        var kept = await Store(Guid.NewGuid(), Guid.NewGuid(), RandomBytes(64));
        var orphan = await Store(Guid.NewGuid(), Guid.NewGuid(), RandomBytes(65));

        Assert.False(await _store.DeleteOrphanAsync(kept.StorageKey, (_, _) => Task.FromResult(true), Ct));
        Assert.True(await _store.DeleteOrphanAsync(orphan.StorageKey, (_, _) => Task.FromResult(false), Ct));
        Assert.False(await _store.DeleteOrphanAsync(orphan.StorageKey, (_, _) => Task.FromResult(false), Ct));   // già assente

        Assert.NotNull(await _store.InspectAsync(kept.StorageKey, Ct));
        Assert.Null(await _store.InspectAsync(orphan.StorageKey, Ct));
    }

    // ---- configurazione e log --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_root_cannot_be_inside_or_contain_the_publicly_served_folder()
    {
        var web = Path.Combine(_sandbox, "app", "wwwroot");

        Assert.Throws<InvalidOperationException>(() => new DocumentFileStore(Path.Combine(web, "uploads"), [web], _logger));
        Assert.Throws<InvalidOperationException>(() => new DocumentFileStore(web, [web], _logger));
        Assert.Throws<InvalidOperationException>(() => new DocumentFileStore(Path.Combine(_sandbox, "app"), [web], _logger));
        _ = new DocumentFileStore(Path.Combine(_sandbox, "private"), [web], _logger);   // un fratello va bene
    }

    [Fact]
    public void The_storage_root_option_must_be_absolute_and_defaults_to_a_private_local_folder()
    {
        var validator = new DocumentOptionsValidator();

        Assert.True(validator.Validate(null, new DocumentOptions()).Succeeded);
        Assert.True(validator.Validate(null, new DocumentOptions { StorageRoot = _root }).Succeeded);
        Assert.False(validator.Validate(null, new DocumentOptions { StorageRoot = "relativa/dir" }).Succeeded);
        Assert.False(validator.Validate(null, new DocumentOptions { StorageRoot = "..\\fuori" }).Succeeded);
    }

    [Fact]
    public async Task Logs_and_error_messages_never_contain_file_content_or_disk_paths()
    {
        var bytes = Encoding.UTF8.GetBytes(Marker + new string('x', 5_000));
        var stored = await Store(Guid.NewGuid(), Guid.NewGuid(), bytes);
        var errors = new List<string>();
        foreach (var attempt in new Func<Task>[]
        {
            () => _store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), new MemoryStream(bytes), 100, Ct),
            () => _store.OpenVerifiedAsync(stored.StorageKey, new string('b', 64), stored.ByteLength, Ct),
            () => _store.OpenVerifiedAsync("../" + Marker, new string('b', 64), 1, Ct),
        })
        {
            try
            {
                await attempt();
            }
            catch (DocumentStoreException ex)
            {
                errors.Add(ex.Message);
            }
        }

        Assert.Equal(3, errors.Count);
        var everything = string.Join('\n', _logger.Entries) + "\n" + string.Join('\n', errors);
        Assert.DoesNotContain(Marker, everything);
        Assert.DoesNotContain(_sandbox, everything);
        Assert.NotEmpty(_logger.Entries);   // gli eventi ci sono (chiave e byte), senza contenuto
    }

    // ---- utilità ---------------------------------------------------------------------------------------------------------------

    /// <summary>Junction su Windows (non richiede privilegi), symlink altrove: in entrambi i casi un reparse point verso <paramref name="target"/>.</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "junction non creata");
    }

    /// <summary>Pulizia che non segue mai i collegamenti: rimuove il link, non il bersaglio.</summary>
    private static void SafeDelete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                SafeDelete(entry);
            }
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }

        Directory.Delete(path, recursive: false);
    }

    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill(0x41);
            BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }
    }

    private sealed class FailingStream(byte[] data, int failAfter) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= failAfter)
            {
                throw new IOException("client interrotto");
            }

            var count = Math.Min(Math.Min(buffer.Length, 10_000), data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class CollectingLogger : ILogger<DocumentFileStore>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
    }
}
