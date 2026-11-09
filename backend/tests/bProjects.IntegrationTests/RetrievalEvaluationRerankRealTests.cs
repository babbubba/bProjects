using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using bProjects.App.Persistence;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>Servizi REALI del recupero con riordino (003 T026): embedding e riordino sullo stesso endpoint/chiave del router; handler HTTP di default.</summary>
internal sealed class RealRerankRig : IRetrievalRig, IDisposable
{
    /// <summary>Id del riordino nel router; sovrascrivibile con BPROJECTS_EVAL_RERANKER_MODEL_ID. Non cambia i default di prodotto.</summary>
    public static string RerankerModelId => Environment.GetEnvironmentVariable("BPROJECTS_EVAL_RERANKER_MODEL_ID") is { Length: > 0 } id ? id : "bge-reranker-v2-m3";

    public RealRerankRig(string endpoint, string apiKey)
    {
        Options = new RetrievalOptions
        {
            Enabled = true,
            RuntimeCheckSeconds = 600,
            TimeoutSeconds = 120,
            Embedding = { Endpoint = endpoint, ApiKey = apiKey, ModelId = RealRetrievalRig.ModelId },
            Reranker = { Endpoint = endpoint, ApiKey = apiKey, ModelId = RerankerModelId, Artifact = "bge-reranker-v2-m3-Q5_K_M.gguf" },
        };
        Embedding = new EmbeddingClient(Options);
        Reranker = new RerankClient(Options);
        Readiness = new RetrievalModelReadiness(Options, Embedding, reranker: Reranker);
    }

    public RetrievalOptions Options { get; }

    public EmbeddingClient Embedding { get; }

    public RerankClient Reranker { get; }

    public RetrievalModelReadiness Readiness { get; }

    public RetrievalIndexBuilder Builder(AppDbContext db) => new(db, Embedding, Readiness, Options);

    public HybridSearch Search(AppDbContext db) => new(db, Embedding, Readiness);

    public CandidateReranker Reranking(AppDbContext db) => new(db, Reranker, Readiness);

    public RetrievalPipeline Pipeline(AppDbContext db) => new(db, Search(db), Reranking(db), Readiness);

    public void Dispose()
    {
        Readiness.Dispose();
        Embedding.Dispose();
        Reranker.Dispose();
    }
}

/// <summary>
/// 003 T026 — MISURA REALE DEL RIORDINO (trait <c>Category=RetrievalEvalReal</c> e <c>Integration</c>). Opt-in: serve l'opt-in esplicito dei test DGX (altrimenti ignorato,
/// nessuna rete) e <c>BPROJECTS_TEST_POSTGRES</c>. Da eseguire SOLO con autorizzazione esplicita del committente. Contatta embedding <c>bge-m3</c> e riordino
/// <c>bge-reranker-v2-m3</c> del router (<c>/v1/models</c>, <c>/v1/embeddings</c>, <c>/v1/rerank</c>): nessun LLM generativo, nessuna richiesta di caricamento/scaricamento
/// esplicita, nessun cambio di modello, nessun troncamento dei testi, nessuna soglia sui punteggi. Stessi corpora, query e gold di T019; politica <see cref="RetrievalPolicy.V1"/>.
/// Uscite: <c>baseline-m2.json</c> con <c>BPROJECTS_EVAL_WRITE_BASELINE=1</c>; rapporto <c>M2-real.md</c> in <c>BPROJECTS_EVAL_OUT</c> se impostata. Nessun giudizio di gate: è T027.
/// </summary>
public sealed class RetrievalEvaluationRerankRealTests
{
    private const string BaselineM1 = "baseline-m1.json";
    private const string BaselineM2 = "baseline-m2.json";
    private const string WriteBaselineVariable = "BPROJECTS_EVAL_WRITE_BASELINE";
    private const string OutputVariable = "BPROJECTS_EVAL_OUT";
    private const double NonRegressionPoints = 2.0;
    private static readonly int[] Ks = [1, 5, 10, 20, 50];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record CorpusResult(string Name, AggregateMetrics Rrf, AggregateMetrics Rerank, int Queries, int QueriesWithDocumentFindings);

