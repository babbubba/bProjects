using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>
/// OFFLINE (nessuna rete, nessun DGX): la verifica reale dei vincoli v9 (<see cref="ReconcileSchemaV9SupportRealTests"/>) è progettata e provata contro provider FINTI. Si dimostra che la prova invia le quattro
/// richieste previste (controllo con lo schema completo + LeafB, Reduce, Cross), una volta sola ciascuna, con lo schema congelato nel corpo HTTP; che il prompt induce davvero il comportamento vietato in ogni variante
/// (la risposta che lo segue è ammessa dallo schema completo e rifiutata da ciascuna variante); che distingue un provider che rispetta, ignora o rifiuta i vincoli; che senza un controllo capace di indurre il
/// comportamento vietato l'esito è «Inconclusive» e mai «Honored»; che il tetto di quattro richieste è duro. NON dimostra il comportamento di llama.cpp b10703: quello è proprio ciò che la verifica reale misurerebbe.
/// </summary>
public sealed class ReconcileSchemaV9SupportProbeTests
{
    private const string ApiKey = "chiave-segreta-di-prova";

    private enum Behaviour { Honor, Ignore, Reject, ControlNull, ControlOnlyEvidence, ControlOnlyNodes, EmptyCandidates, Truncated }

    /// <summary>Provider finto: legge lo schema dal corpo della richiesta e risponde come un provider che applica, ignora o rifiuta i vincoli del descriptor e degli estremi.</summary>
    private sealed class FakeProvider(Behaviour behaviour) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var properties = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("properties");
            var descriptor = properties.GetProperty("descriptor");
            var descriptorNull = descriptor.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "null";
            var descriptorNonEmpty = descriptor.TryGetProperty("minLength", out _);
            var endpointPattern = properties.GetProperty("candidates").GetProperty("items").GetProperty("properties").GetProperty("left").GetProperty("pattern").GetString()!;
            var constrained = descriptorNull || descriptorNonEmpty;

