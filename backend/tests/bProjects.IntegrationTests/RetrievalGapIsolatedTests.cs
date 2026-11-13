using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using bProjects.App.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.IntegrationTests;

internal enum VerdictStatus { Pass, Fail, NotVerified, NotApplicable }

internal sealed record Verdict(VerdictStatus Status, string Label, string Detail);

/// <summary>Esiti dei controlli: PASS, FAIL, NotVerified (non dimostrabile qui, mai promosso a PASS) e NotApplicable.</summary>
internal sealed class Verdicts
{
    private readonly List<Verdict> _items = [];

    public IReadOnlyList<Verdict> Items => _items;

    public List<string> Notes { get; } = [];

    public bool HasFail => _items.Any(i => i.Status == VerdictStatus.Fail);

    public void Check(string label, bool ok, string detail = "") => _items.Add(new(ok ? VerdictStatus.Pass : VerdictStatus.Fail, label, detail));

    public void NotVerified(string label, string why) => _items.Add(new(VerdictStatus.NotVerified, label, why));

    public void NotApplicable(string label, string why) => _items.Add(new(VerdictStatus.NotApplicable, label, why));

    public void Add(Verdicts other)
    {
        _items.AddRange(other._items);
        Notes.AddRange(other.Notes);
    }

    public JsonArray ToJson() => [.. _items.Select(i => (JsonNode)new JsonObject { ["status"] = i.Status.ToString(), ["label"] = i.Label, ["detail"] = i.Detail })];

    public override string ToString() => string.Join('\n', _items.Select(i => $"{i.Status.ToString().ToUpperInvariant(),-13} {i.Label}{(i.Detail.Length > 0 ? " — " + i.Detail : string.Empty)}"));
}

/// <summary>
/// 003 — VERIFICA ISOLATA Retrieval → Gap Stage (piano <c>validation/isolated-retrieval-gap-plan.md</c>). NON è T040 e un suo esito favorevole NON è un PASS di T040: il ledger è una fixture controllata e la
/// gerarchia di riconciliazione è SIMULATA (risposte deterministiche valide); sono REALI PostgreSQL + pgvector, endpoint di analisi, workflow v10 e checkpoint, indicizzazione, embedding, ricerca ibrida, riordino,
/// registro dei candidati, messaggio e schema dello stadio Gap, validatore, coverage-3 e API. Nella prova DGX l'UNICA richiesta generativa al DGX è quella dello stadio Gap (routing fail-closed,
/// <see cref="StageOnlyModelRouter"/>). Single e Multi sono test INDIPENDENTI (classi separate): nessuna campagna monolitica.
/// </summary>
public abstract class RetrievalGapIsolatedBase : ReconcileRig
{
    private const int OfflineWindow = 6000;   // 5 Gap: la risposta attesa dello stadio (~1750 token) deve stare nel budget di output di 2000
    private const string Header = "[ISOLATO: gerarchia SIMULATA; recupero e stadio Gap REALI; NON equivale a T040]";

    private readonly RetrievalCallRecorder _recorder = new();
    private readonly RetrievalTimingLog _timing = new();
    private readonly FakeEmbeddingServer _embedding = new();
    private readonly FakeRerankServer _rerank = new() { Scores = Overlap };
    private readonly RetrievalRig? _rig;
    private readonly HashSet<Guid> _excluded = [];
    private double _indexSeconds;

    protected RetrievalGapIsolatedBase()
    {
        if (!IsReal)
        {
            _rig = new RetrievalRig(_embedding, _rerank);
        }
    }

    /// <summary>True = servizi e modello REALI (prova DGX opt-in, ignorata senza ambiente); false = fornitori HTTP finti, nessuna rete.</summary>
    internal abstract bool IsReal { get; }

    private static string ReportDirectory => Path.Combine(Path.GetTempPath(), "bprojects-isolated-retrieval-gap");

    protected override void ConfigureServices(IServiceCollection services)
    {
        // Come i test fratelli: il run è guidato dal test, nessun dispatcher o worker di parsing concorrente (potrebbero rivendicare il run o riscrivere la preparazione seminata).
        foreach (var hosted in services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType is { } t && (t == typeof(AiDispatchService) || t == typeof(DocumentParseWorker))).ToList())
        {
            services.Remove(hosted);
        }

        if (IsReal)
        {
            if (TestEnvironment.DgxEndpoint is { } endpoint && TestEnvironment.DgxApiKey is { } key)
            {
                RetrievalGapCampaign.AddRetrieval(services, endpoint, key, _recorder, _timing);
            }

            return;
        }