    [Fact]
    [Trait("Category", "RetrievalEvalReal")]
    [Trait("Category", TestCategories.Integration)]
    public async Task The_real_reranker_measurement_produces_the_M2_report_and_baseline()
    {
        var endpoint = TestEnvironment.RequireDgx();   // Skipped (non PASS) senza l'opt-in
        var apiKey = TestEnvironment.DgxApiKey;
        Assert.SkipWhen(apiKey is null, $"{TestEnvironment.DgxApiKeyVariable} non impostata.");
        Assert.Empty(RetrievalGold.Verify(RetrievalGold.Root));
        var outDirectory = OutputDirectory();
        var log = (string message) => Log(outDirectory, message);
        var policy = RetrievalPolicy.V1;

        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RealRerankRig(endpoint.ToString(), apiKey!);

        // ---- readiness dell'embedding (come M1): si osserva; un'eventuale prima richiesta di contenuto autorizzata la carica on-demand nel router (nessun load esplicito).
        var embeddingInitial = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);
        log($"embedding, readiness iniziale: {Describe(embeddingInitial)}");
        if (embeddingInitial.State is RetrievalModelState.ModelUnloaded or RetrievalModelState.ModelLoading)
        {
            var watch = Stopwatch.StartNew();
            await rig.Embedding.EmbedAsync("probe", Ct);
            log($"embedding: prima richiesta dopo «{embeddingInitial.State}»: {watch.Elapsed.TotalSeconds:F1} s");
            rig.Readiness.Invalidate();
        }

