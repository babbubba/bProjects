using bProjects.App.Evidence;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>003 T015 (DET): registro dei candidati da classifiche sintetiche: unione, tie-break, mappatura blocco→evidenza, always_included, budget e regole di retrievalComplete.</summary>
public sealed class CandidateRegistryTests
{
    private static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static BlockProvenance Prov(int block) => new(Id(block), Id(900), "Contratto", 2, "Art. 1 > Pagamenti", 0, 40, new string('a', 64));

    private static IReadOnlyDictionary<Guid, BlockProvenance> Blocks(params int[] blocks) => blocks.ToDictionary(Id, Prov);

    private static RegistryEvidence Doc(int evidence, EvidenceKind kind, params int[] blocks) =>
        new(Id(100 + evidence), EvidenceOriginKind.Document, kind, [.. blocks.Select(Id)]);

    private static RegistryEvidence Message(int evidence) => new(Id(100 + evidence), EvidenceOriginKind.Message, EvidenceKind.Requirement, []);

    private static FusedBlock Fused(int block, int rank, int? lexical = null, int? dense = null) => new(Id(block), lexical, dense, rank, 1.0 / (60 + rank));

    private static RetrievalRegistry Build(
        IReadOnlyList<RegistryGapInput> gaps, IReadOnlyList<RegistryEvidence> evidences, IReadOnlyDictionary<Guid, BlockProvenance> blocks,
        IReadOnlyList<string>? causes = null, RegistryBudget? budget = null) =>
        CandidateRegistry.Build("snap", new string('1', 64), "retrieval-policy-1", new string('2', 64), gaps, blocks, evidences, causes, budget);

    // G1: b1, b2, b3 ; G2: b2, b4   —   e1→b1  e2→b2  e3→b3  e4→b4  e5→b1+b4 ; b6 senza evidenze
    private static (IReadOnlyList<RegistryGapInput> Gaps, IReadOnlyList<RegistryEvidence> Evidences, IReadOnlyDictionary<Guid, BlockProvenance> Blocks) Scenario() =>
    (
        [
            new(Id(501), "Qual è il termine di pagamento?", [Fused(1, 1, 1, 2), Fused(2, 2, 2, 1), Fused(3, 3, 3, null), Fused(6, 4, null, 3)]),
            new(Id(502), "Quale penale si applica?", [Fused(2, 1, 1, 1), Fused(4, 2, 2, 2)]),
        ],
        [Doc(1, EvidenceKind.Requirement, 1), Doc(2, EvidenceKind.Constraint, 2), Doc(3, EvidenceKind.Requirement, 3), Doc(4, EvidenceKind.Action, 4), Doc(5, EvidenceKind.Requirement, 1, 4), Doc(6, EvidenceKind.Gap, 3)],
        Blocks(1, 2, 3, 4, 6)
    );

    [Fact]
    public void Candidates_are_the_evidences_of_the_retrieved_blocks_ordered_by_best_rank_across_gaps_then_catalog_order()
    {
        var (gaps, evidences, blocks) = Scenario();

        var registry = Build(gaps, evidences, blocks);

        // rango migliore: e1=1 (G1/b1), e2=1 (G2/b2), e5=1 (G1/b1), e4=2 (G2/b4), e3=3; a pari rango, ordine di catalogo (e1,e2,e5). L'evidenza Gap (e6) non è mai un candidato.
        Assert.Equal([Id(101), Id(102), Id(105), Id(104), Id(103)], registry.PackageEvidence);
        Assert.DoesNotContain(Id(106), registry.PackageEvidence);
        Assert.Equal((CandidateRegistry.OutcomePackage, true, false, "retrieval", "retrieval-registry-1"), (registry.Outcome, registry.RetrievalComplete, registry.Degraded, registry.SearchBasis, registry.Contract));
        Assert.Empty(registry.Excluded);
    }

