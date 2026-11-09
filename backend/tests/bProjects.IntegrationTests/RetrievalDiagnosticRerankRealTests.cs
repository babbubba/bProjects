using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.Persistence;
using bProjects.App.Retrieval;
using Microsoft.EntityFrameworkCore;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003 T027 — DIAGNOSI PER SINGOLA QUERY del riordino (trait <c>Category=RetrievalDiagnosticReal</c> e <c>Integration</c>). NON è la baseline ufficiale e non la tocca: non scrive
/// <c>baseline-m2.json</c> né <c>m2-results.json</c> né <c>M2-real.md</c>. Servono l'opt-in DGX esplicito, <c>BPROJECTS_TEST_POSTGRES</c> e <c>BPROJECTS_EVAL_OUT</c> (cartella di uscita).
/// Corpora: <c>BPROJECTS_DIAG_SCENARIOS</c> (default <c>N4-fixtures,T083-multi</c>). Stessi gold, corpora, query, politica <see cref="RetrievalPolicy.V1"/> (pool 50, RRF k=60) e modelli di T026.
/// Per ogni query registra classifiche lessicale/densa/RRF/riordino, posizioni dei riscontri, punteggi grezzi (dati diagnostici, mai soglie), lunghezze e vicinanza ai concorrenti.
/// Solo per le query con Hit@1 mancato: ripetizione (stabilità), punteggi per singolo documento (coerenza batch/singolo = controllo di mapping/serving) e una sonda ESPLORATIVA
/// con il nome del file anteposto al testo, che NON è validazione né una proposta già misurata sul gold di accettazione.
/// </summary>
public sealed class RetrievalDiagnosticRerankRealTests
{
    private const string OutputVariable = "BPROJECTS_EVAL_OUT";
    private const string ScenariosVariable = "BPROJECTS_DIAG_SCENARIOS";
    private const string RawFile = "M2-diagnostic-raw.json";
    private const string QueriesFile = "M2-diagnostic-queries.md";
    private const int Top = 10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record BlockInfo(string File, int Ordinal, string Text, string[] Heading);

    [Fact]
    [Trait("Category", "RetrievalDiagnosticReal")]
    [Trait("Category", TestCategories.Integration)]
    public async Task The_real_reranker_diagnosis_per_query_is_written_apart_from_the_baseline()
    {
        var endpoint = TestEnvironment.RequireDgx();   // Skipped (non PASS) senza l'opt-in
        var apiKey = TestEnvironment.DgxApiKey;
        Assert.SkipWhen(apiKey is null, $"{TestEnvironment.DgxApiKeyVariable} non impostata.");
        var outDirectory = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(outDirectory), $"{OutputVariable} non impostata: la diagnosi scrive solo in una cartella esplicita, mai nelle baseline.");
        Directory.CreateDirectory(outDirectory!);
        Assert.Empty(RetrievalGold.Verify(RetrievalGold.Root));
        var wanted = (Environment.GetEnvironmentVariable(ScenariosVariable) is { Length: > 0 } list ? list : "N4-fixtures,T083-multi").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var scenarios = EvaluationCorpus.Scenarios.Where(s => wanted.Contains(s.Name)).ToList();
        Assert.Equal(wanted.Length, scenarios.Count);
        var policy = RetrievalPolicy.V1;

        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RealRerankRig(endpoint.ToString(), apiKey!);

        // Caricamento on-demand del router con richieste ordinarie e minime (come T026); nessun comando di load/unload.
        await rig.Embedding.EmbedAsync("probe", Ct);
        rig.Readiness.Invalidate();
        try
        {
            await rig.Reranker.RerankAsync("probe", ["probe a", "probe b"], Ct);
        }
        catch (RetrievalException failure)
        {
            Assert.Fail($"Riordino non raggiungibile ({failure.Code}): ci si ferma con evidenza.");
        }

        rig.Readiness.InvalidateReranker();
        var embeddingReady = await rig.Readiness.GetEmbeddingSnapshotAsync(Ct);
        var rerankerReady = await rig.Readiness.GetRerankerSnapshotAsync(Ct);
        Assert.True(embeddingReady.IsReady, "Embedding non pronto.");
        Assert.True(rerankerReady.IsReady, "Riordino non pronto.");
        var serving = await ServingInfoAsync(rig.Options, apiKey!);

