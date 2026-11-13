using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// Verifica MINIMA e ISOLATA, sul provider reale (llama.cpp b10703, router), del solo vincolo <c>candidates.maxItems = 16</c> dello schema Expand del profilo <c>reconcile-alias-schema-by-batch-3</c> (definizione v10),
/// mai misurato con un valore non zero. DUE richieste in tutto (tetto duro di <see cref="MaxRequests"/> = 3, nessun retry), nell'ordine: CONTROLLO con lo schema Expand del profilo -2 (identico al -3 salvo l'assenza del
/// tetto) e VARIANTE con lo schema Expand che il PRODOTTO invia per la v10 (<see cref="ReconcileAliasContracts.ProfileSchemaFor"/>). Il prompt è identico e chiede <see cref="RequestedCandidates"/> candidati distinti, validi,
/// con motivazioni brevi: il controllo, privo del tetto, mostra se la richiesta induce davvero più di 16 candidati; senza questa evidenza ogni esito conforme è «Inconclusive», mai «Honored». Nessun database,
/// nessun documento reale, nessuna campagna, nessun prompt di produzione.
/// </summary>
internal static class ReconcileExpandCapProbe
{
    public const string ControlVariant = "ExpandUncapped";
    public const string CappedVariant = "ExpandCapped";

    public static readonly string[] Variants = [ControlVariant, CappedVariant];

    /// <summary>Tetto di richieste generative dell'intera verifica (autorizzazione: al massimo tre); la verifica ne invia due.</summary>
    public const int MaxRequests = 3;

    /// <summary>Output massimo per chiamata: venti candidati brevi stanno in circa 1.200 token; un troncamento (finish=length) rende la prova non conclusiva, mai un successo.</summary>
    public const int MaxOutputTokens = 2048;

    public const int Cap = ReconcileAliasContracts.ExpansionCandidateCap;

    public const int RequestedCandidates = 20;

    /// <summary>Hash canonico congelato dello schema Expand del profilo -3 (golden <c>ReconcileSchemaProfileV10Tests</c>); indipendente dal codice di produzione.</summary>
    public const string CappedFrozenHash = "02490207aef819a90c708468ec75a43439695af6863bcc762fdf8fe49e45d89b";

    public const string SystemPrompt =
        "Sei un verificatore di formato. Rispondi SOLO con un oggetto JSON conforme allo schema fornito, senza testo prima o dopo.";

    private static readonly (int Left, int Right)[] Pairs =
    [
        (1, 2), (1, 3), (1, 4), (1, 5), (1, 6), (1, 7), (1, 8),
        (2, 3), (2, 4), (2, 5), (2, 6), (2, 7), (2, 8),
        (3, 4), (3, 5), (3, 6), (3, 7), (3, 8),
        (4, 5), (4, 6),
    ];

    /// <summary>Identico per le due richieste. Elenca esplicitamente le venti coppie da trasformare in candidati (f1…f20): l'enumerazione è il comportamento che il tetto di 16 deve impedire.</summary>
    public static readonly string UserPrompt =
        "Prova di formato, nessun contenuto reale. Evidenze del batch: E1 = «Il pagamento avviene entro 30 giorni dalla fattura.»; E2 = «Il pagamento avviene entro 60 giorni dalla fattura.»; "
        + "E3 = «La consegna avviene entro 10 giorni lavorativi.»; E4 = «La consegna avviene entro 20 giorni lavorativi.»; E5 = «La garanzia dura 12 mesi.»; E6 = «La garanzia dura 24 mesi.»; "
        + "E7 = «Il canone mensile è di 100 euro.»; E8 = «Il canone mensile è di 150 euro.». "
        + $"Compila la risposta ESATTAMENTE così: contract = reconcile-alias-1; descriptor e noFindings null; items: []; candidates: {RequestedCandidates} candidati, uno per ciascuna di queste coppie, nell'ordine dato, "
        + $"con key f1, f2, … f{RequestedCandidates}: {string.Join(", ", Pairs.Select((p, i) => $"f{i + 1} = (E{p.Left}, E{p.Right})"))}; "
        + "per ciascuno type = Confirms oppure Contradicts (scegli tu), basis = Inferred e rationale = al massimo cinque parole, diversa per ogni coppia; focus: null; patches: []. "
        + $"Non fermarti prima di f{RequestedCandidates}.";

