using System.Net;
using bProjects.App.Evidence;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003 T024 (DET+PG, servizi FINTI): modalità degradata D-3. Matrice {embedding ok/ko} × {riordino ok/ko} × {indice pronto/non pronto}: embedding o indice ko ⇒ Failed senza candidati e senza ricerca;
/// solo riordino ko ⇒ Diagnostic con ordine RRF, degraded, retrievalComplete = false e nessun pacchetto; tutte le cause conservate; mai un ripiego sul solo lessicale. Nessuna inferenza LLM, nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class RetrievalPipelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Unloaded = """{"object":"list","data":[{"id":"%ID%","object":"model","status":{"value":"unloaded"}}]}""";

    private static readonly string[] Texts =
    [
        "Il termine di pagamento è di trenta giorni dalla data fattura.",
        "La penale per ritardo è del cinque per cento.",
        "Il foro competente è quello di Milano.",
        "Le sedi dell'officina sono due.",
    ];

    private sealed class Scenario : IAsyncDisposable
    {
        public required RetrievalDb Database { get; init; }

        public required RetrievalRig Rig { get; init; }

        public required FakeEmbeddingServer Embedding { get; init; }

        public required FakeRerankServer Rerank { get; init; }

        public required SeededDocument Document { get; init; }

        public required IReadOnlyList<Guid> EvidenceIds { get; init; }

        public required RetrievalScope Scope { get; init; }

        public required IReadOnlyList<RegistryEvidence> Evidences { get; init; }

        public static async Task<Scenario> CreateAsync()
        {
            var database = await RetrievalDb.CreateAsync();
            var embedding = new FakeEmbeddingServer();
            var rerank = new FakeRerankServer { Scores = (_, docs) => [.. docs.Select(d => d.Contains("penale") ? 5.0 : d.Contains("foro") ? -2.0 : -7.0)] };
            var rig = new RetrievalRig(embedding, rerank);
            var project = await database.SeedProjectAsync();
            var document = await database.AddDocumentAsync(project, "Contratto", Texts);
            var (version, evidenceIds) = await database.AddEvidenceAsync(
                document, (EvidenceKind.Requirement, "Pagamento a 30 giorni", [0]), (EvidenceKind.Requirement, "Penale del 5%", [1]), (EvidenceKind.Constraint, "Foro di Milano", [2]));
            var snapshot = await database.AddSnapshotAsync(project, [document], evidenceVersions: new Dictionary<Guid, Guid> { [document.PreparationId] = version });
            await using var db = database.NewContext();
            var hash = (await rig.Builder(db).BuildAsync(document.PreparationId, Ct)).IndexConfigHash;
            var scope = await RetrievalScope.FromSnapshotAsync(db, snapshot, hash, Ct);
            var evidences = await BlockEvidenceMapper.LoadDocumentEvidenceAsync(db, [version], Ct);
            return new() { Database = database, Rig = rig, Embedding = embedding, Rerank = rerank, Document = document, EvidenceIds = evidenceIds, Scope = scope, Evidences = evidences };
        }

        /// <summary>Applica lo stato dei servizi voluto DOPO l'indicizzazione (la readiness in cache è scartata).</summary>
        public async Task ConfigureAsync(bool embeddingOk, bool rerankOk, bool indexReady)
        {
            if (!embeddingOk)
            {
                Embedding.Models = () => FakeEmbeddingServer.Json(Unloaded.Replace("%ID%", "bge-m3-Q5"));
            }

            if (!rerankOk)
            {
                Rerank.Models = () => FakeRerankServer.Json(Unloaded.Replace("%ID%", "bge-reranker-v2-m3"));
            }

            if (!indexReady)
            {
                await Database.ExecuteAsync("DELETE FROM \"RetrievalIndexStates\"");
            }

            Rig.Readiness.Invalidate();
            Rig.Readiness.InvalidateReranker();
        }

        public async Task<RetrievalOutcome> RunAsync(params string[] queries) => await RunTimedAsync(null, queries);

        public async Task<RetrievalOutcome> RunTimedAsync(RetrievalTimingLog? timing, params string[] queries)
        {
            await using var db = Database.NewContext();
            return await Rig.Pipeline(db, timing).ExecuteAsync(
                Scope, "snapshot-hash", [.. queries.Select(q => new RetrievalQuery(new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(q))), q))], Evidences, RetrievalPolicy.V1, ct: Ct);
        }

        /// <summary>Richieste di CONTENUTO di embedding (sonda esclusa) e di riordino (sonda esclusa: 2 documenti).</summary>
        public (int Embeddings, int Reranks) ContentRequests() =>
            (Embedding.EmbeddingInputs.Count(i => !(i.Length == 1 && i[0] == "probe")), Rerank.Bodies.Count(b => !b.Contains("\"probe a\"")));

        public async ValueTask DisposeAsync()
        {
            Rig.Dispose();
            await Database.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_optional_timing_log_observes_every_phase_per_gap_and_shared_without_changing_the_registry()
    {
        await using var s = await Scenario.CreateAsync();
        var queries = new[] { "penale ritardo", "foro competente" };
        var plain = (await s.RunAsync(queries)).Registry;
        var log = new RetrievalTimingLog();
        var timed = (await s.RunTimedAsync(log, queries)).Registry;

        Assert.Equal(plain.Hash(), timed.Hash());   // stessa pipeline, stesso registro: la misura è sola osservazione
        Assert.Equal(plain.PackageEvidence, timed.PackageEvidence);

        var gapIds = timed.Gaps.Select(g => g.GapEvidenceId).ToList();
        Assert.Equal(2, gapIds.Count);
        foreach (var phase in new[] { RetrievalTimingLog.PhaseSearch, RetrievalTimingLog.PhaseRerank })
        {
            Assert.Equal(gapIds.Order(), log.Entries.Where(e => e.Phase == phase).Select(e => e.GapEvidenceId!.Value).Order());   // una voce per Gap, con il suo Gap
        }

        // fasi condivise: UNA volta sola e senza Gap; il totale racchiude tutte le altre
        foreach (var phase in new[] { RetrievalTimingLog.PhaseReadiness, RetrievalTimingLog.PhaseIndexReady, RetrievalTimingLog.PhaseRerankerReady, RetrievalTimingLog.PhaseProvenance, RetrievalTimingLog.PhaseRegistry, RetrievalTimingLog.PhaseTotal })
        {
            var entry = Assert.Single(log.Entries, e => e.Phase == phase);
            Assert.Null(entry.GapEvidenceId);
            Assert.False(entry.Faulted);
        }

        var total = log.Entries.Single(e => e.Phase == RetrievalTimingLog.PhaseTotal);
        Assert.All(log.Entries, e => Assert.True(e.StartedAt >= total.StartedAt && e.EndedAt <= total.EndedAt));
        Assert.True(log.Entries.Where(e => e.Phase != RetrievalTimingLog.PhaseTotal).Sum(e => e.Milliseconds) <= total.Milliseconds + 1);   // fasi sequenziali: nessun doppio conteggio
    }

    [Fact]
    public async Task A_failed_run_records_its_faulted_phase_and_propagates_the_same_exception()
    {
        await using var s = await Scenario.CreateAsync();
        var log = new RetrievalTimingLog();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await using var db = s.Database.NewContext();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Rig.Pipeline(db, log).ExecuteAsync(s.Scope, "snapshot-hash", [new RetrievalQuery(Guid.NewGuid(), "penale")], s.Evidences, RetrievalPolicy.V1, ct: cancelled.Token));
        Assert.Contains(log.Entries, e => e.Phase == RetrievalTimingLog.PhaseTotal && e.Faulted);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public async Task The_service_matrix_gives_the_declared_outcome_causes_and_stage_permission(bool embeddingOk, bool rerankOk, bool indexReady)
    {
        await using var s = await Scenario.CreateAsync();
        await s.ConfigureAsync(embeddingOk, rerankOk, indexReady);
        var before = s.ContentRequests();

        var outcome = await s.RunAsync("penale ritardo");

        var registry = outcome.Registry;
        var after = s.ContentRequests();
        Assert.Equal(("retrieval-policy-1", RetrievalPolicy.V1.Hash(), "retrieval"), (registry.PolicyVersion, registry.PolicyHash, registry.SearchBasis));
        Assert.Equal(registry.Causes.Order(StringComparer.Ordinal), registry.Causes);   // cause ordinate e senza duplicati
        Assert.Equal(registry.Causes.Distinct(), registry.Causes);

        if (!embeddingOk || !indexReady)
        {
            // Failed: nessuna ricerca, nessun candidato (nessuna lista parziale), nessun riordino interrogato, nessun invio dello stadio.
            string[] expected = [.. (embeddingOk ? [] : new[] { RetrievalException.EmbeddingUnavailable }), .. (indexReady ? [] : new[] { RetrievalException.IndexNotReady })];
            Assert.Equal(CandidateRegistry.OutcomeFailed, registry.Outcome);
            Assert.Equal(expected.Order(StringComparer.Ordinal), registry.Causes);
            Assert.False(outcome.CanInvokeStage);
            Assert.True(registry.Degraded);
            Assert.False(registry.RetrievalComplete);
            Assert.Empty(registry.PackageEvidence);
            Assert.All(registry.Gaps, g => Assert.Empty(g.Candidates));
            Assert.Equal(before, after);                                      // nessuna richiesta di contenuto, né embedding né riordino
            Assert.Null(registry.Readiness!.Reranker);                        // il riordino non è nemmeno sondato
            Assert.DoesNotContain(RetrievalException.RerankerUnavailable, registry.Causes);
        }
        else if (!rerankOk)
        {
            // Diagnostic: ordine RRF per tutti i Gap, nessun pacchetto utilizzabile dallo stadio, causa tipizzata.
            Assert.Equal(CandidateRegistry.OutcomeDiagnostic, registry.Outcome);
            Assert.Equal([RetrievalException.RerankerUnavailable], registry.Causes);
            Assert.False(outcome.CanInvokeStage);
            Assert.True(registry.Degraded);
            Assert.False(registry.RetrievalComplete);
            Assert.Empty(registry.PackageEvidence);
            var candidates = registry.Gaps.Single().Candidates;
            Assert.NotEmpty(candidates);
            Assert.All(candidates, c => Assert.Equal(c.RrfRank, c.FinalRank));
            Assert.All(candidates, c => Assert.False(c.InPackage));
            Assert.Equal(1, after.Embeddings - before.Embeddings);            // solo l'embedding della query: la ricerca è avvenuta
            Assert.Equal(before.Reranks, after.Reranks);                      // nessun testo inviato al riordino non pronto
            Assert.Equal("bge-reranker-v2-m3", registry.Readiness!.Reranker!.Model);   // lo stato del riordino osservato è nel registro
        }
        else
        {
            Assert.Equal(CandidateRegistry.OutcomePackage, registry.Outcome);
            Assert.Empty(registry.Causes);
            Assert.True(outcome.CanInvokeStage);
            Assert.False(registry.Degraded);
            Assert.True(registry.RetrievalComplete);
            Assert.NotEmpty(registry.PackageEvidence);
            Assert.Equal(s.EvidenceIds[1], registry.PackageEvidence[0]);      // la penale è la prima dopo il riordino
            Assert.All(registry.Gaps.Single().Candidates, c => Assert.True(c.FinalRank >= 1));
        }
    }

    [Fact]
    public async Task With_both_services_down_the_embedding_prevails_and_the_reranker_is_not_even_queried()
    {
        await using var s = await Scenario.CreateAsync();
        await s.ConfigureAsync(embeddingOk: false, rerankOk: false, indexReady: true);

        var outcome = await s.RunAsync("penale");

        Assert.Equal(CandidateRegistry.OutcomeFailed, outcome.Registry.Outcome);
        Assert.Equal([RetrievalException.EmbeddingUnavailable], outcome.Causes);
        Assert.Empty(s.Rerank.Paths);   // nemmeno la sonda di readiness del riordino
    }

    [Fact]
    public async Task An_embedding_failure_during_the_search_of_a_later_gap_discards_the_results_of_the_earlier_gaps()
    {
        await using var s = await Scenario.CreateAsync();
        s.Embedding.EmbeddingsOverride = inputs => inputs[0].Contains("foro", StringComparison.OrdinalIgnoreCase)
            ? FakeEmbeddingServer.Json("{}", HttpStatusCode.InternalServerError)
            : FakeEmbeddingServer.Json(FakeEmbeddingServer.EmbeddingsBody([.. inputs.Select(t => FakeEmbeddingServer.VectorOf(t, 1024))]));

        var outcome = await s.RunAsync("penale ritardo", "foro competente");

        Assert.Equal(CandidateRegistry.OutcomeFailed, outcome.Registry.Outcome);
        Assert.Equal([RetrievalException.EmbeddingUnavailable], outcome.Causes);
        Assert.Equal(2, outcome.Registry.Gaps.Count);                       // entrambi i Gap sono nel registro...
        Assert.All(outcome.Registry.Gaps, g => Assert.Empty(g.Candidates));   // ...ma nessuno con candidati: niente lista parziale
        Assert.Empty(outcome.Registry.PackageEvidence);
        Assert.Equal(0, s.ContentRequests().Reranks);
    }

    [Fact]
    public async Task A_malformed_rerank_response_is_a_degraded_diagnostic_outcome_with_its_own_typed_cause()
    {
        await using var s = await Scenario.CreateAsync();
        s.Rerank.RerankOverride = docs => FakeRerankServer.Json(docs.Length == 2
            ? FakeRerankServer.ResultsBody((0, 1.0), (1, 2.0))
            : FakeRerankServer.ResultsBody((0, 1.0)));   // incompleta: un punteggio su tre

        var outcome = await s.RunAsync("penale ritardo");

        Assert.Equal(CandidateRegistry.OutcomeDiagnostic, outcome.Registry.Outcome);
        Assert.Equal([RetrievalException.RerankerMalformedResponse], outcome.Causes);
        Assert.False(outcome.CanInvokeStage);
        Assert.All(outcome.Registry.Gaps.Single().Candidates, c => Assert.Equal(c.RrfRank, c.FinalRank));
    }

    [Fact]
    public async Task A_reranker_failure_makes_every_gap_diagnostic_never_a_mix_of_reranked_and_rrf_orders()
    {
        await using var s = await Scenario.CreateAsync();
        var calls = 0;
        s.Rerank.RerankOverride = docs => docs.Length == 2 || Interlocked.Increment(ref calls) == 1
            ? FakeRerankServer.Json(FakeRerankServer.ResultsBody([.. docs.Select((_, i) => (i, (double)-i))]))
            : FakeRerankServer.Json("{}", HttpStatusCode.ServiceUnavailable);   // il primo Gap è riordinato, il secondo fallisce

        var outcome = await s.RunAsync("penale ritardo", "foro competente");

        Assert.Equal(CandidateRegistry.OutcomeDiagnostic, outcome.Registry.Outcome);
        Assert.Equal([RetrievalException.RerankerUnavailable], outcome.Causes);
        Assert.All(outcome.Registry.Gaps, g => Assert.All(g.Candidates, c => Assert.Equal(c.RrfRank, c.FinalRank)));
    }

    [Fact]
    public async Task The_registry_is_reproducible_with_the_same_inputs_and_services()
    {
        await using var s = await Scenario.CreateAsync();

        var first = await s.RunAsync("penale ritardo", "foro competente");
        var second = await s.RunAsync("penale ritardo", "foro competente");

        Assert.Equal(CandidateRegistry.OutcomePackage, first.Registry.Outcome);
        Assert.Equal(first.Registry.Hash(), second.Registry.Hash());
    }
}
