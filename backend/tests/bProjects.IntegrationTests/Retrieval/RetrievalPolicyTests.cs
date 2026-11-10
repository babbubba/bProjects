using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>003 T022 (DET): politica <c>retrieval-policy-1</c> pura e hashata; valori fuori range rifiutati; ogni valore cambia l'impronta.</summary>
public sealed class RetrievalPolicyTests
{
    // Impronta FISSATA della versione 1: se cambia, un valore è stato modificato e serve una nuova versione (retrieval-policy-2), non un ritocco di questa.
    private const string PinnedJson = """{"packageBudgetBasis":"stage-input-budget","parentCharBudget":0,"pool":50,"rerank":50,"rrfK":60,"version":"retrieval-policy-1"}""";
    private const string PinnedHash = "0c7049270c3960b8ca981a51fa6ab2ea0ceb24fccb4b978fa365d7d84d4f2245";

    [Fact]
    public void Version_1_has_the_declared_values_and_a_pinned_canonical_form_and_hash()
    {
        var policy = RetrievalPolicy.V1;

        Assert.Equal(("retrieval-policy-1", 50, 50, 60, 0, "stage-input-budget"), (policy.Version, policy.Pool, policy.Rerank, policy.RrfK, policy.ParentCharBudget, policy.PackageBudgetBasis));
        Assert.Equal(Rrf.K, policy.RrfK);
        Assert.Equal(PinnedJson, policy.ToCanonicalJson());
        Assert.Equal(PinnedHash, policy.Hash());
    }

    // Versione 2 (T027): stessi valori della 1 + rerank-input-1. L'impronta della 1 resta quella fissata sopra.
    private const string PinnedJsonV2 = """{"packageBudgetBasis":"stage-input-budget","parentCharBudget":0,"pool":50,"rerank":50,"rerankInput":"rerank-input-1","rrfK":60,"version":"retrieval-policy-2"}""";
    private const string PinnedHashV2 = "84f3fb4aeb291dfe27bda03996f152bf9a3356b39fee21a9021180ba5cc60981";

    [Fact]
    public void Version_2_adds_only_the_rerank_input_and_leaves_version_1_and_its_pinned_hash_untouched()
    {
        var v1 = RetrievalPolicy.V1;
        var v2 = RetrievalPolicy.V2;

        Assert.Equal((RerankInput.TextOnly, PinnedJson, PinnedHash), (v1.RerankInputMode, v1.ToCanonicalJson(), v1.Hash()));   // la 1 non cambia di un byte
        Assert.Equal(("retrieval-policy-2", RerankInput.Provenance1, PinnedJsonV2, PinnedHashV2), (v2.Version, v2.RerankInputMode, v2.ToCanonicalJson(), v2.Hash()));
        Assert.Equal((v1.Pool, v1.Rerank, v1.RrfK, v1.ParentCharBudget, v1.PackageBudgetBasis), (v2.Pool, v2.Rerank, v2.RrfK, v2.ParentCharBudget, v2.PackageBudgetBasis));   // A/B: stessa configurazione di recupero
        Assert.NotEqual(v1.Hash(), v2.Hash());
    }

    [Fact]
    public void The_rerank_input_alone_changes_the_hash_and_the_default_mode_is_the_version_1_form()
    {
        var textOnly = RetrievalPolicy.Create("retrieval-policy-x", 50, 50, 60, 0);
        var provenance = RetrievalPolicy.Create("retrieval-policy-x", 50, 50, 60, 0, RerankInput.Provenance1);

        Assert.Equal(RerankInput.TextOnly, textOnly.RerankInputMode);
        Assert.NotEqual(textOnly.Hash(), provenance.Hash());
        Assert.Equal(RetrievalPolicy.Create("retrieval-policy-1", 50, 50, 60, 0).Hash(), RetrievalPolicy.V1.Hash());
        Assert.ThrowsAny<ArgumentException>(() => RetrievalPolicy.Create("retrieval-policy-x", 50, 50, 60, 0, "rerank-input-9"));
    }

    [Fact]
    public void The_hash_is_stable_across_instances_and_calls()
    {
        var a = RetrievalPolicy.Create("retrieval-policy-1", 50, 50, 60, 0);
        var b = RetrievalPolicy.Create("retrieval-policy-1", 50, 50, 60, 0);

        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(a.Hash(), a.Hash());
        Assert.Equal(RetrievalPolicy.V1.Hash(), a.Hash());
        Assert.Matches("^[0-9a-f]{64}$", a.Hash());
    }

    [Fact]
    public void Changing_any_single_value_changes_the_hash()
    {
        var baseline = RetrievalPolicy.V1.Hash();

        string[] hashes =
        [
            RetrievalPolicy.Create("retrieval-policy-2", 50, 50, 60, 0).Hash(),
            RetrievalPolicy.Create("retrieval-policy-1", 51, 50, 60, 0).Hash(),
            RetrievalPolicy.Create("retrieval-policy-1", 50, 49, 60, 0).Hash(),
            RetrievalPolicy.Create("retrieval-policy-1", 50, 50, 61, 0).Hash(),
            RetrievalPolicy.Create("retrieval-policy-1", 50, 50, 60, 1).Hash(),
        ];

        Assert.DoesNotContain(baseline, hashes);
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
    }

    [Theory]
    [InlineData("", 50, 50, 60, 0)]
    [InlineData("  ", 50, 50, 60, 0)]
    [InlineData("retrieval-policy-1", 0, 50, 60, 0)]
    [InlineData("retrieval-policy-1", -1, 50, 60, 0)]
    [InlineData("retrieval-policy-1", 50, 0, 60, 0)]
    [InlineData("retrieval-policy-1", 50, 101, 60, 0)]   // la fusione di due classifiche da 50 contiene al massimo 100 blocchi
    [InlineData("retrieval-policy-1", 50, 50, 0, 0)]
    [InlineData("retrieval-policy-1", 50, 50, 60, -1)]
    public void Values_out_of_range_are_rejected_not_corrected(string version, int pool, int rerank, int rrfK, int parentCharBudget) =>
        Assert.ThrowsAny<ArgumentException>(() => RetrievalPolicy.Create(version, pool, rerank, rrfK, parentCharBudget));

    [Fact]
    public void Rerank_up_to_twice_the_pool_is_accepted()
    {
        var policy = RetrievalPolicy.Create("retrieval-policy-x", 10, 20, 60, 0);

        Assert.Equal(20, policy.Rerank);
    }

    [Fact]
    public void The_rrf_constant_of_the_policy_drives_the_fusion_score()
    {
        var id = Guid.NewGuid();

        var defaultK = Rrf.Fuse([new(id, 1)], []).Single();
        var policyK = Rrf.Fuse([new(id, 1)], [], 10).Single();

        Assert.Equal(1.0 / 61, defaultK.Score, 12);
        Assert.Equal(1.0 / 11, policyK.Score, 12);
        Assert.Throws<ArgumentOutOfRangeException>(() => Rrf.Fuse([], [], 0));
    }
}
