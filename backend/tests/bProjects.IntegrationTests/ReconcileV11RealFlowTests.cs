using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

// 002 reconciliation-hardening RH-051 (M5, D8 autorizzato): UNA sola esecuzione REALE del percorso applicativo (parser Docling reale, estrazione reale, PM, Analizza, gerarchia v11, stadio Gap) sulla fixture
// ERP/EDI sintetica, con modello DGX reale. La v11 NON e il default: il run viene congelato v11 da un trigger SOLO nel database di test (nessuna modifica del prodotto). Nessun rilancio automatico. Tetto duro di
// chiamate GENERATIVE (completions) per l'intero test. Il gold (fatti, conflitti, Gap) e dichiarato QUI, nel commit che precede l'esecuzione, e non si adatta alle risposte.
//
// Configurazione della prova (adeguamento esplicito della configurazione del test, motivato in specs/002-project-documents/validation/reconciliation-hardening/evidence-m5.md): W=65536, O=8192, M=1024
// => B=56320, ExtractionOutputExpansion=24, Extraction MaxOutputTokens=16384, thinking spento. Seconda e ultima esecuzione. Motivi: (1) con W=32768 e O=16384 (B=15360) lo stadio Gap del ledger da ~170 evidenze
// (21.810 token in M4) non entra nel budget; (2) la prima run (W=40960, fattore 16) si e fermata nell'estrazione: unita di tre tabelle da 10 righe, 916 token proprietari, ~23x di output contro 16x del planner;
// con 24 il planner divide le unita con piu di 682 token proprietari; (3) con ~2 evidenze per riga di tabella il ledger atteso e 250-300 voci: a ~135 token per voce lo stadio Gap richiede ~40k token, oltre
// B=31744 e dentro B=56320. Con O=8192 la gerarchia e comunque instradata (soglia ~68 evidenze).
[Trait("Category", TestCategories.MafReal)]
[Trait("Campaign", "ReconcileV11RealFlow")]
[Collection(RealParser.CollectionName)]
public sealed class ReconcileV11RealFlowTests : LargeDocumentRealFlow
{
    public const int Window = 65536;
    public const int Output = 8192;
    public const int Margin = 1024;

    /// <summary>Fattore di espansione dell'output dell'estrazione per la prova (default del prodotto: 16, invariato); letto dal banco da BPROJECTS_TEST_EXTRACTION_OUTPUT_EXPANSION.</summary>
    public const double ExtractionExpansion = 24;

    public const int ExtractionMaxOutput = 16384;

    protected override string? RejectOptions(AiOptions options) =>
        Math.Abs(options.ExtractionOutputExpansion - ExtractionExpansion) > 1e-9 ? $"ExtractionOutputExpansion risolto {options.ExtractionOutputExpansion} invece di {ExtractionExpansion}"
        : options.Phases.Extraction.MaxOutputTokens != ExtractionMaxOutput ? $"MaxOutputTokens di Extraction {options.Phases.Extraction.MaxOutputTokens} invece di {ExtractionMaxOutput}"
        : options.Phases.Extraction.EnableThinking == true ? "thinking di Extraction acceso"
        : null;

    /// <summary>Tetto duro di chiamate generative (chat/completions) dell'intera prova: estrazione, turni di chat, gerarchia e stadio Gap.</summary>
    public const int GenerativeCap = 200;

    protected override double TargetRatio => 0;

    protected override double MinimumRatio => 0;

    protected override string ExpectedDefinition => DocumentAnalysisWorkflow.DefinitionVersionV11;

    protected override bool GapStageExpected => true;

    protected override int MinimumHierarchyBatches => 1;

    protected override int? RequiredWindowTokens => Window;

    protected override string? RejectProfile(DocumentRuntimeProfile profile) =>
        profile.OutputTokens != Output || profile.MarginTokens != Margin ? $"profilo O={profile.OutputTokens} M={profile.MarginTokens} diverso da O={Output} M={Margin}" : null;

    protected override IReadOnlyList<string> PmQuestions => [.. RepresentativeLedger.Messages.OrderBy(m => m.Sequence).Select(m => m.Text)];

    // ----------------------------------------------------------------------------------------------- gold (fissato prima dell'esecuzione)

    /// <summary>Fatto atteso: id del gold, token (tutti richiesti; «a|b» = alternative; testo piegato senza diacritici) e posizione attesa nelle sezioni (solo informativa).</summary>
    private sealed record Fact(string Id, string[] Tokens, string Section, string Classification);