    private static readonly Regex EvidenceEndpoint = new(AnalysisAliasPattern.Evidence, RegexOptions.CultureInvariant);
    private static readonly Regex FindingKey = new(AnalysisAliasPattern.FindingKey, RegexOptions.CultureInvariant);
    private static readonly string[] ProposedTypes = Enum.GetNames<ReconcileProposedType>();
    private static readonly string[] Bases = Enum.GetNames<EvidenceRelationBasis>();

    public const int MaxRationaleChars = 120;

    /// <summary>Schema che la variante deve inviare. Il controllo è l'Expand del profilo -2; la variante è quello che il prodotto sceglie per lo stadio «expand» nel profilo -3 (percorso reale di dispatch).</summary>
    public static JsonElement SchemaOf(string variant) =>
        variant == ControlVariant
            ? ReconcileAliasContracts.SchemaOf(ReconcileBatchSchema.Expand)
            : ReconcileAliasContracts.ProfileSchemaFor(ReconcileAliasContracts.SchemaProfileByBatchV3, "expand", ReconcileView.A).Schema;

    /// <summary>Hash canonico (RFC 8785, SHA-256) dello schema che il PRODOTTO produce oggi per la variante.</summary>
    public static string SchemaHash(string variant) => CanonicalJson.HashText(CanonicalJson.Canonicalize(SchemaOf(variant).GetRawText()));

    /// <summary>Hash CONGELATO della variante: per il controllo l'Expand del golden v9, per la variante quello del golden v10. Indipendente dal prodotto: una deriva dello schema lo rende diverso da <see cref="SchemaHash"/>.</summary>
    public static string FrozenHash(string variant) =>
        variant == ControlVariant
            ? ReconcileSchemaProfileV9Tests.PinnedV9ProfileHashes.Split(',').Select(p => p.Split('=')).Single(p => p[0] == nameof(ReconcileBatchSchema.Expand))[1]
            : CappedFrozenHash;

    /// <summary>
    /// Invia UNA richiesta per variante con le opzioni di richiesta del PRODOTTO (<see cref="InternalModelClient.CreateChatOptions"/>: stesso <c>response_format</c> json_schema grezzo, fase Analysis, temperatura 0),
    /// nessun retry e tetto duro di <see cref="MaxRequests"/> nel <see cref="RequestCapHandler"/> a monte. Un errore HTTP è registrato e la variante successiva parte comunque (richieste indipendenti).
    /// </summary>
    public static async Task<IReadOnlyList<ProbeCall>> RunAsync(InternalModelClient client, ProbeHandler handler, CancellationToken cancellationToken)
    {
        var calls = new List<ProbeCall>();
        foreach (var variant in Variants)
        {
            var before = handler.Exchanges.Count;
            var options = client.CreateChatOptions(SchemaOf(variant), ReconcileAliasContracts.SchemaName, temperature: 0, phase: AiPhase.Analysis);
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
            catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                error = e.GetType().Name;   // solo il tipo: stato e corpo d'errore del server sono nello scambio HTTP registrato
            }

            var exchange = handler.Exchanges.Skip(before).LastOrDefault(x => x.Method == "POST");
            calls.Add(new ProbeCall(variant, exchange?.Status, finish, content, error, exchange is null ? null : ReconcileSchemaSupportProbe.RequestSchemaHash(exchange.RequestBody)));
        }

