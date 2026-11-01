using System.Security.Cryptography;
using System.Text;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Documents;

/// <summary>Un documento del progetto con la preferenza effettiva del PM (default: incluso, ultima versione pronta) e la disponibilità osservata.</summary>
public sealed record SourceEntry(
    ProjectDocument Document,
    bool Included,
    SourceMode Mode,
    Guid? PinnedVersionId,
    SourceAvailability Availability,
    DocumentVersion? Resolved,
    DocumentPreparation? ReadyPreparation,
    DocumentVersion? Latest,
    DocumentPreparation? LatestPreparation)
{
    /// <summary>Versione di cui descrivere lo stato quando il documento non è pronto: quella fissata, altrimenti l'ultima caricata.</summary>
    public DocumentVersion? Subject => Mode == SourceMode.Pinned ? Resolved : Resolved ?? Latest;
}

public sealed record SourceEvaluation(long Revision, IReadOnlyList<SourceEntry> Entries)
{
    public bool HasDocuments => Entries.Count > 0;

    /// <summary>Documenti inclusi ma non pronti: bloccano Analizza e sono dichiarati indisponibili in chat.</summary>
    public IEnumerable<SourceEntry> Blocking => Entries.Where(e => e.Included && e.Availability != SourceAvailability.Ready);
}

public sealed record SourceItemView(
    Guid DocumentId, bool Included, string Mode, Guid? VersionId, Guid? ResolvedVersionId, Guid? PreparationId, string Availability);

public sealed record BlockingSourceView(Guid DocumentId, string DisplayName, Guid? VersionId, int? Version, string Status, string[] AllowedActions);

public sealed record SourceSelectionView(long Revision, IReadOnlyList<SourceItemView> Items, IReadOnlyList<BlockingSourceView> BlockingSources);

/// <summary>Esito del PUT: applicato, revisione obsoleta o un item non valido (con il codice HTTP da restituire).</summary>
public abstract record SelectionUpdate
{
    public sealed record Applied(SourceSelectionView View) : SelectionUpdate;

    public sealed record Conflict(long CurrentRevision) : SelectionUpdate;

    public sealed record Invalid(int Status, string Code, string Title) : SelectionUpdate;
}

public sealed record SelectionItemInput(Guid DocumentId, bool Included, SourceMode Mode, Guid? VersionId);