    [Fact]
    public void Each_gap_keeps_its_own_ordered_candidates_with_all_ranks_provenance_and_unique_final_ranks()
    {
        var (gaps, evidences, blocks) = Scenario();

        var registry = Build(gaps, evidences, blocks);

        var first = registry.Gaps[0];
        Assert.Equal(Id(501), first.GapEvidenceId);
        Assert.Equal("Qual è il termine di pagamento?", first.Query);
        Assert.Equal([Id(1), Id(2), Id(3), Id(6)], first.Candidates.Select(c => c.BlockId));
        Assert.Equal([1, 2, 3, 4], first.Candidates.Select(c => c.FinalRank));
        Assert.Equal(first.Candidates.Select(c => c.FinalRank).Distinct().Count(), first.Candidates.Count);
        var c1 = first.Candidates[0];
        Assert.Equal((1, 2, 1, 1), (c1.LexicalRank, c1.DenseRank, c1.RrfRank, c1.FinalRank));
        Assert.Equal(("Contratto", 2, "Art. 1 > Pagamenti", CandidateRegistry.OriginRanked, true, (string?)null), (c1.DocumentName, c1.DocumentVersion, c1.Section, c1.Origin, c1.InPackage, c1.ExcludedReason));
        Assert.Equal([0, 40], c1.Range);
        Assert.Equal(new string('a', 64), c1.TextHash);
        Assert.Equal([Id(101), Id(105)], c1.EvidenceIds);   // e1 ed e5 citano il blocco 1, in ordine di catalogo
        Assert.Equal([Id(2), Id(4)], registry.Gaps[1].Candidates.Select(c => c.BlockId));
    }

    [Fact]
    public void A_block_without_evidence_is_recorded_with_its_reason_and_never_offered_to_the_stage()
    {
        var (gaps, evidences, blocks) = Scenario();

        var registry = Build(gaps, evidences, blocks);

        var orphan = registry.Gaps[0].Candidates.Single(c => c.BlockId == Id(6));
        Assert.Empty(orphan.EvidenceIds);
        Assert.Equal((false, CandidateRegistry.ReasonNoEvidence), (orphan.InPackage, orphan.ExcludedReason));
        Assert.DoesNotContain(Id(6), registry.PackageEvidence);
        // Un blocco senza evidenze non rende il recupero incompleto: è un fatto del ledger, registrato.
        Assert.True(registry.RetrievalComplete);
    }

    [Fact]
    public void Message_evidences_are_always_included_first_and_in_catalog_order()
    {
        var (gaps, evidences, blocks) = Scenario();
        IReadOnlyList<RegistryEvidence> withMessages = [Message(7), .. evidences, Message(8)];

        var registry = Build(gaps, withMessages, blocks);

        Assert.Equal([Id(107), Id(108), Id(101), Id(102), Id(105), Id(104), Id(103)], registry.PackageEvidence);
        Assert.Equal([(Id(107), true), (Id(108), true)], registry.AlwaysIncluded.Select(a => (a.EvidenceId, a.InPackage)));
        Assert.True(registry.RetrievalComplete);
    }

    [Fact]
    public void The_budget_cuts_by_whole_evidences_in_rank_order_stopping_at_the_first_that_does_not_fit_and_makes_retrieval_incomplete()
    {
        var (gaps, evidences, blocks) = Scenario();
        var cost = new[] { 101, 102, 103, 104, 105 }.ToDictionary(Id, _ => 10L);

        var registry = Build(gaps, evidences, blocks, budget: new RegistryBudget(25, cost));

        Assert.Equal([Id(101), Id(102)], registry.PackageEvidence);
        Assert.Equal([(Id(105), "budget"), (Id(104), "budget"), (Id(103), "budget")], registry.Excluded.Select(e => (e.EvidenceId, e.Reason)));
        Assert.False(registry.RetrievalComplete);
        Assert.False(registry.Degraded);
        var byBlock = registry.Gaps[0].Candidates.ToDictionary(c => c.BlockId);
        Assert.True(byBlock[Id(1)].InPackage);      // e1 è nel pacchetto
        Assert.Equal((false, "budget"), (byBlock[Id(3)].InPackage, byBlock[Id(3)].ExcludedReason));
        // Tutti i candidati esclusi hanno un motivo (regola: inPackage=false ⇒ excludedReason).
        Assert.All(registry.Gaps.SelectMany(g => g.Candidates), c => Assert.True(c.InPackage || c.ExcludedReason is not null));
    }

    [Fact]
    public void An_always_included_evidence_that_does_not_fit_makes_retrieval_incomplete()
    {
        var (gaps, evidences, blocks) = Scenario();
        IReadOnlyList<RegistryEvidence> withMessage = [Message(7), .. evidences];
        var cost = new Dictionary<Guid, long> { [Id(107)] = 50 };

        var registry = Build(gaps, withMessage, blocks, budget: new RegistryBudget(20, cost));

        Assert.False(registry.RetrievalComplete);
        Assert.Contains(registry.Excluded, e => e.EvidenceId == Id(107) && e.Reason == "budget");
        Assert.Equal((Id(107), false), registry.AlwaysIncluded.Select(a => (a.EvidenceId, a.InPackage)).Single());
    }

