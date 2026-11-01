using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Documents;

public enum UploadOpenKind
{
    /// <summary>Ricevuta Receiving aperta (o riaperta): il chiamante può ricevere lo stream.</summary>
    Proceed,

    /// <summary>Stessa chiave già acquisita: nessun lavoro nuovo, si confronta solo l'input.</summary>
    Replay,

    /// <summary>Stessa chiave ancora in ricezione: 409 upload_in_progress.</summary>
    InProgress,

    /// <summary>Stessa chiave con destinazione diversa: 409 idempotency_conflict.</summary>
    Conflict,

    /// <summary>Nessuno spazio residuo nella quota originali del progetto: 507.</summary>
    QuotaExhausted,
}

/// <summary>Esito dell'apertura: ricevuta, e per Proceed lo stato di quota letto sotto lock (byte già acquisiti dal progetto).</summary>
public sealed record UploadOpening(
    UploadOpenKind Kind, Guid ReceiptId, long CommittedBytes = 0, string? PreviousInputHash = null, string? ReceiptInputHash = null, Guid? ResultVersionId = null);

public sealed record ReceivedFile(StoredDocumentFile File, DocumentFormat Format);

public enum UploadCommitKind
{
    Created,

    /// <summary>L'input differisce da quello di un precedente tentativo con la stessa chiave.</summary>
    Conflict,

    QuotaExhausted,

    /// <summary>La ricevuta non è più Receiving (scaduta e riconciliata): nulla è stato acquisito.</summary>
    Lost,
}

public sealed record UploadCommit(UploadCommitKind Kind, Guid? VersionId = null);

/// <summary>Esito di un passo di pulizia degli orfani: file esaminati e rimossi, e dove riprendere (null = giro completato).</summary>
public sealed record OrphanSweepResult(int Examined, int Removed, string? NextCursor);

/// <summary>
/// Acquisizione degli originali (T012; plan.md §Acquisizione, data-model §Documenti). Il flusso è in tre tempi, ciascuno con la propria
/// transazione breve sotto il lock di progetto (mai tenuto durante lo stream):
/// <list type="number">
/// <item><see cref="OpenAsync"/>: riconcilia le ricevute scadute del progetto, applica l'idempotenza e apre/riapre la ricevuta Receiving
/// con la quota riservata.</item>
/// <item><see cref="ReceiveAsync"/>: stream limitato, hash SHA-256, verifica di firma e contenuto, rename atomico sul volume privato.</item>
/// <item><see cref="CommitAsync"/>: sotto lock verifica di nuovo la quota con la dimensione reale e, solo con il file già durevole,
/// scrive in un'unica transazione documento (se nuovo), versione, preparazione e job di parsing Queued e ricevuta Committed.</item>
/// </list>
/// Il database non è transazionale col filesystem: un guasto dopo il rename lascia un originale non referenziato e una ricevuta che
/// non è mai Committed (quindi mai un falso «acquisito»). L'originale ha chiave = (progetto, ricevuta): il replay con la stessa chiave
/// riadotta lo stesso oggetto se i byte coincidono, senza duplicati. Le ricevute scadute diventano Aborted e i loro orfani non referenziati
/// vengono rimossi; le ricevute non contengono i metadati completi, quindi non si finalizza mai in automatico.
/// </summary>
public sealed class DocumentUploadService(AppDbContext db, DocumentFileStore store, ILogger<DocumentUploadService> logger)
{
    /// <summary>Margine oltre il timeout di ricezione prima che una ricevuta Receiving sia considerata abbandonata.</summary>
    private const int ExpiryGraceSeconds = 60;

    /// <summary>
    /// Stima per difetto dell'overhead multipart (boundary, intestazioni, campi): sottratta al Content-Length per la riserva. Sotto questa soglia la
    /// riserva è 0: una stima per eccesso farebbe rifiutare upload che entrano (la quota è comunque verificata sotto lock con i byte reali).
    /// </summary>
    private const long MultipartOverheadAllowance = 65_536;

