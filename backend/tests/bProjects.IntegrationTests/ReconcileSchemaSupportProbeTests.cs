using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// OFFLINE (nessuna rete, nessun DGX): la verifica reale minima del supporto di <c>maxItems:0</c> e <c>type:null</c> (<see cref="ReconcileSchemaSupportRealTests"/>) è progettata e provata contro provider FINTI.
/// Si dimostra che la prova invia le tre varianti congelate nel manifesto T040 v4 (hash dello schema nel corpo HTTP), una volta sola ciascuna e senza retry, che distingue un provider che rispetta, ignora o rifiuta le
/// funzionalità e che un controllo debole rende l'esito non conclusivo. NON dimostra il comportamento di llama.cpp b10703: quello è proprio ciò che la verifica reale misurerebbe, e non è stata eseguita.
/// </summary>
public sealed class ReconcileSchemaSupportProbeTests
{
    private const string ApiKey = "chiave-segreta-di-prova";

    private enum Behaviour { Honor, Ignore, RejectUnsupported, EmptyItemsEverywhere, Truncated }

    /// <summary>Provider finto: legge lo schema dal corpo della richiesta e risponde come un provider che rispetta, ignora o rifiuta <c>maxItems:0</c> e <c>type:null</c>.</summary>
    private sealed class FakeProvider(Behaviour behaviour) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var schema = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
            var properties = schema.GetProperty("properties");
            var itemsLimited = properties.GetProperty("items").TryGetProperty("maxItems", out var max) && max.GetInt32() == 0;
            var focusNull = properties.GetProperty("focus").TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "null";

