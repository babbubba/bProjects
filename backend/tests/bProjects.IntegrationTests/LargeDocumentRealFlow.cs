using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// Base comune di T082/T083 (DGX): stack reale (API di produzione, worker di parsing, parser Docling reale, PostgreSQL, MAF 1.26.0, modello interno reale) e verifiche
/// distinte per natura: STRUTTURALE (budget, v4, gerarchia), COPERTURA, SEMANTICA, PROVENIENZA, COMPLETAMENTO. Configurazione AI identica a
/// <see cref="DocumentSmallFlowMafRealTests"/> (finestra, output e ragionamento dell'analisi); il budget di input Analysis si calcola A RUNTIME dal profilo effettivo del runtime
/// (<see cref="DocumentRuntimeProfile.InputBudgetTokens"/> = finestra misurata - output - margine) e la dimensione dei documenti si adatta al budget, mai il contrario: nessuna riduzione
/// del documento, nessun aumento della finestra. Un errore gestito, un run non Completed o una verifica non soddisfatta e un FAIL, mai un PASS. Il DGX e uno slot unico: un solo test per volta.
/// </summary>
public abstract class LargeDocumentRealFlow : ArchiveTestBase
{
    private static readonly bool Configured =
        TestEnvironment.DgxEndpoint is not null && TestEnvironment.DgxApiKey is not null && TestEnvironment.DgxModel is not null
        && DocumentTestEnvironment.ParserEndpoint is not null && Environment.GetEnvironmentVariable(RealParser.SecretVariable) is { Length: > 0 };

    /// <summary>Margine voluto sopra il minimo di 2x, perche il conteggio dei token dopo il parsing puo differire di poco da quello prima dell'invio.</summary>
    private const double TargetRatio = 2.5;
    private const double MinimumRatio = 2.0;

    protected static readonly TimeSpan DocumentsTimeout = TimeSpan.FromMinutes(120);
    protected static readonly TimeSpan ChatTimeout = TimeSpan.FromMinutes(8);
    protected static readonly TimeSpan RunTimeout = TimeSpan.FromHours(4);

