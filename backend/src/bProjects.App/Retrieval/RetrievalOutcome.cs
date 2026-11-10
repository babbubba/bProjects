using bProjects.App.Evidence;
using bProjects.App.Persistence;

namespace bProjects.App.Retrieval;

/// <summary>Un Gap da interrogare: identificativo canonico dell'evidenza Gap nel run e testo canonico dell'interrogazione (mai un UUID o un alias verso il modello).</summary>
public sealed record RetrievalQuery(Guid GapEvidenceId, string Query);

/// <summary>
/// Esito del recupero per un run (003 T024, D-3): sempre un registro tipizzato, mai una lista parziale. <see cref="CandidateRegistry.OutcomePackage"/> = pacchetto consegnabile
/// allo stadio; <see cref="CandidateRegistry.OutcomeDiagnostic"/> = riordino non disponibile (ordine RRF consultabile, nessun pacchetto); <see cref="CandidateRegistry.OutcomeFailed"/>
/// = embedding o indice non utilizzabili (nessun candidato). In ogni caso diverso da Package lo stadio NON viene invocato: zero invii LLM, nessun aumento di budget, nessun cambio di modello.
/// </summary>
public sealed record RetrievalOutcome(RetrievalRegistry Registry)
{
    /// <summary>Solo <c>Package</c> autorizza l'invio dello stadio.</summary>
    public bool CanInvokeStage => Registry.Outcome == CandidateRegistry.OutcomePackage;

    /// <summary>Cause tipizzate conservate nel registro (FR-020), ordinate.</summary>
    public IReadOnlyList<string> Causes => Registry.Causes;
}

/// <summary>
/// Esecuzione del recupero per i Gap di un run (003 T024, FR-020, FR-035, SC-011): ricerca ibrida per Gap, riordino, registro. Modalità degradata (D-3):
/// <list type="number">
/// <item>embedding non pronto o indice non <c>Ready</c> ⇒ <c>Failed</c> con le cause (<c>embedding_unavailable</c>/<c>embedding_model_mismatch</c>/…, <c>retrieval_index_not_ready</c>); nessuna ricerca, nessun
/// candidato, nessun ripiego sul solo lessicale. Se l'embedding diventa indisponibile durante la ricerca di un Gap, il risultato dei Gap precedenti è scartato (nessuna lista parziale);</item>
/// <item>riordino non pronto o con risposta non valida (embedding ok) ⇒ <c>Diagnostic</c>: registro con ordine RRF per tutti i Gap, <c>degraded = true</c>, <c>retrievalComplete = false</c>,
/// causa tipizzata, nessun pacchetto; non si cambia modello e non si aumenta il budget;</item>
/// <item>tutte le cause incontrate restano nel registro. Con embedding e riordino entrambi non disponibili prevale l'embedding (<c>Failed</c>): il riordino non è nemmeno interrogato.</item>
/// </list>
/// L'annullamento del chiamante propaga <see cref="OperationCanceledException"/>; errori non tipizzati (guasti dell'applicazione) non sono cause di servizio e propagano.
/// </summary>
public sealed class RetrievalPipeline(AppDbContext db, HybridSearch search, CandidateReranker reranker, RetrievalModelReadiness readiness, RetrievalTimingLog? timing = null)
{
    public Task<RetrievalOutcome> ExecuteAsync(
        RetrievalScope scope, string snapshotHash, IReadOnlyList<RetrievalQuery> gaps, IReadOnlyList<RegistryEvidence> evidences,
        RetrievalPolicy policy, RegistryBudget? budget = null, CancellationToken ct = default) =>
        Timed(RetrievalTimingLog.PhaseTotal, null, () => ExecuteCoreAsync(scope, snapshotHash, gaps, evidences, policy, budget, ct));

    /// <summary>Misura opzionale (solo se nel contenitore c'è un <see cref="RetrievalTimingLog"/>): senza, esegue il lavoro così com'è. Nessun effetto sul risultato.</summary>
    private Task<T> Timed<T>(string phase, Guid? gap, Func<Task<T>> work) => timing is null ? work() : timing.MeasureAsync(phase, gap, work);

