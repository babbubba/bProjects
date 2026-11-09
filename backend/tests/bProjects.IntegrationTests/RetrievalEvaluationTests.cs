using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using bProjects.App.Retrieval;
using Microsoft.EntityFrameworkCore;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003 T018: harness di valutazione SINTETICA del recupero, trait <c>Category=RetrievalEval</c>. Carica gli originali del gold congelato (T004) nei blocchi (parser finto, nessun
/// Docling), costruisce l'indice con EMBEDDING SIMULATI deterministici (hash → vettore, <see cref="FakeEmbeddingServer"/>) e pgvector reale, esegue le query del laboratorio e
/// produce il rapporto (T017) dichiarando «modalità simulata». I vettori simulati non hanno semantica: le metriche denso/RRF NON sono misure di qualità e non si fissa alcuna
/// soglia di Recall; si verifica il <i>plumbing</i> (nessuna perdita blocco→evidenza, citazioni valide, isolamento, determinismo) e la regressione ≤ 2 punti dalla baseline offline.
/// Nessun DGX, nessun servizio reale, nessun LLM: la misura reale è T019 (opt-in separato, non qui).
/// </summary>
public sealed class RetrievalEvaluationTests
{
    private const int Pool = 50;
    private const string BaselineFile = "baseline-m1-simulated.json";
    private const string WriteBaselineVariable = "BPROJECTS_EVAL_WRITE_BASELINE";
    private const string OutputVariable = "BPROJECTS_EVAL_OUT";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- DET: gold, provenienza, risoluzione

    [Fact]
    [Trait("Category", "RetrievalEval")]
    public void The_frozen_gold_is_intact_before_any_evaluation()
    {
        Assert.Empty(RetrievalGold.Verify(RetrievalGold.Root));
    }

    [Fact]
    [Trait("Category", "RetrievalEval")]
    public void The_evaluation_queries_are_the_laboratory_ones_with_recorded_hash_and_the_n4_set_equals_the_frozen_cases()
    {
        using var provenance = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(EvaluationCorpus.EvaluationRoot, "PROVENANCE.json")));
        var recorded = provenance.RootElement.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => f.GetProperty("sha256").GetString()!);
        foreach (var (path, sha) in recorded)
        {
            Assert.Equal(sha, EvaluationCorpus.Sha256Of(Path.Combine(EvaluationCorpus.EvaluationRoot, path)));
        }

        Assert.Equal("ded3345c91b978b7fea71d2f459a94d958841a88ecb392668ebe64078357e59b", recorded["retrieval-cases.json"]);   // = input-hashes.json del laboratorio @ f005c8d
        Assert.Equal([12, 19, 25], EvaluationCorpus.Scenarios.Select(s => EvaluationCorpus.LoadCases(s.Name).Count));

        using var frozen = JsonDocument.Parse(File.ReadAllBytes(RetrievalGold.PathOf("cases.json")));
        var frozenCases = frozen.RootElement.GetProperty("cases").EnumerateArray()
            .Select(c => (Id: c.GetProperty("id").GetString()!, Query: c.GetProperty("query").GetString()!, File: c.GetProperty("gold").GetProperty("file").GetString()!, Contains: c.GetProperty("gold").GetProperty("contains").GetString()!));
        var n4 = EvaluationCorpus.LoadCases("N4-fixtures").Select(c => (c.Id, c.Query, c.Gold.Single().File, c.Gold.Single().Contains));
        Assert.Equal(frozenCases, n4);
    }

    [Fact]
    [Trait("Category", "RetrievalEval")]
    public void Every_gold_finding_resolves_to_exactly_one_block_of_its_file_and_message_findings_are_not_indexable_documents()
    {
        foreach (var scenario in EvaluationCorpus.Scenarios)
        {
            var parsed = scenario.DocumentFiles.ToDictionary(f => f, f => EvaluationCorpus.Parse(RetrievalGold.PathOf(scenario.Directory, f)));
            Assert.All(parsed.Values, blocks => Assert.NotEmpty(blocks));
            foreach (var query in EvaluationCorpus.LoadCases(scenario.Name))
            {
                foreach (var finding in query.Gold)
                {
                    if (scenario.MessageFiles.Contains(finding.File))
                    {
                        Assert.Contains(finding.Contains.Split(' ')[0], File.ReadAllText(RetrievalGold.PathOf(scenario.Directory, finding.File)), StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    var blocks = parsed[finding.File].Select((b, i) => (EvaluationCorpus.BlockId(scenario.Name, finding.File, i), b.Text)).ToList();
                    RetrievalGold.Resolve(blocks, finding.Contains);   // lancia se assente o ambiguo
                }
            }

            _ = EvaluationCorpus.LoadConflicts(scenario);   // ogni lato di conflitto è risolvibile in un solo riscontro
        }
    }

    // ---------------------------------------------------------------- PG: valutazione completa

    [Fact]
    [Trait("Category", "RetrievalEval")]
    [Trait("Category", TestCategories.Integration)]
    public async Task The_simulated_evaluation_preserves_every_finding_through_the_pipeline_and_produces_the_report_and_the_offline_baseline()
    {
        Assert.Empty(RetrievalGold.Verify(RetrievalGold.Root));
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();

        var corpora = new List<CorpusReport>();
        string indexConfigHash = string.Empty;
        double indexSeconds = 0, searchMs = 0;
        var searches = 0;
        foreach (var scenario in EvaluationCorpus.Scenarios)
        {
            var fixture = await ScenarioFixture.SetupAsync(database, rig, scenario);
            var snapshot = await fixture.SnapshotAsync(excluded: null);
            var run = await fixture.EvaluateAsync(snapshot);
            corpora.Add(run.Report);
            indexConfigHash = fixture.IndexConfigHash;
            indexSeconds += fixture.IndexSeconds;
            searchMs += run.SearchMilliseconds;
            searches += run.Searches;

            // Plumbing: nessuna perdita, citazioni valide, identità fra livello blocco e livello evidenza (mappatura 1:1 per costruzione).
            Assert.True(run.Report.MappingLoss == 0, $"{scenario.Name}: perdita di mappatura blocco→evidenza");
            Assert.Equal(0, run.Report.InvalidCitations);
            Assert.Equal(run.Report.MessageRequired, run.Report.MessageRequiredOffered);
            Assert.Equal(run.Report.BlockHybrid.Recall, run.Report.EvidenceHybrid.Recall);
            Assert.Equal(run.Report.BlockHybrid.Hit, run.Report.EvidenceHybrid.Hit);
            Assert.Equal(run.Report.BlockHybrid.FirstRankMean, run.Report.EvidenceHybrid.FirstRankMean);
        }

        var report = BuildReport(corpora, indexConfigHash, indexSeconds, searches == 0 ? 0 : searchMs / searches).Sealed();
        Assert.Equal(report.ComputeHash(), BuildReport(corpora, indexConfigHash, 999, 999).ComputeHash());   // i tempi non entrano nell'hash
        Assert.Equal(EvaluationReport.Simulated, report.Mode);
        Assert.Equal(AcceptanceDecision.NotAssessable, report.Acceptance.Status);
        Assert.DoesNotContain("accettata", report.ToMarkdown().Replace("Non accettata", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

        WriteOutputs(report);
        CompareOrWriteBaseline(report);
    }

    // ---------------------------------------------------------------- PG: isolamento e determinismo

    [Fact]
    [Trait("Category", "RetrievalEval")]
    [Trait("Category", TestCategories.Integration)]
    public async Task Decoys_in_another_project_and_an_excluded_version_never_enter_the_ranking_and_do_not_change_it()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var scenario = EvaluationCorpus.Scenarios[0];
        var fixture = await ScenarioFixture.SetupAsync(database, rig, scenario);

        var before = await fixture.EvaluateAsync(await fixture.SnapshotAsync(excluded: null));
        var again = await fixture.EvaluateAsync(await fixture.SnapshotAsync(excluded: null));
        Assert.Equal(before.Traces, again.Traces);   // determinismo: stessa classifica, blocco per blocco

        // Esche con lo stesso testo: copia in un altro progetto (indicizzata) e copia ESCLUSA dal PM nello stesso progetto (indicizzata).
        var decoyIds = new HashSet<Guid>();
        var otherProject = await database.SeedProjectAsync("altro progetto");
        foreach (var file in scenario.DocumentFiles)
        {
            var blocks = EvaluationCorpus.Parse(RetrievalGold.PathOf(scenario.Directory, file));
            var decoy = await database.AddDocumentAsync(otherProject, file, [.. blocks.Select(b => b.Text)], blockId: i => EvaluationCorpus.BlockId(scenario.Name + "/altro", file, i));
            decoyIds.UnionWith(decoy.BlockIds);
            await using var db = database.NewContext();
            Assert.Equal(RetrievalIndexStatus.Ready, (await rig.Builder(db).BuildAsync(decoy.PreparationId, Ct)).Status);
        }

        var excludedBlocks = EvaluationCorpus.Parse(RetrievalGold.PathOf(scenario.Directory, "txt-conflict-a.txt"));
        var excluded = await database.AddDocumentAsync(fixture.Project, "txt-conflict-a (esclusa)", [.. excludedBlocks.Select(b => b.Text)], blockId: i => EvaluationCorpus.BlockId(scenario.Name + "/esclusa", "a", i));
        decoyIds.UnionWith(excluded.BlockIds);
        await using (var db = database.NewContext())
        {
            Assert.Equal(RetrievalIndexStatus.Ready, (await rig.Builder(db).BuildAsync(excluded.PreparationId, Ct)).Status);
        }

        var after = await fixture.EvaluateAsync(await fixture.SnapshotAsync(excluded: [excluded]));

        Assert.All(after.Traces.SelectMany(t => t.Fused), b => Assert.DoesNotContain(b, decoyIds));
        Assert.Equal(before.Traces, after.Traces);   // i filtri stanno PRIMA del ranking: le esche non spostano nessun rango
        Assert.Equal(JsonSerializer.Serialize(before.Report.BlockHybrid), JsonSerializer.Serialize(after.Report.BlockHybrid));
    }

    // ---------------------------------------------------------------- fixture di scenario

    private sealed record QueryTrace(string Id, IReadOnlyList<Guid> Fused)
    {
        public bool Equals(QueryTrace? other) => other is not null && Id == other.Id && Fused.SequenceEqual(other.Fused);

        public override int GetHashCode() => HashCode.Combine(Id, Fused.Count);
    }

    private sealed record ScenarioRun(CorpusReport Report, IReadOnlyList<QueryTrace> Traces, double SearchMilliseconds, int Searches);

    private sealed class ScenarioFixture
    {
        private readonly RetrievalDb _database;
        private readonly RetrievalRig _rig;
        private readonly EvalScenario _scenario;
        private readonly List<(SeededDocument Doc, IReadOnlyList<Guid> EvidenceIds, Guid EvidenceVersion)> _docs = [];
        private readonly Dictionary<string, Guid> _messageEvidence = [];

        private ScenarioFixture(RetrievalDb database, RetrievalRig rig, EvalScenario scenario)
        {
            _database = database;
            _rig = rig;
            _scenario = scenario;
        }

        public Guid Project { get; private set; }

        public string IndexConfigHash { get; private set; } = string.Empty;

        public double IndexSeconds { get; private set; }

        public static async Task<ScenarioFixture> SetupAsync(RetrievalDb database, RetrievalRig rig, EvalScenario scenario)
        {
            var fixture = new ScenarioFixture(database, rig, scenario) { Project = await database.SeedProjectAsync(scenario.Name) };
            var stopwatch = Stopwatch.StartNew();
            foreach (var file in scenario.DocumentFiles)
            {
                var blocks = EvaluationCorpus.Parse(RetrievalGold.PathOf(scenario.Directory, file));
                var doc = await database.AddDocumentAsync(
                    fixture.Project, file, [.. blocks.Select(b => b.Text)], headings: [.. blocks.Select(b => b.Heading)], blockId: i => EvaluationCorpus.BlockId(scenario.Name, file, i));
                // Una evidenza per blocco: proxy di un ledger completo; la mappatura del prodotto (BlockEvidenceMapper) deve ritrovarla senza perdite.
                var (version, evidenceIds) = await database.AddEvidenceAsync(doc, [.. blocks.Select((_, i) => (EvidenceKind.Context, $"Blocco {i} di {file}", new[] { i }))]);
                fixture._docs.Add((doc, evidenceIds, version));
            }

            stopwatch.Restart();
            foreach (var (doc, _, _) in fixture._docs)
            {
                await using var db = database.NewContext();
                var built = await rig.Builder(db).BuildAsync(doc.PreparationId, Ct);
                Assert.Equal(RetrievalIndexStatus.Ready, built.Status);
                Assert.Equal((doc.BlockIds.Count, doc.BlockIds.Count), (built.BlockCount, built.EntryCount));
                fixture.IndexConfigHash = built.IndexConfigHash;
            }

            fixture.IndexSeconds = stopwatch.Elapsed.TotalSeconds;
            return fixture;
        }

        public Task<Guid> SnapshotAsync(IReadOnlyList<SeededDocument>? excluded) =>
            _database.AddSnapshotAsync(
                Project, [.. _docs.Select(d => d.Doc)], excluded, _docs.ToDictionary(d => d.Doc.PreparationId, d => d.EvidenceVersion));

        public async Task<ScenarioRun> EvaluateAsync(Guid snapshot)
        {
            await using var db = _database.NewContext();
            var scope = await RetrievalScope.FromSnapshotAsync(db, snapshot, IndexConfigHash, Ct);
            var search = _rig.Search(db);

            var documentEvidence = await BlockEvidenceMapper.LoadDocumentEvidenceAsync(db, [.. _docs.Select(d => d.EvidenceVersion)], Ct);
            var cases = EvaluationCorpus.LoadCases(_scenario.Name);

            // Riscontri Message (dichiarazioni del PM): evidenze del ledger SENZA blocchi, sempre offerte allo stadio e mai classificate dal recupero.
            foreach (var finding in cases.SelectMany(c => c.Gold).Where(g => _scenario.MessageFiles.Contains(g.File)))
            {
                _messageEvidence.TryAdd(finding.Id, Guid.NewGuid());
            }

            var evidences = documentEvidence.Concat(_messageEvidence.Values.Select(id => new RegistryEvidence(id, EvidenceOriginKind.Message, EvidenceKind.Requirement, []))).ToList();

            var blockTexts = _docs.SelectMany(d => d.Doc.BlockIds.Zip(d.Doc.Texts)).ToDictionary(t => t.First, t => t.Second);
            var evidenceOfBlock = _docs.SelectMany(d => d.Doc.BlockIds.Zip(d.EvidenceIds)).ToDictionary(t => t.First, t => t.Second);
            var citations = await LoadCitationsAsync(db, [.. _docs.Select(d => d.EvidenceVersion)]);
            var blocksByFile = _docs.ToDictionary(d => d.Doc.Name, d => (IReadOnlyList<(Guid, string)>)[.. d.Doc.BlockIds.Zip(d.Doc.Texts)]);

            // Risoluzione dei riscontri: documento → (blocco, evidenza seminata); Message → evidenza del ledger.
            var findingItems = new Dictionary<string, (Guid? Block, Guid Evidence)>();
            foreach (var finding in cases.SelectMany(c => c.Gold).DistinctBy(g => g.Id))
            {
                if (_scenario.MessageFiles.Contains(finding.File))
                {
                    findingItems[finding.Id] = (null, _messageEvidence[finding.Id]);
                }
                else
                {
                    var block = RetrievalGold.Resolve(blocksByFile[finding.File], finding.Contains);
                    findingItems[finding.Id] = (block, evidenceOfBlock[block]);
                }
            }

            var inScope = blockTexts.Keys.ToHashSet();
            var lexicalMetrics = new List<QueryMetrics>();
            var denseMetrics = new List<QueryMetrics>();
            var hybridMetrics = new List<QueryMetrics>();
            var evidenceMetrics = new List<QueryMetrics>();
            var perQuery = new List<QueryReport>();
            var traces = new List<QueryTrace>();
            var topEvidence = new Dictionary<string, IReadOnlyList<Guid>>();
            var messageRequired = 0;
            var messageOffered = 0;
            var mappingLoss = 0;
            var invalidCitations = 0;
            var elapsed = 0.0;

            foreach (var query in cases)
            {
                var stopwatch = Stopwatch.StartNew();
                var result = await search.SearchAsync(scope, query.Query, Pool, Ct);
                elapsed += stopwatch.Elapsed.TotalMilliseconds;

                var fused = result.Fused.Select(f => f.BlockId).ToList();
                traces.Add(new(query.Id, fused));
                Assert.All(fused, id => Assert.Contains(id, inScope));   // nessun candidato fuori dal perimetro dello snapshot

                var provenance = await BlockEvidenceMapper.LoadProvenanceAsync(db, fused, Ct);
                var registry = CandidateRegistry.Build(
                    "eval-snapshot", IndexConfigHash, "none-m1", new string('0', 64), [new(Guid.NewGuid(), query.Query, result.Fused)], provenance, evidences);
                Assert.Equal((CandidateRegistry.OutcomePackage, true), (registry.Outcome, registry.RetrievalComplete));

                var alwaysIds = registry.AlwaysIncluded.Select(a => a.EvidenceId).ToHashSet();
                var rankedEvidence = registry.PackageEvidence.Where(id => !alwaysIds.Contains(id)).ToList();
                topEvidence[query.Id] = EvaluationMetrics.Top(rankedEvidence, EvaluationMetrics.ConflictK);

                // Ogni evidenza del pacchetto cita un blocco realmente recuperato e le citazioni sono valide sul blocco di origine.
                var retrieved = fused.ToHashSet();
                var byId = evidences.ToDictionary(e => e.EvidenceId);
                Assert.All(rankedEvidence, id => Assert.Contains(byId[id].BlockIds, retrieved.Contains));
                invalidCitations += EvaluationMetrics.InvalidCitations(rankedEvidence.SelectMany(id => citations[id]), blockTexts);

                var docFindings = query.Gold.Where(g => findingItems[g.Id].Block is not null).ToList();
                var messageFindings = query.Gold.Where(g => findingItems[g.Id].Block is null).ToList();
                messageRequired += messageFindings.Count;
                messageOffered += messageFindings.Count(g => registry.AlwaysIncluded.Any(a => a.EvidenceId == findingItems[g.Id].Evidence && a.InPackage));

                // Perdita di mappatura: un blocco gold recuperato la cui evidenza non arriva nel pacchetto.
                mappingLoss += docFindings.Select(g => findingItems[g.Id]).Distinct().Count(f => retrieved.Contains(f.Block!.Value) && !registry.PackageEvidence.Contains(f.Evidence));

                IReadOnlyList<int?> blockPositions = [], evidencePositions = [];
                if (docFindings.Count > 0)
                {
                    var requiredBlocks = docFindings.Select(g => findingItems[g.Id].Block!.Value).Distinct().ToList();
                    var requiredEvidence = docFindings.Select(g => findingItems[g.Id].Evidence).Distinct().ToList();
                    lexicalMetrics.Add(EvaluationMetrics.ForQuery(query.Id, result.Lexical.Blocks.Select(b => b.BlockId), requiredBlocks));
                    denseMetrics.Add(EvaluationMetrics.ForQuery(query.Id, result.Dense.Blocks.Select(b => b.BlockId), requiredBlocks));
                    var hybrid = EvaluationMetrics.ForQuery(query.Id, fused, requiredBlocks);
                    var evidence = EvaluationMetrics.ForQuery(query.Id, rankedEvidence, requiredEvidence);
                    hybridMetrics.Add(hybrid);
                    evidenceMetrics.Add(evidence);
                    blockPositions = hybrid.Positions;
                    evidencePositions = evidence.Positions;
                    Assert.Equal(hybrid.Positions, evidence.Positions);   // blocco 1:1 evidenza: la mappatura non sposta nessun riscontro
                    Assert.All(new[] { hybrid, evidence }, m => Assert.Equal(m.Recall.Values.Order(), m.Recall.Values));   // Recall@k non decresce con k
                }

                perQuery.Add(new(query.Id, query.Query, docFindings.Select(g => findingItems[g.Id].Block).Distinct().Count(), blockPositions, evidencePositions, messageFindings.Count));
            }

            // Conflitti: valutati sulla query che richiede TUTTI i lati; persi se un lato non è fra i primi K (i lati Message sono sempre offerti).
            var conflictReports = new List<ConflictReport>();
            var lost = 0;
            var evaluated = 0;
            foreach (var conflict in EvaluationCorpus.LoadConflicts(_scenario))
            {
                var covering = cases.FirstOrDefault(c => conflict.SideFindingIds.All(side => c.Gold.Any(g => g.Id == side)));
                if (covering is null)
                {
                    conflictReports.Add(new(conflict.Id, "not_evaluated", conflict.SideFindingIds));
                    continue;
                }

                var available = topEvidence[covering.Id].ToHashSet();
                available.UnionWith(_messageEvidence.Values);
                var sides = conflict.SideFindingIds.Select(side => findingItems[side].Evidence).ToList();
                var lostHere = EvaluationMetrics.LostConflicts<Guid>([(conflict.Id, sides)], available).Count;
                evaluated++;
                lost += lostHere;
                conflictReports.Add(new(conflict.Id, lostHere == 0 ? "offered" : "lost", conflict.SideFindingIds));
            }

            var report = new CorpusReport(
                _scenario.Name, _docs.Count, blockTexts.Count, cases.Count,
                MetricSet.From(EvaluationMetrics.Aggregate(lexicalMetrics)), MetricSet.From(EvaluationMetrics.Aggregate(denseMetrics)),
                MetricSet.From(EvaluationMetrics.Aggregate(hybridMetrics)), MetricSet.From(EvaluationMetrics.Aggregate(evidenceMetrics)),
                mappingLoss, invalidCitations, messageRequired, messageOffered, evaluated, lost, conflictReports, perQuery);
            return new(report, traces, elapsed, cases.Count);
        }

        private static async Task<Dictionary<Guid, List<Citation>>> LoadCitationsAsync(AppDbContext db, IReadOnlyList<Guid> versions)
        {
            var rows = await db.Evidences.AsNoTracking().Where(e => versions.Contains(e.VersionId)).Select(e => new { e.Id, e.Content }).ToListAsync(Ct);
            return rows.ToDictionary(
                r => r.Id,
                r => DocumentEvidenceCodec.DecodeContent(r.Content).Sources
                    .Where(s => s.BlockId is not null)
                    .Select(s => new Citation(r.Id, s.BlockId!.Value, s.Start, s.End, s.Quote)).ToList());
        }
    }

    // ---------------------------------------------------------------- rapporto, uscite e baseline

    private static EvaluationReport BuildReport(IReadOnlyList<CorpusReport> corpora, string indexConfigHash, double indexSeconds, double searchMilliseconds)
    {
        var manifest = RetrievalGold.ReadManifest(RetrievalGold.Root);
        var inputs = manifest.Files.ToDictionary(f => "Gold/" + f.Path, f => f.Sha256, StringComparer.Ordinal);
        inputs["Evaluation/retrieval-cases.json"] = EvaluationCorpus.Sha256Of(EvaluationCorpus.CasesPath);
        var (status, statement) = AcceptanceDecision.Decide(simulatedMode: true, thresholdsMet: false, anonymizedCorpus: false, independentQueries: false);
        var upstream = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["002:T138"] = UpstreamStatus("T138"),
            ["002:T150"] = UpstreamStatus("T150"),
            ["SHA-256 dei pesi del modello"] = "NotVerified",
        };

        return new(
            EvaluationReport.ContractName, EvaluationReport.Simulated,
            "Embedding SIMULATI (SHA-256 → vettore, nessuna semantica) e pgvector reale: le metriche denso e RRF NON sono misure di qualità; le misure reali richiedono il DGX (T019, non eseguito).",
            "sintetici (nessun dato cliente)", EvaluationCorpus.QueryAuthor, manifest.SourceCommit, manifest.FrozenAt,
            new SortedDictionary<string, string>(inputs, StringComparer.Ordinal), EvaluationCorpus.Sha256Of(RetrievalGold.PathOf("PROVENANCE.json")),
            new(indexConfigHash, null, "simulato (hash → vettore)", 1024, "nessuno (M1)", "NotVerified (modello simulato)", "italian (PostgreSQL FTS, OR dei termini, ts_rank_cd)", Pool, Rrf.K, "BlockId ascendente"),
            corpora,
            new(status, statement, AnonymizedCorpus: false, IndependentQueries: false, upstream),
            [
                "Modalità simulata: nessuna soglia di qualità (D-1) è applicata o fissata; le metriche non sono confrontabili con quelle reali del laboratorio.",
                "Dati sintetici del laboratorio (nessun dato cliente), riempitivo povero, query di un solo autore: nessuna indipendenza delle query, nessun corpus anonimizzato.",
                "Parser finto a paragrafi (TXT, Markdown con titoli, DOCX): ~3,1k blocchi per corpus, analoghi alla variante «line» del laboratorio (3115/3137 chunk), non ai ~460 blocchi Docling; nessun PDF.",
                "Una evidenza per blocco (proxy di un ledger completo): la mappatura blocco→evidenza è 1:1 per costruzione; il test verifica l'assenza di perdite nella pipeline, non la copertura dell'estrazione reale.",
                "I riscontri Message (dichiarazioni del PM) sono sempre inclusi e non classificati: esclusi da Recall@k (il laboratorio li indicizzava); verificati come presenti nel pacchetto.",
                "Il segnale lessicale è PostgreSQL FTS italiano reale (OR dei termini), diverso dal BM25 del laboratorio: il confronto è oggetto di T019 (R-1).",
                "Nessun riordino, nessun padre, nessun LLM (M1); isolamento fra progetti, versioni escluse e determinismo sono provati dai test PG, non dalle metriche.",
                "SHA-256 dei pesi del modello non verificato (NotVerified); nessuna garanzia di completezza: il Recall è misurato solo sul gold annotato.",
            ],
            new ReportMeasurements(Math.Round(indexSeconds, 3), Math.Round(searchMilliseconds, 3), "Tempi con embedding simulati su PostgreSQL di test: non rappresentano il DGX."),
            null);
    }

    private static string UpstreamStatus(string task)
    {
        var tasks = FindUp(Path.Combine("specs", "002-project-documents", "tasks.md"));
        if (tasks is null)
        {
            return "non determinato (tasks.md della 002 non trovato)";
        }

        var line = File.ReadLines(tasks).FirstOrDefault(l => Regex.IsMatch(l, $@"^\s*- \[[ xX]\] {task}\b"));
        return line is null ? "non determinato (task non trovato)" : Regex.IsMatch(line, @"^\s*- \[[xX]\]") ? "completato (checkbox spuntata)" : "aperto (checkbox non spuntata)";
    }

    private static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void WriteOutputs(EvaluationReport report)
    {
        var directory = Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } custom ? custom : Path.Combine(AppContext.BaseDirectory, "Retrieval", "EvaluationOutput");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "report-simulated.json"), report.ToJson());
        File.WriteAllText(Path.Combine(directory, "report-simulated.md"), report.ToMarkdown());
    }

    /// <summary>Metriche piatte per il confronto con la baseline: corpus × livello/segnale × Recall@k e Hit@k.</summary>
    private static SortedDictionary<string, double> Flatten(EvaluationReport report)
    {
        var flat = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var corpus in report.Corpora)
        {
            foreach (var (level, set) in new[] { ("block.lexical", corpus.BlockLexical), ("block.dense", corpus.BlockDense), ("block.rrf", corpus.BlockHybrid), ("evidence.rrf", corpus.EvidenceHybrid) })
            {
                foreach (var (k, value) in set.Recall)
                {
                    flat[$"{corpus.Name}.{level}.recall@{k}"] = value;
                }

                foreach (var (k, value) in set.Hit)
                {
                    flat[$"{corpus.Name}.{level}.hit@{k}"] = value;
                }
            }
        }

        return flat;
    }

    private static void CompareOrWriteBaseline(EvaluationReport report)
    {
        var current = Flatten(report);
        var inputs = new SortedDictionary<string, string>(report.InputHashes.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        if (Environment.GetEnvironmentVariable(WriteBaselineVariable) == "1")
        {
            var source = FindUp(Path.Combine("backend", "tests", "bProjects.IntegrationTests", "Retrieval")) ?? throw new InvalidOperationException("Cartella dei test non trovata.");
            Directory.CreateDirectory(Path.Combine(source, "Baseline"));
            File.WriteAllText(Path.Combine(source, "Baseline", BaselineFile), JsonSerializer.Serialize(new
            {
                contract = "retrieval-baseline-1",
                mode = report.Mode,
                note = "Baseline OFFLINE con embedding simulati (T018): serve alla regressione del plumbing, NON è la baseline di qualità (baseline-m1.json, T019, richiede il DGX).",
                goldSourceCommit = report.GoldSourceCommit,
                indexConfigHash = report.Configuration.IndexConfigHash,
                inputHashes = inputs,
                reportHash = report.ReportHash,
                metrics = current,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n");
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Retrieval", "Baseline", BaselineFile);
        Assert.True(File.Exists(path), $"Baseline offline mancante ({BaselineFile}): rigenerarla con {WriteBaselineVariable}=1.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var baselineInputs = json.RootElement.GetProperty("inputHashes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var baselineMetrics = json.RootElement.GetProperty("metrics").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
        var problems = RegressionCheck.Compare(baselineInputs, inputs, baselineMetrics, current);
        Assert.True(problems.Count == 0, "Regressione rispetto alla baseline offline: " + string.Join("; ", problems));
        Assert.Equal(json.RootElement.GetProperty("indexConfigHash").GetString(), report.Configuration.IndexConfigHash);
    }
}
