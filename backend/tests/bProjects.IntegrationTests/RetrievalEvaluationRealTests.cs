using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.Persistence;
using bProjects.App.Retrieval;
using Npgsql;
using NpgsqlTypes;

namespace bProjects.IntegrationTests;

/// <summary>Servizi REALI del recupero (003 T019): <see cref="EmbeddingClient"/> e <see cref="RetrievalModelReadiness"/> con l'handler HTTP di default contro l'endpoint del router; nessun riordino.</summary>
internal sealed class RealRetrievalRig : IRetrievalRig, IDisposable
{
    public RealRetrievalRig(string endpoint, string apiKey)
    {
        Options = new RetrievalOptions
        {
            Enabled = true,
            RuntimeCheckSeconds = 600,
            TimeoutSeconds = 120,   // la prima richiesta può includere il caricamento on-demand del modello da parte del router
            Embedding = { Endpoint = endpoint, ApiKey = apiKey },   // ModelId bge-m3, artefatto bge-m3-Q5_K_M.gguf, dimensione 1024: default di RetrievalOptions (D-5)
        };
        Embedding = new EmbeddingClient(Options);
        Readiness = new RetrievalModelReadiness(Options, Embedding);
    }

    public RetrievalOptions Options { get; }

    public EmbeddingClient Embedding { get; }

    public RetrievalModelReadiness Readiness { get; }

    public RetrievalIndexBuilder Builder(AppDbContext db) => new(db, Embedding, Readiness, Options);

    public HybridSearch Search(AppDbContext db) => new(db, Embedding, Readiness);

    public void Dispose()
    {
        Readiness.Dispose();
        Embedding.Dispose();
    }
}

/// <summary>
/// 003 T019 — PRIMA MISURA REALE e baseline (trait <c>Category=RetrievalEvalReal</c> e <c>Integration</c>). Opt-in: serve <c>BPROJECTS_TEST_DGX_ALLOW=1</c> (altrimenti ignorato con
/// messaggio, nessuna rete) e <c>BPROJECTS_TEST_POSTGRES</c>. Contatta SOLO l'embedding <c>bge-m3</c> del router (<c>/v1/models</c>, <c>/v1/embeddings</c>): nessun LLM generativo, nessun
/// riordino, nessun caricamento/scaricamento esplicito, nessun cambio di modello. Stesso harness, corpus, query e gold congelato di T018 (<see cref="RetrievalEvaluationTests"/>).
/// Uscite in <c>BPROJECTS_EVAL_OUT</c> (rapporto, supplemento (a)-(e), avanzamento); con <c>BPROJECTS_EVAL_WRITE_BASELINE=1</c> scrive <c>baseline-m1.json</c>, altrimenti, se esiste, confronta
/// (regressione ≤ 2 punti sullo stesso insieme di input). La valutazione/gate T020 NON è eseguita qui.
/// </summary>
public sealed class RetrievalEvaluationRealTests
{
    private const int Pool = 50;
    private const string BaselineFile = "baseline-m1.json";
    private const string WriteBaselineVariable = "BPROJECTS_EVAL_WRITE_BASELINE";
    private const string OutputVariable = "BPROJECTS_EVAL_OUT";
    private const double MaxEstimatedIndexMinutes = 90;
    private const int Rounds = 3;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record DenseVariant(string Name, string[] Settings);

    private sealed record DenseOutcome(string Variant, bool PlanUsesHnsw, string PlanSummary, double P50, double P95, double Mean, int Samples, double Overlap10, double Overlap50, int ShortResults, IReadOnlyList<(string Corpus, double Recall10, double Hit1, int Queries)> Gold);