/// <summary>
/// Preferenze di fonte del progetto (T015). Una riga di revisione per progetto e un item per documento; il confronto di revisione è
/// atomico perché il PUT avviene sotto il lock di progetto condiviso con upload, Analizza e chat. I documenti senza item hanno il default
/// (incluso, LatestReady). Nessuna lettura avvia lavori: la risoluzione è pura sul database.
/// </summary>
public sealed class SourceSelectionService(AppDbContext db)
{
    public async Task<SourceEvaluation> EvaluateAsync(Guid projectId, CancellationToken ct)
    {
        var revision = await db.ProjectSourceSelections.AsNoTracking().Where(s => s.ProjectId == projectId).Select(s => (long?)s.Revision).SingleOrDefaultAsync(ct) ?? 0;
        var documents = await db.ProjectDocuments.AsNoTracking().Where(d => d.ProjectId == projectId).OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync(ct);
        if (documents.Count == 0)
        {
            return new(revision, []);
        }

        var items = await db.ProjectSourceSelectionItems.AsNoTracking().Where(i => i.ProjectId == projectId).ToDictionaryAsync(i => i.DocumentId, ct);
        var versions = await db.DocumentVersions.AsNoTracking().Where(v => v.ProjectId == projectId).ToListAsync(ct);
        var preparations = await db.DocumentPreparations.AsNoTracking().Where(p => p.ProjectId == projectId).ToListAsync(ct);
        var versionsById = versions.ToDictionary(v => v.Id);
        var latestByDocument = versions.GroupBy(v => v.DocumentId).ToDictionary(g => g.Key, g => g.MaxBy(v => v.Version)!);
        var preparationsByVersion = preparations.GroupBy(p => p.DocumentVersionId).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Generation).ToList());

        DocumentPreparation? Ready(DocumentVersion? version) =>
            version is not null && preparationsByVersion.TryGetValue(version.Id, out var list)
                ? list.FirstOrDefault(p => p.Status == PreparationStatus.Ready && p.CurrentEvidenceVersionId is not null)
                : null;

        var entries = new List<SourceEntry>(documents.Count);
        foreach (var document in documents)
        {
            items.TryGetValue(document.Id, out var item);
            var included = item?.Included ?? true;
            var mode = item?.Mode ?? SourceMode.LatestReady;
            var resolvedId = mode == SourceMode.Pinned ? item!.PinnedVersionId : document.LatestReadyVersionId;
            var resolved = resolvedId is { } id && versionsById.TryGetValue(id, out var found) ? found : null;
            var ready = Ready(resolved);
            latestByDocument.TryGetValue(document.Id, out var latest);
            var latestPreparation = latest is not null && preparationsByVersion.TryGetValue(latest.Id, out var latestList) ? latestList[0] : null;
            var availability = !included ? SourceAvailability.Excluded : ready is not null ? SourceAvailability.Ready : SourceAvailability.Unavailable;
            entries.Add(new(document, included, mode, item?.PinnedVersionId, availability, resolved, ready, latest, latestPreparation));
        }

        return new(revision, entries);
    }

    public async Task<SourceSelectionView> ViewAsync(Guid projectId, CancellationToken ct) => ToView(await EvaluateAsync(projectId, ct), await PreparationStatusesAsync(projectId, ct));

    public static SourceSelectionView ToView(SourceEvaluation evaluation, IReadOnlyDictionary<Guid, PreparationStatus> preparationStatusByVersion)
    {
        var items = evaluation.Entries.Select(e => new SourceItemView(
            e.Document.Id,
            e.Included,
            e.Mode.ToString(),
            e.PinnedVersionId,
            e.Resolved?.Id,
            e.ReadyPreparation?.Id,
            e.Availability.ToString())).ToList();
        var blocking = evaluation.Blocking.Select(e =>
        {
            var subject = e.Subject;
            var status = subject is not null && preparationStatusByVersion.TryGetValue(subject.Id, out var s) ? s.ToString() : "NoPreparation";
            string[] actions = status is "Failed" or "Incomplete" ? ["exclude", "retry"] : ["exclude"];
            return new BlockingSourceView(e.Document.Id, e.Document.DisplayName, subject?.Id, subject?.Version, status, actions);
        }).ToList();
        return new(evaluation.Revision, items, blocking);
    }

    /// <summary>Stato dell'ultima generazione di preparazione per ogni versione del progetto (per descrivere le fonti non pronte).</summary>
    public async Task<IReadOnlyDictionary<Guid, PreparationStatus>> PreparationStatusesAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await db.DocumentPreparations.AsNoTracking().Where(p => p.ProjectId == projectId)
            .Select(p => new { p.DocumentVersionId, p.Generation, p.Status }).ToListAsync(ct);
        return rows.GroupBy(r => r.DocumentVersionId).ToDictionary(g => g.Key, g => g.MaxBy(r => r.Generation)!.Status);
    }

    /// <summary>
    /// Sostituzione completa delle preferenze con confronto di revisione, sotto il lock di progetto. Due PUT sulla stessa revisione
    /// sono serializzati: uno vince, l'altro vede la revisione avanzata e riceve il conflitto.
    /// </summary>
    public async Task<SelectionUpdate> ReplaceAsync(Guid projectId, long expectedRevision, IReadOnlyList<SelectionItemInput> inputs, Guid author, CancellationToken ct)
    {
        // Rifiuti economici prima del lock di progetto: l'elenco non può superare i documenti del progetto né ripetere un documento.
        var documentCount = await db.ProjectDocuments.AsNoTracking().CountAsync(d => d.ProjectId == projectId, ct);
        if (inputs.Count > documentCount)
        {
            return new SelectionUpdate.Invalid(StatusCodes.Status422UnprocessableEntity, "too_many_items", "La selezione ha più elementi dei documenti del progetto");
        }

        var requested = new HashSet<Guid>(inputs.Count);
        if (!inputs.All(i => requested.Add(i.DocumentId)))
        {
            return new SelectionUpdate.Invalid(StatusCodes.Status422UnprocessableEntity, "duplicate_document", "Documento ripetuto nella selezione");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ProjectLock.AcquireAsync(db, projectId, ct);

        var selection = await db.ProjectSourceSelections.SingleOrDefaultAsync(s => s.ProjectId == projectId, ct);
        var current = selection?.Revision ?? 0;
        if (current != expectedRevision)
        {
            return new SelectionUpdate.Conflict(current);
        }

        var documentIds = (await db.ProjectDocuments.AsNoTracking().Where(d => d.ProjectId == projectId).Select(d => d.Id).ToListAsync(ct)).ToHashSet();
        if (inputs.Any(i => !documentIds.Contains(i.DocumentId)))
        {
            return new SelectionUpdate.Invalid(StatusCodes.Status404NotFound, "document_not_found", "Documento non trovato nel progetto");
        }

        // Una sola query per tutte le versioni fissate (mai una per item sotto lock).
        var pinnedIds = inputs.Where(i => i.Mode == SourceMode.Pinned && i.VersionId is not null).Select(i => i.VersionId!.Value).Distinct().ToList();
        var owners = pinnedIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await db.DocumentVersions.AsNoTracking().Where(v => v.ProjectId == projectId && pinnedIds.Contains(v.Id))
                .Select(v => new { v.Id, v.DocumentId }).ToDictionaryAsync(v => v.Id, v => v.DocumentId, ct);

        foreach (var input in inputs)
        {
            if (input.Mode == SourceMode.LatestReady && input.VersionId is not null)
            {
                return new SelectionUpdate.Invalid(StatusCodes.Status422UnprocessableEntity, "version_not_allowed", "versionId è ammesso solo in modalità Pinned");
            }

            if (input.Mode == SourceMode.Pinned)
            {
                if (input.VersionId is not { } versionId)
                {
                    return new SelectionUpdate.Invalid(StatusCodes.Status422UnprocessableEntity, "version_required", "La modalità Pinned richiede versionId");
                }

                if (!owners.TryGetValue(versionId, out var owner))
                {
                    return new SelectionUpdate.Invalid(StatusCodes.Status404NotFound, "version_not_found", "Versione non trovata nel progetto");
                }

                if (owner != input.DocumentId)
                {
                    return new SelectionUpdate.Invalid(StatusCodes.Status422UnprocessableEntity, "version_of_other_document", "La versione appartiene a un altro documento");
                }
            }
        }

        var now = await ProjectLock.NowAsync(db, ct);
        if (selection is null)
        {
            selection = new ProjectSourceSelection { ProjectId = projectId, Revision = 1, UpdatedBy = author, UpdatedAt = now };
            db.ProjectSourceSelections.Add(selection);
        }
        else
        {
            selection.Revision++;
            selection.UpdatedBy = author;
            selection.UpdatedAt = now;
        }

        // Delete prima di inserire: l'indice univoco (progetto, documento) non ammette sovrapposizioni nello stesso batch.
        await db.ProjectSourceSelectionItems.Where(i => i.ProjectId == projectId).ExecuteDeleteAsync(ct);
        foreach (var input in inputs)
        {
            db.ProjectSourceSelectionItems.Add(new ProjectSourceSelectionItem
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                DocumentId = input.DocumentId,
                Included = input.Included,
                Mode = input.Mode,
                PinnedVersionId = input.Mode == SourceMode.Pinned ? input.VersionId : null,
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new SelectionUpdate.Applied(await ViewAsync(projectId, ct));
    }

    /// <summary>
    /// Fissa la selezione alla richiesta (chat o Analizza): un SourceSnapshot immutabile con una riga per documento. Chiamare sotto il
    /// lock di progetto e nella transazione del comando. Solo Ready porta un riferimento di estrazione.
    /// </summary>
    public SourceSnapshot AddSnapshot(Guid projectId, SourceEvaluation evaluation, DateTimeOffset now)
    {
        var snapshotId = Guid.NewGuid();
        var rows = evaluation.Entries.Select(e => new SourceSnapshotItem
        {
            Id = Guid.NewGuid(),
            SnapshotId = snapshotId,
            ProjectId = projectId,
            DocumentId = e.Document.Id,
            Included = e.Included,
            Availability = e.Availability,
            DocumentVersionId = e.Availability == SourceAvailability.Ready ? e.Resolved!.Id : null,
            PreparationId = e.Availability == SourceAvailability.Ready ? e.ReadyPreparation!.Id : null,
            EvidenceVersionId = e.Availability == SourceAvailability.Ready ? e.ReadyPreparation!.CurrentEvidenceVersionId : null,
        }).ToList();

        var canonical = new StringBuilder().Append("snapshot-v1;").Append(evaluation.Revision);
        foreach (var row in rows.OrderBy(r => r.DocumentId))
        {
            canonical.Append(';').Append(row.DocumentId.ToString("N")).Append('|').Append(row.Included).Append('|').Append(row.Availability)
                .Append('|').Append(row.DocumentVersionId?.ToString("N")).Append('|').Append(row.PreparationId?.ToString("N")).Append('|').Append(row.EvidenceVersionId?.ToString("N"));
        }

        var snapshot = new SourceSnapshot
        {
            Id = snapshotId,
            ProjectId = projectId,
            SelectionRevision = evaluation.Revision,
            Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))),
            CreatedAt = now,
        };
        db.SourceSnapshots.Add(snapshot);
        db.SourceSnapshotItems.AddRange(rows);
        return snapshot;
    }
}
