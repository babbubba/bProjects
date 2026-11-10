using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>003 T027 (DET, nessun servizio): il dataset di sviluppo del confronto A/B è congelato, i suoi gold si risolvono in un solo blocco e i calcoli del confronto sono corretti.</summary>
public sealed class RerankAbDatasetTests
{
    private static readonly string[] Categories = ["explicit-doc", "section-ref", "implicit-unique", "similar-multi"];

    [Fact]
    public void The_development_set_matches_its_frozen_manifest()
    {
        Assert.Empty(AbEvaluation.VerifyFrozen(AbEvaluation.DevSet));
    }

    [Fact]
    public void Every_gold_finding_resolves_to_exactly_one_block_of_its_file_and_the_cases_are_well_formed()
    {
        var cases = AbEvaluation.LoadCases(AbEvaluation.DevSet);
        var files = AbEvaluation.DocumentFiles(AbEvaluation.DevSet);
        var blocks = files.ToDictionary(
            f => f,
            f => (IReadOnlyList<(Guid, string)>)[.. EvaluationCorpus.Parse(Path.Combine(AbEvaluation.SetRoot(AbEvaluation.DevSet), f)).Select((b, i) => (EvaluationCorpus.BlockId(AbEvaluation.DevSet, f, i), b.Text))]);

        Assert.Equal(cases.Count, cases.Select(c => c.Id).Distinct().Count());
        Assert.All(cases, c =>
        {
            Assert.Contains(c.Category, Categories);
            Assert.NotEmpty(c.Gold);
            Assert.All(c.Gold, g => Assert.NotEqual(Guid.Empty, RetrievalGold.Resolve(blocks[g.File], g.Contains)));
        });
    }