    [Fact]
    public void An_embedding_cause_makes_the_outcome_failed_with_no_package_and_incomplete_retrieval()
    {
        var (gaps, evidences, blocks) = Scenario();

        var registry = Build([gaps[0] with { Fused = [] }, gaps[1] with { Fused = [] }], evidences, blocks, causes: [RetrievalException.EmbeddingUnavailable]);

        Assert.Equal((CandidateRegistry.OutcomeFailed, false, true), (registry.Outcome, registry.RetrievalComplete, registry.Degraded));
        Assert.Equal([RetrievalException.EmbeddingUnavailable], registry.Causes);
        Assert.Empty(registry.PackageEvidence);
    }

    [Fact]
    public void An_unavailable_reranker_gives_a_diagnostic_registry_with_the_ranking_but_no_delivered_package()
    {
        var (gaps, evidences, blocks) = Scenario();
        IReadOnlyList<RegistryEvidence> withMessage = [Message(7), .. evidences];

        var registry = Build(gaps, withMessage, blocks, causes: [RetrievalException.RerankerUnavailable]);

        Assert.Equal((CandidateRegistry.OutcomeDiagnostic, false, true), (registry.Outcome, registry.RetrievalComplete, registry.Degraded));
        Assert.Empty(registry.PackageEvidence);
        Assert.NotEmpty(registry.Gaps[0].Candidates);   // il registro diagnostico resta consultabile
        Assert.All(registry.Gaps.SelectMany(g => g.Candidates).Where(c => c.EvidenceIds.Count > 0), c => Assert.Equal((false, "degraded"), (c.InPackage, c.ExcludedReason)));
        Assert.False(registry.AlwaysIncluded.Single().InPackage);
    }

    [Fact]
    public void Embedding_and_reranker_causes_together_resolve_to_failed_and_causes_are_sorted_and_distinct()
    {
        var (gaps, evidences, blocks) = Scenario();

        var registry = Build(gaps, evidences, blocks, causes: [RetrievalException.RerankerUnavailable, RetrievalException.EmbeddingUnavailable, RetrievalException.RerankerUnavailable]);

        Assert.Equal(CandidateRegistry.OutcomeFailed, registry.Outcome);
        Assert.Equal([RetrievalException.EmbeddingUnavailable, RetrievalException.RerankerUnavailable], registry.Causes);
    }

    [Fact]
    public void No_candidates_at_all_is_a_valid_complete_registry_never_an_absence_claim()
    {
        var registry = Build([new(Id(501), "Quale penale?", [])], [Doc(1, EvidenceKind.Requirement, 1)], Blocks(1));

        Assert.Equal((CandidateRegistry.OutcomePackage, true), (registry.Outcome, registry.RetrievalComplete));
        Assert.Empty(registry.PackageEvidence);
        Assert.Empty(registry.Gaps[0].Candidates);
    }

    [Fact]
    public void The_registry_is_deterministic_independent_of_gap_and_evidence_arrival_inside_the_union_and_hashes_stably()
    {
        var (gaps, evidences, blocks) = Scenario();

        var a = Build(gaps, evidences, blocks);
        var b = Build(gaps, evidences, blocks);
        var swapped = Build([gaps[1], gaps[0]], evidences, blocks);

        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(a.ToCanonicalJson(), b.ToCanonicalJson());
        Assert.Equal(a.PackageEvidence, swapped.PackageEvidence);   // l'unione non dipende dall'ordine dei Gap
        var json = a.ToCanonicalJson();
        Assert.Contains("\"contract\":\"retrieval-registry-1\"", json);
        Assert.DoesNotContain("score", json, StringComparison.OrdinalIgnoreCase);   // nessun punteggio presentato come probabilità
        Assert.Equal(64, a.Hash().Length);
    }

    [Fact]
    public void A_retrieved_block_without_provenance_is_a_caller_error()
    {
        var (gaps, evidences, _) = Scenario();

        Assert.Throws<ArgumentException>(() => Build(gaps, evidences, Blocks(1)));
    }
}
