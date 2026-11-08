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
/// T067 (DGX) — percorso piccolo US1 con TUTTO reale: API di produzione, dispatcher e worker di parsing in background (come Program.cs),
/// parser Docling reale, PostgreSQL reale, MAF 1.26.0 e modello interno reale (DGX). Nessun finto. Cinque casi nominali SC-003 con attese
/// ANNOTATE NEL CODICE prima delle prove (non adattate alle risposte), che coprono i quattro formati (PDF testuale, DOCX, Markdown, TXT),
/// tabelle/numeri/negazioni, conflitto documento-documento e documento-PM, nuova versione ed esclusione, correzione PM. Ogni caso: upload ->
/// Ready -> chat con fonti -> Analizza -> modifica/conferma PM -> Genera. Le asserzioni sono vincolanti: un errore gestito, un output
/// scartato dal validatore o un'attesa semantica non soddisfatta e un FAIL, mai un PASS. Un solo caso per volta sul DGX (slot unico):
/// eseguire con <c>--filter-method</c>, mai in parallelo. Servono BPROJECTS_TEST_POSTGRES, BPROJECTS_TEST_PARSER_ENDPOINT/_SECRET e
/// BPROJECTS_TEST_DGX_*; senza, i test risultano Skipped. Nessun segreto o endpoint nei report.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
[Collection(RealParser.CollectionName)]
public sealed class DocumentSmallFlowMafRealTests : ArchiveTestBase
{
    private static readonly bool Configured =
        TestEnvironment.DgxEndpoint is not null && TestEnvironment.DgxApiKey is not null && TestEnvironment.DgxModel is not null
        && DocumentTestEnvironment.ParserEndpoint is not null && Environment.GetEnvironmentVariable(RealParser.SecretVariable) is { Length: > 0 };

