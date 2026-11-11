using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

/// <summary>Esito di una funzionalità dello schema sul provider: provata rispettata, ignorata, rifiutata, non conclusiva o non esercitata dalla verifica.</summary>
internal enum FeatureOutcome { Honored, IgnoredByProvider, RejectedByProvider, Inconclusive, NotExercised }

/// <summary>Una chiamata della verifica: variante dello schema inviata, esito HTTP, risposta del modello e hash canonico dello schema REALMENTE presente nel corpo della richiesta.</summary>
internal sealed record ProbeCall(string Variant, int? HttpStatus, string? FinishReason, string? Content, string? Error, string? RequestSchemaHash);

/// <summary>Verdetto su una funzionalità dello schema (es. <c>maxItems:0</c> su <c>items</c> nella variante NoItems).</summary>
internal sealed record FeatureVerdict(string Feature, FeatureOutcome Outcome, string Detail);

/// <summary>Scambio HTTP osservato dal <see cref="ProbeHandler"/>: corpo inviato, stato e corpo della risposta. Nessuna intestazione (la chiave non viene mai registrata).</summary>
internal sealed record ProbeExchange(string Method, string Path, string RequestBody, int? Status, string? ResponseBody);

/// <summary>Osservatore HTTP della verifica: registra corpo della richiesta e risposta, non altera nulla e non ripete mai una richiesta.</summary>
internal sealed class ProbeHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly List<ProbeExchange> _exchanges = [];

    public IReadOnlyList<ProbeExchange> Exchanges => _exchanges;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        HttpResponseMessage? response = null;
        string? responseBody = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);   // bufferizza: il chiamante può rileggere
            return response;
        }
        finally
        {
            _exchanges.Add(new ProbeExchange(request.Method.Method, request.RequestUri?.AbsolutePath ?? string.Empty, body, response is null ? null : (int)response.StatusCode, responseBody));
        }
    }
}

/// <summary>
/// Verifica MINIMA e ISOLATA del supporto, sul provider reale (llama.cpp b10703, router), delle due funzionalità di schema su cui si regge il profilo <c>reconcile-alias-schema-by-batch-1</c> della definizione v8:
/// <c>maxItems: 0</c> (su <c>items</c> in NoItems e Candidate, su <c>patches</c> in NoItems) e <c>{"type":"null"}</c> (su <c>focus</c> in NoItems). Il convertitore schema→grammatica del provider potrebbe ignorarle o rifiutarle:
/// se le ignora la v8 si comporta come la v7 (il compositore rifiuta comunque), se le rifiuta ogni batch fallirebbe. Qui si misura, con TRE sole richieste (una per variante, in ordine, mai ripetute): CONTROLLO con lo schema
/// completo (deve indurre un <c>items</c> non vuoto e un <c>focus</c> oggetto, altrimenti ogni altro esito è non conclusivo), poi NoItems e Candidate con la stessa richiesta. Nessun database, nessun documento,
/// nessuna campagna, nessun prompt di produzione.
/// </summary>
internal static class ReconcileSchemaSupportProbe
{
    public const string ControlVariant = "Full";

    /// <summary>Output massimo per chiamata: l'output atteso è di poche centinaia di token; un troncamento (finish=length) rende la prova non conclusiva, non un PASS.</summary>
    public const int MaxOutputTokens = 1024;

    public const string SystemPrompt =
        "Sei un verificatore di formato. Rispondi SOLO con un oggetto JSON conforme allo schema fornito, senza testo prima o dopo.";

    public const string UserPrompt =
        "Prova di formato, nessun contenuto reale. Evidenze del batch: E1 = «Il pagamento avviene entro 30 giorni.»; E2 = «Il pagamento avviene entro 60 giorni.». Compila la risposta cosi: "
        + "contract = reconcile-alias-1; descriptor e noFindings null; items: UN elemento con key i1, section requirements, text «Pagamento entro 30 giorni», una classification ammessa e evidence [E1]; "
        + "candidates: UN candidato con key f1 fra E1 ed E2, con type, basis e rationale ammessi; focus: outcome Compared con reason «prova»; patches: []. Scegli i valori degli enum fra quelli ammessi dallo schema.";

    public static readonly string[] Variants = [ControlVariant, nameof(ReconcileSchemaVariant.NoItems), nameof(ReconcileSchemaVariant.Candidate)];

