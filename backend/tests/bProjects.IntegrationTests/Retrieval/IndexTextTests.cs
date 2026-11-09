using System.Text;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>003 T009: testo indicizzato <c>index-text-1</c> (funzione pura) e <c>IndexConfigHash</c>.</summary>
public sealed class IndexTextTests
{
    private const string Parser = "parsed-manifest-1|cfg-aaa";

    private static RetrievalOptions Options() => new() { Embedding = { Endpoint = "http://dgx.internal:8081/v1/" } };

    [Fact]
    public void The_text_is_the_canonical_text_normalized_to_NFC_and_single_spaces()
    {
        // «è» scomposta (e + accento combinante) diventa il carattere precomposto; spazi, a capo, tabulazioni e NBSP diventano uno spazio.
        var decomposed = "  Penale:\r\n\t5 %  per  ritardo è grave \n";

        var text = IndexText.Of(decomposed);

        Assert.Equal("Penale: 5 % per ritardo è grave", text);
        Assert.True(text.IsNormalized(NormalizationForm.FormC));
        Assert.Equal(text, IndexText.Of(text));   // idempotente
    }

    [Fact]
    public void Case_digits_and_punctuation_are_untouched()
    {
        Assert.Equal("Art. 12/B: 30 gg, €1.200,50", IndexText.Of("Art. 12/B:  30 gg,\n€1.200,50"));
    }

    [Fact]
    public void The_heading_path_is_not_part_of_the_indexed_text()
    {
        // La funzione accetta il solo testo del blocco: il percorso dei titoli non ha un parametro, quindi non può entrare (A-5).
        var parameters = typeof(IndexText).GetMethod(nameof(IndexText.Of))!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal("canonicalText", parameters[0].Name);
    }

    [Fact]
    public void The_same_block_gives_the_same_text_hash_and_a_different_text_a_different_one()
    {
        Assert.Equal(IndexText.TextHash("Il termine è 30 giorni."), IndexText.TextHash("Il termine  è\n30 giorni."));
        Assert.NotEqual(IndexText.TextHash("Il termine è 30 giorni."), IndexText.TextHash("Il termine è 60 giorni."));
        Assert.Matches("^[0-9a-f]{64}$", IndexText.TextHash("x"));
    }

    [Fact]
    public void The_same_configuration_gives_the_same_hash()
    {
        var a = IndexText.ConfigHash(Options(), Parser);
        var b = IndexText.ConfigHash(Options(), Parser);

        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public void Endpoint_key_and_timeouts_do_not_change_the_hash_but_every_declared_field_does()
    {
        var baseline = IndexText.ConfigHash(Options(), Parser);

        var infra = Options();
        infra.Embedding.Endpoint = "http://other.internal:9/v1/";
        infra.Embedding.ApiKey = "other";
        infra.TimeoutSeconds = 5;
        Assert.Equal(baseline, IndexText.ConfigHash(infra, Parser));

        var model = Options();
        model.Embedding.ModelId = "bge-m3-v2";
        Assert.NotEqual(baseline, IndexText.ConfigHash(model, Parser));

        var artifact = Options();
        artifact.Embedding.Artifact = "bge-m3-Q8_0.gguf";
        Assert.NotEqual(baseline, IndexText.ConfigHash(artifact, Parser));

        var dimension = Options();
        dimension.Embedding.Dimension = 768;
        Assert.NotEqual(baseline, IndexText.ConfigHash(dimension, Parser));

        Assert.NotEqual(baseline, IndexText.ConfigHash(Options(), "parsed-manifest-2|cfg-aaa"));
        Assert.NotEqual(baseline, IndexText.ConfigHash(Options(), "parsed-manifest-1|cfg-bbb"));
    }

    [Fact]
    public void The_text_version_and_the_fts_configuration_are_inputs_of_the_hash()
    {
        var baseline = IndexText.ConfigHash("index-text-1", "bge-m3", "a.gguf", 1024, "italian", Parser);

        Assert.NotEqual(baseline, IndexText.ConfigHash("index-text-2", "bge-m3", "a.gguf", 1024, "italian", Parser));
        Assert.NotEqual(baseline, IndexText.ConfigHash("index-text-1", "bge-m3", "a.gguf", 1024, "simple", Parser));
        Assert.Equal(IndexText.ConfigHash(Options(), Parser), IndexText.ConfigHash("index-text-1", "bge-m3-Q5", "bge-m3-Q5_K_M.gguf", 1024, "italian", Parser));
    }

    [Fact]
    public void The_reranker_is_not_an_input_of_the_index_identity()
    {
        var baseline = IndexText.ConfigHash(Options(), Parser);
        var o = Options();
        o.Reranker.ModelId = "another-reranker";
        o.Reranker.Artifact = "another.gguf";
        Assert.Equal(baseline, IndexText.ConfigHash(o, Parser));
    }
}
