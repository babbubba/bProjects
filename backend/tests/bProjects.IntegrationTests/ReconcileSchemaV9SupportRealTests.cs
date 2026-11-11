using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// Tetto DURO di richieste HTTP: la (n+1)-esima viene rifiutata PRIMA di uscire dalla macchina (eccezione), a prescindere da retry, ripetizioni o difetti del chiamante. Va messo all'ESTERNO dell'osservatore
/// (<see cref="ProbeHandler"/>), così una richiesta bloccata non compare fra gli scambi registrati. Non altera <see cref="ProbeHandler"/>, che resta quello della verifica storica.
/// </summary>
internal sealed class RequestCapHandler(HttpMessageHandler inner, int max) : DelegatingHandler(inner)
{
    private int _attempted;

    /// <summary>Richieste tentate dal client, comprese quelle bloccate dal tetto.</summary>
    public int Attempted => Volatile.Read(ref _attempted);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Interlocked.Increment(ref _attempted) > max
            ? throw new InvalidOperationException($"tetto di {max} richieste superato: la richiesta non e stata inviata")
            : base.SendAsync(request, cancellationToken);
}

/// <summary>
/// Verifica MINIMA e ISOLATA, sul provider reale (llama.cpp b10703, router), dei vincoli strutturali del profilo di schema <c>reconcile-alias-schema-by-batch-2</c> (definizione v9) che la verifica reale della v8
/// NON ha esercitato: <c>descriptor</c> <c>{"type":"string","minLength":1}</c> (foglie e riduzione) contro <c>{"type":"null"}</c> (cross, espansione, confronto) e il prefisso degli estremi dei candidati
/// (<c>^E…</c> nelle foglie, <c>^N…</c> nelle riduzioni e nel cross). QUATTRO richieste in tutto, in quest'ordine e mai ripetute: CONTROLLO con lo schema completo <c>reconcile-alias-1</c> (invariato) e lo stesso prompt
/// delle altre, poi LeafB, Reduce, Cross. Il prompt è costruito per INDURRE il comportamento vietato in ogni variante (descriptor stringa vuota, un candidato con estremi E e uno con estremi N): il controllo, privo
/// dei vincoli, mostra se la richiesta riesce a indurlo; senza questa evidenza ogni esito conforme è «Inconclusive», mai «Honored». Nessun database, nessun documento, nessuna campagna, nessun prompt di produzione.
/// </summary>
internal static class ReconcileSchemaV9Probe
{
    public const string ControlVariant = "Full";

    public static readonly string[] Variants = [ControlVariant, nameof(ReconcileBatchSchema.LeafB), nameof(ReconcileBatchSchema.Reduce), nameof(ReconcileBatchSchema.Cross)];

    /// <summary>Tetto di richieste generative dell'intera verifica.</summary>
    public const int MaxRequests = 4;

    /// <summary>Output massimo per chiamata: l'output atteso è di poche centinaia di token; un troncamento (finish=length) rende la prova non conclusiva, non un PASS.</summary>
    public const int MaxOutputTokens = 1024;

    public const string SystemPrompt =
        "Sei un verificatore di formato. Rispondi SOLO con un oggetto JSON conforme allo schema fornito, senza testo prima o dopo.";

    /// <summary>
    /// Identico per tutte le richieste. Induce, in OGNI variante, un comportamento che lo schema di quella variante vieta: <c>descriptor</c> = stringa vuota (vietata in LeafB e Reduce per <c>minLength:1</c>, vietata
    /// in Cross perché non null) e due candidati, uno con estremi E (vietati in Reduce e Cross) e uno con estremi N (vietati in LeafB). Il formato di Cross, LeafB e Reduce è altrimenti compatibile con la richiesta:
    /// <c>items</c> e <c>patches</c> vuoti e <c>focus</c> null sono ammessi da tutte.
    /// </summary>
    public const string UserPrompt =
        "Prova di formato, nessun contenuto reale. Il batch contiene le evidenze E1 = «Il pagamento avviene entro 30 giorni.» ed E2 = «Il pagamento avviene entro 60 giorni.» e i nodi N1 = «Condizioni di pagamento» "
        + "ed N2 = «Termini di consegna». Compila la risposta ESATTAMENTE cosi: contract = reconcile-alias-1; descriptor = la stringa vuota \"\" (due virgolette senza alcun carattere in mezzo); noFindings null; "
        + "items: []; candidates: DUE candidati, f1 fra E1 ed E2 e f2 fra N1 ed N2, ciascuno con type, basis e rationale ammessi (scegli i valori degli enum fra quelli ammessi dallo schema); focus null; patches: [].";

