using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Retrieval;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T040 — verifiche OFFLINE e deterministiche dell'harness (nessun servizio, nessun DGX, nessuna rete): manifesto congelato e versionato, corpus e gold invariati, struttura delle cinque attese,
/// vocabolario d'estensione disgiunto dai corpus, rifiuto delle configurazioni incompatibili, classificazione dei fallimenti, registratore delle chiamate e dimensionamento del ledger rispetto al budget.
/// Non dimostrano il comportamento dei modelli reali (T040 resta aperto finché la campagna reale non passa).
/// </summary>
public sealed class RetrievalGapCampaignTests
{
    private const string WriteVariable = "BPROJECTS_T040_WRITE_MANIFEST";

    private static string Fold(string text) => LargeDocumentRealFlow.Fold(text);

    private static string ManifestFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "specs", "003-retrieval-evidence-search", "validation");
            if (Directory.Exists(candidate))
            {
                return Path.Combine(candidate, "T040-campaign-manifest.json");
            }
        }

        throw new DirectoryNotFoundException("specs/003-retrieval-evidence-search/validation non raggiungibile da questo albero di build.");
    }

    private static string FileContent() =>
        new JsonObject
        {
            ["campaign"] = RetrievalGapCampaign.Id,
            ["frozenSha256"] = RetrievalGapCampaign.ManifestSha256(),
            ["note"] = "Manifesto congelato PRIMA di ogni inferenza: corpus (hash del testo), gold (hash), domande, attese, soglie e configurazione. Rigenerabile solo con BPROJECTS_T040_WRITE_MANIFEST=1 e ri-congelamento esplicito della costante FrozenManifestSha256.",
            ["manifest"] = JsonNode.Parse(RetrievalGapCampaign.BuildManifestJson()),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

    [Fact]
    public void The_manifest_is_deterministic_and_matches_the_frozen_hash()
    {
        Assert.Equal(RetrievalGapCampaign.BuildManifestJson(), RetrievalGapCampaign.BuildManifestJson());
        Assert.Equal(RetrievalGapCampaign.FrozenManifestSha256, RetrievalGapCampaign.ManifestSha256());
    }

    [Fact]
    public void The_versioned_manifest_file_is_the_frozen_manifest()
    {
        var path = ManifestFile();
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            File.WriteAllText(path, FileContent());
        }

        Assert.True(File.Exists(path), $"manca {path}: generarlo con {WriteVariable}=1 e versionarlo");
        var stored = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(RetrievalGapCampaign.FrozenManifestSha256, stored["frozenSha256"]!.GetValue<string>());
        Assert.Equal(RetrievalGapCampaign.BuildManifestJson(), stored["manifest"]!.ToJsonString());
    }

    [Fact]
    public void The_campaign_reuses_the_T082_T083_generator_with_their_rules_and_the_unmodified_gold()
    {
        var single = RetrievalGapCampaign.BuildCorpus(RetrievalGapCampaign.Single);
        var multi = RetrievalGapCampaign.BuildCorpus(RetrievalGapCampaign.Multi);
        Assert.Equal(260, RetrievalGapCampaign.SingleBulkRules);
        Assert.Equal(90, RetrievalGapCampaign.MultiBulkRulesPerDocument);
        Assert.Equal(260, single.Documents.Sum(d => d.BulkRules.Count));
        Assert.Equal(3 * 90, multi.Documents.Sum(d => d.BulkRules.Count));
        Assert.All(single.Documents.Concat(multi.Documents), d => Assert.Empty(d.Fillers));
        Assert.Same(LargeDocumentCorpus.SingleGold, single.Gold);
        Assert.Same(LargeDocumentCorpus.MultiGold, multi.Gold);
        Assert.Same(LargeDocumentCorpus.SingleConflicts, single.Conflicts);
        Assert.Same(LargeDocumentCorpus.MultiConflicts, multi.Conflicts);

        // Deterministico: due costruzioni identiche (testo canonico).
        Assert.Equal(single.Documents.Select(d => d.PlainTextSha256), RetrievalGapCampaign.BuildCorpus(RetrievalGapCampaign.Single).Documents.Select(d => d.PlainTextSha256));
        Assert.Equal(multi.Documents.Select(d => d.PlainTextSha256), RetrievalGapCampaign.BuildCorpus(RetrievalGapCampaign.Multi).Documents.Select(d => d.PlainTextSha256));
    }

    [Fact]
    public void Every_scenario_annotates_exactly_the_five_required_expectations_before_the_run()
    {
        foreach (var scenario in new[] { RetrievalGapCampaign.Single, RetrievalGapCampaign.Multi })
        {
            var corpus = RetrievalGapCampaign.BuildCorpus(scenario);
            var text = Fold(string.Join("\n", corpus.Documents.Select(d => d.PlainText)));
            Assert.Equal(["present", "partial", "absent", "conflict", "excluded"], scenario.Expectations.Select(e => e.Kind));
            Assert.Equal(5, scenario.Expectations.Select(e => e.Question).Distinct().Count());
            Assert.All(scenario.Expectations, e => Assert.Contains(Fold(e.QuestionToken), Fold(e.Question), StringComparison.Ordinal));

            // Solo la risposta «presente» è AnsweredInSnapshot: tutto il resto Open (assente, parziale, conflitto, escluso).
            Assert.Equal(["present"], scenario.Expectations.Where(e => e.Outcome == "AnsweredInSnapshot").Select(e => e.Kind));
            Assert.All(scenario.Expectations.Where(e => e.Kind != "present"), e => Assert.Equal("Open", e.Outcome));
            Assert.Equal(["conflict"], scenario.Expectations.Where(e => e.ExpectOpenConflict).Select(e => e.Kind));
            Assert.Equal(2, scenario.Expectations.Single(e => e.Kind == "conflict").SupportQuotes.Length);

            // I riscontri attesi dei corpus T082/T083 esistono davvero nel loro testo; l'argomento «assente» no.
            foreach (var e in scenario.Expectations.Where(e => e.Kind is "present" or "conflict"))
            {
                Assert.All(e.SupportQuotes.SelectMany(q => q), q => Assert.Contains(Fold(q), text, StringComparison.Ordinal));
            }

            Assert.DoesNotContain("telefon", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_extension_documents_carry_the_partial_and_excluded_answers_and_their_vocabulary_is_disjoint_from_both_corpora()
    {
        var corpora = new[] { RetrievalGapCampaign.Single, RetrievalGapCampaign.Multi }
            .Select(s => Fold(string.Join("\n", RetrievalGapCampaign.BuildCorpus(s).Documents.Select(d => d.PlainText)))).ToList();
        foreach (var token in new[] { "formazion", "operatori", "tre giornate", "pegno", "attrezzature", "cinquantamila", "garanzie reali" })
        {
            Assert.All(corpora, c => Assert.DoesNotContain(token, c, StringComparison.Ordinal));
        }

        Assert.Equal(2, RetrievalGapCampaign.ExtensionDocuments.Count);
        Assert.Equal(["t040-parziale"], RetrievalGapCampaign.ExtensionDocuments.Where(d => !d.ExcludedByPm).Select(d => d.Key));
        Assert.Equal(["t040-escluso"], RetrievalGapCampaign.ExtensionDocuments.Where(d => d.ExcludedByPm).Select(d => d.Key));
        Assert.Contains("tre giornate", RetrievalGapCampaign.PartialDocument.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("costo", Fold(RetrievalGapCampaign.PartialDocument.Text), StringComparison.Ordinal);   // risposta parziale: la durata c'è, il costo no
        Assert.Contains("cinquantamila", RetrievalGapCampaign.ExcludedDocument.Text, StringComparison.Ordinal);

        // Le attese «parziale» ed «escluso» si basano su quei documenti e non sul corpus.
        foreach (var scenario in new[] { RetrievalGapCampaign.Single, RetrievalGapCampaign.Multi })
        {
            var partial = scenario.Expectations.Single(e => e.Kind == "partial");
            Assert.Contains(partial.SupportQuotes.Single().Single(), RetrievalGapCampaign.PartialDocument.Text, StringComparison.Ordinal);
            Assert.Contains(Fold(partial.QuestionToken), Fold(RetrievalGapCampaign.PartialDocument.Text), StringComparison.Ordinal);
            var excluded = scenario.Expectations.Single(e => e.Kind == "excluded");
            Assert.Contains(Fold(excluded.QuestionToken), Fold(RetrievalGapCampaign.ExcludedDocument.Text.Replace("Il pegno sulle attrezzature", "pegno sulle attrezzature")), StringComparison.Ordinal);
            Assert.Equal("NoAnswer", excluded.Reasons.Single());
        }
    }

    [Fact]
    public void The_campaign_configuration_is_explicit_coherent_with_the_T040_requirements_and_leaves_the_product_defaults_untouched()
    {
        Assert.Equal(15360, RetrievalGapCampaign.BudgetTokens);
        Assert.Equal(RetrievalGapCampaign.WindowTokens - RetrievalGapCampaign.OutputTokens - RetrievalGapCampaign.MarginTokens, RetrievalGapCampaign.BudgetTokens);
        Assert.Equal(RetrievalPolicy.Version1, RetrievalGapCampaign.PolicyVersion);

        // I valori fissati coincidono con i default di prodotto: nessun cambio di default, nessuna promozione della politica 2.
        var defaults = new RetrievalOptions();
        Assert.False(defaults.Enabled);
        Assert.Equal(RetrievalPolicy.Version1, defaults.PolicyVersion);
        Assert.Equal((RetrievalGapCampaign.EmbeddingModelId, RetrievalGapCampaign.EmbeddingArtifact, RetrievalGapCampaign.EmbeddingDimension), (defaults.Embedding.ModelId, defaults.Embedding.Artifact, defaults.Embedding.Dimension));
        Assert.Equal((RetrievalGapCampaign.RerankerModelId, RetrievalGapCampaign.RerankerArtifact), (defaults.Reranker.ModelId, defaults.Reranker.Artifact));
        Assert.Equal(DocumentAnalysisWorkflow.DefinitionVersionV7, "project-analysis-documents-v7");
    }

    [Fact]
    public void Incompatible_environments_are_rejected_before_any_upload_or_inference()
    {
        static Func<string, string?> Env(params (string Name, string Value)[] pairs) => name => pairs.FirstOrDefault(p => p.Name == name).Value;
        var model = (TestEnvironment.DgxModelVariable, "qwen-test");

        Assert.Empty(RetrievalGapCampaign.ValidateEnvironment(Env(model)));
        Assert.Empty(RetrievalGapCampaign.ValidateEnvironment(Env(model, ("BPROJECTS_TEST_CTX_WINDOW", "32768"), ("BPROJECTS_TEST_ANALYSIS_MAX_OUTPUT", "16384"), ("BPROJECTS_TEST_ANALYSIS_THINKING", "0"), ("BPROJECTS_TEST_SAFETY_MARGIN", "1024"))));
        Assert.Contains("BPROJECTS_TEST_CTX_WINDOW", Assert.Single(RetrievalGapCampaign.ValidateEnvironment(Env(model, ("BPROJECTS_TEST_CTX_WINDOW", "131072")))));
        Assert.Contains("BPROJECTS_TEST_ANALYSIS_MAX_OUTPUT", Assert.Single(RetrievalGapCampaign.ValidateEnvironment(Env(model, ("BPROJECTS_TEST_ANALYSIS_MAX_OUTPUT", "8192")))));
        Assert.Contains("BPROJECTS_TEST_ANALYSIS_THINKING", Assert.Single(RetrievalGapCampaign.ValidateEnvironment(Env(model, ("BPROJECTS_TEST_ANALYSIS_THINKING", "1")))));
        Assert.Contains("BPROJECTS_TEST_SAFETY_MARGIN", Assert.Single(RetrievalGapCampaign.ValidateEnvironment(Env(model, ("BPROJECTS_TEST_SAFETY_MARGIN", "2048")))));
        Assert.Contains("BPROJECTS_EVAL_RERANKER_MODEL_ID", Assert.Single(RetrievalGapCampaign.ValidateEnvironment(Env(model, ("BPROJECTS_EVAL_RERANKER_MODEL_ID", "altro-reranker")))));
        Assert.Contains(TestEnvironment.DgxModelVariable, Assert.Single(RetrievalGapCampaign.ValidateEnvironment(Env())));
        Assert.Equal(2, RetrievalGapCampaign.ValidateEnvironment(Env(("BPROJECTS_TEST_CTX_WINDOW", "1"), ("BPROJECTS_TEST_SAFETY_MARGIN", "1"))).Count - 1);   // + modello assente
    }

    [Fact]
    public void The_measured_runtime_profile_must_equal_the_frozen_configuration()
    {
        static DocumentRuntimeProfile Profile(int window = 32768, int output = 16384, int margin = 1024, bool thinking = false, string model = "qwen-test") =>
            new(AiPhase.Analysis, model, window, output, margin, thinking, true, null, "fp");

        Assert.Null(RetrievalGapCampaign.RejectProfile(Profile(), "qwen-test"));
        Assert.Contains("finestra", RetrievalGapCampaign.RejectProfile(Profile(window: 131072), "qwen-test"));
        Assert.Contains("output", RetrievalGapCampaign.RejectProfile(Profile(output: 4096), "qwen-test"));
        Assert.Contains("margine", RetrievalGapCampaign.RejectProfile(Profile(margin: 2048), "qwen-test"));
        Assert.Contains("ragionamento", RetrievalGapCampaign.RejectProfile(Profile(thinking: true), "qwen-test"));
        Assert.Contains("modello", RetrievalGapCampaign.RejectProfile(Profile(model: "altro"), "qwen-test"));
    }

    [Fact]
    public void A_failed_run_is_attributed_to_extraction_retrieval_or_the_gap_stage_from_its_typed_code()
    {
        Assert.StartsWith("RECUPERO", RetrievalGapCampaign.Classify(RetrievalException.EmbeddingUnavailable));
        Assert.StartsWith("RECUPERO", RetrievalGapCampaign.Classify(RetrievalException.IndexNotReady));
        Assert.StartsWith("RECUPERO", RetrievalGapCampaign.Classify(RetrievalException.RerankerUnavailable));
        Assert.StartsWith("ROUTING", RetrievalGapCampaign.Classify("gap_ledger_exceeds_input"));
        Assert.StartsWith("STADIO GAP", RetrievalGapCampaign.Classify("work_output_invalid"));
        Assert.StartsWith("ESTRAZIONE", RetrievalGapCampaign.Classify("document_extraction_failed"));
        Assert.Equal("codice assente", RetrievalGapCampaign.Classify(""));
    }

    [Theory]
    [InlineData(0, 10, true)]
    [InlineData(2, 10, true)]
    [InlineData(3, 10, false)]
    [InlineData(1, 5, true)]
    [InlineData(2, 5, false)]
    [InlineData(1, 4, false)]
    [InlineData(1, 6, true)]
    [InlineData(0, 0, true)]
    public void The_two_out_of_ten_thresholds_are_applied_in_proportion_to_the_cases_measured(int bad, int total, bool expected) =>
        Assert.Equal(expected, RetrievalGapCampaign.WithinPer10(bad, total, 2));


    [Theory]
    [InlineData("http://dgx.internal:8000/v1")]
    [InlineData("http://192.168.1.50:8000/v1/")]
    public void The_frozen_retrieval_configuration_passes_the_product_validator_and_resolves_the_real_clients_with_the_recorder(string endpoint)
    {
        var services = new ServiceCollection();
        services.AddOptions<RetrievalOptions>();
        RetrievalGapCampaign.AddRetrieval(services, endpoint, "chiave-di-prova", new RetrievalCallRecorder());
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RetrievalOptions>>().Value;

        Assert.True(new RetrievalOptionsValidator().Validate(null, options).Succeeded, string.Join("; ", new RetrievalOptionsValidator().Validate(null, options).Failures ?? []));
        Assert.True(options.Enabled);
        Assert.Equal(RetrievalPolicy.Version1, options.PolicyVersion);
        Assert.Equal((RetrievalGapCampaign.EmbeddingModelId, RetrievalGapCampaign.EmbeddingArtifact), (options.Embedding.ModelId, options.Embedding.Artifact));
        Assert.Equal((RetrievalGapCampaign.RerankerModelId, RetrievalGapCampaign.RerankerArtifact), (options.Reranker.ModelId, options.Reranker.Artifact));
        Assert.NotNull(provider.GetRequiredService<EmbeddingClient>());
        Assert.NotNull(provider.GetRequiredService<RerankClient>());
        Assert.NotNull(provider.GetRequiredService<RetrievalModelReadiness>());
        Assert.False(new RetrievalOptions().Enabled);   // il default di prodotto non è stato toccato
    }
    [Fact]
    public void The_retrieval_recorder_reads_embedding_and_rerank_bodies_and_attributes_latency_per_gap()
    {
        var (kind, inputs) = RetrievalCallRecorder.Describe("/v1/embeddings", """{"model":"m","input":["Domanda uno?","Domanda due?"]}""");
        Assert.Equal("embedding", kind);
        Assert.Equal(["Domanda uno?", "Domanda due?"], inputs);
        var (rerankKind, rerankInputs) = RetrievalCallRecorder.Describe("/v1/rerank", """{"model":"r","query":"Domanda uno?","documents":["a","b"]}""");
        Assert.Equal("rerank", rerankKind);
        Assert.Equal(["Domanda uno?"], rerankInputs);
        Assert.Equal("other", RetrievalCallRecorder.Describe("/v1/models", string.Empty).Kind);
        Assert.Empty(RetrievalCallRecorder.Describe("/v1/embeddings", "non json").Inputs);
    }

    [Fact]
    public void The_ledger_of_both_campaign_corpora_exceeds_the_stage_input_budget_so_that_v7_must_retrieve()
    {
        // Stima prudente in ECCESSO di capacità (4 car/token, più generosa di 2,5), sulla fascia bassa 0,75x del nominale: se anche così supera B, il recupero è atteso. Non sostituisce la misura reale:
        // se a campagna avviata il ledger entrasse in B, la prova fallisce con «sorgente exhaustive» invece di dichiarare un PASS.
        const double charsPerToken = 4.0;
        const int metadataChars = 150;
        const int fixedTokens = 4000;
        foreach (var scenario in new[] { RetrievalGapCampaign.Single, RetrievalGapCampaign.Multi })
        {
            var corpus = RetrievalGapCampaign.BuildCorpus(scenario);
            var ruleTokens = corpus.Documents.SelectMany(d => d.BulkRules).Sum(r => (2 * r.Length + metadataChars) / charsPerToken);
            var goldTokens = corpus.Gold.Where(g => g.DocKey != "pm").Sum(g => (2 * g.Sentence.Length + metadataChars) / charsPerToken);
            var tokens = ((ruleTokens + goldTokens) * 0.75) + fixedTokens;
            Assert.True(tokens > RetrievalGapCampaign.BudgetTokens * 1.1, $"{scenario.Label}: ledger stimato in difetto {tokens:F0} token contro B={RetrievalGapCampaign.BudgetTokens}");

            // La gerarchia si attiva: stima di output della chiamata singola oltre O (come nel dimensionamento T150, qui a O=16384).
            var evidences = (int)((corpus.Documents.Sum(d => d.BulkRules.Count) + corpus.Gold.Count(g => g.DocKey != "pm")) * 0.75);
            Assert.True(AnalysisOutputEstimate.Exceeds(Enumerable.Repeat(120, evidences), 5, 0, RetrievalGapCampaign.OutputTokens), $"{scenario.Label}: {evidences} evidenze non attivano la gerarchia");
        }
    }

    [Fact]
    public void Without_the_dgx_opt_in_the_campaign_is_skipped_never_passed_and_both_scenarios_share_one_serial_collection()
    {
        Assert.False(TestEnvironment.DgxAllowedFrom(_ => null));
        Assert.False(TestEnvironment.DgxAllowedFrom(name => name == TestEnvironment.DgxAllowVariable ? "true" : null));   // solo il valore esatto «1»
        Assert.Contains(TestEnvironment.DgxAllowVariable, TestEnvironment.DgxUnavailableReason(_ => null));

        var types = new[] { typeof(RetrievalGapStageMafRealTests.Single), typeof(RetrievalGapStageMafRealTests.Multi) };
        foreach (var type in types)
        {
            var traits = type.GetCustomAttributes<TraitAttribute>(true).Select(t => (t.Name, t.Value)).ToList();
            Assert.Contains(("Category", TestCategories.MafReal), traits);
            Assert.Contains(("Campaign", "T040"), traits);
            Assert.Equal(RealParser.CollectionName, type.GetCustomAttributes<CollectionAttribute>(true).Single().Name);   // seriale con ogni altra prova reale
            Assert.Single(type.GetMethods(), m => m.GetCustomAttribute<FactAttribute>() is not null);
        }
    }
}
