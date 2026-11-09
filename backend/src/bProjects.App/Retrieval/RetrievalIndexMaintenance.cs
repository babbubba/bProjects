using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Retrieval;

/// <summary>Un passaggio indicizzato: identità e hash del testo del blocco al momento dell'indicizzazione (SC-006: l'insieme prima/dopo la ricostruzione è identico).</summary>
public sealed record IndexedPassage(Guid BlockId, string TextHash);

public sealed record RetrievalRebuildSummary(int Preparations, int Ready, int Failed, int EntriesRemoved);

/// <summary>
/// Manutenzione dell'indice derivato (003 T011, FR-007, SC-006). L'indice è una proiezione dei soli <c>DocumentBlock</c>: cancellarlo non perde nulla e la ricostruzione
/// riproduce lo stesso insieme di passaggi (stessi <c>BlockId</c> + <c>TextHash</c>). Un cambio di <c>IndexConfigHash</c> produce nuove voci (la chiave comprende la
/// configurazione) e le voci di altre configurazioni si eliminano con <see cref="PurgeOtherConfigurationsAsync"/>: non servono ai run storici, che usano il registro congelato.
/// Operazioni esplicite (comando <c>rebuild-retrieval-index</c>), mai all'avvio del server.
/// </summary>
public sealed class RetrievalIndexMaintenance(AppDbContext db, RetrievalIndexBuilder builder)
{
    /// <summary>Cancella l'intero indice derivato (voci e stati). Ritorna le voci eliminate.</summary>
    public async Task<int> ClearAsync(CancellationToken ct = default)
    {
        var removed = await db.RetrievalIndexEntries.ExecuteDeleteAsync(ct);
        await db.RetrievalIndexStates.ExecuteDeleteAsync(ct);
        return removed;
    }

    /// <summary>Elimina voci e stati di ogni configurazione diversa da <paramref name="keepConfigHash"/> (configurazione cambiata).</summary>
    public async Task<int> PurgeOtherConfigurationsAsync(string keepConfigHash, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keepConfigHash);
        var removed = await db.RetrievalIndexEntries.Where(e => e.IndexConfigHash != keepConfigHash).ExecuteDeleteAsync(ct);
        await db.RetrievalIndexStates.Where(s => s.IndexConfigHash != keepConfigHash).ExecuteDeleteAsync(ct);
        return removed;
    }

    /// <summary>
    /// Ricostruisce l'indice per le preparazioni correnti <c>Ready</c> (quelle puntate da <c>EvidenceDocumentState</c>). Con <paramref name="clear"/> parte da un indice
    /// vuoto. Un errore di servizio su una preparazione la lascia <c>Failed</c> con causa tipizzata e prosegue con le altre.
    /// </summary>
    public async Task<RetrievalRebuildSummary> RebuildAsync(bool clear, CancellationToken ct = default)
    {
        var removed = clear ? await ClearAsync(ct) : 0;
        var preparations = await (from s in db.EvidenceDocumentStates.AsNoTracking()
                                  join p in db.DocumentPreparations.AsNoTracking() on s.CurrentPreparationId equals p.Id
                                  where p.Status == PreparationStatus.Ready
                                  orderby p.RequestedAt, p.Id
                                  select p.Id).ToListAsync(ct);
        int ready = 0, failed = 0;
        foreach (var id in preparations)
        {
            var result = await builder.BuildAsync(id, ct);
            if (result.Status == RetrievalIndexStatus.Ready)
            {
                ready++;
            }
            else
            {
                failed++;
            }
        }

        return new(preparations.Count, ready, failed, removed);
    }

    /// <summary>Insieme dei passaggi indicizzati per una configurazione, ordinato (per confronti prima/dopo).</summary>
    public async Task<IReadOnlyList<IndexedPassage>> IndexedPassagesAsync(string configHash, CancellationToken ct = default) =>
        [.. (await db.RetrievalIndexEntries.AsNoTracking().Where(e => e.IndexConfigHash == configHash).Select(e => new { e.BlockId, e.TextHash }).ToListAsync(ct))
            .OrderBy(e => e.BlockId).Select(e => new IndexedPassage(e.BlockId, e.TextHash))];
}
