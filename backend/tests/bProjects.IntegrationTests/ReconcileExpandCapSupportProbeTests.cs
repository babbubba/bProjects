using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// OFFLINE (nessuna rete, nessun DGX): la verifica reale del tetto <c>maxItems:16</c> (<see cref="ReconcileExpandCapSupportRealTests"/>) è progettata e provata contro provider FINTI. Si dimostra che invia
/// due sole richieste (controllo senza tetto e variante con tetto), una volta ciascuna, con lo schema congelato nel corpo HTTP; che il prompt induce davvero più di 16 candidati validi e distinti, accettati dal controllo
/// e vietati dalla variante; che distingue un provider che rispetta, ignora o rifiuta il tetto; che senza un controllo capace di superare 16 l'esito è «Inconclusive» e mai «Honored»; che un output troncato o un errore
/// del modello non è un successo. NON dimostra il comportamento di llama.cpp b10703: quello è proprio ciò che la verifica reale misurerebbe.
/// </summary>
public sealed class ReconcileExpandCapSupportProbeTests
{
    private const string ApiKey = "chiave-segreta-di-prova";

    private enum Behaviour { Honor, Ignore, Reject, ControlShort, ControlTruncated, CappedShort, CappedTruncated, ControlWithInvalid, ControlWithDuplicates, ModelErrorOnCapped, ServerErrorOnControl, Capped17 }

    private static readonly (int L, int R)[] AllPairs = [.. Enumerable.Range(1, 8).SelectMany(l => Enumerable.Range(l + 1, 8 - l).Select(r => (l, r)))];

    private static string Candidate(int index, int left, int right, string leftPrefix = "E", string rationale = "") =>
        JsonSerializer.Serialize(new { key = $"f{index}", left = $"{leftPrefix}{left}", right = $"E{right}", type = "Confirms", basis = "Inferred", rationale = rationale.Length > 0 ? rationale : $"coppia {left} {right}" });

    private static string Content(IEnumerable<string> candidates) =>
        $$"""{"contract":"reconcile-alias-1","descriptor":null,"noFindings":null,"items":[],"candidates":[{{string.Join(",", candidates)}}],"focus":null,"patches":[]}""";

    private static string Twenty(Func<int, string?>? tweak = null) => Content(AllPairs.Take(20).Select((p, i) => tweak?.Invoke(i + 1) ?? Candidate(i + 1, p.L, p.R)));

    private static string Take(int count) => Content(AllPairs.Take(count).Select((p, i) => Candidate(i + 1, p.L, p.R)));

    /// <summary>Provider finto: legge lo schema dal corpo della richiesta e risponde come un provider che applica, ignora o rifiuta <c>maxItems</c> sui candidati.</summary>
    private sealed class FakeProvider(Behaviour behaviour) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var candidates = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("candidates");
            var capped = candidates.TryGetProperty("maxItems", out var max) && max.GetInt32() == 16;