            if (behaviour == Behaviour.RejectUnsupported && (itemsLimited || focusNull))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"Unrecognized schema: maxItems 0","type":"invalid_request_error"}}""", Encoding.UTF8, "application/json") };
            }

            var items = behaviour switch
            {
                Behaviour.EmptyItemsEverywhere => 0,
                Behaviour.Honor or Behaviour.RejectUnsupported or Behaviour.Truncated => itemsLimited ? 0 : 1,
                _ => 1,
            };
            var focusIsNull = behaviour is Behaviour.Honor && focusNull;
            var content = JsonSerializer.Serialize(new
            {
                contract = "reconcile-alias-1", descriptor = (string?)null, noFindings = (string?)null,
                items = Enumerable.Range(1, items).Select(i => new { key = $"i{i}" }),
                candidates = new[] { new { key = "f1" } },
                focus = focusIsNull ? null : new { outcome = "Compared", reason = "prova" },
                patches = Array.Empty<object>(),
            });
            var finish = behaviour == Behaviour.Truncated ? "length" : "stop";
            var completion = JsonSerializer.Serialize(new
            {
                id = "c1", @object = "chat.completion", created = 1, model = RetrievalGapCampaign.LlmModelId,
                choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = finish } },
                usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(completion, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<(IReadOnlyList<ProbeCall> Calls, IReadOnlyList<FeatureVerdict> Verdicts, ProbeHandler Handler, FakeProvider Provider)> RunAsync(Behaviour behaviour)
    {
        var provider = new FakeProvider(behaviour);
        var handler = new ProbeHandler(provider);
        using var client = new InternalModelClient(new AiOptions { Endpoint = "http://localhost:1/v1/", ModelId = RetrievalGapCampaign.LlmModelId, ApiKey = ApiKey }, handler);
        var calls = await ReconcileSchemaSupportProbe.RunAsync(client, handler, TestContext.Current.CancellationToken);
        return (calls, ReconcileSchemaSupportProbe.Evaluate(calls), handler, provider);
    }

    private static JsonElement SentSchema(ProbeExchange exchange) =>
        JsonDocument.Parse(exchange.RequestBody).RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").Clone();

    [Fact]
    public async Task A_provider_that_honors_both_features_is_proven_and_exactly_three_requests_carry_the_frozen_schema_variants()
    {
        var (calls, verdicts, handler, provider) = await RunAsync(Behaviour.Honor);

        Assert.Equal(3, provider.Requests);   // una richiesta per variante, mai ripetuta
        Assert.Equal(["Full", "NoItems", "Candidate"], calls.Select(c => c.Variant));
        Assert.Equal(["Full", "NoItems", "Candidate"], RetrievalGapCampaign.Run3ReconcileSchemaVariantHashes.Keys);   // stesso ordine del manifesto
        // Lo schema REALMENTE nel corpo HTTP di ciascuna richiesta è quello congelato nel manifesto v4 (hash canonico): nessuna copia ricostruita dal test.
        Assert.All(calls, c => Assert.Equal(RetrievalGapCampaign.Run3ReconcileSchemaVariantHashes[c.Variant], c.RequestSchemaHash));

        var full = SentSchema(handler.Exchanges[0]).GetProperty("properties");
        Assert.False(full.GetProperty("items").TryGetProperty("maxItems", out _));   // il controllo è lo schema completo, invariato
        Assert.Equal(JsonValueKind.Array, full.GetProperty("focus").GetProperty("type").ValueKind);
        var noItems = SentSchema(handler.Exchanges[1]).GetProperty("properties");
        Assert.Equal(0, noItems.GetProperty("items").GetProperty("maxItems").GetInt32());
        Assert.Equal(0, noItems.GetProperty("patches").GetProperty("maxItems").GetInt32());
        Assert.Equal("null", noItems.GetProperty("focus").GetProperty("type").GetString());
        var candidate = SentSchema(handler.Exchanges[2]).GetProperty("properties");
        Assert.Equal(0, candidate.GetProperty("items").GetProperty("maxItems").GetInt32());
        Assert.False(candidate.GetProperty("patches").TryGetProperty("maxItems", out _));

        // Richiesta del prodotto: temperatura 0, ragionamento esplicitamente spento, output limitato.
        using var first = JsonDocument.Parse(handler.Exchanges[0].RequestBody);
        Assert.Equal(0, first.RootElement.GetProperty("temperature").GetDouble());
        var limit = first.RootElement.TryGetProperty("max_completion_tokens", out var completion) ? completion : first.RootElement.GetProperty("max_tokens");   // nome del campo secondo la versione dell'SDK
        Assert.Equal(ReconcileSchemaSupportProbe.MaxOutputTokens, limit.GetInt32());
        Assert.False(first.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());

        Assert.Equal(
            [("maxItems:0 su items (NoItems)", FeatureOutcome.Honored), ("type:null su focus (NoItems)", FeatureOutcome.Honored), ("maxItems:0 su items (Candidate)", FeatureOutcome.Honored), ("maxItems:0 su patches (NoItems)", FeatureOutcome.NotExercised)],
            verdicts.Select(v => (v.Feature, v.Outcome)));
        Assert.True(ReconcileSchemaSupportProbe.Proven(verdicts));
    }

    [Fact]
    public async Task A_provider_that_ignores_the_constraints_is_reported_as_ignoring_them_never_as_proven()
    {
        var (_, verdicts, _, provider) = await RunAsync(Behaviour.Ignore);
        Assert.Equal(3, provider.Requests);
        Assert.Equal(FeatureOutcome.IgnoredByProvider, verdicts.Single(v => v.Feature == "maxItems:0 su items (NoItems)").Outcome);
        Assert.Equal(FeatureOutcome.IgnoredByProvider, verdicts.Single(v => v.Feature == "type:null su focus (NoItems)").Outcome);
        Assert.Equal(FeatureOutcome.IgnoredByProvider, verdicts.Single(v => v.Feature == "maxItems:0 su items (Candidate)").Outcome);
        Assert.False(ReconcileSchemaSupportProbe.Proven(verdicts));
    }

    [Fact]
    public async Task A_provider_that_rejects_the_schema_is_reported_as_rejecting_it_and_nothing_is_retried()
    {
        var (calls, verdicts, _, provider) = await RunAsync(Behaviour.RejectUnsupported);
        Assert.Equal(3, provider.Requests);   // nessun retry sul 400
        Assert.Equal([200, 400, 400], calls.Select(c => c.HttpStatus));
        Assert.Equal(FeatureOutcome.RejectedByProvider, verdicts.Single(v => v.Feature == "maxItems:0 su items (NoItems)").Outcome);
        Assert.Equal(FeatureOutcome.RejectedByProvider, verdicts.Single(v => v.Feature == "type:null su focus (NoItems)").Outcome);
        Assert.Equal(FeatureOutcome.RejectedByProvider, verdicts.Single(v => v.Feature == "maxItems:0 su items (Candidate)").Outcome);
        Assert.False(ReconcileSchemaSupportProbe.Proven(verdicts));
    }

    [Fact]
    public async Task A_control_that_does_not_induce_items_makes_the_items_verdicts_inconclusive_not_honored()
    {
        // Un provider che non produce mai items non dimostra nulla sul vincolo: senza controllo valido l'esito è «non conclusivo».
        var (_, verdicts, _, _) = await RunAsync(Behaviour.EmptyItemsEverywhere);
        Assert.Equal(FeatureOutcome.Inconclusive, verdicts.Single(v => v.Feature == "maxItems:0 su items (NoItems)").Outcome);
        Assert.Equal(FeatureOutcome.Inconclusive, verdicts.Single(v => v.Feature == "maxItems:0 su items (Candidate)").Outcome);
        Assert.False(ReconcileSchemaSupportProbe.Proven(verdicts));
    }

    [Fact]
    public async Task A_truncated_answer_is_inconclusive()
    {
        var (_, verdicts, _, _) = await RunAsync(Behaviour.Truncated);
        Assert.All(verdicts.Where(v => v.Outcome != FeatureOutcome.NotExercised), v => Assert.Equal(FeatureOutcome.Inconclusive, v.Outcome));
        Assert.False(ReconcileSchemaSupportProbe.Proven(verdicts));
    }

    private static ProbeCall Valid(string variant, int status, string? content, string? finish = "stop", string? error = null) =>
        new(variant, status, finish, content, error, ReconcileSchemaSupportProbe.SchemaHash(variant));

    private const string WithItems = """{"items":[{"key":"i1"}],"candidates":[],"focus":{"outcome":"Compared"}}""";
    private const string Empty = """{"items":[],"candidates":[],"focus":null}""";

    [Fact]
    public void Only_a_request_error_about_the_schema_counts_as_rejection_other_failures_and_a_wrong_schema_are_inconclusive()
    {
        FeatureOutcome ItemsNoItems(ProbeCall noItems) =>
            ReconcileSchemaSupportProbe.Evaluate([Valid("Full", 200, WithItems), noItems, Valid("Candidate", 200, Empty)]).Single(v => v.Feature == "maxItems:0 su items (NoItems)").Outcome;

        Assert.Equal(FeatureOutcome.Honored, ItemsNoItems(Valid("NoItems", 200, Empty)));
        Assert.Equal(FeatureOutcome.RejectedByProvider, ItemsNoItems(Valid("NoItems", 400, null)));
        Assert.Equal(FeatureOutcome.RejectedByProvider, ItemsNoItems(Valid("NoItems", 422, null)));
        foreach (var status in new[] { 401, 429, 503 })
        {
            Assert.Equal(FeatureOutcome.Inconclusive, ItemsNoItems(Valid("NoItems", status, null)));   // avvio a freddo, chiave, carico: nulla sullo schema
        }

        Assert.Equal(FeatureOutcome.Inconclusive, ItemsNoItems(Valid("NoItems", 0, null, null, "TaskCanceledException") with { HttpStatus = null }));   // timeout del client
        // Risposta «vuota» ma con lo schema SBAGLIATO nel corpo (o nessuno): mai Honored.
        Assert.Equal(FeatureOutcome.Inconclusive, ItemsNoItems(Valid("NoItems", 200, Empty) with { RequestSchemaHash = ReconcileSchemaSupportProbe.SchemaHash("Full") }));
        Assert.Equal(FeatureOutcome.Inconclusive, ItemsNoItems(Valid("NoItems", 200, Empty) with { RequestSchemaHash = null }));
        // Controllo con lo schema sbagliato: non vale come controllo.
        var badControl = Valid("Full", 200, WithItems) with { RequestSchemaHash = ReconcileSchemaSupportProbe.SchemaHash("NoItems") };
        Assert.Equal(FeatureOutcome.Inconclusive, ReconcileSchemaSupportProbe.Evaluate([badControl, Valid("NoItems", 200, Empty), Valid("Candidate", 200, Empty)])[0].Outcome);
    }

    [Fact]
    public void An_incomplete_set_of_calls_is_inconclusive_and_never_proven()
    {
        var verdicts = ReconcileSchemaSupportProbe.Evaluate([new ProbeCall("Full", 200, "stop", "{}", null, null)]);
        Assert.Equal(FeatureOutcome.Inconclusive, Assert.Single(verdicts).Outcome);
        Assert.False(ReconcileSchemaSupportProbe.Proven(verdicts));
        Assert.False(ReconcileSchemaSupportProbe.Proven([]));
    }

    [Fact]
    public async Task The_observer_never_records_the_api_key_and_the_report_inputs_carry_no_headers()
    {
        var (_, _, handler, _) = await RunAsync(Behaviour.Honor);
        var serialized = JsonSerializer.Serialize(handler.Exchanges);
        Assert.DoesNotContain(ApiKey, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(ProbeExchange).GetProperties(), p => p.Name.Contains("Header", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_variant_hashes_the_probe_computes_are_the_ones_frozen_in_the_manifest()
    {
        Assert.Equal(RetrievalGapCampaign.Run3ReconcileSchemaVariantHashes.Keys, ReconcileSchemaSupportProbe.Variants);
        Assert.All(ReconcileSchemaSupportProbe.Variants, v => Assert.Equal(RetrievalGapCampaign.Run3ReconcileSchemaVariantHashes[v], ReconcileSchemaSupportProbe.SchemaHash(v)));
        Assert.Null(ReconcileSchemaSupportProbe.RequestSchemaHash("""{"model":"m"}"""));   // senza response_format (ValidatedTextJson) nessuno schema: hash assente
        Assert.Null(ReconcileSchemaSupportProbe.RequestSchemaHash("non json"));
    }

    [Fact]
    public void The_real_check_is_one_isolated_opt_in_test_that_is_skipped_without_the_dgx_opt_in()
    {
        var type = typeof(ReconcileSchemaSupportRealTests);
        var traits = type.GetCustomAttributes<TraitAttribute>(true).Select(t => (t.Name, t.Value)).ToList();
        Assert.Contains(("Category", TestCategories.MafReal), traits);
        Assert.Contains(("Campaign", "SchemaSupport"), traits);
        Assert.Equal(RealParser.CollectionName, type.GetCustomAttributes<CollectionAttribute>(true).Single().Name);   // seriale con ogni altra prova reale: il DGX è uno slot unico
        Assert.Single(type.GetMethods(), m => m.GetCustomAttribute<FactAttribute>() is not null);
        Assert.False(TestEnvironment.DgxAllowedFrom(_ => null));
        Assert.Contains(TestEnvironment.DgxAllowVariable, TestEnvironment.DgxUnavailableReason(_ => null));
        Assert.Equal(3, ReconcileSchemaSupportProbe.Variants.Length);   // tre richieste in tutto
    }
}