        var embeddingReady = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);
        log($"embedding, readiness: {Describe(embeddingReady)}");
        Assert.True(embeddingReady.IsReady, $"Embedding non pronto ({Describe(embeddingReady)}): ci si ferma con evidenza, nessun aggiramento.");
        Assert.Equal((rig.Options.Embedding.ModelId, rig.Options.Embedding.Artifact, 1024, Sha256Status.NotVerified), (embeddingReady.ModelId, embeddingReady.Artifact, embeddingReady.Dimension, embeddingReady.Sha256Status));

        // ---- indicizzazione a freddo dei tre corpora (embedding reali, come M1)
        var fixtures = new List<(EvalScenario Scenario, RetrievalEvaluationTests.ScenarioFixture Fixture)>();
        string indexConfigHash = string.Empty;
        foreach (var scenario in EvaluationCorpus.Scenarios)
        {
            log($"[{scenario.Name}] indicizzazione...");
            var fixture = await RetrievalEvaluationTests.ScenarioFixture.SetupAsync(database, rig, scenario);
            log($"[{scenario.Name}] indicizzato in {fixture.IndexSeconds:F1} s");
            fixtures.Add((scenario, fixture));
            indexConfigHash = fixture.IndexConfigHash;
        }

        // ---- (5) riordino NON raggiungibile: SOLO lettura. Lo stato iniziale è registrato; nessun load/unload. Se non Ready si registra l'esito del pipeline e si attende
        // (solo sonde di readiness, tetto del prodotto) senza forzare nulla; se non diventa Ready ci si ferma con evidenza.
        var rerankerInitial = await rig.Readiness.GetRerankerSnapshotAsync(Ct);
        log($"riordino, readiness iniziale: {Describe(rerankerInitial)}");
        string unreachableCase;
        if (rerankerInitial.IsReady)
        {
            unreachableCase = "non osservato: servizio raggiungibile";
        }
        else
        {
            unreachableCase = await ObservePipelineAsync(database, rig, fixtures[0], policy, rerankerInitial);
            log("caso riordino non pronto: " + unreachableCase);
            File.WriteAllText(Path.Combine(outDirectory, "reranker-not-ready.txt"), unreachableCase + "\n");
            rig.Readiness.InvalidateReranker();
            var waited = await rig.Readiness.WaitUntilRerankerReadyAsync(TimeSpan.FromMinutes(2), Ct);
            if (!waited.Snapshot.IsReady && waited.Snapshot.State == RetrievalModelState.ModelUnloaded)
            {
                // Il router carica il modello alla prima richiesta (come per l'embedding in T019): una richiesta di riordino ordinaria e minima, non un comando di load.
                log("riordino scaricato: una richiesta di riordino minima per il caricamento on-demand del router");
                try
                {
                    await rig.Reranker.RerankAsync("probe", ["probe a", "probe b"], Ct);
                }
                catch (RetrievalException failure)
                {
                    log($"richiesta minima di riordino fallita: {failure.Code}");
                }

                rig.Readiness.InvalidateReranker();
            }
        }

        var rerankerReady = await rig.Readiness.GetRerankerSnapshotAsync(Ct);
        log($"riordino, readiness: {Describe(rerankerReady)}");
        Assert.True(
            rerankerReady.IsReady,
            $"Riordino non pronto ({Describe(rerankerReady)}): ci si ferma con evidenza, nessun load/unload forzato. Caso osservato: {unreachableCase}");
        Assert.Equal((rig.Options.Reranker.ModelId, rig.Options.Reranker.Artifact), (rerankerReady.ModelId, rerankerReady.Artifact));

        // ---- misura: ricerca ibrida (RRF) + riordino per ogni query, tempi separati
        var corpora = new List<CorpusResult>();
        var allRrf = new List<QueryMetrics>();
        var allRerank = new List<QueryMetrics>();
        var searchMs = new List<double>();   // embedding della query via rete + lessicale + denso + RRF
        var rerankMs = new List<double>();   // solo il riordino (lettura dei testi dal DB inclusa)
        var totalMs = new List<double>();
        foreach (var (scenario, fixture) in fixtures)
        {
            await using var db = database.NewContext();
            var scope = await RetrievalScope.FromSnapshotAsync(db, await fixture.SnapshotAsync(excluded: null), fixture.IndexConfigHash, Ct);
            var search = rig.Search(db);
            var reranker = rig.Reranking(db);
            var required = fixture.RequiredBlocks();
            var rrfMetrics = new List<QueryMetrics>();
            var rerankMetrics = new List<QueryMetrics>();
            var cases = EvaluationCorpus.LoadCases(scenario.Name);
            foreach (var query in cases)
            {
                var watch = Stopwatch.StartNew();
                var result = await search.SearchAsync(scope, query.Query, policy, Ct);
                var searchElapsed = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                var reranked = await reranker.OrderAsync(query.Query, result.Fused, policy, Ct);
                var rerankElapsed = watch.Elapsed.TotalMilliseconds;
                searchMs.Add(searchElapsed);
                rerankMs.Add(rerankElapsed);
                totalMs.Add(searchElapsed + rerankElapsed);

                var rrfOrder = result.Fused.OrderBy(f => f.RrfRank).Select(f => f.BlockId).ToList();
                Assert.Equal(rrfOrder.Order(), reranked.Order());   // il riordino è una permutazione dei candidati: nessuno perso o inventato
                if (required[query.Id].Count > 0)
                {
                    rrfMetrics.Add(EvaluationMetrics.ForQuery(query.Id, rrfOrder, required[query.Id]));
                    rerankMetrics.Add(EvaluationMetrics.ForQuery(query.Id, reranked, required[query.Id]));
                }
            }

            var result1 = new CorpusResult(scenario.Name, EvaluationMetrics.Aggregate(rrfMetrics), EvaluationMetrics.Aggregate(rerankMetrics), cases.Count, rrfMetrics.Count);
            corpora.Add(result1);
            allRrf.AddRange(rrfMetrics);
            allRerank.AddRange(rerankMetrics);
            log($"[{scenario.Name}] Hit@1 RRF {result1.Rrf.Hit[1]:P1} → riordino {result1.Rerank.Hit[1]:P1}; Recall@10 RRF {result1.Rrf.Recall[10]:P1} → riordino {result1.Rerank.Recall[10]:P1}");
        }

        var overall = new CorpusResult("ALL", EvaluationMetrics.Aggregate(allRrf), EvaluationMetrics.Aggregate(allRerank), searchMs.Count, allRrf.Count);
        var finalReranker = await rig.Readiness.GetRerankerSnapshotAsync(Ct);
        var services = await DescribeServicesAsync(database);
        var metrics = Flatten(corpora, overall);
        var inputs = InputHashes();
        var m1 = LoadBaselineM1();
        var comparison = Compare(m1, corpora, overall, inputs);
        var latency = new Latency(Summarize(totalMs), Summarize(searchMs), Summarize(rerankMs));

        File.WriteAllText(Path.Combine(outDirectory, "m2-results.json"), ResultsJson(metrics, comparison, latency, rerankerInitial, finalReranker, unreachableCase));
        if (Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 })
        {
            File.WriteAllText(Path.Combine(outDirectory, "M2-real.md"), Markdown(corpora, overall, comparison, latency, embeddingReady, rerankerInitial, finalReranker, unreachableCase, services, policy, indexConfigHash));
        }

        if (Environment.GetEnvironmentVariable(WriteBaselineVariable) == "1")
        {
            WriteBaseline(metrics, inputs, indexConfigHash, policy, embeddingReady, finalReranker, services, latency, comparison);
        }

        log("fine");
    }

    // ---------------------------------------------------------------- caso «riordino non raggiungibile» (sola lettura)

    private static async Task<string> ObservePipelineAsync(
        RetrievalDb database, RealRerankRig rig, (EvalScenario Scenario, RetrievalEvaluationTests.ScenarioFixture Fixture) first, RetrievalPolicy policy, RetrievalModelSnapshot snapshot)
    {
        var sb = new StringBuilder($"stato iniziale del riordino: {Describe(snapshot)}; ");
        try
        {
            await using var db = database.NewContext();
            var scope = await RetrievalScope.FromSnapshotAsync(db, await first.Fixture.SnapshotAsync(excluded: null), first.Fixture.IndexConfigHash, Ct);
            var query = EvaluationCorpus.LoadCases(first.Scenario.Name)[0];
            var outcome = await rig.Pipeline(db).ExecuteAsync(scope, "eval-snapshot", [new RetrievalQuery(Guid.NewGuid(), query.Query)], [], policy, ct: Ct);
            sb.Append($"esito del pipeline: {outcome.Registry.Outcome}; stadio invocabile: {outcome.CanInvokeStage}; cause: [{string.Join(", ", outcome.Causes)}]");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            sb.Append($"pipeline non completato ({e.GetType().Name}: {e.Message})");
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- metriche e confronto

    private sealed record Latency(IReadOnlyDictionary<string, double> Total, IReadOnlyDictionary<string, double> Search, IReadOnlyDictionary<string, double> Rerank);

    private sealed record Delta(string Corpus, string Metric, double M1Rrf, double RunRrf, double RunRerank)
    {
        public double RerankVsM1Points => (RunRerank - M1Rrf) * 100;

        public double RrfVsM1Points => (RunRrf - M1Rrf) * 100;
    }

    private sealed record Comparison(bool Available, bool SameInputs, IReadOnlyList<string> Notes, IReadOnlyList<Delta> Deltas);

    private static Dictionary<string, double> Summarize(List<double> values)
    {
        var sorted = values.Order().ToList();
        return new()
        {
            ["n"] = sorted.Count,
            ["p50"] = Math.Round(Percentile(sorted, 0.5), 2),
            ["p95"] = Math.Round(Percentile(sorted, 0.95), 2),
            ["mean"] = Math.Round(sorted.Count == 0 ? 0 : sorted.Average(), 2),
            ["max"] = Math.Round(sorted.Count == 0 ? 0 : sorted[^1], 2),
        };
    }

    private static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static SortedDictionary<string, double> Flatten(IReadOnlyList<CorpusResult> corpora, CorpusResult overall)
    {
        var flat = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var c in corpora.Append(overall))
        {
            foreach (var (level, set) in new[] { ("block.rrf", c.Rrf), ("block.rerank", c.Rerank) })
            {
                foreach (var k in Ks)
                {
                    flat[$"{c.Name}.{level}.recall@{k}"] = Math.Round(set.Recall[k], 6);
                    flat[$"{c.Name}.{level}.hit@{k}"] = Math.Round(set.Hit[k], 6);
                }
            }

            flat[$"{c.Name}.queries.documentFindings"] = c.QueriesWithDocumentFindings;
        }

        return flat;
    }

    private static SortedDictionary<string, string> InputHashes()
    {
        var manifest = RetrievalGold.ReadManifest(RetrievalGold.Root);
        var inputs = manifest.Files.ToDictionary(f => "Gold/" + f.Path, f => f.Sha256, StringComparer.Ordinal);
        inputs["Evaluation/retrieval-cases.json"] = EvaluationCorpus.Sha256Of(EvaluationCorpus.CasesPath);
        return new(inputs, StringComparer.Ordinal);
    }

    private static Dictionary<string, double>? LoadBaselineM1(out Dictionary<string, string> inputs)
    {
        inputs = [];
        var path = Path.Combine(AppContext.BaseDirectory, "Retrieval", "Baseline", BaselineM1);
        if (!File.Exists(path))
        {
            return null;
        }

        using var json = JsonDocument.Parse(File.ReadAllBytes(path));   // sola lettura
        inputs = json.RootElement.GetProperty("inputHashes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        return json.RootElement.GetProperty("metrics").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
    }

    private static (Dictionary<string, double>? Metrics, Dictionary<string, string> Inputs) LoadBaselineM1()
    {
        var metrics = LoadBaselineM1(out var inputs);
        return (metrics, inputs);
    }

    private static Comparison Compare((Dictionary<string, double>? Metrics, Dictionary<string, string> Inputs) m1, IReadOnlyList<CorpusResult> corpora, CorpusResult overall, SortedDictionary<string, string> inputs)
    {
        if (m1.Metrics is null)
        {
            return new(false, false, [$"{BaselineM1} non trovato fra gli output: confronto non disponibile."], []);
        }

        var same = m1.Inputs.Count == inputs.Count && m1.Inputs.All(kv => inputs.TryGetValue(kv.Key, out var h) && h == kv.Value);
        var notes = new List<string> { same ? "Input (hash di gold, documenti e query) identici a quelli di M1." : "ATTENZIONE: gli hash degli input differiscono da M1: il confronto NON è sullo stesso insieme di query." };
        var deltas = new List<Delta>();
        foreach (var metric in new[] { "hit@1", "recall@1", "recall@5", "recall@10" })
        {
            var k = int.Parse(metric[(metric.IndexOf('@') + 1)..], CultureInfo.InvariantCulture);
            var isHit = metric.StartsWith("hit", StringComparison.Ordinal);
            double Pick(AggregateMetrics a) => isHit ? a.Hit[k] : a.Recall[k];
            double M1(string corpus) => m1.Metrics.GetValueOrDefault($"{corpus}.block.rrf.{metric}");
            foreach (var c in corpora)
            {
                deltas.Add(new(c.Name, metric, M1(c.Name), Pick(c.Rrf), Pick(c.Rerank)));
            }

            // Insieme complessivo: media di M1 pesata sul numero di query con riscontri documentali (stesso insieme, hash identici).
            var weight = corpora.Sum(c => c.QueriesWithDocumentFindings);
            var m1All = weight == 0 ? 0 : corpora.Sum(c => M1(c.Name) * c.QueriesWithDocumentFindings) / weight;
            deltas.Add(new("ALL", metric, m1All, Pick(overall.Rrf), Pick(overall.Rerank)));
        }

        return new(true, same, notes, deltas);
    }

    // ---------------------------------------------------------------- uscite

    private static string Pt(double points) => points.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " pt";

    private static string P(double v) => (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    private static string Markdown(
        IReadOnlyList<CorpusResult> corpora, CorpusResult overall, Comparison comparison, Latency latency, RetrievalModelSnapshot embedding,
        RetrievalModelSnapshot rerankerInitial, RetrievalModelSnapshot rerankerFinal, string unreachableCase, string services, RetrievalPolicy policy, string indexConfigHash)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# M2 — misura reale del riordino (003 T026)");
        sb.AppendLine();
        sb.AppendLine("Questo rapporto è una MISURA descrittiva: non contiene alcun giudizio di gate. Il gate di chiusura M2 è T027 e spetta al committente.");
        sb.AppendLine();
        sb.AppendLine("## Configurazione");
        sb.AppendLine();
        sb.AppendLine($"- Politica `{policy.Version}` (hash `{policy.Hash()}`): pool {policy.Pool}, riordino dei primi {policy.Rerank} della RRF, k RRF {policy.RrfK}. `IndexConfigHash` {indexConfigHash}.");
        sb.AppendLine($"- Embedding: {Describe(embedding)}.");
        sb.AppendLine($"- Riordino, stato iniziale: {Describe(rerankerInitial)}.");
        sb.AppendLine($"- Riordino, stato finale: {Describe(rerankerFinal)}.");
        sb.AppendLine($"- Servizi: {services}");
        sb.AppendLine($"- Caso «riordino non raggiungibile» (solo lettura, nessun load/unload): {unreachableCase}.");
        foreach (var note in comparison.Notes)
        {
            sb.AppendLine($"- {note}");
        }

        sb.AppendLine();
        sb.AppendLine("## Metriche a livello di blocco (solo riscontri documentali)");
        sb.AppendLine();
        sb.AppendLine("| Corpus | Query | Hit@1 RRF | Hit@1 riordino | Recall@1 RRF | Recall@1 riordino | Recall@5 RRF | Recall@5 riordino | Recall@10 RRF | Recall@10 riordino |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var c in corpora.Append(overall))
        {
            sb.AppendLine($"| {c.Name} | {c.QueriesWithDocumentFindings} | {P(c.Rrf.Hit[1])} | {P(c.Rerank.Hit[1])} | {P(c.Rrf.Recall[1])} | {P(c.Rerank.Recall[1])} | {P(c.Rrf.Recall[5])} | {P(c.Rerank.Recall[5])} | {P(c.Rrf.Recall[10])} | {P(c.Rerank.Recall[10])} |");
        }

        sb.AppendLine();
        sb.AppendLine($"## Confronto con M1 (`{BaselineM1}`, ordine RRF); riferimento T027: variazione peggiorativa oltre {NonRegressionPoints:F0} pt su Hit@1 e Recall@10");
        sb.AppendLine();
        if (comparison.Available)
        {
            sb.AppendLine("| Corpus | Metrica | M1 (RRF) | Questa esecuzione, RRF | Questa esecuzione, riordino | Riordino − M1 | RRF (stessa esecuzione) − M1 | Peggioramento > 2 pt (indicazione) |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---|");
            foreach (var d in comparison.Deltas)
            {
                var watched = d.Metric is "hit@1" or "recall@10";
                var worse = watched ? (d.RerankVsM1Points < -NonRegressionPoints ? "sì" : "no") : "n/a";
                sb.AppendLine($"| {d.Corpus} | {d.Metric} | {P(d.M1Rrf)} | {P(d.RunRrf)} | {P(d.RunRerank)} | {Pt(d.RerankVsM1Points)} | {Pt(d.RrfVsM1Points)} | {worse} |");
            }
        }
        else
        {
            sb.AppendLine("Confronto non disponibile.");
        }

        sb.AppendLine();
        sb.AppendLine($"## Latenza per query (n={latency.Total["n"]:F0}, tutte le query del gold, anche senza riscontri documentali)");
        sb.AppendLine();
        sb.AppendLine("| Fase | p50 ms | p95 ms | media ms | max ms |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var (name, set) in new[] { ("totale (embedding query + ricerca + riordino)", latency.Total), ("ricerca (embedding query + lessicale + denso + RRF)", latency.Search), ("solo riordino", latency.Rerank) })
        {
            sb.AppendLine($"| {name} | {set["p50"]:F1} | {set["p95"]:F1} | {set["mean"]:F1} | {set["max"]:F1} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Limiti noti");
        sb.AppendLine();
        foreach (var limit in new[]
        {
            "Dati sintetici del laboratorio (nessun dato cliente); query di un solo autore, tutte usate per la misura: nessuna indipendenza delle query, nessun corpus anonimizzato (SC-012).",
            "Parser finto a paragrafi (TXT, Markdown, DOCX), ~3,1k blocchi per corpus; non i ~460 blocchi Docling del laboratorio. Nessun PDF.",
            "Una evidenza per blocco: la mappatura è 1:1 per costruzione; le metriche sono a livello di BLOCCO e sui soli riscontri documentali (i riscontri Message sono esclusi da Recall@k).",
            "Servizi quantizzati (Q5_K_M) su router condiviso: i valori possono variare fra esecuzioni; SHA-256 dei pesi non verificato. Le latenze sono indicative, la prima richiesta può includere il caricamento on-demand del modello.",
            "Nessun troncamento dei testi e nessuna soglia sui punteggi del riordino. Il Recall è misurato solo sul gold annotato, senza garanzia di completezza.",
            "Nessun giudizio di gate: l'esito di T027 spetta al committente.",
        })
        {
            sb.AppendLine($"- {limit}");
        }

        return sb.ToString();
    }

    private static string ResultsJson(IReadOnlyDictionary<string, double> metrics, Comparison comparison, Latency latency, RetrievalModelSnapshot initial, RetrievalModelSnapshot final, string unreachableCase) =>
        JsonSerializer.Serialize(new
        {
            metrics,
            latencyMs = new { total = latency.Total, search = latency.Search, rerankOnly = latency.Rerank },
            comparison = new
            {
                comparison.Available,
                comparison.SameInputs,
                comparison.Notes,
                deltas = comparison.Deltas.Select(d => new { d.Corpus, d.Metric, d.M1Rrf, d.RunRrf, d.RunRerank, rerankVsM1Points = Math.Round(d.RerankVsM1Points, 3), rrfVsM1Points = Math.Round(d.RrfVsM1Points, 3) }),
            },
            rerankerReadiness = new { initial = initial.State.ToString(), final = final.State.ToString(), model = final.ModelId, artifact = final.Artifact },
            unreachableCase,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n";

    private static void WriteBaseline(
        SortedDictionary<string, double> metrics, SortedDictionary<string, string> inputs, string indexConfigHash, RetrievalPolicy policy,
        RetrievalModelSnapshot embedding, RetrievalModelSnapshot reranker, string services, Latency latency, Comparison comparison)
    {
        var manifest = RetrievalGold.ReadManifest(RetrievalGold.Root);
        var source = RetrievalEvaluationTests.FindUp(Path.Combine("backend", "tests", "bProjects.IntegrationTests", "Retrieval")) ?? throw new InvalidOperationException("Cartella dei test non trovata.");
        Directory.CreateDirectory(Path.Combine(source, "Baseline"));
        File.WriteAllText(Path.Combine(source, "Baseline", BaselineM2), JsonSerializer.Serialize(new
        {
            contract = "retrieval-baseline-1",
            mode = EvaluationReport.Real,
            note = "BASELINE DICHIARATA di M2 (T026): embedding bge-m3 e riordino bge-reranker-v2-m3 REALI, lessicale PG FTS italiano, pgvector con scansione esatta, politica retrieval-policy-1. Stessi query e hash di M1 (verificare 'sameInputsAsM1'). Metriche a livello di blocco sui soli riscontri documentali; non è un'accettazione né un giudizio di gate (T027, committente).",
            goldSourceCommit = manifest.SourceCommit,
            indexConfigHash,
            policyHash = policy.Hash(),
            policy = new { policy.Version, policy.Pool, policy.Rerank, policy.RrfK },
            inputHashes = inputs,
            sameInputsAsM1 = comparison.SameInputs,
            embeddingReadiness = new { state = embedding.State.ToString(), model = embedding.ModelId, artifact = embedding.Artifact, dimension = embedding.Dimension, sha256Status = embedding.Sha256Status.ToString() },
            rerankerReadiness = new { state = reranker.State.ToString(), model = reranker.ModelId, artifact = reranker.Artifact, sha256Status = reranker.Sha256Status.ToString() },
            services,
            latencyMs = new { total = latency.Total, search = latency.Search, rerankOnly = latency.Rerank },
            knownLimits = new[]
            {
                "dati sintetici, query di un solo autore, nessun corpus anonimizzato",
                "parser finto a paragrafi (non Docling), nessun PDF",
                "una evidenza per blocco (mappatura 1:1)",
                "servizi quantizzati su router condiviso: variabilità non deterministica; SHA-256 dei pesi non verificato",
                "nessun troncamento dei testi, nessuna soglia sui punteggi del riordino; nessun giudizio di gate",
            },
            measuredOn = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            metrics,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n");
    }

    private static string Describe(RetrievalModelSnapshot s) =>
        $"stato {s.State}; modello {s.ModelId}; artefatto atteso {s.ExpectedArtifact}, osservato {s.Artifact ?? "n/d"}; dimensione osservata {(s.Dimension is { } d ? d.ToString(CultureInfo.InvariantCulture) : "n/d")}; SHA-256 {s.Sha256Status}";

    private static async Task<string> DescribeServicesAsync(RetrievalDb database)
    {
        var postgres = ((string?)await database.ScalarAsync("SELECT version()"))?.Split(',')[0] ?? "n/d";
        var vector = (string?)await database.ScalarAsync("SELECT extversion FROM pg_extension WHERE extname = 'vector'") ?? "n/d";
        return $"{postgres}; pgvector {vector}; router llama.cpp interno (host non riportato): embedding bge-m3 (GGUF Q5_K_M) e riordino bge-reranker-v2-m3 (GGUF Q5_K_M)";
    }

    private static string OutputDirectory()
    {
        var directory = Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } custom ? custom : Path.Combine(AppContext.BaseDirectory, "Retrieval", "EvaluationOutput");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Log(string directory, string message)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss} {message}";
        File.AppendAllText(Path.Combine(directory, "progress-m2.log"), line + Environment.NewLine);
        TestContext.Current.SendDiagnosticMessage(line);
    }
}