            if (behaviour == Behaviour.Reject && capped)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"Unrecognized schema: maxItems","type":"invalid_request_error"}}""", Encoding.UTF8, "application/json") };
            }

            if (behaviour == Behaviour.ServerErrorOnControl && !capped)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"error":{"message":"boom"}}""", Encoding.UTF8, "application/json") };
            }

            var finish = behaviour == Behaviour.ControlTruncated && !capped || behaviour == Behaviour.CappedTruncated && capped ? "length" : "stop";
            var content = (behaviour, capped) switch
            {
                (Behaviour.Honor or Behaviour.CappedTruncated or Behaviour.ControlTruncated, true) => Take(16),
                (Behaviour.Capped17, true) => Take(17),
                (Behaviour.CappedShort, true) => Take(8),
                (Behaviour.ControlShort, _) => Take(10),
                (Behaviour.ControlWithInvalid, false) => Twenty(i => i <= 5 ? Candidate(i, i, i + 1, leftPrefix: "N") : null),   // 5 estremi N su 20: 15 validi
                (Behaviour.ControlWithDuplicates, false) => Twenty(i => Candidate(i, 1 + (i % 2), 3, rationale: "uguale")),         // solo due coppie distinte
                _ => Twenty(),   // Ignore e i controlli: venti candidati validi e distinti
            };
            if (behaviour == Behaviour.ModelErrorOnCapped && capped)
            {
                content = "non e json";
            }

            var completion = JsonSerializer.Serialize(new
            {
                id = "c1", @object = "chat.completion", created = 1, model = RetrievalGapCampaign.LlmModelId,
                choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = finish } },
                usage = new { prompt_tokens = 321, completion_tokens = 1200, total_tokens = 1521 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(completion, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<(IReadOnlyList<ProbeCall> Calls, IReadOnlyList<FeatureVerdict> Verdicts, ProbeHandler Handler, FakeProvider Provider, RequestCapHandler Cap)> RunAsync(Behaviour behaviour)
    {
        var provider = new FakeProvider(behaviour);
        var probe = new ProbeHandler(provider);
        var cap = new RequestCapHandler(probe, ReconcileExpandCapProbe.MaxRequests);
        using var client = new InternalModelClient(new AiOptions { Endpoint = "http://localhost:1/v1/", ModelId = RetrievalGapCampaign.LlmModelId, ApiKey = ApiKey }, cap);
        var calls = await ReconcileExpandCapProbe.RunAsync(client, probe, TestContext.Current.CancellationToken);
        return (calls, ReconcileExpandCapProbe.Evaluate(calls), probe, provider, cap);
    }

    private static FeatureOutcome Outcome(IReadOnlyList<FeatureVerdict> verdicts, string feature) => verdicts.Single(v => v.Feature == feature).Outcome;

    // ================================================================ due richieste, schema congelato

    [Fact]
    public async Task A_provider_that_honors_the_cap_is_proven_and_exactly_two_requests_carry_the_frozen_schemas()
    {
        var (calls, verdicts, handler, provider, cap) = await RunAsync(Behaviour.Honor);

        Assert.Equal(2, provider.Requests);
        Assert.Equal((2, 2), (cap.Attempted, handler.Exchanges.Count));
        Assert.Equal(["ExpandUncapped", "ExpandCapped"], calls.Select(c => c.Variant));
        Assert.All(calls, c => Assert.Equal(ReconcileExpandCapProbe.FrozenHash(c.Variant), c.RequestSchemaHash));   // lo schema REALMENTE nel corpo è quello congelato
        Assert.All(handler.Exchanges, x => Assert.Equal("POST", x.Method));
        Assert.True(ReconcileExpandCapProbe.Proven(verdicts), string.Join(" | ", verdicts.Select(v => $"{v.Feature}: {v.Outcome} ({v.Detail})")));
        Assert.Equal(FeatureOutcome.Honored, Outcome(verdicts, ReconcileExpandCapProbe.CapFeature));
        Assert.Equal(FeatureOutcome.Honored, Outcome(verdicts, ReconcileExpandCapProbe.AcceptedFeature));

        var control = ReconcileExpandCapProbe.SentSchema(handler.Exchanges[0].RequestBody)!.Value.GetProperty("properties").GetProperty("candidates");
        var capped = ReconcileExpandCapProbe.SentSchema(handler.Exchanges[1].RequestBody)!.Value.GetProperty("properties").GetProperty("candidates");
        Assert.False(control.TryGetProperty("maxItems", out _));
        Assert.Equal(16, capped.GetProperty("maxItems").GetInt32());
        Assert.True(capped.TryGetProperty("items", out _));

        // Richiesta del prodotto: temperatura 0, ragionamento spento, output limitato, stesso messaggio nelle due richieste.
        using var first = JsonDocument.Parse(handler.Exchanges[0].RequestBody);
        Assert.Equal(0, first.RootElement.GetProperty("temperature").GetDouble());
        var limit = first.RootElement.TryGetProperty("max_completion_tokens", out var completion) ? completion : first.RootElement.GetProperty("max_tokens");
        Assert.Equal(ReconcileExpandCapProbe.MaxOutputTokens, limit.GetInt32());
        Assert.False(first.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Single(handler.Exchanges.Select(x => JsonDocument.Parse(x.RequestBody).RootElement.GetProperty("messages").GetRawText()).Distinct());
        Assert.True(ReconcileExpandCapProbe.MaxRequests <= 3);
    }

    [Fact]
    public void The_two_schemas_differ_only_by_the_candidates_cap_and_match_the_frozen_hashes_of_the_product()
    {
        Assert.Equal(ReconcileExpandCapProbe.FrozenHash("ExpandUncapped"), ReconcileExpandCapProbe.SchemaHash("ExpandUncapped"));
        Assert.Equal(ReconcileExpandCapProbe.FrozenHash("ExpandCapped"), ReconcileExpandCapProbe.SchemaHash("ExpandCapped"));
        Assert.NotEqual(ReconcileExpandCapProbe.FrozenHash("ExpandUncapped"), ReconcileExpandCapProbe.FrozenHash("ExpandCapped"));
        Assert.Contains($"Expand={ReconcileExpandCapProbe.CappedFrozenHash}", ReconcileAliasContracts.SchemaProfileV3Hashes());
        Assert.Contains($"Expand={ReconcileExpandCapProbe.FrozenHash("ExpandUncapped")}", ReconcileAliasContracts.SchemaProfileV2Hashes());

        var control = JsonNode.Parse(ReconcileExpandCapProbe.SchemaOf("ExpandUncapped").GetRawText())!.AsObject();
        var capped = JsonNode.Parse(ReconcileExpandCapProbe.SchemaOf("ExpandCapped").GetRawText())!.AsObject();
        Assert.Equal(16, capped["properties"]!["candidates"]!["maxItems"]!.GetValue<int>());
        capped["properties"]!["candidates"]!.AsObject().Remove("maxItems");
        Assert.Equal(control.ToJsonString(), capped.ToJsonString());
    }

    [Fact]
    public void The_prompt_induces_more_than_16_distinct_valid_candidates_that_the_control_schema_allows_and_the_capped_one_forbids()
    {
        var prompt = ReconcileExpandCapProbe.UserPrompt;
        Assert.Contains($"{ReconcileExpandCapProbe.RequestedCandidates} candidati", prompt, StringComparison.Ordinal);
        Assert.InRange(ReconcileExpandCapProbe.RequestedCandidates, ReconcileExpandCapProbe.Cap + 1, 28);   // con otto evidenze le coppie distinte sono 28
        var pairs = Regex.Matches(prompt, @"f\d+ = \(E\d, E\d\)").Select(m => m.Value).ToList();
        Assert.Equal(ReconcileExpandCapProbe.RequestedCandidates, pairs.Count);
        Assert.Equal(ReconcileExpandCapProbe.RequestedCandidates, pairs.Distinct().Count());

        // La risposta che il prompt chiede (venti coppie distinte) è valida per costruzione: la valutazione la conta come venti validi e distinti.
        var parsed = ReconcileExpandCapProbe.Parse(new ProbeCall("ExpandUncapped", 200, "stop", Twenty(), null, null))!;
        Assert.Equal((20, 20, 0), (parsed.ArrayLength, parsed.ValidDistinct, parsed.Invalid));
        Assert.Contains("rationale = al massimo cinque parole", prompt, StringComparison.Ordinal);
    }

    // ================================================================ verdetti

    [Fact]
    public async Task A_provider_that_ignores_the_cap_is_reported_as_ignored_never_as_honored()
    {
        var (_, verdicts, _, _, _) = await RunAsync(Behaviour.Ignore);

        Assert.Equal(FeatureOutcome.IgnoredByProvider, Outcome(verdicts, ReconcileExpandCapProbe.CapFeature));
        Assert.False(ReconcileExpandCapProbe.Proven(verdicts));
    }

    [Fact]
    public async Task A_provider_that_rejects_the_capped_schema_is_reported_as_a_schema_rejection_when_the_control_was_accepted()
    {
        var (_, verdicts, _, provider, _) = await RunAsync(Behaviour.Reject);

        Assert.Equal(2, provider.Requests);   // nessun retry dopo il 400
        Assert.Equal(FeatureOutcome.RejectedByProvider, Outcome(verdicts, ReconcileExpandCapProbe.CapFeature));
        Assert.Equal(FeatureOutcome.RejectedByProvider, Outcome(verdicts, ReconcileExpandCapProbe.AcceptedFeature));
        Assert.False(ReconcileExpandCapProbe.Proven(verdicts));
    }

    [Fact]
    public async Task A_control_that_does_not_exceed_16_valid_distinct_candidates_never_yields_honored()
    {
        foreach (var behaviour in new[] { Behaviour.ControlShort, Behaviour.ControlWithInvalid, Behaviour.ControlWithDuplicates })
        {
            var (_, verdicts, _, _, _) = await RunAsync(behaviour);

            Assert.Equal(FeatureOutcome.Inconclusive, Outcome(verdicts, ReconcileExpandCapProbe.CapFeature));
            Assert.False(ReconcileExpandCapProbe.Proven(verdicts));
        }
    }

    [Fact]
    public async Task A_truncated_response_is_never_a_success_whichever_variant_it_comes_from()
    {
        foreach (var behaviour in new[] { Behaviour.ControlTruncated, Behaviour.CappedTruncated })
        {
            var (_, verdicts, _, _, _) = await RunAsync(behaviour);

            Assert.Equal(FeatureOutcome.Inconclusive, Outcome(verdicts, ReconcileExpandCapProbe.CapFeature));
            Assert.False(ReconcileExpandCapProbe.Proven(verdicts));
        }
    }

    [Fact]
    public async Task A_model_error_on_the_capped_variant_is_inconclusive_and_distinct_from_a_schema_rejection()
    {
        var (calls, verdicts, _, _, _) = await RunAsync(Behaviour.ModelErrorOnCapped);

        Assert.Equal(200, calls[1].HttpStatus);   // il provider ha accettato lo schema
        Assert.Equal(FeatureOutcome.Honored, Outcome(verdicts, ReconcileExpandCapProbe.AcceptedFeature));
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(verdicts, ReconcileExpandCapProbe.CapFeature));
        Assert.False(ReconcileExpandCapProbe.Proven(verdicts));
    }

    [Fact]
    public async Task A_capped_variant_that_stops_below_or_above_the_cap_is_not_proven()
    {
        var (_, below, _, _, _) = await RunAsync(Behaviour.CappedShort);
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(below, ReconcileExpandCapProbe.CapFeature));   // sotto il tetto: non raggiunto

        var (_, above, _, _, _) = await RunAsync(Behaviour.Capped17);
        Assert.Equal(FeatureOutcome.IgnoredByProvider, Outcome(above, ReconcileExpandCapProbe.CapFeature));   // sopra il tetto: ignorato
        Assert.False(ReconcileExpandCapProbe.Proven(below));
        Assert.False(ReconcileExpandCapProbe.Proven(above));
    }

    [Fact]
    public async Task A_server_error_on_the_control_is_inconclusive_not_a_rejection_of_the_schema()
    {
        var (_, verdicts, _, provider, _) = await RunAsync(Behaviour.ServerErrorOnControl);

        Assert.Equal(2, provider.Requests);   // nessun retry dopo il 500
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(verdicts, ReconcileExpandCapProbe.CapFeature));
        Assert.False(ReconcileExpandCapProbe.Proven(verdicts));
    }

    [Fact]
    public void A_response_that_does_not_carry_the_frozen_schema_hash_is_inconclusive()
    {
        var valid = new ProbeCall("ExpandUncapped", 200, "stop", Twenty(), null, ReconcileExpandCapProbe.FrozenHash("ExpandUncapped"));
        var capped = new ProbeCall("ExpandCapped", 200, "stop", Take(16), null, ReconcileExpandCapProbe.FrozenHash("ExpandCapped"));
        Assert.True(ReconcileExpandCapProbe.Proven(ReconcileExpandCapProbe.Evaluate([valid, capped])));

        foreach (var wrong in new[] { valid with { RequestSchemaHash = ReconcileExpandCapProbe.FrozenHash("ExpandCapped") }, valid with { RequestSchemaHash = null } })
        {
            Assert.Equal(FeatureOutcome.Inconclusive, Outcome(ReconcileExpandCapProbe.Evaluate([wrong, capped]), ReconcileExpandCapProbe.CapFeature));
        }

        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(ReconcileExpandCapProbe.Evaluate([valid, capped with { RequestSchemaHash = ReconcileExpandCapProbe.FrozenHash("ExpandUncapped") }]), ReconcileExpandCapProbe.CapFeature));
        Assert.Equal(FeatureOutcome.Inconclusive, Outcome(ReconcileExpandCapProbe.Evaluate([valid]), ReconcileExpandCapProbe.CapFeature));   // servono esattamente due chiamate
    }

    [Fact]
    public void Usage_is_read_from_the_provider_response_and_absent_bodies_give_nulls()
    {
        Assert.Equal((321, 1200, 1521), ReconcileExpandCapProbe.Usage("""{"usage":{"prompt_tokens":321,"completion_tokens":1200,"total_tokens":1521}}"""));
        Assert.Equal((null, null, null), ReconcileExpandCapProbe.Usage(null));
        Assert.Equal((null, null, null), ReconcileExpandCapProbe.Usage("non json"));
        Assert.Equal((null, null, null), ReconcileExpandCapProbe.Usage("""{"id":"x"}"""));
    }
}
