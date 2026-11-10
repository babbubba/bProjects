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
        var model = (TestEnvironment.DgxModelVariable, RetrievalGapCampaign.LlmModelId);

        Assert.Empty(RetrievalGapCampaign.ValidateEnvironment(Env(model)));
        Assert.Empty(RetrievalGapCampaign.ValidateEnvironment(Env(model, ("BPROJECTS_TEST_CTX_WINDOW", "32768"), ("BPROJECTS_TEST_ANALYSIS_MAX_OUTPUT", "16384"), ("BPROJECTS_TEST_ANALYSIS_THINKING", "0"), ("BPROJECTS_TEST_SAFETY_MARGIN", "1024"))));
        Assert.Contains("BPROJECTS_TEST_CTX_WINDOW", Assert.Single(RetrievalGapCampaign.ValidateEnvironment(Env(model, ("BPROJECTS_TEST_CTX_WINDOW", "131072")))));
        Assert.Contains("BPROJECTS_TEST_ANALYSIS_MAX_OUTPUT", Assert.Single(RetrievalGapCampaign.ValidateEnvironment(Env(model, ("BPROJECTS_TEST_ANALYSIS_MAX_OUTPUT", "8192")))));
        Assert.Contains("BPROJECTS_TEST_ANALYSIS_THINKING", Assert.Single(RetrievalGapCampaign.ValidateEnvironment(Env(model, ("BPROJECTS_TEST_ANALYSIS_THINKING", "1")))));
        Assert.Contains("BPROJECTS_TEST_SAFETY_MARGIN", Assert.Single(RetrievalGapCampaign.ValidateEnvironment(Env(model, ("BPROJECTS_TEST_SAFETY_MARGIN", "2048")))));
        Assert.Contains("BPROJECTS_EVAL_RERANKER_MODEL_ID", Assert.Single(RetrievalGapCampaign.ValidateEnvironment(Env(model, ("BPROJECTS_EVAL_RERANKER_MODEL_ID", "altro-reranker")))));
        Assert.Contains(TestEnvironment.DgxModelVariable, Assert.Single(RetrievalGapCampaign.ValidateEnvironment(Env())));
        Assert.Contains(RetrievalGapCampaign.LlmModelId, Assert.Single(RetrievalGapCampaign.ValidateEnvironment(Env((TestEnvironment.DgxModelVariable, "altro-modello")))));   // mai un altro LLM
        Assert.Equal("Qwen3.6-35B-Smart", RetrievalGapCampaign.LlmModelId);
        Assert.Equal(2, RetrievalGapCampaign.ValidateEnvironment(Env(("BPROJECTS_TEST_CTX_WINDOW", "1"), ("BPROJECTS_TEST_SAFETY_MARGIN", "1"))).Count - 1);   // + modello assente
    }

    [Fact]
    public void The_measured_runtime_profile_must_equal_the_frozen_configuration()
    {
        static DocumentRuntimeProfile Profile(int window = 32768, int output = 16384, int margin = 1024, bool thinking = false, string model = RetrievalGapCampaign.LlmModelId) =>
            new(AiPhase.Analysis, model, window, output, margin, thinking, true, null, "fp");

        Assert.Null(RetrievalGapCampaign.RejectProfile(Profile()));
        Assert.Contains("finestra", RetrievalGapCampaign.RejectProfile(Profile(window: 131072)));
        Assert.Contains("output", RetrievalGapCampaign.RejectProfile(Profile(output: 4096)));
        Assert.Contains("margine", RetrievalGapCampaign.RejectProfile(Profile(margin: 2048)));
        Assert.Contains("ragionamento", RetrievalGapCampaign.RejectProfile(Profile(thinking: true)));
        Assert.Contains("modello", RetrievalGapCampaign.RejectProfile(Profile(model: "altro")));
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
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(5, 0)]
    [InlineData(9, 0)]   // sotto i 10 casi del laboratorio il riferimento non è trasferibile: tolleranza zero
    [InlineData(10, 2)]
    [InlineData(11, 2)]   // arrotondamento per difetto: mai più permissivo
    [InlineData(14, 2)]
    [InlineData(15, 3)]
    [InlineData(20, 4)]
    public void Lost_conflicts_have_zero_tolerance_below_ten_cases_and_scale_the_lab_reference_from_ten_up(int observedCases, int allowed) =>
        Assert.Equal(allowed, RetrievalGapCampaign.AllowedLostConflicts(observedCases));

    [Fact]
    public void The_conflict_thresholds_follow_SC_003_and_upstream_loss_is_never_tolerated()
    {
        Assert.Equal((2, 10, 0), (RetrievalGapCampaign.LabConflictLostMax, RetrievalGapCampaign.LabConflictReferenceCases, RetrievalGapCampaign.MaxUpstreamLostConflicts));
        // i corpus della campagna hanno meno di 10 conflitti gold: tolleranza zero
        foreach (var scenario in RetrievalGapCampaign.Sequence)
        {
            var conflicts = RetrievalGapCampaign.BuildCorpus(scenario).Conflicts.Count;
            Assert.True(conflicts < RetrievalGapCampaign.LabConflictReferenceCases);
            Assert.Equal(0, RetrievalGapCampaign.AllowedLostConflicts(conflicts));
        }
    }

    [Fact]
    public void The_frozen_manifest_pins_the_llm_the_thresholds_and_the_refreeze_reason_without_touching_corpus_gold_or_expectations()
    {
        var manifest = JsonNode.Parse(RetrievalGapCampaign.BuildManifestJson())!;
        Assert.Equal(RetrievalGapCampaign.LlmModelId, manifest["config"]!["llm"]!["id"]!.GetValue<string>());
        Assert.NotEmpty(manifest["config"]!["llm"]!["unverified"]!.AsArray());
        Assert.Equal(0, manifest["thresholds"]!["lostConflicts"]!["belowThatTolerance"]!.GetValue<int>());
        Assert.Equal(0, manifest["thresholds"]!["lostConflicts"]!["upstream"]!.GetValue<int>());
        Assert.Equal(2, manifest["freeze"]!["version"]!.GetValue<int>());
        Assert.Equal(RetrievalGapCampaign.SupersededManifestSha256, manifest["freeze"]!["supersedes"]!.GetValue<string>());
        Assert.NotEqual(RetrievalGapCampaign.SupersededManifestSha256, RetrievalGapCampaign.FrozenManifestSha256);
        Assert.Contains("Nessuna campagna DGX", manifest["freeze"]!["reason"]!.GetValue<string>(), StringComparison.Ordinal);

        // corpus, gold, domande e attese: contenuto per scenario identico a quello della versione 1 (hash del testo e del gold, ricalcolati qui con lo stesso generatore, non letti dal manifesto)
        foreach (var scenario in RetrievalGapCampaign.Sequence)
        {
            var node = manifest["scenarios"]!.AsArray().Single(s => s!["Label"]!.GetValue<string>() == scenario.Label)!;
            var corpus = RetrievalGapCampaign.BuildCorpus(scenario);
            Assert.Equal(corpus.Documents.Select(d => d.PlainTextSha256), node["documents"]!.AsArray().Select(d => d!["PlainTextSha256"]!.GetValue<string>()));
            Assert.Equal(scenario.Expectations.Select(e => e.Question), node["questions"]!.AsArray().Select(q => q!.GetValue<string>()));
        }
    }

    // ================================================================ ordine, arresto e assenza di rilanci della campagna

    [Fact]
    public async Task The_campaign_runs_single_then_multi_in_a_single_entry_point()
    {
        Assert.Equal([RetrievalGapCampaign.Single, RetrievalGapCampaign.Multi], RetrievalGapCampaign.Sequence);
        var order = new List<string>();
        var results = await CampaignSequencer.RunAsync([new("Single", () => Record(order, "Single")), new("Multi", () => Record(order, "Multi"))]);
        Assert.Equal(["Single", "Multi"], order);
        Assert.All(results, r => Assert.Equal(CampaignStepStatus.Passed, r.Status));
    }

    [Theory]
    [InlineData("[STRUTTURALE] finestra diversa")]
    [InlineData("[COMPLETAMENTO] preparazione non Ready")]
    [InlineData("[COPERTURA] ledger non coperto")]
    public async Task A_blocking_failure_in_single_stops_the_campaign_without_running_or_retrying_multi_and_keeps_the_failure(string tag)
    {
        var calls = new Dictionary<string, int> { ["Single"] = 0, ["Multi"] = 0 };
        var persisted = new List<IReadOnlyList<CampaignStepResult>>();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => CampaignSequencer.RunAsync(
            [new("Single", () => { calls["Single"]++; throw new Xunit.Sdk.XunitException("T040: 1 verifiche fallite: " + tag); }), new("Multi", () => { calls["Multi"]++; return Task.CompletedTask; })],
            results => persisted.Add([.. results])));

        Assert.Equal(1, calls["Single"]);   // nessun retry
        Assert.Equal(0, calls["Multi"]);   // nessun altro corpus
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(failure);   // mai convertito in skip/PASS
        Assert.Contains(tag, failure.Message, StringComparison.Ordinal);
        Assert.Contains("NotRun", failure.Message, StringComparison.Ordinal);
        var last = persisted[^1];
        Assert.Equal([CampaignStepStatus.Blocked, CampaignStepStatus.NotRun], last.Select(r => r.Status));
        Assert.Contains(tag, last[0].Detail, StringComparison.Ordinal);   // evidenza del primo passo conservata
    }

    [Fact]
    public async Task A_non_assertion_error_in_single_is_a_block_and_a_merit_failure_lets_multi_run_once_while_the_failure_is_kept()
    {
        var multiCalls = 0;
        var blocked = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => CampaignSequencer.RunAsync(
            [new("Single", () => throw new InvalidOperationException("database non raggiungibile")), new("Multi", () => { multiCalls++; return Task.CompletedTask; })]));
        Assert.Equal(0, multiCalls);
        Assert.IsType<InvalidOperationException>(blocked.InnerException);   // l'errore originale resta raggiungibile
        Assert.Contains("database non raggiungibile", blocked.Message, StringComparison.Ordinal);

        var singleCalls = 0;
        multiCalls = 0;
        var states = new List<IReadOnlyList<CampaignStepResult>>();
        var merit = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => CampaignSequencer.RunAsync(
            [new("Single", () => { singleCalls++; throw new Xunit.Sdk.XunitException("(4) esito atteso Open"); }), new("Multi", () => { multiCalls++; return Task.CompletedTask; })], results => states.Add([.. results])));
        Assert.Equal((1, 1), (singleCalls, multiCalls));   // il criterio di merito non ferma Multi, ma il fallimento di Single resta
        Assert.Equal("(4) esito atteso Open", merit.Message);   // unico fallimento, nessun passo non eseguito: l eccezione originale, non un riepilogo
        Assert.Equal([CampaignStepStatus.Failed, CampaignStepStatus.Passed], states[^1].Select(r => r.Status));
    }

    [Fact]
    public async Task A_dynamic_skip_propagates_untouched_and_runs_nothing_else()
    {
        var multiCalls = 0;
        var skipped = await Assert.ThrowsAnyAsync<Exception>(() => CampaignSequencer.RunAsync(
            [new("Single", () => { throw new SkipTestException(); }), new("Multi", () => { multiCalls++; return Task.CompletedTask; })]));
        Assert.True(CampaignSequencer.IsSkip(skipped));
        Assert.Equal(0, multiCalls);
    }

    [Fact]
    public void The_real_campaign_has_one_test_runs_scenarios_with_their_own_isolated_state_and_never_uses_static_halt_state()
    {
        var campaign = typeof(RetrievalGapStageMafRealTests.Campaign);
        Assert.Single(campaign.GetMethods(), m => m.GetCustomAttribute<FactAttribute>() is not null);
        Assert.DoesNotContain(typeof(RetrievalGapStageMafRealTests.MultiScenario).GetMethods(), m => m.GetCustomAttribute<FactAttribute>() is not null);
        Assert.Equal(RetrievalGapCampaign.Single, (CampaignScenario)typeof(RetrievalGapStageMafRealTests).GetProperty("Scenario", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Activator.CreateInstance(campaign))!);
        Assert.Equal(RetrievalGapCampaign.Multi, (CampaignScenario)typeof(RetrievalGapStageMafRealTests).GetProperty("Scenario", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Activator.CreateInstance(typeof(RetrievalGapStageMafRealTests.MultiScenario)))!);
        Assert.Equal([RetrievalGapCampaign.Single, RetrievalGapCampaign.Multi], RetrievalGapCampaign.Sequence);
        Assert.DoesNotContain(typeof(RetrievalGapStageMafRealTests).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public), f => !f.IsLiteral && !f.IsInitOnly);   // nessuno stato statico mutabile
    }

    private static Task Record(List<string> order, string name)
    {
        order.Add(name);
        return Task.CompletedTask;
    }

    // ================================================================ latenza per Gap e misura opzionale

    private sealed class SkipTestException : Exception;

    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static RetrievalTimingLog.Entry Entry(string phase, Guid? gap, double startMs, double durationMs, bool faulted = false) =>
        new(phase, gap, T0.AddMilliseconds(startMs), T0.AddMilliseconds(startMs + durationMs), durationMs, faulted);

    private static RetrievalCallRecorder.Call Call(string kind, double endMs, double durationMs, string phase = "run") => new(kind, phase, [], durationMs, 200, T0.AddMilliseconds(endMs));

    [Fact]
    public void Latency_is_attributed_per_gap_from_its_own_windows_and_the_shared_phases_are_never_divided_among_gaps()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var entries = new[]
        {
            Entry(RetrievalTimingLog.PhaseReadiness, null, 0, 10),
            Entry(RetrievalTimingLog.PhaseIndexReady, null, 10, 5),
            Entry(RetrievalTimingLog.PhaseSearch, a, 20, 100),
            Entry(RetrievalTimingLog.PhaseSearch, b, 120, 80),
            Entry(RetrievalTimingLog.PhaseRerankerReady, null, 200, 7),
            Entry(RetrievalTimingLog.PhaseRerank, a, 210, 300),
            Entry(RetrievalTimingLog.PhaseRerank, b, 510, 200),
            Entry(RetrievalTimingLog.PhaseProvenance, null, 710, 20),
            Entry(RetrievalTimingLog.PhaseRegistry, null, 730, 40),
            Entry(RetrievalTimingLog.PhaseTotal, null, 0, 770),
        };
        var calls = new[]
        {
            Call("other", 5, 4), Call("embedding", 60, 50), Call("embedding", 170, 40), Call("rerank", 400, 250), Call("rerank", 650, 150),
            Call("embedding", 30, 99, phase: "index"),   // fase indice: mai attribuita
        };

        var report = LatencyReport.Build(entries, calls, [a, b]);
        var ga = report.PerGap[0];
        var gb = report.PerGap[1];
        Assert.Equal((100d, 300d, 400d), (ga.SearchMs, ga.RerankMs, ga.EndToEndMs));
        Assert.Equal((50d, 1, 250d, 1), (ga.EmbeddingHttpMs, ga.EmbeddingCalls, ga.RerankHttpMs, ga.RerankCalls));
        Assert.Equal((50d, 50d), (ga.PostgresAndFusionMs, ga.RerankApplicationMs));
        Assert.Equal((40d, 1, 150d, 1), (gb.EmbeddingHttpMs, gb.EmbeddingCalls, gb.RerankHttpMs, gb.RerankCalls));
        Assert.Equal((280d, false), (gb.EndToEndMs, gb.Incomplete));

        // le fasi condivise compaiono UNA volta, non per Gap; la composizione del pacchetto è separata; il totale è quello della pipeline
        Assert.Equal(10, report.SharedMs[RetrievalTimingLog.PhaseReadiness]);
        Assert.Equal(40, report.SharedPackageCompositionMs);
        Assert.Equal(770, report.TotalMs);
        Assert.DoesNotContain(RetrievalTimingLog.PhaseSearch, report.SharedMs.Keys);
        Assert.Equal(ga.EndToEndMs + gb.EndToEndMs + report.SharedMs.Values.Sum(), report.PerGap.Sum(g => g.EndToEndMs) + report.SharedMs.Values.Sum());
        Assert.Equal(2, report.Describe(id => id == a ? "A" : "B").Count(l => l.StartsWith("Gap", StringComparison.Ordinal)));
        Assert.Single(report.Describe(_ => "x"), l => l.Contains("CONDIVISE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_gap_without_candidates_may_skip_the_reranker_but_a_gap_with_candidates_may_not()
    {
        var withoutRerank = new GapLatency(Guid.NewGuid(), 100, 5, 50, 1, 0, 0, false);
        Assert.True(withoutRerank.IsMeasuredCompletely(0));    // nessun candidato: nessuna richiesta al reranker, caso legittimo
        Assert.False(withoutRerank.IsMeasuredCompletely(3));   // con candidati il riordino è obbligatorio
        Assert.True(new GapLatency(Guid.NewGuid(), 100, 300, 50, 1, 250, 1, false).IsMeasuredCompletely(3));
        Assert.False(new GapLatency(Guid.NewGuid(), 100, 5, 0, 0, 0, 0, false).IsMeasuredCompletely(0));   // l'embedding della query c'è sempre
        Assert.False(new GapLatency(Guid.NewGuid(), 100, 5, 50, 1, 0, 0, true).IsMeasuredCompletely(0));   // misura incompleta resta tale
    }

    [Fact]
    public void A_gap_without_a_complete_search_and_rerank_measure_is_flagged_incomplete_not_silently_zero()
    {
        var gap = Guid.NewGuid();
        Assert.True(LatencyReport.Build([Entry(RetrievalTimingLog.PhaseSearch, gap, 0, 10)], [], [gap]).PerGap.Single().Incomplete);
        Assert.True(LatencyReport.Build([Entry(RetrievalTimingLog.PhaseSearch, gap, 0, 10), Entry(RetrievalTimingLog.PhaseRerank, gap, 10, 10, faulted: true)], [], [gap]).PerGap.Single().Incomplete);
    }

    [Fact]
    public async Task The_timing_log_records_phase_gap_and_window_for_success_and_failure_without_changing_the_result()
    {
        var log = new RetrievalTimingLog();
        var gap = Guid.NewGuid();
        Assert.Equal(7, await log.MeasureAsync(RetrievalTimingLog.PhaseSearch, gap, async () => { await Task.Delay(5, TestContext.Current.CancellationToken); return 7; }));
        Assert.Equal(3, log.Measure(RetrievalTimingLog.PhaseRegistry, null, () => 3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => log.MeasureAsync<int>(RetrievalTimingLog.PhaseRerank, gap, () => throw new InvalidOperationException()));

        Assert.Equal([RetrievalTimingLog.PhaseSearch, RetrievalTimingLog.PhaseRegistry, RetrievalTimingLog.PhaseRerank], log.Entries.Select(e => e.Phase));
        Assert.Equal([gap, null, gap], log.Entries.Select(e => e.GapEvidenceId));
        Assert.Equal([false, false, true], log.Entries.Select(e => e.Faulted));
        Assert.All(log.Entries, e => Assert.True(e.EndedAt >= e.StartedAt && e.Milliseconds >= 0));
        Assert.True(log.Entries[0].Milliseconds >= 4);
    }


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

        var types = new[] { typeof(RetrievalGapStageMafRealTests.Campaign), typeof(RetrievalGapStageMafRealTests.MultiScenario) };
        foreach (var type in types)
        {
            var traits = type.GetCustomAttributes<TraitAttribute>(true).Select(t => (t.Name, t.Value)).ToList();
            Assert.Contains(("Category", TestCategories.MafReal), traits);
            Assert.Contains(("Campaign", "T040"), traits);
            Assert.Equal(RealParser.CollectionName, type.GetCustomAttributes<CollectionAttribute>(true).Single().Name);   // seriale con ogni altra prova reale
        }

        Assert.Single(typeof(RetrievalGapStageMafRealTests.Campaign).GetMethods(), m => m.GetCustomAttribute<FactAttribute>() is not null);   // un solo test reale per l'intera campagna
    }
}