    private static readonly Fact[] GoldFacts =
    [
        new("OB-02", ["elimin", "digitazione", "ordini"], "goals", "Documented"),
        new("CT-05", ["400|quattrocento", "1000|mille|1.000"], "overview", "Documented"),
        new("FO-03", ["conferma", "cinque secondi|5 secondi"], "operationalFlows", "Documented"),
        new("FI-03", ["fattura", "24 ore|ventiquattro"], "requirements", "Documented"),
        new("RF-02", ["500|cinquecento", "righe"], "requirements", "Documented"),
        new("RF-04", ["orders", "cinque anni|5 anni"], "requirements", "Documented"),
        new("RF-23", ["certificato", "client"], "requirements", "Documented"),
        new("RF-24", ["dieci secondi|10 secondi", "richieste"], "requirements", "Documented"),
        new("ER-02", ["cinque volte|5 volte|cinque tentativi|5 tentativi", "error"], "requirements", "Documented"),
        new("ER-05", ["timeout", "due ore|2 ore"], "requirements", "Documented"),
        new("VN-02", ["cinque megabyte|5 megabyte|5 mb"], "constraints", "Documented"),
        new("VN-05", ["domenica", "manutenzion"], "constraints", "Documented"),
        new("VN-08", ["richieste", "secondo", "10|dieci"], "constraints", "Documented"),
        new("EX-01", ["ansi", "x12"], "excluded", "Documented"),
        new("DP-01", ["certificato", "rilascio"], "dependencies", "Documented"),
        new("DC-01", ["endpoint", "un solo|singolo|unico"], "requirements", "Documented"),
        new("AS-02", ["non superi|non superiore|non supera", "mille|1000", "promozional"], "assumptions", "Assumed"),
        new("AS-04", ["ordrsp", "un'ora|un ora|1 ora"], "assumptions", "Assumed"),
        new("IN-03", ["domenica", "fatture", "dopo le quattro|dopo le 4|04:00|4:00"], "constraints", "Inferred"),
        new("AL-01", ["riprocessamento", "scadenza"], "assumptions", "Assumed"),
        new("AL-03", ["archivio", "storage"], "openQuestions", "Documented"),
        new("DA-02", ["ventiquattro|24", "quarantotto|48"], "openQuestions", "Documented"),
        new("VB-03", ["timeout", "quattro ore|4 ore"], "requirements", "Documented"),
        new("VB-05", ["codicedestinazione", "obbligatori"], "requirements", "Documented"),
        new("PM-01", ["anagrafich", "gestionale alfa"], "constraints", "Declared"),
        new("PM-02", ["go-live|go live|golive", "marzo"], "requirements", "Declared"),
    ];

    /// <summary>Conflitti di versione attesi: ogni lato deve restare alternativa di un conflitto Open senza risoluzione; nessun lato affermato fuori dal conflitto.</summary>
    private sealed record GoldConflictSpec(string Id, string[] Topic, string[] SideA, string[] SideB);

    private static readonly GoldConflictSpec[] GoldConflicts =
    [
        new("C1", ["timeout", "riprocess"], ["due ore|2 ore"], ["quattro ore|4 ore"]),
        new("C2", ["codicedestinazione"], ["facoltativ|opzional"], ["obbligatori"]),
    ];

    /// <summary>Gap attesi (domande del PM): token della domanda canonica e motivi ammessi per la valutazione dello stadio Gap (sempre esito Open: nessun documento risponde in modo univoco).</summary>
    private sealed record GoldGapSpec(string Id, string[] Tokens, string[] AllowedReasons);

    private static readonly GoldGapSpec[] GoldGaps =
    [
        new("PM-03", ["scarti", "desadv"], ["Partial", "NoAnswer"]),
        new("PM-04", ["24", "48"], ["Ambiguous", "Partial"]),
    ];

    private const double FactThreshold = 0.90;

    // ----------------------------------------------------------------------------------------------- servizi: tetto di chiamate generative

    private static GenerativeCapHandler? _cap;

    internal sealed class GenerativeCapHandler(HttpMessageHandler inner, int max) : DelegatingHandler(inner)
    {
        private int _generative;
        private int _blocked;

        public int Generative => Volatile.Read(ref _generative);

