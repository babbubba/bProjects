using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace bProjects.App.Documents;

public enum DocumentStoreFailure
{
    /// <summary>Lo stream supera il limite di byte: la lettura si ferma al primo byte oltre il limite (413).</summary>
    TooLarge,

    /// <summary>Nessun byte ricevuto (422): un originale vuoto non è una versione.</summary>
    Empty,

    /// <summary>Disco pieno o quota del volume esaurita (507).</summary>
    InsufficientSpace,

    /// <summary>Volume non scrivibile/leggibile per altre ragioni (503).</summary>
    Unavailable,

    /// <summary>Chiave non generata dal server, percorso fuori radice o reparse point/symlink lungo il percorso.</summary>
    InvalidKey,

    NotFound,

    /// <summary>Lunghezza o SHA-256 dei byte su disco diversi da quelli registrati.</summary>
    IntegrityFailure,
}

/// <summary>Errore dello store originali. Il messaggio non contiene mai percorsi, nomi di file o contenuto.</summary>
public sealed class DocumentStoreException(DocumentStoreFailure reason, string message, Exception? inner = null) : Exception(message, inner)
{
    public DocumentStoreFailure Reason { get; } = reason;
}

/// <summary>Punti del ciclo di scrittura in cui un test può iniettare un guasto (<see cref="DocumentFileStore.OnStage"/>).</summary>
public enum DocumentStoreStage
{
    /// <summary>Tutti i byte sono nel file temporaneo, prima del flush su disco.</summary>
    TempWritten,

    /// <summary>Temporaneo scaricato su disco, subito prima del rename.</summary>
    BeforeRename,

    /// <summary>Rename eseguito: l'originale è durevole ma nessun record DB lo referenzia ancora.</summary>
    AfterRename,

    /// <summary>Solo in <see cref="DocumentFileStore.DeleteOrphanAsync"/>: verificato che è orfano, subito prima di cancellarlo.</summary>
    BeforeDelete,
}

/// <summary>Esito di una scrittura o ispezione: chiave server, lunghezza reale dallo stream e SHA-256 esadecimale minuscolo.</summary>
public sealed record StoredDocumentFile(string StorageKey, long ByteLength, string Sha256);

/// <summary>
/// Storage privato degli originali (plan.md §Acquisizione, storage e parser; data-model §Documenti). Layout sotto la radice
/// configurata <c>Documents:StorageRoot</c> (fuori da webroot): <c>objects/{progetto}/{oggetto}</c> per gli originali immutabili e
/// <c>tmp/{oggetto}.part</c> per le scritture in corso, sullo stesso volume così il rename è atomico.
/// Le chiavi sono generate dal server (<see cref="KeyFor"/>), contengono il progetto e non derivano mai da un nome file:
/// stesso contenuto in progetti diversi = oggetti diversi (nessuna deduplica globale). Il percorso si ricava solo da chiavi nel formato
/// esatto e ogni componente sotto la radice non può essere un reparse point (symlink/junction). Nessun contenuto né nome file nei log.
/// Lo store non decide la quota né sostituisce versioni: ciò resta a DocumentUploadService (T012). Limite noto: .NET non espone
/// fsync sulla cartella, quindi la durabilità del rename in sé dipende dal file system; il file è comunque scaricato su disco prima.
/// </summary>
public sealed partial class DocumentFileStore
{
    private const int BufferSize = 81920;
    private const string ObjectsDirectory = "objects";
    private const string TempDirectory = "tmp";
    private const string TempSuffix = ".part";

    private readonly string _root;
    private readonly ILogger<DocumentFileStore> _logger;
    private readonly object _initLock = new();
    private bool _initialized;

    public DocumentFileStore(IOptions<DocumentOptions> options, IWebHostEnvironment environment, ILogger<DocumentFileStore> logger)
        : this(ConfiguredRoot(options.Value), PublicRoots(environment), logger)
    {
    }