    /// <summary>Hash canonico (RFC 8785, SHA-256) dello schema di una variante: lo stesso del manifesto T040 v4.</summary>
    public static string SchemaHash(string variantName) =>
        CanonicalJson.HashText(CanonicalJson.Canonicalize(ReconcileAliasContracts.SchemaOf(Enum.Parse<ReconcileSchemaVariant>(variantName)).GetRawText()));

    /// <summary>
    /// Invia UNA richiesta per variante, con le opzioni di richiesta del PRODOTTO (<see cref="InternalModelClient.CreateChatOptions"/>: stesso <c>response_format</c> json_schema grezzo, stesso ragionamento esplicito,
    /// fase Analysis, temperatura 0). Nessun retry: un errore HTTP è registrato e la variante successiva parte comunque (sono richieste indipendenti, ciascuna al più una volta).
    /// </summary>
    public static async Task<IReadOnlyList<ProbeCall>> RunAsync(InternalModelClient client, ProbeHandler handler, CancellationToken cancellationToken)
    {
        var calls = new List<ProbeCall>();
        foreach (var variant in Variants)
        {
            var before = handler.Exchanges.Count;
            var options = client.CreateChatOptions(ReconcileAliasContracts.SchemaOf(Enum.Parse<ReconcileSchemaVariant>(variant)), ReconcileAliasContracts.SchemaName, temperature: 0, phase: AiPhase.Analysis);
            options.MaxOutputTokens = MaxOutputTokens;
            string? content = null;
            string? finish = null;
            string? error = null;
            try
            {
                var response = await client.ChatClient.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, UserPrompt)], options, cancellationToken);
                content = response.Text;
                finish = response.FinishReason?.Value;
            }
            catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))   // un timeout del client e un errore della variante, non l'annullamento del test
            {
                error = e.GetType().Name;   // solo il tipo: lo stato e il corpo d'errore del server sono nello scambio HTTP registrato
            }

            var exchange = handler.Exchanges.Skip(before).LastOrDefault(x => x.Method == "POST");
            calls.Add(new ProbeCall(variant, exchange?.Status, finish, content, error, exchange is null ? null : RequestSchemaHash(exchange.RequestBody)));
        }

        return calls;
    }

    /// <summary>Hash canonico dello schema presente nel <c>response_format</c> del corpo inviato; null se il corpo non ne contiene uno (es. nessuno schema inviato).</summary>
    public static string? RequestSchemaHash(string requestBody)
    {
        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return document.RootElement.TryGetProperty("response_format", out var format) && format.TryGetProperty("json_schema", out var js) && js.TryGetProperty("schema", out var schema)
                ? CanonicalJson.HashText(CanonicalJson.Canonicalize(schema.GetRawText()))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Parsed(int Items, int Candidates, JsonValueKind Focus);

    private static Parsed? Parse(ProbeCall call)
    {
        if (call.HttpStatus is not (>= 200 and < 300) || call.Error is not null || call.Content is null || string.Equals(call.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(call.Content);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                && root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && root.TryGetProperty("focus", out var focus)
                ? new Parsed(items.GetArrayLength(), candidates.GetArrayLength(), focus.ValueKind)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Rifiuto dello schema = errore di richiesta (400/422) del provider. Altri errori (401, 429, 5xx, timeout, nessuna risposta) non dicono nulla sullo schema: sono non conclusivi, mai «rifiutato».</summary>
    private static bool Rejected(ProbeCall call) => call.HttpStatus is 400 or 422;

    private static bool Failed(ProbeCall call) => call.Error is not null || call.HttpStatus is not (>= 200 and < 300);

    /// <summary>Lo schema REALMENTE inviato è quello congelato per la variante (altrimenti la risposta non dice nulla sulla variante).</summary>
    private static bool SentFrozenSchema(ProbeCall call) => call.RequestSchemaHash is { } hash && string.Equals(hash, SchemaHash(call.Variant), StringComparison.Ordinal);

    /// <summary>
    /// Verdetto per funzionalità, funzione PURA sulle tre chiamate (controllo, NoItems, Candidate). Un esito «Honored» è possibile solo se il CONTROLLO ha mostrato che la richiesta induce il contenuto che la
    /// funzionalità dovrebbe impedire; altrimenti «Inconclusive». <c>maxItems:0</c> su <c>patches</c> NON è esercitato (il controllo non induce patch): il suo supporto è inferito, mai dichiarato provato.
    /// </summary>
    public static IReadOnlyList<FeatureVerdict> Evaluate(IReadOnlyList<ProbeCall> calls)
    {
        var control = calls.SingleOrDefault(c => c.Variant == ControlVariant);
        var noItems = calls.SingleOrDefault(c => c.Variant == nameof(ReconcileSchemaVariant.NoItems));
        var candidate = calls.SingleOrDefault(c => c.Variant == nameof(ReconcileSchemaVariant.Candidate));
        if (control is null || noItems is null || candidate is null)
        {
            return [new("verifica", FeatureOutcome.Inconclusive, "servono esattamente le tre chiamate (controllo, NoItems, Candidate)")];
        }

        var parsedControl = SentFrozenSchema(control) ? Parse(control) : null;
        var controlItems = parsedControl is { Items: > 0 };
        var controlFocus = parsedControl is { Focus: JsonValueKind.Object };

        FeatureVerdict Judge(string feature, ProbeCall call, bool controlInduces, string controlNote, Func<Parsed, (bool Respected, string Detail)> check)
        {
            if (!SentFrozenSchema(call))
            {
                return new(feature, FeatureOutcome.Inconclusive, "lo schema nel corpo della richiesta non e quello congelato per la variante (o non e stato inviato): la risposta non dice nulla sulla variante");
            }

            if (Rejected(call))
            {
                return new(feature, FeatureOutcome.RejectedByProvider, $"il provider ha rifiutato lo schema (HTTP {call.HttpStatus}, {call.Error ?? "errore di richiesta"})");
            }

            if (Failed(call))
            {
                return new(feature, FeatureOutcome.Inconclusive, $"la richiesta non e andata a buon fine per un motivo non riconducibile allo schema (HTTP {call.HttpStatus?.ToString() ?? "nessuno"}, {call.Error ?? "errore"})");
            }

            if (!controlInduces)
            {
                return new(feature, FeatureOutcome.Inconclusive, $"il controllo con lo schema completo non ha mostrato {controlNote}: la richiesta non induce il contenuto che la funzionalità dovrebbe impedire");
            }

            var parsed = Parse(call);
            if (parsed is null)
            {
                return new(feature, FeatureOutcome.Inconclusive, $"risposta non valutabile (finish={call.FinishReason ?? "?"}, HTTP {call.HttpStatus?.ToString() ?? "?"}, JSON non conforme alla forma attesa)");
            }

            var (respected, detail) = check(parsed);
            return new(feature, respected ? FeatureOutcome.Honored : FeatureOutcome.IgnoredByProvider, detail);
        }

        return
        [
            Judge("maxItems:0 su items (NoItems)", noItems, controlItems, "un items non vuoto", p => (p.Items == 0, $"items = {p.Items} elementi (controllo: {parsedControl?.Items})")),
            Judge("type:null su focus (NoItems)", noItems, controlFocus, "un focus oggetto", p => (p.Focus == JsonValueKind.Null, $"focus = {p.Focus} (controllo: {parsedControl?.Focus})")),
            Judge("maxItems:0 su items (Candidate)", candidate, controlItems, "un items non vuoto", p => (p.Items == 0, $"items = {p.Items} elementi (controllo: {parsedControl?.Items})")),
            new("maxItems:0 su patches (NoItems)", FeatureOutcome.NotExercised, "il controllo non induce patch: stesso meccanismo del convertitore di maxItems su items, supporto inferito e NON provato"),
        ];
    }

    /// <summary>Supporto provato: almeno una funzionalità esercitata e tutte quelle esercitate «Honored» (la non esercitata non entra, ma resta dichiarata come non provata nel rapporto).</summary>
    public static bool Proven(IReadOnlyList<FeatureVerdict> verdicts) =>
        verdicts.Any(v => v.Outcome == FeatureOutcome.Honored) && verdicts.Where(v => v.Outcome != FeatureOutcome.NotExercised).All(v => v.Outcome == FeatureOutcome.Honored);
}

/// <summary>
/// VERIFICA REALE (DGX) — NON eseguita senza autorizzazione esplicita. Misura il supporto di <c>maxItems:0</c> e <c>type:null</c> nello schema inviato a llama.cpp (build b10703, router): vedi
/// <see cref="ReconcileSchemaSupportProbe"/>. Esattamente TRE richieste al modello, nessun retry, nessun documento, nessun database; modello fissato come per la campagna (<see cref="RetrievalGapCampaign.LlmModelId"/>).
/// Rapporto in un file NUOVO <c>%TEMP%\bprojects-t040-campaign\schema-support-report-&lt;UTC&gt;.json</c> (schema inviato, stato HTTP, risposta del modello, verdetto per funzionalità), scritto anche in caso di errore;
/// nessuna chiave. Opt-in come ogni test DGX (<c>BPROJECTS_TEST_DGX_ALLOW=1</c>): senza, Skipped. Un esito diverso da «supporto provato» è un FAIL della verifica (funzionalità ignorata, rifiutata o non
/// conclusiva), mai un PASS; non è di per sé un difetto del prodotto: il compositore rifiuta comunque ogni violazione.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
[Trait("Campaign", "SchemaSupport")]
[Collection(RealParser.CollectionName)]
public sealed class ReconcileSchemaSupportRealTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_provider_honors_maxItems_zero_and_type_null_in_the_reconcile_schema_variants()
    {
        var options = TestEnvironment.RequireDgxOptions();   // Skipped senza opt-in, endpoint, chiave o modello: nessuna inferenza
        var problems = RetrievalGapCampaign.ValidateEnvironment(Environment.GetEnvironmentVariable).ToList();   // modello fissato e variabili della campagna
        if (options.StructuredOutputMode != RetrievalGapCampaign.RequiredStructuredOutputMode)
        {
            problems.Add($"StructuredOutputMode {options.StructuredOutputMode}: senza JsonSchema nessuno schema viene inviato e la verifica non avrebbe senso");
        }

        Assert.True(problems.Count == 0, "verifica di supporto dello schema: configurazione incompatibile, nessuna richiesta inviata: " + string.Join(" | ", problems));
        options.Phases.Analysis.EnableThinking = RetrievalGapCampaign.Thinking;   // ragionamento esplicito spento, come la campagna
        options.Phases.Analysis.RequestTimeoutSeconds = 600;                      // la prima richiesta può caricare il modello (router, cold start)
        options.RequestTimeoutSeconds = 600;

        var path = Path.Combine(Path.GetTempPath(), "bprojects-t040-campaign", $"schema-support-report-{DateTime.UtcNow:yyyyMMddTHHmmssfff}Z.json");
        var handler = new ProbeHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        IReadOnlyList<ProbeCall> calls = [];
        IReadOnlyList<FeatureVerdict> verdicts = [];
        try
        {
            using var client = new InternalModelClient(options, handler);
            calls = await ReconcileSchemaSupportProbe.RunAsync(client, handler, Ct);
            verdicts = ReconcileSchemaSupportProbe.Evaluate(calls);
        }
        finally
        {
            Persist(path, handler, calls, verdicts);
        }

        var failing = verdicts.Where(v => v.Outcome is not (FeatureOutcome.Honored or FeatureOutcome.NotExercised)).ToList();
        Assert.True(
            ReconcileSchemaSupportProbe.Proven(verdicts),
            "supporto di maxItems:0 / type:null NON provato: " + string.Join(" | ", failing.Select(v => $"{v.Feature}: {v.Outcome} ({v.Detail})")) + $" (rapporto in {path})");
    }

    private static void Persist(string path, ProbeHandler handler, IReadOnlyList<ProbeCall> calls, IReadOnlyList<FeatureVerdict> verdicts)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var report = new
            {
                scenario = "schema-support",
                writtenAtUtc = DateTimeOffset.UtcNow,
                provider = "llama.cpp b10703 (router): build dichiarata da docs/roadmap/slice-002-technical-closure.md, NON rilevata da questa verifica",
                model = RetrievalGapCampaign.LlmModelId,
                requests = handler.Exchanges.Count,
                calls,
                exchanges = handler.Exchanges.Select(x => new { x.Method, x.Path, x.Status, request = Truncate(x.RequestBody, 60_000), response = Truncate(x.ResponseBody, 4_000) }),
                verdicts = verdicts.Select(v => new { v.Feature, outcome = v.Outcome.ToString(), v.Detail }),
                provenForExercisedFeatures = ReconcileSchemaSupportProbe.Proven(verdicts),
                notProven = verdicts.Where(v => v.Outcome == FeatureOutcome.NotExercised).Select(v => v.Feature),   // MAI coperte da «provenForExercisedFeatures»
            };
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), Encoding.UTF8);
        }
        catch (IOException)
        {
            // diagnostica best-effort: non maschera l'esito della verifica
        }
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max] + "…";
}