        public int Blocked => Volatile.Read(ref _blocked);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/completions", StringComparison.Ordinal) == true)
            {
                if (Interlocked.Increment(ref _generative) > max)
                {
                    Interlocked.Increment(ref _blocked);
                    throw new InvalidOperationException($"tetto di {max} chiamate generative raggiunto: la richiesta non e stata inviata");
                }
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    protected override void ConfigureCampaignServices(IServiceCollection services) =>
        services.AddSingleton(sp =>
        {
            var cap = new GenerativeCapHandler(MafDiagnostics.Wrap(), GenerativeCap);
            _cap = cap;
            return new InternalModelClient(sp.GetRequiredService<IOptions<AiOptions>>().Value, cap);
        });

    // ----------------------------------------------------------------------------------------------- corpus: le due fonti documentali della fixture rappresentativa

    private static Corpus BuildCorpus()
    {
        var docs = new List<CorpusDocument>();
        foreach (var (key, file) in new[] { ("af", RepresentativeLedger.Af), ("verbale", RepresentativeLedger.Verbale) })
        {
            var text = RepresentativeLedger.Read(file);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            docs.Add(new CorpusDocument(key, file, DocumentFormat.Markdown, bytes, RealParser.Sha256(bytes), text, RealParser.Sha256(bytes), [], []));
        }

        // Nessun gold nel formato del corpus T082/T083: il gold di RH-051 e valutato da VerifyExtraAsync (sopra).
        return new Corpus("RH-051 ERP/EDI rappresentativo", docs, [], [], [], null);
    }

    [Fact]
    public async Task RH051_the_v11_hierarchy_with_the_real_parser_extraction_and_model_on_the_representative_erp_edi_fixture()
    {
        TestEnvironment.RequireDgx();
        await Require();
        await RunScenarioAsync("RH051", _ => BuildCorpus());
    }

    // ----------------------------------------------------------------------------------------------- prima di Analizza: budget misurato con il tokenizer reale e congelamento v11 (solo DB di test)

    protected override async Task<bool> PrepareBeforeAnalyzeAsync(HttpClient client, Guid project, Verdicts v)
    {
        var sp = App.Services;
        var factory = AgentFactory.Create(
            sp.GetRequiredService<IOptions<AiOptions>>().Value, sp.GetRequiredService<InternalModelClient>(), sp.GetRequiredService<ModelRuntimeMonitor>(),
            sp.GetRequiredService<IOptions<DocumentOptions>>().Value.Calibration.ToCalibration());
        var profile = await factory.Preflight.ResolveProfileAsync(AiPhase.Analysis, Ct);
        var budget = profile.InputBudgetTokens;

        var statements = new List<string>();
        await using (var connection = await OpenConnection())
        await using (var command = new Npgsql.NpgsqlCommand("SELECT \"Statement\" FROM \"Evidences\" WHERE \"ProjectId\" = @p", connection))
        {
            command.Parameters.AddWithValue("p", project);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                statements.Add(reader.GetString(0));
            }
        }

        var n = statements.Count;
        var statementTokens = await factory.Preflight.CountVerifiedTextAsync(profile, string.Join('\n', statements), Ct)
            ?? throw new InvalidOperationException("tokenizer del modello non verificato: impossibile misurare il ledger (nessuna stima).");

        // Stima PRUDENTE dello stadio Gap: token degli enunciati (tokenizer reale) + ~110 token di struttura per voce (M4: 129 token per voce con la misura piu realistica, enunciati inclusi). La guardia AUTORITATIVA
        // e quella del prodotto: senza l'intero ledger nel budget lo stadio Gap e un fail-fast a zero invii, PRIMA della gerarchia.
        var gapEstimate = statementTokens + 110L * n;
        var hierarchyThreshold = Output / 120;   // ~120 token di output per evidenza nella stima della chiamata singola (assessment §8)
        // MISURE AUTOREVOLI (bloccanti): token reali degli enunciati; una condizione NECESSARIA, non una stima — se i soli enunciati non entrano in B lo stadio Gap non puo entrare (il prodotto fallirebbe a zero invii).
        v.Note($"BUDGET (prima di Analizza) — MISURATO: W={profile.WindowTokens} O={profile.OutputTokens} M={profile.MarginTokens} B={budget}; ledger={n} evidenze; enunciati={statementTokens} token (tokenizer reale del modello).");
        v.Check("[STRUTTURALE/MISURATO] ledger non vuoto e i soli enunciati (token reali) entrano nel budget di input B: condizione necessaria per lo stadio Gap", n > 0 && statementTokens < budget, $"{n} evidenze, {statementTokens} token contro B={budget}");

        // STIME EURISTICHE (NON bloccanti: solo informative; la misura autorevole e quella del prodotto durante il run — fail-fast a zero invii per lo stadio Gap, instradamento gerarchico dalla stima di output).
        v.Note($"STIMA EURISTICA (non autorevole, non bloccante): Gap ≈ {gapEstimate} token = enunciati + 110/voce (margine 15% → {gapEstimate * 1.15:F0} contro B={budget}); soglia di instradamento gerarchico ≈ {hierarchyThreshold} evidenze (O/120 per evidenza) → ledger {n} {(n > hierarchyThreshold ? "oltre" : "SOTTO")} la soglia. Il ramo effettivo e verificato dopo il run dagli stadi dei batch.");

        // Il fattore 24 NON e nel PlanJson: e nella configurazione effettiva (opzioni risolte, verificate PRIMA dell'upload da RejectOptions) e congelato nel ConfigHash del manifesto di estrazione
        // (DocumentEvidenceAgent: configHash include outputExpansion; test DocumentEvidenceAgentTests.The_extraction_output_expansion_is_frozen_...). Qui si registra solo il fatto osservabile.
        var resolved = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        var manifestHashes = await Rows("SELECT DISTINCT \"ConfigHash\" FROM \"WorkManifests\" WHERE \"DefinitionVersion\" = '" + DocumentEvidenceAgent.DefinitionVersion + "' ORDER BY 1");
        v.Note($"fattore di espansione dell'estrazione RISOLTO={resolved.ExtractionOutputExpansion} (autorevole, verificato prima dell'upload); manifesti di estrazione osservati: {manifestHashes.Count} ConfigHash distinti (il fattore e uno dei suoi ingressi)");

        // Precondizione del trigger di congelamento: il freeze di produzione, con le opzioni di recupero RISOLTE, deve essere la v6 (altrimenti il trigger non scatterebbe e il run non sarebbe v11).
        var retrieval = sp.GetRequiredService<IOptions<bProjects.App.Retrieval.RetrievalOptions>>().Value;
        var productFreeze = SourceSnapshotStore.DocumentaryAnalysisFreeze(retrieval.Enabled, retrieval).Definition;
        v.Check("[STRUTTURALE] il freeze di produzione delle nuove richieste di Analizza (opzioni risolte) e la v6: il trigger di prova lo trasforma in v11", productFreeze == SourceSnapshotStore.AnalysisDefinitionV6, $"freeze={productFreeze} recupero abilitato={retrieval.Enabled}");
        if (n == 0 || statementTokens >= budget || productFreeze != SourceSnapshotStore.AnalysisDefinitionV6)
        {
            return false;   // nessuna analisi: la prova non e valida e non viene avviata
        }

        // Copia consistente della preparazione (documenti, evidenze, snapshot, manifesti) PRIMA di Analizza: se la riconciliazione fallisce non si ripete l'estrazione.
        await BackupDatabaseAsync("pre-analizza", v);

        // Congelamento v11 SOLO nel database di test (trigger sulla creazione del run di analisi v6): nessuna modifica del prodotto, nessuna corsa con il worker.
        var prompts = SourceSnapshotStore.AnalysisPromptVersionsV11();
        await Execute(
            "CREATE OR REPLACE FUNCTION rh051_force_v11() RETURNS trigger AS $f$ BEGIN "
            + $"IF NEW.\"WorkflowDefinitionVersion\" = '{SourceSnapshotStore.AnalysisDefinitionV6}' THEN NEW.\"WorkflowDefinitionVersion\" := '{DocumentAnalysisWorkflow.DefinitionVersionV11}'; NEW.\"PromptVersions\" := $j${prompts}$j$::jsonb; END IF; "
            + "RETURN NEW; END; $f$ LANGUAGE plpgsql");
        await Execute("DROP TRIGGER IF EXISTS rh051_force_v11 ON \"ProcessingRuns\"");
        await Execute("CREATE TRIGGER rh051_force_v11 BEFORE INSERT ON \"ProcessingRuns\" FOR EACH ROW EXECUTE FUNCTION rh051_force_v11()");
        v.Note("la nuova analisi sara congelata v11 da un trigger del database di test (il default del prodotto resta v6)");
        return true;
    }