    [Fact]
    public void The_set_covers_queries_with_and_without_a_document_reference_similar_documents_and_identical_sentences_in_different_documents()
    {
        var cases = AbEvaluation.LoadCases(AbEvaluation.DevSet);

        Assert.True(cases.Count(c => c.MentionsDocument) >= 10 && cases.Count(c => !c.MentionsDocument) >= 10);
        Assert.All(Categories, category => Assert.Contains(cases, c => c.Category == category));
        Assert.All(cases.Where(c => c.Category == "similar-multi"), c => Assert.True(c.Gold.Select(g => g.File).Distinct().Count() >= 2));   // più documenti, risposte diverse
        var texts = AbEvaluation.DocumentFiles(AbEvaluation.DevSet).SelectMany(f => EvaluationCorpus.Parse(Path.Combine(AbEvaluation.SetRoot(AbEvaluation.DevSet), f)).Select(b => (File: f, b.Text)))
            .Where(t => t.Text.Contains("penali per ritardata consegna sono dello 0,5", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, texts.Select(t => t.File).Distinct().Count());   // la stessa frase in due documenti: solo il contesto le distingue
        Assert.Equal(texts[0].Text, texts[1].Text);
    }

    [Fact]
    public void Every_set_delivered_by_a_reviewer_is_frozen_independent_well_formed_and_resolves_all_its_gold()
    {
        // Nessun insieme consegnato ⇒ nessuna verifica (il test non passa «per vuoto» su dati inesistenti: verifica solo ciò che esiste).
        foreach (var set in AbEvaluation.SchemaSets())
        {
            Assert.Empty(AbEvaluation.VerifyFrozen(set));
            Assert.True(AbEvaluation.Authorship(set).Independent, $"{set}: dichiarazione di indipendenza assente o incompleta");
            var cases = AbEvaluation.LoadCases(set);
            Assert.InRange(cases.Count, 50, 100);
            Assert.Equal(cases.Count, cases.Select(c => c.Id).Distinct().Count());
            var blocks = AbEvaluation.DocumentFiles(set).ToDictionary(
                f => f, f => (IReadOnlyList<(Guid, string)>)[.. EvaluationCorpus.Parse(Path.Combine(AbEvaluation.SetRoot(set), f)).Select((b, i) => (EvaluationCorpus.BlockId(set, f, i), b.Text))]);
            Assert.All(cases.SelectMany(c => c.Gold), g => Assert.NotEqual(Guid.Empty, RetrievalGold.Resolve(blocks[g.File], g.Contains)));
            Assert.Contains(cases, c => c.MentionsDocument);
            Assert.Contains(cases, c => !c.MentionsDocument);
        }
    }

    [Fact]
    public void The_development_set_is_not_declared_independent()
    {
        Assert.False(AbEvaluation.Authorship(AbEvaluation.DevSet).Independent);
    }

    [Fact]
    public void A_modified_added_or_missing_file_is_reported_by_the_frozen_check()
    {
        var copy = Path.Combine(Path.GetTempPath(), "bp-ab-" + Guid.NewGuid().ToString("N"));
        var set = Path.GetFileName(copy);
        try
        {
            Directory.CreateDirectory(copy);
            foreach (var f in Directory.GetFiles(AbEvaluation.SetRoot(AbEvaluation.DevSet)))
            {
                File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
            }

            // SetRoot è relativa alla cartella di output: si verifica la stessa logica puntando alla copia con un percorso relativo
            var relative = Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "Retrieval", "Evaluation"), copy);
            Assert.Empty(AbEvaluation.VerifyFrozen(relative));
            File.AppendAllText(Path.Combine(copy, "cases.json"), " ");
            File.WriteAllText(Path.Combine(copy, "nuovo.txt"), "x");
            File.Delete(Path.Combine(copy, "verbale-collaudo-orione.md"));

            var problems = AbEvaluation.VerifyFrozen(relative);

            Assert.Contains("file modificato dopo il congelamento: cases.json", problems);
            Assert.Contains("file non congelato: nuovo.txt", problems);
            Assert.Contains("file congelato mancante: verbale-collaudo-orione.md", problems);
            _ = set;
        }
        finally
        {
            if (Directory.Exists(copy))
            {
                Directory.Delete(copy, recursive: true);
            }
        }
    }

    [Fact]
    public void Line_endings_do_not_change_the_frozen_hash()
    {
        var path = Path.Combine(Path.GetTempPath(), "bp-lf-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "a\nb\n");
            var lf = AbEvaluation.Sha256Lf(path);
            File.WriteAllText(path, "a\r\nb\r\n");

            Assert.Equal(lf, AbEvaluation.Sha256Lf(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", TestCategories.Integration)]
    public async Task The_development_set_indexes_and_both_variants_rerank_the_same_candidates_with_provenance_sent_only_in_B()
    {
        await using var database = await RetrievalDb.CreateAsync();
        var scenario = new EvalScenario(AbEvaluation.DevSet, Path.Combine("..", "Evaluation", AbEvaluation.DevSet), [.. AbEvaluation.DocumentFiles(AbEvaluation.DevSet)], [], null);
        var server = new FakeRerankServer { Scores = (_, docs) => [.. docs.Select((_, i) => (double)i)] };
        using var rig = new RetrievalRig(rerankServer: server);
        var fixture = await RetrievalEvaluationTests.ScenarioFixture.SetupAsync(database, rig, scenario);
        await using var db = database.NewContext();
        var scope = await RetrievalScope.FromSnapshotAsync(db, await fixture.SnapshotAsync(excluded: null), fixture.IndexConfigHash, TestContext.Current.CancellationToken);
        var result = await rig.Search(db).SearchAsync(scope, "garanzia sulle apparecchiature", RetrievalPolicy.V1, TestContext.Current.CancellationToken);
        Assert.NotEmpty(result.Fused);

        string[] Sent() => [.. System.Text.Json.JsonDocument.Parse(server.Bodies.Last()).RootElement.GetProperty("documents").EnumerateArray().Select(e => e.GetString()!)];
        var orderA = await rig.Reranking(db).OrderAsync("garanzia sulle apparecchiature", result.Fused, RetrievalPolicy.V1, TestContext.Current.CancellationToken);
        var sentA = Sent();
        var orderB = await rig.Reranking(db).OrderAsync("garanzia sulle apparecchiature", result.Fused, RetrievalPolicy.V2, TestContext.Current.CancellationToken);
        var sentB = Sent();

        Assert.Equal(orderA.Order(), orderB.Order());   // stessi candidati
        Assert.DoesNotContain(sentA, d => d.StartsWith("Documento: ", StringComparison.Ordinal));
        Assert.All(sentB, d => Assert.StartsWith("Documento: ", d));
        Assert.Contains(sentB, d => d.Contains("\nSezione: ", StringComparison.Ordinal));   // i file Markdown hanno il percorso dei titoli
        Assert.Contains(sentB, d => d.StartsWith("Documento: offerta-commerciale-orione.md\nSezione: Offerta commerciale Orione > Garanzia\n", StringComparison.Ordinal));
        Assert.Equal(sentA.Length, sentB.Length);
        Assert.All(sentA.Zip(sentB), t => Assert.EndsWith(t.First, t.Second));   // il testo del blocco è intero in entrambe
    }

    // ---- calcoli del confronto

    private static QueryMetrics Metrics(string id, params int?[] positions) => EvaluationMetrics.ForQuery(id, positions);

    private static AbQuery Query(string id, int? rrf, int? a, int? b, bool mentions = false, double msA = 100, double msB = 110) =>
        new("S", new AbCase(id, "q", "implicit-unique", mentions, true, []), Metrics(id, rrf), Metrics(id, a), Metrics(id, b), msA, msB);

    [Fact]
    public void Regressions_are_queries_right_in_A_and_wrong_in_B_and_improvements_the_opposite()
    {
        AbQuery[] queries = [Query("both", 1, 1, 1), Query("regress", 1, 1, 2), Query("improve", 3, 2, 1), Query("neither", 2, 2, 3)];

        Assert.Equal(["regress"], AbEvaluation.Regressions(queries).Select(q => q.Case.Id));
        Assert.Equal(["improve"], AbEvaluation.Improvements(queries).Select(q => q.Case.Id));
    }

    [Fact]
    public void Groups_aggregate_hit_and_recall_for_rrf_A_and_B_over_the_same_queries()
    {
        var group = AbEvaluation.Group("g", [Query("1", 1, 1, 1), Query("2", 2, 2, 1), Query("3", 3, 6, 2), Query("4", 1, 1, 1)]);

        Assert.Equal(4, group.Queries);
        Assert.Equal((0.5, 0.5, 0.75), (group.Rrf.Hit[1], group.A.Hit[1], group.B.Hit[1]));
        Assert.Equal((1.0, 0.75, 1.0), (group.Rrf.Recall[5], group.A.Recall[5], group.B.Recall[5]));
    }

    [Fact]
    public void Latency_percentiles_use_the_nearest_rank_and_an_empty_series_is_zero()
    {
        var latency = AbEvaluation.Latency([.. Enumerable.Range(1, 20).Select(i => (double)i)]);

        Assert.Equal((20.0, 10.0, 19.0, 20.0), (latency["n"], latency["p50"], latency["p95"], latency["max"]));
        Assert.Equal(0, AbEvaluation.Latency([])["p95"]);
    }

    [Theory]
    [InlineData("Quanto dura la garanzia secondo il verbale di collaudo?", true)]
    [InlineData("Cosa dice la Scheda B sul termine di pagamento?", true)]
    [InlineData("Il progetto richiede la gestione dei resi?", false)]
    [InlineData("Quanto si attende per ricevere i ricambi ordinari?", false)]
    public void The_document_reference_heuristic_for_the_historical_gold_is_a_keyword_match(string query, bool expected) =>
        Assert.Equal(expected, AbEvaluation.MentionsDocumentHeuristic(query));
}