        var queryReports = new List<Dictionary<string, object?>>();
        var summaries = new List<object>();
        foreach (var scenario in scenarios)
        {
            var fixture = await RetrievalEvaluationTests.ScenarioFixture.SetupAsync(database, rig, scenario);
            var blocks = new Dictionary<Guid, BlockInfo>();
            foreach (var file in scenario.DocumentFiles)
            {
                var parsed = EvaluationCorpus.Parse(RetrievalGold.PathOf(scenario.Directory, file));
                for (var i = 0; i < parsed.Count; i++)
                {
                    blocks[EvaluationCorpus.BlockId(scenario.Name, file, i)] = new(file, i, parsed[i].Text, parsed[i].Heading);
                }
            }

            await using var db = database.NewContext();
            var blockIds = blocks.Keys.ToList();
            var dbTexts = await db.DocumentBlocks.AsNoTracking().Where(b => blockIds.Contains(b.Id)).Select(b => new { b.Id, b.CanonicalText }).ToDictionaryAsync(b => b.Id, b => b.CanonicalText, Ct);
            var textMismatches = blocks.Count(b => !dbTexts.TryGetValue(b.Key, out var t) || IndexText.Of(t) != IndexText.Of(b.Value.Text));

            var scope = await RetrievalScope.FromSnapshotAsync(db, await fixture.SnapshotAsync(excluded: null), fixture.IndexConfigHash, Ct);
            var search = rig.Search(db);
            var reranker = rig.Reranking(db);
            var required = fixture.RequiredBlocks();
            var cases = EvaluationCorpus.LoadCases(scenario.Name);
            var scenarioReports = new List<Dictionary<string, object?>>();

            foreach (var query in cases)
            {
                var req = required[query.Id];
                if (req.Count == 0)
                {
                    continue;   // solo riscontri Message: fuori dalle metriche a livello di blocco
                }

                var result = await search.SearchAsync(scope, query.Query, policy, Ct);
                var window = RerankOrder.Window(result.Fused, policy.Rerank);
                var documents = window.Select(w => IndexText.Of(dbTexts[w.BlockId])).ToList();
                var queryText = IndexText.Of(query.Query);

                var scores = await rig.Reranker.RerankAsync(queryText, documents, Ct);   // ordinati per punteggio decrescente (indice = posizione nella finestra)
                var scoreOf = scores.ToDictionary(s => s.Index, s => s.Score);
                var rrfOrder = result.Fused.OrderBy(f => f.RrfRank).Select(f => f.BlockId).ToList();
                var ownOrder = scores.Select(s => window[s.Index].BlockId).Concat(rrfOrder.Skip(window.Count)).ToList();
                var productOrder = await reranker.OrderAsync(query.Query, result.Fused, policy, Ct);

                var lexical = result.Lexical.Blocks.OrderBy(b => b.Rank).Select(b => b.BlockId).ToList();
                var dense = result.Dense.Blocks.OrderBy(b => b.Rank).Select(b => b.BlockId).ToList();
                var rrfM = EvaluationMetrics.ForQuery(query.Id, rrfOrder, req);
                var rerankM = EvaluationMetrics.ForQuery(query.Id, ownOrder, req);
                var failsRerank = !rerankM.Hit[1];

                var windowIndexOf = window.Select((w, i) => (w.BlockId, i)).ToDictionary(t => t.BlockId, t => t.i);
                var goldTexts = req.Select(g => blocks[g].Text).ToList();
                var ownIds = ownOrder;

                object Entry(Guid id, int rank, bool withScore) => new Dictionary<string, object?>
                {
                    ["rank"] = rank,
                    ["blockId"] = id,
                    ["file"] = blocks[id].File,
                    ["ordinal"] = blocks[id].Ordinal,
                    ["gold"] = req.Contains(id),
                    ["rrfRank"] = result.Fused.FirstOrDefault(f => f.BlockId == id)?.RrfRank,
                    ["rerankScore"] = withScore && windowIndexOf.TryGetValue(id, out var wi) ? Math.Round(scoreOf[wi], 4) : null,
                    ["chars"] = IndexText.Of(blocks[id].Text).Length,
                    ["snippet"] = Snippet(blocks[id].Text),
                };

                var windowLengths = documents.Select(d => d.Length).ToList();
                var bestGoldPosition = rerankM.FirstRank;
                var preferred = ownIds.Take(bestGoldPosition is { } bp ? bp - 1 : Top).Where(id => !req.Contains(id)).Take(3).ToList();

                var report = new Dictionary<string, object?>
                {
                    ["corpus"] = scenario.Name,
                    ["queryId"] = query.Id,
                    ["query"] = query.Query,
                    ["goldDocumentFindings"] = query.Gold.Where(g => !scenario.MessageFiles.Contains(g.File)).Select(g =>
                    {
                        var id = RetrievalGold.Resolve([.. blocks.Where(b => b.Value.File == g.File).OrderBy(b => b.Value.Ordinal).Select(b => (b.Key, b.Value.Text))], g.Contains);
                        return new { findingId = g.Id, file = g.File, contains = g.Contains, blockId = id, ordinal = blocks[id].Ordinal, text = blocks[id].Text };
                    }).ToList(),
                    ["goldMessageFindings"] = query.Gold.Where(g => scenario.MessageFiles.Contains(g.File)).Select(g => new { findingId = g.Id, g.File, g.Contains }).ToList(),
                    ["requiredDocumentBlocks"] = req.Count,
                    ["fusedCount"] = result.Fused.Count,
                    ["windowCount"] = window.Count,
                    ["positions"] = new Dictionary<string, object?>
                    {
                        ["lexical"] = EvaluationMetrics.Positions(lexical, req),
                        ["dense"] = EvaluationMetrics.Positions(dense, req),
                        ["rrf"] = rrfM.Positions,
                        ["rerank"] = rerankM.Positions,
                    },
                    ["hit1"] = new Dictionary<string, bool> { ["rrf"] = rrfM.Hit[1], ["rerank"] = rerankM.Hit[1] },
                    ["recall10"] = new { rrf = rrfM.Recall[10], rerank = rerankM.Recall[10] },
                    ["top10"] = new Dictionary<string, object?>
                    {
                        ["lexical"] = lexical.Take(Top).Select((id, i) => Entry(id, i + 1, withScore: false)).ToList(),
                        ["dense"] = dense.Take(Top).Select((id, i) => Entry(id, i + 1, withScore: false)).ToList(),
                        ["rrf"] = rrfOrder.Take(Top).Select((id, i) => Entry(id, i + 1, withScore: false)).ToList(),
                        ["rerank"] = ownIds.Take(Top).Select((id, i) => Entry(id, i + 1, withScore: true)).ToList(),
                    },
                    ["goldRerankScores"] = req.Select(g => new
                    {
                        blockId = g,
                        rrfRank = result.Fused.FirstOrDefault(f => f.BlockId == g)?.RrfRank,
                        rerankPosition = ownIds.IndexOf(g) is var p and >= 0 ? p + 1 : (int?)null,
                        rerankScore = windowIndexOf.TryGetValue(g, out var wi) ? Math.Round(scoreOf[wi], 4) : (double?)null,
                    }).ToList(),
                    ["firstPreferredNonGold"] = preferred.Select(id => new
                    {
                        blockId = id,
                        file = blocks[id].File,
                        ordinal = blocks[id].Ordinal,
                        rerankScore = Math.Round(scoreOf[windowIndexOf[id]], 4),
                        sameFileAsAGold = req.Any(g => blocks[g].File == blocks[id].File),
                        maxJaccardWithGold = Math.Round(goldTexts.Max(t => Jaccard(t, blocks[id].Text)), 3),
                        snippet = Snippet(blocks[id].Text),
                    }).ToList(),
                    ["scoreGapTop1MinusBestGold"] = bestGoldPosition is > 1 and var bg
                        ? Math.Round(scoreOf[windowIndexOf[ownIds[0]]] - scoreOf[windowIndexOf[ownIds[bg - 1]]], 4)
                        : (double?)null,
                    ["lengths"] = new
                    {
                        queryChars = queryText.Length,
                        windowMaxChars = windowLengths.Max(),
                        windowMeanChars = Math.Round(windowLengths.Average(), 1),
                        goldChars = req.Select(g => IndexText.Of(blocks[g].Text).Length).ToList(),
                        approxMaxTokens = (int)Math.Ceiling((windowLengths.Max() + queryText.Length) / 3.5),
                    },
                    ["checks"] = new
                    {
                        scoresSortedDescending = scores.Zip(scores.Skip(1)).All(t => t.First.Score >= t.Second.Score),
                        indicesUniqueAndComplete = scores.Select(s => s.Index).Order().SequenceEqual(Enumerable.Range(0, window.Count)),
                        productOrderEqualsOwnOrder = productOrder.SequenceEqual(ownOrder),
                        rerankIsPermutationOfRrf = rrfOrder.Order().SequenceEqual(ownOrder.Order()),
                        windowEqualsRrfPrefix = window.Select(w => w.BlockId).SequenceEqual(rrfOrder.Take(window.Count)),
                    },
                    ["nearDuplicatesOfGoldInTop10Rerank"] = ownIds.Take(Top).Where(id => !req.Contains(id) && goldTexts.Any(t => Jaccard(t, blocks[id].Text) >= 0.6)).Select(id => blocks[id].File + "#" + blocks[id].Ordinal).ToList(),
                };

                if (failsRerank)
                {
                    // (1) stabilità: seconda richiesta identica
                    var again = await rig.Reranker.RerankAsync(queryText, documents, Ct);
                    var againScore = again.ToDictionary(s => s.Index, s => s.Score);
                    var probeIds = req.Concat(preferred).Distinct().ToList();

                    // (2) coerenza batch/singolo sui documenti che contano: stesso punteggio (a meno della numerica) se non c'è un problema di mapping/serving
                    var single = new List<object>();
                    foreach (var id in probeIds)
                    {
                        var one = await rig.Reranker.RerankAsync(queryText, [documents[windowIndexOf[id]]], Ct);
                        single.Add(new { blockId = id, gold = req.Contains(id), batchScore = Math.Round(scoreOf[windowIndexOf[id]], 4), singleScore = Math.Round(one[0].Score, 4), repeatBatchScore = Math.Round(againScore[windowIndexOf[id]], 4) });
                    }

                    report["stability"] = new
                    {
                        sameTop10 = again.Take(Top).Select(s => s.Index).SequenceEqual(scores.Take(Top).Select(s => s.Index)),
                        maxAbsScoreDifference = Math.Round(scores.Max(s => Math.Abs(s.Score - againScore[s.Index])), 6),
                        top1BlockIdSecondRun = window[again[0].Index].BlockId,
                    };
                    report["batchVersusSingleScores"] = single;

                    // (3) SONDA ESPLORATIVA (non validazione): nome del file anteposto al testo del candidato; query invariata.
                    var prefixed = window.Select(w => $"Documento: {blocks[w.BlockId].File}\n{IndexText.Of(dbTexts[w.BlockId])}").ToList();
                    var probe = await rig.Reranker.RerankAsync(queryText, prefixed, Ct);
                    var probeOrder = probe.Select(s => window[s.Index].BlockId).ToList();
                    var probeM = EvaluationMetrics.ForQuery(query.Id, probeOrder, req);
                    report["exploratoryDocNamePrefix"] = new
                    {
                        note = "ESPLORATIVO: nome del file anteposto ai candidati, solo sulle query con Hit@1 mancato; non è validazione e non cambia il prodotto.",
                        rerankPositions = probeM.Positions,
                        hit1 = probeM.Hit[1],
                        top1 = Entry(probeOrder[0], 1, withScore: false),
                    };
                }

                scenarioReports.Add(report);
                queryReports.Add(report);
            }

            List<string> Missed(string which) => [.. scenarioReports.Where(r => !((Dictionary<string, bool>)r["hit1"]!)[which]).Select(r => (string)r["queryId"]!)];
            var missedRrf = Missed("rrf");
            var missedRerank = Missed("rerank");
            summaries.Add(new
            {
                corpus = scenario.Name,
                queriesWithDocumentFindings = scenarioReports.Count,
                indexedBlocks = blocks.Count,
                annotatedVsIndexedTextMismatches = textMismatches,
                hit1Rrf = Math.Round(1 - (double)missedRrf.Count / scenarioReports.Count, 4),
                hit1Rerank = Math.Round(1 - (double)missedRerank.Count / scenarioReports.Count, 4),
                hit1MissedRrf = missedRrf,
                hit1MissedRerank = missedRerank,
                missedBoth = missedRrf.Intersect(missedRerank).ToList(),
                missedOnlyByRerank = missedRerank.Except(missedRrf).ToList(),
                missedOnlyByRrf = missedRrf.Except(missedRerank).ToList(),
            });
        }