    // ----------------------------------------------------------------------------------------------- gold: fatti, conflitti, Gap

    protected override async Task VerifyExtraAsync(HttpClient client, Guid project, Guid run, Guid revision, Corpus corpus, Content content, Verdicts v)
    {
        var report = new JsonObject { ["run"] = run.ToString(), ["revision"] = revision.ToString(), ["generativeCalls"] = _cap?.Generative, ["generativeBlocked"] = _cap?.Blocked };

        // ---- fatti attesi
        var facts = new JsonArray();
        var represented = 0;
        foreach (var fact in GoldFacts)
        {
            var matches = content.Items.Where(i => fact.Tokens.All(i.Has)).ToList();
            var found = matches.Count > 0;
            represented += found ? 1 : 0;
            var inPlace = matches.Any(i => i.Section == fact.Section || (fact.Section is "overview" && i.Section is "overview" or "included") || (fact.Section == "constraints" && i.Section == "constraints"));
            facts.Add(new JsonObject
            {
                ["id"] = fact.Id, ["represented"] = found, ["sectionAsExpected"] = inPlace, ["expectedSection"] = fact.Section, ["expectedClassification"] = fact.Classification,
                ["matches"] = new JsonArray([.. matches.Take(3).Select(m => (JsonNode?)JsonValue.Create($"[{m.Section}/{m.Classification}] {Short(m.Text, 200)}"))]),
            });
        }

        var ratio = (double)represented / GoldFacts.Length;
        report["factsRepresented"] = $"{represented}/{GoldFacts.Length}";
        report["facts"] = facts;
        v.Check($"[SEMANTICA/GOLD] almeno il 90% dei fatti attesi e rappresentato (item, alternativa di conflitto o domanda aperta): {represented}/{GoldFacts.Length}", ratio >= FactThreshold,
            string.Join(", ", GoldFacts.Where(f => !content.Items.Any(i => f.Tokens.All(i.Has))).Select(f => f.Id)));

        // ---- classificazioni sostanziali (contratto: Declared = messaggio PM, Assumed = ipotesi, Inferred = deduzione). La SEZIONE non e un requisito: un fatto conservato come alternativa di un conflitto Open e corretto.
        var substantive = GoldFacts.Where(f => f.Classification is "Declared" or "Assumed" or "Inferred").ToList();
        var classWrong = substantive.Where(f => content.Items.Any(i => f.Tokens.All(i.Has)) && !content.Items.Where(i => f.Tokens.All(i.Has)).Any(i => i.Classification == f.Classification)).Select(f => f.Id).ToList();
        v.Check($"[SEMANTICA/GOLD] classificazioni sostanziali (Declared/Assumed/Inferred) dei fatti presenti coerenti con il gold: {substantive.Count - classWrong.Count}/{substantive.Count} (assenti: valutati dalla presenza)", classWrong.Count == 0, string.Join(", ", classWrong));
        var clarify = new[] { "AL-03", "DA-02" };
        var clarifyAsDecision = clarify.Select(id => GoldFacts.Single(f => f.Id == id)).Where(f => content.Items.Where(i => f.Tokens.All(i.Has)).Any() && !content.Items.Where(i => f.Tokens.All(i.Has)).Any(i => i.Section == "openQuestions" || i.InConflict)).Select(f => f.Id).ToList();
        v.Check("[SEMANTICA/GOLD] i punti da chiarire (AL-03, DA-02) restano domande aperte o alternative di un conflitto, mai decisioni acquisite", clarifyAsDecision.Count == 0, string.Join(", ", clarifyAsDecision));
        report["classificationMismatches"] = new JsonArray([.. classWrong.Select(id => (JsonNode?)JsonValue.Create(id))]);

        // ---- ramo effettivamente scelto dalla v11 e stadi del run (misura autorevole del prodotto)
        var stages = await Rows("SELECT \"Stage\" || ':' || count(*) FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r GROUP BY \"Stage\" ORDER BY 1", ("r", run));
        report["batchesByStage"] = new JsonArray([.. stages.Select(s => (JsonNode?)JsonValue.Create(s))]);
        v.Note($"RAMO v11 (misura del prodotto): batch per stadio = {string.Join(", ", stages)}; il ramo gerarchia + stadio Gap e quello eseguito se compaiono stadi leaf/cross/compare e gap");
        v.Check("[STRUTTURALE] la v11 ha eseguito la gerarchia (foglie) e lo stadio Gap", stages.Any(s => s.StartsWith("leaf:", StringComparison.Ordinal)) && stages.Any(s => s.StartsWith("gap:", StringComparison.Ordinal)), string.Join(", ", stages));

        // ---- conflitti attesi: Open, senza risoluzione, entrambi i lati come alternative; nessun lato affermato altrove
        var conflicts = new JsonArray();
        foreach (var spec in GoldConflicts)
        {
            bool Side(Item a, string[] side) => spec.Topic.Any(a.Has) && side.All(a.Has);
            var holding = content.Conflicts.Where(c => c.Alternatives.Any(a => Side(a, spec.SideA)) && c.Alternatives.Any(a => Side(a, spec.SideB))).ToList();
            var open = holding.Any(c => c.Status == "Open" && !c.HasResolution);
            var arbitrary = content.Items.Where(i => !i.InConflict && spec.Topic.Any(i.Has) && (spec.SideA.All(i.Has) || spec.SideB.All(i.Has)) && i.Section != "openQuestions").Select(i => $"[{i.Section}/{i.Classification}] {Short(i.Text, 160)}").ToList();
            conflicts.Add(new JsonObject { ["id"] = spec.Id, ["conflictsHoldingBothSides"] = holding.Count, ["openUnresolved"] = open, ["sidesAssertedOutsideConflict"] = new JsonArray([.. arbitrary.Select(a => (JsonNode?)JsonValue.Create(a))]) });
            v.Check($"[SEMANTICA/GOLD] conflitto {spec.Id}: entrambi i lati sono alternative dello stesso conflitto Open senza risoluzione", open, $"conflitti con entrambi i lati={holding.Count}");
            v.Check($"[SEMANTICA/GOLD] conflitto {spec.Id}: nessun lato e affermato come decisione acquisita fuori dal conflitto", arbitrary.Count == 0, string.Join(" | ", arbitrary));
        }

        report["conflicts"] = conflicts;
        report["allConflicts"] = new JsonArray([.. content.Conflicts.Select(c => (JsonNode?)JsonValue.Create($"{c.Status}{(c.HasResolution ? "/risolto" : "")}: " + string.Join(" || ", c.Alternatives.Select(a => Short(a.Text, 140)))))]);

        // ---- Gap: domanda canonica presente e valutazione dello stadio Gap
        var gapRows = new JsonArray();
        var payloadText = (await Rows("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1' AND \"Status\" = 'Committed' LIMIT 1", ("r", run))).FirstOrDefault();
        if (payloadText is null)
        {
            v.Check("[COPERTURA/GOLD] lo stadio Gap ha un WorkOutput Committed (schema gap-stage-alias-1): senza di esso le valutazioni dei Gap non sono verificabili", false, "nessun output committato");
            Persist("rh051-gold-evaluation", report);
            return;
        }

        using var payload = JsonDocument.Parse(payloadText);
        report["gapStagePayload"] = JsonNode.Parse(payloadText);
        var assessments = payload.RootElement.TryGetProperty("gapAssessments", out var g) && g.ValueKind == JsonValueKind.Array ? g.EnumerateArray().ToList() : [];
        v.Check("[COPERTURA/GOLD] lo stadio Gap ha esito Complete e un record per ciascuno dei Gap del PM", payload.RootElement.GetProperty("outcome").GetString() == "Complete" && assessments.Count == GoldGaps.Length, $"esito={payload.RootElement.GetProperty("outcome").GetString()} record={assessments.Count}");
        foreach (var spec in GoldGaps)
        {
            var question = content.Items.Where(i => i.Section == "openQuestions" && spec.Tokens.All(i.Has)).ToList();
            v.Check($"[COPERTURA/GOLD] {spec.Id}: la domanda canonica e presente tra i punti aperti", question.Count >= 1, string.Join(" | ", question.Select(q => Short(q.Text, 120))));
            gapRows.Add(new JsonObject { ["id"] = spec.Id, ["openQuestions"] = new JsonArray([.. question.Select(q => (JsonNode?)JsonValue.Create(q.Text))]), ["allowedReasons"] = string.Join('/', spec.AllowedReasons) });
        }

        var reasons = assessments.Select(a => a.TryGetProperty("reason", out var r) ? r.GetString() : null).ToList();
        var outcomes = assessments.Select(a => a.TryGetProperty("outcome", out var o) ? o.GetString() : null).ToList();
        v.Note($"valutazioni dello stadio Gap: {string.Join("; ", outcomes.Zip(reasons, (o, r) => $"{o}/{r}"))} (attesi: tutte Open con motivo ammesso dal gold: PM-03 Partial|NoAnswer, PM-04 Ambiguous|Partial)");
        // Collegamento record -> Gap del gold: il record (gap-assessment-1) indica il Gap con l'ALIAS `gap` (E<n>); il catalogo alias del run, dentro lo stesso payload (`catalog.evidence[]`: alias → evidenceId),
        // lo risolve nell'evidenza, il cui enunciato e confrontato con la domanda del gold. Poi motivo ammesso dal gold.
        var aliasToEvidence = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (payload.RootElement.TryGetProperty("catalog", out var catalogEl) && catalogEl.TryGetProperty("evidence", out var catalogEvidence) && catalogEvidence.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in catalogEvidence.EnumerateArray())
            {
                if (entry.TryGetProperty("alias", out var aliasEl) && entry.TryGetProperty("evidenceId", out var idEl) && Guid.TryParse(idEl.GetString(), out var evidenceId) && aliasEl.GetString() is { } alias)
                {
                    aliasToEvidence[alias] = evidenceId;
                }
            }
        }