    /// <summary>Diagnostica locale delle chiamate di estrazione (solo harness): cattura la risposta grezza del modello prima di BatchSender e la correla ai batch a fine run.</summary>
    private ExtractionDiagnosticsRecorder? _diag;

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 2_000_000;
        options.ProjectQuotaBytes = 20_000_000;
    }

    private static AiOptions RealOptions()
    {
        var options = TestEnvironment.RequireProductionLikeOptions();
        options.ContextWindowTokens = EnvInt("BPROJECTS_TEST_CTX_WINDOW") ?? 32768;
        options.Phases.Analysis.MaxOutputTokens = EnvInt("BPROJECTS_TEST_ANALYSIS_MAX_OUTPUT") ?? 16384;
        options.Phases.Analysis.EnableThinking = EnvInt("BPROJECTS_TEST_ANALYSIS_THINKING") == 1;
        if (EnvInt("BPROJECTS_TEST_SAFETY_MARGIN") is { } margin)
        {
            options.SafetyMarginTokens = margin;
        }

        MafDiagnostics.RecordConfig(options);
        return options;
    }

    private static int? EnvInt(string name) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : null;

    protected override void ConfigureServices(IServiceCollection services)
    {
        if (!Configured)
        {
            return;
        }

        var ai = RealOptions();
        var parser = RealParser.Options();
        var diag = _diag = new ExtractionDiagnosticsRecorder(secrets: ExtractionDiagnosticsRecorder.EnvironmentSecrets(ai.ApiKey));
        services.PostConfigure<AiOptions>(o => CopyOptions(ai, o));
        services.Configure<ExecutionOptions>(o => o.AnalysisRunDeadlineSeconds = TestEnvironment.ProductionAnalysisRunDeadlineSeconds);
        services.Configure<ParserOptions>(o =>
        {
            o.BaseUrl = parser.BaseUrl;
            o.Secret = parser.Secret;
        });
        services.AddSingleton(sp => new InternalModelClient(sp.GetRequiredService<IOptions<AiOptions>>().Value, MafDiagnostics.Wrap()));
        services.AddAiWorkflows();
        services.AddSingleton<IEvidenceExtractionExecutor>(sp =>
        {
            var model = sp.GetRequiredService<InternalModelClient>();
            return new EvidenceExtractor(
                sp.GetRequiredService<IServiceScopeFactory>(),
                AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model, sp.GetRequiredService<ModelRuntimeMonitor>()),
                model.CreateChatOptions(EvidenceExtractionSchema.Base, EvidenceExtractionSchema.Name, temperature: 0, phase: AiPhase.Extraction),
                sp.GetRequiredService<EvidenceEvents>());
        });
        services.AddSingleton<IDocumentUnitExecutor>(sp =>
        {
            var model = sp.GetRequiredService<InternalModelClient>();
            var documents = sp.GetRequiredService<IOptions<DocumentOptions>>();
            return new DocumentEvidenceAgent(
                sp.GetRequiredService<IServiceScopeFactory>(),
                diag.CreateFactory(sp.GetRequiredService<IOptions<AiOptions>>().Value, model, sp.GetRequiredService<ModelRuntimeMonitor>(), documents.Value.Calibration.ToCalibration()),
                model, documents);
        });
    }

    private static void CopyOptions(AiOptions from, AiOptions to)
    {
        foreach (var property in typeof(AiOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.Name != nameof(AiOptions.Phases)))
        {
            property.SetValue(to, property.GetValue(from));
        }

        foreach (var phase in Enum.GetValues<AiPhase>())
        {
            var source = from.Phases.Of(phase);
            var target = to.Phases.Of(phase);
            target.ModelId = source.ModelId;
            target.MaxOutputTokens = source.MaxOutputTokens;
            target.EnableThinking = source.EnableThinking;
            target.RequestTimeoutSeconds = source.RequestTimeoutSeconds;
        }
    }

    /// <summary>Skip esplicito senza opt-in/ambiente; poi richiesta minima che carica il modello (router mode, come T067), senza fallback.</summary>
    protected static async Task Require()
    {
        Assert.SkipUnless(Configured, (TestEnvironment.DgxUnavailableReason() ?? string.Empty) + " Servono BPROJECTS_TEST_DGX_* (con opt-in), BPROJECTS_TEST_PARSER_ENDPOINT/_SECRET e BPROJECTS_TEST_POSTGRES: livello DGX non eseguibile.");
        var options = RealOptions();
        using var model = new InternalModelClient(options, MafDiagnostics.Wrap());
        var warm = model.CreateChatOptions(temperature: 0, phase: AiPhase.Extraction);
        warm.MaxOutputTokens = 16;
        await model.ChatClient.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Rispondi solo: ok")], warm, Ct);
    }

    // ================================================================ budget e dimensione

    /// <summary>Profilo effettivo dell'analisi dal runtime e conteggio con il tokenizer VERIFICATO del modello.</summary>
    private async Task<(DocumentRuntimeProfile Profile, Func<string, Task<int>> Count)> BudgetAsync()
    {
        var sp = App.Services;
        var factory = AgentFactory.Create(
            sp.GetRequiredService<IOptions<AiOptions>>().Value, sp.GetRequiredService<InternalModelClient>(), sp.GetRequiredService<ModelRuntimeMonitor>(),
            sp.GetRequiredService<IOptions<DocumentOptions>>().Value.Calibration.ToCalibration());
        var profile = await factory.Preflight.ResolveProfileAsync(AiPhase.Analysis, Ct);
        return (profile, async text => await factory.Preflight.CountVerifiedTextAsync(profile, text, Ct)
            ?? throw new InvalidOperationException("tokenizer del modello non verificato: impossibile misurare il testo (nessuna stima)."));
    }

    /// <summary>
    /// Genera il corpus con la dimensione che porta il testo ad almeno <see cref="TargetRatio"/>x il budget Analysis misurato (tokenizer reale): si ingrandisce il riempitivo, mai si riduce.
    /// </summary>
    private async Task<(Corpus Corpus, DocumentRuntimeProfile Profile, long Tokens)> BuildSizedAsync(Func<int, Corpus> build)
    {
        var (profile, count) = await BudgetAsync();
        var budget = profile.InputBudgetTokens;
        var filler = (int)(budget * TargetRatio * 3.0);
        for (var attempt = 0; ; attempt++)
        {
            var corpus = build(filler);
            long tokens = 0;
            foreach (var doc in corpus.Documents)
            {
                tokens += await count(doc.PlainText);
            }

            if (tokens >= budget * TargetRatio || attempt == 5)
            {
                return (corpus, profile, tokens);
            }

            filler = (int)(filler * (budget * TargetRatio / tokens) * 1.05) + 1;
        }
    }

    // ================================================================ scenario

    protected async Task RunScenarioAsync(string label, Func<int, Corpus> build)
    {
        try
        {
            await RunScenarioCoreAsync(label, build);
        }
        finally
        {
            // Anche a run fallito e PRIMA che il database di test venga eliminato (DisposeAsync del fixture): correlazione chiamata -> batch e file diagnostici.
            await FinalizeDiagnosticsAsync();
        }
    }

    private async Task FinalizeDiagnosticsAsync()
    {
        if (_diag is null)
        {
            return;
        }

        try
        {
            await using var connection = await OpenConnection();
            Func<string, CancellationToken, Task<int?>>? measure = null;
            try
            {
                var sp = App.Services;
                var factory = AgentFactory.Create(
                    sp.GetRequiredService<IOptions<AiOptions>>().Value, sp.GetRequiredService<InternalModelClient>(), sp.GetRequiredService<ModelRuntimeMonitor>(),
                    sp.GetRequiredService<IOptions<DocumentOptions>>().Value.Calibration.ToCalibration());
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                measure = await ExtractionDiagnosticsRecorder.OwnedMeasurerAsync(factory, timeout.Token);
            }
            catch (Exception)
            {
                // tokenizer non raggiungibile: i token proprietari restano null nel riepilogo
            }

            var lines = await _diag.FinalizeAsync(connection, measure);
            TestContext.Current.SendDiagnosticMessage(string.Join('\n', lines));
        }
        catch (Exception e)
        {
            TestContext.Current.SendDiagnosticMessage($"diag: finalizzazione non riuscita ({e.GetType().Name}); calls.jsonl in {_diag.Directory}");
        }
    }

    private async Task RunScenarioCoreAsync(string label, Func<int, Corpus> build)
    {
        var v = new Verdicts(label);
        var (corpus, profile, generatedTokens) = await BuildSizedAsync(build);
        var budget = profile.InputBudgetTokens;
        var directory = Path.Combine(Path.GetTempPath(), "bprojects-i5-real", label + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        var manifestPath = corpus.Materialize(directory);
        v.Note($"configurazione: finestra misurata W={profile.WindowTokens} output O={profile.OutputTokens} margine M={profile.MarginTokens} ragionamento={profile.Thinking} => budget di input Analysis B=W-O-M={budget} token; modello={profile.ModelId}");
        v.Note($"corpus generato (seed {LargeDocumentCorpus.Seed}) in {directory}: {corpus.Documents.Count} documenti, {corpus.PlainTextChars} caratteri, {generatedTokens} token misurati = {(double)generatedTokens / budget:F2}x B; hash in {manifestPath}");
        foreach (var d in corpus.Documents)
        {
            v.Note($"  {d.FileName} sha256={d.Sha256} testo-sha256={d.PlainTextSha256} byte={d.Bytes.Length} regole-secondarie={d.BulkRules.Count}");
        }

        v.Check("[STRUTTURALE] il testo generato supera 2x il budget di input Analysis (tokenizer reale, prima dell'upload)", generatedTokens > MinimumRatio * budget, $"{generatedTokens} token contro 2xB={2 * budget}");

        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        if (corpus.PmStatement is not null)
        {
            // Dichiarazione del PM (messaggio di chat) PRIMA dei documenti: il turno di chat resta ordinario e non scansiona documenti oltre contesto (la chat ampia e I6, T084+).
            await PmMessageAsync(client, project, corpus.PmStatement, v);
        }

        var uploaded = new List<(CorpusDocument Doc, Guid Id)>();
        foreach (var doc in corpus.Documents)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, doc.FileName), Ct);
            v.Check($"[STRUTTURALE] {doc.FileName}: originale su disco con SHA-256 registrato", RealParser.Sha256(bytes) == doc.Sha256);
            var response = await Upload(client, project, bytes, doc.FileName, DocumentUploadValidation.MediaTypeOf(doc.Format), displayName: doc.FileName);
            v.Check($"[STRUTTURALE] {doc.FileName}: upload 201", response.StatusCode == HttpStatusCode.Created, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            if (response.StatusCode != HttpStatusCode.Created)
            {
                v.Flush();
                return;
            }

            uploaded.Add((doc, (await Json(response)).GetProperty("documentId").GetGuid()));
        }

        foreach (var (doc, id) in uploaded)
        {
            var status = await Poll(async () => (await Json(await client.GetAsync($"/api/projects/{project}/documents/{id}", Ct))).GetProperty("versions")[0].GetProperty("processingStatus").GetString()!,
                s => s is "Ready" or "Failed" or "Incomplete" or "Rejected", DocumentsTimeout);
            var detail = status;
            if (status != "Ready")
            {
                detail = $"{status}: {Short((await Json(await client.GetAsync($"/api/projects/{project}/documents/{id}/versions/1/preparations/latest", Ct))).GetRawText(), 500)} | {await PreparationDiagnosticsAsync()}";
            }

            v.Check($"[COMPLETAMENTO] {doc.FileName}: preparazione Ready (parser reale + estrazione reale su tutte le porzioni)", status == "Ready", detail);
            if (status != "Ready")
            {
                v.Flush();
                return;
            }
        }

        // --- testo REALMENTE estratto dal parser: budget, fedelta dei fatti gold, copertura delle porzioni.
        var extracted = new Dictionary<string, ExtractedDoc>();
        long extractedTokens = 0;
        var (_, count) = await BudgetAsync();
        foreach (var (doc, id) in uploaded)
        {
            var e = await ExtractedAsync(id);
            extracted[doc.Key] = e;
            extractedTokens += await count(e.Text);
            var pending = await Count(RunSignalSql.PendingPortions, ("p", e.PreparationId));
            var portions = await Count(RunSignalSql.Portions, ("p", e.PreparationId));
            v.Check($"[COPERTURA] {doc.FileName}: tutte le {portions} porzioni di estrazione sono Completed (nessuna omessa o fallita)", portions > 0 && pending == 0, $"{pending} non completate");
            v.Check($"[COPERTURA] {doc.FileName}: il testo estratto copre il testo generato (nessun blocco perso dal parser)", Fold(e.Text).Length >= Fold(doc.PlainText).Length * 0.97, $"{e.Text.Length} contro {doc.PlainText.Length} caratteri");
        }

        v.Note($"testo estratto dal parser: {extractedTokens} token = {(double)extractedTokens / budget:F2}x B");
        v.Check("[STRUTTURALE] il testo REALMENTE estratto dal parser supera 2x il budget di input Analysis (nessuna riduzione, finestra non aumentata)", extractedTokens > MinimumRatio * budget, $"{extractedTokens} token contro 2xB={2 * budget}");
        foreach (var fact in corpus.Gold.Where(g => g.DocKey != "pm"))
        {
            v.Check($"[PROVENIENZA] {fact.Id}: il fatto gold e nel testo estratto dal documento {fact.DocKey} (il parser non lo ha alterato)", Fold(extracted[fact.DocKey].Text).Contains(Fold(fact.Sentence), StringComparison.Ordinal));
        }

        // --- Analizza (endpoint di produzione): v4, gerarchia, completamento.
        var analysis = await AnalyzeAsync(client, project, v);
        if (analysis is null)
        {
            await TelemetryAsync(project, v);
            v.Flush();
            return;
        }

        var content = new Content(analysis.Content, await MessageBodies(analysis.Content));
        v.Note($"analisi: {content.Items.Count} voci, {content.Sources.Count} fonti, {content.Conflicts.Count} conflitti");
        foreach (var item in content.Items.Where(i => corpus.Gold.Any(g => GoldMatchesText(g, i)) || i.InConflict))
        {
            v.Note($"  [{item.Classification}{(item.InConflict ? "/conflitto" : "")}] {Short(item.Text, 150)} <= {Short(string.Join(" | ", item.Quotes), 110)}");
        }

        await VerifyStructureAsync(v, analysis.Run, project);
        await VerifyCoverageAsync(client, project, v, analysis.Run, content, extracted);
        VerifySemantics(v, corpus, content);
        await VerifyProvenanceAsync(client, project, v, corpus, content, extracted, uploaded);
        await TelemetryAsync(project, v);
        v.Flush();
    }

    // ================================================================ verifiche

    /// <summary>STRUTTURALE: definizione v4 e analisi passata dalla riconciliazione gerarchica (segnali reali nelle tabelle del run).</summary>
    private async Task VerifyStructureAsync(Verdicts v, Guid run, Guid project)
    {
        var args = ("r", (object)run);
        v.Check("[STRUTTURALE] la definizione del run e project-analysis-documents-v5 (T141)", await Scalar<string>(RunSignalSql.Definition, args) == DocumentAnalysisWorkflow.DefinitionVersionV5);
        var batches = await Count(RunSignalSql.Batches, args);
        var nodes = await Count(RunSignalSql.Nodes, args);
        var aliasOutputs = await Count(RunSignalSql.AliasOutputs, args);
        var singleOutputs = await Count(RunSignalSql.SingleOutputs, args);
        var calls = await Scalar<int>(RunSignalSql.Calls, args);
        v.Note($"gerarchia: batch={batches} nodi di catalogo={nodes} output reconcile-alias-1={aliasOutputs} output a chiamata singola={singleOutputs} chiamate del run={calls}");
        v.Check("[STRUTTURALE] l'analisi e passata dalla riconciliazione gerarchica (batch di riconciliazione e nodi di catalogo presenti, nessun output a chiamata singola)",
            batches > 1 && nodes > 0 && aliasOutputs == batches && singleOutputs == 0, $"batch={batches} nodi={nodes} alias={aliasOutputs} singola={singleOutputs}");
        v.Check("[STRUTTURALE] il run ha usato piu di una chiamata al modello (CallCount maggiore di 1)", calls > 1, $"CallCount={calls}");
        var openWork = await Count(RunSignalSql.OpenCandidates, args);
        var badPatches = await Count(RunSignalSql.BadPatches, args);
        v.Check("[COPERTURA] ogni candidato ha disposition terminale e nessuna patch resta Stale/Invalid/NotProcessed (nessuna porzione omessa in silenzio)", openWork == 0 && badPatches == 0, $"candidati non terminali={openWork} patch non valide={badPatches}");
        _ = project;
    }

    /// <summary>COPERTURA: ogni evidenza selezionata e collegata a una voce; citazioni documentali coerenti con ledger e passaggi.</summary>
    private async Task VerifyCoverageAsync(HttpClient client, Guid project, Verdicts v, Guid run, Content content, IReadOnlyDictionary<string, ExtractedDoc> extracted)
    {
        var selected = await Count(RunSignalSql.SelectedEvidences, ("r", run));
        var linked = await Count(RunSignalSql.LinkedEvidences, ("r", run));
        v.Check("[COPERTURA] ogni evidenza selezionata dallo snapshot e collegata a una voce della revisione", selected > 0 && linked == selected, $"selezionate={selected} collegate={linked}");
        v.Check("[COPERTURA] ogni voce ha almeno una fonte dichiarata", content.Items.All(i => i.Refs.Count > 0 && i.Refs.All(content.Sources.ContainsKey)));

        var evidences = await DocumentEvidences(client, project);
        var bySource = evidences.SelectMany(e => e.Sources).GroupBy(s => s.SourceId).ToDictionary(g => g.Key, g => g.First());
        var referenced = content.Items.SelectMany(i => i.Refs).ToHashSet();
        var missing = evidences.Count(e => e.Sources.Any(s => !referenced.Contains(s.SourceId)));
        v.Check($"[COPERTURA] ogni evidenza documentale ({evidences.Count}) ha tutte le sue fonti riferite da una voce", missing == 0, $"{missing} evidenze con fonti non riferite");
        var allText = extracted.Values.Select(e => Fold(e.Text)).ToList();
        var wrong = new List<string>();
        var checkedCount = 0;
        foreach (var source in content.Sources.Values.Where(s => s.Kind == "DocumentPassage"))
        {
            checkedCount++;
            if (!bySource.TryGetValue(source.Id, out var known)
                || known.Quote != source.Quote || known.BlockId != source.BlockId || known.Start != source.Start || known.End != source.End || known.DocumentId != source.DocumentId
                || await PassageText(client, project, known) != source.Quote
                || !allText.Any(t => t.Contains(Fold(source.Quote!), StringComparison.Ordinal)))
            {
                wrong.Add(Short(source.Quote ?? "", 40));
            }
        }

        v.Check($"[PROVENIENZA] 100% delle citazioni documentali valide ({checkedCount} fonti: ID reale dell'evidenza, blocco/range, citazione esatta nel passaggio e nel testo estratto)", checkedCount > 0 && wrong.Count == 0, string.Join(" | ", wrong.Take(10)));
        v.Check("[PROVENIENZA] Declared solo con messaggio PM, Documented solo con passaggio di documento", content.Items.All(i =>
            (i.Classification != "Declared" || i.Refs.Any(r => content.Sources[r].Kind == "Message")) && (i.Classification != "Documented" || i.Refs.Any(r => content.Sources[r].Kind == "DocumentPassage"))));
    }

    /// <summary>SEMANTICA: fatti gold con polarita e qualificatori, conflitti preservati e mai risolti, equivalenze parafrasate non scambiate per conflitti.</summary>
    private static void VerifySemantics(Verdicts v, Corpus corpus, Content content)
    {
        foreach (var fact in corpus.Gold)
        {
            var found = content.Items.Any(i => GoldMatchesText(fact, i));
            v.Check($"[SEMANTICA] {fact.Id}{(fact.Negative ? " (negazione)" : "")}: fatto presente con numeri/condizioni/polarita corretti — {Short(fact.Sentence, 90)}", found);
        }

        foreach (var conflict in corpus.Conflicts)
        {
            var open = content.Conflicts.Where(c => c.Status == "Open" && !c.HasResolution).ToList();
            var sideRepresented = conflict.Sides.Select(side => open.Any(c => c.Alternatives.Any(a => side.All(a.QuotesHave)))).ToList();
            v.Check($"[SEMANTICA] conflitto «{conflict.Id}»: ogni posizione resta come alternativa di un conflitto Open senza risoluzione ({conflict.Sides.Length} lati)", sideRepresented.All(x => x),
                string.Join(", ", conflict.Sides.Where((_, k) => !sideRepresented[k]).Select(s => string.Join(" ", s))));
            v.Check($"[SEMANTICA] conflitto «{conflict.Id}»: almeno un conflitto Open mette a confronto due o piu posizioni", open.Any(c => conflict.Sides.Count(side => c.Alternatives.Any(a => side.All(a.QuotesHave))) >= 2));
            var arbitrary = content.Items.Where(i => !i.InConflict && conflict.Topic.All(i.Has) && conflict.Sides.Any(side => side.All(t => i.Has(t) || i.QuotesHave(t)))).Select(i => i.Short).ToList();
            v.Check($"[SEMANTICA] conflitto «{conflict.Id}»: nessuna posizione e affermata fuori dal conflitto (nessuna risoluzione arbitraria)", arbitrary.Count == 0, string.Join(" | ", arbitrary));
        }

        foreach (var eq in corpus.Equivalents)
        {
            var a = content.Items.Any(i => eq.QuoteA.All(i.QuotesHave));
            var b = content.Items.Any(i => eq.QuoteB.All(i.QuotesHave));
            var spurious = content.Conflicts.Any(c => c.Alternatives.Any(x => eq.QuoteA.All(x.QuotesHave)) && c.Alternatives.Any(x => eq.QuoteB.All(x.QuotesHave)));
            v.Check($"[SEMANTICA] equivalenza parafrasata «{eq.Id}»: entrambe le formulazioni sono rappresentate e non sono un conflitto", a && b && !spurious, $"A={a} B={b} conflitto-spurio={spurious}");
        }

        v.Check("[SEMANTICA] nessun item Declared senza giustificazione del PM quando non c'e dichiarazione", corpus.PmStatement is not null || content.Items.All(i => i.Classification != "Declared"));
    }

    /// <summary>PROVENIENZA: ogni fatto gold cita il documento e il passaggio corretti, nella zona attesa (inizio/meta/fine) del testo estratto.</summary>
    private async Task VerifyProvenanceAsync(HttpClient client, Guid project, Verdicts v, Corpus corpus, Content content, IReadOnlyDictionary<string, ExtractedDoc> extracted, List<(CorpusDocument Doc, Guid Id)> uploaded)
    {
        _ = client;
        _ = project;
        var docKeyById = uploaded.ToDictionary(u => u.Id, u => u.Doc.Key);
        foreach (var fact in corpus.Gold)
        {
            var ok = content.Items.Where(i => GoldMatchesText(fact, i)).Any(i => i.Refs.Select(r => content.Sources[r]).Any(s =>
                fact.DocKey == "pm"
                    ? s.Kind == "Message" && fact.Quote.All(t => Fold(content.MessageBody(s)).Contains(t, StringComparison.Ordinal))
                    : s.Kind == "DocumentPassage" && s.DocumentId is { } d && docKeyById.GetValueOrDefault(d) == fact.DocKey
                      && fact.Quote.All(t => Fold(s.Quote!).Contains(t, StringComparison.Ordinal)) && InZone(extracted[fact.DocKey], s, fact.Zone)));
            v.Check($"[PROVENIENZA] {fact.Id}: la voce cita {(fact.DocKey == "pm" ? "il messaggio del PM" : $"il documento {fact.DocKey}, passaggio corretto, zona {fact.Zone}")}", ok);
        }
    }

    private static bool GoldMatchesText(GoldFact fact, Item item) =>
        item.Classification == fact.Class && fact.Text.All(item.Has) && (!fact.Negative || item.NegativeText || item.Section == "excluded");

    private static bool InZone(ExtractedDoc doc, Source source, CorpusZone zone)
    {
        if (source.BlockId is not { } block || source.Start is not { } start || !doc.BlockOffsets.TryGetValue(block, out var offset))
        {
            return false;
        }

        var ratio = (double)(offset + start) / Math.Max(1, doc.Text.Length);
        return zone switch
        {
            CorpusZone.Start => ratio < 0.25,
            CorpusZone.Middle => ratio is >= 0.35 and <= 0.65,
            _ => ratio > 0.75,
        };
    }

    // ================================================================ passi

    protected sealed record ExtractedDoc(Guid PreparationId, string Text, IReadOnlyDictionary<Guid, int> BlockOffsets);

    private async Task<ExtractedDoc> ExtractedAsync(Guid document)
    {
        var preparation = await Scalar<Guid>(RunSignalSql.LatestPreparation, ("d", document));
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand(RunSignalSql.Blocks, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var text = new StringBuilder();
        var offsets = new Dictionary<Guid, int>();
        while (await reader.ReadAsync(Ct))
        {
            offsets[reader.GetGuid(0)] = text.Length;
            text.Append(reader.GetString(1)).Append("\n\n");
        }

        return new ExtractedDoc(preparation, text.ToString(), offsets);
    }

    private async Task PmMessageAsync(HttpClient client, Guid project, string statement, Verdicts v)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text = statement }, Ct);
        v.Check("[COMPLETAMENTO] dichiarazione del PM: POST messaggio 201", response.StatusCode == HttpStatusCode.Created, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        if (response.StatusCode != HttpStatusCode.Created)
        {
            return;
        }

        var turn = (await Json(response)).GetProperty("chatTurnId").GetGuid();
        var status = await Poll(() => Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)), s => s is not ("Queued" or "Running"), ChatTimeout);
        v.Check("[COMPLETAMENTO] dichiarazione del PM: turno di chat Completed (l'estrazione della dichiarazione e in coda al turno)", status == "Completed", status);
    }

    private sealed record AnalysisOutcome(Guid Run, Guid Revision, JsonNode Content);

    private async Task<AnalysisOutcome?> AnalyzeAsync(HttpClient client, Guid project, Verdicts v)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        var body = await Json(response);
        v.Check("[COMPLETAMENTO] POST Analizza 202", response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode} {Short(body.GetRawText(), 300)}");
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            return null;
        }

        var run = body.GetProperty("id").GetGuid();
        var status = await Poll(() => Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)), s => s is not ("Queued" or "Running"), RunTimeout);
        v.Check("[COMPLETAMENTO] run di analisi terminale Completed (un errore gestito non e un PASS)", status == "Completed", $"{status} {await Failure(run)}");
        if (status != "Completed")
        {
            return null;
        }

        var view = await Json(await client.GetAsync($"/api/projects/{project}/analyses/{run}", Ct));
        v.Check("[COMPLETAMENTO] revisione AI 1.1.0 con ErrorCode assente", view.GetProperty("schemaVersion").GetString() == "1.1.0" && await Scalar<string>("SELECT coalesce(\"ErrorCode\", '') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)) == "");
        return new AnalysisOutcome(run, view.GetProperty("currentRevisionId").GetGuid(), JsonNode.Parse(view.GetProperty("content").GetRawText())!);
    }

    private async Task TelemetryAsync(Guid project, Verdicts v)
    {
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT \"Phase\", count(*), coalesce(sum(\"DurationMs\"), 0), coalesce(sum(CASE WHEN \"FinishReason\" = 'length' THEN 1 ELSE 0 END), 0) FROM \"InferenceCalls\" WHERE \"ProjectId\" = @p GROUP BY \"Phase\" ORDER BY 1", connection);
        command.Parameters.AddWithValue("p", project);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        long truncated = 0;
        while (await reader.ReadAsync(Ct))
        {
            v.Note($"chiamate al modello fase={reader.GetValue(0)}: n={reader.GetValue(1)} durata totale={Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture) / 1000}s finish=length={reader.GetValue(3)}");
            truncated += Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
        }

        v.Check("[COMPLETAMENTO] nessuna chiamata troncata (finish=length)", truncated == 0, $"{truncated} troncate");
    }

    /// <summary>Solo osservabilita: errori dei job documentali falliti e riepilogo delle chiamate di inferenza (il database di test viene eliminato a fine prova).</summary>
    private async Task<string> PreparationDiagnosticsAsync()
    {
        var parts = new List<string>();
        await using var connection = await OpenConnection();
        await using (var jobs = new Npgsql.NpgsqlCommand("SELECT \"Kind\"::text, \"Status\"::text, \"ErrorCode\", \"ErrorMessage\" FROM \"DocumentJobs\" WHERE \"ErrorCode\" IS NOT NULL OR \"Status\"::text = 'Failed' LIMIT 5", connection))
        await using (var reader = await jobs.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                parts.Add($"job {reader.GetString(0)}/{reader.GetString(1)} {(reader.IsDBNull(2) ? "-" : reader.GetString(2))}: {(reader.IsDBNull(3) ? "-" : Short(reader.GetString(3), 600))}");
            }
        }

        await using (var calls = new Npgsql.NpgsqlCommand("SELECT \"Phase\"::text, \"State\"::text, \"FinishReason\", \"PromptTokens\", \"CompletionTokens\", \"MaxOutputTokensRequested\", \"DurationMs\" FROM \"InferenceCalls\" ORDER BY \"StartedAt\" DESC LIMIT 6", connection))
        await using (var reader = await calls.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                parts.Add($"call {reader.GetString(0)}/{reader.GetString(1)} finish={(reader.IsDBNull(2) ? "-" : reader.GetString(2))} prompt={(reader.IsDBNull(3) ? "-" : reader.GetValue(3))} completion={(reader.IsDBNull(4) ? "-" : reader.GetValue(4))} maxOut={(reader.IsDBNull(5) ? "-" : reader.GetValue(5))} ms={(reader.IsDBNull(6) ? "-" : reader.GetValue(6))}");
            }
        }

        return string.Join(" ; ", parts);
    }

    private async Task<string> Failure(Guid run)
    {
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand("SELECT \"ErrorCode\", \"ErrorMessage\" FROM \"ProcessingRuns\" WHERE \"Id\" = @i", connection);
        command.Parameters.AddWithValue("i", run);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct) ? $"{(reader.IsDBNull(0) ? "-" : reader.GetString(0))}: {(reader.IsDBNull(1) ? "-" : Short(reader.GetString(1), 400))}" : "(riga assente)";
    }

    private static async Task<string> Poll(Func<Task<string>> probe, Func<string, bool> terminal, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = "";
        while (DateTime.UtcNow < deadline)
        {
            last = await probe();
            if (terminal(last))
            {
                return last;
            }

            await Task.Delay(3_000, Ct);
        }

        return last + " (TIMEOUT)";
    }

    // ================================================================ dati

    protected sealed record Source(Guid Id, string Kind, string? Quote, Guid? DocumentId, Guid? BlockId, int? Start, int? End, JsonNode Raw);

    protected sealed class Item(string section, bool inConflict, string text, string classification, IReadOnlyList<Guid> refs, IReadOnlyDictionary<Guid, Source> sources, IReadOnlyDictionary<Guid, string> messages)
    {
        public string Section { get; } = section;
        public bool InConflict { get; } = inConflict;
        public string Text { get; } = text;
        public string Classification { get; } = classification;
        public IReadOnlyList<Guid> Refs { get; } = refs;

        public IEnumerable<string> Quotes => Refs.Select(r => sources.TryGetValue(r, out var s) ? s.Kind == "Message" ? messages.GetValueOrDefault(Guid.TryParse(s.Raw["messageId"]?.GetValue<string>(), out var m) ? m : Guid.Empty, "") : s.Quote ?? "" : "");

        public string Short => LargeDocumentRealFlow.Short(Text, 120);

        public bool Has(string token) => LargeDocumentRealFlow.HasToken(Text, token);

        public bool QuotesHave(string token) => Quotes.Any(q => LargeDocumentRealFlow.HasToken(q, token));

        public bool NegativeText => AnalysisVerificationRules.IsNegative(Text);
    }

    protected sealed class Conflict(string status, IReadOnlyList<Item> alternatives, bool hasResolution)
    {
        public string Status { get; } = status;
        public IReadOnlyList<Item> Alternatives { get; } = alternatives;
        public bool HasResolution { get; } = hasResolution;
    }

    protected sealed class Content
    {
        private readonly IReadOnlyDictionary<Guid, string> _messages;

        public Content(JsonNode root, IReadOnlyDictionary<Guid, string> messages)
        {
            _messages = messages;
            Sources = root["sources"]!.AsArray().Select(s => new Source(
                Guid.Parse(s!["id"]!.GetValue<string>()), s["kind"]!.GetValue<string>(), s["quote"]?.GetValue<string>(),
                Guid.TryParse(s["documentId"]?.GetValue<string>(), out var d) ? d : null, Guid.TryParse(s["blockId"]?.GetValue<string>(), out var b) ? b : null,
                s["start"]?.GetValue<int>(), s["end"]?.GetValue<int>(), s)).ToDictionary(s => s.Id);
            var items = new List<Item>();
            Item Make(string section, bool inConflict, JsonNode n) =>
                new(section, inConflict, n["text"]!.GetValue<string>(), n["classification"]!.GetValue<string>(), [.. n["evidenceRefs"]!.AsArray().Select(r => Guid.Parse(r!.GetValue<string>()))], Sources, messages);
            foreach (var name in new[] { "goals", "requirements", "operationalFlows", "constraints", "dependencies", "assumptions", "openQuestions" })
            {
                items.AddRange(root[name]!.AsArray().Select(n => Make(name, false, n!)));
            }

            foreach (var name in new[] { "overview", "included", "excluded" })
            {
                items.AddRange(root["contextAndScope"]![name]!.AsArray().Select(n => Make(name, false, n!)));
            }

            var conflicts = new List<Conflict>();
            foreach (var c in root["conflicts"]!.AsArray())
            {
                var alternatives = c!["alternatives"]!.AsArray().Select(n => Make("conflict", true, n!)).ToList();
                items.AddRange(alternatives);
                conflicts.Add(new Conflict(c["status"]!.GetValue<string>(), alternatives, c["resolution"] is not null));
            }

            Items = items;
            Conflicts = conflicts;
        }

        public IReadOnlyDictionary<Guid, Source> Sources { get; }
        public IReadOnlyList<Item> Items { get; }
        public IReadOnlyList<Conflict> Conflicts { get; }

        public string MessageBody(Source source) => _messages.GetValueOrDefault(Guid.TryParse(source.Raw["messageId"]?.GetValue<string>(), out var m) ? m : Guid.Empty, "");
    }

    private async Task<IReadOnlyDictionary<Guid, string>> MessageBodies(JsonNode content)
    {
        var map = new Dictionary<Guid, string>();
        foreach (var source in content["sources"]!.AsArray().Where(s => s!["kind"]!.GetValue<string>() == "Message"))
        {
            var id = Guid.Parse(source!["messageId"]!.GetValue<string>());
            map[id] = await Scalar<string>("SELECT \"Body\" FROM \"ConversationMessages\" WHERE \"Id\" = @i", ("i", id));
        }

        return map;
    }

    private sealed record EvSource(Guid SourceId, Guid DocumentId, Guid PreparationId, Guid BlockId, int Start, int End, string Quote, int Version);

    private sealed record Ev(Guid Id, List<EvSource> Sources);

    private static async Task<List<Ev>> DocumentEvidences(HttpClient client, Guid project)
    {
        var page = await Json(await client.GetAsync($"/api/projects/{project}/evidence?originKind=Document&viewVersion=1.1.0&limit=100", Ct));
        return [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("evidence")).Select(e => new Ev(
            e.GetProperty("id").GetGuid(),
            [.. e.GetProperty("content").GetProperty("sources").EnumerateArray().Select(s => new EvSource(
                s.GetProperty("sourceId").GetGuid(), s.GetProperty("documentId").GetGuid(), s.GetProperty("preparationId").GetGuid(), s.GetProperty("blockId").GetGuid(),
                s.GetProperty("start").GetInt32(), s.GetProperty("end").GetInt32(), s.GetProperty("quote").GetString()!, s.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number ? ver.GetInt32() : 1))]))];
    }

    private static async Task<string?> PassageText(HttpClient client, Guid project, EvSource source)
    {
        var response = await client.GetAsync(
            $"/api/projects/{project}/documents/{source.DocumentId}/versions/{source.Version}/preparations/{source.PreparationId}/passages/{source.BlockId}?start={source.Start}&end={source.End}", Ct);
        return response.IsSuccessStatusCode ? (await Json(response)).GetProperty("quote").GetProperty("text").GetString() : null;
    }

    // ================================================================ testo e report

    internal static string Short(string text, int max = 160)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    internal static string Fold(string text) => DocumentSmallFlowMafRealTests.Fold(text);

    /// <summary>Token «a|b»: vero se una delle alternative (piegate) e contenuta nel testo piegato.</summary>
    internal static bool HasToken(string text, string token)
    {
        var folded = Fold(text);
        return token.Split('|').Any(t => folded.Contains(Fold(t), StringComparison.Ordinal));
    }

    protected sealed class Verdicts(string label)
    {
        private readonly List<string> _lines = [];
        private readonly List<string> _failures = [];

        public void Note(string line) => _lines.Add("  NOTE " + line);

        public void Check(string name, bool ok, string detail = "")
        {
            _lines.Add($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
            if (!ok)
            {
                _failures.Add(name + (detail.Length > 0 ? " — " + detail : ""));
            }
        }

        public void Flush()
        {
            var directory = Path.Combine(Path.GetTempPath(), "bprojects-i5-real");
            Directory.CreateDirectory(directory);
            var report = new StringBuilder().AppendLine($"== {label} {(_failures.Count == 0 ? "PASS" : "FAIL")} ({_lines.Count(l => l.StartsWith("  PASS", StringComparison.Ordinal))} PASS / {_failures.Count} FAIL) ==").AppendJoin('\n', _lines).AppendLine().ToString();
            File.WriteAllText(Path.Combine(directory, label + "-" + DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture) + ".txt"), report);
            TestContext.Current.SendDiagnosticMessage(report);
            TestContext.Current.TestOutputHelper?.WriteLine(report);
            Assert.True(_failures.Count == 0, $"{label}: {_failures.Count} verifiche fallite: " + string.Join(" || ", _failures));
        }
    }
}