    private const int OrphanLookbackDays = 1;
    private const int MaxOrphansPerPass = 50;

    /// <summary>Hash segnaposto: la ricevuta è stata aperta ma l'input completo non è mai stato visto.</summary>
    public static readonly string UnknownInputHash = new('0', 64);

    public async Task<UploadOpening> OpenAsync(
        Guid projectId, Guid requestKey, Guid? targetDocumentId, long? contentLength, DocumentLimits limits, CancellationToken ct)
    {
        UploadOpening result;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await ProjectLock.AcquireAsync(db, projectId, ct);
            var now = await ProjectLock.NowAsync(db, ct);
            await ReconcileAsync(projectId, now, ct);

            var receipt = await db.UploadReceipts.SingleOrDefaultAsync(r => r.ProjectId == projectId && r.RequestKey == requestKey, ct);
            UploadOpening? early = receipt switch
            {
                null => null,
                _ when receipt.DocumentId != targetDocumentId => new(UploadOpenKind.Conflict, receipt.Id),
                { Status: UploadReceiptStatus.Committed } => new(UploadOpenKind.Replay, receipt.Id, ReceiptInputHash: receipt.InputHash, ResultVersionId: receipt.ResultVersionId),
                { Status: UploadReceiptStatus.Receiving } => new(UploadOpenKind.InProgress, receipt.Id),
                _ => null,
            };
            if (early is not null)
            {
                await tx.CommitAsync(ct);   // la riconciliazione fatta sopra va conservata anche se la richiesta non procede
                db.ChangeTracker.Clear();
                return early;
            }

            var (committed, reserved) = await UsedBytesAsync(projectId, now, receipt?.Id, ct);
            var headroom = limits.ProjectQuotaBytes - committed - reserved;
            // Per difetto: il file è almeno Content-Length meno l'overhead multipart. Se già questo non entra, il rifiuto è certo.
            var lowerBound = contentLength is { } length ? Math.Max(0, length - MultipartOverheadAllowance) : 0;
            if (headroom <= 0 || headroom < lowerBound)
            {
                await tx.CommitAsync(ct);
                db.ChangeTracker.Clear();
                return new(UploadOpenKind.QuotaExhausted, Guid.Empty);
            }

            // Senza Content-Length (chunked) la dimensione è ignota: si riserva il massimo per file, così upload paralleli senza lunghezza
            // non eludono la quota; la dimensione reale è comunque verificata sotto lock al commit.
            var estimate = contentLength is null ? limits.MaxFileBytes : lowerBound;
            var reserve = Math.Min(Math.Min(estimate, limits.MaxFileBytes), headroom);
            var expires = now.AddSeconds(limits.UploadTimeoutSeconds + ExpiryGraceSeconds);
            if (receipt is null)
            {
                receipt = new UploadReceipt
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    DocumentId = targetDocumentId,
                    RequestKey = requestKey,
                    InputHash = UnknownInputHash,
                    ReservedBytes = reserve,
                    Status = UploadReceiptStatus.Receiving,
                    CreatedAt = now,
                    ExpiresAt = expires,
                };
                db.UploadReceipts.Add(receipt);
                result = new(UploadOpenKind.Proceed, receipt.Id, committed);
            }
            else
            {
                // Tentativo precedente interrotto (Aborted): si riapre la stessa ricevuta, quindi lo stesso oggetto di storage.
                var previous = receipt.InputHash == UnknownInputHash ? null : receipt.InputHash;
                receipt.Status = UploadReceiptStatus.Receiving;
                receipt.ReservedBytes = reserve;
                receipt.ExpiresAt = expires;
                result = new(UploadOpenKind.Proceed, receipt.Id, committed, previous);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        db.ChangeTracker.Clear();
        SweepTemporaries(limits);
        return result;
    }