        var output = new
        {
            kind = "DIAGNOSTICA T027: separata dalla baseline ufficiale (baseline-m2.json / m2-results.json / M2-real.md non toccati). I punteggi grezzi sono dati diagnostici, mai soglie.",
            policy = new { policy.Version, policy.Pool, policy.Rerank, policy.RrfK },
            embedding = new { embeddingReady.ModelId, embeddingReady.Artifact, embeddingReady.Dimension },
            reranker = new { rerankerReady.ModelId, rerankerReady.Artifact },
            serving,
            measuredOn = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            summaries,
            queries = queryReports,
        };
        File.WriteAllText(Path.Combine(outDirectory!, RawFile), JsonSerializer.Serialize(output, JsonOptions) + "\n");
        File.WriteAllText(Path.Combine(outDirectory!, QueriesFile), QueriesMarkdown(queryReports));
    }

    // ---------------------------------------------------------------- helpers

    private static string Snippet(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= 170 ? flat : flat[..170] + "…";
    }

    private static HashSet<string> Words(string text) =>
        [.. Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => m.Value)];

    private static double Jaccard(string a, string b)
    {
        var x = Words(a);
        var y = Words(b);
        var union = x.Union(y).Count();
        return union == 0 ? 0 : (double)x.Intersect(y).Count() / union;
    }

    /// <summary>Parametri di serving del riordino letti da <c>/v1/models</c> (se esposti): contesto e batch; flag di modello, host e chiave scartati.</summary>
    private static async Task<object> ServingInfoAsync(RetrievalOptions options, string apiKey)
    {
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var uri = new Uri(new Uri(options.Reranker.Endpoint.TrimEnd('/') + "/"), "models");
            using var json = JsonDocument.Parse(await http.GetStringAsync(uri, Ct));
            var deny = new HashSet<string>(StringComparer.Ordinal) { "-m", "--model", "--host", "--api-key", "--port", "--alias", "-a", "--path", "--models-dir", "--models-preset", "--mmproj" };
            var entries = new List<object>();
            foreach (var model in json.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = model.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (id is null || !id.Contains("rerank", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var args = new List<string>();
                string? state = null;
                if (model.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
                {
                    state = status.TryGetProperty("value", out var v) ? v.GetString() : null;
                    if (status.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array)
                    {
                        var all = argsElement.EnumerateArray().Select(a => a.GetString() ?? string.Empty).ToList();
                        for (var i = 0; i < all.Count; i++)
                        {
                            if (deny.Contains(all[i]))
                            {
                                i++;
                                continue;
                            }

                            args.Add(all[i]);
                        }
                    }
                }

                entries.Add(new { id, state, args });
            }

            return new { available = true, rerankerEntries = entries };
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or TaskCanceledException)
        {
            return new { available = false, reason = e.GetType().Name };
        }
    }

    private static string QueriesMarkdown(IReadOnlyList<Dictionary<string, object?>> queries)
    {
        var sb = new StringBuilder("# M2 — diagnostica per query (003 T027) — dati grezzi leggibili\n\nSeparata dalla baseline ufficiale. Posizioni 1-based dei riscontri documentali; «—» = assente dalla classifica. Punteggi del riordino: solo dati diagnostici.\n\n");
        sb.AppendLine("| Corpus | Query | #gold | lessicale | denso | RRF | riordino | Hit@1 RRF | Hit@1 riordino |");
        sb.AppendLine("|---|---|---:|---|---|---|---|---|---|");
        static string Pos(object? positions) => string.Join(",", ((IEnumerable<int?>)positions!).Select(p => p?.ToString(CultureInfo.InvariantCulture) ?? "—"));
        foreach (var q in queries)
        {
            var pos = (Dictionary<string, object?>)q["positions"]!;
            var hit = (Dictionary<string, bool>)q["hit1"]!;
            sb.AppendLine($"| {q["corpus"]} | {q["queryId"]} | {q["requiredDocumentBlocks"]} | {Pos(pos["lexical"])} | {Pos(pos["dense"])} | {Pos(pos["rrf"])} | {Pos(pos["rerank"])} | {(hit["rrf"] ? "sì" : "**no**")} | {(hit["rerank"] ? "sì" : "**no**")} |");
        }

        sb.AppendLine();
        sb.AppendLine("Dettaglio completo (top 10 per segnale, punteggi, concorrenti preferiti, controlli, stabilità, sonda esplorativa) in `M2-diagnostic-raw.json`.");
        return sb.ToString();
    }
}