    private static readonly Regex EvidenceEndpoint = new(AnalysisAliasPattern.Evidence, RegexOptions.CultureInvariant);
    private static readonly Regex NodeEndpoint = new(ReconcileAliasContracts.NodeAliasPattern, RegexOptions.CultureInvariant);

    /// <summary>Schema che la variante deve inviare: il completo per il controllo, la variante v9 per le altre. Calcolato dal prodotto; l'identità congelata è <see cref="FrozenHash"/>.</summary>
    public static JsonElement SchemaOf(string variant) =>
        variant == ControlVariant ? ReconcileAliasContracts.JsonSchema() : ReconcileAliasContracts.SchemaOf(Enum.Parse<ReconcileBatchSchema>(variant));

    /// <summary>Hash canonico (RFC 8785, SHA-256) dello schema che il PRODOTTO produce oggi per la variante.</summary>
    public static string SchemaHash(string variant) => CanonicalJson.HashText(CanonicalJson.Canonicalize(SchemaOf(variant).GetRawText()));

    /// <summary>
    /// Hash CONGELATO della variante: per il controllo quello dello schema completo (<see cref="RetrievalGapCampaign.FullSchemaHash"/>, invariato dal manifesto T040 v4), per le varianti v9 il golden del profilo
    /// (<see cref="ReconcileSchemaProfileV9Tests.PinnedV9ProfileHashes"/>). Indipendente dal codice di produzione: una deriva dello schema lo rende diverso da <see cref="SchemaHash"/>.
    /// </summary>
    public static string FrozenHash(string variant) =>
        variant == ControlVariant
            ? RetrievalGapCampaign.FullSchemaHash
            : ReconcileSchemaProfileV9Tests.PinnedV9ProfileHashes.Split(',').Select(p => p.Split('=')).Single(p => p[0] == variant)[1];

