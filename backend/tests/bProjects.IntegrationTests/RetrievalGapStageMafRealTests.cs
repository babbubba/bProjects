using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using bProjects.App.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T040 (DGX) — gate «SC-001/SC-003-retrieval»: percorso COMPLETO e reale Estrazione → Evidence Ledger → indice → recupero v7 (ibrido PostgreSQL + embedding reale) → riordino reale → UNA chiamata dello stadio
/// Gap sul pacchetto recuperato → coverage, su due corpus generati con lo stesso generatore e gli stessi gold di T082/T083 (<see cref="LargeDocumentCorpus"/>, gold NON modificati; parametri fissati in
/// <see cref="RetrievalGapCampaign"/>) più due documenti d'estensione fissi (risposta parziale; documento ESCLUSO dal PM). Le CINQUE attese per corpus (presente, parziale, assente, conflitto, documento
/// escluso) e la configurazione sono annotate e congelate con hash in <see cref="RetrievalGapCampaign"/> PRIMA di ogni inferenza: non si adattano alle risposte.
/// Criteri PASS (tutti; tasks.md T040): nessun gap_ledger_exceeds_input; 100% dei Gap con record finale valido; 0 falsi AnsweredInSnapshot; conflitti persi ≤ riferimento del laboratorio e nessuno per
/// esclusioni a monte; citazioni invalide ≤ 2 su 10; latenza del recupero per Gap riportata. Errore, SKIP, configurazione incompatibile o fail-fast sono FAIL/non-PASS, mai PASS.
/// Una sola campagna, DGX in modalità seriale (collezione RealParser): nessun rilancio automatico; se il primo corpus fallisce per un blocco STRUTTURALE/COMPLETAMENTO/COPERTURA il secondo non parte.
/// Servono BPROJECTS_TEST_POSTGRES, BPROJECTS_TEST_PARSER_ENDPOINT/_SECRET, BPROJECTS_TEST_DGX_* con opt-in BPROJECTS_TEST_DGX_ALLOW=1; senza, Skipped.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
[Trait("Campaign", "T040")]
[Collection(RealParser.CollectionName)]
public abstract class RetrievalGapStageMafRealTests : LargeDocumentRealFlow
{
    private readonly RetrievalCallRecorder _recorder = new();
    private readonly RetrievalTimingLog _timing = new();
    private readonly HashSet<Guid> _excluded = [];
    private double _indexSeconds;
    /// <summary>Etichetta del check d'indicizzazione: il tag [COMPLETAMENTO] la rende bloccante per il sequencer (verificato da un test).</summary>
    internal const string IndexCheckLabel = "[COMPLETAMENTO] [T040/PREPARAZIONE] indice costruito per ogni preparazione Ready del progetto, nessun fallimento (nessuna analisi LLM se fallisce)";

    private readonly IndexDiagnostics _indexDiagnostics;

    protected RetrievalGapStageMafRealTests() => _indexDiagnostics = new IndexDiagnostics(() => Scenario.Label);

    private readonly string _stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    private int _callsBefore;

    /// <summary>Un file per scenario E per esecuzione (marca temporale): un run successivo non sovrascrive mai le evidenze di uno precedente.</summary>
    private string DiagnosticsPath => Path.Combine(Path.GetTempPath(), "bprojects-t040-campaign", $"diagnostic-report-{_stamp}-{Scenario.Label}.json");

    internal abstract CampaignScenario Scenario { get; }

    protected override IReadOnlyList<string> PmQuestions => [.. Scenario.Expectations.Select(e => e.Question)];

    protected override string ExpectedDefinition => RetrievalGapCampaign.DefinitionVersion;   // v9 (Run 4); il Run 3 usava la v8, il Run 2 la v7

    protected override bool GapStageExpected => true;

    protected override int StageBatchesExpected => 2;   // recupero deterministico + stadio Gap

    protected override int? RequiredWindowTokens => RetrievalGapCampaign.WindowTokens;

    // Corpus a parametri fissati (riempitivo zero): nessun requisito di testo grezzo > 2xB (quello è di T082/T083); il recupero si attiva se il LEDGER non entra in B, e lo si verifica sul run.
    protected override double TargetRatio => 0;

    protected override double MinimumRatio => 0;

    // Riempitivo zero: la gerarchia può produrre un solo batch di riconciliazione (come T150); la prova del recupero è il batch `retrieval`, verificato a parte.
    protected override int MinimumHierarchyBatches => 1;

    protected override IEnumerable<string> ExtraSourceTexts => RetrievalGapCampaign.ExtensionDocuments.Where(d => !d.ExcludedByPm).Select(d => d.Text);

    protected override IReadOnlySet<Guid> ExcludedDocumentIds => _excluded;

    protected override string? RejectProfile(DocumentRuntimeProfile profile) => RetrievalGapCampaign.RejectProfile(profile);

    protected override string? RejectOptions(AiOptions options) => RetrievalGapCampaign.RejectOptions(options);

    protected override string ClassifyFailure(string errorCode) => RetrievalGapCampaign.Classify(errorCode);

    protected override void ConfigureCampaignServices(IServiceCollection services)
    {
        RetrievalGapCampaign.AddRetrieval(services, TestEnvironment.DgxEndpoint!, TestEnvironment.DgxApiKey!, _recorder, _timing);
    }

