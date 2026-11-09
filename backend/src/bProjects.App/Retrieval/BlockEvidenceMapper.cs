using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Retrieval;

/// <summary>Evidenza del ledger congelato vista dal registro: origine, tipo e blocchi citati dalle sue fonti documentali (vuoti per le evidenze <c>Message</c>).</summary>
public sealed record RegistryEvidence(Guid EvidenceId, EvidenceOriginKind Origin, EvidenceKind Kind, IReadOnlyList<Guid> BlockIds);

/// <summary>Provenienza di un blocco per il registro (FR-013): documento, versione, sezione, intervallo UTF-16 <c>[start,end)</c> sul <c>CanonicalText</c> e hash del testo originale.</summary>
public sealed record BlockProvenance(
    Guid BlockId, Guid DocumentId, string DocumentName, int DocumentVersion, string Section, int RangeStart, int RangeEnd, string TextHash);

/// <summary>
/// Mappatura blocco → evidenze (003 T015, R-3, FR-004): il recupero ordina BLOCCHI, lo stadio Gap consuma EVIDENZE. Un blocco risolve nelle evidenze documentali (non Gap)
/// del ledger congelato che hanno una fonte su quel <c>BlockId</c>, in ordine di catalogo. Nessuna seconda pipeline di estrazione e nessun secondo ledger: si
/// leggono le evidenze della 002 così come sono.
/// </summary>
public static class BlockEvidenceMapper
{
    /// <summary>Adattatore dalle evidenze dello snapshot del run (già in ordine di catalogo).</summary>
    public static RegistryEvidence From(DocumentAnalysisEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new(evidence.EvidenceId, evidence.Origin, evidence.Kind, BlocksOf(evidence.Sources));
    }

    /// <summary>Blocco → evidenze (ordine di catalogo = ordine della lista in ingresso). Le evidenze Gap non sono candidati e le Message non hanno blocchi.</summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> BlockToEvidence(IEnumerable<RegistryEvidence> evidences)
    {
        ArgumentNullException.ThrowIfNull(evidences);
        var map = new Dictionary<Guid, List<Guid>>();
        foreach (var evidence in evidences.Where(e => e.Origin == EvidenceOriginKind.Document && e.Kind != EvidenceKind.Gap))
        {
            foreach (var block in evidence.BlockIds.Distinct())
            {
                if (!map.TryGetValue(block, out var list))
                {
                    map[block] = list = [];
                }

                if (!list.Contains(evidence.EvidenceId))
                {
                    list.Add(evidence.EvidenceId);
                }
            }
        }

        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Guid>)kv.Value);
    }

    /// <summary>
    /// Evidenze documentali delle versioni Evidence date (quelle fissate dallo snapshot), in ordine: prima l'ordine delle versioni passato, poi l'ordinale. Sola lettura.
    /// </summary>
    public static async Task<IReadOnlyList<RegistryEvidence>> LoadDocumentEvidenceAsync(AppDbContext db, IReadOnlyList<Guid> evidenceVersionIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(evidenceVersionIds);
        var rows = await db.Evidences.AsNoTracking()
            .Where(e => evidenceVersionIds.Contains(e.VersionId) && e.OriginKind == EvidenceOriginKind.Document).ToListAsync(ct);
        var order = evidenceVersionIds.Select((id, index) => (id, index)).ToDictionary(t => t.id, t => t.index);
        return [.. rows
            .OrderBy(r => order[r.VersionId]).ThenBy(r => r.Ordinal)
            .Select(r => new RegistryEvidence(r.Id, r.OriginKind, r.Kind, BlocksOf(DocumentEvidenceCodec.DecodeContent(r.Content).Sources)))];
    }

    /// <summary>Provenienza dei blocchi dati (documento, versione, sezione = percorso dei titoli unito da « &gt; », vuota se assente: mai inventata).</summary>
    public static async Task<IReadOnlyDictionary<Guid, BlockProvenance>> LoadProvenanceAsync(AppDbContext db, IReadOnlyCollection<Guid> blockIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(blockIds);
        var rows = await (from b in db.DocumentBlocks.AsNoTracking()
                          join a in db.ParsedArtifacts.AsNoTracking() on b.ArtifactId equals a.Id
                          join p in db.DocumentPreparations.AsNoTracking() on a.PreparationId equals p.Id
                          join v in db.DocumentVersions.AsNoTracking() on p.DocumentVersionId equals v.Id
                          join d in db.ProjectDocuments.AsNoTracking() on v.DocumentId equals d.Id
                          where blockIds.Contains(b.Id)
                          select new { b.Id, b.CanonicalText, b.TextHash, b.HeadingPathJson, DocumentId = d.Id, d.DisplayName, v.Version }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r =>
        {
            var path = JsonSerializer.Deserialize<string[]>(r.HeadingPathJson) ?? [];
            return new BlockProvenance(r.Id, r.DocumentId, r.DisplayName, r.Version, string.Join(" > ", path), 0, r.CanonicalText.Length, r.TextHash);
        });
    }

    private static List<Guid> BlocksOf(IReadOnlyList<PersistedSource> sources) =>
        [.. sources.Where(s => s.Kind == EvidenceSourceKind.DocumentPassage && s.BlockId is not null).Select(s => s.BlockId!.Value).Distinct()];
}
