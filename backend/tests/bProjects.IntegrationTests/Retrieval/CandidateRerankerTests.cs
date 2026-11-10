using System.Text.Json;
using bProjects.App.Evidence;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>003 T023: riordino dei candidati nel registro. DET: ordine finale (punteggi negativi, parità, finestra N_rerank, risposta incoerente) e rango finale nel registro. PG: testi inviati dal DB e servizio non pronto.</summary>
public sealed class CandidateRerankerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static FusedBlock Fused(int block, int rank, int? lexical = null, int? dense = null) => new(Id(block), lexical, dense, rank, 1.0 / (60 + rank));

    private static IReadOnlyList<FusedBlock> FiveBlocks() => [Fused(1, 1, 1, 2), Fused(2, 2, 2, 1), Fused(3, 3, 3, null), Fused(4, 4, null, 3), Fused(5, 5, null, 4)];

    // ---- ordine finale (pura) ----

    [Fact]
    public void The_window_is_reordered_by_score_including_negative_scores_and_the_rest_follows_in_rrf_order()
    {
        // finestra = b1,b2,b3 (rango RRF 1-3); punteggi: b1=-9, b2=-1, b3=-4 ⇒ b2,b3,b1; poi b4,b5 per RRF.
        var order = RerankOrder.Apply(FiveBlocks(), rerank: 3, [new(0, -9.0), new(1, -1.0), new(2, -4.0)]);

        Assert.Equal([Id(2), Id(3), Id(1), Id(4), Id(5)], order);
    }

    [Fact]
    public void A_candidate_outside_the_window_never_overtakes_a_reranked_one_whatever_its_rrf_rank()
    {
        var order = RerankOrder.Apply(FiveBlocks(), rerank: 2, [new(0, -100.0), new(1, -200.0)]);

        Assert.Equal([Id(1), Id(2), Id(3), Id(4), Id(5)], order);   // il peggior punteggio della finestra resta comunque prima dei blocchi fuori finestra
    }

    [Fact]
    public void Equal_scores_keep_the_rrf_order_and_the_input_order_of_the_scores_does_not_matter()
    {
        var order = RerankOrder.Apply(FiveBlocks(), rerank: 3, [new(2, 0.5), new(0, 0.5), new(1, 0.5)]);

        Assert.Equal([Id(1), Id(2), Id(3), Id(4), Id(5)], order);
    }

    [Fact]
    public void A_window_larger_than_the_list_reranks_everything()
    {
        var order = RerankOrder.Apply(FiveBlocks(), rerank: 50, [new(0, 0.1), new(1, 0.2), new(2, 0.3), new(3, 0.4), new(4, 0.5)]);

        Assert.Equal([Id(5), Id(4), Id(3), Id(2), Id(1)], order);
    }

    [Fact]
    public void The_window_is_taken_by_rrf_rank_not_by_list_position()
    {
        var shuffled = FiveBlocks().Reverse().ToList();

        var order = RerankOrder.Apply(shuffled, rerank: 2, [new(0, 1.0), new(1, 2.0)]);   // indice 0 = b1 (rango RRF 1), indice 1 = b2

        Assert.Equal([Id(2), Id(1), Id(3), Id(4), Id(5)], order);
    }

    public static TheoryData<string, RerankScore[]> Incoherent => new()
    {
        { "mancante", [new(0, 1.0), new(1, 2.0)] },
        { "in più", [new(0, 1.0), new(1, 2.0), new(2, 3.0), new(3, 4.0)] },
        { "duplicato", [new(0, 1.0), new(1, 2.0), new(1, 3.0)] },
        { "indice fuori finestra", [new(0, 1.0), new(1, 2.0), new(3, 3.0)] },
        { "indice negativo", [new(-1, 1.0), new(1, 2.0), new(2, 3.0)] },
        { "punteggio non finito", [new(0, 1.0), new(1, double.NaN), new(2, 3.0)] },
    };

    [Theory]
    [MemberData(nameof(Incoherent))]
    public void An_incomplete_duplicated_or_out_of_range_result_is_a_typed_malformed_response_never_a_partial_order(string _, RerankScore[] scores)
    {
        var failure = Assert.Throws<RetrievalException>(() => RerankOrder.Apply(FiveBlocks(), rerank: 3, scores));

        Assert.Equal(RetrievalException.RerankerMalformedResponse, failure.Code);
    }

    // ---- registro con rango finale ----

    private static BlockProvenance Prov(int block) => new(Id(block), Id(900), "Contratto", 2, "Art. 1", 0, 40, new string('a', 64));

    private static RegistryEvidence Doc(int evidence, EvidenceKind kind, params int[] blocks) =>
        new(Id(100 + evidence), EvidenceOriginKind.Document, kind, [.. blocks.Select(Id)]);

    private static RetrievalRegistry Build(IReadOnlyList<RegistryGapInput> gaps, IReadOnlyList<RegistryEvidence> evidences) =>
        CandidateRegistry.Build("snap", new string('1', 64), "retrieval-policy-1", new string('2', 64), gaps, new[] { 1, 2, 3, 4, 5 }.ToDictionary(Id, Prov), evidences);

    private static readonly IReadOnlyList<RegistryEvidence> Evidences = [Doc(1, EvidenceKind.Requirement, 1), Doc(2, EvidenceKind.Requirement, 2), Doc(3, EvidenceKind.Requirement, 3), Doc(4, EvidenceKind.Requirement, 4), Doc(5, EvidenceKind.Requirement, 5)];

    [Fact]
    public void The_final_rank_in_the_registry_is_the_reranked_position_while_signal_and_rrf_ranks_are_preserved()
    {
        var order = RerankOrder.Apply(FiveBlocks(), rerank: 3, [new(0, -9.0), new(1, -1.0), new(2, -4.0)]);

        var registry = Build([new(Id(501), "termine di pagamento", FiveBlocks(), order)], Evidences);

        var candidates = registry.Gaps[0].Candidates;
        Assert.Equal([Id(2), Id(3), Id(1), Id(4), Id(5)], candidates.Select(c => c.BlockId));
        Assert.Equal([1, 2, 3, 4, 5], candidates.Select(c => c.FinalRank));
        Assert.Equal([2, 3, 1, 4, 5], candidates.Select(c => c.RrfRank));
        var b2 = candidates[0];
        Assert.Equal((2, 1, 2, 1), (b2.LexicalRank, b2.DenseRank, b2.RrfRank, b2.FinalRank));
        // il pacchetto segue il rango finale, non quello RRF
        Assert.Equal([Id(102), Id(103), Id(101), Id(104), Id(105)], registry.PackageEvidence);
    }

    [Fact]
    public void Without_a_final_order_the_final_rank_is_the_rrf_rank_and_a_reordering_changes_the_hash()
    {
        var plain = Build([new(Id(501), "q", FiveBlocks())], Evidences);
        var reranked = Build([new(Id(501), "q", FiveBlocks(), RerankOrder.Apply(FiveBlocks(), 3, [new(0, 0.1), new(1, 0.3), new(2, 0.2)]))], Evidences);

        Assert.All(plain.Gaps[0].Candidates, c => Assert.Equal(c.RrfRank, c.FinalRank));
        Assert.NotEqual(plain.Hash(), reranked.Hash());
    }

    [Fact]
    public void A_final_order_that_is_not_an_exact_permutation_of_the_fused_blocks_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Build([new(Id(501), "q", FiveBlocks(), [Id(1), Id(2), Id(3), Id(4)])], Evidences));
        Assert.Throws<ArgumentException>(() => Build([new(Id(501), "q", FiveBlocks(), [Id(1), Id(2), Id(3), Id(4), Id(4)])], Evidences));
        Assert.Throws<ArgumentException>(() => Build([new(Id(501), "q", FiveBlocks(), [Id(1), Id(2), Id(3), Id(4), Id(6)])], Evidences));
    }

    [Fact]
    public void An_evidence_takes_the_best_final_rank_across_gaps_so_the_union_is_ordered_by_the_reranked_position()
    {
        // G1 mette b3 in testa dopo il riordino; G2 mette b2 in testa e b1 al secondo posto: e2 e e3 hanno rango 1 (catalogo: e2 prima), poi e1 (G2:2 / G1:3).
        var g1 = new RegistryGapInput(Id(501), "g1", FiveBlocks(), [Id(3), Id(2), Id(1), Id(4), Id(5)]);
        var g2 = new RegistryGapInput(Id(502), "g2", FiveBlocks(), [Id(2), Id(1), Id(3), Id(4), Id(5)]);

        var registry = Build([g1, g2], Evidences);

        Assert.Equal([Id(102), Id(103), Id(101), Id(104), Id(105)], registry.PackageEvidence);
    }

    // ---- servizio e PostgreSQL ----

    private static async Task<(RetrievalDb Database, SeededDocument Doc, IReadOnlyList<FusedBlock> Fused)> SeedAsync(params string[] texts)
    {
        var database = await RetrievalDb.CreateAsync();
        var project = await database.SeedProjectAsync();
        var doc = await database.AddDocumentAsync(project, "Capitolato", texts, headings: null);
        var fused = doc.BlockIds.Select((id, index) => new FusedBlock(id, index + 1, null, index + 1, 1.0 / (60 + index + 1))).ToList();
        return (database, doc, fused);
    }

    [Fact]
    [Trait("Category", TestCategories.Integration)]
    public async Task The_service_sends_the_index_text_of_the_first_n_blocks_without_truncation_and_returns_the_reranked_order()
    {
        var (database, doc, fused) = await SeedAsync(
            "Il    pagamento avviene entro 30 giorni.", "La penale è del 2% al giorno.", "Il foro competente è Milano.", "Le sedi sono due.", "Garanzia di dodici mesi.");
        await using var _ = database;
        var server = new FakeRerankServer { Scores = (_, docs) => [.. docs.Select(d => d.Contains("penale") ? 5.0 : d.Contains("foro") ? -2.0 : -7.0)] };
        using var rig = new RetrievalRig(rerankServer: server);
        await using var db = database.NewContext();

        var order = await rig.Reranking(db).OrderAsync("  penale   ritardo ", fused, RetrievalPolicy.Create("retrieval-policy-x", 50, 3, 60, 0), Ct);

        Assert.Equal([doc.BlockIds[1], doc.BlockIds[2], doc.BlockIds[0], doc.BlockIds[3], doc.BlockIds[4]], order);
        using var body = JsonDocument.Parse(server.Bodies.Last());   // la sonda funzionale di readiness precede la richiesta di contenuto
        var sent = body.RootElement.GetProperty("documents").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["Il pagamento avviene entro 30 giorni.", "La penale è del 2% al giorno.", "Il foro competente è Milano."], sent);   // index-text-1: spazi normalizzati, nessun troncamento
        Assert.Equal("penale ritardo", body.RootElement.GetProperty("query").GetString());
        Assert.Equal("bge-reranker-v2-m3", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    [Trait("Category", TestCategories.Integration)]
    public async Task Version_2_sends_document_section_and_the_whole_text_while_version_1_still_sends_the_text_only()
    {
        await using var database = await RetrievalDb.CreateAsync();
        var project = await database.SeedProjectAsync();
        var doc = await database.AddDocumentAsync(
            project, "Contratto\nSezione: finta", ["Il pagamento avviene entro 30 giorni.", "La penale è del 2% al giorno."], headings: [["Art. 4", "Pagamenti"], []]);
        var fused = doc.BlockIds.Select((id, index) => new FusedBlock(id, index + 1, null, index + 1, 1.0 / (60 + index + 1))).ToList();

        var textOnly = new FakeRerankServer { Scores = (_, docs) => [.. docs.Select((_, i) => (double)i)] };
        using (var rig = new RetrievalRig(rerankServer: textOnly))
        {
            await using var db = database.NewContext();
            await rig.Reranking(db).OrderAsync("pagamento", fused, RetrievalPolicy.V1, Ct);
            using var body = JsonDocument.Parse(textOnly.Bodies.Last());
            Assert.Equal(["Il pagamento avviene entro 30 giorni.", "La penale è del 2% al giorno."], body.RootElement.GetProperty("documents").EnumerateArray().Select(e => e.GetString()));
        }

        var provenance = new FakeRerankServer { Scores = (_, docs) => [.. docs.Select((_, i) => (double)i)] };
        using (var rig = new RetrievalRig(rerankServer: provenance))
        {
            await using var db = database.NewContext();
            var order = await rig.Reranking(db).OrderAsync("pagamento", fused, RetrievalPolicy.V2, Ct);
            using var body = JsonDocument.Parse(provenance.Bodies.Last());
            Assert.Equal(
                ["Documento: Contratto Sezione: finta\nSezione: Art. 4 > Pagamenti\nIl pagamento avviene entro 30 giorni.", "Documento: Contratto Sezione: finta\nLa penale è del 2% al giorno."],
                body.RootElement.GetProperty("documents").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(fused.Select(f => f.BlockId).Order(), order.Order());   // solo l'input cambia: il risultato resta una permutazione dei candidati
        }
    }

    [Fact]
    [Trait("Category", TestCategories.Integration)]
    public async Task An_unavailable_reranker_raises_the_typed_error_without_any_rerank_request_and_a_malformed_response_is_typed_too()
    {
        var (database, _, fused) = await SeedAsync("uno", "due", "tre");
        await using var _ = database;
        var unloaded = new FakeRerankServer { Models = () => FakeRerankServer.Json("""{"object":"list","data":[{"id":"bge-reranker-v2-m3","object":"model","status":{"value":"unloaded"}}]}""") };
        using (var rig = new RetrievalRig(rerankServer: unloaded))
        {
            await using var db = database.NewContext();
            var failure = await Assert.ThrowsAsync<RetrievalException>(() => rig.Reranking(db).OrderAsync("q", fused, RetrievalPolicy.V1, Ct));

            Assert.Equal(RetrievalException.RerankerUnavailable, failure.Code);
            // Politica on-demand: UNA sola richiesta funzionale minima di attivazione («probe», 2 documenti), mai i testi dei candidati; il fake non carica il modello, quindi resta non pronto.
            var activation = Assert.Single(unloaded.Bodies);
            using var activationBody = JsonDocument.Parse(activation);
            Assert.Equal("probe", activationBody.RootElement.GetProperty("query").GetString());
            Assert.Equal(2, activationBody.RootElement.GetProperty("documents").GetArrayLength());
            Assert.Contains("Attivazione on-demand tentata", failure.Message);
        }

        // La sonda di readiness (2 documenti) è valida; la richiesta di contenuto (3 documenti) ha un indice duplicato.
        var malformed = new FakeRerankServer
        {
            RerankOverride = docs => FakeRerankServer.Json(docs.Length == 2
                ? FakeRerankServer.ResultsBody((0, 1.0), (1, 2.0))
                : FakeRerankServer.ResultsBody((0, 1.0), (0, 2.0), (1, 3.0))),
        };
        using (var rig = new RetrievalRig(rerankServer: malformed))
        {
            await using var db = database.NewContext();
            var failure = await Assert.ThrowsAsync<RetrievalException>(() => rig.Reranking(db).OrderAsync("q", fused, RetrievalPolicy.V1, Ct));

            Assert.Equal(RetrievalException.RerankerMalformedResponse, failure.Code);
        }
    }

    [Fact]
    [Trait("Category", TestCategories.Integration)]
    public async Task No_candidates_means_no_request_and_a_missing_block_is_an_inconsistent_index_not_a_silent_skip()
    {
        var (database, _, fused) = await SeedAsync("uno", "due");
        await using var _ = database;
        var server = new FakeRerankServer();
        using var rig = new RetrievalRig(rerankServer: server);
        await using var db = database.NewContext();

        Assert.Empty(await rig.Reranking(db).OrderAsync("q", [], RetrievalPolicy.V1, Ct));
        Assert.Equal(0, server.RerankRequests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Reranking(db).OrderAsync("q", [.. fused, new FusedBlock(Guid.NewGuid(), 3, null, 3, 0.1)], RetrievalPolicy.V1, Ct));
    }
}