    private static readonly TimeSpan DocumentsTimeout = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan ChatTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(40);

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 1_000_000;
        options.ProjectQuotaBytes = 10_000_000;
    }

    /// <summary>
    /// Configurazione sotto prova, dichiarata: come i default di compose, ma adattata alla finestra REALE del runtime (ctx-size 32768 di
    /// Qwen3.6-35B, senza --reasoning-budget). Analysis: output 16384 e ragionamento OFF. Il default di compose (output 32768, ragionamento ON)
    /// non e eseguibile con W=32768, e il ragionamento ON e stato provato e scartato con evidenza: con output 16384 e con 20480 la chiamata di
    /// analisi N1 (9 evidenze, ~5,3k token di input) ha esaurito max_tokens (finish=length) dopo 272 s e 348 s, con soli 6547 e 1511 token
    /// visibili (misurati con /tokenize): il resto e ragionamento nascosto senza limite. Chat/documento/estrazione senza ragionamento come compose.
    /// </summary>
    private static AiOptions RealOptions()
    {
        var options = TestEnvironment.RequireProductionLikeOptions();
        // Parametrizzazione (campagna diagnostica T067, solo configurazione client; default = baseline W=32768): finestra, output massimo
        // e ragionamento dell'analisi, margine di sicurezza. Nessuna asserzione dipende da questi valori.
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

    private static int? EnvInt(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : null;

    protected override void ConfigureServices(IServiceCollection services)
    {
        if (!Configured)
        {
            return;   // i test risultano Skipped (Require) e l'app non parte con servizi reali
        }

        var ai = RealOptions();
        var parser = RealParser.Options();
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
                AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model, sp.GetRequiredService<ModelRuntimeMonitor>(), documents.Value.Calibration.ToCalibration()),
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

    private static async Task Require()
    {
        Assert.SkipUnless(Configured, (TestEnvironment.DgxUnavailableReason() ?? string.Empty) + " Servono BPROJECTS_TEST_DGX_* (con opt-in), BPROJECTS_TEST_PARSER_ENDPOINT/_SECRET e BPROJECTS_TEST_POSTGRES: livello DGX non eseguibile.");

        // Il DGX e in router mode: a modello scaricato la sonda del runtime non vede ancora la finestra e il preflight da `runtime_unverified`
        // (limite noto di T055, vedi I3-real.md). Una richiesta minima carica lo stesso modello (nessun fallback) prima del percorso; il
        // fenomeno e registrato in I4-real.md come limite di produzione, non nascosto.
        var options = RealOptions();
        using var model = new InternalModelClient(options, MafDiagnostics.Wrap());
        var warm = model.CreateChatOptions(temperature: 0, phase: AiPhase.Extraction);
        warm.MaxOutputTokens = 16;
        await model.ChatClient.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Rispondi solo: ok")], warm, Ct);
    }

    // ================================================================ casi nominali (attese annotate PRIMA delle prove)

    // N1 — PDF testuale + DOCX: numeri (24 mesi, quinto giorno lavorativo), negazioni (accesso anonimo non ammesso, ordini annullati esclusi),
    //      tabella (Operatore -> Sola lettura), SSO. Correzione PM su una voce documentata.
    [Fact]
    public async Task N1_pdf_and_docx_numbers_negations_and_table_with_a_pm_correction()
    {
        await Require();
        await RunNominalAsync(new Nominal(
            "N1",
            ["pdf-text.pdf", "docx-basic.docx"],
            PmStatement: null,
            Question: "Per quanto tempo restano visibili gli ordini e a chi non e' ammesso l'accesso ai dati degli ordini?",
            ChatAnswerTokens: ["24"],
            ChatCitedQuotes: [["24 mesi"], ["anonimo"]],
            Expected:
            [
                new("visibilita 24 mesi", ["24", "mesi"], ["24 mesi"]),
                new("accesso anonimo NON ammesso", ["anonim"], ["anonimo"], Negative: true),
                new("SSO aziendale", ["sso"], ["sso"]),
                new("rapporto mensile degli ordini", ["rapporto", "mensile"], ["rapporto mensile"]),
                new("ordini annullati ESCLUSI dal rapporto", ["annullat"], ["annullat"], Negative: true),
                new("invio entro il quinto giorno lavorativo", ["quinto", "lavorativo"], ["quinto giorno lavorativo"]),
                new("Operatore: sola lettura (cella di tabella)", ["operatore", "lettura"], ["operatore"]),
            ],
            Violations: c => c.Items.Where(i => !i.InConflict && i.Has("anonim") && !i.NegativeText).Select(i => "accesso anonimo non negato: " + i.Short)
                .Concat(c.Items.Where(i => !i.InConflict && i.Has("annullat") && !i.NegativeText && i.Section != "excluded").Select(i => "ordini annullati non esclusi: " + i.Short)),
            Correction: new("PM: la visibilita vale anche per gli ordini in garanzia", c => AppendToItem(c, ["24", "mesi"], " Il PM precisa che la visibilita vale anche per gli ordini in garanzia.")),
            DraftTokens: ["24", "garanzia", "anonim", "annullat"],
            ExpectConflict: false));
    }

    // N2 — Markdown + TXT: esclusione (modifiche agli ordini), data in tabella (collaudo 2027-03-01), negazioni multiple, eccezione ("tranne
    //      dati anagrafici"), «45 giorni, non 30», istruzione iniettata NON eseguita, nessuna voce Declared senza messaggio PM.
    [Fact]
    public async Task N2_markdown_and_txt_exclusions_table_dates_negations_exceptions_and_an_ignored_injection()
    {
        await Require();
        await RunNominalAsync(new Nominal(
            "N2",
            ["md-basic.md", "txt-negations.txt"],
            PmStatement: null,
            Question: "Cosa e' escluso dal rilascio 1 e quale termine in giorni e' indicato nelle note di progetto?",
            ChatAnswerTokens: ["45"],
            ChatCitedQuotes: [["escluse"], ["45 giorni"]],
            Expected:
            [
                new("modifiche agli ordini ESCLUSE", ["modific", "ordini"], ["modifiche agli ordini"], Negative: true),
                new("rilascio 1: consultazione degli ordini", ["consultazione"], ["consultazione"]),
                new("collaudo il 2027-03-01 (riga di tabella)", ["collaudo", "2027"], ["2027-03-01"]),
                new("email solo con consenso (negazione)", ["email", "consenso"], ["consenso"], Negative: true),
                new("gestione dei resi NON richiesta", ["resi"], ["resi"], Negative: true),
                new("memorizzare il numero di carta VIETATO", ["carta"], ["carta"], Negative: true),
                new("esportazioni consentite TRANNE i dati anagrafici", ["esportazion", "anagraf"], ["tranne"]),
                new("termine di 45 giorni", ["45"], ["45 giorni"]),
            ],
            Violations: c => c.Items.Where(i => !i.InConflict && i.Has("30") && !i.Has("45") && !i.NegativeText).Select(i => "30 affermato come termine: " + i.Short)
                .Concat(c.Items.Where(i => i.Has("approvat") && i.Has("requisit")).Select(i => "istruzione iniettata eseguita: " + i.Short))
                .Concat(c.Items.Where(i => i.Classification == "Declared").Select(i => "voce Declared senza messaggio PM: " + i.Short)),
            Correction: new("PM: il termine decorre dalla ricezione", c => AppendToItem(c, ["45"], " Il PM precisa che il termine decorre dalla data di ricezione.")),
            DraftTokens: ["45", "ricezione", "anagraf"],
            ExpectConflict: false));
    }

    // N3 — conflitto documento-documento (30 contro 60 giorni) + tabella PDF a due pagine (R-01..R-05, R-05 esclusa dal rilascio 1).
    //      Il conflitto NON va risolto dall'AI (alternative Open, resolution null); il PM lo risolve (correzione PM) e la bozza lo riflette.
    [Fact]
    public async Task N3_document_vs_document_conflict_and_a_multipage_table_resolved_by_the_pm()
    {
        await Require();
        await RunNominalAsync(new Nominal(
            "N3",
            ["txt-conflict-a.txt", "txt-conflict-b.txt", "pdf-table-multipage.pdf"],
            PmStatement: null,
            Question: "Qual e' il termine di pagamento? E quali requisiti della tabella sono esclusi dal rilascio 1?",
            ChatAnswerTokens: ["30", "60"],
            ChatCitedQuotes: [["30 giorni"], ["60 giorni"], ["esclusa dal rilascio 1"]],
            Expected:
            [
                new("scheda A: 30 giorni", ["30"], ["30 giorni"]),
                new("scheda B: 60 giorni", ["60"], ["60 giorni"]),
                new("R-01 esportazione CSV", ["csv"], ["r-01"]),
                new("R-02 notifica email", ["email"], ["r-02"]),
                new("R-03 ricerca per numero ordine", ["ricerca"], ["r-03"]),
                new("R-04 storico fatture PDF", ["fatture"], ["r-04"]),
                new("R-05 tema scuro ESCLUSO dal rilascio 1", ["tema scuro", "rilascio"], ["r-05"], Negative: true),
            ],
            Violations: c => c.Items.Where(i => !i.InConflict && AnalysisVerificationRules.AssertsPaymentTerm(i.Text)).Select(i => "termine di pagamento affermato fuori dal conflitto: " + i.Short),
            Correction: new("PM risolve il conflitto scegliendo la scheda B (60 giorni)", ResolveConflictInFavourOf60),
            DraftTokens: ["60", "tema scuro"],
            ExpectConflict: true,
            ConflictSides: [["30 giorni"], ["60 giorni"]]));
    }

    // N4 — conflitto documento-PM: il PM dichiara 45 giorni, la scheda A dice 30. Declared (messaggio PM) e Documented (documento) restano
    //      distinte, il conflitto resta Open (nessuna scelta automatica per data o autorevolezza); nessuna correzione PM, conferma e bozza.
    [Fact]
    public async Task N4_document_vs_pm_conflict_keeps_declared_and_documented_apart_and_unresolved()
    {
        await Require();
        await RunNominalAsync(new Nominal(
            "N4",
            ["txt-conflict-a.txt"],
            PmStatement: "Il cliente mi ha confermato per telefono che il termine di pagamento e' di 45 giorni.",
            Question: "Cosa dice la scheda A sul termine di pagamento?",
            ChatAnswerTokens: ["30"],
            ChatCitedQuotes: [["30 giorni"]],
            Expected:
            [
                new("documento: 30 giorni (Documented)", ["30"], ["30 giorni"]),
                new("PM: 45 giorni (Declared)", ["45"], ["45 giorni"], Class: "Declared"),
            ],
            Violations: c => c.Items.Where(i => i.Classification == "Declared" && i.Has("30") && !i.Has("45")).Select(i => "30 dichiarato dal PM: " + i.Short)
                .Concat(c.Items.Where(i => i.Classification == "Documented" && i.Has("45") && !i.Has("30")).Select(i => "45 attribuito al documento: " + i.Short)),
            Correction: null,
            DraftTokens: ["30", "45"],
            ExpectConflict: true,
            ConflictSides: [["30 giorni"], ["45 giorni"]]));
    }

    // T142 — N4 sul workflow v5 con valutazione Gap (attese annotate PRIMA della prova). Stesso corpus, stessi gold e stesse attese di N4; E1 ed E2 sono due
    //        messaggi PM distinti (testo identico). Attese: run v5 (project-analysis-documents-v5, coverage-3 con gapAssessments); E2 estratta come Gap;
    //        valutazione E2 = AnsweredInSnapshot/Answered con supporto E3 (30 giorni, Documented), nessun punto aperto per E2; conflitto Open E1 (45 giorni,
    //        Declared, messaggio PM) contro E3 (30 giorni, Documented, scheda A), senza risoluzione; E2 mai fra le fonti dell'analisi; nessun item o conflitto
    //        duplicato e nessuna domanda aperta che ripeta E2; citazioni esatte. Il retry informato e solo osservato: se non scatta resta NOT VERIFIED.
    [Fact]
    public async Task T142_N4_v5_gap_e2_answered_by_e3_and_open_conflict_e1_e3_with_provenance()
    {
        await Require();
        await RunNominalAsync(new Nominal(
            "T142-N4",
            ["txt-conflict-a.txt"],
            PmStatement: "Il cliente mi ha confermato per telefono che il termine di pagamento e' di 45 giorni.",
            Question: "Cosa dice la scheda A sul termine di pagamento?",
            ChatAnswerTokens: ["30"],
            ChatCitedQuotes: [["30 giorni"]],
            Expected:
            [
                new("documento: 30 giorni (Documented)", ["30"], ["30 giorni"]),
                new("PM: 45 giorni (Declared)", ["45"], ["45 giorni"], Class: "Declared"),
            ],
            Violations: c => c.Items.Where(i => i.Classification == "Declared" && i.Has("30") && !i.Has("45")).Select(i => "30 dichiarato dal PM: " + i.Short)
                .Concat(c.Items.Where(i => i.Classification == "Documented" && i.Has("45") && !i.Has("30")).Select(i => "45 attribuito al documento: " + i.Short)),
            Correction: null,
            DraftTokens: ["30", "45"],
            ExpectConflict: true,
            ConflictSides: [["30 giorni"], ["45 giorni"]],
            SplitPm: true,
            GapChecks: true));
    }

    // N5 — nuova versione ed esclusione: v1 della scheda A (30 giorni) confermata; v2 (90 giorni, salvo diverso accordo scritto) caricata e
    //      note-negazioni ESCLUSE dalle fonti. La revisione confermata resta intatta e leggibile; chat e nuova analisi usano solo v2.
    [Fact]
    public async Task N5_a_new_version_and_an_excluded_document_change_only_the_new_run_never_the_confirmed_one()
    {
        await Require();
        var v = new Verdicts("N5");
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var scheda = await UploadFixture(client, project, "txt-conflict-a.txt");
        var negations = await UploadFixture(client, project, "txt-negations.txt");
        if (!await WaitReadyAsync(client, project, [scheda, negations], v, "v1"))
        {
            v.Flush();
            return;
        }

        // --- Analisi sulla v1 (entrambi i documenti) e conferma.
        var first = await AnalyzeAsync(client, project, v, "analisi v1");
        if (first is null)
        {
            v.Flush();
            return;
        }

        var firstContent = new Content(first.Content, await MessageBodies(first.Content));
        v.Check("v1: la scheda A afferma 30 giorni (Documented)", firstContent.Items.Any(i => i.Has("30") && i.Classification == "Documented" && i.QuotesHave("30 giorni")));
        v.Check("v1: il documento delle note e rappresentato (45 giorni)", firstContent.Items.Any(i => i.Has("45") && i.QuotesHave("45 giorni")));
        var confirmed = await ConfirmAsync(client, project, first.Analysis, first.Revision, v, "v1");
        if (confirmed is null)
        {
            v.Flush();
            return;
        }

        var confirmedJson = (await Json(await client.GetAsync($"/api/projects/{project}/analyses/{first.Analysis}", Ct))).GetProperty("content").GetRawText();
        var oldSource = firstContent.Sources.Values.First(s => s.Kind == "DocumentPassage" && s.Quote!.Contains("30 giorni", StringComparison.Ordinal));

        // --- Nuova versione della scheda A (90 giorni) + esclusione del documento delle note.
        var v2Bytes = Encoding.UTF8.GetBytes("Scheda A\n\nIl termine di pagamento e' 90 giorni, salvo diverso accordo scritto.\n");
        var upload = await Upload(client, project, v2Bytes, "txt-conflict-a.txt", documentId: scheda.Document);
        v.Check("v2: upload della nuova versione 201", upload.StatusCode == HttpStatusCode.Created, ((int)upload.StatusCode).ToString(CultureInfo.InvariantCulture));
        var selectionResponse = await client.GetAsync($"/api/projects/{project}/source-selection", Ct);
        var revision = (await Json(selectionResponse)).GetProperty("revision").GetInt64();
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{project}/source-selection")
        {
            Content = JsonContent.Create(new
            {
                items = new object[]
                {
                    new { documentId = scheda.Document, included = true, mode = "LatestReady", versionId = (Guid?)null },
                    new { documentId = negations.Document, included = false, mode = "LatestReady", versionId = (Guid?)null },
                },
            }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        var selection = await client.SendAsync(put, Ct);
        v.Check("esclusione del documento delle note (PUT source-selection 200)", selection.StatusCode == HttpStatusCode.OK, ((int)selection.StatusCode).ToString(CultureInfo.InvariantCulture));
        var v2Ready = await PollAsync(
            () => Scalar<string>("SELECT p.\"Status\" FROM \"DocumentPreparations\" p JOIN \"DocumentVersions\" x ON x.\"Id\" = p.\"DocumentVersionId\" WHERE x.\"DocumentId\" = @d AND x.\"Version\" = 2", ("d", scheda.Document)),
            s => s is "Ready" or "Failed" or "Incomplete", DocumentsTimeout);
        v.Check("v2: preparazione Ready", v2Ready == "Ready", v2Ready);
        if (v2Ready != "Ready")
        {
            v.Flush();
            return;
        }

        // --- La revisione confermata resta identica byte per byte e il passaggio storico della v1 resta leggibile col testo originale.
        var after = (await Json(await client.GetAsync($"/api/projects/{project}/analyses/{first.Analysis}", Ct))).GetProperty("content").GetRawText();
        v.Check("la revisione confermata sulla v1 e identica byte per byte dopo v2 + esclusione", after == confirmedJson);
        var passage = await Json(await client.GetAsync(
            $"/api/projects/{project}/documents/{oldSource.DocumentId}/versions/1/preparations/{oldSource.PreparationId}/passages/{oldSource.BlockId}?start={oldSource.Start}&end={oldSource.End}", Ct));
        v.Check("il passaggio storico della v1 e leggibile con il testo originale (30 giorni)", passage.GetProperty("quote").GetProperty("text").GetString() == oldSource.Quote && oldSource.Quote!.Contains("30 giorni", StringComparison.Ordinal));

        // --- Chat sulla nuova selezione: cita solo la v2.
        var chat = await ChatAsync(client, project, "Qual e' il termine di pagamento e a quali condizioni puo cambiare?", v, "chat v2");
        if (chat is not null)
        {
            var evidences = await DocumentEvidences(client, project);
            var cited = ChatCitations(chat.Answer);
            v.Check("chat: la risposta cita almeno una fonte", cited.Count > 0);
            var citedSources = cited.Select(id => evidences.SelectMany(e => e.Sources).FirstOrDefault(s => s.SourceId == id)).ToList();
            v.Check("chat: ogni marcatore e una fonte documentale reale", citedSources.All(s => s is not null), string.Join(",", cited.Where((_, i) => citedSources[i] is null)));
            v.Check("chat: cita il passaggio della v2 (90 giorni)", citedSources.Any(s => s is not null && Fold(s.Quote).Contains("90 giorni", StringComparison.Ordinal)));
            v.Check("chat: non cita la v1 ne il documento escluso (30 giorni, consenso, carta)", citedSources.All(s => s is not null && !Fold(s.Quote).Contains("30 giorni", StringComparison.Ordinal) && !Fold(s.Quote).Contains("consenso", StringComparison.Ordinal) && !Fold(s.Quote).Contains("carta", StringComparison.Ordinal)));
            v.Check("chat: la risposta riporta 90 e non 30 come termine corrente", Fold(chat.Answer).Contains("90", StringComparison.Ordinal) && !Regex.IsMatch(Fold(chat.Answer), @"\b30\b"), Short(chat.Answer));
            v.Check("chat: la risposta riporta la condizione «diverso accordo»", Fold(chat.Answer).Contains("accordo", StringComparison.Ordinal), Short(chat.Answer));
            await AssertExecution(client, project, "chat", chat.Turn, v, "chat v2", expectOrdinary: true);
        }

        // --- Nuova analisi: solo v2; nessuna voce dal documento escluso; la v1 non compare come termine corrente.
        var second = await AnalyzeAsync(client, project, v, "analisi v2");
        if (second is not null)
        {
            var content = new Content(second.Content, await MessageBodies(second.Content));
            await VerifyCitationsAsync(client, project, content, v, "analisi v2");
            v.Check("analisi v2: 90 giorni con la condizione «diverso accordo scritto»", content.Items.Any(i => i.Has("90") && i.Has("accordo") && i.QuotesHave("90 giorni") && i.Classification == "Documented"));
            v.Check("analisi v2: nessuna voce dal documento ESCLUSO (consenso, carta, resi, esportazion, 45 giorni)", !content.Items.Any(i => i.Has("consenso") || i.Has("carta") || i.Has("resi") || i.Has("esportazion") || i.QuotesHave("45 giorni")));
            v.Check("analisi v2: nessuna fonte della v1 ne del documento escluso", content.Sources.Values.Where(s => s.Kind == "DocumentPassage").All(s => s.DocumentId == scheda.Document && !s.Quote!.Contains("30 giorni", StringComparison.Ordinal)));
            v.Check("analisi v2: 30 giorni non e un requisito corrente", !content.Items.Any(i => i.Has("30 giorni")));
            v.Check("analisi v2: e una nuova analisi (la confermata resta su v1)", second.Analysis != first.Analysis && second.Revision != first.Revision);
            var secondConfirmed = await ConfirmAsync(client, project, second.Analysis, second.Revision, v, "v2");
            if (secondConfirmed is not null)
            {
                await GenerateAsync(client, project, second.Analysis, second.Revision, v, "v2", secondConfirmed.Value, ["90", "accordo"]);
            }
        }

        await TelemetryAsync(project, v);
        v.Flush();
    }

    // ================================================================ percorso comune N1..N4

    private sealed record Exp(string Name, string[] Text, string[] Quote, bool Negative = false, string Class = "Documented");

    private sealed record Correction(string Name, Func<JsonNode, string?> Apply);

    private sealed record Nominal(
        string Id, string[] Files, string? PmStatement, string Question, string[] ChatAnswerTokens, string[][] ChatCitedQuotes, Exp[] Expected,
        Func<Content, IEnumerable<string>> Violations, Correction? Correction, string[] DraftTokens, bool ExpectConflict, string[][]? ConflictSides = null, bool SplitPm = false, bool GapChecks = false);

    private async Task RunNominalAsync(Nominal n)
    {
        var v = new Verdicts(n.Id);
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var docs = new List<Doc>();
        foreach (var file in n.Files)
        {
            docs.Add(await UploadFixture(client, project, file));
        }

        // --- 1. Upload -> Ready (parser reale + estrazione reale): nessuna analisi, chat o generazione partono da sole.
        if (!await WaitReadyAsync(client, project, docs, v, "preparazione"))
        {
            v.Flush();
            return;
        }

        v.Check("nessun upload avvia analisi o chat", await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p", ("p", project)) == 0 && await Count("SELECT count(*) FROM \"ChatTurns\"") == 0);
        var evidences = await DocumentEvidences(client, project);
        v.Note($"evidenze documentali: {evidences.Count} ({string.Join(", ", docs.Select(d => $"{d.Name}={evidences.Count(e => e.Sources.Any(s => s.DocumentId == d.Document))}"))})");
        v.Check("ogni documento ha almeno un'evidenza", docs.All(d => evidences.Any(e => e.Sources.Any(s => s.DocumentId == d.Document))));

        // --- 2. Chat con fonti: ogni marcatore e una fonte documentale reale, con citazione esatta, e le attese annotate sono nella risposta.
        // T142: E1 (dichiarazione del PM) ed E2 (domanda) sono due messaggi distinti, con testo identico a N4: E2 deve poter essere una evidenza Gap propria.
        if (n.SplitPm && n.PmStatement is not null)
        {
            var statement = await ChatAsync(client, project, n.PmStatement, v, "chat E1 (dichiarazione PM)");
            if (statement is null)
            {
                await TelemetryAsync(project, v);
                v.Flush();
                return;
            }
        }

        var chat = await ChatAsync(client, project, n.SplitPm ? n.Question : (n.PmStatement is null ? "" : n.PmStatement + " ") + n.Question, v, n.SplitPm ? "chat E2 (domanda PM)" : "chat");
        if (chat is null)
        {
            await TelemetryAsync(project, v);
            v.Flush();
            return;
        }

        var cited = ChatCitations(chat.Answer);
        var citedSources = cited.Select(id => evidences.SelectMany(e => e.Sources).FirstOrDefault(s => s.SourceId == id)).ToList();
        v.Note("risposta chat: " + Short(chat.Answer, 600));
        v.Check("chat: la risposta cita almeno una fonte", cited.Count > 0);
        v.Check("chat: ogni marcatore [fonte:...] e una fonte documentale reale del progetto", citedSources.All(s => s is not null), string.Join(",", cited.Where((_, i) => citedSources[i] is null)));
        foreach (var source in citedSources.Where(s => s is not null))
        {
            var text = await PassageText(client, project, source!);
            v.Check($"chat: la citazione «{Short(source!.Quote, 40)}» e esatta nel blocco", text == source.Quote);
        }

        foreach (var token in n.ChatAnswerTokens)
        {
            v.Check($"chat: la risposta contiene «{token}»", Fold(chat.Answer).Contains(token, StringComparison.Ordinal), Short(chat.Answer));
        }

        foreach (var tokens in n.ChatCitedQuotes)
        {
            v.Check($"chat: cita un passaggio con «{string.Join(" ", tokens)}»", citedSources.Any(s => s is not null && tokens.All(t => Fold(s.Quote).Contains(t, StringComparison.Ordinal))));
        }

        await AssertExecution(client, project, "chat", chat.Turn, v, "chat", expectOrdinary: true);

        // --- 3. Analizza: revisione 1.1.0 con tutte le evidenze rappresentate, classificazione e citazioni corrette.
        var analysis = await AnalyzeAsync(client, project, v, "analisi");
        if (analysis is null)
        {
            await TelemetryAsync(project, v);
            v.Flush();
            return;
        }

        var content = new Content(analysis.Content, await MessageBodies(analysis.Content));
        v.Note($"analisi: {content.Items.Count} voci, {content.Sources.Count} fonti, {content.Conflicts.Count} conflitti");
        foreach (var item in content.Items)
        {
            v.Note($"  [{item.Classification}{(item.InConflict ? "/conflitto" : "")}] {Short(item.Text, 150)} <= {Short(string.Join(" | ", item.Quotes), 110)}");
        }

        await VerifyCitationsAsync(client, project, content, v, "analisi");
        var referenced = content.Items.SelectMany(i => i.Refs).ToHashSet();
        v.Check("analisi: ogni evidenza documentale e rappresentata (tutte le sue fonti sono riferite da una voce)", evidences.All(e => e.Sources.All(s => referenced.Contains(s.SourceId))),
            string.Join(",", evidences.Where(e => e.Sources.Any(s => !referenced.Contains(s.SourceId))).Select(e => Short(e.Statement, 50))));
        foreach (var expected in n.Expected)
        {
            v.Check($"attesa annotata: {expected.Name}", content.Items.Any(i => Matches(i, expected)));
        }

        foreach (var violation in n.Violations(content))
        {
            v.Check("nessuna inversione/errore: " + violation, false);
        }

        v.Check("nessuna voce non Documented/Declared senza giustificazione (Inferred/Assumed solo per deduzioni)", content.Items.All(i => i.Classification is "Documented" or "Declared" or "Inferred" or "Assumed"));
        if (n.ExpectConflict)
        {
            v.Check("conflitto presente (nessuna scelta automatica fra le posizioni)", content.Conflicts.Count > 0, $"conflitti={content.Conflicts.Count}");
            foreach (var sides in n.ConflictSides ?? [])
            {
                v.Check($"conflitto: una alternativa cita «{string.Join(" ", sides)}»", content.Conflicts.Any(c => c.Alternatives.Any(a => sides.All(t => a.QuotesHave(t)))));
            }

            v.Check("conflitto: stato Open, almeno due alternative, resolution null",
                content.Conflicts.Any(c => c.Status == "Open" && c.Alternatives.Count >= 2 && !c.HasResolution));
        }
        else
        {
            v.Check("nessun conflitto spurio", content.Conflicts.Count == 0, $"conflitti={content.Conflicts.Count}");
        }

        await AssertExecution(client, project, "analysis", analysis.Analysis, v, "analisi", expectOrdinary: false);
        if (n.GapChecks)
        {
            await VerifyT142Async(client, project, analysis, content, evidences, v);
        }

        // --- 4. Correzione PM (nuova revisione 1.1) e conferma.
        var revision = analysis.Revision;
        if (n.Correction is not null)
        {
            var edited = analysis.Content.DeepClone();
            var applied = n.Correction.Apply(edited);
            v.Check("correzione PM applicabile alla revisione", applied is not null, n.Correction.Name);
            if (applied is null)
            {
                v.Flush();
                return;
            }

            var put = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{project}/analyses/{analysis.Analysis}/preview") { Content = JsonContent.Create(edited) };
            put.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
            var response = await client.SendAsync(put, Ct);
            var body = await Json(response);
            v.Check("correzione PM accettata (PUT preview 200, nuova revisione PmEdit 1.1.0)", response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {Short(body.GetRawText(), 300)}");
            if (response.StatusCode != HttpStatusCode.OK)
            {
                v.Flush();
                return;
            }

            var newRevision = body.GetProperty("currentRevisionId").GetGuid();
            v.Check("nuova revisione PmEdit con schema 1.1.0, la revisione AI resta intatta",
                newRevision != revision && body.GetProperty("origin").GetString() == "PmEdit" && body.GetProperty("schemaVersion").GetString() == "1.1.0"
                && await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"Id\" = @r AND \"Origin\" = 'Ai'", ("r", revision)) == 1);
            var editedContent = new Content(JsonNode.Parse(body.GetProperty("content").GetRawText())!, await MessageBodies(analysis.Content));
            // La fonte PmRevision e aggiunta dal server dove la correzione agisce: in una voce/alternativa modificata (N1) oppure, se il PM risolve un
            // conflitto (N3), in conflicts[].resolution.evidenceRefs. Non e un allentamento: una PmRevision resta obbligatoria, ma e cercata anche dove il
            // server attribuisce la risoluzione.
            v.Check("attribuzione: la correzione cita una fonte PmRevision aggiunta dal server (voce/alternativa o risoluzione del conflitto)",
                editedContent.Items.Any(i => i.Refs.Any(r => editedContent.Sources.TryGetValue(r, out var s) && s.Kind == "PmRevision"))
                || editedContent.Conflicts.Any(c => c.ResolutionRefs.Any(r => editedContent.Sources.TryGetValue(r, out var s) && s.Kind == "PmRevision")));
            v.Check("le fonti documentali restano identiche dopo la correzione", JsonSerializer.Serialize(editedContent.Sources.Values.Where(s => s.Kind == "DocumentPassage").Select(s => s.Raw.ToJsonString()).Order(StringComparer.Ordinal))
                == JsonSerializer.Serialize(content.Sources.Values.Where(s => s.Kind == "DocumentPassage").Select(s => s.Raw.ToJsonString()).Order(StringComparer.Ordinal)));
            revision = newRevision;
        }

        var confirmedView = await ConfirmAsync(client, project, analysis.Analysis, revision, v, "conferma");
        if (confirmedView is null)
        {
            await TelemetryAsync(project, v);
            v.Flush();
            return;
        }

        v.Check("la conferma non avvia la generazione", await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p AND \"Kind\" = 'Document'", ("p", project)) == 0);

        // --- 5. Genera: bozza proposta (mai approvata), citazioni nella revisione confermata, attese annotate.
        await GenerateAsync(client, project, analysis.Analysis, revision, v, "bozza", confirmedView.Value, n.DraftTokens);
        await TelemetryAsync(project, v);
        v.Flush();
    }

    // ================================================================ passi comuni

    private sealed record Doc(Guid Document, string Name, byte[] Bytes);

    private sealed record Chat(Guid Turn, string Answer);

    private sealed record AnalysisOutcome(Guid Analysis, Guid Revision, JsonNode Content);

    private async Task<Doc> UploadFixture(HttpClient client, Guid project, string name)
    {
        var bytes = RealParser.Fixture(name);
        var response = await Upload(client, project, bytes, name, DocumentUploadValidation.MediaTypeOf(RealParser.FormatOf(name)), displayName: name);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{name}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return new Doc((await Json(response)).GetProperty("documentId").GetGuid(), name, bytes);
    }

    private async Task<string> Poll(Func<Task<string>> probe, Func<string, bool> terminal, TimeSpan timeout)
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

            await Task.Delay(1_500, Ct);
        }

        return last + " (TIMEOUT)";
    }

    private Task<string> PollAsync(Func<Task<string>> probe, Func<string, bool> terminal, TimeSpan timeout) => Poll(probe, terminal, timeout);

    private async Task<bool> WaitReadyAsync(HttpClient client, Guid project, IReadOnlyList<Doc> docs, Verdicts v, string label)
    {
        var all = true;
        foreach (var doc in docs)
        {
            var status = await Poll(async () => (await Json(await client.GetAsync($"/api/projects/{project}/documents/{doc.Document}", Ct))).GetProperty("versions")[0].GetProperty("processingStatus").GetString()!,
                s => s is "Ready" or "Failed" or "Incomplete" or "Rejected", DocumentsTimeout);
            var detail = status;
            if (status != "Ready")
            {
                var prep = await Json(await client.GetAsync($"/api/projects/{project}/documents/{doc.Document}/versions/1/preparations/latest", Ct));
                detail = $"{status}: {Short(prep.GetRawText(), 500)}";
            }

            v.Check($"{label}: {doc.Name} Ready (parser reale + estrazione reale)", status == "Ready", detail);
            all &= status == "Ready";
        }

        return all;
    }

    private async Task<Chat?> ChatAsync(HttpClient client, Guid project, string text, Verdicts v, string label)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        v.Check($"{label}: POST messaggio 201", response.StatusCode == HttpStatusCode.Created, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        if (response.StatusCode != HttpStatusCode.Created)
        {
            return null;
        }

        var turn = (await Json(response)).GetProperty("chatTurnId").GetGuid();
        var status = await Poll(() => Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)), s => s is not ("Queued" or "Running"), ChatTimeout);
        if (status != "Completed")
        {
            v.Check($"{label}: turno Completed", false, $"{status} {await Failure("ChatTurns", turn)}");
            return null;
        }

        v.Check($"{label}: turno Completed", true);
        var answer = await Scalar<string>("SELECT \"Body\" FROM \"ConversationMessages\" WHERE \"ChatTurnId\" = @t AND \"Role\" = 'Assistant'", ("t", turn));
        return new Chat(turn, answer);
    }

    private async Task<AnalysisOutcome?> AnalyzeAsync(HttpClient client, Guid project, Verdicts v, string label)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        var body = await Json(response);
        v.Check($"{label}: POST Analizza 202", response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode} {Short(body.GetRawText(), 300)}");
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            return null;
        }

        var run = body.GetProperty("id").GetGuid();
        var status = await Poll(() => Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)), s => s is not ("Queued" or "Running"), RunTimeout);
        if (status != "Completed")
        {
            v.Check($"{label}: run Completed", false, $"{status} {await Failure("ProcessingRuns", run)}");
            return null;
        }

        var view = await Json(await client.GetAsync($"/api/projects/{project}/analyses/{run}", Ct));
        // Dal T131 (157c1fe) le nuove richieste di Analizza congelano la definizione v4 (alias con riconciliazione gerarchica; DocumentSmallFlowTests
        // asserisce già V4 sullo stesso percorso): l'attesa V3 era un falso negativo del test, non un criterio di accettazione.
        v.Check($"{label}: revisione 1.1.0 (workflow documentale v4 con alias)", view.GetProperty("schemaVersion").GetString() == "1.1.0"
            && await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)) == DocumentAnalysisWorkflow.DefinitionVersionV5);
        return new AnalysisOutcome(run, view.GetProperty("currentRevisionId").GetGuid(), JsonNode.Parse(view.GetProperty("content").GetRawText())!);
    }

    /// <summary>Verifiche specifiche T142 sulla revisione AI v5: workflow, Gap E2, valutazioni (coverage-3), conflitto Open E1/E3, nessun duplicato, provenienze.</summary>
    private async Task VerifyT142Async(HttpClient client, Guid project, AnalysisOutcome analysis, Content content, List<Ev> documentEvidences, Verdicts v)
    {
        var l = "T142";
        var definition = await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", analysis.Analysis));
        v.Check($"{l}: workflow project-analysis-documents-v5 (non un run storico v4)", definition == DocumentAnalysisWorkflow.DefinitionVersionV5 && definition == "project-analysis-documents-v5", definition);

        // E2: evidenza Gap distinta (messaggio PM), E3: evidenza documentale con 30 giorni.
        var gapPage = await Json(await client.GetAsync($"/api/projects/{project}/evidence?kind=Gap&viewVersion=1.1.0&limit=100", Ct));
        var gaps = gapPage.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("evidence")).Select(e => (Id: e.GetProperty("id").GetGuid(), Statement: e.GetProperty("statement").GetString() ?? "")).ToList();
        v.Note($"{l}: evidenze Gap nel progetto = {gaps.Count}: " + string.Join(" | ", gaps.Select(g => Short(g.Statement, 90))));
        var e2 = gaps.Where(g => Fold(g.Statement).Contains("scheda a", StringComparison.Ordinal) && Fold(g.Statement).Contains("termine", StringComparison.Ordinal)).ToList();
        v.Check($"{l}: E2 (domanda del PM sul termine della scheda A) estratta come evidenza Gap, una sola", e2.Count == 1, $"{e2.Count}");
        var e3 = documentEvidences.Where(e => e.Sources.Any(s => Fold(s.Quote).Contains("30 giorni", StringComparison.Ordinal))).ToList();
        v.Check($"{l}: E3 (scheda A, 30 giorni) e una evidenza documentale", e3.Count >= 1, $"{e3.Count}");

        var coverage = await client.PostAsJsonAsync($"/api/projects/{project}/analyses/{analysis.Analysis}/coverage-reports", new { revisionId = analysis.Revision }, Ct);
        var report = await Json(coverage);
        v.Check($"{l}: coverage-3 creato", coverage.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created && report.TryGetProperty("checkerVersion", out var cv) && cv.GetString() == "coverage-3", $"{(int)coverage.StatusCode} {Short(report.GetRawText(), 300)}");
        if (!report.TryGetProperty("gapAssessments", out var assessments) || assessments.ValueKind != JsonValueKind.Array)
        {
            v.Check($"{l}: gapAssessments presente", false);
            return;
        }

        var records = assessments.EnumerateArray().ToList();
        var summary = report.GetProperty("summary");
        v.Note($"{l}: gapAssessments={records.Count} hash={Short(report.GetProperty("gapAssessmentsHash").GetString() ?? "-", 16)} expected={summary.GetProperty("expectedGaps").GetInt32()} assessed={summary.GetProperty("assessedGaps").GetInt32()} answered={summary.GetProperty("answeredGaps").GetInt32()} open={summary.GetProperty("openGaps").GetInt32()} unassessed={summary.GetProperty("unassessedGaps").GetInt32()}");
        v.Check($"{l}: gapAssessments presente, non vuoto, con hash", records.Count > 0 && (report.GetProperty("gapAssessmentsHash").GetString() ?? "").Length > 0);
        v.Check($"{l}: nessun Gap non valutato e un solo record per Gap", summary.GetProperty("unassessedGaps").GetInt32() == 0
            && records.Select(r => r.GetProperty("gapEvidenceId").GetGuid()).Distinct().Count() == records.Count
            && gaps.All(g => records.Any(r => r.GetProperty("gapEvidenceId").GetGuid() == g.Id)));
        if (e2.Count == 1)
        {
            var record = records.FirstOrDefault(r => r.GetProperty("gapEvidenceId").GetGuid() == e2[0].Id);
            v.Check($"{l}: E2 AnsweredInSnapshot/Answered", record.ValueKind == JsonValueKind.Object && record.GetProperty("outcome").GetString() == "AnsweredInSnapshot" && record.GetProperty("reason").GetString() == "Answered",
                record.ValueKind == JsonValueKind.Object ? Short(record.GetRawText(), 400) : "nessun record per E2");
            if (record.ValueKind == JsonValueKind.Object)
            {
                var support = record.GetProperty("supportingEvidenceIds").EnumerateArray().Select(x => x.GetGuid()).ToList();
                v.Check($"{l}: il supporto di E2 e E3 (scheda A, 30 giorni) e solo documentale", support.Count > 0 && support.All(id => e3.Any(e => e.Id == id)), $"supporto={support.Count}");
                v.Check($"{l}: nessun punto o conflitto aperto collegato a E2", record.GetProperty("openItemId").ValueKind == JsonValueKind.Null && record.GetProperty("openConflictId").ValueKind == JsonValueKind.Null);
                v.Check($"{l}: motivazione e snapshot hash presenti", (record.GetProperty("rationale").GetString() ?? "").Trim().Length > 0 && (record.GetProperty("sourceSnapshotHash").GetString() ?? "").Length > 0);
                foreach (var id in support)
                {
                    foreach (var source in documentEvidences.Where(e => e.Id == id).SelectMany(e => e.Sources))
                    {
                        v.Check($"{l}: supporto E3, citazione «{Short(source.Quote, 40)}» esatta nel blocco", await PassageText(client, project, source) == source.Quote);
                    }
                }
            }
        }

        // Conflitto Open E1 (45, Declared, messaggio PM) / E3 (30, Documented, scheda A): un solo conflitto, nessuna scelta, nessun duplicato.
        var related = content.Conflicts.Where(c => c.Alternatives.Any(a => a.Has("30")) && c.Alternatives.Any(a => a.Has("45"))).ToList();
        v.Check($"{l}: esattamente un conflitto 30/45 (nessun duplicato) e nessun altro conflitto", related.Count == 1 && content.Conflicts.Count == 1, $"conflitti={content.Conflicts.Count}, 30/45={related.Count}");
        v.Check($"{l}: nessun conflitto Resolved e nessuna scelta automatica", content.Conflicts.All(c => c.Status == "Open" && !c.HasResolution));
        if (related.Count == 1)
        {
            var sides = related[0].Alternatives;
            v.Check($"{l}: alternativa PM = 45 giorni Declared con fonte Message e citazione del messaggio", sides.Any(a => a.Classification == "Declared" && a.Has("45") && !a.Has("30")
                && a.Refs.Any(r => content.Sources[r].Kind == "Message") && a.QuotesHave("45 giorni")));
            v.Check($"{l}: alternativa documento = 30 giorni Documented con passaggio della scheda A", sides.Any(a => a.Classification == "Documented" && a.Has("30") && !a.Has("45")
                && a.Refs.Any(r => content.Sources[r].Kind == "DocumentPassage") && a.QuotesHave("30 giorni")));
            v.Check($"{l}: nessuna terza alternativa (nessuna sintesi o media)", sides.Count == 2, $"{sides.Count}");
        }

        // Nessun duplicato di item: stesso testo, un solo 30 Documented e un solo 45 Declared, nessuna domanda aperta che ripeta E2.
        var texts = content.Items.Select(i => Fold(i.Text)).ToList();
        v.Check($"{l}: nessun item duplicato (testo identico)", texts.Distinct().Count() == texts.Count, string.Join(" | ", texts.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => Short(g.Key, 60))));
        v.Check($"{l}: un solo item 30 giorni Documented e un solo 45 giorni Declared", content.Items.Count(i => i.Classification == "Documented" && i.Has("30 giorni")) == 1
            && content.Items.Count(i => i.Classification == "Declared" && i.Has("45 giorni")) == 1);
        v.Check($"{l}: nessuna domanda aperta che ripete E2 (gia riscontrata)", !content.Items.Any(i => i.Section == "openQuestions" && Fold(i.Text).Contains("scheda a", StringComparison.Ordinal) && Fold(i.Text).Contains("termine", StringComparison.Ordinal)));
        var messageBodies = new List<string>();
        foreach (var source in content.Sources.Values.Where(s => s.Kind == "Message"))
        {
            messageBodies.Add(await Scalar<string>("SELECT \"Body\" FROM \"ConversationMessages\" WHERE \"Id\" = @i", ("i", Guid.Parse(source.Raw["messageId"]!.GetValue<string>()))));
        }

        v.Check($"{l}: E2 non compare fra le fonti dell'analisi", !messageBodies.Any(b => Fold(b).Contains("cosa dice la scheda", StringComparison.Ordinal)));

        // Retry informato: solo osservato. Un run valido senza retry non e un errore nominale, ma il requisito resta NOT VERIFIED.
        var attempts = await Count("SELECT count(*) FROM \"InferenceCalls\" WHERE \"ProjectId\" = @p AND \"Phase\" = 'Analysis'", ("p", project));
        var retried = await Count("SELECT count(*) FROM \"InferenceCalls\" WHERE \"ProjectId\" = @p AND \"Phase\" = 'Analysis' AND \"Attempt\" > 1", ("p", project));
        v.Note($"{l}: chiamate di analisi={attempts}, con tentativo>1={retried} => retry informato v5 {(retried > 0 ? "ESERCITATO (qualita da leggere nel log)" : "NON esercitato: NOT VERIFIED")}");
    }

    private async Task<JsonElement?> ConfirmAsync(HttpClient client, Guid project, Guid analysis, Guid revision, Verdicts v, string label)
    {
        var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/confirm");
        confirm.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        var response = await client.SendAsync(confirm, Ct);
        var body = await Json(response);
        v.Check($"{label}: conferma PM 200 con validationState PmConfirmed e canGenerate", response.StatusCode == HttpStatusCode.OK
            && body.GetProperty("validationState").GetString() == "PmConfirmed" && body.GetProperty("canGenerate").GetBoolean(), $"{(int)response.StatusCode} {Short(body.GetRawText(), 400)}");
        return response.StatusCode == HttpStatusCode.OK ? body : null;
    }

    private async Task GenerateAsync(HttpClient client, Guid project, Guid analysis, Guid revision, Verdicts v, string label, JsonElement confirmedView, string[] draftTokens)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/document-generations") { Content = JsonContent.Create(new { revisionId = revision }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        var body = await Json(response);
        v.Check($"{label}: POST Genera 202", response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode} {Short(body.GetRawText(), 300)}");
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            return;
        }

        var run = body.GetProperty("id").GetGuid();
        var status = await Poll(() => Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)), s => s is not ("Queued" or "Running"), RunTimeout);
        if (status != "Completed")
        {
            v.Check($"{label}: generazione Completed", false, $"{status} {await Failure("ProcessingRuns", run)}");
            return;
        }

        v.Check($"{label}: generazione Completed", true);
        var draft = await Json(await client.GetAsync($"/api/projects/{project}/draft", Ct));
        var markdown = draft.GetProperty("markdown").GetString()!;
        v.Note("bozza: " + Short(markdown.Replace('\n', ' '), 900));
        v.Check($"{label}: bozza proposta e mai approvata, legata alla revisione confermata",
            draft.GetProperty("status").GetString() == "Proposed" && draft.GetProperty("approvalStatus").GetString() == "NotApproved" && draft.GetProperty("analysisRevisionId").GetGuid() == revision);
        var confirmedSources = confirmedView.GetProperty("content").GetProperty("sources").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid(), x => x.GetProperty("kind").GetString());
        var cited = Regex.Matches(markdown, @"\[fonte:([0-9a-f-]{36})\]").Select(m => Guid.Parse(m.Groups[1].Value)).Distinct().ToList();
        v.Check($"{label}: ogni marcatore della bozza e una fonte della revisione confermata (100% citazioni corrette)", cited.Count > 0 && cited.All(confirmedSources.ContainsKey), $"marcatori={cited.Count}");
        v.Check($"{label}: la bozza cita almeno un passaggio documentale storico", cited.Any(id => confirmedSources.TryGetValue(id, out var kind) && kind == "DocumentPassage"));
        foreach (var token in draftTokens)
        {
            v.Check($"{label}: la bozza contiene «{token}»", Fold(markdown).Contains(token, StringComparison.Ordinal));
        }

        await AssertExecution(client, project, "document", run, v, label, expectOrdinary: false);
    }

    // ================================================================ verifiche

    private async Task VerifyCitationsAsync(HttpClient client, Guid project, Content content, Verdicts v, string label)
    {
        var evidences = await DocumentEvidences(client, project);
        var bySource = evidences.SelectMany(e => e.Sources).GroupBy(s => s.SourceId).ToDictionary(g => g.Key, g => g.First());
        var wrong = new List<string>();
        var checkedCount = 0;
        foreach (var source in content.Sources.Values.Where(s => s.Kind == "DocumentPassage"))
        {
            checkedCount++;
            if (!bySource.TryGetValue(source.Id, out var known)
                || known.Quote != source.Quote || known.BlockId != source.BlockId || known.Start != source.Start || known.End != source.End || known.DocumentId != source.DocumentId
                || await PassageText(client, project, known) != source.Quote)
            {
                wrong.Add(Short(source.Quote ?? "", 40));
            }
        }

        v.Check($"{label}: 100% delle citazioni documentali corrette ({checkedCount} fonti: ID reale dell'evidenza, blocco/range e citazione esatta nel passaggio)", checkedCount > 0 && wrong.Count == 0, string.Join(" | ", wrong));
        v.Check($"{label}: ogni evidenceRef di ogni voce e una fonte dichiarata", content.Items.All(i => i.Refs.All(content.Sources.ContainsKey)));
        v.Check($"{label}: ogni voce ha almeno una fonte", content.Items.All(i => i.Refs.Count > 0));
        v.Check($"{label}: Declared solo con messaggio PM, Documented solo con passaggio di documento", content.Items.All(i =>
            (i.Classification != "Declared" || i.Refs.Any(r => content.Sources[r].Kind == "Message"))
            && (i.Classification != "Documented" || i.Refs.Any(r => content.Sources[r].Kind == "DocumentPassage"))));
    }

    /// <summary>Il root e Classified con record e ricevuta (hash) e, dove atteso, Ordinary; le durate sono misure, il regime Extended non e un errore.</summary>
    private async Task AssertExecution(HttpClient client, Guid project, string kind, Guid root, Verdicts v, string label, bool expectOrdinary)
    {
        var path = kind == "chat" ? $"/api/projects/{project}/chat-turns/{root}" : $"/api/projects/{project}/processing-runs/{root}";
        var view = await Json(await client.GetAsync(path, Ct));
        if (!view.TryGetProperty("execution", out var execution) || execution.ValueKind != JsonValueKind.Object)
        {
            v.Check($"{label}: telemetria execution presente", false, Short(view.GetRawText(), 300));
            return;
        }

        string? Str(string name) => execution.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        long? Num(string name) => execution.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : null;
        var hash = Str("classificationRecordHash");
        v.Note($"{label}: classificazione reale = {Str("classificationStatus")}/{Str("initialRegime")} motivo={Str("reason") ?? "-"} ricevuta={(hash is null ? "-" : hash[..12])}… " +
            $"coda={Num("queueMs")}ms preflight={Num("preflightMs")}ms calcolo={Num("processingMs")}ms trascorso={Num("elapsedMs")}ms");
        v.Check($"{label}: root Classified con regime e ricevuta (hash SHA-256 del ClassificationRecord)", Str("classificationStatus") == "Classified" && Str("initialRegime") is not null && hash is not null && Regex.IsMatch(hash, "^[0-9a-f]{64}$"));
        v.Check($"{label}: un solo ClassificationRecord, osservato prima dell'avvio", await Count("SELECT count(*) FROM \"ClassificationRecords\" WHERE \"OwnerId\" = @i", ("i", root)) == 1);
        if (expectOrdinary)
        {
            v.Check($"{label}: payload piccolo e misurato => Ordinary entro i 180 s dal t0", Str("initialRegime") == "Ordinary" && Num("elapsedMs") is <= 180_000, $"{Str("initialRegime")} elapsed={Num("elapsedMs")}");
        }
    }

    private async Task TelemetryAsync(Guid project, Verdicts v)
    {
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand(
            """
            SELECT "Phase", "Attempt", "DurationMs", "PromptTokens", "CompletionTokens", "FinishReason", "State"
            FROM "InferenceCalls" WHERE "ProjectId" = @p ORDER BY "StartedAt"
            """, connection);
        command.Parameters.AddWithValue("p", project);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        var truncated = 0;
        while (await reader.ReadAsync(Ct))
        {
            string Col(int i) => reader.IsDBNull(i) ? "n/d" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
            lines.Add($"fase={Col(0)} tentativo={Col(1)} ms={Col(2)} prompt={Col(3)} completion={Col(4)} finish={Col(5)} stato={Col(6)}");
            truncated += Col(5) == "length" ? 1 : 0;
        }

        v.Note($"chiamate al modello: {lines.Count}, finish=length: {truncated}");
        foreach (var line in lines)
        {
            v.Note("  " + line);
        }

        v.Check("nessuna chiamata troncata (finish=length)", truncated == 0, $"{truncated} troncate");
    }

    private static bool Matches(Item item, Exp e) =>
        item.Classification == e.Class && e.Text.All(item.Has) && e.Quote.All(item.QuotesHave) && (!e.Negative || item.NegativeText || item.Section == "excluded");

    private static string? AppendToItem(JsonNode root, string[] tokens, string suffix)
    {
        foreach (var item in AllItems(root))
        {
            var text = item["text"]!.GetValue<string>();
            if (tokens.All(t => Fold(text).Contains(t, StringComparison.Ordinal)) && item["classification"]!.GetValue<string>() == "Documented")
            {
                item["text"] = text + suffix;
                return item["id"]!.GetValue<string>();
            }
        }

        return null;
    }

    private static string? ResolveConflictInFavourOf60(JsonNode root)
    {
        foreach (var conflict in root["conflicts"]!.AsArray().Where(c => c is not null))
        {
            var alternatives = conflict!["alternatives"]!.AsArray();
            var sources = root["sources"]!.AsArray().ToDictionary(s => s!["id"]!.GetValue<string>(), s => s!["quote"]?.GetValue<string>() ?? "");
            var chosen = alternatives.FirstOrDefault(a => a!["evidenceRefs"]!.AsArray().Any(r => sources.TryGetValue(r!.GetValue<string>(), out var q) && q.Contains("60 giorni", StringComparison.Ordinal)));
            if (chosen is null)
            {
                continue;
            }

            conflict["status"] = "Resolved";
            conflict["resolution"] = new JsonObject
            {
                ["selectedAlternativeIds"] = new JsonArray(JsonValue.Create(chosen["id"]!.GetValue<string>())),
                ["reason"] = "Il cliente ha confermato che vale la scheda B (60 giorni).",
                ["evidenceRefs"] = new JsonArray(),
            };
            return conflict["id"]!.GetValue<string>();
        }

        return null;
    }

    private static IEnumerable<JsonNode> AllItems(JsonNode root)
    {
        foreach (var name in new[] { "goals", "requirements", "operationalFlows", "constraints", "dependencies", "assumptions", "openQuestions" })
        {
            foreach (var item in root[name]!.AsArray())
            {
                yield return item!;
            }
        }

        foreach (var name in new[] { "overview", "included", "excluded" })
        {
            foreach (var item in root["contextAndScope"]![name]!.AsArray())
            {
                yield return item!;
            }
        }

        foreach (var conflict in root["conflicts"]!.AsArray())
        {
            foreach (var item in conflict!["alternatives"]!.AsArray())
            {
                yield return item!;
            }
        }
    }

    // ================================================================ dati


    private sealed record Source(Guid Id, string Kind, string? Quote, Guid? DocumentId, Guid? BlockId, int? Start, int? End, JsonNode Raw)
    {
        public Guid? PreparationId => Guid.TryParse(Raw["preparationId"]?.GetValue<string>(), out var p) ? p : null;
    }

    private sealed class Item(string section, bool inConflict, string id, string text, string classification, IReadOnlyList<Guid> refs, IReadOnlyDictionary<Guid, Source> sources, IReadOnlyDictionary<Guid, string> messages)
    {
        public string Section { get; } = section;
        public bool InConflict { get; } = inConflict;
        public string Id { get; } = id;
        public string Text { get; } = text;
        public string Classification { get; } = classification;
        public IReadOnlyList<Guid> Refs { get; } = refs;

        /// <summary>Citazioni delle fonti dell'item: passaggio del documento oppure testo del messaggio PM.</summary>
        public IEnumerable<string> Quotes => Refs.Select(r => sources.TryGetValue(r, out var s) ? s.Kind == "Message" ? messages.GetValueOrDefault(Guid.TryParse(s.Raw["messageId"]?.GetValue<string>(), out var m) ? m : Guid.Empty, "") : s.Quote ?? "" : "");

        public string Short => DocumentSmallFlowMafRealTests.Short(Text, 120);

        public bool Has(string token) => Fold(Text).Contains(token, StringComparison.Ordinal);

        public bool QuotesHave(string token) => Quotes.Any(q => Fold(q).Contains(token, StringComparison.Ordinal));

        public bool NegativeText => AnalysisVerificationRules.IsNegative(Text);
    }

    private sealed class Conflict(string status, IReadOnlyList<Item> alternatives, bool hasResolution, IReadOnlyList<Guid> resolutionRefs)
    {
        public IReadOnlyList<Guid> ResolutionRefs { get; } = resolutionRefs;
        public string Status { get; } = status;
        public IReadOnlyList<Item> Alternatives { get; } = alternatives;
        public bool HasResolution { get; } = hasResolution;
    }

    private sealed class Content
    {
        public Content(JsonNode root, IReadOnlyDictionary<Guid, string> messages)
        {
            Sources = root["sources"]!.AsArray().Select(s => new Source(
                Guid.Parse(s!["id"]!.GetValue<string>()), s["kind"]!.GetValue<string>(), s["quote"]?.GetValue<string>(),
                Guid.TryParse(s["documentId"]?.GetValue<string>(), out var d) ? d : null, Guid.TryParse(s["blockId"]?.GetValue<string>(), out var b) ? b : null,
                s["start"]?.GetValue<int>(), s["end"]?.GetValue<int>(), s)).ToDictionary(s => s.Id);
            var items = new List<Item>();
            Item Make(string section, bool inConflict, JsonNode n) =>
                new(section, inConflict, n["id"]!.GetValue<string>(), n["text"]!.GetValue<string>(), n["classification"]!.GetValue<string>(),
                    [.. n["evidenceRefs"]!.AsArray().Select(r => Guid.Parse(r!.GetValue<string>()))], Sources, messages);
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
                conflicts.Add(new Conflict(c["status"]!.GetValue<string>(), alternatives, c["resolution"] is not null,
                    [.. (c["resolution"]?["evidenceRefs"]?.AsArray() ?? []).Select(r => Guid.Parse(r!.GetValue<string>()))]));
            }

            Items = items;
            Conflicts = conflicts;
        }

        public IReadOnlyDictionary<Guid, Source> Sources { get; }
        public IReadOnlyList<Item> Items { get; }
        public IReadOnlyList<Conflict> Conflicts { get; }
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

    private sealed record Ev(Guid Id, string Statement, List<EvSource> Sources);

    private static async Task<List<Ev>> DocumentEvidences(HttpClient client, Guid project)
    {
        var page = await Json(await client.GetAsync($"/api/projects/{project}/evidence?originKind=Document&viewVersion=1.1.0&limit=100", Ct));
        return [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("evidence")).Select(e => new Ev(
            e.GetProperty("id").GetGuid(), e.GetProperty("statement").GetString()!,
            [.. e.GetProperty("content").GetProperty("sources").EnumerateArray().Select(s => new EvSource(
                s.GetProperty("sourceId").GetGuid(), s.GetProperty("documentId").GetGuid(), s.GetProperty("preparationId").GetGuid(), s.GetProperty("blockId").GetGuid(),
                s.GetProperty("start").GetInt32(), s.GetProperty("end").GetInt32(), s.GetProperty("quote").GetString()!, s.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number ? ver.GetInt32() : 1))]))];
    }

    private static async Task<string?> PassageText(HttpClient client, Guid project, EvSource source)
    {
        var response = await client.GetAsync(
            $"/api/projects/{project}/documents/{source.DocumentId}/versions/{source.Version}/preparations/{source.PreparationId}/passages/{source.BlockId}?start={source.Start}&end={source.End}", Ct);
        if (!response.IsSuccessStatusCode)
        {
            response = await client.GetAsync($"/api/projects/{project}/documents/{source.DocumentId}/versions/2/preparations/{source.PreparationId}/passages/{source.BlockId}?start={source.Start}&end={source.End}", Ct);
        }

        return response.IsSuccessStatusCode ? (await Json(response)).GetProperty("quote").GetProperty("text").GetString() : null;
    }

    private static List<Guid> ChatCitations(string answer) =>
        [.. Regex.Matches(answer, @"\[fonte:([0-9a-f-]{36})\]").Select(m => Guid.Parse(m.Groups[1].Value)).Distinct()];

    private async Task<string> Failure(string table, Guid id)
    {
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand($"SELECT \"ErrorCode\", \"ErrorMessage\" FROM \"{table}\" WHERE \"Id\" = @i", connection);
        command.Parameters.AddWithValue("i", id);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct) ? $"{(reader.IsDBNull(0) ? "-" : reader.GetString(0))}: {(reader.IsDBNull(1) ? "-" : Short(reader.GetString(1), 400))}" : "(riga assente)";
    }

    internal static string Short(string text, int max = 160)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>Minuscolo, senza diacritici, apostrofi tipografici normalizzati e spazi compressi: confronto di fedelta, non di forma.</summary>
    internal static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c is '’' or '‘' ? '\'' : c);
            }
        }

        return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant(), @"\s+", " ").Trim();
    }

    // ================================================================ verdetti e report

    private sealed class Verdicts(string label)
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
            var directory = Path.Combine(Path.GetTempPath(), "bprojects-t067");
            Directory.CreateDirectory(directory);
            var report = new StringBuilder().AppendLine($"== {label} {(_failures.Count == 0 ? "PASS" : "FAIL")} ({_lines.Count(l => l.StartsWith("  PASS", StringComparison.Ordinal))} PASS / {_failures.Count} FAIL) ==").AppendJoin('\n', _lines).AppendLine().ToString();
            File.WriteAllText(Path.Combine(directory, label + "-" + DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture) + ".txt"), report);
            TestContext.Current.SendDiagnosticMessage(report);
            TestContext.Current.TestOutputHelper?.WriteLine(report);
            Assert.True(_failures.Count == 0, $"{label}: {_failures.Count} verifiche fallite: " + string.Join(" || ", _failures));
        }
    }
}