        report["gapCatalogAliases"] = aliasToEvidence.Count;
        v.Check("[COPERTURA/GOLD] il catalogo alias del payload dello stadio Gap risolve i record (alias → evidenza): precondizione del collegamento ai Gap del gold", aliasToEvidence.Count > 0 && assessments.All(a => a.TryGetProperty("gap", out var gp) && gp.GetString() is { } al && aliasToEvidence.ContainsKey(al)), $"alias nel catalogo={aliasToEvidence.Count} record={assessments.Count}");
        foreach (var spec in GoldGaps)
        {
            string? matchedReason = null;
            string? matchedOutcome = null;
            var linked = false;
            foreach (var a in assessments)
            {
                if (!a.TryGetProperty("gap", out var gapAlias) || gapAlias.GetString() is not { } aliasValue || !aliasToEvidence.TryGetValue(aliasValue, out var gapId))
                {
                    continue;
                }

                var statement = (await Rows("SELECT \"Statement\" FROM \"Evidences\" WHERE \"Id\" = @i LIMIT 1", ("i", gapId))).FirstOrDefault() ?? string.Empty;
                if (spec.Tokens.All(t => HasToken(statement, t)))
                {
                    linked = true;
                    matchedOutcome = a.GetProperty("outcome").GetString();
                    matchedReason = a.GetProperty("reason").GetString();
                }
            }

            v.Check($"[COPERTURA/GOLD] {spec.Id}: la valutazione e collegata al Gap, esito Open e motivo ammesso ({string.Join('|', spec.AllowedReasons)})", linked && matchedOutcome == "Open" && spec.AllowedReasons.Contains(matchedReason ?? string.Empty), $"collegata={linked} esito={matchedOutcome} motivo={matchedReason}");
        }

