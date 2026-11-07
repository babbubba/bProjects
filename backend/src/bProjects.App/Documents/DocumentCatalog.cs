using System.Buffers.Text;
using System.Globalization;
using System.Text;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Documents;

/// <summary>Sintesi dei problemi di una versione: codice e testo fisso lato server. Il messaggio libero della preparazione non esce mai dall'API.</summary>
public sealed record IssuesSummary(string Code, string? Message)
{
    public const string FixedMessage = "La preparazione del documento non è riuscita";
}

/// <summary>DocumentVersionView del contratto HTTP (§Documenti). Nessuna chiave di storage né percorso: l'originale si scarica solo dalla route autenticata.</summary>
public sealed record DocumentVersionView(
    Guid Id,
    Guid DocumentId,
    Guid ProjectId,
    int Version,
    string OriginalName,
    string MediaType,
    long ByteLength,
    string Sha256,
    Guid UploadedBy,
    DateTimeOffset UploadedAt,
    string ProcessingStatus,
    string ApplicationStatus,
    Guid? CurrentPreparationId,
    Guid? CurrentEvidenceVersionId,
    long BundleRevision,
    IReadOnlyList<Guid> CandidatePreparations,
    Guid? LatestJobId,
    bool Included,
    string SelectionMode,
    bool IsApplicable,
    Guid? PreviousReadyVersionId,
    IssuesSummary? IssuesSummary,
    IReadOnlyList<string> AllowedActions);

public sealed record DocumentVersionSummary(Guid Id, int Version, string ProcessingStatus, DateTimeOffset UploadedAt);

public sealed record DocumentListItem(
    Guid DocumentId,
    string DisplayName,
    DateTimeOffset CreatedAt,
    int VersionCount,
    DocumentVersionSummary? LatestVersion,
    Guid? ApplicableVersionId,
    bool Included,
    string SelectionMode,
    string Availability,
    string? Reason);

public sealed record DocumentListPage(IReadOnlyList<DocumentListItem> Items, string? NextCursor);

public sealed record SourcePreferenceView(bool Included, string Mode, Guid? VersionId);

public sealed record DocumentDetailView(
    Guid DocumentId,
    Guid ProjectId,
    string DisplayName,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DocumentVersionView> Versions,
    SourcePreferenceView SourcePreference,
    string Availability,
    Guid? ApplicableVersionId);