    [Fact]
    [Trait("Category", "RetrievalEvalReal")]
    [Trait("Category", TestCategories.Integration)]
    public async Task The_real_bge_m3_embedding_measurement_produces_the_first_real_report_and_the_baseline()
    {
        var endpoint = TestEnvironment.RequireDgx();   // Skipped (non PASS) senza BPROJECTS_TEST_DGX_ALLOW=1
        var apiKey = TestEnvironment.DgxApiKey;
        Assert.SkipWhen(apiKey is null, $"{TestEnvironment.DgxApiKeyVariable} non impostata.");
        Assert.Empty(RetrievalGold.Verify(RetrievalGold.Root));
        var outDirectory = OutputDirectory();
        var log = (string message) => Log(outDirectory, message);

        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RealRetrievalRig(endpoint.ToString(), apiKey!);

        // ---- (e) readiness reale: osservata, mai dedotta. Se difforme: ci si ferma con evidenza, senza aggirare.
        var initial = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);
        log($"readiness iniziale: {Describe(initial)}");
        var warmup = new List<string>();
        double? warmupSeconds = null;
        if (initial.State is RetrievalModelState.ModelUnloaded or RetrievalModelState.ModelLoading)
        {
            // Router llama.cpp: il modello è «scaricato» a riposo e si carica alla prima richiesta di embedding AUTORIZZATA (nessuna chiamata di load/unload esplicita).
            // Si registra il tempo della prima richiesta, poi si risonda: la costruzione dell'indice procede solo con readiness «Ready».
            var watch = Stopwatch.StartNew();
            await rig.Embedding.EmbedAsync("probe", Ct);
            warmupSeconds = watch.Elapsed.TotalSeconds;
            warmup.Add($"prima richiesta di embedding dopo «{initial.State}»: {warmupSeconds:F1} s (caricamento on-demand del router incluso)");
            log(warmup[0]);
            rig.Readiness.Invalidate();
        }

