using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Retrieval;

/// <summary>
/// Ordine finale dei candidati di un Gap dopo il riordino (003 T023, FR-009, FR-010, A-3). Funzione pura e deterministica. I primi <c>N_rerank</c> blocchi della fusione RRF
/// sono riordinati dai punteggi del servizio; i punteggi servono SOLO a ordinare (possono essere negativi, non sono probabilità, non sono mai una soglia né sono conservati).
/// I blocchi oltre <c>N_rerank</c> seguono nell'ordine RRF. A pari punteggio vale l'ordine RRF. Un insieme di punteggi incompleto, duplicato o fuori indice è una risposta
/// malformata, non un ordine parziale.
/// </summary>
public static class RerankOrder
{
    /// <summary>Blocchi da inviare al riordino: i primi <paramref name="rerank"/> della fusione, in ordine RRF.</summary>
    public static IReadOnlyList<FusedBlock> Window(IReadOnlyList<FusedBlock> fused, int rerank)
    {
        ArgumentNullException.ThrowIfNull(fused);
        ArgumentOutOfRangeException.ThrowIfLessThan(rerank, 1);
        return [.. fused.OrderBy(f => f.RrfRank).Take(rerank)];
    }

    /// <summary>Ordine finale dei <c>BlockId</c> dato l'esito del riordino della finestra (<see cref="Window"/>); gli <see cref="RerankScore.Index"/> sono posizioni nella finestra.</summary>
    public static IReadOnlyList<Guid> Apply(IReadOnlyList<FusedBlock> fused, int rerank, IReadOnlyList<RerankScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        var window = Window(fused, rerank);
        if (scores.Count != window.Count || scores.Select(s => s.Index).Distinct().Count() != window.Count || scores.Any(s => s.Index < 0 || s.Index >= window.Count || !double.IsFinite(s.Score)))
        {
            throw new RetrievalException(
                RetrievalException.RerankerMalformedResponse,
                $"Risposta del servizio di riordino non coerente con i {window.Count} candidati inviati.");
        }

        var reranked = scores.OrderByDescending(s => s.Score).ThenBy(s => s.Index).Select(s => window[s.Index].BlockId);
        var rest = fused.OrderBy(f => f.RrfRank).Skip(window.Count).Select(f => f.BlockId);
        return [.. reranked, .. rest];
    }
}

/// <summary>
/// Riordino dei candidati di un Gap con il servizio interno (003 T023). Verifica la readiness del riordino, invia i testi dei primi <c>N_rerank</c> blocchi (testo indicizzato
/// <c>index-text-1</c>, mai troncato) e restituisce l'ordine finale. Servizio non pronto o risposta non valida ⇒ <see cref="RetrievalException"/> tipizzata
/// (<c>reranker_unavailable</c> / <c>reranker_malformed_response</c>): la modalità degradata è decisa dal chiamante (T024), mai un servizio alternativo né un ordine inventato.
/// </summary>
public sealed class CandidateReranker(AppDbContext db, RerankClient rerank, RetrievalModelReadiness readiness)
{
    /// <summary>Ordine finale dei <c>BlockId</c> di <paramref name="fused"/> (tutti, ciascuno una volta). Nessuna fusione ⇒ lista vuota e nessuna richiesta.</summary>
    public async Task<IReadOnlyList<Guid>> OrderAsync(string query, IReadOnlyList<FusedBlock> fused, RetrievalPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(fused);
        ArgumentNullException.ThrowIfNull(policy);
        if (fused.Count == 0)
        {
            return [];
        }

        await readiness.EnsureRerankerReadyAsync(ct);

        var window = RerankOrder.Window(fused, policy.Rerank);
        var ids = window.Select(w => w.BlockId).ToList();
        var texts = await db.DocumentBlocks.AsNoTracking().Where(b => ids.Contains(b.Id)).Select(b => new { b.Id, b.CanonicalText }).ToDictionaryAsync(b => b.Id, b => b.CanonicalText, ct);
        var documents = ids.Select(id => IndexText.Of(texts.TryGetValue(id, out var text) ? text : throw new InvalidOperationException($"Blocco {id} inesistente: indice incoerente."))).ToList();

        var scores = await rerank.RerankAsync(IndexText.Of(query), documents, ct);
        return RerankOrder.Apply(fused, policy.Rerank, scores);
    }
}
