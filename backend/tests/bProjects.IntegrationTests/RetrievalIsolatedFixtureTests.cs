using System.Net;
using System.Text;
using System.Text.Json;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>Test OFFLINE senza database né rete: identità e coerenza del ledger controllato isolato, manifesto versionato e routing fail-closed del modello (003, verifica isolata Retrieval → Gap Stage).</summary>
public sealed class RetrievalIsolatedFixtureTests
{
    private const string WriteVariable = "BPROJECTS_ISOLATED_WRITE_MANIFEST";

    private static string Fold(string text) => DocumentSmallFlowMafRealTests.Fold(text);

    public static TheoryData<string> Scenarios => new() { RetrievalGapCampaign.Single.Label, RetrievalGapCampaign.Multi.Label };

    private static CampaignScenario Scenario(string label) => RetrievalGapCampaign.Sequence.Single(s => s.Label == label);

    [Fact]
    public void The_versioned_manifest_matches_the_fixture_and_never_touches_the_frozen_T040_manifest()
    {
        var expected = RetrievalIsolatedFixture.BuildManifestJson();
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            File.WriteAllText(RetrievalIsolatedManifest.FilePath(), expected);
        }

        var actual = RetrievalIsolatedManifest.Read();
        Assert.NotNull(actual);
        Assert.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), actual.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(RetrievalGapCampaign.FrozenManifestSha256, RetrievalGapCampaign.ManifestSha256());   // il manifesto T040 resta quello congelato
        using var doc = JsonDocument.Parse(actual);
        Assert.Equal(RetrievalGapCampaign.FrozenManifestSha256, doc.RootElement.GetProperty("t040ManifestSha256").GetString());
        Assert.True(doc.RootElement.GetProperty("notEquivalentToT040").GetString()!.Contains("NON e un PASS di T040", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void The_ledger_is_deterministic_and_every_evidence_cites_the_exact_text_of_its_block(string label)
    {
        var scenario = Scenario(label);
        var ledger = RetrievalIsolatedFixture.Build(scenario);
        Assert.Equal(ledger.Sha256, RetrievalIsolatedFixture.Build(scenario).Sha256);
        foreach (var d in ledger.Documents)
        {
            Assert.Equal(d.Blocks.Count, d.Blocks.Select(b => b.Ordinal).Distinct().Count());
            foreach (var e in d.Evidences)
            {
                var block = d.Blocks.Single(b => b.Ordinal == e.BlockOrdinal);
                Assert.Equal(e.Statement, block.Text[e.Start..e.End]);
            }
        }

        var gold = RetrievalGapCampaign.BuildCorpus(scenario).Gold;
        var statements = ledger.Documents.Where(d => !d.ExcludedByPm).SelectMany(d => d.Evidences).Select(e => Fold(e.Statement)).ToList();
        Assert.All(gold.Where(g => g.DocKey != "pm"), g => Assert.Contains(statements, s => s.Contains(Fold(g.Sentence).TrimEnd('.'), StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void The_questions_gold_expectations_and_exclusion_are_those_frozen_for_T040(string label)
    {
        var scenario = Scenario(label);
        var ledger = RetrievalIsolatedFixture.Build(scenario);
        var gaps = ledger.Documents.Single(d => d.Key == RetrievalIsolatedFixture.QuestionsKey).Evidences;
        Assert.Equal(scenario.Expectations.Select(e => e.Question), gaps.Select(g => g.Statement));
        Assert.All(gaps, g => Assert.Equal(EvidenceKind.Gap, g.Kind));
        Assert.Equal(1, ledger.Documents.Count(d => d.ExcludedByPm));
        Assert.Equal(RetrievalGapCampaign.ExcludedDocument.Key, ledger.Documents.Single(d => d.ExcludedByPm).Key);
        Assert.DoesNotContain(ledger.Documents.Where(d => d.ExcludedByPm).SelectMany(d => d.Evidences), e => e.Kind == EvidenceKind.Gap);

        // Ogni riscontro atteso è nel ledger incluso nello snapshot; il testo vietato esiste SOLO nel documento escluso.
        var included = ledger.Documents.Where(d => !d.ExcludedByPm).SelectMany(d => d.Evidences).Select(e => Fold(e.Statement)).ToList();
        foreach (var quote in scenario.Expectations.SelectMany(e => e.SupportQuotes))
        {
            Assert.Contains(included, s => quote.Any(q => s.Contains(Fold(q), StringComparison.Ordinal)));
        }

        var excluded = ledger.Documents.Where(d => d.ExcludedByPm).SelectMany(d => d.Evidences).Select(e => Fold(e.Statement)).ToList();
        foreach (var forbidden in scenario.Expectations.SelectMany(e => e.ForbiddenQuotes).Where(f => f == "cinquantamila"))
        {
            Assert.DoesNotContain(included, s => s.Contains(Fold(forbidden), StringComparison.Ordinal));
            Assert.Contains(excluded, s => s.Contains(Fold(forbidden), StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_single_ledger_is_sized_like_the_run_5_document_set()
    {
        // Run 5: indicizzazione 117 + 2 + 2 blocchi per le tre preparazioni; qui in più il documento delle domande (5 blocchi).
        var ledger = RetrievalIsolatedFixture.Build(RetrievalGapCampaign.Single);
        Assert.Equal([117, 2, 2, 5], ledger.Documents.Select(d => d.Blocks.Count));
        Assert.True(ledger.IncludedNonGapEvidenceCount >= RetrievalGapCampaign.SingleBulkRules);
    }

    // ================================================================ routing fail-closed

    private sealed class Upstream : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Paths)
            {
                Paths.Add(request.RequestUri!.AbsolutePath);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") });
        }
    }

    private static HttpRequestMessage Chat(string text) => new(HttpMethod.Post, "http://dgx.internal/v1/chat/completions")
    {
        Content = new StringContent(JsonSerializer.Serialize(new { messages = new[] { new { role = "user", content = text } } }), Encoding.UTF8, "application/json"),
    };

    private const string Stage = StageOnlyModelRouter.StageMarker + "\n" + StageOnlyModelRouter.RetrievalBasisMarker + "\n";
    private const string Leaf = "Tipo di batch: foglia\nVista: A\n";

    [Fact]
    public async Task Only_the_gap_stage_reaches_the_upstream_model_and_only_once()
    {
        var upstream = new Upstream();
        var router = new StageOnlyModelRouter(upstream, _ => "{}");
        using var client = new HttpClient(router);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Chat(Leaf), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Chat("Tipo di batch: cross\nVista: Cross\n"), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(upstream.Paths);                                    // la gerarchia (anche il cross) è simulata localmente: mai al modello reale
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Chat(Stage), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(["/v1/chat/completions"], upstream.Paths);

        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(Chat(Stage), TestContext.Current.CancellationToken));
        Assert.Contains("non prevista", second.Message, StringComparison.Ordinal);
        Assert.Single(upstream.Paths);                                    // la seconda richiesta generativa NON è stata inoltrata
        Assert.Equal((1, 1), (router.ForwardedStageCalls, router.Violations.Count));
    }

    [Fact]
    public async Task A_stage_request_on_the_exhaustive_ledger_path_is_refused_before_reaching_the_upstream()
    {
        var upstream = new Upstream();
        var router = new StageOnlyModelRouter(upstream, _ => "{}");
        using var client = new HttpClient(router);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(Chat(StageOnlyModelRouter.StageMarker + "\nsearchBasis: ledger-exhaustive\n"), TestContext.Current.CancellationToken));
        Assert.Empty(upstream.Paths);
        Assert.Single(router.Violations);
    }

    [Fact]
    public async Task An_unrecognised_chat_request_or_path_is_a_violation_and_is_never_forwarded()
    {
        var upstream = new Upstream();
        var router = new StageOnlyModelRouter(upstream, _ => "{}");
        using var client = new HttpClient(router);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(Chat("Rispondi solo: ok"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("http://dgx.internal/v1/embeddings", new StringContent("{}"), TestContext.Current.CancellationToken));
        Assert.Empty(upstream.Paths);
        Assert.Equal(2, router.Violations.Count);
    }

    [Fact]
    public async Task The_warm_up_request_is_admitted_once_with_a_16_token_cap_and_never_otherwise()
    {
        var upstream = new Upstream();
        var router = new StageOnlyModelRouter(upstream, _ => "{}", maxWarmupCalls: 1);
        using var client = new HttpClient(router);
        var ct = TestContext.Current.CancellationToken;
        HttpRequestMessage Warm(int tokens) => new(HttpMethod.Post, "http://dgx.internal/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { max_completion_tokens = tokens, messages = new[] { new { role = "user", content = StageOnlyModelRouter.WarmupText } } }), Encoding.UTF8, "application/json"),
        };

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Warm(16), ct)).StatusCode);
        Assert.Single(upstream.Paths);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(Warm(16), ct));      // secondo riscaldamento: non ammesso
        Assert.Single(upstream.Paths);
        Assert.Equal(1, router.ForwardedWarmups);
        Assert.True(StageOnlyModelRouter.CapIsAtMost("{\"max_tokens\":16}", 16));
        Assert.False(StageOnlyModelRouter.CapIsAtMost("{\"messages\":[]}", 16));

        var capped = new Upstream();
        using var cappedClient = new HttpClient(new StageOnlyModelRouter(capped, _ => "{}", maxWarmupCalls: 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cappedClient.SendAsync(Warm(4000), ct));   // tetto di token superato: non inoltrato
        Assert.Empty(capped.Paths);

        var strict = new StageOnlyModelRouter(new Upstream(), _ => "{}");                               // default: nessun riscaldamento
        using var strictClient = new HttpClient(strict);
        await Assert.ThrowsAsync<InvalidOperationException>(() => strictClient.SendAsync(Warm(16), ct));
    }

    [Fact]
    public async Task The_production_client_warm_up_request_is_accepted_by_the_router()
    {
        // Il corpo REALE prodotto dal client di produzione (nessuna rete: il modello a monte e finto) deve rispettare il tetto di 16 token.
        var upstream = new Upstream();
        var router = new StageOnlyModelRouter(upstream, _ => "{}", maxWarmupCalls: 1);
        var options = new bProjects.App.AI.AiOptions { Endpoint = "http://dgx.internal:8080/v1/", ModelId = "m", ApiKey = "k", ContextWindowTokens = 4000, MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60 };
        using var client = new bProjects.App.AI.InternalModelClient(options, new NonOwningHandler(router));
        var warm = client.CreateChatOptions(temperature: 0, phase: bProjects.App.AI.AiPhase.Extraction);
        warm.MaxOutputTokens = 16;

        await Record.ExceptionAsync(() => client.ChatClient.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, StageOnlyModelRouter.WarmupText)], warm, TestContext.Current.CancellationToken));   // la risposta finta non e una chat valida: conta solo l'ammissione

        Assert.Empty(router.Violations);
        Assert.Equal(1, router.ForwardedWarmups);
    }

    [Fact]
    public async Task Non_generative_requests_are_forwarded_counted_and_attributed_to_the_phase_of_the_run()
    {
        var upstream = new Upstream();
        var router = new StageOnlyModelRouter(upstream, _ => "{}");
        using var client = new HttpClient(router);
        var ct = TestContext.Current.CancellationToken;

        await client.PostAsync("http://dgx.internal/tokenize", new StringContent("{}"), ct);          // prima della gerarchia
        await client.SendAsync(Chat(Leaf), ct);
        await client.PostAsync("http://dgx.internal/tokenize", new StringContent("{}"), ct);          // dentro la gerarchia? no: dopo l'unica risposta simulata = recupero e misura
        await client.SendAsync(Chat(Stage), ct);
        await client.PostAsync("http://dgx.internal/tokenize", new StringContent("{}"), ct);          // dalla richiesta dello stadio

        var phases = StageOnlyModelRouter.ByPhase(router.Calls).Where(p => p.Path == "/tokenize").ToDictionary(p => p.Phase, p => p.Requests);
        Assert.Equal(1, phases["pre-hierarchy"]);
        Assert.Equal(1, phases["retrieval-and-measure"]);
        Assert.Equal(1, phases["stage"]);
        Assert.Equal(["/tokenize", "/tokenize", "/tokenize", "/v1/chat/completions"], upstream.Paths.Order(StringComparer.Ordinal));
        Assert.Empty(router.Violations);
    }
}