    /// <summary>Elimina i soli temporanei di scritture morte (mai gli originali): oltre il timeout di ricezione più un margine nessuno li sta più usando.</summary>
    private void SweepTemporaries(DocumentLimits limits)
    {
        try
        {
            store.SweepTemporaries(TimeSpan.FromSeconds(limits.UploadTimeoutSeconds + ExpiryGraceSeconds));
        }
        catch (Exception ex) when (ex is DocumentStoreException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Pulizia dei temporanei non riuscita: {ExceptionType}", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Riceve lo stream: firma dei primi byte (prima di creare file), scrittura limitata con hash, controlli di contenuto sul file durevole.
    /// Su rifiuto di contenuto l'originale appena scritto (non referenziato) è rimosso. Solleva <see cref="UploadRejectedException"/>.
    /// </summary>
    public async Task<ReceivedFile> ReceiveAsync(
        UploadOpening opening, Guid projectId, DocumentFormat format, Stream body, DocumentLimits limits, CancellationToken ct)
    {
        var head = new byte[DocumentUploadValidation.HeadBytes];
        var headLength = await body.ReadAtLeastAsync(head.AsMemory(), head.Length, throwOnEndOfStream: false, ct);
        DocumentUploadValidation.CheckHead(format, head.AsSpan(0, headLength));

        // Il tetto è il minore fra limite per file e spazio che la quota lascerebbe anche se nessun altro caricasse: oltre è 507, non 413.
        var quotaRoom = Math.Max(1, limits.ProjectQuotaBytes - opening.CommittedBytes);
        var limit = Math.Min(limits.MaxFileBytes, quotaRoom);
        StoredDocumentFile stored;
        try
        {
            stored = await store.WriteAsync(projectId, opening.ReceiptId, new HeadReplayStream(head, headLength, body), limit, ct);
        }
        catch (DocumentStoreException ex)
        {
            var mapped = ex.Reason switch
            {
                DocumentStoreFailure.TooLarge when limit < limits.MaxFileBytes =>
                    new UploadRejectedException(StatusCodes.Status507InsufficientStorage, "quota_exceeded", "Quota dei documenti del progetto esaurita"),
                DocumentStoreFailure.TooLarge =>
                    new UploadRejectedException(StatusCodes.Status413PayloadTooLarge, "file_too_large", "Il file supera il limite consentito"),
                DocumentStoreFailure.Empty =>
                    new UploadRejectedException(StatusCodes.Status422UnprocessableEntity, "empty_file", "Il file è vuoto"),
                DocumentStoreFailure.InsufficientSpace =>
                    new UploadRejectedException(StatusCodes.Status507InsufficientStorage, "storage_full", "Spazio di archiviazione insufficiente"),
                DocumentStoreFailure.Unavailable =>
                    new UploadRejectedException(StatusCodes.Status503ServiceUnavailable, "storage_unavailable", "Archiviazione temporaneamente non disponibile"),
                DocumentStoreFailure.IntegrityFailure =>
                    new UploadRejectedException(StatusCodes.Status409Conflict, "idempotency_conflict", "La chiave di idempotenza è già stata usata con un contenuto diverso"),
                _ => null,
            };
            if (mapped is null)
            {
                throw;
            }

            throw mapped;
        }

        try
        {
            await using var written = store.OpenForValidation(stored.StorageKey);
            await DocumentUploadValidation.ValidateStoredAsync(format, written, limits.MaxExpandedBytes, ct);
        }
        catch (UploadRejectedException)
        {
            await RemoveUnreferencedAsync(stored.StorageKey);
            throw;
        }

        return new(stored, format);
    }

    /// <summary>
    /// Replay di una chiave già acquisita: legge il corpo solo per calcolarne l'hash (nulla viene scritto), con lo stesso tetto di byte.
    /// </summary>
    public static async Task<(long Length, string Sha256)> HashOnlyAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new UploadRejectedException(StatusCodes.Status413PayloadTooLarge, "file_too_large", "Il file supera il limite consentito");
            }

            hash.AppendData(buffer.AsSpan(0, read));
        }

        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>Hash canonico di file, metadati e destinazione: base dell'idempotenza (la stessa chiave con input diverso è 409).</summary>
    public static string InputHash(Guid? targetDocumentId, string displayName, string originalName, string sha256) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"upload-v1\n{(targetDocumentId is { } d ? d.ToString("N") : "new")}\n{displayName}\n{originalName}\n{sha256}")));

    public async Task<UploadCommit> CommitAsync(
        UploadOpening opening, Guid projectId, Guid? targetDocumentId, ReceivedFile received, string originalName, string displayName,
        Guid uploadedBy, DocumentLimits limits, string policyHash, CancellationToken ct)
    {
        var inputHash = InputHash(targetDocumentId, displayName, originalName, received.File.Sha256);
        UploadCommit outcome;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await ProjectLock.AcquireAsync(db, projectId, ct);
            var now = await ProjectLock.NowAsync(db, ct);
            var receipt = await db.UploadReceipts.SingleOrDefaultAsync(r => r.Id == opening.ReceiptId && r.ProjectId == projectId, ct);
            if (receipt is not { Status: UploadReceiptStatus.Receiving })
            {
                return new(UploadCommitKind.Lost);   // scaduta e riconciliata mentre riceveva: nulla acquisito
            }

            if (opening.PreviousInputHash is { } previous && previous != inputHash)
            {
                receipt.Status = UploadReceiptStatus.Aborted;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                db.ChangeTracker.Clear();
                return new(UploadCommitKind.Conflict);
            }

            // Quota con la dimensione reale: acquisiti + prenotazioni vive degli altri + questo file, tutto sotto lock.
            var (committed, reservedByOthers) = await UsedBytesAsync(projectId, now, receipt.Id, ct);
            if (committed + reservedByOthers + received.File.ByteLength > limits.ProjectQuotaBytes)
            {
                receipt.Status = UploadReceiptStatus.Aborted;
                await db.SaveChangesAsync(ct);
                // Controllo di riferimento e cancellazione sotto il lock di progetto, prima di rilasciarlo: un retry con la stessa chiave
                // (stesso StorageKey) può riaprire la ricevuta e riscrivere il file solo dopo, mai fra verifica e cancellazione.
                await RemoveUnreferencedAsync(received.File.StorageKey);
                await tx.CommitAsync(ct);
                db.ChangeTracker.Clear();
                return new(UploadCommitKind.QuotaExhausted);
            }

            ProjectDocument document;
            if (targetDocumentId is { } existing)
            {
                document = await db.ProjectDocuments.SingleAsync(d => d.Id == existing && d.ProjectId == projectId, ct);
            }
            else
            {
                document = new ProjectDocument { Id = Guid.NewGuid(), ProjectId = projectId, DisplayName = displayName, CreatedBy = uploadedBy, CreatedAt = now };
                db.ProjectDocuments.Add(document);
            }

            var next = (await db.DocumentVersions.Where(v => v.DocumentId == document.Id).MaxAsync(v => (int?)v.Version, ct) ?? 0) + 1;
            var version = new DocumentVersion
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                DocumentId = document.Id,
                Version = next,
                OriginalName = originalName,
                MediaType = DocumentUploadValidation.MediaTypeOf(received.Format),
                ByteLength = received.File.ByteLength,
                Sha256 = received.File.Sha256,
                StorageKey = received.File.StorageKey,
                UploadedBy = uploadedBy,
                UploadedAt = now,
                UploadReceiptId = receipt.Id,
            };
            // t0 della preparazione = commit dell'acquisizione (orologio del database); il cap è congelato adesso.
            var hardDeadline = now.AddSeconds(limits.PreparationDeadlineSeconds);
            var preparation = new DocumentPreparation
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                DocumentVersionId = version.Id,
                Generation = 1,
                Status = PreparationStatus.Queued,
                RequestedAt = now,
                PolicyHash = policyHash,
                HardDeadlineAt = hardDeadline,
                EffectiveDeadlineAt = hardDeadline,
                CreatedAt = now,
            };
            db.DocumentVersions.Add(version);
            db.DocumentPreparations.Add(preparation);
            receipt.InputHash = inputHash;
            receipt.ReservedBytes = received.File.ByteLength;
            receipt.Status = UploadReceiptStatus.Committed;
            receipt.ResultVersionId = version.Id;
            await db.SaveChangesAsync(ct);

            // Il job nasce Queued (mai eseguito qui) e la preparazione lo referenzia: FK circolare, quindi in due passi nella stessa transazione.
            var job = new DocumentJob
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                PreparationId = preparation.Id,
                Kind = DocumentJobKind.Parse,
                Status = DocumentJobStatus.Queued,
                RequestedBy = uploadedBy,
                RequestedAt = now,
                DeadlineAt = hardDeadline,
                MaxAttempts = 3,
                ConfigJson = JsonSerializer.Serialize(new
                {
                    policyHash,
                    limits.ParseTimeoutSeconds,
                    limits.MaxPages,
                    limits.MaxBlocks,
                    limits.MaxExpandedBytes,
                    limits.MaxDerivedBytes,
                }),
            };
            db.DocumentJobs.Add(job);
            await db.SaveChangesAsync(ct);
            preparation.ParseJobId = job.Id;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            outcome = new(UploadCommitKind.Created, version.Id);
        }

        db.ChangeTracker.Clear();
        return outcome;
    }

    /// <summary>Rilascia la prenotazione di un tentativo fallito (best effort, anche a richiesta annullata). L'originale eventuale resta per il replay.</summary>
    public async Task AbortAsync(Guid receiptId)
    {
        try
        {
            db.ChangeTracker.Clear();
            await db.UploadReceipts
                .Where(r => r.Id == receiptId && r.Status == UploadReceiptStatus.Receiving)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, UploadReceiptStatus.Aborted), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Se anche questo fallisce, la ricevuta scade da sola e la riconciliazione la annulla.
            logger.LogWarning("Ricevuta {ReceiptId} non annullata: {ExceptionType}", receiptId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Riconciliazione dei residui dopo un guasto, nello stesso progetto e sotto il suo lock (chiamare dentro una transazione). Percorso veloce,
    /// limitato alle ricevute scadute di recente: gli orfani più vecchi o oltre il tetto per passo li raccoglie <see cref="SweepOrphansAsync"/>.
    /// </summary>
    private async Task ReconcileAsync(Guid projectId, DateTimeOffset now, CancellationToken ct)
    {
        await AbortExpiredAsync(projectId, now, ct);

        var since = now.AddDays(-OrphanLookbackDays);
        var stale = await db.UploadReceipts.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Status == UploadReceiptStatus.Aborted && r.ExpiresAt <= now && r.ExpiresAt > since)
            .OrderByDescending(r => r.ExpiresAt)
            .Take(MaxOrphansPerPass)
            .Select(r => r.Id)
            .ToListAsync(ct);
        foreach (var id in stale)
        {
            await RemoveUnreferencedAsync(DocumentFileStore.KeyFor(projectId, id));
        }
    }

    private async Task AbortExpiredAsync(Guid projectId, DateTimeOffset now, CancellationToken ct)
    {
        var expired = await db.UploadReceipts
            .Where(r => r.ProjectId == projectId && r.Status == UploadReceiptStatus.Receiving && r.ExpiresAt <= now)
            .ToListAsync(ct);
        foreach (var receipt in expired)
        {
            receipt.Status = UploadReceiptStatus.Aborted;   // nessun metadato completo da finalizzare: la ricevuta non diventa mai acquisita
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Pulizia periodica degli originali orfani, indipendente dagli upload (un progetto fermo, un riavvio o una ricevuta Aborted più vecchia della
    /// finestra di <see cref="ReconcileAsync"/> non lasciano più storage perso). Parte dal volume, non dalle ricevute: ogni file più vecchio di
    /// <paramref name="minAge"/> con chiave dopo <paramref name="after"/> è un candidato; si esaminano al più <paramref name="maxExamined"/> chiavi e se ne
    /// rimuovono al più <paramref name="maxRemoved"/> per passo, e il cursore restituito permette di riprendere (dopo un riavvio si riparte da zero).
    /// Un file è rimosso solo sotto il lock del suo progetto, dopo aver abortito le ricevute scadute, se nessuna versione lo referenzia e la sua
    /// ricevuta non è né Committed né una Receiving ancora viva. Una ricevuta Aborted riaperta dallo stesso tentativo riscrive il file (idempotente).
    /// </summary>
    public async Task<OrphanSweepResult> SweepOrphansAsync(string? after, TimeSpan minAge, int maxExamined, int maxRemoved, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - minAge;
        var batch = store.EnumerateObjects(after).Take(maxExamined).ToList();
        var removed = 0;
        string? cursor = batch.Count < maxExamined ? null : batch[^1].StorageKey;
        foreach (var project in batch.GroupBy(o => o.StorageKey[..32]))
        {
            var candidates = project.Where(o => o.LastWriteUtc <= cutoff).Select(o => o.StorageKey).ToList();
            if (candidates.Count > 0)
            {
                removed += await SweepProjectAsync(Guid.ParseExact(project.Key, "N"), candidates, maxRemoved - removed, ct);
            }

            if (removed >= maxRemoved)
            {
                cursor = project.Last().StorageKey;   // tetto raggiunto: il progetto è stato esaminato per intero, si riprende dopo
                break;
            }
        }

        return new(batch.Count, removed, cursor);
    }

    private async Task<int> SweepProjectAsync(Guid projectId, List<string> keys, int budget, CancellationToken ct)
    {
        var removed = 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ProjectLock.AcquireAsync(db, projectId, ct);
        var now = await ProjectLock.NowAsync(db, ct);
        await AbortExpiredAsync(projectId, now, ct);
        var objectIds = keys.Select(k => Guid.ParseExact(k[33..], "N")).ToList();
        var protectedIds = (await db.UploadReceipts.AsNoTracking()
            .Where(r => r.ProjectId == projectId && objectIds.Contains(r.Id)
                && (r.Status == UploadReceiptStatus.Committed || (r.Status == UploadReceiptStatus.Receiving && r.ExpiresAt > now)))
            .Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        foreach (var key in keys)
        {
            if (removed >= budget)
            {
                break;
            }

            if (!protectedIds.Contains(Guid.ParseExact(key[33..], "N")) && await RemoveUnreferencedAsync(key))
            {
                removed++;
            }
        }

        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return removed;
    }

    /// <summary>Rimuove un originale solo se nessuna versione lo referenzia (mai un file con riferimento nel database).</summary>
    private async Task<bool> RemoveUnreferencedAsync(string storageKey)
    {
        try
        {
            return await store.DeleteOrphanAsync(
                storageKey,
                async (key, token) => await db.DocumentVersions.AsNoTracking().AnyAsync(v => v.StorageKey == key, token)
                    || await db.ParsedArtifacts.AsNoTracking().AnyAsync(a => a.StorageKey == key, token),   // gli artefatti di parsing vivono nello stesso volume
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is DocumentStoreException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Originale orfano non rimosso: {ExceptionType}", ex.GetType().Name);
            return false;
        }
    }

    private async Task<(long Committed, long ReservedByOthers)> UsedBytesAsync(Guid projectId, DateTimeOffset now, Guid? excludeReceipt, CancellationToken ct)
    {
        var committed = await db.DocumentVersions.AsNoTracking().Where(v => v.ProjectId == projectId).SumAsync(v => (long?)v.ByteLength, ct) ?? 0;
        var reserved = await db.UploadReceipts.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Status == UploadReceiptStatus.Receiving && r.ExpiresAt > now && r.Id != (excludeReceipt ?? Guid.Empty))
            .SumAsync(r => (long?)r.ReservedBytes, ct) ?? 0;
        return (committed, reserved);
    }
}
