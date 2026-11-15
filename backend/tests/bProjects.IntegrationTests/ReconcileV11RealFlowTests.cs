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
// Configurazione della prova (adeguamento esplicito della configurazione del test, motivato in specs/002-project-documents/validation/reconciliation-hardening/evidence-m5.md): W=40960, O=8192, M=1024
// => B=31744. Con W=32768 e O=16384 (B=15360) lo stadio Gap del ledger da ~170 evidenze (21.810 token misurati in M4) non entra nel budget; con O=8192 la gerarchia e comunque instradata (soglia ~68 evidenze).
[Trait("Category", TestCategories.MafReal)]
[Trait("Campaign", "ReconcileV11RealFlow")]
[Collection(RealParser.CollectionName)]
public sealed class ReconcileV11RealFlowTests : LargeDocumentRealFlow
{
    public const int Window = 40960;
    public const int Output = 8192;
    public const int Margin = 1024;

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
        v.Note($"BUDGET (prima di Analizza): W={profile.WindowTokens} O={profile.OutputTokens} M={profile.MarginTokens} B={budget}; ledger={n} evidenze, enunciati={statementTokens} token (tokenizer reale); stima Gap={gapEstimate} token; soglia gerarchia ≈ {hierarchyThreshold} evidenze");
        v.Check("[STRUTTURALE] la gerarchia sara instradata (evidenze oltre la soglia di output della chiamata singola)", n > hierarchyThreshold, $"{n} evidenze contro soglia ≈ {hierarchyThreshold}");
        v.Check("[STRUTTURALE] lo stadio Gap e eseguibile entro il budget (stima con margine del 15%)", gapEstimate * 1.15 < budget, $"stima {gapEstimate} contro B={budget}");
        if (n <= hierarchyThreshold || gapEstimate * 1.15 >= budget)
        {
            return false;   // nessuna analisi: la prova non e valida e non viene avviata
        }

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
        var payloadText = await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1' AND \"Status\" = 'Committed' LIMIT 1", ("r", run));
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