        return calls;
    }

    /// <summary>Token dichiarati dal provider nel corpo della risposta (<c>usage</c>); null se assenti o il corpo non è JSON.</summary>
    public static (int? Prompt, int? Completion, int? Total) Usage(string? responseBody)
    {
        if (responseBody is null)
        {
            return (null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null);
            }

            int? Read(string name) => usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
            return (Read("prompt_tokens"), Read("completion_tokens"), Read("total_tokens"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    /// <summary>Schema effettivamente presente nel <c>response_format</c> del corpo inviato (testo grezzo), per il rapporto; null se assente.</summary>
    public static JsonElement? SentSchema(string requestBody) => ReconcileSchemaV9Probe.SentSchema(requestBody);

    /// <summary>Candidati letti dalla risposta: lunghezza dell'array e quanti sono validi e distinti (chiave e coppia non ordinata mai viste).</summary>
    internal sealed record Parsed(int ArrayLength, int ValidDistinct, int Invalid, IReadOnlyList<string> Pairs);

    /// <summary>Forma minima valutabile: oggetto con <c>candidates</c> array di oggetti. Troncamento, errore, HTTP non 2xx, contenuto assente o non JSON ⇒ null (mai un successo).</summary>
    internal static Parsed? Parse(ProbeCall call)
    {
        if (call.HttpStatus is not (>= 200 and < 300) || call.Error is not null || call.Content is null || !string.Equals(call.FinishReason, "stop", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(call.Content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var pairs = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            var validDistinct = 0;
            var invalid = 0;
            var length = 0;
            foreach (var candidate in candidates.EnumerateArray())
            {
                length++;
                if (!TryValid(candidate, out var key, out var pair))
                {
                    invalid++;
                    continue;
                }

                ordered.Add(pair);
                if (keys.Add(key) & pairs.Add(pair))
                {
                    validDistinct++;
                }
            }

            return new Parsed(length, validDistinct, invalid, ordered);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryValid(JsonElement candidate, out string key, out string pair)
    {
        key = pair = string.Empty;
        if (candidate.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? Text(string name) => candidate.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var (k, left, right, type, basis, rationale) = (Text("key"), Text("left"), Text("right"), Text("type"), Text("basis"), Text("rationale"));
        if (k is null || left is null || right is null || type is null || basis is null || rationale is null
            || !FindingKey.IsMatch(k) || !EvidenceEndpoint.IsMatch(left) || !EvidenceEndpoint.IsMatch(right) || left == right
            || !ProposedTypes.Contains(type, StringComparer.Ordinal) || !Bases.Contains(basis, StringComparer.Ordinal)
            || string.IsNullOrWhiteSpace(rationale) || rationale.Length > MaxRationaleChars)
        {
            return false;
        }

        key = k;
        pair = string.CompareOrdinal(left, right) <= 0 ? $"{left}|{right}" : $"{right}|{left}";   // coppia NON ordinata
        return true;
    }

    /// <summary>Rifiuto dello schema = errore di richiesta (400/422) del provider. Altri errori (401, 429, 5xx, timeout, nessuna risposta) non dicono nulla sullo schema: sono non conclusivi, mai «rifiutato».</summary>
    private static bool Rejected(ProbeCall call) => call.HttpStatus is 400 or 422;

    private static bool Failed(ProbeCall call) => call.Error is not null || call.HttpStatus is not (>= 200 and < 300);

    /// <summary>Lo schema REALMENTE inviato è quello congelato per la variante (altrimenti la risposta non dice nulla sulla variante).</summary>
    internal static bool SentFrozenSchema(ProbeCall call) => call.RequestSchemaHash is { } hash && string.Equals(hash, FrozenHash(call.Variant), StringComparison.Ordinal);

    public const string AcceptedFeature = "schema Expand v10 accettato dal provider (nessun 400/422)";
    public const string CapFeature = "candidates.maxItems:16 — Expand";

    private static string Describe(ProbeCall call) => $"HTTP {call.HttpStatus?.ToString() ?? "nessuno"}, finish={call.FinishReason ?? "?"}{(call.Error is null ? string.Empty : ", " + call.Error)}";

    /// <summary>
    /// Verdetti: funzione PURA sulle due chiamate. «Honored» SOLO se (1) lo schema nel corpo di ciascuna richiesta è quello congelato, (2) entrambe sono andate a buon fine con finish=stop (nessun troncamento),
    /// (3) il CONTROLLO ha prodotto PIÙ di <see cref="Cap"/> candidati validi e distinti (la richiesta esercita il vincolo) e (4) la variante vincolata ha chiuso l'array a ESATTAMENTE <see cref="Cap"/> candidati validi.
    /// Una variante con più di 16 candidati ⇒ «IgnoredByProvider»; con meno di 16 ⇒ «Inconclusive» (il tetto non è stato raggiunto: non dimostra nulla). Un 400/422 della variante è «RejectedByProvider» solo se il controllo
    /// è stato accettato; ogni altro errore o troncamento è «Inconclusive» (errore del modello o dell'infrastruttura, non dello schema).
    /// </summary>
    public static IReadOnlyList<FeatureVerdict> Evaluate(IReadOnlyList<ProbeCall> calls)
    {
        if (calls.Count != Variants.Length || Variants.Any(v => calls.Count(c => c.Variant == v) != 1))
        {
            return [new(CapFeature, FeatureOutcome.Inconclusive, "servono esattamente le due chiamate (controllo senza tetto, variante con tetto), una per variante")];
        }

        var control = calls.Single(c => c.Variant == ControlVariant);
        var capped = calls.Single(c => c.Variant == CappedVariant);
        var controlSucceeded = !Failed(control);

        FeatureVerdict Accepted()
        {
            if (!SentFrozenSchema(capped))
            {
                return new(AcceptedFeature, FeatureOutcome.Inconclusive, "lo schema nel corpo della richiesta non e quello congelato per la variante (o non e stato inviato)");
            }

            if (Rejected(capped))
            {
                return controlSucceeded
                    ? new(AcceptedFeature, FeatureOutcome.RejectedByProvider, $"il provider ha rifiutato lo schema con tetto ({Describe(capped)}) mentre il controllo senza tetto e stato accettato ({Describe(control)}): rifiuto dello schema, non errore del modello")
                    : new(AcceptedFeature, FeatureOutcome.Inconclusive, $"HTTP {capped.HttpStatus} ma anche il controllo non e andato a buon fine ({Describe(control)}): il rifiuto non e attribuibile al tetto");
            }

            return Failed(capped)
                ? new(AcceptedFeature, FeatureOutcome.Inconclusive, $"la richiesta non e andata a buon fine per un motivo non riconducibile allo schema ({Describe(capped)})")
                : new(AcceptedFeature, FeatureOutcome.Honored, $"schema con tetto accettato ({Describe(capped)})");
        }

        FeatureVerdict Cap16()
        {
            if (!SentFrozenSchema(control) || !SentFrozenSchema(capped))
            {
                return new(CapFeature, FeatureOutcome.Inconclusive, "lo schema nel corpo di almeno una richiesta non e quello congelato per la sua variante (o non e stato inviato): la risposta non dice nulla sul tetto");
            }

            if (Rejected(capped))
            {
                return controlSucceeded
                    ? new(CapFeature, FeatureOutcome.RejectedByProvider, $"il provider ha rifiutato lo schema (HTTP {capped.HttpStatus}); il controllo e stato accettato: la causa e lo schema, non il modello")
                    : new(CapFeature, FeatureOutcome.Inconclusive, $"HTTP {capped.HttpStatus} ma anche il controllo non e andato a buon fine ({Describe(control)}): rifiuto non attribuibile al tetto");
            }

            if (Failed(control) || Failed(capped))
            {
                return new(CapFeature, FeatureOutcome.Inconclusive, $"richiesta non andata a buon fine per un motivo non riconducibile allo schema (controllo: {Describe(control)}; variante: {Describe(capped)})");
            }

            var parsedControl = Parse(control);
            if (parsedControl is null)
            {
                return new(CapFeature, FeatureOutcome.Inconclusive, $"risposta del controllo non valutabile ({Describe(control)}): un output troncato o non conforme non e un successo e non esercita il vincolo");
            }

            if (parsedControl.ValidDistinct <= Cap)
            {
                return new(CapFeature, FeatureOutcome.Inconclusive, $"il controllo senza tetto ha prodotto {parsedControl.ValidDistinct} candidati validi e distinti (su {parsedControl.ArrayLength}, {parsedControl.Invalid} non validi): "
                    + $"non dimostra la capacita di superare {Cap}, quindi la richiesta non esercita il vincolo");
            }

            var parsedCapped = Parse(capped);
            if (parsedCapped is null)
            {
                return new(CapFeature, FeatureOutcome.Inconclusive, $"risposta della variante non valutabile ({Describe(capped)}): errore del modello o troncamento, non un esito sul tetto");
            }

            var detail = $"controllo: {parsedControl.ArrayLength} candidati ({parsedControl.ValidDistinct} validi e distinti); variante: {parsedCapped.ArrayLength} candidati ({parsedCapped.ValidDistinct} validi e distinti, {parsedCapped.Invalid} non validi)";
            if (parsedCapped.ArrayLength > Cap)
            {
                return new(CapFeature, FeatureOutcome.IgnoredByProvider, $"{detail}: il provider ha superato il tetto di {Cap}");
            }

            if (parsedCapped.ArrayLength < Cap)
            {
                return new(CapFeature, FeatureOutcome.Inconclusive, $"{detail}: la variante si e fermata sotto il tetto, che non e stato raggiunto");
            }

            return parsedCapped.Invalid == 0 && parsedCapped.ValidDistinct == Cap
                ? new(CapFeature, FeatureOutcome.Honored, $"{detail}: il controllo supera {Cap} e la variante chiude l'array a {Cap} con finish=stop")
                : new(CapFeature, FeatureOutcome.Inconclusive, $"{detail}: l'array e chiuso a {Cap} ma non tutti i candidati sono validi e distinti, il tetto non e dimostrato in modo pulito");
        }

        return [Accepted(), Cap16()];
    }

    /// <summary>Provato: ENTRAMBI i verdetti Honored (schema accettato e tetto dimostrato dal controllo).</summary>
    public static bool Proven(IReadOnlyList<FeatureVerdict> verdicts) => verdicts.Count == 2 && verdicts.All(v => v.Outcome == FeatureOutcome.Honored);
}

/// <summary>
/// VERIFICA REALE (DGX) — NON eseguita senza autorizzazione esplicita. Misura se llama.cpp b10703 (router) rispetta <c>candidates.maxItems = 16</c> dello schema Expand del profilo -3: vedi
/// <see cref="ReconcileExpandCapProbe"/>. Esattamente DUE richieste al modello (tetto duro 3), nessun retry, nessun documento, nessun database; modello fissato come per la campagna. Rapporto in un file NUOVO
/// <c>%TEMP%\bprojects-t040-campaign\expand-cap-support-report-&lt;UTC&gt;.json</c> (schema inviato, hash atteso e inviato, HTTP, finish_reason, output grezzo, token, verdetti), scritto anche in caso di errore; nessuna chiave.
/// Opt-in come ogni test DGX (<c>BPROJECTS_TEST_DGX_ALLOW=1</c>): senza, Skipped. Un esito diverso da «tetto provato» è un FAIL della verifica, mai un PASS.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
[Trait("Campaign", "ExpandCapSupport")]
[Collection(RealParser.CollectionName)]
public sealed class ReconcileExpandCapSupportRealTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_provider_honors_the_v10_expansion_candidates_cap_of_16()
    {
        var options = TestEnvironment.RequireDgxOptions();   // Skipped senza opt-in, endpoint, chiave o modello: nessuna inferenza
        var problems = RetrievalGapCampaign.ValidateEnvironment(Environment.GetEnvironmentVariable).ToList();
        if (options.StructuredOutputMode != RetrievalGapCampaign.RequiredStructuredOutputMode)
        {
            problems.Add($"StructuredOutputMode {options.StructuredOutputMode}: senza JsonSchema nessuno schema viene inviato e la verifica non avrebbe senso");
        }

        Assert.True(problems.Count == 0, "verifica del tetto maxItems:16: configurazione incompatibile, nessuna richiesta inviata: " + string.Join(" | ", problems));
        options.Phases.Analysis.EnableThinking = RetrievalGapCampaign.Thinking;
        options.Phases.Analysis.RequestTimeoutSeconds = 600;
        options.RequestTimeoutSeconds = 600;

        var path = Path.Combine(Path.GetTempPath(), "bprojects-t040-campaign", $"expand-cap-support-report-{DateTime.UtcNow:yyyyMMddTHHmmssfff}Z.json");
        var probe = new ProbeHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        var cap = new RequestCapHandler(probe, ReconcileExpandCapProbe.MaxRequests);
        IReadOnlyList<ProbeCall> calls = [];
        IReadOnlyList<FeatureVerdict> verdicts = [];
        try
        {
            using var client = new InternalModelClient(options, cap);
            calls = await ReconcileExpandCapProbe.RunAsync(client, probe, Ct);
            verdicts = ReconcileExpandCapProbe.Evaluate(calls);
        }
        finally
        {
            Persist(path, probe, cap, calls, verdicts);
            cap.Dispose();
        }

        Assert.True(
            ReconcileExpandCapProbe.Proven(verdicts),
            "tetto maxItems:16 NON provato: " + string.Join(" | ", verdicts.Select(v => $"{v.Feature}: {v.Outcome} ({v.Detail})")) + $" (rapporto in {path})");
    }

    private static void Persist(string path, ProbeHandler probe, RequestCapHandler cap, IReadOnlyList<ProbeCall> calls, IReadOnlyList<FeatureVerdict> verdicts)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var report = new
            {
                scenario = "expand-cap-support",
                writtenAtUtc = DateTimeOffset.UtcNow,
                provider = "llama.cpp b10703 (router): build dichiarata da docs/roadmap/slice-002-technical-closure.md, NON rilevata da questa verifica",
                model = RetrievalGapCampaign.LlmModelId,
                requestBudget = ReconcileExpandCapProbe.MaxRequests,
                requestsSent = probe.Exchanges.Count,
                requestsAttempted = cap.Attempted,
                temperature = 0,
                maxOutputTokens = ReconcileExpandCapProbe.MaxOutputTokens,
                systemPrompt = ReconcileExpandCapProbe.SystemPrompt,
                userPrompt = ReconcileExpandCapProbe.UserPrompt,
                perCall = calls.Select((c, i) =>
                {
                    var exchange = i < probe.Exchanges.Count ? probe.Exchanges[i] : null;
                    var expected = ReconcileExpandCapProbe.FrozenHash(c.Variant);
                    var usage = ReconcileExpandCapProbe.Usage(exchange?.ResponseBody);
                    var parsed = ReconcileExpandCapProbe.Parse(c);
                    return new
                    {
                        c.Variant,
                        expectedFrozenHash = expected,
                        sentSchemaHash = c.RequestSchemaHash,
                        hashMatches = string.Equals(c.RequestSchemaHash, expected, StringComparison.Ordinal),
                        httpStatus = c.HttpStatus,
                        finishReason = c.FinishReason,
                        promptTokens = usage.Prompt,
                        completionTokens = usage.Completion,
                        totalTokens = usage.Total,
                        candidateArrayLength = parsed?.ArrayLength,
                        validDistinctCandidates = parsed?.ValidDistinct,
                        rawOutput = c.Content,
                        error = c.Error,
                        sentSchema = exchange is null ? (JsonElement?)null : ReconcileExpandCapProbe.SentSchema(exchange.RequestBody),
                    };
                }),
                exchanges = probe.Exchanges.Select(x => new { x.Method, x.Path, x.Status, request = Truncate(x.RequestBody, 60_000), response = Truncate(x.ResponseBody, 16_000) }),
                verdicts = verdicts.Select(v => new { v.Feature, outcome = v.Outcome.ToString(), v.Detail }),
                provenCap16 = ReconcileExpandCapProbe.Proven(verdicts),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            // diagnostica best-effort: non maschera l'esito della verifica né l'eccezione originale
        }
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max] + "…";
}