/// <summary>
/// Letture dell'archivio (T014): lista a cursore, dettaglio e versioni, con lo stato reale di preparazione e la preferenza di fonte.
/// Sola lettura: nessuna chiamata avvia job. Ogni identificativo è cercato sotto il progetto del percorso (altro progetto → assente).
/// </summary>
public sealed class DocumentCatalog(AppDbContext db, SourceSelectionService selection)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    public async Task<DocumentListPage> ListAsync(Guid projectId, (DateTimeOffset At, Guid Id)? after, int limit, CancellationToken ct)
    {
        // Ordine stabile (creazione, id) e cursore a confronto di riga: nessun duplicato né salto fra pagine, anche con documenti creati nel frattempo.
        var page = after is { } c
            ? await db.ProjectDocuments.FromSql($"""
                SELECT * FROM "ProjectDocuments" WHERE "ProjectId" = {projectId} AND ("CreatedAt", "Id") > ({c.At}, {c.Id})
                ORDER BY "CreatedAt", "Id" LIMIT {limit + 1}
                """).AsNoTracking().ToListAsync(ct)
            : await db.ProjectDocuments.FromSql($"""
                SELECT * FROM "ProjectDocuments" WHERE "ProjectId" = {projectId} ORDER BY "CreatedAt", "Id" LIMIT {limit + 1}
                """).AsNoTracking().ToListAsync(ct);
        var hasMore = page.Count > limit;
        var ids = page.Take(limit).Select(d => d.Id).ToList();

        var evaluation = await selection.EvaluateAsync(projectId, ct);
        var statuses = await selection.PreparationStatusesAsync(projectId, ct);
        var counts = await db.DocumentVersions.AsNoTracking().Where(v => ids.Contains(v.DocumentId))
            .GroupBy(v => v.DocumentId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var entries = evaluation.Entries.Where(e => ids.Contains(e.Document.Id)).ToDictionary(e => e.Document.Id);

        var items = ids.Select(id =>
        {
            var e = entries[id];
            var latest = e.Latest is null ? null : new DocumentVersionSummary(e.Latest.Id, e.Latest.Version, StatusOf(statuses, e.Latest.Id), e.Latest.UploadedAt);
            return new DocumentListItem(
                id, e.Document.DisplayName, e.Document.CreatedAt, counts.GetValueOrDefault(id), latest,
                e.Availability == SourceAvailability.Ready ? e.Resolved?.Id : null, e.Included, e.Mode.ToString(), e.Availability.ToString(), ReasonOf(e, statuses));
        }).ToList();
        var next = hasMore ? EncodeCursor(page[limit - 1].CreatedAt, page[limit - 1].Id) : null;
        return new(items, next);
    }

    public async Task<DocumentDetailView?> GetAsync(Guid projectId, Guid documentId, CancellationToken ct)
    {
        var context = await LoadAsync(projectId, documentId, ct);
        return context is null ? null : Detail(context);
    }

    /// <summary>Versione per numero o per id (altro documento o progetto → null).</summary>
    public async Task<DocumentVersionView?> GetVersionAsync(Guid projectId, Guid documentId, string version, CancellationToken ct)
    {
        var context = await LoadAsync(projectId, documentId, ct);
        if (context is null)
        {
            return null;
        }

        var found = int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? context.Versions.SingleOrDefault(v => v.Version == number)
            : Guid.TryParse(version, out var id) ? context.Versions.SingleOrDefault(v => v.Id == id) : null;
        return found is null ? null : ViewOf(context, found);
    }

    public async Task<DocumentVersion?> FindVersionAsync(Guid projectId, Guid documentId, string version, CancellationToken ct)
    {
        var query = db.DocumentVersions.AsNoTracking().Where(v => v.ProjectId == projectId && v.DocumentId == documentId);
        if (int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return await query.SingleOrDefaultAsync(v => v.Version == number, ct);
        }

        return Guid.TryParse(version, out var id) ? await query.SingleOrDefaultAsync(v => v.Id == id, ct) : null;
    }

    private sealed record Context(
        SourceEntry Entry, IReadOnlyList<DocumentVersion> Versions, ILookup<Guid, DocumentPreparation> Preparations, IReadOnlyDictionary<Guid, EvidenceDocumentState> States,
        IReadOnlySet<Guid> FormerlyCurrent);

    private async Task<Context?> LoadAsync(Guid projectId, Guid documentId, CancellationToken ct)
    {
        var evaluation = await selection.EvaluateAsync(projectId, ct);
        var entry = evaluation.Entries.SingleOrDefault(e => e.Document.Id == documentId);
        if (entry is null)
        {
            return null;
        }

        var versions = await db.DocumentVersions.AsNoTracking().Where(v => v.DocumentId == documentId && v.ProjectId == projectId).OrderBy(v => v.Version).ToListAsync(ct);
        var versionIds = versions.Select(v => v.Id).ToList();
        var preparations = (await db.DocumentPreparations.AsNoTracking().Where(p => versionIds.Contains(p.DocumentVersionId)).ToListAsync(ct))
            .OrderByDescending(p => p.Generation).ToLookup(p => p.DocumentVersionId);
        var states = await db.EvidenceDocumentStates.AsNoTracking().Where(s => versionIds.Contains(s.DocumentVersionId)).ToDictionaryAsync(s => s.DocumentVersionId, ct);
        // Superseded = è stata corrente e un'altra promozione l'ha sostituita: resta nello storico e promovibile, ma non è più una candidata.
        var formerly = (await db.DocumentPromotions.AsNoTracking().Where(p => versionIds.Contains(p.DocumentVersionId)).Select(p => p.ToPreparationId).ToListAsync(ct)).ToHashSet();
        return new(entry, versions, preparations, states, formerly);
    }

    private static DocumentDetailView Detail(Context c) => new(
        c.Entry.Document.Id,
        c.Entry.Document.ProjectId,
        c.Entry.Document.DisplayName,
        c.Entry.Document.CreatedBy,
        c.Entry.Document.CreatedAt,
        c.Versions.Select(v => ViewOf(c, v)).ToList(),
        new(c.Entry.Included, c.Entry.Mode.ToString(), c.Entry.PinnedVersionId),
        c.Entry.Availability.ToString(),
        c.Entry.Availability == SourceAvailability.Ready ? c.Entry.Resolved?.Id : null);

    private static DocumentVersionView ViewOf(Context c, DocumentVersion v)
    {
        var prepared = c.Preparations[v.Id].ToList();
        var latest = prepared.FirstOrDefault();
        // Bundle corrente = i puntatori dello stato della versione file (mai la massima generazione Ready); le Ready mai state correnti sono candidate, quelle sostituite sono Superseded.
        var state = c.States.GetValueOrDefault(v.Id);
        var candidates = prepared.Where(p => p.Status == PreparationStatus.Ready && p.CurrentEvidenceVersionId is not null && p.Id != state?.CurrentPreparationId
                && !c.FormerlyCurrent.Contains(p.Id))
            .OrderBy(p => p.Generation).Select(p => p.Id).ToList();
        var applicable = c.Entry.Availability == SourceAvailability.Ready && c.Entry.Resolved?.Id == v.Id;
        return new(
            v.Id, v.DocumentId, v.ProjectId, v.Version, v.OriginalName, v.MediaType, v.ByteLength, v.Sha256, v.UploadedBy, v.UploadedAt,
            latest is null ? "NotPrepared" : latest.Status.ToString(),
            applicable ? "Applicable" : c.Entry.Included ? "NotApplicable" : "Excluded",
            state?.CurrentPreparationId,
            state?.CurrentEvidenceVersionId,
            BundleRevision: state?.Revision ?? 0,
            candidates,
            latest?.ParseJobId,
            c.Entry.Included,
            c.Entry.Mode.ToString(),
            applicable,
            c.Entry.Document.LatestReadyVersionId is { } previous && previous != v.Id ? previous : null,
            latest?.ErrorCode is { } code ? new IssuesSummary(code, IssuesSummary.FixedMessage) : null,
            ["download"]);
    }

    private static string StatusOf(IReadOnlyDictionary<Guid, PreparationStatus> statuses, Guid versionId) =>
        statuses.TryGetValue(versionId, out var status) ? status.ToString() : "NotPrepared";

    private static string? ReasonOf(SourceEntry e, IReadOnlyDictionary<Guid, PreparationStatus> statuses) => e.Availability switch
    {
        SourceAvailability.Ready => null,
        SourceAvailability.Excluded => "excluded_by_pm",
        _ => e.Subject is { } subject && statuses.TryGetValue(subject.Id, out var status) ? $"preparation_{status.ToString().ToLowerInvariant()}" : "no_preparation",
    };

    // ---- cursore opaco -------------------------------------------------------------------------------------------------------

    public static string EncodeCursor(DateTimeOffset at, Guid id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{at.UtcTicks}:{id:N}"));

    public static bool TryDecodeCursor(string? cursor, out (DateTimeOffset At, Guid Id) value)
    {
        value = default;
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            var parts = text.Split(':');
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks > 0 && ticks <= DateTime.MaxValue.Ticks && Guid.TryParseExact(parts[1], "N", out var id))
            {
                value = (new DateTimeOffset(ticks, TimeSpan.Zero), id);
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // cursore non valido: il chiamante risponde 400
        }

        return false;
    }
}