        v.Check("[SEMANTICA/GOLD] nessun Gap dichiarato risposto in modo univoco: tutte le valutazioni sono Open", outcomes.Count == GoldGaps.Length && outcomes.All(o => o == "Open"));
        report["gaps"] = gapRows;

        // ---- fonti: nessuna citazione inventata (testo delle fonti e dei messaggi), oltre alle verifiche di provenienza del banco
        var allowed = corpus.Documents.Select(d => Fold(d.PlainText)).Concat(RepresentativeLedger.Messages.Select(m => Fold(m.Text))).ToList();
        var unknownQuotes = content.Sources.Values.Where(s => s.Quote is { Length: > 0 }).Where(s => !allowed.Any(t => t.Contains(Fold(s.Quote!), StringComparison.Ordinal))).Select(s => Short(s.Quote!, 100)).ToList();
        report["quotesNotLiteralInSourceMarkdown"] = new JsonArray([.. unknownQuotes.Select(q => (JsonNode?)JsonValue.Create(q))]);
        report["sources"] = content.Sources.Count;
        report["items"] = content.Items.Count;
        report["conflictCount"] = content.Conflicts.Count;
        v.Note($"citazioni non letterali nel markdown/messaggi di origine (dopo piegatura): {unknownQuotes.Count} su {content.Sources.Count} fonti (il parser puo normalizzare il markdown; la verifica di provenienza autorevole e quella del banco sul testo estratto)");