            if (behaviour == Behaviour.Reject && constrained)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"Unrecognized schema: minLength","type":"invalid_request_error"}}""", Encoding.UTF8, "application/json") };
            }

            var honor = behaviour is not (Behaviour.Ignore or Behaviour.Reject);
            var descriptorValue = (honor && descriptorNull ? null : honor && descriptorNonEmpty ? "gruppo di regole" : behaviour == Behaviour.ControlNull && !constrained ? null : "");
            var onlyE = honor && endpointPattern.StartsWith("^E", StringComparison.Ordinal) || behaviour == Behaviour.ControlOnlyEvidence && !constrained;
            var onlyN = honor && endpointPattern.StartsWith("^N", StringComparison.Ordinal) || behaviour == Behaviour.ControlOnlyNodes && !constrained;
            var first = onlyN ? 'N' : 'E';
            var second = onlyE ? 'E' : 'N';
            object[] candidates = behaviour == Behaviour.EmptyCandidates && constrained
                ? []
                :
                [
                    new { key = "f1", left = $"{first}1", right = $"{first}2", type = "Contradicts", basis = "Inferred", rationale = "prova" },
                    new { key = "f2", left = $"{second}1", right = $"{second}2", type = "Contradicts", basis = "Inferred", rationale = "prova" },
                ];
            var content = JsonSerializer.Serialize(new { contract = "reconcile-alias-1", descriptor = descriptorValue, noFindings = (string?)null, items = Array.Empty<object>(), candidates, focus = (object?)null, patches = Array.Empty<object>() });
            var completion = JsonSerializer.Serialize(new
            {
                id = "c1", @object = "chat.completion", created = 1, model = RetrievalGapCampaign.LlmModelId,
                choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = behaviour == Behaviour.Truncated ? "length" : "stop" } },
                usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(completion, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<(IReadOnlyList<ProbeCall> Calls, IReadOnlyList<FeatureVerdict> Verdicts, ProbeHandler Handler, FakeProvider Provider, RequestCapHandler Cap)> RunAsync(Behaviour behaviour)
    {
        var provider = new FakeProvider(behaviour);
        var probe = new ProbeHandler(provider);
        var cap = new RequestCapHandler(probe, ReconcileSchemaV9Probe.MaxRequests);
        using var client = new InternalModelClient(new AiOptions { Endpoint = "http://localhost:1/v1/", ModelId = RetrievalGapCampaign.LlmModelId, ApiKey = ApiKey }, cap);
        var calls = await ReconcileSchemaV9Probe.RunAsync(client, probe, TestContext.Current.CancellationToken);
        return (calls, ReconcileSchemaV9Probe.Evaluate(calls), probe, provider, cap);
    }

    private static FeatureOutcome Outcome(IReadOnlyList<FeatureVerdict> verdicts, string feature) => verdicts.Single(v => v.Feature == feature).Outcome;

    private const string LeafBNonEmpty = "descriptor != \"\" (minLength:1) — LeafB";
    private const string ReduceNonEmpty = "descriptor != \"\" (minLength:1) — Reduce";
    private const string CrossNull = "descriptor == null (type:null) — Cross";
    private const string LeafBEndpoints = "estremi ^E (pattern) — LeafB";
    private const string ReduceEndpoints = "estremi ^N (pattern) — Reduce";
    private const string CrossEndpoints = "estremi ^N (pattern) — Cross";
    private const string LeafBNonNull = "descriptor != null (type:string) — LeafB";
    private const string ReduceNonNull = "descriptor != null (type:string) — Reduce";

    private static readonly string[] JudgedFeatures = [LeafBNonEmpty, ReduceNonEmpty, CrossNull, LeafBEndpoints, ReduceEndpoints, CrossEndpoints];

    private static JsonElement SentSchema(ProbeExchange exchange) =>
        JsonDocument.Parse(exchange.RequestBody).RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").Clone();

    // ================================================================ la prova invia quattro richieste, con lo schema congelato

    [Fact]
    public async Task A_provider_that_honors_every_constraint_is_proven_and_exactly_four_requests_carry_the_frozen_schemas()
    {
        var (calls, verdicts, handler, provider, cap) = await RunAsync(Behaviour.Honor);

        Assert.Equal(4, provider.Requests);   // una richiesta per variante, mai ripetuta
        Assert.Equal((4, 4), (cap.Attempted, handler.Exchanges.Count));
        Assert.Equal(["Full", "LeafB", "Reduce", "Cross"], calls.Select(c => c.Variant));
        // Lo schema REALMENTE nel corpo HTTP di ciascuna richiesta è quello congelato (hash canonico): nessuna copia ricostruita dal test.
        Assert.All(calls, c => Assert.Equal(ReconcileSchemaV9Probe.FrozenHash(c.Variant), c.RequestSchemaHash));
        Assert.All(handler.Exchanges, x => Assert.Equal("POST", x.Method));

        // Il controllo è lo schema completo, invariato: descriptor nullable senza minLength e candidati con ^[EN].
        var full = SentSchema(handler.Exchanges[0]).GetProperty("properties");
        Assert.Equal(JsonValueKind.Array, full.GetProperty("descriptor").GetProperty("type").ValueKind);
        Assert.False(full.GetProperty("descriptor").TryGetProperty("minLength", out _));
        Assert.Equal("^[EN][1-9][0-9]*$", full.GetProperty("candidates").GetProperty("items").GetProperty("properties").GetProperty("left").GetProperty("pattern").GetString());
        var leafB = SentSchema(handler.Exchanges[1]).GetProperty("properties");
        Assert.Equal("""{"type":"string","minLength":1}""", leafB.GetProperty("descriptor").GetRawText());
        Assert.Equal("^E[1-9][0-9]*$", leafB.GetProperty("candidates").GetProperty("items").GetProperty("properties").GetProperty("right").GetProperty("pattern").GetString());
        var reduce = SentSchema(handler.Exchanges[2]).GetProperty("properties");
        Assert.Equal("""{"type":"string","minLength":1}""", reduce.GetProperty("descriptor").GetRawText());
        Assert.Equal("^N[1-9][0-9]*$", reduce.GetProperty("candidates").GetProperty("items").GetProperty("properties").GetProperty("left").GetProperty("pattern").GetString());
        var cross = SentSchema(handler.Exchanges[3]).GetProperty("properties");
        Assert.Equal("""{"type":"null"}""", cross.GetProperty("descriptor").GetRawText());
        Assert.Equal("^N[1-9][0-9]*$", cross.GetProperty("candidates").GetProperty("items").GetProperty("properties").GetProperty("right").GetProperty("pattern").GetString());

        // Richiesta del prodotto: temperatura 0, ragionamento esplicitamente spento, output limitato, stesso prompt in tutte e quattro.
        using var first = JsonDocument.Parse(handler.Exchanges[0].RequestBody);
        Assert.Equal(0, first.RootElement.GetProperty("temperature").GetDouble());
        var limit = first.RootElement.TryGetProperty("max_completion_tokens", out var completion) ? completion : first.RootElement.GetProperty("max_tokens");   // nome del campo secondo la versione dell'SDK
        Assert.Equal(ReconcileSchemaV9Probe.MaxOutputTokens, limit.GetInt32());
        Assert.False(first.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.All(handler.Exchanges, x => Assert.Contains("la stringa vuota", x.RequestBody, StringComparison.Ordinal));
        Assert.Single(handler.Exchanges.Select(x => JsonDocument.Parse(x.RequestBody).RootElement.GetProperty("messages").GetRawText()).Distinct());

        Assert.All(JudgedFeatures, f => Assert.Equal(FeatureOutcome.Honored, Outcome(verdicts, f)));
        // Il prompt induce la stringa vuota, non null: «descriptor != null» NON è esercitato e resta dichiarato come tale (mai Honored).
        Assert.Equal(FeatureOutcome.NotExercised, Outcome(verdicts, LeafBNonNull));
        Assert.Equal(FeatureOutcome.NotExercised, Outcome(verdicts, ReduceNonNull));
        Assert.Equal(ReconcileSchemaV9Probe.NotSent.Select(v => v.Feature), verdicts.TakeLast(ReconcileSchemaV9Probe.NotSent.Count).Select(v => v.Feature));
        Assert.True(ReconcileSchemaV9Probe.Proven(verdicts));
    }

    [Fact]
    public async Task A_provider_that_ignores_the_constraints_is_reported_as_ignoring_them_never_as_proven()
    {
        var (_, verdicts, _, provider, _) = await RunAsync(Behaviour.Ignore);

        Assert.Equal(4, provider.Requests);
        Assert.All(JudgedFeatures, f => Assert.Equal(FeatureOutcome.IgnoredByProvider, Outcome(verdicts, f)));
        Assert.False(ReconcileSchemaV9Probe.Proven(verdicts));
    }

    [Fact]
    public async Task A_provider_that_rejects_the_schema_is_reported_as_rejecting_it_and_nothing_is_retried()
    {
        var (calls, verdicts, _, provider, cap) = await RunAsync(Behaviour.Reject);

        Assert.Equal(4, provider.Requests);   // nessun retry sul 400
        Assert.Equal(4, cap.Attempted);
        Assert.Equal([200, 400, 400, 400], calls.Select(c => c.HttpStatus));
        Assert.All(JudgedFeatures, f => Assert.Equal(FeatureOutcome.RejectedByProvider, Outcome(verdicts, f)));
        Assert.False(ReconcileSchemaV9Probe.Proven(verdicts));
    }

    // ================================================================ senza un controllo capace di indurre il comportamento vietato non c'è prova

    [Fact]
    public async Task A_control_that_does_not_produce_the_empty_descriptor_makes_the_minLength_verdicts_inconclusive_not_honored()
    {
        // Il modello ignora la richiesta di una stringa vuota e produce null: la grammatica che impone una stringa non vuota non è provata contro "", ma lo è contro null.
        var (_, verdicts, _, _, _) = await RunAsync(Behaviour.ControlNull);

        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(verdicts, LeafBNonEmpty));
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(verdicts, ReduceNonEmpty));
        Assert.Equal(FeatureOutcome.Honored, Outcome(verdicts, LeafBNonNull));
        Assert.Equal(FeatureOutcome.Honored, Outcome(verdicts, ReduceNonNull));
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(verdicts, CrossNull));   // il controllo non ha mostrato un descriptor non null
        Assert.False(ReconcileSchemaV9Probe.Proven(verdicts));
    }

    [Fact]
    public async Task A_control_without_node_endpoints_makes_the_leaf_endpoint_verdict_inconclusive_and_one_without_evidence_endpoints_the_node_ones()
    {
        var (_, onlyEvidence, _, _, _) = await RunAsync(Behaviour.ControlOnlyEvidence);
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(onlyEvidence, LeafBEndpoints));   // nessun N nel controllo: nulla mostra che le foglie dovrebbero escluderlo
        Assert.Equal(FeatureOutcome.Honored, Outcome(onlyEvidence, ReduceEndpoints));
        Assert.Equal(FeatureOutcome.Honored, Outcome(onlyEvidence, CrossEndpoints));
        Assert.False(ReconcileSchemaV9Probe.Proven(onlyEvidence));

        var (_, onlyNodes, _, _, _) = await RunAsync(Behaviour.ControlOnlyNodes);
        Assert.Equal(FeatureOutcome.Honored, Outcome(onlyNodes, LeafBEndpoints));
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(onlyNodes, ReduceEndpoints));   // nessun E nel controllo
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(onlyNodes, CrossEndpoints));
        Assert.False(ReconcileSchemaV9Probe.Proven(onlyNodes));
    }

    [Fact]
    public async Task A_response_with_no_candidates_does_not_prove_the_endpoint_constraints()
    {
        var (_, verdicts, _, _, _) = await RunAsync(Behaviour.EmptyCandidates);

        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(verdicts, LeafBEndpoints));
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(verdicts, ReduceEndpoints));
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(verdicts, CrossEndpoints));
        Assert.Equal(FeatureOutcome.Honored, Outcome(verdicts, LeafBNonEmpty));   // il descriptor è comunque valutabile
        Assert.False(ReconcileSchemaV9Probe.Proven(verdicts));
    }

    [Fact]
    public async Task A_truncated_answer_is_inconclusive()
    {
        var (_, verdicts, _, _, _) = await RunAsync(Behaviour.Truncated);

        Assert.All(verdicts.Where(v => v.Outcome != FeatureOutcome.NotExercised), v => Assert.Equal(FeatureOutcome.Inconclusive, v.Outcome));
        Assert.False(ReconcileSchemaV9Probe.Proven(verdicts));
    }

    private static ProbeCall Call(string variant, int status, string? content, string? finish = "stop", string? error = null) =>
        new(variant, status, finish, content, error, ReconcileSchemaV9Probe.FrozenHash(variant));

    private const string Followed = """{"contract":"reconcile-alias-1","descriptor":"","candidates":[{"left":"E1","right":"E2"},{"left":"N1","right":"N2"}]}""";
    private const string Compliant = """{"descriptor":"gruppo","candidates":[{"left":"E1","right":"E2"},{"left":"E1","right":"E2"}]}""";
    private const string NullDescriptor = """{"descriptor":null,"candidates":[{"left":"N1","right":"N2"}]}""";

    private static IReadOnlyList<FeatureVerdict> Evaluate(ProbeCall leafB, ProbeCall? control = null) =>
        ReconcileSchemaV9Probe.Evaluate([control ?? Call("Full", 200, Followed), leafB, Call("Reduce", 200, NullDescriptor), Call("Cross", 200, NullDescriptor)]);

    [Fact]
    public void Only_a_request_error_about_the_schema_counts_as_rejection_other_failures_and_a_wrong_schema_are_inconclusive()
    {
        Assert.Equal(FeatureOutcome.Honored, Outcome(Evaluate(Call("LeafB", 200, Compliant)), LeafBNonEmpty));
        Assert.Equal(FeatureOutcome.RejectedByProvider, Outcome(Evaluate(Call("LeafB", 400, null)), LeafBNonEmpty));
        Assert.Equal(FeatureOutcome.RejectedByProvider, Outcome(Evaluate(Call("LeafB", 422, null)), LeafBNonEmpty));
        foreach (var status in new[] { 401, 429, 503 })
        {
            Assert.Equal(FeatureOutcome.Inconclusive, Outcome(Evaluate(Call("LeafB", status, null)), LeafBNonEmpty));   // avvio a freddo, chiave, carico: nulla sullo schema
        }

        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(Evaluate(Call("LeafB", 0, null, null, "TaskCanceledException") with { HttpStatus = null }), LeafBNonEmpty));   // timeout del client
        // Risposta conforme ma con lo schema SBAGLIATO nel corpo (o nessuno): mai Honored.
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(Evaluate(Call("LeafB", 200, Compliant) with { RequestSchemaHash = ReconcileSchemaV9Probe.FrozenHash("Reduce") }), LeafBNonEmpty));
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(Evaluate(Call("LeafB", 200, Compliant) with { RequestSchemaHash = ReconcileSchemaV9Probe.FrozenHash("Full") }), LeafBNonEmpty));
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(Evaluate(Call("LeafB", 200, Compliant) with { RequestSchemaHash = null }), LeafBNonEmpty));
        // Controllo con lo schema sbagliato: non vale come controllo, tutto diventa non conclusivo.
        var badControl = Call("Full", 200, Followed) with { RequestSchemaHash = ReconcileSchemaV9Probe.FrozenHash("LeafB") };
        Assert.All(JudgedFeatures, f => Assert.Equal(FeatureOutcome.Inconclusive, Outcome(Evaluate(Call("LeafB", 200, Compliant), badControl), f)));
    }

    [Fact]
    public void A_non_evaluable_body_is_inconclusive_even_when_the_status_is_ok()
    {
        foreach (var body in new[] { "non json", "[]", """{"candidates":[]}""", """{"descriptor":3,"candidates":[]}""", """{"descriptor":"x","candidates":[{"left":1,"right":"E2"}]}""", """{"descriptor":"x"}"""})
        {
            Assert.Equal(FeatureOutcome.Inconclusive, Outcome(Evaluate(Call("LeafB", 200, body)), LeafBNonEmpty));
        }

        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(Evaluate(Call("LeafB", 200, Compliant, "length")), LeafBNonEmpty));
    }

    [Fact]
    public void An_incomplete_or_duplicated_set_of_calls_is_inconclusive_and_never_proven()
    {
        var verdicts = ReconcileSchemaV9Probe.Evaluate([Call("Full", 200, Followed)]);
        Assert.Equal(FeatureOutcome.Inconclusive, Assert.Single(verdicts).Outcome);
        Assert.False(ReconcileSchemaV9Probe.Proven(verdicts));
        Assert.False(ReconcileSchemaV9Probe.Proven([]));
        Assert.False(ReconcileSchemaV9Probe.Proven(ReconcileSchemaV9Probe.Evaluate([Call("Full", 200, Followed), Call("Full", 200, Followed), Call("LeafB", 200, Compliant), Call("Reduce", 200, NullDescriptor)])));
        Assert.False(ReconcileSchemaV9Probe.Proven(ReconcileSchemaV9Probe.NotSent));   // i soli «non esercitati» non provano nulla
    }

    [Fact]
    public void A_rejection_is_attributed_to_the_schema_only_when_the_full_schema_control_was_accepted()
    {
        // Il controllo (schema completo) rifiutato insieme alle varianti: modello, limiti o contesto, non lo schema v9.
        var rejectedControl = Call("Full", 400, null);
        var verdicts = Evaluate(Call("LeafB", 400, null), rejectedControl);
        Assert.All(JudgedFeatures, f => Assert.NotEqual(FeatureOutcome.RejectedByProvider, Outcome(verdicts, f)));
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(verdicts, LeafBNonEmpty));
        Assert.Contains("non e attribuibile allo schema", verdicts.Single(v => v.Feature == LeafBNonEmpty).Detail, StringComparison.Ordinal);
        Assert.False(ReconcileSchemaV9Probe.Proven(verdicts));
    }

    // Dati REGISTRATI dall'esecuzione reale del 2026-11-11 (rapporto schema-v9-support-report-20261111T221305333Z.json, SHA-256 309D0C8A…25EE): output grezzi di Qwen3.6-35B-Smart su llama.cpp b10703.
    private const string RecordedFull =
        """{"contract":"reconcile-alias-1","descriptor":"","noFindings":null,"items":[],"candidates":[{"key":"f1","left":"E1","right":"E2","type":"Contradicts","basis":"Inferred","rationale":"I termini di pagamento 30 e 60 giorni sono incompatibili."},{"key":"f2","left":"N1","right":"N2","type":"Confirms","basis":"Inferred","rationale":"I nodi riguardano aspetti diversi ma correlati alla transazione."}],"focus":null,"patches":[]}""";

    private const string RecordedLeafB =
        """{"contract":"reconcile-alias-1","descriptor":"\"\"","noFindings":null,"items":[],"candidates":[{"key":"f1","left":"E1","right":"E2","type":"Contradicts","basis":"Inferred","rationale":"I termini di pagamento 30 e 60 giorni sono incompatibili."},{"key":"f2","left":"E1","right":"E2","type":"Contradicts","basis":"Inferred","rationale":"I termini di pagamento 30 e 60 giorni sono incompatibili."}],"focus":null,"patches":[]}""";

    private const string RecordedReduce =
        """{"contract":"reconcile-alias-1","descriptor":"\"\"","noFindings":null,"items":[],"candidates":[{"key":"f1","left":"N1","right":"N2","type":"SameRule","basis":"Inferred","rationale":"Evidenze contraddittorie su termini di pagamento"},{"key":"f2","left":"N1","right":"N2","type":"SameRule","basis":"Inferred","rationale":"Evidenze contraddittorie su termini di pagamento"}],"focus":null,"patches":[]}""";

    private const string RecordedCross =
        """{"contract":"reconcile-alias-1","descriptor":null,"noFindings":null,"items":[],"candidates":[{"key":"f1","left":"N1","right":"N2","type":"SameRule","basis":"Inferred","rationale":"Both nodes refer to payment terms."},{"key":"f2","left":"N1","right":"N2","type":"SameRule","basis":"Inferred","rationale":"Both nodes refer to delivery terms."}],"focus":null,"patches":[]}""";

    [Fact]
    public void The_recorded_real_run_is_evaluated_as_six_honored_constraints_and_the_declared_not_exercised_ones()
    {
        // Regressione sulla logica di valutazione: i dati del run reale (HTTP 200, finish=stop, hash inviato = congelato) restano sei Honored; nessun altro esito esercitato.
        var verdicts = ReconcileSchemaV9Probe.Evaluate([Call("Full", 200, RecordedFull), Call("LeafB", 200, RecordedLeafB), Call("Reduce", 200, RecordedReduce), Call("Cross", 200, RecordedCross)]);

        Assert.All(JudgedFeatures, f => Assert.Equal(FeatureOutcome.Honored, Outcome(verdicts, f)));
        Assert.Equal([FeatureOutcome.NotExercised], verdicts.Where(v => v.Outcome != FeatureOutcome.Honored).Select(v => v.Outcome).Distinct());
        Assert.Equal(6, verdicts.Count(v => v.Outcome == FeatureOutcome.Honored));
        Assert.Equal(FeatureOutcome.NotExercised, Outcome(verdicts, LeafBNonNull));
        Assert.Equal(FeatureOutcome.NotExercised, Outcome(verdicts, ReduceNonNull));
        Assert.True(ReconcileSchemaV9Probe.Proven(verdicts));
        // Il descriptor delle foglie B e della riduzione è la stringa letterale di due virgolette: soddisfa minLength:1 ma e privo di significato (osservazione in M3-real.md).
        Assert.Equal("\"\"", JsonDocument.Parse(RecordedLeafB).RootElement.GetProperty("descriptor").GetString());
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileSchemaV9Probe.SchemaOf("LeafB"), RecordedLeafB));
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileSchemaV9Probe.SchemaOf("Reduce"), RecordedReduce));
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileSchemaV9Probe.SchemaOf("Cross"), RecordedCross));
    }

    // ================================================================ tetto duro e riservatezza

    private sealed class Counting : HttpMessageHandler
    {
        public int Reached { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reached++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public async Task The_cap_blocks_the_fifth_request_before_it_leaves_the_machine_and_the_blocked_one_is_not_recorded()
    {
        var network = new Counting();
        var probe = new ProbeHandler(network);
        var cap = new RequestCapHandler(probe, 4);
        using var http = new HttpClient(cap);

        for (var i = 0; i < 4; i++)
        {
            using var ok = await http.PostAsync("http://localhost:1/v1/chat/completions", new StringContent("{}"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => http.PostAsync("http://localhost:1/v1/chat/completions", new StringContent("{}"), TestContext.Current.CancellationToken));
        Assert.Equal((4, 4, 5), (network.Reached, probe.Exchanges.Count, cap.Attempted));   // la quinta non arriva alla rete né all'osservatore
    }

    [Fact]
    public async Task The_observer_never_records_the_api_key_and_the_report_inputs_carry_no_headers()
    {
        var (_, _, handler, _, _) = await RunAsync(Behaviour.Honor);
        var serialized = JsonSerializer.Serialize(handler.Exchanges);
        Assert.DoesNotContain(ApiKey, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(ProbeExchange).GetProperties(), p => p.Name.Contains("Header", StringComparison.OrdinalIgnoreCase));
    }

    // ================================================================ gli hash congelati sono quelli del prodotto; il prompt induce il comportamento vietato

    [Fact]
    public void The_frozen_hashes_are_the_ones_the_product_produces_today_and_the_pinned_ones_are_distinct()
    {
        Assert.Equal(["Full", "LeafB", "Reduce", "Cross"], ReconcileSchemaV9Probe.Variants);
        Assert.Equal(ReconcileSchemaV9Probe.MaxRequests, ReconcileSchemaV9Probe.Variants.Length);   // quattro richieste in tutto
        Assert.All(ReconcileSchemaV9Probe.Variants, v => Assert.Equal(ReconcileSchemaV9Probe.FrozenHash(v), ReconcileSchemaV9Probe.SchemaHash(v)));
        Assert.Equal(RetrievalGapCampaign.ReconcileSchemaVariantHashes["Full"], ReconcileSchemaV9Probe.FrozenHash("Full"));   // il controllo è lo schema completo del manifesto v4
        Assert.Equal(4, ReconcileSchemaV9Probe.Variants.Select(ReconcileSchemaV9Probe.FrozenHash).Distinct().Count());
        // Le sei varianti v9 hanno golden propri; il frozen della prova è esattamente la stringa del profilo.
        foreach (var variant in Enum.GetNames<ReconcileBatchSchema>())
        {
            Assert.Contains($"{variant}={CanonicalJson.HashText(CanonicalJson.Canonicalize(ReconcileAliasContracts.SchemaOf(Enum.Parse<ReconcileBatchSchema>(variant)).GetRawText()))}", ReconcileAliasContracts.SchemaProfileV2Hashes());
        }

        Assert.Equal("^E[1-9][0-9]*$", AnalysisAliasPattern.Evidence);
        Assert.Equal("^N[1-9][0-9]*$", ReconcileAliasContracts.NodeAliasPattern);
        Assert.Null(ReconcileSchemaSupportProbe.RequestSchemaHash("""{"model":"m"}"""));   // senza response_format nessuno schema: hash assente
    }

    private static JsonObject FollowedPrompt() => JsonNode.Parse(
        """
        {"contract":"reconcile-alias-1","descriptor":"","noFindings":null,"items":[],
         "candidates":[{"key":"f1","left":"E1","right":"E2","type":"Contradicts","basis":"Inferred","rationale":"prova"},
                       {"key":"f2","left":"N1","right":"N2","type":"Contradicts","basis":"Inferred","rationale":"prova"}],
         "focus":null,"patches":[]}
        """)!.AsObject();

    [Fact]
    public void The_answer_that_follows_the_prompt_is_admitted_by_the_control_and_rejected_by_every_variant_only_on_descriptor_and_endpoints()
    {
        var json = FollowedPrompt().ToJsonString();
        Assert.Contains("la stringa vuota", ReconcileSchemaV9Probe.UserPrompt, StringComparison.Ordinal);   // il prompt chiede esattamente questo output
        Assert.Contains("f1 fra E1 ed E2 e f2 fra N1 ed N2", ReconcileSchemaV9Probe.UserPrompt, StringComparison.Ordinal);

        // Il controllo (schema completo) lo ammette: il comportamento vietato è producibile dal modello senza vincoli.
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileSchemaV9Probe.SchemaOf("Full"), json));

        // Ogni variante lo rifiuta, e soltanto per il descriptor e gli estremi dei candidati: items, patches e focus della richiesta sono ammessi da tutte.
        var violations = Enum.GetNames<ReconcileBatchSchema>()
            .Where(v => ReconcileSchemaV9Probe.Variants.Contains(v))
            .ToDictionary(v => v, v => ReconcileSchemaSubset.Violations(ReconcileSchemaV9Probe.SchemaOf(v), json));
        Assert.Equal(["LeafB", "Reduce", "Cross"], violations.Keys);
        Assert.All(violations, kv => Assert.All(kv.Value, line => Assert.True(line.StartsWith("$.descriptor", StringComparison.Ordinal) || line.StartsWith("$.candidates[", StringComparison.Ordinal), $"{kv.Key}: {line}")));
        Assert.Contains(violations["LeafB"], l => l.StartsWith("$.descriptor: lunghezza inferiore a 1", StringComparison.Ordinal));
        Assert.Contains(violations["Reduce"], l => l.StartsWith("$.descriptor: lunghezza inferiore a 1", StringComparison.Ordinal));
        Assert.Contains(violations["Cross"], l => l.StartsWith("$.descriptor: tipo string non ammesso", StringComparison.Ordinal));
        Assert.Equal(["$.candidates[1].left", "$.candidates[1].right"], violations["LeafB"].Where(l => l.StartsWith("$.candidates", StringComparison.Ordinal)).Select(l => l[..l.IndexOf(':')]));   // E ammesso, N vietato
        Assert.Equal(["$.candidates[0].left", "$.candidates[0].right"], violations["Reduce"].Where(l => l.StartsWith("$.candidates", StringComparison.Ordinal)).Select(l => l[..l.IndexOf(':')]));   // N ammesso, E vietato
        Assert.Equal(["$.candidates[0].left", "$.candidates[0].right"], violations["Cross"].Where(l => l.StartsWith("$.candidates", StringComparison.Ordinal)).Select(l => l[..l.IndexOf(':')]));

        // E ciascuna variante ammette un output conforme (descriptor corretto, estremi dello stadio): la prova non è impossibile da soddisfare.
        string Obeying(string descriptor, string letter) => FollowedPrompt().Also(o =>
        {
            o["descriptor"] = descriptor.Length == 0 ? null : descriptor;
            foreach (var c in o["candidates"]!.AsArray())
            {
                c!["left"] = $"{letter}1";
                c["right"] = $"{letter}2";
            }
        }).ToJsonString();
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileSchemaV9Probe.SchemaOf("LeafB"), Obeying("gruppo", "E")));
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileSchemaV9Probe.SchemaOf("Reduce"), Obeying("gruppo", "N")));
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileSchemaV9Probe.SchemaOf("Cross"), Obeying("", "N")));
    }

    [Fact]
    public void The_variants_not_sent_share_the_descriptor_and_endpoint_subschemas_of_the_ones_that_are()
    {
        // Le inferenze dichiarate come «non provate» poggiano su sottoschemi identici (stessi byte) a quelli esercitati: la verifica è rappresentativa, ma non li ha misurati.
        static string Descriptor(ReconcileBatchSchema v) => ReconcileAliasContracts.SchemaOf(v).GetProperty("properties").GetProperty("descriptor").GetRawText();
        static string Endpoints(ReconcileBatchSchema v) => ReconcileAliasContracts.SchemaOf(v).GetProperty("properties").GetProperty("candidates").GetProperty("items").GetProperty("properties").GetRawText();

        Assert.Equal(Descriptor(ReconcileBatchSchema.LeafB), Descriptor(ReconcileBatchSchema.LeafA));
        Assert.Equal(Endpoints(ReconcileBatchSchema.LeafB), Endpoints(ReconcileBatchSchema.LeafA));
        Assert.Equal(Descriptor(ReconcileBatchSchema.Cross), Descriptor(ReconcileBatchSchema.Expand));
        Assert.Equal(Descriptor(ReconcileBatchSchema.Cross), Descriptor(ReconcileBatchSchema.Compare));
        Assert.Equal(Endpoints(ReconcileBatchSchema.LeafB), Endpoints(ReconcileBatchSchema.Expand));
        Assert.Equal(Endpoints(ReconcileBatchSchema.LeafB), Endpoints(ReconcileBatchSchema.Compare));
        Assert.Equal(Descriptor(ReconcileBatchSchema.LeafB), Descriptor(ReconcileBatchSchema.Reduce));
        Assert.Equal(Endpoints(ReconcileBatchSchema.Reduce), Endpoints(ReconcileBatchSchema.Cross));
        // ...e i due insiemi sono davvero diversi: se non lo fossero la verifica non distinguerebbe nulla.
        Assert.NotEqual(Descriptor(ReconcileBatchSchema.LeafB), Descriptor(ReconcileBatchSchema.Cross));
        Assert.NotEqual(Endpoints(ReconcileBatchSchema.LeafB), Endpoints(ReconcileBatchSchema.Reduce));
    }

    [Fact]
    public void The_real_check_is_one_isolated_opt_in_test_that_is_skipped_without_the_dgx_opt_in_and_leaves_the_v8_check_untouched()
    {
        var type = typeof(ReconcileSchemaV9SupportRealTests);
        var traits = type.GetCustomAttributes<TraitAttribute>(true).Select(t => (t.Name, t.Value)).ToList();
        Assert.Contains(("Category", TestCategories.MafReal), traits);
        Assert.Contains(("Campaign", "SchemaV9Support"), traits);
        Assert.Equal(RealParser.CollectionName, type.GetCustomAttributes<CollectionAttribute>(true).Single().Name);   // seriale con ogni altra prova reale: il DGX è uno slot unico
        Assert.Single(type.GetMethods(), m => m.GetCustomAttribute<FactAttribute>() is not null);
        Assert.False(TestEnvironment.DgxAllowedFrom(_ => null));
        Assert.Contains(TestEnvironment.DgxAllowVariable, TestEnvironment.DgxUnavailableReason(_ => null));

        // La verifica storica della v8 è intatta: tre richieste, tre varianti, stesso rapporto.
        Assert.Equal(3, ReconcileSchemaSupportProbe.Variants.Length);
        Assert.Contains(("Campaign", "SchemaSupport"), typeof(ReconcileSchemaSupportRealTests).GetCustomAttributes<TraitAttribute>(true).Select(t => (t.Name, t.Value)));
    }
}