        services.AddSingleton(_timing);
        services.Configure<RetrievalOptions>(o =>
        {
            o.Enabled = true;
            o.AllowedHosts = ["dgx.internal"];
            o.PolicyVersion = RetrievalPolicy.Version1;
            o.Embedding.Endpoint = _rig!.Options.Embedding.Endpoint;
            o.Embedding.ApiKey = _rig.Options.Embedding.ApiKey;
            o.Reranker.Endpoint = _rig.Options.Reranker.Endpoint;
            o.Reranker.ApiKey = _rig.Options.Reranker.ApiKey;
        });
        services.AddSingleton(_rig!.Embedding);
        services.AddSingleton(_rig.Reranker!);
        services.AddSingleton(_rig.Readiness);
    }

    /// <summary>Riordino finto deterministico (solo offline): parole (≥ 5 lettere) dell'interrogazione contenute nel passaggio.</summary>
    private static double[] Overlap(string query, string[] docs)
    {
        static HashSet<string> Words(string text) => [.. Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\d]{5,}").Select(m => m.Value)];
        var q = Words(query);
        return [.. docs.Select(d => (double)Words(d).Count(q.Contains))];
    }

    // ================================================================ seminagione del ledger controllato

    internal sealed record SeededDoc(IsolatedDocument Doc, Guid DocumentId, Guid VersionId, Guid PreparationId, IReadOnlyList<Guid> BlockIds, IReadOnlyList<Guid> EvidenceIds);

    internal sealed record Arranged(HttpClient Client, Guid Project, Guid Run, AnalysisRunClaim Claim, IsolatedLedger Ledger, IReadOnlyList<SeededDoc> Docs)
    {
        public IReadOnlyList<Guid> GapIds => Docs.Single(d => d.Doc.Key == RetrievalIsolatedFixture.QuestionsKey).EvidenceIds;

        public IReadOnlySet<Guid> ExcludedEvidence => Docs.Where(d => d.Doc.ExcludedByPm).SelectMany(d => d.EvidenceIds).ToHashSet();

        public IReadOnlySet<Guid> ExcludedDocuments => Docs.Where(d => d.Doc.ExcludedByPm).Select(d => d.DocumentId).ToHashSet();
    }

    /// <summary>Stesso meccanismo di <c>RetrievalGapFlowTests.NewRetrievalDocument</c>: documento caricato dall'API (riga documento/versione/preparazione) e blocchi, artefatto, evidenze e fonti seminati direttamente.</summary>
    private async Task<SeededDoc> SeedAsync(HttpClient client, Guid project, IsolatedDocument doc)
    {
        var created = await Json(await Upload(client, project, Text(doc.Key), doc.Key + ".txt", displayName: doc.Name));
        var (documentId, versionId) = (created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid());
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == versionId, Ct);
        var (artifact, job, version) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var blocks = doc.Blocks.Select(_ => Guid.NewGuid()).ToList();
        var now = DateTimeOffset.UtcNow;

        await using (var connection = new NpgsqlConnection(db.Database.GetConnectionString()))
        {
            await connection.OpenAsync(Ct);
            await new NpgsqlCommand("SET session_replication_role = replica", connection).ExecuteNonQueryAsync(Ct);
            await using var seed = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
            seed.ParsedArtifacts.Add(new ParsedArtifact
            {
                Id = artifact, ProjectId = preparation.ProjectId, PreparationId = preparation.Id, JobId = Guid.NewGuid(), FencingToken = 1, SchemaVersion = "parsed-1.0.0",
                StorageKey = "k/" + Guid.NewGuid().ToString("N"), Sha256 = new string('a', 64), ByteLength = 10, ParserConfigHash = new string('b', 64), BlockCount = doc.Blocks.Count,
            });
            for (var i = 0; i < doc.Blocks.Count; i++)
            {
                seed.DocumentBlocks.Add(new DocumentBlock
                {
                    Id = blocks[i], ProjectId = preparation.ProjectId, ArtifactId = artifact, Ordinal = doc.Blocks[i].Ordinal, Ref = $"b{doc.Blocks[i].Ordinal}", Type = BlockType.Paragraph,
                    CanonicalText = doc.Blocks[i].Text, TextHash = DocumentTestFixtures.Sha256(doc.Blocks[i].Text), HeadingPathJson = JsonSerializer.Serialize(doc.Blocks[i].Heading),
                });
            }

            await seed.SaveChangesAsync(Ct);
        }

        db.EvidenceExtractionJobs.Add(new EvidenceExtractionJob
        {
            Id = job, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Reason = ExtractionJobReason.Initial,
            Status = ExtractionJobStatus.Queued, RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
        });
        db.EvidenceExtractionVersions.Add(new EvidenceExtractionVersion
        {
            Id = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, ManifestHash = new string('1', 64),
            Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = doc.Evidences.Count, CreatedAt = now,
        });
        await db.SaveChangesAsync(Ct);

        var evidence = new List<Guid>();
        var blockIndex = doc.Blocks.Select((b, i) => (b.Ordinal, Index: i)).ToDictionary(t => t.Ordinal, t => t.Index);
        for (var i = 0; i < doc.Evidences.Count; i++)
        {
            var e = doc.Evidences[i];
            var content = new DocumentEvidenceContent
            {
                SchemaVersion = DocumentEvidenceContent.SchemaVersionValue,
                KindRationale = null,
                Structure = new DocumentEvidenceStructure { Subject = null, Action = null, Object = null, Scope = null, Purpose = null, Instrument = null, Channel = null, Recipient = null },
                Qualifiers = [],
                Sources =
                [
                    new PersistedSource
                    {
                        SourceId = Guid.NewGuid(), Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Guid.NewGuid(), Part = 0,
                        MessageId = null, Sequence = null, DocumentId = documentId, DocumentVersionId = versionId, PreparationId = preparation.Id,
                        ArtifactId = artifact, BlockId = blocks[blockIndex[e.BlockOrdinal]], Start = e.Start, End = e.End, Quote = e.Statement,
                    },
                ],
            };
            var id = Guid.NewGuid();
            evidence.Add(id);
            db.Evidences.Add(new EvidenceItem
            {
                Id = id, VersionId = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Ordinal = i + 1,
                Key = "e" + (i + 1), Kind = e.Kind, Polarity = e.Polarity, Certainty = EvidenceCertainty.Declared, Statement = e.Statement,
                Content = DocumentEvidenceCodec.EncodeContent(content),
            });
        }

        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = versionId, ProjectId = preparation.ProjectId, CurrentPreparationId = preparation.Id, CurrentEvidenceVersionId = version, Revision = 1, UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        preparation.Status = PreparationStatus.Ready;
        preparation.ParserVersion = "parser-1";
        preparation.CurrentEvidenceVersionId = version;
        await db.SaveChangesAsync(Ct);
        await db.ProjectDocuments.Where(d => d.Id == documentId).ExecuteUpdateAsync(x => x.SetProperty(d => d.LatestReadyVersionId, versionId), Ct);
        return new(doc, documentId, versionId, preparation.Id, blocks, evidence);
    }

    /// <summary>Progetto con il ledger controllato, indice ricostruito con i servizi di recupero configurati (finti o reali), documento d'estensione escluso dal PM, run richiesto dall'endpoint reale e rivendicato.</summary>
    internal async Task<Arranged> ArrangeAsync(CampaignScenario scenario)
    {
        var ledger = RetrievalIsolatedFixture.Build(scenario);
        var client = await App.LoginAsync();
        var project = await NewProject(client);
        var docs = new List<SeededDoc>();
        foreach (var d in ledger.Documents)
        {
            docs.Add(await SeedAsync(client, project, d));
        }

        // Indice: ricostruzione ESPLICITA come in produzione, misurata a parte. Include i documenti esclusi: l'esclusione è dello snapshot, non dell'indice.
        _recorder.Phase = "index";
        var watch = Stopwatch.StartNew();
        RetrievalRebuildSummary summary;
        using (var scope = App.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<AppDbContext>();
            var builder = new RetrievalIndexBuilder(db, sp.GetRequiredService<EmbeddingClient>(), sp.GetRequiredService<RetrievalModelReadiness>(), sp.GetRequiredService<IOptions<RetrievalOptions>>().Value);
            summary = await new RetrievalIndexMaintenance(db, builder).RebuildAsync(false, Ct);
        }

        _indexSeconds = watch.Elapsed.TotalSeconds;
        _recorder.Phase = "run";
        Assert.True(summary.Failed == 0 && summary.Ready == summary.Preparations && summary.Preparations == docs.Count,
            $"indicizzazione non completa: preparazioni={summary.Preparations} Ready={summary.Ready} Failed={summary.Failed}");

        foreach (var d in docs.Where(d => d.Doc.ExcludedByPm))
        {
            _excluded.Add(d.DocumentId);
        }

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{project}/source-selection")
        {
            Content = JsonContent.Create(new { items = _excluded.Select(d => new { documentId = d, included = false, mode = "LatestReady", versionId = (Guid?)null }).ToArray() }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(put, Ct)).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = (await Json(response)).GetProperty("id").GetGuid();
        return new(client, project, run, await ClaimAsync(run), ledger, docs);
    }

    // ================================================================ modelli: gerarchia simulata e stadio

    private sealed record Voice(string Alias, string Kind, string? Question, string Statement);

    private static List<Voice> Voices(string text) =>
        [.. Regex.Matches(text, @"<<<VOCE \[(E\d+)\] origine=\w+ tipo=(\w+)[^>\n]*?(?: domandaCanonica=(i\d+))?>>>\n([^\n]*)\n")
            .Select(m => new Voice(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success ? m.Groups[3].Value : null, m.Groups[4].Value))];

    private static string Fold(string text) => DocumentSmallFlowMafRealTests.Fold(text);

    /// <summary>Gerarchia simulata: risposte valide deterministiche (le stesse di <c>RetrievalGapFlowTests</c>); mai inoltrata al DGX.</summary>
    internal static string SimulatedHierarchy(string text) => ClampDescriptor(RetrievalGapFlowTests.Hierarchy(new DocumentChatWorkflowTests.ChatRequest(string.Empty, [("user", text)])));

    /// <summary>Il validatore di produzione limita il <c>descriptor</c> a 512 caratteri Unicode: con batch di taglia reale (centinaia di voci) il descriptor elencativo dello stub lo supera. Lo si abbrevia in modo deterministico.</summary>
    internal static string ClampDescriptor(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        if (node["descriptor"]?.GetValue<string>() is { } descriptor && descriptor.EnumerateRunes().Count() > 512)
        {
            node["descriptor"] = string.Concat(descriptor.EnumerateRunes().Take(480).Select(r => r.ToString())) + " …";
        }

        return node.ToJsonString();
    }

    /// <summary>
    /// Stadio Gap FINTO (solo offline): legge SOLO i candidati ricevuti e risponde secondo le attese annotate (nessuna attesa nuova). Il conflitto resta Open su openItem (la gerarchia simulata non offre conflitti).
    /// <paramref name="tamper"/> altera un record per i test negativi del verificatore.
    /// </summary>
    internal static string OfflineStage(string text, CampaignScenario scenario, Func<CampaignExpectation, JsonObject, string[], JsonObject>? tamper = null)
    {
        var voices = Voices(text);
        var candidates = voices.Where(v => v.Kind != "Gap").ToList();
        string[] Having(IEnumerable<string> alternatives) =>
            [.. candidates.Where(c => alternatives.Any(a => Fold(c.Statement).Contains(Fold(a), StringComparison.Ordinal))).Select(c => c.Alias)];
        var records = new JsonArray();
        foreach (var gap in voices.Where(v => v.Kind == "Gap"))
        {
            var e = scenario.Expectations.Single(x => Fold(gap.Statement).Contains(Fold(x.QuestionToken), StringComparison.Ordinal));
            var support = Having(e.SupportQuotes.SelectMany(q => q));
            var record = new JsonObject
            {
                ["gap"] = gap.Alias, ["outcome"] = e.Outcome, ["reason"] = e.Reasons[0], ["rationale"] = "Valutazione sui soli candidati ricevuti.",
                ["supportingEvidence"] = new JsonArray([.. support.Select(s => (JsonNode)JsonValue.Create(s)!)]),
                ["openItem"] = e.Outcome == "AnsweredInSnapshot" ? null : gap.Question, ["openConflict"] = null,
            };
            records.Add(tamper?.Invoke(e, record, [.. candidates.Select(c => c.Alias)]) ?? record);
        }

        return new JsonObject { ["contract"] = "gap-stage-1", ["gapAssessments"] = records }.ToJsonString();
    }

    private StageOnlyModelRouter OfflineRouter(CampaignScenario scenario, Func<CampaignExpectation, JsonObject, string[], JsonObject>? tamper = null, Func<string, string>? stage = null, int maxStageCalls = 1)
    {
        var upstream = new DocumentChatWorkflowTests.FakeChatModel { ContextWindow = OfflineWindow, Responder = r => (stage ?? (t => OfflineStage(t, scenario, tamper)))(r.AllText) };
        return new StageOnlyModelRouter(upstream, SimulatedHierarchy, maxStageCalls, ownsUpstream: true);
    }

    private static AiOptions RealOptions()
    {
        var options = TestEnvironment.RequireProductionLikeOptions();
        options.ContextWindowTokens = RetrievalGapCampaign.WindowTokens;
        options.Phases.Analysis.MaxOutputTokens = RetrievalGapCampaign.OutputTokens;
        options.Phases.Analysis.EnableThinking = RetrievalGapCampaign.Thinking;
        return options;
    }

    private AiOptions OfflineOptions() => new()
    {
        Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = OfflineWindow,
        MaxOutputTokens = 2000, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
    };

    /// <summary>Riscaldamento (come la campagna T040): UNA richiesta da 16 token che carica il modello, perche il runtime sia verificabile. Passa dal router (contata, al massimo una); nessun contenuto del progetto.</summary>
    private async Task WarmUpAsync(StageOnlyModelRouter router)
    {
        var options = RealOptions();
        using var client = new InternalModelClient(options, new NonOwningHandler(router));
        var warm = client.CreateChatOptions(temperature: 0, phase: AiPhase.Extraction);
        warm.MaxOutputTokens = 16;
        await client.ChatClient.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, StageOnlyModelRouter.WarmupText)], warm, Ct);
    }

    /// <summary>Workflow di produzione sul run, con il client di produzione davanti al router fail-closed. Offline: opzioni di test; reale: opzioni della campagna e <see cref="DocumentOptions"/> risolte dall'applicazione.</summary>
    internal async Task<AnalysisRunResult> ExecuteAsync(AnalysisRunClaim claim, StageOnlyModelRouter router)
    {
        var options = IsReal ? RealOptions() : OfflineOptions();
        using var client = new InternalModelClient(options, router);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        var documents = IsReal ? App.Services.GetRequiredService<IOptions<DocumentOptions>>() : Options.Create(new DocumentOptions());
        return await new DocumentAnalysisWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, documents).ExecuteAsync(claim, Ct);
    }

    // ================================================================ verifiche

    private async Task<(string Status, string Code, string Message, int Calls)> RunRow(Guid run) => (
        await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<string>("SELECT coalesce(\"ErrorCode\",'') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<string>("SELECT coalesce(\"ErrorMessage\",'') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<int>("SELECT coalesce(\"CallCount\",0) FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));

    private async Task<RetrievalRegistry> RegistryOf(Guid run) => RetrievalOutput.Read(await Scalar<string>(CampaignSql.RegistryPayload, ("r", run)));

    private sealed record LoadedSource(Guid DocumentId, Guid PreparationId, Guid BlockId, int Start, int End, string Quote);

    private async Task<Dictionary<Guid, (EvidenceKind Kind, IReadOnlyList<LoadedSource> Sources)>> LoadEvidenceAsync(Guid project)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Evidences.AsNoTracking().Where(e => e.ProjectId == project).Select(e => new { e.Id, e.Kind, e.Content }).ToListAsync(Ct);
        return rows.ToDictionary(
            r => r.Id,
            r => (r.Kind, (IReadOnlyList<LoadedSource>)(r.Kind == EvidenceKind.Gap ? [] : [.. DocumentEvidenceCodec.DecodeContent(r.Content).Sources
                .Where(s => s.Kind == EvidenceSourceKind.DocumentPassage).Select(s => new LoadedSource(s.DocumentId!.Value, s.PreparationId!.Value, s.BlockId!.Value, s.Start, s.End, s.Quote!))])));
    }

    private static async Task<string?> PassageAsync(HttpClient client, Guid project, LoadedSource s)
    {
        var response = await client.GetAsync($"/api/projects/{project}/documents/{s.DocumentId}/versions/1/preparations/{s.PreparationId}/passages/{s.BlockId}?start={s.Start}&end={s.End}", Ct);
        return response.IsSuccessStatusCode ? (await Json(response)).GetProperty("quote").GetProperty("text").GetString() : null;
    }

    private static string Short(string text, int max = 160) => LargeDocumentRealFlow.Short(text, max);

    /// <summary>Verifica del run completato: stessi criteri funzionali di T040 dove applicabili; quelli non dimostrabili con la gerarchia simulata sono NotVerified.</summary>
    internal async Task<Verdicts> VerifyAsync(Arranged a, StageOnlyModelRouter router, AnalysisRunResult result)
    {
        const string l = "[ISOLATO]";
        var v = new Verdicts();
        var expectations = a.Ledger.Scenario.Expectations;
        var args = ("r", (object)a.Run);
        var row = await RunRow(a.Run);

        v.Check($"{l} violazioni di instradamento: nessuna (una richiesta non riconosciuta o una seconda generativa farebbe fallire)", router.Violations.Count == 0, string.Join(" | ", router.Violations));
        v.Check($"{l} run terminale Completed (un errore gestito non e un PASS)", result.Status == ProcessingRunStatus.Completed, $"{row.Status} {row.Code}: {Short(row.Message, 200)}");
        v.Check($"{l} una sola richiesta di contenuto inoltrata al modello a monte (lo stadio Gap), piu il solo riscaldamento da 16 token nella prova reale, e la gerarchia solo simulata", router.ForwardedStageCalls == 1 && router.ChatRequests > 1 && router.ForwardedWarmups == (IsReal ? 1 : 0),
            $"stadio inoltrato={router.ForwardedStageCalls} chat totali={router.ChatRequests}");
        if (result.Status != ProcessingRunStatus.Completed)
        {
            v.NotVerified($"{l} criteri successivi (registro, stadio, citazioni, conflitti, latenza)", "il run non e terminato Completed: nessun esito dello stadio da giudicare");
            return v;
        }

        var retrievalBatches = await Count(CampaignSql.StageBatches, args, ("s", "retrieval"));
        var gapBatches = await Count(CampaignSql.StageBatches, args, ("s", "gap"));
        var retrievalOutputs = await Count(CampaignSql.CommittedOutputs, args, ("s", "retrieval-output-1"));
        var stageOutputs = await Count(CampaignSql.CommittedOutputs, args, ("s", "gap-stage-alias-1"));
        v.Check($"{l} (1) un batch di recupero con un WorkOutput retrieval-output-1 e un batch dello stadio con un WorkOutput gap-stage-alias-1", retrievalBatches == 1 && gapBatches == 1 && retrievalOutputs == 1 && stageOutputs == 1,
            $"recupero={retrievalBatches}/{retrievalOutputs} stadio={gapBatches}/{stageOutputs}");
        var retrievalOrdinal = await Scalar<int>(CampaignSql.StageOrdinal, args, ("s", "retrieval"));
        var gapOrdinal = await Scalar<int>(CampaignSql.StageOrdinal, args, ("s", "gap"));
        v.Check($"{l} (1) il recupero precede immediatamente lo stadio Gap, dopo la gerarchia", gapOrdinal == retrievalOrdinal + 1 && retrievalOrdinal > 0, $"recupero={retrievalOrdinal} stadio={gapOrdinal}");
        var batches = await Count(RunSignalSql.Batches, args);
        v.Check($"{l} (1) una chiamata per batch con modello e nessuna per il recupero (CallCount = richieste generative del router = batch - 1), nessun retry",
            row.Calls == router.ChatRequests && batches - 1 == router.ChatRequests, $"CallCount={row.Calls} router={router.ChatRequests} batch={batches}");
        var stageCall = router.Calls.FirstOrDefault(c => c.Kind == StageOnlyModelRouter.PhaseStage && c.Forwarded);
        v.Check($"{l} (7) la chiamata dello stadio non e troncata (finish_reason = stop, osservato) e ha risposto 2xx", stageCall is { Status: >= 200 and < 300 } && string.Equals(stageCall.FinishReason, "stop", StringComparison.OrdinalIgnoreCase),
            $"status={stageCall?.Status} finish={stageCall?.FinishReason} prompt={stageCall?.PromptTokens} completion={stageCall?.CompletionTokens} ms={stageCall?.Milliseconds:F0}");
        if (IsReal)
        {
            v.Check($"{l} (7) il prompt dello stadio entra nel budget di input fissato ({RetrievalGapCampaign.BudgetTokens} token)", stageCall?.PromptTokens is > 0 and var prompt && prompt <= RetrievalGapCampaign.BudgetTokens, $"prompt={stageCall?.PromptTokens}");
        }

        // (2) registro congelato nel WorkOutput: contratto, politica, esclusioni.
        var registry = await RegistryOf(a.Run);
        v.Check($"{l} (2) registro {CandidateRegistry.Contract}: sorgente retrieval, esito Package, non degradato, senza cause",
            registry.Contract == CandidateRegistry.Contract && registry.SearchBasis == CandidateRegistry.SearchBasis && registry.Outcome == CandidateRegistry.OutcomePackage && !registry.Degraded && registry.Causes.Count == 0,
            $"{registry.Contract} {registry.SearchBasis} {registry.Outcome} degraded={registry.Degraded} cause=[{string.Join(",", registry.Causes)}]");
        v.Check($"{l} (2) politica {RetrievalGapCampaign.PolicyVersion} con il suo hash", registry.PolicyVersion == RetrievalGapCampaign.PolicyVersion && registry.PolicyHash == RetrievalPolicy.V1.Hash());
        v.Check($"{l} (2) snapshot e indice del registro persistiti con provenienza", registry.SnapshotHash.Length == 64 && registry.IndexConfigHash.Length > 0 && registry.Hash().Length > 0, $"snapshot={Short(registry.SnapshotHash, 16)} indice={Short(registry.IndexConfigHash, 16)}");
        if (IsReal)
        {
            v.Check($"{l} (2) modelli e artefatti osservati dalla readiness uguali a quelli fissati",
                registry.Readiness?.Embedding is { } emb && emb.Model == RetrievalGapCampaign.EmbeddingModelId && emb.Artifact == RetrievalGapCampaign.EmbeddingArtifact && emb.Dimension == RetrievalGapCampaign.EmbeddingDimension
                && registry.Readiness?.Reranker is { } rer && rer.Model == RetrievalGapCampaign.RerankerModelId && rer.Artifact == RetrievalGapCampaign.RerankerArtifact, $"emb={registry.Readiness?.Embedding} rer={registry.Readiness?.Reranker}");
        }
        else
        {
            v.NotApplicable($"{l} (2) modelli e artefatti osservati = quelli fissati", "fornitori finti: nessun modello reale osservato");
        }

        v.Check($"{l} (2) un'entrata del registro per ciascun Gap controllato ({expectations.Count}) e un pacchetto non vuoto", registry.Gaps.Count == expectations.Count && registry.PackageEvidence.Count > 0, $"gap={registry.Gaps.Count} pacchetto={registry.PackageEvidence.Count}");
        var api = await a.Client.GetAsync($"/api/projects/{a.Project}/analyses/{a.Run}/retrieval-registry", Ct);
        var apiBody = api.IsSuccessStatusCode ? await Json(api) : default;
        v.Check($"{l} (2) il registro e consultabile dall'API (stessa sorgente, esito, politica, un'entrata per Gap)", api.IsSuccessStatusCode && apiBody.GetProperty("searchBasis").GetString() == registry.SearchBasis
            && apiBody.GetProperty("outcome").GetString() == registry.Outcome && apiBody.GetProperty("policyVersion").GetString() == registry.PolicyVersion && apiBody.GetProperty("gaps").GetArrayLength() == registry.Gaps.Count, ((int)api.StatusCode).ToString(CultureInfo.InvariantCulture));

        // (2) documenti esclusi: mai fra candidati, pacchetto, sempre-incluse ed esclusi; ma nell'indice.
        var excludedEvidence = a.ExcludedEvidence;
        v.Check($"{l} (2) il documento escluso ha evidenze nel ledger (la prova di esclusione non e vuota)", excludedEvidence.Count > 0, $"{excludedEvidence.Count}");
        var candidateEvidence = registry.Gaps.SelectMany(g => g.Candidates).SelectMany(c => c.EvidenceIds).ToHashSet();
        v.Check($"{l} (2) nessuna evidenza dei documenti esclusi fra candidati, pacchetto, sempre-incluse ed esclusi del registro",
            !registry.Gaps.SelectMany(g => g.Candidates).Any(c => a.ExcludedDocuments.Contains(c.DocumentId)) && !candidateEvidence.Overlaps(excludedEvidence) && !registry.PackageEvidence.Any(excludedEvidence.Contains)
            && !registry.AlwaysIncluded.Any(x => excludedEvidence.Contains(x.EvidenceId)) && !registry.Excluded.Any(x => excludedEvidence.Contains(x.EvidenceId)));
        v.Check($"{l} (2) il documento escluso e nell'indice Ready: l'esclusione e dello snapshot, non un'assenza dall'indice",
            await Count(CampaignSql.IndexedExcluded, ("p", a.Project), ("d", a.ExcludedDocuments.ToArray())) == a.ExcludedDocuments.Count);

        // (3) pacchetto consegnato allo stadio.
        var stage = JsonDocument.Parse(await Scalar<string>(CampaignSql.StagePayload, args)).RootElement;
        var info = stage.GetProperty("stage");
        v.Check($"{l} (3) lo stadio ha ricevuto SOLO il pacchetto recuperato: sorgente retrieval, candidati = pacchetto del registro, retrievalComplete come nel registro",
            info.GetProperty("searchBasis").GetString() == "retrieval" && info.GetProperty("candidates").GetInt32() == registry.PackageEvidence.Count && info.GetProperty("retrievalComplete").GetBoolean() == registry.RetrievalComplete,
            $"candidati={info.GetProperty("candidates").GetInt32()} pacchetto={registry.PackageEvidence.Count}");
        var wire = router.StageRequestTexts.FirstOrDefault() ?? string.Empty;
        v.Check($"{l} (3) il messaggio realmente inviato allo stadio contiene tanti candidati quante evidenze del pacchetto e nessun testo dei documenti esclusi",
            wire.Length > 0 && Voices(wire).Count(x => x.Kind != "Gap") == registry.PackageEvidence.Count
            && !a.Ledger.Documents.Where(d => d.ExcludedByPm).SelectMany(d => d.Evidences).Any(e => wire.Contains(e.Statement, StringComparison.Ordinal)),
            $"candidati nel messaggio={Voices(wire).Count(x => x.Kind != "Gap")} pacchetto={registry.PackageEvidence.Count}");
        var selected = await Count(RunSignalSql.SelectedEvidences, args);
        v.Check($"{l} (3) il ledger NON entrava nello stadio: il pacchetto e un sottoinsieme proprio del ledger non-Gap", registry.PackageEvidence.Count < selected - expectations.Count,
            $"pacchetto={registry.PackageEvidence.Count} ledger non-Gap={selected - expectations.Count}");

        // (4) coverage-3, esiti, supporti, citazioni, assenze.
        var coverage = await a.Client.PostAsJsonAsync($"/api/projects/{a.Project}/analyses/{a.Run}/coverage-reports", new { revisionId = result.ResultRevisionId }, Ct);
        var report = await Json(coverage);
        v.Check($"{l} (4) coverage-3 creato", (coverage.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created) && report.TryGetProperty("checkerVersion", out var cv) && cv.GetString() == "coverage-3", $"{(int)coverage.StatusCode} {Short(report.GetRawText(), 200)}");
        if (!report.TryGetProperty("gapAssessments", out var assessments) || assessments.ValueKind != JsonValueKind.Array)
        {
            v.Check($"{l} (4) gapAssessments presente", false);
            return v;
        }

        var records = assessments.EnumerateArray().ToList();
        var summary = report.GetProperty("summary");
        v.Check($"{l} (4) un record valido per ciascun Gap controllato: un solo record per Gap, nessun Gap non valutato", summary.GetProperty("unassessedGaps").GetInt32() == 0 && records.Count == a.GapIds.Count
            && a.GapIds.All(id => records.Count(r => r.GetProperty("gapEvidenceId").GetGuid() == id) == 1), $"gap={a.GapIds.Count} record={records.Count}");

        var evidences = await LoadEvidenceAsync(a.Project);
        var packageIds = registry.PackageEvidence.ToHashSet();
        var falseAnswered = new List<string>();
        var invalid = 0;
        var total = 0;
        for (var i = 0; i < expectations.Count; i++)
        {
            var expected = expectations[i];
            var gapId = a.GapIds[i];
            var record = records.FirstOrDefault(r => r.GetProperty("gapEvidenceId").GetGuid() == gapId);
            if (record.ValueKind != JsonValueKind.Object)
            {
                v.Check($"{l} {expected.Id}: record presente", false);
                continue;
            }

            var outcome = record.GetProperty("outcome").GetString();
            var reason = record.GetProperty("reason").GetString();
            var detail = Short(record.GetRawText(), 380);
            if (outcome == "AnsweredInSnapshot" && expected.Outcome != "AnsweredInSnapshot")
            {
                falseAnswered.Add(expected.Id);
            }

            v.Check($"{l} (4) {expected.Id} ({expected.Kind}): esito {expected.Outcome} con motivo in [{string.Join("/", expected.Reasons)}]", outcome == expected.Outcome && expected.Reasons.Contains(reason), detail);
            v.Check($"{l} {expected.Id}: motivazione e snapshot hash presenti", (record.GetProperty("rationale").GetString() ?? string.Empty).Trim().Length > 0 && (record.GetProperty("sourceSnapshotHash").GetString() ?? string.Empty).Length > 0);

            var support = record.GetProperty("supportingEvidenceIds").EnumerateArray().Select(x => x.GetGuid()).ToList();
            var openItem = record.GetProperty("openItemId").ValueKind != JsonValueKind.Null;
            var openConflict = record.GetProperty("openConflictId").ValueKind != JsonValueKind.Null;
            v.Check($"{l} (4) {expected.Id}: ogni supporto e un'evidenza documentale del pacchetto recuperato (mai un Gap, mai fuori pacchetto, mai di un documento escluso)",
                support.All(id => packageIds.Contains(id) && evidences.TryGetValue(id, out var ev) && ev.Kind != EvidenceKind.Gap && ev.Sources.Count > 0 && !excludedEvidence.Contains(id)), $"supporti={support.Count}");
            var sources = support.Where(evidences.ContainsKey).SelectMany(id => evidences[id].Sources).ToList();
            var invalidHere = 0;
            foreach (var source in sources)
            {
                total++;
                if (await PassageAsync(a.Client, a.Project, source) != source.Quote)
                {
                    invalidHere++;
                    invalid++;
                }
            }

            v.Notes.Add($"{expected.Id}: {sources.Count} citazioni di supporto, {invalidHere} non esatte nel blocco");
            foreach (var quote in expected.SupportQuotes)
            {
                v.Check($"{l} (4) {expected.Id}: tra i supporti c'e il riscontro «{quote[0]}»", sources.Any(s => quote.Any(q => Fold(s.Quote).Contains(Fold(q), StringComparison.Ordinal))));
            }

            foreach (var forbidden in expected.ForbiddenQuotes)
            {
                v.Check($"{l} (4) {expected.Id}: nessun supporto contiene «{forbidden}»", !sources.Any(s => Fold(s.Quote).Contains(Fold(forbidden), StringComparison.Ordinal)));
            }

            v.Check($"{l} (4) {expected.Id}: " + (expected.Outcome == "AnsweredInSnapshot" ? "nessun riferimento Open e almeno un supporto" : "almeno un riferimento Open (openItem o openConflict)"),
                expected.Outcome == "AnsweredInSnapshot" ? !openItem && !openConflict && support.Count > 0 : openItem || openConflict, $"openItem={openItem} openConflict={openConflict}");
            v.Check($"{l} (4) {expected.Id}: sorgente retrieval dichiarata e assenza MAI provata", record.TryGetProperty("searchBasis", out var basis) && basis.GetString() == "retrieval"
                && record.TryGetProperty("absenceProven", out var proven) && proven.ValueKind == JsonValueKind.False, detail);

            if (expected.ExpectOpenConflict)
            {
                // Parte RECUPERO di SC-003: entrambi i lati nel pacchetto e non solo del documento escluso. È l'unica cosa dimostrabile senza una gerarchia che offra conflitti.
                foreach (var side in expected.SupportQuotes)
                {
                    var sideEvidence = evidences.Where(e => e.Value.Kind != EvidenceKind.Gap && e.Value.Sources.Any(s => side.Any(x => Fold(s.Quote).Contains(Fold(x), StringComparison.Ordinal)))).Select(e => e.Key).ToList();
                    v.Check($"{l} (5) {expected.Id}: il lato «{side[0]}» non e perso a monte — almeno una sua evidenza e nel pacchetto recuperato e nessuna e del documento escluso",
                        sideEvidence.Any(packageIds.Contains) && !sideEvidence.All(excludedEvidence.Contains), $"evidenze del lato={sideEvidence.Count}");
                }

                v.NotVerified($"{l} (5) {expected.Id}: conflitto Open nella revisione (openConflict valorizzato, nessuna risoluzione automatica)",
                    "la gerarchia simulata non offre conflitti allo stadio: la conservazione del conflitto richiede la riconciliazione reale (end-to-end)");
            }
        }

        v.Check($"{l} (4) 0 falsi AnsweredInSnapshot ({falseAnswered.Count}: {string.Join(",", falseAnswered)}) e nessun AnsweredInSnapshot fuori dalle attese",
            falseAnswered.Count == 0 && records.Count(r => r.GetProperty("outcome").GetString() == "AnsweredInSnapshot") == expectations.Count(e => e.Outcome == "AnsweredInSnapshot"));
        v.Check($"{l} (4) citazioni invalide ≤ 2 su 10: {invalid} su {total} (proporzione {RetrievalGapCampaign.MaxInvalidCitationsPer10}/10)", total > 0 && RetrievalGapCampaign.WithinPer10(invalid, total, RetrievalGapCampaign.MaxInvalidCitationsPer10));
        v.NotVerified($"{l} la domanda e stata ESTRATTA come una evidenza Gap dal modello", "il ledger e una fixture: l'estrazione LLM non e esercitata (richiede l'end-to-end)");
        v.NotVerified($"{l} espansione v10, rischio Cross e limite di 512 chiamate", "la gerarchia e simulata: nessun candidato, nessuna espansione");

        // (6) tempi e chiamate del recupero.
        var timing = _timing.Entries;
        v.Notes.Add($"recupero: totale pipeline {timing.Where(e => e.Phase == RetrievalTimingLog.PhaseTotal).Sum(e => e.Milliseconds):F0} ms; ricerca {timing.Where(e => e.Phase == RetrievalTimingLog.PhaseSearch).Sum(e => e.Milliseconds):F0} ms; riordino {timing.Where(e => e.Phase == RetrievalTimingLog.PhaseRerank).Sum(e => e.Milliseconds):F0} ms");
        if (IsReal)
        {
            var latency = LatencyReport.Build(timing, _recorder.Calls, a.GapIds);
            v.Check($"{l} (6) latenza del recupero misurata per ciascun Gap (ricerca e riordino completi)", latency.PerGap.Count == a.GapIds.Count && latency.PerGap.All(g => !g.Incomplete || g.EmbeddingCalls >= 1), string.Join(" | ", latency.Describe(id => id.ToString("N")[..8])));
            v.Check($"{l} (6) nessuna chiamata di embedding/riordino con esito non 2xx (nessun fallback silenzioso)", _recorder.Calls.All(c => c.Status is >= 200 and < 300));
        }
        else
        {
            v.NotApplicable($"{l} (6) latenza del recupero con servizi reali", "fornitori finti");
        }

        return v;
    }

    /// <summary>Replay: nuovo owner e fence sul run già Completed, router senza richieste generative ammesse. Nessuna nuova chiamata, stesso registro, nessun duplicato.</summary>
    internal async Task<Verdicts> VerifyReplayAsync(Arranged a, Func<StageOnlyModelRouter> newRouter)
    {
        const string l = "[ISOLATO/REPLAY]";
        var v = new Verdicts();
        var registry = await RegistryOf(a.Run);
        var before = (Outputs: await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", a.Run)), Batches: await Count(RunSignalSql.Batches, ("r", a.Run)), Calls: await Count(CampaignSql.InferenceCalls, ("r", a.Run)));
        var retrievalRequests = RetrievalRequests();
        await Execute("UPDATE \"ProcessingRuns\" SET \"LeaseOwner\" = 'owner-2', \"FencingToken\" = 2, \"AttemptCount\" = 2 WHERE \"Id\" = @r", ("r", a.Run));
        var router = newRouter();
        var replay = await ExecuteAsync(new AnalysisRunClaim(a.Run, "owner-2", 2, 2), router);
        v.Check($"{l} il run gia Completed resta Completed", replay.Status == ProcessingRunStatus.Completed, replay.ErrorCode ?? string.Empty);
        v.Check($"{l} nessuna richiesta generativa e nessuna violazione", router.ChatRequests == 0 && router.Violations.Count == 0, $"chat={router.ChatRequests} violazioni={string.Join("|", router.Violations)}");
        v.Check($"{l} nessuna nuova richiesta ai servizi di recupero (embedding, riordino)", RetrievalRequests() == retrievalRequests, $"prima={retrievalRequests} dopo={RetrievalRequests()}");
        v.Check($"{l} stesso registro, nessun WorkOutput, batch o chiamata duplicati per output Committed", (await RegistryOf(a.Run)).Hash() == registry.Hash()
            && (await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", a.Run)), await Count(RunSignalSql.Batches, ("r", a.Run)), await Count(CampaignSql.InferenceCalls, ("r", a.Run))) == (before.Outputs, before.Batches, before.Calls));
        return v;
    }

    private int RetrievalRequests() => IsReal ? _recorder.Calls.Count(c => c.Phase == "run" && c.Kind is "embedding" or "rerank") : _embedding.EmbeddingRequests + _rerank.RerankRequests;

    // ================================================================ prova reale: preflight e rapporto

    /// <summary>NO-GO prima di qualunque richiesta al DGX: ambiente, manifesto T040 invariato, identità del prodotto, commit di lancio, opzioni risolte, manifesto isolato. Skipped (mai PASS) senza opt-in.</summary>
    internal void RealPreflight(CampaignScenario scenario)
    {
        TestEnvironment.RequireDgx();
        var ai = RealOptions();
        var problems = new List<string>(RetrievalGapCampaign.ValidateEnvironment(Environment.GetEnvironmentVariable));
        var retrieval = App.Services.GetRequiredService<IOptions<RetrievalOptions>>().Value;
        problems.AddRange(RetrievalGapCampaign.RejectIdentity(RetrievalGapCampaign.AnalysisIdentity.FromProduct(retrieval), RetrievalGapCampaign.DefinitionVersion));
        problems.AddRange(RetrievalGapCampaign.RejectLaunch(RetrievalGapCampaign.LaunchRecord.Read()));
        if (RetrievalGapCampaign.RejectRetrievalOptions(retrieval) is { } r)
        {
            problems.Add("RetrievalOptions risolte: " + r);
        }

        if (RetrievalGapCampaign.RejectOptions(ai) is { } o)
        {
            problems.Add("AiOptions: " + o);
        }

        if (RetrievalGapCampaign.RejectDocumentOptions(App.Services.GetRequiredService<IOptions<DocumentOptions>>().Value) is { } d)
        {
            problems.Add("DocumentOptions risolte: " + d);
        }

        if (RetrievalIsolatedManifest.Read() is not { } file || file.Replace("\r\n", "\n", StringComparison.Ordinal) != RetrievalIsolatedFixture.BuildManifestJson().Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            problems.Add("il manifesto isolato versionato non coincide con la fixture");
        }

        Assert.True(problems.Count == 0, $"{Header} configurazione incompatibile (NO-GO), nessuna richiesta al modello: " + string.Join(" | ", problems));
        _ = scenario;
    }

    /// <summary>Esecuzione completa di UNO scenario con report su file (anche in caso di errore).</summary>
    internal async Task<Verdicts> RunScenarioAsync(CampaignScenario scenario, Func<StageOnlyModelRouter>? routerFactory = null)
    {
        var all = new Verdicts();
        StageOnlyModelRouter? router = null;
        Arranged? arranged = null;
        Exception? failure = null;
        try
        {
            if (IsReal)
            {
                RealPreflight(scenario);
            }

            arranged = await ArrangeAsync(scenario);
            StageOnlyModelRouter New() => routerFactory?.Invoke() ?? (IsReal ? new StageOnlyModelRouter(MafDiagnostics.Wrap(), SimulatedHierarchy, ownsUpstream: true, maxWarmupCalls: 1) : OfflineRouter(scenario));
            router = New();
            if (IsReal)
            {
                await WarmUpAsync(router);
            }

            var result = await ExecuteAsync(arranged.Claim, router);
            try
            {
                all.Add(await VerifyAsync(arranged, router, result));
            }
            catch (Exception e) when (e is not Xunit.Sdk.SkipException)
            {
                all.Check("[ISOLATO] il verificatore e terminato con un'eccezione (le verifiche successive non sono state eseguite)", false, e.GetType().Name + ": " + Short(e.Message, 300));
            }

            if (result.Status == ProcessingRunStatus.Completed && !all.HasFail)
            {
                all.Add(await VerifyReplayAsync(arranged, () => IsReal ? new StageOnlyModelRouter(MafDiagnostics.Wrap(), SimulatedHierarchy, maxStageCalls: 0, ownsUpstream: true) : OfflineRouter(scenario, maxStageCalls: 0)));
            }
        }
        catch (Exception e) when (e is not Xunit.Sdk.SkipException)
        {
            failure = e;
            throw;
        }
        finally
        {
            if (arranged is not null || failure is not null)
            {
                WriteReport(scenario, arranged, router, all, failure);
            }
        }

        return all;
    }

    private void WriteReport(CampaignScenario scenario, Arranged? a, StageOnlyModelRouter? router, Verdicts verdicts, Exception? failure)
    {
        try
        {
            var calls = router?.Calls ?? [];
            var byPhase = StageOnlyModelRouter.ByPhase(calls);
            var timing = _timing.Entries;
            var stub = calls.Where(c => c.Kind == StageOnlyModelRouter.PhaseHierarchyStub).ToList();
            var stageCall = calls.FirstOrDefault(c => c.Kind == StageOnlyModelRouter.PhaseStage);
            var report = new JsonObject
            {
                ["header"] = Header,
                ["manifest"] = RetrievalIsolatedFixture.Id,
                ["scenario"] = scenario.Label,
                ["mode"] = IsReal ? "real-services" : "fake-providers-offline",
                ["hierarchy"] = "SIMULATED (deterministic stub; no generative call to the DGX)",
                ["retrievalAndGapStage"] = IsReal ? "REAL (production components, real embedding/reranker/LLM)" : "production components over FAKE providers (protocol only)",
                ["notEquivalentToT040"] = true,
                ["ledgerSha256"] = a?.Ledger.Sha256,
                ["ledger"] = a is null ? null : new JsonObject { ["documents"] = a.Ledger.Documents.Count, ["blocks"] = a.Ledger.BlockCount, ["evidences"] = a.Ledger.EvidenceCount, ["includedNonGap"] = a.Ledger.IncludedNonGapEvidenceCount },
                ["timingsSeconds"] = new JsonObject
                {
                    ["indexing"] = Math.Round(_indexSeconds, 2),
                    ["retrievalPipelineTotal"] = Math.Round(timing.Where(e => e.Phase == RetrievalTimingLog.PhaseTotal).Sum(e => e.Milliseconds) / 1000, 3),
                    ["retrievalSearch"] = Math.Round(timing.Where(e => e.Phase == RetrievalTimingLog.PhaseSearch).Sum(e => e.Milliseconds) / 1000, 3),
                    ["retrievalRerank"] = Math.Round(timing.Where(e => e.Phase == RetrievalTimingLog.PhaseRerank).Sum(e => e.Milliseconds) / 1000, 3),
                    ["gapStageCall"] = stageCall is null ? null : Math.Round(stageCall.Milliseconds / 1000, 3),
                    ["hierarchySimulatedSpan"] = stub.Count < 2 ? null : Math.Round((stub.Max(c => c.At) - stub.Min(c => c.At)).TotalSeconds, 2),
                },
                ["modelRequests"] = new JsonObject
                {
                    ["generativeForwardedToUpstream"] = router?.ForwardedStageCalls,
                    ["hierarchySimulatedLocal"] = stub.Count,
                    ["violations"] = new JsonArray([.. (router?.Violations ?? []).Select(x => (JsonNode)JsonValue.Create(x)!)]),
                    ["forwardedByEndpointAndPhase"] = new JsonArray([.. byPhase.Select(p => (JsonNode)new JsonObject
                    {
                        ["path"] = p.Path, ["phase"] = p.Phase, ["requests"] = p.Requests, ["totalMs"] = Math.Round(p.TotalMilliseconds, 1), ["maxMs"] = Math.Round(p.MaxMilliseconds, 1), ["nonSuccess"] = p.NonSuccess,
                    })]),
                },
                ["retrievalServiceRequests"] = IsReal
                    ? new JsonArray([.. _recorder.Calls.GroupBy(c => (c.Kind, c.Phase)).Select(g => (JsonNode)new JsonObject { ["kind"] = g.Key.Kind, ["phase"] = g.Key.Phase, ["requests"] = g.Count(), ["totalMs"] = Math.Round(g.Sum(c => c.Milliseconds), 1) })])
                    : new JsonArray { new JsonObject { ["embeddingRequests"] = _embedding.EmbeddingRequests, ["rerankRequests"] = _rerank.RerankRequests } },
                ["verdicts"] = verdicts.ToJson(),
                ["counts"] = new JsonObject
                {
                    ["pass"] = verdicts.Items.Count(i => i.Status == VerdictStatus.Pass), ["fail"] = verdicts.Items.Count(i => i.Status == VerdictStatus.Fail),
                    ["notVerified"] = verdicts.Items.Count(i => i.Status == VerdictStatus.NotVerified), ["notApplicable"] = verdicts.Items.Count(i => i.Status == VerdictStatus.NotApplicable),
                },
                ["notes"] = new JsonArray([.. verdicts.Notes.Select(x => (JsonNode)JsonValue.Create(x)!)]),
                ["exception"] = failure is null ? null : failure.GetType().Name + ": " + Short(failure.Message, 600),
            };
            Directory.CreateDirectory(ReportDirectory);
            File.WriteAllText(Path.Combine(ReportDirectory, $"report-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}-{scenario.Label}-{(IsReal ? "real" : "offline")}.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            // Un errore nel rapporto non altera l'esito del test, ma non è silenzioso.
            Console.Error.WriteLine($"[ISOLATO] rapporto non scritto: {e.GetType().Name}: {e.Message}");
        }
    }

    // ================================================================ test OFFLINE (fornitori HTTP finti; PostgreSQL e MAF reali)

    [Trait("Category", TestCategories.Integration)]
    public sealed class Offline : RetrievalGapIsolatedBase
    {
        internal override bool IsReal => false;

        [Fact]
        public async Task The_single_scenario_runs_retrieval_and_the_gap_stage_over_the_controlled_ledger_with_one_stage_call()
        {
            var v = await RunScenarioAsync(RetrievalGapCampaign.Single);
            Assert.False(v.HasFail, v.ToString());
            Assert.Contains(v.Items, i => i.Status == VerdictStatus.NotVerified && i.Label.Contains("openConflict", StringComparison.Ordinal));
        }

        [Fact]
        public async Task The_multi_scenario_runs_independently_with_the_same_contract()
        {
            var v = await RunScenarioAsync(RetrievalGapCampaign.Multi);
            Assert.False(v.HasFail, v.ToString());
        }

        [Fact]
        public async Task A_stage_that_answers_a_gap_without_an_answer_is_reported_as_a_false_AnsweredInSnapshot()
        {
            var scenario = RetrievalGapCampaign.Single;
            var arranged = await ArrangeAsync(scenario);
            var router = OfflineRouter(scenario, tamper: (e, record, candidates) =>
            {
                if (e.Id == "absent")
                {
                    // Risposta VALIDA per il validatore (supporto fra i candidati ricevuti) ma sbagliata rispetto all'attesa: il run completa e il verificatore deve rilevarla.
                    record["outcome"] = "AnsweredInSnapshot";
                    record["reason"] = "Answered";
                    record["openItem"] = null;
                    record["supportingEvidence"] = new JsonArray(JsonValue.Create(candidates[0]));
                }

                return record;
            });
            var result = await ExecuteAsync(arranged.Claim, router);
            var v = await VerifyAsync(arranged, router, result);
            Assert.Equal(ProcessingRunStatus.Completed, result.Status);
            Assert.Contains(v.Items, i => i.Status == VerdictStatus.Fail && i.Label.Contains("0 falsi AnsweredInSnapshot", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_second_generative_stage_request_fails_the_run_and_is_reported_as_a_violation()
        {
            var scenario = RetrievalGapCampaign.Single;
            var arranged = await ArrangeAsync(scenario);
            var router = OfflineRouter(scenario, maxStageCalls: 0);
            var failure = await Record.ExceptionAsync(() => ExecuteAsync(arranged.Claim, router));   // fail-closed: la violazione interrompe il workflow, non viene assorbita

            Assert.True(failure is not null || (await RunRow(arranged.Run)).Status != "Completed");
            Assert.NotEmpty(router.Violations);
            Assert.Contains("non prevista", router.Violations[0], StringComparison.Ordinal);
            Assert.Equal(0, router.ForwardedStageCalls);                                              // nulla e arrivato al modello a monte
            Assert.Equal(0, await Count(CampaignSql.CommittedOutputs, ("r", arranged.Run), ("s", "gap-stage-alias-1")));
        }

        [Fact]
        public async Task A_crash_at_the_stage_call_resumes_with_exactly_one_stage_request_and_no_hierarchy_or_retrieval_repeat()
        {
            var scenario = RetrievalGapCampaign.Single;
            var arranged = await ArrangeAsync(scenario);
            var crash = OfflineRouter(scenario, stage: _ => throw new InvalidOperationException("crash simulato"));
            var first = await Record.ExceptionAsync(() => ExecuteAsync(arranged.Claim, crash));
            Assert.True(first is not null || (await RunRow(arranged.Run)).Status != "Completed");
            Assert.Equal(1, await Count(CampaignSql.CommittedOutputs, ("r", arranged.Run), ("s", "retrieval-output-1")));
            Assert.Equal(0, await Count(CampaignSql.CommittedOutputs, ("r", arranged.Run), ("s", "gap-stage-alias-1")));
            var registry = await RegistryOf(arranged.Run);
            var requests = RetrievalRequests();

            await Execute("UPDATE \"ProcessingRuns\" SET \"Status\" = 'Running', \"ErrorCode\" = NULL, \"ErrorMessage\" = NULL, \"LeaseOwner\" = 'owner-2', \"FencingToken\" = 2, \"AttemptCount\" = 2, \"LeaseUntil\" = now() + interval '10 minutes' WHERE \"Id\" = @r", ("r", arranged.Run));
            var resume = OfflineRouter(scenario);
            var resumed = await ExecuteAsync(new AnalysisRunClaim(arranged.Run, "owner-2", 2, 2), resume);

            Assert.True(resumed.Status == ProcessingRunStatus.Completed, (await RunRow(arranged.Run)).Message);
            Assert.Empty(resume.Violations);
            Assert.Equal((1, 1), (resume.ChatRequests, resume.ForwardedStageCalls));   // solo lo stadio: gerarchia e recupero sono Committed
            Assert.Equal(requests, RetrievalRequests());
            Assert.Equal(registry.Hash(), (await RegistryOf(arranged.Run)).Hash());
            Assert.Equal(1, await Count(CampaignSql.CommittedOutputs, ("r", arranged.Run), ("s", "gap-stage-alias-1")));
        }
    }

    // ================================================================ prova REALE (opt-in DGX; MAI eseguita senza autorizzazione esplicita): scenari indipendenti, un solo test ciascuno

    /// <summary>Solo Single. Ignorato senza <c>BPROJECTS_TEST_DGX_ALLOW=1</c> e ambiente DGX completo. UNA richiesta generativa al DGX (lo stadio Gap); nessun retry.</summary>
    [Trait("Category", TestCategories.MafReal)]
    [Trait("Campaign", "IsolatedRetrievalGap")]
    public sealed class RealSingle : RetrievalGapIsolatedBase
    {
        internal override bool IsReal => true;

        [Fact]
        public async Task Isolated_retrieval_and_gap_stage_over_the_single_document_controlled_ledger()
        {
            var v = await RunScenarioAsync(RetrievalGapCampaign.Single);
            Assert.False(v.HasFail, v.ToString());
            Assert.Contains(v.Items, i => i.Status == VerdictStatus.NotVerified && i.Label.Contains("openConflict", StringComparison.Ordinal));   // il conflitto non e mai promosso a PASS
        }
    }

    /// <summary>Solo Multi, indipendente da Single. Stesse condizioni di <see cref="RealSingle"/>.</summary>
    [Trait("Category", TestCategories.MafReal)]
    [Trait("Campaign", "IsolatedRetrievalGap")]
    public sealed class RealMulti : RetrievalGapIsolatedBase
    {
        internal override bool IsReal => true;

        [Fact]
        public async Task Isolated_retrieval_and_gap_stage_over_the_multi_document_controlled_ledger()
        {
            var v = await RunScenarioAsync(RetrievalGapCampaign.Multi);
            Assert.False(v.HasFail, v.ToString());
            Assert.Contains(v.Items, i => i.Status == VerdictStatus.NotVerified && i.Label.Contains("openConflict", StringComparison.Ordinal));   // il conflitto non e mai promosso a PASS
        }
    }
}

/// <summary>Manifesto versionato del ledger isolato (<c>specs/003-retrieval-evidence-search/validation/isolated-retrieval-gap-manifest.json</c>).</summary>
internal static class RetrievalIsolatedManifest
{
    public static string FilePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "specs", "003-retrieval-evidence-search", "validation");
            if (Directory.Exists(candidate))
            {
                return Path.Combine(candidate, "isolated-retrieval-gap-manifest.json");
            }
        }

        throw new DirectoryNotFoundException("specs/003-retrieval-evidence-search/validation non raggiungibile da questo albero di build.");
    }

    public static string? Read() => File.Exists(FilePath()) ? File.ReadAllText(FilePath()) : null;
}