        _ = client;
        _ = project;
        Persist("rh051-gold-evaluation", report);
    }

    // ----------------------------------------------------------------------------------------------- evidenze della prova (anche a run fallito, prima dell'eliminazione del database di test)

    protected override async Task OnFinalizingAsync()
    {
        var dump = new JsonObject { ["generativeCalls"] = _cap?.Generative, ["generativeBlocked"] = _cap?.Blocked, ["generativeCap"] = GenerativeCap, ["window"] = Window, ["output"] = Output, ["margin"] = Margin };
        Guid? run = null;
        try
        {
            run = await Scalar<Guid>("SELECT \"Id\" FROM \"ProcessingRuns\" WHERE \"Kind\"::text = 'Analysis' ORDER BY \"RequestedAt\" DESC LIMIT 1");
            dump["run"] = run.ToString();
        }
        catch (Exception e)
        {
            dump["runError"] = e.GetType().Name;
        }

        // Ogni interrogazione e indipendente: l'errore di una non impedisce le altre.
        async Task Section(string name, string sql, params (string Name, object Value)[] args)
        {
            try
            {
                dump[name] = await RowsAsync(sql, args);
            }
            catch (Exception e)
            {
                dump[name + "Error"] = e.GetType().Name + ": " + e.Message;
            }
        }

        // Copia consistente dello stato finale (anche a run fallito), PRIMA che il database di test venga eliminato.
        dump["finalBackup"] = await BackupDatabaseAsync("finale", null);
        var r = run ?? Guid.Empty;
        await Section("runRow", "SELECT to_jsonb(r) - 'PromptVersions' FROM \"ProcessingRuns\" r WHERE r.\"Id\" = @r", ("r", r));
        await Section(
            "inferenceCalls",
            "SELECT jsonb_build_object('phase', \"Phase\"::text, 'finish', \"FinishReason\", 'promptTokens', \"PromptTokens\", 'completionTokens', \"CompletionTokens\", 'maxOut', \"MaxOutputTokensRequested\", 'ms', \"DurationMs\", 'startedAt', \"StartedAt\", 'runId', \"ProcessingRunId\") FROM \"InferenceCalls\" ORDER BY \"StartedAt\"");
        await Section("derivedBatches", "SELECT to_jsonb(b) FROM \"DerivedBatches\" b WHERE b.\"OwnerId\" = @r ORDER BY b.\"Ordinal\"", ("r", r));
        await Section("workOutputs", "SELECT to_jsonb(w) - 'PayloadJson' || jsonb_build_object('payloadBytes', length(w.\"PayloadJson\"::text)) FROM \"WorkOutputs\" w WHERE w.\"OwnerId\" = @r ORDER BY w.\"CommittedAt\"", ("r", r));
        await Section("ledger", "SELECT jsonb_build_object('kind', e.\"Kind\"::text, 'certainty', e.\"Certainty\"::text, 'origin', e.\"OriginKind\"::text, 'statement', e.\"Statement\") FROM \"Evidences\" e ORDER BY e.\"OriginKind\", e.\"Ordinal\"");
        await Section("analysisRevision", "SELECT jsonb_build_object('revisionId', r.\"Id\", 'content', r.\"ContentJson\"::jsonb) FROM \"AnalysisRevisions\" r WHERE r.\"AnalysisId\" = @r", ("r", r));
        Persist("rh051-run-dump", dump);
    }