    /// <summary>
    /// Guardia di configurazione PRIMA di ogni inferenza (una volta per la campagna), nell'ordine: (1) salto esplicito senza opt-in o senza ambiente completo (mai PASS); (2) controlli PURI, senza rete —
    /// ambiente e manifesto congelato, identità della pipeline del prodotto (definizione, prompt con SHA-256, profilo e hash degli schemi, chiavi di freeze, definizione attesa dalla prova) e <see cref="AiOptions"/>
    /// RISOLTE dall'applicazione in prova (StructuredOutputMode, fase Analysis, fattore di estrazione); tutti i problemi sono riportati insieme; (3) SOLO ORA la prima richiesta al modello (<see cref="Require"/>).
    /// Un problema ai punti (1)-(2) è NO-GO: nessuna richiesta al DGX, nessun upload.
    /// </summary>
    internal async Task PreflightAsync()
    {
        TestEnvironment.RequireDgx();   // Skipped (mai PASS) senza opt-in
        RequireConfigured();            // Skipped senza ambiente completo; nessuna richiesta di rete
        var problems = new List<string>(RetrievalGapCampaign.ValidateEnvironment(Environment.GetEnvironmentVariable));
        var retrieval = App.Services.GetRequiredService<IOptions<RetrievalOptions>>().Value;
        problems.AddRange(RetrievalGapCampaign.RejectIdentity(RetrievalGapCampaign.AnalysisIdentity.FromProduct(retrieval), ExpectedDefinition));
        problems.AddRange(RetrievalGapCampaign.RejectLaunch(RetrievalGapCampaign.LaunchRecord.Read()));   // commit di lancio rilevato e codice di prodotto = baseline del manifesto
        if (RetrievalGapCampaign.RejectRetrievalOptions(retrieval) is { } retrievalRejection)
        {
            problems.Add("RetrievalOptions risolte: " + retrievalRejection);
        }

        if (RejectOptions(App.Services.GetRequiredService<IOptions<AiOptions>>().Value) is { } optionsRejection)
        {
            problems.Add("AiOptions risolte: " + optionsRejection);
        }

        Assert.True(problems.Count == 0, "T040: configurazione incompatibile, nessun upload né inferenza: " + string.Join(" | ", problems));
        await Require();
    }

    /// <summary>
    /// Fine scenario (anche con eccezione o blocco), PRIMA dell'eliminazione del database di test: righe di stato d'indice, run, job e batch derivati, rapporto delle chiamate al modello (anche Analysis:
    /// stadio, vista, hash, token, finish, conteggi) e chiamate HTTP del recupero, tutto su file. Solo osservazione: ogni segmento è protetto da solo e il file viene sempre scritto; mai alterare l'esito.
    /// </summary>
    protected override async Task OnFinalizingAsync()
    {
        Guarded(() => _indexDiagnostics.Launch = RetrievalGapCampaign.LaunchRecord.Read().ToJson());   // commit di lancio effettivo, distinto dal baseline del codice nel manifesto
        Guarded(() => _indexDiagnostics.Http = RetrievalIndexGate.HttpSummary(_recorder.Calls));
        Guarded(() => _indexDiagnostics.AnalysisCalls = AnalysisCallReport.Build(MafDiagnostics.Current().Dir, _callsBefore));   // solo le chiamate di QUESTO scenario (Single e Multi condividono la sessione)
        var database = new System.Text.Json.Nodes.JsonObject();
        try
        {
            // Connessione NUOVA con token proprio: il token del test è già annullato se il test è stato interrotto, ed è proprio il caso da documentare.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var connection = await OpenConnectionWithToken(timeout.Token);
            foreach (var (name, sql) in new[]
            {
                ("retrievalIndexStates", "SELECT \"ProjectId\", \"DocumentVersionId\", \"IndexConfigHash\", \"Status\"::text AS \"Status\", \"ErrorCode\", \"BlockCount\", \"EntryCount\", \"UpdatedAt\" FROM \"RetrievalIndexStates\" ORDER BY \"UpdatedAt\""),
                ("processingRuns", "SELECT \"Id\", \"Status\"::text AS \"Status\", \"ErrorCode\", left(\"ErrorMessage\", 4000) AS \"ErrorMessage\", \"AttemptCount\" FROM \"ProcessingRuns\" ORDER BY \"RequestedAt\""),
                ("documentJobsByStatus", "SELECT \"Kind\"::text AS \"Kind\", \"Status\"::text AS \"Status\", \"ErrorCode\", count(*) AS \"Count\" FROM \"DocumentJobs\" GROUP BY 1, 2, 3 ORDER BY 1, 2, 3"),
                ("failedDocumentJobs", "SELECT \"Kind\"::text AS \"Kind\", \"Status\"::text AS \"Status\", \"ErrorCode\", left(\"ErrorMessage\", 2000) AS \"ErrorMessage\" FROM \"DocumentJobs\" WHERE \"ErrorCode\" IS NOT NULL OR \"Status\"::text = 'Failed'"),
                ("derivedBatches", "SELECT \"BatchId\", \"Stage\", \"View\", \"Level\", \"Ordinal\", \"MeasuredTokens\", \"InputHash\", \"SchemaVersion\", \"PromptVersion\" FROM \"DerivedBatches\" ORDER BY \"CreatedAt\""),
            })
            {
                try
                {
                    database[name] = await Rows(connection, sql, timeout.Token);
                }
                catch (Exception e)
                {
                    // una query sbagliata non cancella le altre; solo codice SQL e messaggio del server (nessun parametro né dato)
                    database[name] = new System.Text.Json.Nodes.JsonObject { ["error"] = e.GetType().Name, ["sqlState"] = (e as Npgsql.PostgresException)?.SqlState, ["message"] = (e as Npgsql.PostgresException)?.MessageText };
                }
            }
        }
        catch (Exception e)
        {
            database["connectionError"] = e.GetType().Name;
        }

        _indexDiagnostics.Database = database;
        _indexDiagnostics.Persist(DiagnosticsPath);
    }