    private async Task<RetrievalOutcome> ExecuteCoreAsync(
        RetrievalScope scope, string snapshotHash, IReadOnlyList<RetrievalQuery> gaps, IReadOnlyList<RegistryEvidence> evidences,
        RetrievalPolicy policy, RegistryBudget? budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(gaps);
        ArgumentNullException.ThrowIfNull(evidences);
        ArgumentNullException.ThrowIfNull(policy);

        // 1. Servizi e indice: nessuna ricerca se l'embedding o l'indice non sono pronti (tutte le cause sono registrate).
        var embeddingSnapshot = await Timed(RetrievalTimingLog.PhaseReadiness, null, () => readiness.GetEmbeddingSnapshotForUseAsync(ct));
        var causes = new List<string>();
        if (!embeddingSnapshot.IsReady)
        {
            causes.Add(RetrievalModelReadiness.ToException(embeddingSnapshot).Code);
        }

        if (!await Timed(RetrievalTimingLog.PhaseIndexReady, null, () => search.IndexReadyAsync(scope, ct)))
        {
            causes.Add(RetrievalException.IndexNotReady);
        }

        if (causes.Count > 0)
        {
            return Failed(scope, snapshotHash, gaps, evidences, policy, causes, ReadinessOf(embeddingSnapshot, null));
        }

        // 2. Ricerca ibrida per Gap: un errore tipizzato dell'embedding scarta tutto (nessun risultato parziale).
        var searched = new List<(RetrievalQuery Gap, IReadOnlyList<FusedBlock> Fused)>();
        try
        {
            foreach (var gap in gaps)
            {
                var result = await Timed(RetrievalTimingLog.PhaseSearch, gap.GapEvidenceId, () => search.SearchAsync(scope, gap.Query, policy, ct));
                searched.Add((gap, result.Fused));
            }
        }
        catch (RetrievalException failure)
        {
            return Failed(scope, snapshotHash, gaps, evidences, policy, [failure.Code], ReadinessOf(embeddingSnapshot, null));
        }

        // 3. Riordino: pronto per tutti i Gap, oppure ordine RRF diagnostico per tutti (mai un ordine misto).
        var finalOrders = new List<IReadOnlyList<Guid>?>();
        RetrievalModelSnapshot? rerankerSnapshot = null;
        try
        {
            rerankerSnapshot = await Timed(RetrievalTimingLog.PhaseRerankerReady, null, () => readiness.EnsureRerankerReadyAsync(ct));
            foreach (var (gap, fused) in searched)
            {
                finalOrders.Add(await Timed(RetrievalTimingLog.PhaseRerank, gap.GapEvidenceId, () => reranker.OrderAsync(gap.Query, fused, policy, ct)));
            }
        }
        catch (RetrievalException failure) when (failure.Code is RetrievalException.RerankerUnavailable or RetrievalException.RerankerMalformedResponse)
        {
            causes.Add(failure.Code);
            finalOrders = [.. searched.Select(_ => (IReadOnlyList<Guid>?)null)];
            rerankerSnapshot ??= await readiness.GetRerankerSnapshotAsync(ct);
        }

        var provenance = await Timed(RetrievalTimingLog.PhaseProvenance, null, () => BlockEvidenceMapper.LoadProvenanceAsync(db, [.. searched.SelectMany(s => s.Fused).Select(f => f.BlockId).Distinct()], ct));
        // Il registro (unione fra Gap, pacchetto per rango fino al budget) è un lavoro CONDIVISO dei Gap: si misura una volta sola, mai per Gap.
        RetrievalRegistry Build() => CandidateRegistry.Build(
            snapshotHash, scope.IndexConfigHash, policy.Version, policy.Hash(),
            [.. searched.Select((s, index) => new RegistryGapInput(s.Gap.GapEvidenceId, s.Gap.Query, s.Fused, finalOrders[index]))],
            provenance, evidences, causes, budget, ReadinessOf(embeddingSnapshot, rerankerSnapshot), rerankWindow: policy.Rerank);
        var registry = timing is null ? Build() : timing.Measure(RetrievalTimingLog.PhaseRegistry, null, Build);
        return new(registry);
    }

    private static RetrievalOutcome Failed(
        RetrievalScope scope, string snapshotHash, IReadOnlyList<RetrievalQuery> gaps, IReadOnlyList<RegistryEvidence> evidences, RetrievalPolicy policy,
        IReadOnlyList<string> causes, RegistryReadiness readiness) =>
        new(CandidateRegistry.Build(
            snapshotHash, scope.IndexConfigHash, policy.Version, policy.Hash(),
            [.. gaps.Select(g => new RegistryGapInput(g.GapEvidenceId, g.Query, []))],
            new Dictionary<Guid, BlockProvenance>(), evidences, causes, readiness: readiness));

    /// <summary>Modello come osservato dalla readiness (nessun valore dedotto dalla configurazione); riordino assente = non interrogato.</summary>
    private static RegistryReadiness ReadinessOf(RetrievalModelSnapshot embedding, RetrievalModelSnapshot? reranker) => new(Of(embedding), reranker is null ? null : Of(reranker));

    private static RegistryModelReadiness Of(RetrievalModelSnapshot snapshot) =>
        new(snapshot.ModelId, snapshot.Artifact ?? "n/d", snapshot.Dimension, snapshot.Sha256Status.ToString());
}