    // ----------------------------------------------------------------------------------------------- copia consistente del database di test (circoscritta a questa prova)

    /// <summary>
    /// Copia consistente (pg_dump -Fc, snapshot transazionale, funziona con l'app in esecuzione) del SOLO database di test di questa prova, eseguita dentro il container PostgreSQL di test
    /// (variabile BPROJECTS_TEST_PG_CONTAINER, default «bprojects-test-pg»; autenticazione locale del container: nessuna password nei comandi, nei log o nel file).
    /// Il file va in %TEMP%\bprojects-rh051-backup (fuori dal repository, mai committato). Un errore non fa fallire la prova: e registrato. Contiene i dati sintetici della prova, inclusi gli account
    /// di test creati dal banco (hash di password usa-e-getta); i file originali dei documenti sono in %TEMP%\bprojects-i5-real e i blob dei parser nello storage locale del banco (non nel dump).
    /// </summary>
    private async Task<string?> BackupDatabaseAsync(string label, Verdicts? v)
    {
        try
        {
            await using var connection = await OpenConnection();
            var database = connection.Database;
            var user = new Npgsql.NpgsqlConnectionStringBuilder(connection.ConnectionString).Username ?? "postgres";
            if (!database.StartsWith("bprojects_test_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("il database non e un database di test del banco: nessuna copia");
            }

            var container = Environment.GetEnvironmentVariable("BPROJECTS_TEST_PG_CONTAINER") is { Length: > 0 } c ? c : "bprojects-test-pg";
            var directory = Path.Combine(Path.GetTempPath(), "bprojects-rh051-backup");
            Directory.CreateDirectory(directory);
            var file = $"rh051-{label}-{DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture)}.dump";
            var inside = "/tmp/" + file;
            await RunDockerAsync(["exec", container, "pg_dump", "-U", user, "-Fc", "-f", inside, database]);
            var target = Path.Combine(directory, file);
            await RunDockerAsync(["cp", $"{container}:{inside}", target]);
            await RunDockerAsync(["exec", container, "rm", "-f", inside]);
            var size = new FileInfo(target).Length;
            var message = $"copia consistente del database di test ({label}): {target} ({size} byte). Ripristino: createdb + pg_restore nel container di test; non committare";
            v?.Note(message);
            TestContext.Current.SendDiagnosticMessage(message);
            return target;
        }
        catch (Exception e)
        {
            var message = $"copia del database di test ({label}) NON riuscita: {e.GetType().Name}: {e.Message}";
            v?.Note(message);
            TestContext.Current.SendDiagnosticMessage(message);
            return null;
        }
    }

    private static async Task RunDockerAsync(string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("docker non avviabile");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
        {
            var text = (await error).Trim();
            throw new InvalidOperationException($"docker {arguments[0]} codice {process.ExitCode}: {text[..Math.Min(200, text.Length)]}");
        }
    }

    private async Task<List<string>> Rows(string sql, params (string Name, object Value)[] args)
    {
        var rows = new List<string>();
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<JsonArray> RowsAsync(string sql, params (string Name, object Value)[] args)
    {
        var rows = new JsonArray();
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(JsonNode.Parse(reader.GetString(0)));
        }

        return rows;
    }

    private static void Persist(string name, JsonObject report)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bprojects-rh051");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{name}-{DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture)}Z.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
    }
}