    private static void Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            TestContext.Current.SendDiagnosticMessage($"diag T040: segmento non riuscito ({e.GetType().Name})");
        }
    }

    private static async Task<System.Text.Json.Nodes.JsonArray> Rows(Npgsql.NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        var rows = new System.Text.Json.Nodes.JsonArray();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new System.Text.Json.Nodes.JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : System.Text.Json.Nodes.JsonValue.Create(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Un solo scenario, una sola volta: stato, registratore, tempi, database e applicazione sono propri di questa istanza.</summary>
    internal Task RunScenarioOnceAsync()
    {
        var directory = MafDiagnostics.Current().Dir;
        _callsBefore = Directory.Exists(directory) ? Directory.GetFiles(directory, "*-request.json").Length : 0;
        return RunScenarioAsync(Scenario.Label, _ => RetrievalGapCampaign.BuildCorpus(Scenario));
    }

    /// <summary>Ciclo di vita manuale di uno scenario non gestito da xUnit (il secondo): inizializza, esegue, elimina sempre.</summary>
    internal async Task RunOwnedAsync()
    {
        try
        {
            await InitializeAsync();   // dentro il try: se l avvio fallisce a meta, DisposeAsync libera comunque database e sandbox
            await RunScenarioOnceAsync();
        }
        finally
        {
            await DisposeAsync();
        }
    }

    /// <summary>
    /// Punto d'ingresso UNICO della campagna: Single poi Multi nell'ordine di <see cref="RetrievalGapCampaign.Sequence"/>, in un solo test. Multi parte solo se Single non ha incontrato un blocco
    /// (STRUTTURALE/COMPLETAMENTO/COPERTURA); nessun retry; il fallimento di Single resta tale e il suo stato è persistito.
    /// </summary>
    protected async Task RunCampaignAsync()
    {
        await PreflightAsync();
        var path = Path.Combine(Path.GetTempPath(), "bprojects-t040-campaign", "state.json");
        await CampaignSequencer.RunAsync(
            [
                new(RetrievalGapCampaign.Single.Label, RunScenarioOnceAsync),
                new(RetrievalGapCampaign.Multi.Label, () => new MultiScenario().RunOwnedAsync()),
            ],
            results => CampaignSequencer.WriteState(path, RetrievalGapCampaign.Id, results));
    }

    // ================================================================ passi fra la preparazione del corpus e Analizza

    protected override async Task<bool> PrepareBeforeAnalyzeAsync(HttpClient client, Guid project, Verdicts v)
    {
        const string l = "[T040/PREPARAZIONE]";
        var extension = new List<(ExtensionDocument Doc, Guid Document)>();
        foreach (var doc in RetrievalGapCampaign.ExtensionDocuments)
        {
            var response = await Upload(client, project, Encoding.UTF8.GetBytes(doc.Text), doc.FileName, "text/plain", displayName: doc.FileName);
            v.Check($"{l} {doc.FileName}: upload 201", response.StatusCode == HttpStatusCode.Created, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            if (response.StatusCode != HttpStatusCode.Created)
            {
                return false;
            }

            extension.Add((doc, (await Json(response)).GetProperty("documentId").GetGuid()));
        }

        foreach (var (doc, id) in extension)
        {
            var status = await Poll(async () =>
                {
                    var reply = await client.GetAsync($"/api/projects/{project}/documents/{id}", Ct);
                    var body = await Json(reply);
                    return body.TryGetProperty("versions", out var versions) ? versions[0].GetProperty("processingStatus").GetString()! : $"HTTP{(int)reply.StatusCode}";
                },
                s => s is "Ready" or "Failed" or "Incomplete" or "Rejected" || s.StartsWith("HTTP", StringComparison.Ordinal), DocumentsTimeout);
            v.Check($"[COMPLETAMENTO] {doc.FileName}: preparazione Ready (parser reale + estrazione reale)", status == "Ready", status);
            if (status != "Ready")
            {
                return false;
            }

            if (doc.ExcludedByPm)
            {
                _excluded.Add(id);
            }
        }

        // Indice di recupero: ricostruzione ESPLICITA come in produzione (mai all'avvio), con embedding reali, misurata. Tutti i documenti, esclusi compresi: l'esclusione è dello snapshot, non dell'indice.
        // Solo osservazione: nessun warm-up né richiesta di risveglio. Readiness prima dell'indicizzazione (sonda del prodotto), esito per preparazione con ErrorCode/messaggio, chiamate HTTP del recupero e righe
        // del database vengono scritti su file PRIMA dell'eliminazione del database, anche in caso di eccezione o blocco (vedi OnFinalizingAsync).
        _recorder.Phase = "index";
        var options = App.Services.GetRequiredService<IOptions<RetrievalOptions>>().Value;
        var readiness = App.Services.GetRequiredService<RetrievalModelReadiness>();
        _indexDiagnostics.Readiness.AddRange(await RetrievalIndexGate.CaptureAsync(readiness, options, "prima-dell-indicizzazione", probe: true, Ct));
        _indexDiagnostics.Persist(DiagnosticsPath);
        foreach (var record in _indexDiagnostics.Readiness)
        {
            v.Note($"{l} readiness {RetrievalIndexGate.Describe(record)}");
        }

        RetrievalRebuildSummary summary;
        try
        {
            summary = await RetrievalIndexGate.RebuildAsync(async () =>
            {
                using var scope = App.Services.CreateScope();
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<AppDbContext>();
                var builder = new RetrievalIndexBuilder(db, App.Services.GetRequiredService<EmbeddingClient>(), readiness, sp.GetRequiredService<IOptions<RetrievalOptions>>().Value);
                return await new RetrievalIndexMaintenance(db, builder).RebuildAsync(false, Ct);
            }, _indexDiagnostics, DiagnosticsPath);
        }
        finally
        {
            _recorder.Phase = "run";
            _indexDiagnostics.Http = RetrievalIndexGate.HttpSummary(_recorder.Calls);
            _indexDiagnostics.Readiness.AddRange(await RetrievalIndexGate.CaptureAsync(readiness, options, "dopo-l-indicizzazione-ultimo-stato-noto", probe: false, Ct));
            _indexDiagnostics.Persist(DiagnosticsPath);
        }

        _indexSeconds = _indexDiagnostics.Seconds ?? 0;
        var documents = await Count(CampaignSql.PreparationsReady, ("p", project));
        var ready = await Count(CampaignSql.IndexReady, ("p", project));
        v.Note($"{l} indice di recupero: preparazioni={summary.Preparations} Ready={summary.Ready} Failed={summary.Failed} in {_indexSeconds:F1} s (embedding reali); stati Ready del progetto={ready}; diagnostica in {DiagnosticsPath}");
        foreach (var result in summary.Results ?? [])
        {
            v.Note($"{l} indice, {RetrievalIndexGate.Describe(result)}{(result.ErrorMessage is null ? string.Empty : "; messaggio: " + result.ErrorMessage)}");
        }

        // BLOCCO: un'indicizzazione non completa ferma subito la campagna (tag [COMPLETAMENTO]): nessuna analisi LLM, nessun secondo corpus.
        var indexed = RetrievalIndexGate.IsComplete(summary, (int)documents) && ready == documents;
        v.Check(IndexCheckLabel, indexed,
            $"preparazioni Ready={documents} indici Ready={ready} falliti={summary.Failed} codici={string.Join(',', (summary.Results ?? []).Select(r => r.ErrorCode ?? "-"))}");
        if (!indexed)
        {
            return false;
        }

        // Esclusione del PM del documento d'estensione (If-Match = ETag corrente della selezione).
        var current = await client.GetAsync($"/api/projects/{project}/source-selection", Ct);
        var etag = current.Headers.ETag?.Tag;
        v.Check($"{l} selezione fonti leggibile con ETag", current.IsSuccessStatusCode && etag is not null, ((int)current.StatusCode).ToString(CultureInfo.InvariantCulture));
        if (etag is null)
        {
            return false;
        }

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{project}/source-selection")
        {
            Content = JsonContent.Create(new { items = _excluded.Select(d => new { documentId = d, included = false, mode = "LatestReady", versionId = (Guid?)null }).ToArray() }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", etag);
        var excluded = await client.SendAsync(put, Ct);
        v.Check($"{l} documento d'estensione escluso dal PM (PUT selezione 200)", excluded.StatusCode == HttpStatusCode.OK && _excluded.Count == 1, ((int)excluded.StatusCode).ToString(CultureInfo.InvariantCulture));
        return excluded.StatusCode == HttpStatusCode.OK && _excluded.Count == 1;
    }

    // ================================================================ verifiche T040

    private sealed record LoadedEvidence(Guid Id, EvidenceKind Kind, string Statement, IReadOnlyList<EvSource> Sources);

    private async Task<List<LoadedEvidence>> LoadEvidencesAsync(Guid project)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Evidences.AsNoTracking().Where(e => e.ProjectId == project).Select(e => new { e.Id, e.Kind, e.Statement, e.Content, e.OriginKind }).ToListAsync(Ct);
        var result = new List<LoadedEvidence>();
        foreach (var row in rows)
        {
            var sources = new List<EvSource>();
            if (row.OriginKind == EvidenceOriginKind.Document && row.Kind != EvidenceKind.Gap)
            {
                foreach (var s in DocumentEvidenceCodec.DecodeContent(row.Content).Sources.Where(s => s.Kind == EvidenceSourceKind.DocumentPassage))
                {
                    sources.Add(new EvSource(s.SourceId, s.DocumentId!.Value, s.PreparationId!.Value, s.BlockId!.Value, s.Start, s.End, s.Quote!, 1));
                }
            }

            result.Add(new LoadedEvidence(row.Id, row.Kind, row.Statement, sources));
        }

        return result;
    }

    protected override async Task VerifyExtraAsync(HttpClient client, Guid project, Guid run, Guid revision, Corpus corpus, Content content, Verdicts v)
    {
        const string l = "[T040]";
        var args = ("r", (object)run);
        var expectations = Scenario.Expectations;

        // (1) instradamento: v7, il recupero SOSTITUISCE il fail-fast; esattamente un batch recupero e un batch stadio, una sola chiamata dello stadio, nessuna chiamata LLM per il recupero.
        var runError = await Scalar<string>(CampaignSql.RunError, args);
        var runMessage = await Scalar<string>(CampaignSql.RunMessage, args);
        v.Check($"{l} (1) nessun gap_ledger_exceeds_input", runError != "gap_ledger_exceeds_input" && !runMessage.Contains("gap_ledger_exceeds_input", StringComparison.Ordinal), $"{runError} {Short(runMessage, 120)}");
        var retrievalBatches = await Count(CampaignSql.StageBatches, args, ("s", "retrieval"));
        var gapBatches = await Count(CampaignSql.StageBatches, args, ("s", "gap"));
        var retrievalOutputs = await Count(CampaignSql.CommittedOutputs, args, ("s", "retrieval-output-1"));
        var stageOutputs = await Count(CampaignSql.CommittedOutputs, args, ("s", "gap-stage-alias-1"));
        v.Check($"{l} (1) esattamente un batch di recupero con un WorkOutput retrieval-output-1 e un batch dello stadio Gap con un WorkOutput gap-stage-alias-1",
            retrievalBatches == 1 && gapBatches == 1 && retrievalOutputs == 1 && stageOutputs == 1, $"recupero={retrievalBatches}/{retrievalOutputs} stadio={gapBatches}/{stageOutputs}");
        var retrievalOrdinal = await Scalar<int>(CampaignSql.StageOrdinal, args, ("s", "retrieval"));
        var gapOrdinal = await Scalar<int>(CampaignSql.StageOrdinal, args, ("s", "gap"));
        v.Check($"{l} (1) il recupero precede immediatamente lo stadio Gap, dopo la gerarchia", gapOrdinal == retrievalOrdinal + 1, $"ordinali recupero={retrievalOrdinal} stadio={gapOrdinal}");
        var batches = await Count(RunSignalSql.Batches, args);
        var calls = await Count(CampaignSql.InferenceCalls, args);
        v.Check($"{l} (1) una chiamata per batch con modello e nessuna per il recupero: UNA sola chiamata dello stadio, nessun retry", calls == batches - 1, $"chiamate={calls} batch totali={batches} (gerarchia + recupero + stadio)");

        // (2) registro dei candidati congelato nel WorkOutput: contratto, politica, modelli, documenti esclusi e pacchetto realmente consegnato.
        var registry = RetrievalOutput.Read(await Scalar<string>(CampaignSql.RegistryPayload, args));
        v.Note($"{l} registro: esito={registry.Outcome} basis={registry.SearchBasis} retrievalComplete={registry.RetrievalComplete} degraded={registry.Degraded} politica={registry.PolicyVersion} gap={registry.Gaps.Count} pacchetto={registry.PackageEvidence.Count} "
            + $"candidati per gap=[{string.Join(",", registry.Gaps.Select(g => g.Candidates.Count))}] esclusi={registry.Excluded.Count} hashRegistro={Short(registry.Hash(), 16)} indexConfigHash={Short(registry.IndexConfigHash, 16)}");
        v.Check($"{l} (2) registro {CandidateRegistry.Contract}: sorgente retrieval, esito Package, non degradato, nessuna causa di servizio",
            registry.Contract == CandidateRegistry.Contract && registry.SearchBasis == CandidateRegistry.SearchBasis && registry.Outcome == CandidateRegistry.OutcomePackage && !registry.Degraded && registry.Causes.Count == 0,
            $"{registry.Contract} {registry.SearchBasis} {registry.Outcome} degraded={registry.Degraded} cause=[{string.Join(",", registry.Causes)}]");
        v.Check($"{l} (2) politica selezionata esplicitamente ({RetrievalGapCampaign.PolicyVersion}) con il suo hash, e modelli/artefatti osservati dalla readiness uguali a quelli fissati",
            registry.PolicyVersion == RetrievalGapCampaign.PolicyVersion && registry.PolicyHash == RetrievalPolicy.V1.Hash()
            && registry.Readiness?.Embedding is { } emb && emb.Model == RetrievalGapCampaign.EmbeddingModelId && emb.Artifact == RetrievalGapCampaign.EmbeddingArtifact && emb.Dimension == RetrievalGapCampaign.EmbeddingDimension
            && registry.Readiness?.Reranker is { } rer && rer.Model == RetrievalGapCampaign.RerankerModelId && rer.Artifact == RetrievalGapCampaign.RerankerArtifact,
            $"emb={registry.Readiness?.Embedding} rer={registry.Readiness?.Reranker}");
        v.Check($"{l} (2) un'entrata del registro per ciascun Gap annotato ({expectations.Count}) e un pacchetto non vuoto", registry.Gaps.Count == expectations.Count && registry.PackageEvidence.Count > 0, $"gap={registry.Gaps.Count} pacchetto={registry.PackageEvidence.Count}");
        var api = await client.GetAsync($"/api/projects/{project}/analyses/{run}/retrieval-registry", Ct);
        var apiBody = api.IsSuccessStatusCode ? await Json(api) : default;
        v.Check($"{l} (2) il registro è consultabile dall'API (GET retrieval-registry 200, stessa sorgente, esito e politica, un'entrata per Gap)", api.IsSuccessStatusCode
            && apiBody.GetProperty("searchBasis").GetString() == registry.SearchBasis && apiBody.GetProperty("outcome").GetString() == registry.Outcome
            && apiBody.GetProperty("policyVersion").GetString() == registry.PolicyVersion && apiBody.GetProperty("gaps").GetArrayLength() == registry.Gaps.Count, ((int)api.StatusCode).ToString(CultureInfo.InvariantCulture));

        // (2) documento escluso: mai tra candidati, pacchetto, sempre-incluse né esclusi del registro, anche se è nell'indice; e ha davvero prodotto evidenze (altrimenti la prova sarebbe vuota).
        var evidences = await LoadEvidencesAsync(project);
        var excludedEvidence = evidences.Where(e => e.Sources.Any(s => _excluded.Contains(s.DocumentId))).Select(e => e.Id).ToHashSet();
        v.Check($"{l} (2) il documento escluso dal PM ha prodotto evidenze nel progetto (la prova di esclusione non è vuota)", excludedEvidence.Count > 0, $"{excludedEvidence.Count}");
        var candidateEvidence = registry.Gaps.SelectMany(g => g.Candidates).SelectMany(c => c.EvidenceIds).ToHashSet();
        v.Check($"{l} (2) il documento escluso non compare fra candidati, pacchetto, sempre-incluse ed esclusi del registro",
            !registry.Gaps.SelectMany(g => g.Candidates).Any(c => _excluded.Contains(c.DocumentId)) && !candidateEvidence.Overlaps(excludedEvidence) && !registry.PackageEvidence.Any(excludedEvidence.Contains)
            && !registry.AlwaysIncluded.Any(a => excludedEvidence.Contains(a.EvidenceId)) && !registry.Excluded.Any(x => excludedEvidence.Contains(x.EvidenceId)));
        var indexedExcluded = await Count(CampaignSql.IndexedExcluded, ("p", project), ("d", _excluded.ToArray()));
        v.Check($"{l} (2) il documento escluso è nell'indice Ready: l'esclusione è dello snapshot, non un'assenza dall'indice", indexedExcluded == _excluded.Count, $"{indexedExcluded}");

        // (3) pacchetto realmente consegnato allo stadio: stesso numero di candidati, stessa sorgente, e rispetto del budget di input misurato.
        var stage = JsonDocument.Parse(await Scalar<string>(CampaignSql.StagePayload, args)).RootElement;
        var info = stage.GetProperty("stage");
        v.Check($"{l} (3) lo stadio ha ricevuto SOLO il pacchetto recuperato: candidati = pacchetto del registro, un Gap per domanda, sorgente retrieval, retrievalComplete come nel registro",
            info.GetProperty("candidates").GetInt32() == registry.PackageEvidence.Count && info.GetProperty("gaps").GetInt32() == expectations.Count
            && info.GetProperty("searchBasis").GetString() == "retrieval" && info.GetProperty("retrievalComplete").GetBoolean() == registry.RetrievalComplete,
            $"candidati={info.GetProperty("candidates").GetInt32()} pacchetto={registry.PackageEvidence.Count}");
        var selected = await Count(RunSignalSql.SelectedEvidences, args);
        v.Note($"{l} ledger del run={selected} evidenze selezionate (Gap compresi); pacchetto consegnato={registry.PackageEvidence.Count}");
        v.Check($"{l} (3) il ledger NON entrava nello stadio: il pacchetto è un sottoinsieme proprio del ledger non-Gap (il recupero ha lavorato davvero)", registry.PackageEvidence.Count < selected - expectations.Count, $"pacchetto={registry.PackageEvidence.Count} ledger non-Gap={selected - expectations.Count}");

        // (4) Gap, coverage-3, esiti, supporti, citazioni, conflitti, assenze.
        var gapPage = await Json(await client.GetAsync($"/api/projects/{project}/evidence?kind=Gap&viewVersion=1.1.0&limit=100", Ct));
        var gaps = gapPage.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("evidence")).Select(e => (Id: e.GetProperty("id").GetGuid(), Statement: e.GetProperty("statement").GetString() ?? "")).ToList();
        var coverage = await client.PostAsJsonAsync($"/api/projects/{project}/analyses/{run}/coverage-reports", new { revisionId = revision }, Ct);
        var report = await Json(coverage);
        v.Check($"{l} (4) coverage-3 creato", (coverage.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created) && report.TryGetProperty("checkerVersion", out var cv) && cv.GetString() == "coverage-3", $"{(int)coverage.StatusCode} {Short(report.GetRawText(), 300)}");
        if (!report.TryGetProperty("gapAssessments", out var assessments) || assessments.ValueKind != JsonValueKind.Array)
        {
            v.Check($"{l} (4) gapAssessments presente", false);
            return;
        }

        var records = assessments.EnumerateArray().ToList();
        var summary = report.GetProperty("summary");
        v.Note($"{l} summary: expected={summary.GetProperty("expectedGaps").GetInt32()} assessed={summary.GetProperty("assessedGaps").GetInt32()} answered={summary.GetProperty("answeredGaps").GetInt32()} open={summary.GetProperty("openGaps").GetInt32()} unassessed={summary.GetProperty("unassessedGaps").GetInt32()}");
        v.Check($"{l} (4) 100% dei Gap con un record finale valido: un solo record per Gap, nessun Gap non valutato, tanti Gap quante le domande", summary.GetProperty("unassessedGaps").GetInt32() == 0 && gaps.Count == expectations.Count
            && records.Count == gaps.Count && gaps.All(g => records.Count(r => r.GetProperty("gapEvidenceId").GetGuid() == g.Id) == 1), $"gap={gaps.Count} record={records.Count}");

        var packageIds = registry.PackageEvidence.ToHashSet();
        var byId = evidences.ToDictionary(e => e.Id);
        var falseAnswered = new List<string>();
        var invalidCitations = 0;
        var totalCitations = 0;
        foreach (var expected in expectations)
        {
            var gap = gaps.Where(g => Fold(g.Statement).Contains(Fold(expected.QuestionToken), StringComparison.Ordinal)).ToList();
            v.Check($"{l} {expected.Id} ({expected.Kind}): la domanda è stata estratta come UNA evidenza Gap", gap.Count == 1, $"{gap.Count}");
            if (gap.Count != 1)
            {
                continue;
            }

            var record = records.FirstOrDefault(r => r.GetProperty("gapEvidenceId").GetGuid() == gap[0].Id);
            if (record.ValueKind != JsonValueKind.Object)
            {
                v.Check($"{l} {expected.Id}: record presente", false);
                continue;
            }

            var outcome = record.GetProperty("outcome").GetString();
            var reason = record.GetProperty("reason").GetString();
            var detail = Short(record.GetRawText(), 420);
            if (outcome == "AnsweredInSnapshot" && expected.Outcome != "AnsweredInSnapshot")
            {
                falseAnswered.Add(expected.Id);
            }

            v.Check($"{l} (4) {expected.Id} ({expected.Kind}): esito {expected.Outcome} con motivo in [{string.Join("/", expected.Reasons)}]", outcome == expected.Outcome && expected.Reasons.Contains(reason), detail);
            v.Check($"{l} {expected.Id}: motivazione e snapshot hash presenti", (record.GetProperty("rationale").GetString() ?? "").Trim().Length > 0 && (record.GetProperty("sourceSnapshotHash").GetString() ?? "").Length > 0);

            var support = record.GetProperty("supportingEvidenceIds").EnumerateArray().Select(x => x.GetGuid()).ToList();
            var openItem = record.GetProperty("openItemId").ValueKind != JsonValueKind.Null;
            var openConflict = record.GetProperty("openConflictId").ValueKind != JsonValueKind.Null;

            // supporti: solo passaggi del PACCHETTO consegnato, documentali, mai Gap né documento escluso; citazioni esatte nel blocco.
            v.Check($"{l} (4) {expected.Id}: ogni supporto è un'evidenza documentale del pacchetto recuperato (mai un Gap, mai fuori pacchetto, mai del documento escluso)",
                support.All(id => packageIds.Contains(id) && byId.TryGetValue(id, out var ev) && ev.Kind != EvidenceKind.Gap && ev.Sources.Count > 0 && !excludedEvidence.Contains(id)), $"supporti={support.Count}");
            var supportSources = support.Where(byId.ContainsKey).SelectMany(id => byId[id].Sources).ToList();
            var invalidHere = 0;
            foreach (var source in supportSources)
            {
                totalCitations++;
                if (await PassageText(client, project, source) != source.Quote)
                {
                    invalidHere++;
                    invalidCitations++;
                }
            }

            v.Note($"{l} {expected.Id}: {supportSources.Count} citazioni di supporto, {invalidHere} non esatte nel blocco");
            foreach (var quote in expected.SupportQuotes)
            {
                v.Check($"{l} (4) {expected.Id}: tra i supporti c'è il riscontro «{quote[0]}»", supportSources.Any(s => quote.Any(q => Fold(s.Quote).Contains(Fold(q), StringComparison.Ordinal))));
            }

            foreach (var forbidden in expected.ForbiddenQuotes)
            {
                v.Check($"{l} (4) {expected.Id}: nessun supporto contiene «{forbidden}» (riscontro non ammesso per questa attesa)", !supportSources.Any(s => Fold(s.Quote).Contains(Fold(forbidden), StringComparison.Ordinal)));
            }

            if (expected.Outcome == "AnsweredInSnapshot")
            {
                v.Check($"{l} (4) {expected.Id}: nessun riferimento Open e almeno un supporto", !openItem && !openConflict && support.Count > 0);
            }
            else
            {
                v.Check($"{l} (4) {expected.Id}: esattamente uno fra openItem e openConflict valorizzato", openItem ^ openConflict, $"openItem={openItem} openConflict={openConflict}");
            }

            // assenza: con sorgente retrieval NON è mai provata (FR-018/FR-019), qualunque sia l'esito
            v.Check($"{l} (4) {expected.Id}: sorgente retrieval dichiarata e assenza MAI provata (absenceProven != true)",
                record.TryGetProperty("searchBasis", out var basis) && basis.GetString() == "retrieval" && !(record.TryGetProperty("absenceProven", out var proven) && proven.ValueKind == JsonValueKind.True), detail);

            if (expected.ExpectOpenConflict)
            {
                v.Check($"{l} (5) {expected.Id}: conflitto aperto — openConflict valorizzato, conflitto Open nella revisione, nessuna risoluzione automatica, entrambi i lati fra i supporti",
                    openConflict && content.Conflicts.Any(c => c.Status == "Open") && content.Conflicts.All(c => !c.HasResolution) && expected.SupportQuotes.All(q => supportSources.Any(s => q.Any(x => Fold(s.Quote).Contains(Fold(x), StringComparison.Ordinal)))), detail);
                foreach (var side in expected.SupportQuotes)
                {
                    var sideEvidence = evidences.Where(e => e.Sources.Any(s => side.Any(x => Fold(s.Quote).Contains(Fold(x), StringComparison.Ordinal))) && e.Kind != EvidenceKind.Gap).Select(e => e.Id).ToList();
                    v.Check($"{l} (5) {expected.Id}: il lato «{side[0]}» non è perso per esclusioni a monte — almeno una sua evidenza è nel pacchetto recuperato e nessuna è del documento escluso",
                        sideEvidence.Any(packageIds.Contains) && !sideEvidence.All(excludedEvidence.Contains), $"evidenze del lato={sideEvidence.Count}");
                }
            }
        }

        // criteri aggregati
        v.Check($"{l} (4) 0 falsi AnsweredInSnapshot ({falseAnswered.Count}: {string.Join(",", falseAnswered)}) e nessun record AnsweredInSnapshot fuori dalle attese", falseAnswered.Count == 0
            && records.Count(r => r.GetProperty("outcome").GetString() == "AnsweredInSnapshot") == expectations.Count(e => e.Outcome == "AnsweredInSnapshot"));
        v.Check($"{l} (4) citazioni invalide ≤ 2 su 10: {invalidCitations} su {totalCitations} citazioni di supporto (proporzione {RetrievalGapCampaign.MaxInvalidCitationsPer10}/10)",
            totalCitations > 0 && RetrievalGapCampaign.WithinPer10(invalidCitations, totalCitations, RetrievalGapCampaign.MaxInvalidCitationsPer10));
        var lost = corpus.Conflicts.Count(c =>
        {
            var open = content.Conflicts.Where(x => x.Status == "Open" && !x.HasResolution).ToList();
            return !c.Sides.All(side => open.Any(x => x.Alternatives.Any(a => side.All(a.QuotesHave))));
        });
        var allowedLost = RetrievalGapCampaign.AllowedLostConflicts(corpus.Conflicts.Count);
        v.Check($"{l} (5) conflitti persi ≤ riferimento del laboratorio (SC-003: {RetrievalGapCampaign.LabConflictLostMax} su {RetrievalGapCampaign.LabConflictReferenceCases} casi; sotto {RetrievalGapCampaign.LabConflictReferenceCases} casi tolleranza zero): "
            + $"{lost} persi su {corpus.Conflicts.Count} conflitti gold, ammessi {allowedLost}", lost <= allowedLost);

        // nessun conflitto perso per esclusione a monte: ogni lato di ogni conflitto gold ha almeno un'evidenza nel pacchetto recuperato che non sia del solo documento escluso.
        var upstreamLost = corpus.Conflicts.Count(c => c.Sides.Any(side =>
        {
            var sideEvidence = evidences.Where(e => e.Kind != EvidenceKind.Gap && side.All(q => e.Sources.Any(s => Fold(s.Quote).Contains(Fold(q), StringComparison.Ordinal)))).Select(e => e.Id).ToList();
            return !sideEvidence.Any(id => packageIds.Contains(id) && !excludedEvidence.Contains(id));
        }));
        v.Check($"{l} (5) conflitti persi per esclusione a monte del recupero = {RetrievalGapCampaign.MaxUpstreamLostConflicts} (mai scalato): {upstreamLost} su {corpus.Conflicts.Count} conflitti gold", upstreamLost <= RetrievalGapCampaign.MaxUpstreamLostConflicts);

        // (6) latenza del recupero PER GAP (fasi proprie del Gap con componenti HTTP) e fasi CONDIVISE riportate a parte, mai divise per Gap.
        var latency = LatencyReport.Build(_timing.Entries, _recorder.Calls, [.. gaps.Select(g => g.Id)]);
        var statements = gaps.ToDictionary(g => g.Id, g => g.Statement);
        foreach (var line in latency.Describe(id => Short(statements.GetValueOrDefault(id, id.ToString()), 50)))
        {
            v.Note($"{l} (6) {line}");
        }

        foreach (var g in latency.PerGap)
        {
            var candidateCount = registry.Gaps.Where(x => x.GapEvidenceId == g.GapEvidenceId).Sum(x => x.Candidates.Count);
            v.Check($"{l} (6) latenza del recupero misurata per «{Short(statements[g.GapEvidenceId], 40)}»: fasi ricerca e riordino complete, una chiamata di embedding e, se il Gap ha candidati, almeno una di riordino (un Gap senza candidati non invia nulla al reranker)",
                g.IsMeasuredCompletely(candidateCount), $"end-to-end {g.EndToEndMs:F0} ms, embedding {g.EmbeddingCalls}, riordino {g.RerankCalls}, candidati {candidateCount}");
        }

        v.Check($"{l} (6) tempo condiviso e totale riportati separatamente: il totale della pipeline copre la somma delle fasi per Gap", latency.TotalMs > 0 && latency.TotalMs + 1 >= latency.PerGap.Sum(g => g.EndToEndMs), $"totale {latency.TotalMs:F0} ms");

        var rerankCalls = _recorder.Calls.Count(c => c.Phase == "run" && c.Kind == "rerank");
        var failedCalls = _recorder.Calls.Count(c => c.Phase is "run" or "index" && c.Kind != "other" && c.Status is < 200 or >= 300);
        v.Note($"{l} (6) chiamate al recupero: embedding indice={_recorder.Calls.Count(c => c.Phase == "index" && c.Kind == "embedding")} (in {_indexSeconds:F1} s), run: embedding={_recorder.Calls.Count(c => c.Phase == "run" && c.Kind == "embedding")} riordino={rerankCalls} sonde={_recorder.Calls.Count(c => c.Kind == "other")} non-2xx={failedCalls}");
        v.Check($"{l} (6) nessuna chiamata di embedding/riordino con esito non 2xx (nessun fallback silenzioso)", failedCalls == 0, $"{failedCalls}");
        var committed = await Scalar<DateTime>(CampaignSql.RetrievalCommitted, args);
        var previous = await Scalar<DateTime>(CampaignSql.HierarchyCommitted, args);
        v.Note($"{l} (6) recupero (dal commit dell'ultimo batch di gerarchia al commit del registro, misurazione dei costi inclusa): {(committed - previous).TotalSeconds:F1} s");

        // token e durata dello stadio (ultima chiamata del run) e del budget di input misurato.
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand(
            CampaignSql.LastCall, connection);
        command.Parameters.AddWithValue("r", run);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        if (await reader.ReadAsync(Ct))
        {
            var prompt = reader.IsDBNull(0) ? -1 : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            v.Note($"{l} (7) chiamata dello stadio: prompt={prompt} token (budget di input B={RetrievalGapCampaign.BudgetTokens}), output={reader.GetValue(1)} token, durata={Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture) / 1000}s, finish={reader.GetValue(3)}");
            v.Check($"{l} (7) la chiamata dello stadio non è troncata e il prompt entra nel budget di input fissato", !string.Equals(reader.GetValue(3)?.ToString(), "length", StringComparison.OrdinalIgnoreCase) && prompt is >= 0 && prompt <= RetrievalGapCampaign.BudgetTokens, $"prompt={prompt}");
        }
        else
        {
            v.Check($"{l} (7) chiamata dello stadio registrata", false);
        }
    }

    // ================================================================ scenari

    /// <summary>L'UNICO test della campagna: xUnit lo istanzia con lo scenario Single; Multi è eseguito da <see cref="CampaignSequencer"/> subito dopo, con istanza e risorse proprie.</summary>
    public sealed class Campaign : RetrievalGapStageMafRealTests
    {
        internal override CampaignScenario Scenario => RetrievalGapCampaign.Single;

        [Fact]
        public async Task T040_retrieval_feeds_the_gap_stage_over_the_single_then_the_multi_document_ledger() => await RunCampaignAsync();
    }

    /// <summary>Scenario Multi: nessun test proprio (non scoperto da xUnit); ciclo di vita manuale dal punto d'ingresso unico.</summary>
    public sealed class MultiScenario : RetrievalGapStageMafRealTests
    {
        internal override CampaignScenario Scenario => RetrievalGapCampaign.Multi;
    }
}