    /// <summary>La radice non può stare dentro (né contenere) una cartella servita come file statici: gli originali non hanno URL pubblico.</summary>
    public DocumentFileStore(string root, IEnumerable<string> publicRoots, ILogger<DocumentFileStore> logger)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _logger = logger;
        foreach (var publicRoot in publicRoots.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var web = Path.TrimEndingDirectorySeparator(Path.GetFullPath(publicRoot));
            if (IsUnder(_root, web) || IsUnder(web, _root))
            {
                throw new InvalidOperationException("Documents:StorageRoot non può trovarsi in (o contenere) la cartella servita pubblicamente.");
            }
        }
    }

    /// <summary>
    /// Solo per i test: invocato ai punti di <see cref="DocumentStoreStage"/> durante <see cref="WriteAsync"/>; un'eccezione sollevata qui
    /// simula un guasto in quel punto. In produzione resta null.
    /// </summary>
    public Action<DocumentStoreStage>? OnStage { get; set; }

    /// <summary>Chiave server dell'oggetto: <c>{progetto:N}/{oggetto:N}</c>. L'oggetto è un id generato dal server (es. la ricevuta di upload).</summary>
    public static string KeyFor(Guid projectId, Guid objectId) => $"{projectId:N}/{objectId:N}";

    /// <summary>
    /// Scrive lo stream in un temporaneo, ne calcola lunghezza e SHA-256 mentre lo legge, scarica il file su disco e lo rinomina
    /// nella chiave finale. Il limite di byte è duro: si legge al più un byte oltre <paramref name="maxBytes"/>. Su errore il temporaneo
    /// viene rimosso; dopo il rename l'originale resta (orfano finché un record lo referenzia o lo sweep lo verifica).
    /// Se la chiave esiste già con gli stessi byte (replay dopo crash) la scrittura è idempotente.
    /// </summary>
    public async Task<StoredDocumentFile> WriteAsync(Guid projectId, Guid objectId, Stream content, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        var key = KeyFor(projectId, objectId);
        string finalPath, tempPath;
        try
        {
            Initialize();
            finalPath = ResolveForWrite(projectId, objectId);
            tempPath = Path.Combine(TempPath(), $"{objectId:N}{TempSuffix}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw MapIo(ex);
        }

        long total = 0;
        string sha256;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            FileStream file;
            try
            {
                // FileShare.None: due scritture concorrenti sullo stesso oggetto non si intrecciano.
                file = new FileStream(tempPath, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = BufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw MapIo(ex);
            }

            await using (file)
            {
                while (true)
                {
                    var toRead = (int)Math.Min(buffer.Length, maxBytes - total + 1);   // mai più di un byte oltre il limite
                    var read = await content.ReadAsync(buffer.AsMemory(0, toRead), ct);   // errori del sorgente: non sono guasti del volume
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > maxBytes)
                    {
                        throw new DocumentStoreException(DocumentStoreFailure.TooLarge, "Il file supera il limite consentito.");
                    }

                    hash.AppendData(buffer.AsSpan(0, read));
                    try
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw MapIo(ex);
                    }
                }

                if (total == 0)
                {
                    throw new DocumentStoreException(DocumentStoreFailure.Empty, "Il file è vuoto.");
                }

                try
                {
                    OnStage?.Invoke(DocumentStoreStage.TempWritten);
                    await file.FlushAsync(ct);
                    file.Flush(flushToDisk: true);   // i byte sono su disco prima del rename: nessuna versione punta a un file non durevole
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw MapIo(ex);
                }
            }

            sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            OnStage?.Invoke(DocumentStoreStage.BeforeRename);
            await PublishAsync(tempPath, finalPath, key, total, sha256, ct);
        }
        catch
        {
            TryDelete(Path.Combine(TempPath(), $"{objectId:N}{TempSuffix}"));   // un temporaneo non è mai referenziato: rimuoverlo non perde dati
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        OnStage?.Invoke(DocumentStoreStage.AfterRename);
        _logger.LogInformation("Originale salvato: chiave {StorageKey}, {Bytes} byte", key, total);
        return new StoredDocumentFile(key, total, sha256);
    }

    /// <summary>
    /// Apre l'originale in lettura dopo averne verificato lunghezza e SHA-256 registrati (download e parsing mai su byte alterati).
    /// L'handle è aperto una volta sola con FileShare.Read: i byte verificati sono quelli serviti. Il chiamante lo elimina.
    /// </summary>
    public async Task<FileStream> OpenVerifiedAsync(string storageKey, string expectedSha256, long expectedLength, CancellationToken ct)
    {
        var stream = OpenForRead(storageKey);
        try
        {
            var (length, sha256) = await HashAsync(stream, ct);
            if (length != expectedLength || !string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Integrità violata: chiave {StorageKey}", storageKey);
                throw new DocumentStoreException(DocumentStoreFailure.IntegrityFailure, "I byte dell'originale non corrispondono a quelli registrati.");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Apre l'originale appena scritto in sola lettura, senza rileggerlo per l'hash (il chiamante lo ha appena calcolato): per i controlli
    /// di contenuto di DocumentUploadService prima del commit. Il chiamante lo elimina.
    /// </summary>
    public FileStream OpenForValidation(string storageKey) => OpenForRead(storageKey);

    /// <summary>Lunghezza e hash dei byte presenti sotto la chiave, o null se non esiste: per riconciliare residui dopo un crash.</summary>
    public async Task<StoredDocumentFile?> InspectAsync(string storageKey, CancellationToken ct)
    {
        FileStream stream;
        try
        {
            stream = OpenForRead(storageKey);
        }
        catch (DocumentStoreException ex) when (ex.Reason == DocumentStoreFailure.NotFound)
        {
            return null;
        }

        await using (stream)
        {
            var (length, sha256) = await HashAsync(stream, ct);
            return new StoredDocumentFile(storageKey, length, sha256);
        }
    }

    /// <summary>
    /// Elimina i soli temporanei (<c>tmp/*.part</c>) non toccati da più di <paramref name="olderThan"/>. Non guarda mai gli originali:
    /// un file referenziato non può essere rimosso da qui. I temporanei aperti da una scrittura in corso restano (sharing violation).
    /// </summary>
    public int SweepTemporaries(TimeSpan olderThan)
    {
        var tmp = TempPath();
        if (!Directory.Exists(tmp) || IsReparsePoint(tmp))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - olderThan;
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(tmp, "*" + TempSuffix))
        {
            try
            {
                if (!IsReparsePoint(path) && File.GetLastWriteTimeUtc(path) <= cutoff)
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    removed++;
                }
            }
            catch (IOException)
            {
                // in uso da una scrittura viva: lo riprenderà il prossimo giro
            }
        }

        return removed;
    }

    /// <summary>
    /// Rimuove un originale orfano solo se <paramref name="isReferenced"/> dichiara che nessun record (versione o ricevuta non scaduta)
    /// lo referenzia. Ritorna false e lascia il file se è referenziato o non esiste. Il verdetto di riferimento è del chiamante.
    /// </summary>
    public async Task<bool> DeleteOrphanAsync(string storageKey, Func<string, CancellationToken, Task<bool>> isReferenced, CancellationToken ct)
    {
        var path = ResolveExisting(storageKey);
        if (path is null || await isReferenced(storageKey, ct))
        {
            return false;
        }

        OnStage?.Invoke(DocumentStoreStage.BeforeDelete);
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
        _logger.LogInformation("Originale orfano rimosso: chiave {StorageKey}", storageKey);
        return true;
    }

    // ---- percorsi -------------------------------------------------------------------------------------------------------------

    private string TempPath() => Path.Combine(_root, TempDirectory);

    private void Initialize()
    {
        lock (_initLock)
        {
            if (!_initialized)
            {
                Directory.CreateDirectory(_root);
                Directory.CreateDirectory(Path.Combine(_root, ObjectsDirectory));
                Directory.CreateDirectory(TempPath());
                _initialized = true;
            }
        }

        // Le sottocartelle gestite non possono essere reparse point (la radice stessa può essere un mount configurato).
        RejectReparse(Path.Combine(_root, ObjectsDirectory));
        RejectReparse(TempPath());
    }

    private string ResolveForWrite(Guid projectId, Guid objectId)
    {
        var projectDirectory = Path.Combine(_root, ObjectsDirectory, projectId.ToString("N"));
        RejectReparse(projectDirectory);   // se esiste ed è un collegamento, si rifiuta prima di creare/scrivere
        Directory.CreateDirectory(projectDirectory);
        RejectReparse(projectDirectory);
        var path = Path.Combine(projectDirectory, objectId.ToString("N"));
        RejectReparse(path);
        return EnsureInsideRoot(path);
    }

    /// <summary>Percorso di un originale esistente; null se manca. Chiave malformata o reparse point lungo il percorso → InvalidKey.</summary>
    private string? ResolveExisting(string storageKey)
    {
        var (project, obj) = ParseKey(storageKey);
        var objects = Path.Combine(_root, ObjectsDirectory);
        var projectDirectory = Path.Combine(objects, project);
        var path = Path.Combine(projectDirectory, obj);
        foreach (var component in new[] { objects, projectDirectory, path })
        {
            if (!Exists(component))
            {
                return null;
            }

            RejectReparse(component);
        }

        return EnsureInsideRoot(path);
    }

    private static (string Project, string Object) ParseKey(string storageKey)
    {
        var match = storageKey is null ? null : KeyPattern().Match(storageKey);
        if (match is null || !match.Success)
        {
            throw new DocumentStoreException(DocumentStoreFailure.InvalidKey, "Chiave di storage non valida.");
        }

        return (match.Groups["p"].Value, match.Groups["o"].Value);
    }

    private string EnsureInsideRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (!IsUnder(full, _root))
        {
            throw new DocumentStoreException(DocumentStoreFailure.InvalidKey, "Percorso fuori dalla radice di storage.");
        }

        return full;
    }

    private FileStream OpenForRead(string storageKey)
    {
        var path = ResolveExisting(storageKey)
            ?? throw new DocumentStoreException(DocumentStoreFailure.NotFound, "Originale non trovato.");
        try
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        }
        catch (FileNotFoundException ex)
        {
            throw new DocumentStoreException(DocumentStoreFailure.NotFound, "Originale non trovato.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw MapIo(ex);
        }
    }

    private static async Task<(long Length, string Sha256)> HashAsync(Stream stream, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long length = 0;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
                length += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>Rename atomico del temporaneo nella chiave finale (stesso volume). Se esiste già con gli stessi byte è un replay idempotente.</summary>
    private async Task PublishAsync(string tempPath, string finalPath, string key, long length, string sha256, CancellationToken ct)
    {
        try
        {
            File.SetAttributes(tempPath, FileAttributes.ReadOnly);   // l'originale è immutabile anche per il processo
            if (File.Exists(finalPath))
            {
                var existing = await InspectAsync(key, ct);
                if (existing is null || existing.ByteLength != length || existing.Sha256 != sha256)
                {
                    throw new DocumentStoreException(DocumentStoreFailure.IntegrityFailure, "La chiave esiste già con byte diversi.");
                }

                TryDelete(tempPath);
                return;
            }

            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw MapIo(ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // resta un temporaneo orfano: lo riprende SweepTemporaries
        }
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    private static bool IsReparsePoint(string path) =>
        Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void RejectReparse(string path)
    {
        if (IsReparsePoint(path))
        {
            throw new DocumentStoreException(DocumentStoreFailure.InvalidKey, "Collegamento simbolico non ammesso nello storage.");
        }
    }

    private static bool IsUnder(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(path, directory, comparison) || path.StartsWith(directory + Path.DirectorySeparatorChar, comparison);
    }

    private static bool IsDiskFull(Exception ex) =>
        OperatingSystem.IsWindows() ? (ex.HResult & 0xFFFF) is 39 or 112 : ex.HResult == 28;

    private static DocumentStoreException MapIo(Exception ex) =>
        IsDiskFull(ex)
            ? new(DocumentStoreFailure.InsufficientSpace, "Spazio insufficiente per salvare il file.", ex)
            : new(DocumentStoreFailure.Unavailable, "Storage degli originali non disponibile.", ex);

    private static string ConfiguredRoot(DocumentOptions options) =>
        string.IsNullOrWhiteSpace(options.StorageRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bProjects", "documents")
            : options.StorageRoot;

    private static IEnumerable<string> PublicRoots(IWebHostEnvironment environment)
    {
        yield return environment.WebRootPath ?? string.Empty;
        yield return Path.Combine(environment.ContentRootPath, "wwwroot");
    }

    [GeneratedRegex("^(?<p>[0-9a-f]{32})/(?<o>[0-9a-f]{32})$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