        var ready = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);
        log($"readiness: {Describe(ready)}");
        if (!ready.IsReady)
        {
            WriteText(outDirectory, "readiness-fermata.txt", $"iniziale: {Describe(initial)}\nattuale: {Describe(ready)}\n");
        }

        Assert.True(ready.IsReady, $"Embedding non pronto ({Describe(ready)}): ci si ferma con evidenza, nessun aggiramento (ArtifactMismatch/ModelMissing/DimensionMismatch vanno riportati).");
        Assert.Equal((rig.Options.Embedding.ModelId, rig.Options.Embedding.Artifact, 1024, Sha256Status.NotVerified), (ready.ModelId, ready.Artifact, ready.Dimension, ready.Sha256Status));

        // ---- stima dei tempi prima dell'indicizzazione completa
        var allTexts = EvaluationCorpus.Scenarios.SelectMany(s => s.DocumentFiles.SelectMany(f => EvaluationCorpus.Parse(RetrievalGold.PathOf(s.Directory, f)).Select(b => b.Text))).ToList();
        var sample = allTexts.Where(t => t.Length > 20).Take(16).Select(IndexText.Of).ToList();
        var sampleWatch = Stopwatch.StartNew();
        await rig.Embedding.EmbedAsync(sample, Ct);
        var perBlockMs = sampleWatch.Elapsed.TotalMilliseconds / sample.Count;
        var estimateMinutes = perBlockMs * allTexts.Count / 60000;
        var estimate = $"stima: {allTexts.Count} blocchi, {perBlockMs:F1} ms/blocco su 16 blocchi di prova ⇒ ~{estimateMinutes:F1} min di indicizzazione";
        log(estimate);
        Assert.True(estimateMinutes <= MaxEstimatedIndexMinutes, $"{estimate}: oltre il tetto di {MaxEstimatedIndexMinutes} min, interrotto senza insistere.");

        // ---- indicizzazione a freddo e valutazione per corpus (stesso harness di T018, servizi reali)
        var corpora = new List<CorpusReport>();
        var fixtures = new List<(EvalScenario Scenario, RetrievalEvaluationTests.ScenarioFixture Fixture)>();
        var indexTimes = new List<(string Name, int Blocks, double Seconds)>();
        var queryMs = new List<double>();
        string indexConfigHash = string.Empty;
        double indexSeconds = 0, searchMs = 0;
        var searches = 0;
        foreach (var scenario in EvaluationCorpus.Scenarios)
        {
            log($"[{scenario.Name}] indicizzazione...");
            var fixture = await RetrievalEvaluationTests.ScenarioFixture.SetupAsync(database, rig, scenario);
            log($"[{scenario.Name}] indicizzato in {fixture.IndexSeconds:F1} s");
            var snapshot = await fixture.SnapshotAsync(excluded: null);
            var run = await fixture.EvaluateAsync(snapshot);
            log($"[{scenario.Name}] valutato: Recall@10 RRF blocco {run.Report.BlockHybrid.Recall["10"]:P1}");
            corpora.Add(run.Report);
            fixtures.Add((scenario, fixture));
            indexConfigHash = fixture.IndexConfigHash;
            indexSeconds += fixture.IndexSeconds;
            indexTimes.Add((scenario.Name, run.Report.Blocks, fixture.IndexSeconds));
            searchMs += run.SearchMilliseconds;
            searches += run.Searches;
            queryMs.AddRange(run.QueryMilliseconds);
        }

        // Plumbing con servizi reali: citazioni valide e riscontri Message sempre offerti sono invarianti; la perdita di mappatura è un RISULTATO (riportato, non asserito a 0).
        Assert.All(corpora, c => Assert.Equal(0, c.InvalidCitations));
        Assert.All(corpora, c => Assert.Equal(c.MessageRequired, c.MessageRequiredOffered));

        var final = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);
        var report = BuildReport(corpora, indexConfigHash, indexSeconds, searches == 0 ? 0 : searchMs / searches, final).Sealed();
        Assert.Equal(EvaluationReport.Real, report.Mode);
        File.WriteAllText(Path.Combine(outDirectory, "report-real.json"), report.ToJson());
        File.WriteAllText(Path.Combine(outDirectory, "report-real.md"), report.ToMarkdown());
        log("rapporto scritto");

        // ---- (b) scansione esatta vs HNSW sul segnale denso (indice creato SOLO nel database temporaneo di test; nessun codice di prodotto toccato)
        var dense = await MeasureDenseAsync(database, rig, fixtures, indexConfigHash, log);

        var services = await DescribeServicesAsync(database);
        File.WriteAllText(Path.Combine(outDirectory, "supplement-real.md"), Supplement(report, final, initial, warmup, estimate, indexTimes, queryMs, dense, services));
        log("supplemento scritto");
        WriteOutputsJson(outDirectory, report, final, initial, indexTimes, queryMs, dense, services);
        CompareOrWriteBaseline(report, final, services);
        log("fine");
    }

    // ---------------------------------------------------------------- (b) esatta vs HNSW

    private static async Task<IReadOnlyList<DenseOutcome>> MeasureDenseAsync(
        RetrievalDb database, RealRetrievalRig rig, IReadOnlyList<(EvalScenario Scenario, RetrievalEvaluationTests.ScenarioFixture Fixture)> fixtures, string indexConfigHash, Action<string> log)
    {
        // Vettori delle query (una volta sola): la latenza misurata è quella della SOLA query SQL, senza rete verso il modello.
        var work = new List<(string Corpus, string QueryId, float[] Vector, RetrievalScope Scope, IReadOnlyList<Guid> Required)>();
        foreach (var (scenario, fixture) in fixtures)
        {
            await using var db = database.NewContext();
            var scope = await RetrievalScope.FromSnapshotAsync(db, await fixture.SnapshotAsync(null), indexConfigHash, Ct);
            var required = fixture.RequiredBlocks();
            foreach (var query in EvaluationCorpus.LoadCases(scenario.Name))
            {
                work.Add((scenario.Name, query.Id, await rig.Embedding.EmbedAsync(IndexText.Of(query.Query), Ct), scope, required[query.Id]));
            }
        }

        // Verifica di coerenza: la query SQL replicata qui deve dare la stessa classifica di HybridSearch.DenseAsync (nessuna divergenza dal prodotto).
        await using (var check = new NpgsqlConnection(database.ConnectionString))
        {
            await check.OpenAsync(Ct);
            var first = work[^1];
            await using var db = database.NewContext();
            var product = (await rig.Search(db).DenseAsync(first.Scope, EvaluationCorpus.LoadCases(first.Corpus).Single(c => c.Id == first.QueryId).Query, Pool, Ct)).Blocks.Select(b => b.BlockId).ToList();
            Assert.Equal(product, await DenseIdsAsync(check, first.Scope, first.Vector));
        }

        var variants = new List<DenseVariant> { new("esatta (scansione, nessun indice HNSW)", []) };
        var outcomes = new List<DenseOutcome>();
        Dictionary<string, IReadOnlyList<Guid>> exactTop = [];
        outcomes.Add(await RunVariantAsync(database, work, variants[0], exactTop, exact: null, log));

        await using (var admin = new NpgsqlConnection(database.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            var watch = Stopwatch.StartNew();
            await new NpgsqlCommand("CREATE INDEX eval_hnsw ON \"RetrievalIndexEntries\" USING hnsw (\"Embedding\" vector_cosine_ops)", admin).ExecuteNonQueryAsync(Ct);
            var buildSeconds = watch.Elapsed.TotalSeconds;
            await new NpgsqlCommand("ANALYZE \"RetrievalIndexEntries\"", admin).ExecuteNonQueryAsync(Ct);
            var size = (long)(await new NpgsqlCommand("SELECT pg_relation_size('eval_hnsw')", admin).ExecuteScalarAsync(Ct))!;
            log($"HNSW creato in {buildSeconds:F2} s, {size / 1024} KiB (m=16, ef_construction=64 di default)");
            HnswBuild = $"HNSW (m=16, ef_construction=64: default pgvector) creato in {buildSeconds:F2} s, indice di {size / 1024} KiB, per tutti i progetti del database di prova";
        }

        var hnsw = new[]
        {
            new DenseVariant("HNSW, impostazioni predefinite (ef_search 40), piano scelto dal planner", []),
            new DenseVariant("HNSW, piano forzato (enable_seqscan=off), ef_search 40", ["SET enable_seqscan = off"]),
            new DenseVariant("HNSW, piano forzato, ef_search 200", ["SET enable_seqscan = off", "SET hnsw.ef_search = 200"]),
            new DenseVariant("HNSW, piano forzato, ef_search 200 + iterative_scan=relaxed_order", ["SET enable_seqscan = off", "SET hnsw.ef_search = 200", "SET hnsw.iterative_scan = relaxed_order"]),
        };
        foreach (var variant in hnsw)
        {
            try
            {
                outcomes.Add(await RunVariantAsync(database, work, variant, exactTop, exact: exactTop, log));
            }
            catch (PostgresException e) when (e.SqlState is "42704" or "22023")
            {
                log($"variante «{variant.Name}» non supportata da questa versione di pgvector: {e.MessageText}");
                outcomes.Add(new(variant.Name + " [non supportata: " + e.MessageText + "]", false, "n/d", 0, 0, 0, 0, 0, 0, 0, []));
            }
        }

        return outcomes;
    }

    private static string HnswBuild { get; set; } = "n/d";

    private static async Task<DenseOutcome> RunVariantAsync(
        RetrievalDb database, List<(string Corpus, string QueryId, float[] Vector, RetrievalScope Scope, IReadOnlyList<Guid> Required)> work, DenseVariant variant,
        Dictionary<string, IReadOnlyList<Guid>> exactTop, Dictionary<string, IReadOnlyList<Guid>>? exact, Action<string> log)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        foreach (var setting in variant.Settings)
        {
            await new NpgsqlCommand(setting, connection).ExecuteNonQueryAsync(Ct);
        }

        var plan = await PlanAsync(connection, work[^1].Scope, work[^1].Vector);
        var usesHnsw = plan.Contains("eval_hnsw", StringComparison.Ordinal);

        // un giro di riscaldamento (non misurato), poi Rounds giri misurati
        foreach (var w in work)
        {
            await DenseIdsAsync(connection, w.Scope, w.Vector);
        }

        var times = new List<double>();
        var tops = new Dictionary<string, IReadOnlyList<Guid>>();
        for (var round = 0; round < Rounds; round++)
        {
            foreach (var w in work)
            {
                var watch = Stopwatch.StartNew();
                var ids = await DenseIdsAsync(connection, w.Scope, w.Vector);
                times.Add(watch.Elapsed.TotalMilliseconds);
                if (round == 0)
                {
                    tops[$"{w.Corpus}/{w.QueryId}"] = ids;
                }
            }
        }

        if (exact is null)
        {
            foreach (var (key, ids) in tops)
            {
                exactTop[key] = ids;
            }
        }

        double o10 = 0, o50 = 0;
        var shortResults = 0;
        foreach (var (key, ids) in tops)
        {
            var reference = exactTop[key];
            o10 += reference.Take(10).Intersect(ids.Take(10)).Count() / (double)Math.Max(1, reference.Take(10).Count());
            o50 += reference.Intersect(ids).Count() / (double)Math.Max(1, reference.Count);
            shortResults += ids.Count < reference.Count ? 1 : 0;
        }

        var gold = work.GroupBy(w => w.Corpus).Select(group =>
        {
            var metrics = group.Where(w => w.Required.Count > 0).Select(w => EvaluationMetrics.ForQuery(w.QueryId, tops[$"{w.Corpus}/{w.QueryId}"], w.Required)).ToList();
            var aggregate = EvaluationMetrics.Aggregate(metrics);
            return (group.Key, aggregate.Recall[10], aggregate.Hit[1], aggregate.Queries);
        }).ToList();

        times.Sort();
        var outcome = new DenseOutcome(variant.Name, usesHnsw, plan, Percentile(times, 0.5), Percentile(times, 0.95), times.Average(), times.Count, o10 / tops.Count, o50 / tops.Count, shortResults, gold);
        log($"densa «{variant.Name}»: p50 {outcome.P50:F2} ms, p95 {outcome.P95:F2} ms, HNSW nel piano: {usesHnsw}, overlap@10 {outcome.Overlap10:P1}");
        return outcome;
    }

    private const string DenseSql = """
        SELECT e."BlockId" FROM "RetrievalIndexEntries" e
        JOIN "DocumentBlocks" b ON b."Id" = e."BlockId"
        JOIN "ParsedArtifacts" a ON a."Id" = b."ArtifactId"
        WHERE e."ProjectId" = @project AND b."ProjectId" = @project AND e."IndexConfigHash" = @hash
          AND e."DocumentVersionId" = ANY(@versions) AND a."PreparationId" = ANY(@preparations)
          AND e."TextHash" = b."TextHash"
        ORDER BY e."Embedding" <=> @query::vector(1024) ASC, e."BlockId" ASC
        LIMIT @pool
        """;

    private static NpgsqlCommand DenseCommand(NpgsqlConnection connection, string sql, RetrievalScope scope, float[] vector)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("project", NpgsqlDbType.Uuid) { Value = scope.ProjectId });
        command.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Varchar) { Value = scope.IndexConfigHash });
        command.Parameters.Add(new NpgsqlParameter("versions", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = scope.DocumentVersionIds.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("preparations", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = scope.PreparationIds.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("query", NpgsqlDbType.Text) { Value = "[" + string.Join(',', vector.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]" });
        command.Parameters.Add(new NpgsqlParameter("pool", NpgsqlDbType.Integer) { Value = Pool });
        return command;
    }

    private static async Task<IReadOnlyList<Guid>> DenseIdsAsync(NpgsqlConnection connection, RetrievalScope scope, float[] vector)
    {
        await using var command = DenseCommand(connection, DenseSql, scope, vector);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<string> PlanAsync(NpgsqlConnection connection, RetrievalScope scope, float[] vector)
    {
        await using var command = DenseCommand(connection, "EXPLAIN " + DenseSql, scope, vector);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0).Trim());
        }

        return string.Join(" | ", lines.Where(l => l.Contains("Scan", StringComparison.Ordinal) || l.Contains("Sort", StringComparison.Ordinal)).Select(l => l.Length > 110 ? l[..110] : l));
    }

    private static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    // ---------------------------------------------------------------- rapporto

    private static string Describe(RetrievalModelSnapshot s) =>
        $"stato {s.State}; modello {s.ModelId}; artefatto atteso {s.ExpectedArtifact}, osservato {s.Artifact ?? "n/d"}; dimensione osservata {(s.Dimension is { } d ? d.ToString(CultureInfo.InvariantCulture) : "n/d")}; SHA-256 {s.Sha256Status}";

    private static EvaluationReport BuildReport(IReadOnlyList<CorpusReport> corpora, string indexConfigHash, double indexSeconds, double searchMilliseconds, RetrievalModelSnapshot readiness)
    {
        var manifest = RetrievalGold.ReadManifest(RetrievalGold.Root);
        var inputs = manifest.Files.ToDictionary(f => "Gold/" + f.Path, f => f.Sha256, StringComparer.Ordinal);
        inputs["Evaluation/retrieval-cases.json"] = EvaluationCorpus.Sha256Of(EvaluationCorpus.CasesPath);

        // Soglie dichiarate dal gate di M1 (tasks T020, SC-002): Recall@10 ibrido ≥ 95% e perdita di mappatura = 0 su ciascun corpus. Nessun'altra soglia è introdotta qui.
        var thresholdsMet = corpora.All(c => c.BlockHybrid.Recall["10"] >= 0.95 && c.MappingLoss == 0);
        var (status, statement) = AcceptanceDecision.Decide(simulatedMode: false, thresholdsMet, anonymizedCorpus: false, independentQueries: false);
        var upstream = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["002:T138"] = RetrievalEvaluationTests.UpstreamStatus("T138"),
            ["002:T150"] = RetrievalEvaluationTests.UpstreamStatus("T150"),
            ["SHA-256 dei pesi del modello"] = readiness.Sha256Status.ToString(),
        };

        return new(
            EvaluationReport.ContractName, EvaluationReport.Real,
            "Embedding REALI bge-m3 (router llama.cpp interno, nessun LLM generativo, nessun riordino) e pgvector reale, su corpus e gold sintetici congelati: prima misura di M1 (T019). Non è una valutazione di gate: il gate è T020.",
            "sintetici (nessun dato cliente)", EvaluationCorpus.QueryAuthor, manifest.SourceCommit, manifest.FrozenAt,
            new SortedDictionary<string, string>(inputs, StringComparer.Ordinal), EvaluationCorpus.Sha256Of(RetrievalGold.PathOf("PROVENANCE.json")),
            new(indexConfigHash, null, $"{readiness.ModelId} ({readiness.Artifact ?? readiness.ExpectedArtifact})", readiness.Dimension ?? 0, "nessuno (M1)", readiness.Sha256Status.ToString(), "italian (PostgreSQL FTS, OR dei termini, ts_rank_cd)", Pool, Rrf.K, "BlockId ascendente"),
            corpora,
            new(status, statement, AnonymizedCorpus: false, IndependentQueries: false, upstream),
            [
                "Dati sintetici del laboratorio (nessun dato cliente), riempitivo povero, query di un solo autore: nessuna indipendenza delle query, nessun corpus anonimizzato.",
                "Parser finto a paragrafi (TXT, Markdown con titoli, DOCX): ~3,1k blocchi per corpus, analoghi alla variante «line» del laboratorio, NON i ~460 blocchi Docling di T082 del laboratorio: il confronto con le cifre del laboratorio non è a parità di granularità. Nessun PDF.",
                "Una evidenza per blocco (proxy di un ledger completo): la mappatura blocco→evidenza è 1:1 per costruzione, quindi una perdita pari a 0 NON misura la copertura dell'estrazione reale (R-3).",
                "I riscontri Message (dichiarazioni del PM) sono sempre inclusi e non classificati: esclusi da Recall@k; verificati come presenti nel pacchetto.",
                "Il segnale lessicale è PostgreSQL FTS italiano reale (OR dei termini), diverso dal BM25 del laboratorio: il confronto è riportato nel supplemento (R-1).",
                "Nessun riordino, nessun padre, nessun LLM (M1). SHA-256 dei pesi del modello non verificato (NotVerified); nessuna garanzia di completezza: il Recall è misurato solo sul gold annotato.",
                "Embedding quantizzati (Q5_K_M) serviti da un router condiviso: i valori denso/RRF possono variare in modo non deterministico fra esecuzioni; la baseline dichiarata è questa misura, non una garanzia di riproducibilità bit a bit.",
            ],
            new ReportMeasurements(Math.Round(indexSeconds, 3), Math.Round(searchMilliseconds, 3), "Tempi con embedding reali su PostgreSQL di test e router condiviso: indicativi, non un requisito prestazionale."),
            null);
    }

    // ---------------------------------------------------------------- supplemento (a)-(e)

    private static string Supplement(
        EvaluationReport report, RetrievalModelSnapshot ready, RetrievalModelSnapshot initial, IReadOnlyList<string> warmup, string estimate,
        IReadOnlyList<(string Name, int Blocks, double Seconds)> indexTimes, IReadOnlyList<double> queryMs, IReadOnlyList<DenseOutcome> dense, string services)
    {
        string P(double v) => (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
        string D(double v) => ((v * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)) + " pt";
        var sb = new StringBuilder();

        sb.AppendLine("## (e) Readiness reale");
        sb.AppendLine();
        sb.AppendLine($"- Sonda iniziale: {Describe(initial)}.");
        foreach (var line in warmup)
        {
            sb.AppendLine($"- {line}.");
        }

        sb.AppendLine($"- Sonda finale: {Describe(ready)}; verificata alle {ready.CheckedAt:u}.");
        sb.AppendLine($"- Servizi: {services}");
        sb.AppendLine();

        var lab = LoadLab();
        sb.AppendLine("## (a) Lessicale PostgreSQL FTS italiano vs BM25 del laboratorio (e confronto denso/RRF)");
        sb.AppendLine();
        sb.AppendLine("Livello blocco. Il laboratorio usa varianti di chunking diverse (N4 line_text, T082 **docling_text ~460 blocchi**, T083 line_context): il confronto NON è a parità di granularità per T082 (qui ~3,1k blocchi a paragrafi). Differenze in punti percentuali (reale − laboratorio).");
        sb.AppendLine();
        sb.AppendLine("| Corpus | Segnale | Recall@1 (PG / lab / Δ) | Recall@10 (PG / lab / Δ) | Hit@1 (PG / lab / Δ) |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var c in report.Corpora)
        {
            foreach (var (label, set, method) in new[] { ("lessicale PG FTS vs BM25", c.BlockLexical, "bm25"), ("denso bge-m3", c.BlockDense, "dense"), ("RRF", c.BlockHybrid, "hybrid_rrf") })
            {
                var l = lab.GetValueOrDefault((c.Name, method));
                string Cell(double actual, double? reference) => reference is { } r ? $"{P(actual)} / {P(r)} / {D(actual - r)}" : $"{P(actual)} / n/d";
                sb.AppendLine($"| {c.Name} | {label} | {Cell(set.Recall["1"], l?.Recall1)} | {Cell(set.Recall["10"], l?.Recall10)} | {Cell(set.Hit["1"], l?.Hit1)} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## (c) Recall a livello di blocco vs evidenza dopo mappatura (R-3)");
        sb.AppendLine();
        sb.AppendLine("| Corpus | Recall@10 blocco RRF | Recall@10 evidenza RRF | Perdita di mappatura | Citazioni invalide | Conflitti persi / valutati |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var c in report.Corpora)
        {
            sb.AppendLine($"| {c.Name} | {P(c.BlockHybrid.Recall["10"])} | {P(c.EvidenceHybrid.Recall["10"])} | {c.MappingLoss} | {c.InvalidCitations} | {c.LostConflicts} / {c.ConflictsEvaluated} |");
        }

        sb.AppendLine();
        sb.AppendLine("Avvertenza: la mappatura è 1:1 per costruzione (una evidenza per blocco), quindi la perdita misurata verifica la pipeline, non la copertura dell'estrazione reale.");
        sb.AppendLine();
        sb.AppendLine("## (d) Tempo di indicizzazione a freddo (embedding reali, batch da 4, modello già caricato)");
        sb.AppendLine();
        sb.AppendLine($"{estimate}.");
        sb.AppendLine();
        sb.AppendLine("| Corpus | Blocchi | Secondi | Blocchi/s |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var (name, blocks, seconds) in indexTimes)
        {
            sb.AppendLine($"| {name} | {blocks} | {seconds:F1} | {(seconds > 0 ? blocks / seconds : 0):F1} |");
        }

        var sorted = queryMs.Order().ToList();
        sb.AppendLine();
        sb.AppendLine($"Ricerca ibrida completa per query (embedding della query via rete + lessicale + denso + RRF): n={sorted.Count}, p50 {Percentile(sorted, 0.5):F1} ms, p95 {Percentile(sorted, 0.95):F1} ms.");
        sb.AppendLine();
        sb.AppendLine("## (b) Scansione esatta vs HNSW sul segnale denso");
        sb.AppendLine();
        sb.AppendLine($"Metodo: i vettori delle query sono calcolati una volta; si misura la SOLA query SQL densa (stessi filtri del prodotto, pool {Pool}), {Rounds} giri dopo uno di riscaldamento sulle {dense.FirstOrDefault()?.Samples / Rounds ?? 0} query dei tre corpora, su un database con i tre progetti (~6,3k voci). L'indice HNSW è creato solo nel database temporaneo di test (nessuna migrazione, nessun codice di prodotto toccato). {HnswBuild}.");
        sb.AppendLine();
        sb.AppendLine("| Variante | HNSW nel piano | p50 ms | p95 ms | Media ms | Overlap@10 vs esatta | Overlap@50 vs esatta | Query con meno risultati dell'esatta | Recall@10 denso (N4 / T082 / T083) | Hit@1 denso (N4 / T082 / T083) |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---|---|");
        foreach (var o in dense)
        {
            var recall = string.Join(" / ", o.Gold.Select(g => P(g.Recall10)));
            var hit = string.Join(" / ", o.Gold.Select(g => P(g.Hit1)));
            sb.AppendLine($"| {o.Variant} | {(o.PlanUsesHnsw ? "sì" : "no")} | {o.P50:F2} | {o.P95:F2} | {o.Mean:F2} | {P(o.Overlap10)} | {P(o.Overlap50)} | {o.ShortResults} | {recall} | {hit} |");
        }

        sb.AppendLine();
        foreach (var o in dense)
        {
            sb.AppendLine($"- Piano «{o.Variant}»: {o.PlanSummary}");
        }

        return sb.ToString();
    }

    private sealed record LabRow(double Recall1, double Recall10, double Hit1);

    private static Dictionary<(string Scenario, string Method), LabRow> LoadLab()
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(EvaluationCorpus.EvaluationRoot, "lab-metrics-subset.json")));
        return json.RootElement.GetProperty("results").EnumerateArray().ToDictionary(
            r => (r.GetProperty("scenario").GetString()!, r.GetProperty("method").GetString()!),
            r =>
            {
                var s = r.GetProperty("summary");
                return new LabRow(s.GetProperty("recall").GetProperty("1").GetDouble(), s.GetProperty("recall").GetProperty("10").GetDouble(), s.GetProperty("hit").GetProperty("1").GetDouble());
            });
    }

    // ---------------------------------------------------------------- uscite e baseline

    private static async Task<string> DescribeServicesAsync(RetrievalDb database)
    {
        var postgres = ((string?)await database.ScalarAsync("SELECT version()"))?.Split(',')[0] ?? "n/d";
        var vector = (string?)await database.ScalarAsync("SELECT extversion FROM pg_extension WHERE extname = 'vector'") ?? "n/d";
        return $"{postgres}; pgvector {vector}; embedding: router llama.cpp interno (host non riportato), modello bge-m3, formato GGUF Q5_K_M, richieste a batch di {EmbeddingClient.BatchSize}";
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
        File.AppendAllText(Path.Combine(directory, "progress-real.log"), line + Environment.NewLine);
        TestContext.Current.SendDiagnosticMessage(line);
    }

    private static void WriteText(string directory, string name, string text) => File.WriteAllText(Path.Combine(directory, name), text);

    private static void WriteOutputsJson(
        string directory, EvaluationReport report, RetrievalModelSnapshot ready, RetrievalModelSnapshot initial, IReadOnlyList<(string Name, int Blocks, double Seconds)> indexTimes,
        IReadOnlyList<double> queryMs, IReadOnlyList<DenseOutcome> dense, string services)
    {
        var sorted = queryMs.Order().ToList();
        File.WriteAllText(Path.Combine(directory, "supplement-real.json"), JsonSerializer.Serialize(new
        {
            initialReadiness = new { state = initial.State.ToString(), artifact = initial.Artifact, dimension = initial.Dimension },
            readiness = new { state = ready.State.ToString(), model = ready.ModelId, artifact = ready.Artifact, dimension = ready.Dimension, sha256Status = ready.Sha256Status.ToString(), checkedAt = ready.CheckedAt },
            services,
            indexTimes = indexTimes.Select(t => new { corpus = t.Name, blocks = t.Blocks, seconds = Math.Round(t.Seconds, 2) }),
            hybridSearchMs = new { p50 = Math.Round(Percentile(sorted, 0.5), 2), p95 = Math.Round(Percentile(sorted, 0.95), 2), n = sorted.Count },
            dense = dense.Select(d => new { d.Variant, d.PlanUsesHnsw, d.P50, d.P95, d.Mean, d.Samples, d.Overlap10, d.Overlap50, d.ShortResults, d.PlanSummary, gold = d.Gold.Select(g => new { g.Corpus, g.Recall10, g.Hit1, g.Queries }) }),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n");
    }

    private static void CompareOrWriteBaseline(EvaluationReport report, RetrievalModelSnapshot ready, string services)
    {
        var current = RetrievalEvaluationTests.Flatten(report);
        var inputs = new SortedDictionary<string, string>(report.InputHashes.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        var source = RetrievalEvaluationTests.FindUp(Path.Combine("backend", "tests", "bProjects.IntegrationTests", "Retrieval")) ?? throw new InvalidOperationException("Cartella dei test non trovata.");
        if (Environment.GetEnvironmentVariable(WriteBaselineVariable) == "1")
        {
            Directory.CreateDirectory(Path.Combine(source, "Baseline"));
            File.WriteAllText(Path.Combine(source, "Baseline", BaselineFile), JsonSerializer.Serialize(new
            {
                contract = "retrieval-baseline-1",
                mode = report.Mode,
                note = "BASELINE DICHIARATA di M1 (T019): embedding reali bge-m3, lessicale PG FTS italiano, pgvector con scansione esatta. È il riferimento per la regressione ≤ 2 punti sullo STESSO insieme di query (D-1); non è un'accettazione (gate: T020; riserva su corpus anonimizzato/query indipendenti, SC-012).",
                goldSourceCommit = report.GoldSourceCommit,
                indexConfigHash = report.Configuration.IndexConfigHash,
                inputHashes = inputs,
                reportHash = report.ReportHash,
                configuration = report.Configuration,
                embeddingReadiness = new { state = ready.State.ToString(), model = ready.ModelId, artifact = ready.Artifact, dimension = ready.Dimension, sha256Status = ready.Sha256Status.ToString() },
                services,
                measuredOn = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                metrics = current,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n");
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Retrieval", "Baseline", BaselineFile);
        if (!File.Exists(path))
        {
            return;   // prima misura: la baseline si scrive esplicitamente con BPROJECTS_EVAL_WRITE_BASELINE=1
        }

        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var baselineInputs = json.RootElement.GetProperty("inputHashes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var baselineMetrics = json.RootElement.GetProperty("metrics").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
        var problems = RegressionCheck.Compare(baselineInputs, inputs, baselineMetrics, current);
        Assert.True(problems.Count == 0, "Regressione rispetto alla baseline reale M1: " + string.Join("; ", problems));
    }
}
