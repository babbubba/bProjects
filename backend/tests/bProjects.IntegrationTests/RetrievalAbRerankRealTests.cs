using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003 T027 — CONFRONTO A/B REALE dell'input del riordino (trait <c>Category=RetrievalAbReal</c> e <c>Integration</c>). NON ESEGUIRE senza autorizzazione DGX specifica.
/// A = <c>retrieval-policy-1</c> (solo testo del blocco); B = <c>retrieval-policy-2</c> (<c>rerank-input-1</c>: documento + sezione + testo). Per ogni query la ricerca ibrida è eseguita UNA volta e
/// i due ordini sono calcolati sugli STESSI candidati (stessi documenti, query, indice, modelli, RRF); l'ordine di A e B è alternato fra le query per non favorire la prima richiesta.
/// Insiemi (<c>BPROJECTS_AB_SETS</c>, predefinito <c>T027-dev,N4-fixtures,T082-single,T083-multi</c>): <c>T027-dev</c> è il dataset di SVILUPPO congelato (verificato contro il manifest);
/// gli altri sono i gold storici di M1/M2, usati SOLO come controllo di regressione, mai per scegliere o tarare la forma dell'input. Un insieme indipendente (altro autore) si aggiunge come
/// cartella in <c>Retrieval/Evaluation/&lt;nome&gt;</c> con lo stesso schema e si elenca in <c>BPROJECTS_AB_SETS</c>. Uscite (mai le baseline): <c>T027-ab-raw.json</c> e <c>T027-ab-report.md</c> in
/// <c>BPROJECTS_EVAL_OUT</c>. Nessun giudizio di gate: spetta al committente (T027).
/// </summary>
public sealed class RetrievalAbRerankRealTests
{
    private const string OutputVariable = "BPROJECTS_EVAL_OUT";
    private const string SetsVariable = "BPROJECTS_AB_SETS";
    private const double RegressionPoints = 2.0;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    [Trait("Category", "RetrievalAbReal")]
    [Trait("Category", TestCategories.Integration)]
    public async Task The_real_reranker_ab_comparison_of_text_only_versus_document_section_text_is_written_apart_from_the_baseline()
    {
        var endpoint = TestEnvironment.RequireDgx();   // Skipped (non PASS) senza l'opt-in
        var apiKey = TestEnvironment.DgxApiKey;
        Assert.SkipWhen(apiKey is null, $"{TestEnvironment.DgxApiKeyVariable} non impostata.");
        var outDirectory = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(outDirectory), $"{OutputVariable} non impostata: il confronto scrive solo in una cartella esplicita, mai nelle baseline.");
        Directory.CreateDirectory(outDirectory!);
        Assert.Empty(RetrievalGold.Verify(RetrievalGold.Root));   // gold storici intatti
        var requested = (Environment.GetEnvironmentVariable(SetsVariable) is { Length: > 0 } list ? list : $"{AbEvaluation.DevSet},N4-fixtures,T082-single,T083-multi")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var set in requested.Where(s => !EvaluationCorpus.Scenarios.Any(x => x.Name == s)))
        {
            Assert.Empty(AbEvaluation.VerifyFrozen(set));   // query, gold e documenti congelati PRIMA della misura: una modifica ferma tutto
        }

        var a = RetrievalPolicy.V1;
        var b = RetrievalPolicy.V2;
        Assert.Equal((a.Pool, a.Rerank, a.RrfK, a.ParentCharBudget, a.PackageBudgetBasis), (b.Pool, b.Rerank, b.RrfK, b.ParentCharBudget, b.PackageBudgetBasis));   // stessa configurazione di recupero

        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RealRerankRig(endpoint.ToString(), apiKey!);
        await rig.Embedding.EmbedAsync("probe", Ct);
        rig.Readiness.Invalidate();
        await rig.Reranker.RerankAsync("probe", ["probe a", "probe b"], Ct);   // caricamento on-demand ordinario, nessun load/unload esplicito
        rig.Readiness.InvalidateReranker();
        var embeddingReady = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);
        var rerankerReady = await rig.Readiness.GetRerankerSnapshotAsync(Ct);
        Assert.True(embeddingReady.IsReady && rerankerReady.IsReady, "Servizi non pronti: ci si ferma con evidenza.");

        var results = new List<AbQuery>();
        var queryIndex = 0;
        var indexSeconds = new Dictionary<string, double>();
        foreach (var name in requested)
        {
            var legacy = EvaluationCorpus.Scenarios.FirstOrDefault(s => s.Name == name);
            var scenario = legacy ?? new EvalScenario(name, Path.Combine("..", "Evaluation", name), [.. AbEvaluation.DocumentFiles(name)], [], null);
            var fixture = await RetrievalEvaluationTests.ScenarioFixture.SetupAsync(database, rig, scenario);
            indexSeconds[name] = Math.Round(fixture.IndexSeconds, 1);

            // Blocchi richiesti: gold storico (riscontri documentali) oppure cases.json dell'insieme.
            var blocks = scenario.DocumentFiles.ToDictionary(
                f => f,
                f => (IReadOnlyList<(Guid, string)>)[.. EvaluationCorpus.Parse(RetrievalGold.PathOf(scenario.Directory, f)).Select((x, i) => (EvaluationCorpus.BlockId(scenario.Name, f, i), x.Text))]);
            var cases = legacy is not null
                ? EvaluationCorpus.LoadCases(name).Select(c => new AbCase(c.Id, c.Query, "legacy", AbEvaluation.MentionsDocumentHeuristic(c.Query), false, c.Gold)).ToList()
                : [.. AbEvaluation.LoadCases(name)];
            var required = cases.ToDictionary(c => c.Id, c => (IReadOnlyList<Guid>)[.. c.Gold.Where(g => !scenario.MessageFiles.Contains(g.File)).Select(g => RetrievalGold.Resolve(blocks[g.File], g.Contains)).Distinct()]);

            await using var db = database.NewContext();
            var scope = await RetrievalScope.FromSnapshotAsync(db, await fixture.SnapshotAsync(excluded: null), fixture.IndexConfigHash, Ct);
            var search = rig.Search(db);
            var reranker = rig.Reranking(db);
            foreach (var c in cases.Where(c => required[c.Id].Count > 0))
            {
                var result = await search.SearchAsync(scope, c.Query, a, Ct);   // una sola ricerca: gli stessi candidati per A e B
                async Task<(IReadOnlyList<Guid> Order, double Ms)> Run(RetrievalPolicy policy)
                {
                    var watch = Stopwatch.StartNew();
                    var order = await reranker.OrderAsync(c.Query, result.Fused, policy, Ct);
                    return (order, watch.Elapsed.TotalMilliseconds);
                }

                IReadOnlyList<Guid> orderA;
                IReadOnlyList<Guid> orderB;
                double msA, msB;
                if (queryIndex++ % 2 == 0)
                {
                    (orderA, msA) = await Run(a);
                    (orderB, msB) = await Run(b);
                }
                else
                {
                    (orderB, msB) = await Run(b);
                    (orderA, msA) = await Run(a);
                }

                var rrfOrder = result.Fused.OrderBy(f => f.RrfRank).Select(f => f.BlockId).ToList();
                Assert.Equal(rrfOrder.Order(), orderA.Order());   // entrambi sono permutazioni degli stessi candidati
                Assert.Equal(rrfOrder.Order(), orderB.Order());
                results.Add(new(name, c,
                    EvaluationMetrics.ForQuery(c.Id, rrfOrder, required[c.Id]), EvaluationMetrics.ForQuery(c.Id, orderA, required[c.Id]), EvaluationMetrics.ForQuery(c.Id, orderB, required[c.Id]), msA, msB));
            }
        }

        var measuredOn = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(outDirectory!, "T027-ab-raw.json"), JsonSerializer.Serialize(Raw(results, a, b, embeddingReady, rerankerReady, indexSeconds, measuredOn), JsonOptions) + "\n");
        File.WriteAllText(Path.Combine(outDirectory!, "T027-ab-report.md"), Markdown(results, a, b, embeddingReady, rerankerReady, indexSeconds, measuredOn));
    }

    // ---------------------------------------------------------------- uscite

    private static string P(double v) => (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    private static string Pt(double points) => points.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " pt";

    private static object Raw(IReadOnlyList<AbQuery> results, RetrievalPolicy a, RetrievalPolicy b, RetrievalModelSnapshot embedding, RetrievalModelSnapshot reranker, IReadOnlyDictionary<string, double> indexSeconds, string measuredOn) => new
    {
        kind = "CONFRONTO A/B T027: separato dalla baseline ufficiale (baseline-m2.json non toccato). Nessun giudizio di gate.",
        measuredOn,
        variantA = new { a.Version, hash = a.Hash(), rerankInput = a.RerankInputMode },
        variantB = new { b.Version, hash = b.Hash(), rerankInput = b.RerankInputMode },
        retrieval = new { b.Pool, b.Rerank, b.RrfK },
        embedding = new { embedding.ModelId, embedding.Artifact, embedding.Dimension },
        reranker = new { reranker.ModelId, reranker.Artifact },
        indexSeconds,
        queries = results.Select(q => new
        {
            q.Set,
            q.Case.Id,
            q.Case.Query,
            q.Case.Category,
            q.Case.MentionsDocument,
            mentionDeclared = q.Case.MentionDeclared,
            positions = new { rrf = q.Rrf.Positions, a = q.A.Positions, b = q.B.Positions },
            hit1 = new { rrf = q.Rrf.Hit[1], a = q.A.Hit[1], b = q.B.Hit[1] },
            rerankMs = new { a = Math.Round(q.RerankMsA, 1), b = Math.Round(q.RerankMsB, 1) },
        }).ToList(),
    };

    private static string Markdown(IReadOnlyList<AbQuery> results, RetrievalPolicy a, RetrievalPolicy b, RetrievalModelSnapshot embedding, RetrievalModelSnapshot reranker, IReadOnlyDictionary<string, double> indexSeconds, string measuredOn)
    {
        var sb = new StringBuilder("# T027 — confronto A/B dell'input del riordino\n\n");
        sb.AppendLine("Misura descrittiva, separata dalla baseline ufficiale. **Nessun giudizio di gate**: T027 resta del committente. I gold storici (N4, T082, T083) sono qui solo controllo di regressione; `T027-dev` è un insieme di SVILUPPO dell'assistente, non una validazione indipendente.");
        sb.AppendLine();
        sb.AppendLine($"- Data: {measuredOn}. A: `{a.Version}` (hash `{a.Hash()}`, solo testo). B: `{b.Version}` (hash `{b.Hash()}`, `{b.RerankInputMode}`).");
        sb.AppendLine($"- Stessa configurazione di recupero: pool {b.Pool}, riordino dei primi {b.Rerank}, RRF k={b.RrfK}; una ricerca per query, stessi candidati per A e B. Embedding `{embedding.ModelId}`, riordino `{reranker.ModelId}`.");
        sb.AppendLine($"- Indicizzazione (s): {string.Join(", ", indexSeconds.Select(kv => $"{kv.Key} {kv.Value.ToString("F1", CultureInfo.InvariantCulture)}"))}.");
        sb.AppendLine();

        void Table(string title, IEnumerable<AbGroup> groups)
        {
            sb.AppendLine($"## {title}");
            sb.AppendLine();
            sb.AppendLine("| Gruppo | Query | Hit@1 RRF | Hit@1 A | Hit@1 B | B − A | Recall@5 A | Recall@5 B | Recall@10 A | Recall@10 B | B − A (R@10) |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var g in groups.Where(g => g.Queries > 0))
            {
                sb.AppendLine($"| {g.Name} | {g.Queries} | {P(g.Rrf.Hit[1])} | {P(g.A.Hit[1])} | {P(g.B.Hit[1])} | {Pt((g.B.Hit[1] - g.A.Hit[1]) * 100)} | {P(g.A.Recall[5])} | {P(g.B.Recall[5])} | {P(g.A.Recall[10])} | {P(g.B.Recall[10])} | {Pt((g.B.Recall[10] - g.A.Recall[10]) * 100)} |");
            }

            sb.AppendLine();
        }

        var sets = results.Select(r => r.Set).Distinct().ToList();
        Table("Per insieme", sets.Select(s => AbEvaluation.Group(s, results.Where(r => r.Set == s))).Append(AbEvaluation.Group("TUTTI", results)));
        var dev = results.Where(r => r.Set == AbEvaluation.DevSet).ToList();
        if (dev.Count > 0)
        {
            Table("T027-dev per categoria", dev.Select(r => r.Case.Category).Distinct().Select(c => AbEvaluation.Group(c, dev.Where(r => r.Case.Category == c))));
        }

        Table("Con e senza riferimento al documento nella query (dichiarato per T027-dev, euristica per parole chiave sui gold storici)",
        [
            AbEvaluation.Group("cita il documento", results.Where(r => r.Case.MentionsDocument)),
            AbEvaluation.Group("non cita il documento", results.Where(r => !r.Case.MentionsDocument)),
        ]);
        Table("Solo gold storici (controllo di regressione)", sets.Where(s => s != AbEvaluation.DevSet).Select(s => AbEvaluation.Group(s, results.Where(r => r.Set == s))));

        var historic = results.Where(r => r.Set != AbEvaluation.DevSet).ToList();
        if (historic.Count > 0)
        {
            var g = AbEvaluation.Group("storici", historic);
            var hit = (g.B.Hit[1] - g.A.Hit[1]) * 100;
            var recall = (g.B.Recall[10] - g.A.Recall[10]) * 100;
            sb.AppendLine($"Regressione sui gold storici (B − A): Hit@1 {Pt(hit)}, Recall@10 {Pt(recall)}. Segnalazione per peggioramento oltre {RegressionPoints:F0} pt: Hit@1 {(hit < -RegressionPoints ? "SÌ" : "no")}, Recall@10 {(recall < -RegressionPoints ? "SÌ" : "no")}.");
            sb.AppendLine();
        }

        sb.AppendLine("## Query cambiate (Hit@1)");
        sb.AppendLine();
        var regressions = AbEvaluation.Regressions(results);
        var improvements = AbEvaluation.Improvements(results);
        sb.AppendLine($"Regressioni (corrette in A, mancate in B): **{regressions.Count}**. Miglioramenti (mancate in A, corrette in B): **{improvements.Count}**.");
        sb.AppendLine();
        sb.AppendLine("| Esito | Insieme | Query | Categoria | Cita il documento | Posizioni A | Posizioni B |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        static string Pos(QueryMetrics m) => string.Join(",", m.Positions.Select(p => p?.ToString(CultureInfo.InvariantCulture) ?? "—"));
        foreach (var (label, list) in new[] { ("regressione", regressions), ("miglioramento", improvements) })
        {
            foreach (var q in list)
            {
                sb.AppendLine($"| {label} | {q.Set} | {q.Case.Id} | {q.Case.Category} | {(q.Case.MentionsDocument ? "sì" : "no")} | {Pos(q.A)} | {Pos(q.B)} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Latenza del solo riordino per query (ms, include la lettura dei testi e della provenienza dal DB)");
        sb.AppendLine();
        sb.AppendLine("| Variante | n | p50 | p95 | media | max |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        var la = AbEvaluation.Latency([.. results.Select(r => r.RerankMsA)]);
        var lb = AbEvaluation.Latency([.. results.Select(r => r.RerankMsB)]);
        sb.AppendLine($"| A (solo testo) | {la["n"]:F0} | {la["p50"]:F1} | {la["p95"]:F1} | {la["mean"]:F1} | {la["max"]:F1} |");
        sb.AppendLine($"| B (documento + sezione + testo) | {lb["n"]:F0} | {lb["p50"]:F1} | {lb["p95"]:F1} | {lb["mean"]:F1} | {lb["max"]:F1} |");
        sb.AppendLine();
        sb.AppendLine("## Limiti");
        sb.AppendLine();
        sb.AppendLine("- Dati sintetici; `T027-dev` scritto dopo aver osservato i fallimenti di M2 dallo stesso autore dell'esperimento: serve un insieme indipendente (altro autore o revisore) per una validazione.");
        sb.AppendLine("- Una esecuzione per variante; servizi quantizzati su router condiviso; latenze indicative. Hit@1 su query multi-riscontro = almeno un riscontro in prima posizione.");
        sb.AppendLine("- Il riferimento al documento nei gold storici è un'euristica per parole chiave, non una dichiarazione d'autore.");
        return sb.ToString();
    }
}