    /// <summary>
    /// Invia UNA richiesta per variante, con le opzioni di richiesta del PRODOTTO (<see cref="InternalModelClient.CreateChatOptions"/>: stesso <c>response_format</c> json_schema grezzo, fase Analysis, temperatura 0),
    /// nessun retry (il client è configurato con zero tentativi aggiuntivi) e un tetto duro di <see cref="MaxRequests"/> nel <see cref="RequestCapHandler"/> a monte. Un errore HTTP è registrato e la variante successiva
    /// parte comunque (richieste indipendenti, ciascuna al più una volta): il rifiuto di una variante non dipende dal controllo.
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
            catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))   // un timeout del client è un errore della variante, non l'annullamento del test
            {
                error = e.GetType().Name;   // solo il tipo: stato e corpo d'errore del server sono nello scambio HTTP registrato
            }

            var exchange = handler.Exchanges.Skip(before).LastOrDefault(x => x.Method == "POST");
            calls.Add(new ProbeCall(variant, exchange?.Status, finish, content, error, exchange is null ? null : ReconcileSchemaSupportProbe.RequestSchemaHash(exchange.RequestBody)));
        }

        return calls;
    }

    /// <summary>Schema effettivamente presente nel <c>response_format</c> del corpo inviato (testo grezzo), per il rapporto; null se assente.</summary>
    public static JsonElement? SentSchema(string requestBody)
    {
        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return document.RootElement.TryGetProperty("response_format", out var format) && format.TryGetProperty("json_schema", out var js) && js.TryGetProperty("schema", out var schema)
                ? schema.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal enum DescriptorKind { Null, Empty, Text }

    internal sealed record Parsed(DescriptorKind Descriptor, IReadOnlyList<string> Endpoints);

    /// <summary>Forma minima valutabile: oggetto con <c>descriptor</c> stringa o null e <c>candidates</c> array di oggetti con <c>left</c> e <c>right</c> stringa. Altro (o troncamento, errore, HTTP non 2xx) ⇒ null.</summary>
    internal static Parsed? Parse(ProbeCall call)
    {
        if (call.HttpStatus is not (>= 200 and < 300) || call.Error is not null || call.Content is null || string.Equals(call.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(call.Content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("descriptor", out var descriptor) || !root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            DescriptorKind? kind = descriptor.ValueKind switch
            {
                JsonValueKind.Null => DescriptorKind.Null,
                JsonValueKind.String => descriptor.GetString()!.Length == 0 ? DescriptorKind.Empty : DescriptorKind.Text,
                _ => null,
            };
            var endpoints = new List<string>();
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object || !candidate.TryGetProperty("left", out var left) || left.ValueKind != JsonValueKind.String
                    || !candidate.TryGetProperty("right", out var right) || right.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                endpoints.Add(left.GetString()!);
                endpoints.Add(right.GetString()!);
            }

            return kind is { } k ? new Parsed(k, endpoints) : null;
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
    internal static bool SentFrozenSchema(ProbeCall call) => call.RequestSchemaHash is { } hash && string.Equals(hash, FrozenHash(call.Variant), StringComparison.Ordinal);

    private static bool IsE(string endpoint) => EvidenceEndpoint.IsMatch(endpoint);

    private static bool IsN(string endpoint) => NodeEndpoint.IsMatch(endpoint);

    /// <summary>
    /// Verdetti per funzionalità: funzione PURA sulle quattro chiamate. «Honored» solo se (1) lo schema nel corpo è quello congelato, (2) la richiesta è andata a buon fine e la risposta è valutabile, (3) il CONTROLLO
    /// (schema completo, stesso prompt) ha mostrato il comportamento che la funzionalità dovrebbe impedire, (4) la variante non lo ha tenuto. Senza (3) l'esito è «Inconclusive»; ciò che la richiesta non induce per
    /// costruzione, o che non è stato inviato, è «NotExercised» e non è mai coperto dal «provato».
    /// </summary>
    public static IReadOnlyList<FeatureVerdict> Evaluate(IReadOnlyList<ProbeCall> calls)
    {
        if (calls.Count != Variants.Length || Variants.Any(v => calls.Count(c => c.Variant == v) != 1))
        {
            return [new("verifica", FeatureOutcome.Inconclusive, "servono esattamente le quattro chiamate (controllo, LeafB, Reduce, Cross), una per variante")];
        }

        var control = calls.Single(c => c.Variant == ControlVariant);
        var controlSucceeded = !Failed(control);
        var parsedControl = SentFrozenSchema(control) ? Parse(control) : null;
        var controlDescriptor = parsedControl?.Descriptor.ToString() ?? "non valutabile";
        var controlHasN = parsedControl?.Endpoints.Any(IsN) == true;
        var controlHasE = parsedControl?.Endpoints.Any(IsE) == true;

        // check: true = vincolo rispettato, false = violato, null = risposta non valutabile per questa funzionalità.
        FeatureVerdict Judge(string feature, string variant, bool controlInduces, string controlNote, Func<Parsed, (bool? Respected, string Detail)> check, bool notExercisedWithoutControl = false)
        {
            var call = calls.Single(c => c.Variant == variant);
            if (!SentFrozenSchema(call))
            {
                return new(feature, FeatureOutcome.Inconclusive, "lo schema nel corpo della richiesta non e quello congelato per la variante (o non e stato inviato): la risposta non dice nulla sulla variante");
            }

            if (Rejected(call))
            {
                // Un 400/422 è attribuibile allo schema solo se lo schema completo (controllo, stessa richiesta e stesso modello) è stato accettato: altrimenti il rifiuto può dipendere da modello, limiti o contesto.
                return controlSucceeded
                    ? new(feature, FeatureOutcome.RejectedByProvider, $"il provider ha rifiutato lo schema (HTTP {call.HttpStatus}, {call.Error ?? "errore di richiesta"})")
                    : new(feature, FeatureOutcome.Inconclusive, $"HTTP {call.HttpStatus} ma anche il controllo con lo schema completo non e andato a buon fine (HTTP {control.HttpStatus?.ToString() ?? "nessuno"}): il rifiuto non e attribuibile allo schema della variante");
            }

            if (Failed(call))
            {
                return new(feature, FeatureOutcome.Inconclusive, $"la richiesta non e andata a buon fine per un motivo non riconducibile allo schema (HTTP {call.HttpStatus?.ToString() ?? "nessuno"}, {call.Error ?? "errore"})");
            }

            if (!controlInduces)
            {
                var note = $"il controllo con lo schema completo non ha mostrato {controlNote} (descriptor del controllo: {controlDescriptor}): la richiesta non induce il comportamento che il vincolo dovrebbe impedire";
                return new(feature, notExercisedWithoutControl ? FeatureOutcome.NotExercised : FeatureOutcome.Inconclusive, note);
            }

            var parsed = Parse(call);
            if (parsed is null)
            {
                return new(feature, FeatureOutcome.Inconclusive, $"risposta non valutabile (finish={call.FinishReason ?? "?"}, HTTP {call.HttpStatus?.ToString() ?? "?"}, JSON non conforme alla forma attesa)");
            }

            var (respected, detail) = check(parsed);
            return respected switch
            {
                true => new(feature, FeatureOutcome.Honored, detail),
                false => new(feature, FeatureOutcome.IgnoredByProvider, detail),
                null => new(feature, FeatureOutcome.Inconclusive, detail),
            };
        }

        (bool?, string) NonEmpty(Parsed p) => (p.Descriptor == DescriptorKind.Text, $"descriptor = {p.Descriptor} (controllo: {controlDescriptor})");
        (bool?, string) NonNull(Parsed p) => (p.Descriptor != DescriptorKind.Null, $"descriptor = {p.Descriptor} (controllo: {controlDescriptor})");
        (bool?, string) IsNull(Parsed p) => (p.Descriptor == DescriptorKind.Null, $"descriptor = {p.Descriptor} (controllo: {controlDescriptor})");

        (bool?, string) EndpointsOnly(Parsed p, Func<string, bool> allowed, string prefix) => p.Endpoints.Count == 0
            ? (null, "nessun candidato nella risposta: gli estremi non sono valutabili (un array vuoto non dimostra il vincolo)")
            : (p.Endpoints.All(allowed), $"estremi = [{string.Join(", ", p.Endpoints)}], tutti {prefix}: {p.Endpoints.All(allowed)} (controllo: [{string.Join(", ", parsedControl?.Endpoints ?? [])}])");

        const string NoDescriptorNull = "descriptor != null (type:string)";
        return
        [
            Judge("descriptor != \"\" (minLength:1) — LeafB", nameof(ReconcileBatchSchema.LeafB), parsedControl is { Descriptor: DescriptorKind.Empty }, "un descriptor stringa vuota", NonEmpty),
            Judge("descriptor != \"\" (minLength:1) — Reduce", nameof(ReconcileBatchSchema.Reduce), parsedControl is { Descriptor: DescriptorKind.Empty }, "un descriptor stringa vuota", NonEmpty),
            Judge("descriptor == null (type:null) — Cross", nameof(ReconcileBatchSchema.Cross), parsedControl is { Descriptor: DescriptorKind.Empty or DescriptorKind.Text }, "un descriptor non null", IsNull),
            Judge("estremi ^E (pattern) — LeafB", nameof(ReconcileBatchSchema.LeafB), controlHasN, "un estremo N (vietato nelle foglie)", p => EndpointsOnly(p, IsE, "E")),
            Judge("estremi ^N (pattern) — Reduce", nameof(ReconcileBatchSchema.Reduce), controlHasE, "un estremo E (vietato nelle riduzioni)", p => EndpointsOnly(p, IsN, "N")),
            Judge("estremi ^N (pattern) — Cross", nameof(ReconcileBatchSchema.Cross), controlHasE, "un estremo E (vietato nel cross)", p => EndpointsOnly(p, IsN, "N")),
            // Esercitato solo se il controllo ha prodotto null (la richiesta induce la stringa vuota, non null): altrimenti NotExercised e inferito da type:string, MAI dichiarato provato.
            Judge($"{NoDescriptorNull} — LeafB", nameof(ReconcileBatchSchema.LeafB), parsedControl is { Descriptor: DescriptorKind.Null }, "un descriptor null", NonNull, notExercisedWithoutControl: true),
            Judge($"{NoDescriptorNull} — Reduce", nameof(ReconcileBatchSchema.Reduce), parsedControl is { Descriptor: DescriptorKind.Null }, "un descriptor null", NonNull, notExercisedWithoutControl: true),
            .. NotSent,
        ];
    }

    /// <summary>Funzionalità che questa verifica NON esercita per costruzione (limite di quattro richieste o responsabilità del validatore): dichiarate, mai coperte dal «provato».</summary>
    public static readonly IReadOnlyList<FeatureVerdict> NotSent =
    [
        new("LeafA (descriptor, estremi E)", FeatureOutcome.NotExercised, "non inviata (tetto di quattro richieste): stesso sottoschema di descriptor ed estremi di LeafB, inferito e NON provato"),
        new("Expand e Compare (descriptor null, estremi E)", FeatureOutcome.NotExercised, "non inviate (tetto di quattro richieste): descriptor identico a Cross ed estremi identici a LeafB, inferito e NON provato"),
        new("items/patches maxItems:0 e focus null nelle varianti v9", FeatureOutcome.NotExercised, "il prompt non li induce (items [] e patches [] e focus null sono richiesti): gia misurati sulla v8 (NoItems), qui non ripetuti"),
        new("descriptor di soli spazi o oltre 512 caratteri", FeatureOutcome.NotExercised, "non espressi dalla grammatica v9 (nessun maxLength, nessun \\S): restano al validatore"),
    ];

    /// <summary>Provato: ALMENO un verdetto Honored e tutti quelli esercitati Honored (quindi schema inviato = congelato in ogni chiamata e controllo valido); i NotExercised restano dichiarati e fuori dal «provato».</summary>
    public static bool Proven(IReadOnlyList<FeatureVerdict> verdicts) =>
        verdicts.Any(v => v.Outcome == FeatureOutcome.Honored) && verdicts.Where(v => v.Outcome != FeatureOutcome.NotExercised).All(v => v.Outcome == FeatureOutcome.Honored);
}

/// <summary>
/// VERIFICA REALE (DGX) — NON eseguita senza autorizzazione esplicita. Misura se llama.cpp b10703 (router) rispetta, nello schema inviato, <c>descriptor</c> <c>string+minLength:1</c> / <c>null</c> e il prefisso degli
/// estremi <c>^E</c> / <c>^N</c> delle varianti v9: vedi <see cref="ReconcileSchemaV9Probe"/>. Esattamente QUATTRO richieste al modello, nessun retry, tetto duro nel client, nessun documento, nessun database;
/// modello fissato come per la campagna (<see cref="RetrievalGapCampaign.LlmModelId"/>). Rapporto in un file NUOVO <c>%TEMP%\bprojects-t040-campaign\schema-v9-support-report-&lt;UTC&gt;.json</c> (schema inviato, hash atteso e inviato,
/// stato HTTP, finish_reason, output grezzo, verdetto per funzionalità), scritto anche in caso di errore; nessuna chiave. Opt-in come ogni test DGX (<c>BPROJECTS_TEST_DGX_ALLOW=1</c>): senza, Skipped.
/// Un esito diverso da «supporto provato» è un FAIL della verifica, mai un PASS; non è di per sé un difetto del prodotto: il validatore della gerarchia rifiuta comunque ogni violazione.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
[Trait("Campaign", "SchemaV9Support")]
[Collection(RealParser.CollectionName)]
public sealed class ReconcileSchemaV9SupportRealTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_provider_honors_the_v9_descriptor_and_endpoint_constraints_in_the_reconcile_schema_variants()
    {
        var options = TestEnvironment.RequireDgxOptions();   // Skipped senza opt-in, endpoint, chiave o modello: nessuna inferenza
        var problems = RetrievalGapCampaign.ValidateEnvironment(Environment.GetEnvironmentVariable).ToList();   // modello fissato e variabili della campagna
        if (options.StructuredOutputMode != RetrievalGapCampaign.RequiredStructuredOutputMode)
        {
            problems.Add($"StructuredOutputMode {options.StructuredOutputMode}: senza JsonSchema nessuno schema viene inviato e la verifica non avrebbe senso");
        }

        Assert.True(problems.Count == 0, "verifica di supporto dello schema v9: configurazione incompatibile, nessuna richiesta inviata: " + string.Join(" | ", problems));
        options.Phases.Analysis.EnableThinking = RetrievalGapCampaign.Thinking;   // ragionamento esplicito spento, come la campagna
        options.Phases.Analysis.RequestTimeoutSeconds = 600;                      // la prima richiesta può caricare il modello (router, cold start)
        options.RequestTimeoutSeconds = 600;

        var path = Path.Combine(Path.GetTempPath(), "bprojects-t040-campaign", $"schema-v9-support-report-{DateTime.UtcNow:yyyyMMddTHHmmssfff}Z.json");
        var probe = new ProbeHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        var cap = new RequestCapHandler(probe, ReconcileSchemaV9Probe.MaxRequests);
        IReadOnlyList<ProbeCall> calls = [];
        IReadOnlyList<FeatureVerdict> verdicts = [];
        try
        {
            using var client = new InternalModelClient(options, cap);
            calls = await ReconcileSchemaV9Probe.RunAsync(client, probe, Ct);
            verdicts = ReconcileSchemaV9Probe.Evaluate(calls);
        }
        finally
        {
            Persist(path, probe, cap, calls, verdicts);
            cap.Dispose();   // il client non possiede il gestore (disposeHandler:false): chiude esplicitamente la catena cap → osservatore → socket
        }

        var failing = verdicts.Where(v => v.Outcome is not (FeatureOutcome.Honored or FeatureOutcome.NotExercised)).ToList();
        Assert.True(
            ReconcileSchemaV9Probe.Proven(verdicts),
            "supporto dei vincoli v9 NON provato: " + string.Join(" | ", failing.Select(v => $"{v.Feature}: {v.Outcome} ({v.Detail})")) + $" (rapporto in {path})");
    }

    private static void Persist(string path, ProbeHandler probe, RequestCapHandler cap, IReadOnlyList<ProbeCall> calls, IReadOnlyList<FeatureVerdict> verdicts)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var report = new
            {
                scenario = "schema-v9-support",
                writtenAtUtc = DateTimeOffset.UtcNow,
                provider = "llama.cpp b10703 (router): build dichiarata da docs/roadmap/slice-002-technical-closure.md, NON rilevata da questa verifica",
                model = RetrievalGapCampaign.LlmModelId,
                requestBudget = ReconcileSchemaV9Probe.MaxRequests,
                requestsSent = probe.Exchanges.Count,
                requestsAttempted = cap.Attempted,
                temperature = 0,
                maxOutputTokens = ReconcileSchemaV9Probe.MaxOutputTokens,
                systemPrompt = ReconcileSchemaV9Probe.SystemPrompt,
                userPrompt = ReconcileSchemaV9Probe.UserPrompt,
                perCall = calls.Select((c, i) =>
                {
                    var exchange = i < probe.Exchanges.Count ? probe.Exchanges[i] : null;
                    var expected = ReconcileSchemaV9Probe.FrozenHash(c.Variant);
                    return new
                    {
                        c.Variant,
                        expectedFrozenHash = expected,
                        sentSchemaHash = c.RequestSchemaHash,
                        hashMatches = string.Equals(c.RequestSchemaHash, expected, StringComparison.Ordinal),
                        httpStatus = c.HttpStatus,
                        finishReason = c.FinishReason,
                        rawOutput = c.Content,
                        error = c.Error,
                        sentSchema = exchange is null ? (JsonElement?)null : ReconcileSchemaV9Probe.SentSchema(exchange.RequestBody),
                    };
                }),
                exchanges = probe.Exchanges.Select(x => new { x.Method, x.Path, x.Status, request = Truncate(x.RequestBody, 60_000), response = Truncate(x.ResponseBody, 8_000) }),
                verdicts = verdicts.Select(v => new { v.Feature, outcome = v.Outcome.ToString(), v.Detail }),
                provenForExercisedFeatures = ReconcileSchemaV9Probe.Proven(verdicts),
                notProven = verdicts.Where(v => v.Outcome == FeatureOutcome.NotExercised).Select(v => v.Feature),   // MAI coperte da «provenForExercisedFeatures»
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