/// <summary>Query sui segnali reali del run e dell'estrazione: condivise fra le prove DGX (T082/T083) e la prova offline con modello finto, che le esegue su un run v4 reale.</summary>
internal static class RunSignalSql
{
    public const string Definition = "SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r";
    public const string Batches = "SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r";
    public const string Nodes = "SELECT count(*) FROM \"RunCatalogNodes\" WHERE \"RunId\" = @r";
    public const string AliasOutputs = "SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"SchemaVersion\" = 'reconcile-alias-1' AND \"Status\" = 'Committed'";
    public const string SingleOutputs = "SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"SchemaVersion\" = 'document-analysis-alias-1'";
    public const string Calls = "SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r";

    public const string OpenCandidates =
        """
        SELECT count(*) FROM "RunCandidateDispositions" d
        WHERE d."RunId" = @r AND d."State" IN ('Pending', 'NotProcessed')
          AND d."Version" = (SELECT max(x."Version") FROM "RunCandidateDispositions" x WHERE x."RunId" = d."RunId" AND x."CandidateId" = d."CandidateId")
        """;

    public const string BadPatches =
        """
        SELECT count(*) FROM "RunPatchDispositions" d
        WHERE d."RunId" = @r AND d."Kind" IN ('Stale', 'Invalid', 'NotProcessed')
          AND d."Version" = (SELECT max(x."Version") FROM "RunPatchDispositions" x WHERE x."RunId" = d."RunId" AND x."PatchId" = d."PatchId")
        """;

    public const string SelectedEvidences =
        """
        SELECT count(*) FROM "Evidences" e WHERE e."VersionId" IN (SELECT i."VersionId" FROM "AnalysisEvidenceSelectionItems" i WHERE i."RunId" = @r)
        """;

    public const string LinkedEvidences = "SELECT count(DISTINCT \"EvidenceId\") FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r";
    public const string Portions = "SELECT count(*) FROM \"DocumentPortions\" WHERE \"PreparationId\" = @p";
    public const string PendingPortions = "SELECT count(*) FROM \"DocumentPortions\" q WHERE q.\"PreparationId\" = @p AND q.\"Status\" <> 'Completed'";

    public const string LatestPreparation =
        """
        SELECT p."Id" FROM "DocumentPreparations" p JOIN "DocumentVersions" x ON x."Id" = p."DocumentVersionId"
        WHERE x."DocumentId" = @d ORDER BY p."Generation" DESC LIMIT 1
        """;

    public const string Blocks =
        """
        SELECT b."Id", b."CanonicalText" FROM "DocumentBlocks" b JOIN "ParsedArtifacts" a ON a."Id" = b."ArtifactId"
        WHERE a."PreparationId" = @p ORDER BY b."Ordinal"
        """;
}
