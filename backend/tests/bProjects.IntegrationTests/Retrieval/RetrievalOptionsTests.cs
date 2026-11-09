using bProjects.App.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>003 T005: configurazione del recupero. Inerte per default, validata all'avvio come AiOptions: host interno, dimensione 1024, SHA-256 opzionale.</summary>
public sealed class RetrievalOptionsTests
{
    private static RetrievalOptions Valid() => new()
    {
        Enabled = true,
        Embedding = { Endpoint = "http://dgx.internal:8081/v1/", ApiKey = "k", Dimension = 1024 },
    };

    private static ValidateOptionsResult Validate(RetrievalOptions o) => new RetrievalOptionsValidator().Validate(null, o);

    [Fact]
    public void Defaults_are_inert_and_valid_without_any_service_configured()
    {
        var o = new RetrievalOptions();

        Assert.False(o.Enabled);
        Assert.Equal(string.Empty, o.Embedding.Endpoint);
        Assert.Equal(string.Empty, o.Reranker.Endpoint);
        Assert.Equal(1024, o.Embedding.Dimension);
        Assert.Equal("index-text-1", RetrievalOptions.IndexTextVersion);
        Assert.True(Validate(o).Succeeded);
    }

    [Fact]
    public void Binds_from_the_Retrieval_section_and_keeps_the_pinned_model_defaults()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Retrieval:Enabled"] = "true",
            ["Retrieval:Embedding:Endpoint"] = "http://127.0.0.1:8081/v1/",
            ["Retrieval:Embedding:ApiKey"] = "from-env",
            ["Retrieval:Embedding:Sha256"] = new string('a', 64),
        }).Build();

        var o = config.GetSection(RetrievalOptions.SectionName).Get<RetrievalOptions>()!;

        Assert.True(o.Enabled);
        Assert.Equal("bge-m3", o.Embedding.ModelId);
        Assert.Equal("bge-m3-Q5_K_M.gguf", o.Embedding.Artifact);
        Assert.Equal("bge-reranker-v2-m3", o.Reranker.ModelId);
        Assert.True(Validate(o).Succeeded);
    }

    [Theory]
    [InlineData("http://api.openai.com/v1/")]
    [InlineData("https://embeddings.example.com/v1/")]
    [InlineData("http://8.8.8.8:8081/v1/")]
    public void An_external_host_is_rejected_for_embedding_and_reranker(string endpoint)
    {
        var o = Valid();
        o.Embedding.Endpoint = endpoint;
        Assert.Contains(Validate(o).Failures!, f => f.Contains("Retrieval:Embedding:Endpoint") && f.Contains("host interno"));

        var r = Valid();
        r.Reranker.Endpoint = endpoint;
        Assert.Contains(Validate(r).Failures!, f => f.Contains("Retrieval:Reranker:Endpoint") && f.Contains("host interno"));
    }

    [Theory]
    [InlineData("http://10.1.2.3:8081/v1/")]
    [InlineData("http://192.168.253.18:8081/v1/")]
    [InlineData("http://localhost:8081/v1/")]
    [InlineData("http://dgx.internal:8081/v1/")]
    public void Internal_hosts_are_accepted(string endpoint)
    {
        var o = Valid();
        o.Embedding.Endpoint = endpoint;
        Assert.True(Validate(o).Succeeded);
    }

    [Fact]
    public void An_allowed_host_makes_a_corporate_name_acceptable()
    {
        var o = Valid();
        o.Embedding.Endpoint = "http://embed.corp.example.com:8081/v1/";
        Assert.False(Validate(o).Succeeded);

        o.AllowedHosts = ["embed.corp.example.com"];
        Assert.True(Validate(o).Succeeded);
    }

    [Theory]
    [InlineData(768)]
    [InlineData(1536)]
    [InlineData(0)]
    public void A_dimension_other_than_1024_is_rejected(int dimension)
    {
        var o = Valid();
        o.Embedding.Dimension = dimension;

        Assert.Contains(Validate(o).Failures!, f => f.Contains("Dimension") && f.Contains("1024"));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://dgx.internal/v1/")]
    [InlineData("http://user:pw@dgx.internal/v1/")]
    [InlineData("http://dgx.internal/v1/?api_key=secret")]
    public void A_malformed_endpoint_or_one_with_credentials_or_query_is_rejected(string endpoint)
    {
        var o = Valid();
        o.Embedding.Endpoint = endpoint;
        Assert.False(Validate(o).Succeeded);
    }

    [Fact]
    public void An_enabled_retrieval_requires_the_embedding_endpoint_and_the_secret_from_the_environment()
    {
        var o = Valid();
        o.Embedding.Endpoint = string.Empty;
        Assert.False(Validate(o).Succeeded);

        o = Valid();
        o.Embedding.ApiKey = string.Empty;
        Assert.Contains(Validate(o).Failures!, f => f.Contains("ApiKey"));
    }

    [Fact]
    public void A_missing_sha256_means_not_verified_and_is_not_blocking()
    {
        var o = Valid();
        Assert.True(Validate(o).Succeeded);

        Assert.Equal(Sha256Status.NotVerified, o.Embedding.ResolveSha256Status(null));
        Assert.Equal(Sha256Status.NotVerified, o.Embedding.ResolveSha256Status(new string('a', 64)));   // nulla è dichiarato: nessuna prova possibile
    }

    [Fact]
    public void A_configured_sha256_is_verified_only_when_the_runtime_proves_the_same_value()
    {
        var o = Valid();
        var sha = new string('a', 64);
        o.Embedding.Sha256 = sha;

        Assert.True(Validate(o).Succeeded);
        Assert.Equal(Sha256Status.NotVerified, o.Embedding.ResolveSha256Status(null));            // il runtime non può dimostrarlo: nessun falso «Verified»
        Assert.Equal(Sha256Status.NotVerified, o.Embedding.ResolveSha256Status(new string('b', 64)));
        Assert.Equal(Sha256Status.Verified, o.Embedding.ResolveSha256Status(sha.ToUpperInvariant()));
    }

    [Fact]
    public void A_malformed_sha256_is_rejected()
    {
        var o = Valid();
        o.Embedding.Sha256 = "not-a-hash";
        Assert.Contains(Validate(o).Failures!, f => f.Contains("Sha256"));
    }

    [Fact]
    public void Timeouts_must_be_positive()
    {
        var o = Valid();
        o.TimeoutSeconds = 0;
        o.RuntimeCheckSeconds = -1;
        Assert.Equal(2, Validate(o).Failures!.Count());
    }
}
