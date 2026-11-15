using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

// 002 reconciliation-hardening RH-050 (M5, D8 autorizzato): sonda della grammatica v11 (<= 4 richieste) e replay di batch singoli reali (<= 10 richieste) sul DGX, con le opzioni di richiesta del PRODOTTO
// (istruzioni v6, schema by-stage-1 della variante, temperatura 0.1 per i batch, fase Analysis). Nessun retry, tetto duro nel RequestCapHandler, nessuna riparazione dell'output, nessun database.
// Opt-in come ogni test DGX (BPROJECTS_TEST_DGX_ALLOW=1): senza, Skipped. Il rapporto e' un file NUOVO in %TEMP%\bprojects-rh050 (output grezzo, hash dell'input, validazioni, token, durata).

/// <summary>Sonda della grammatica: quattro richieste, due prompt (X, Y) per due schemi (controllo completo, confronto by-stage-1).</summary>
internal static class ReconcileV11GrammarProbe
{
    public const int MaxRequests = 4;
    public const int MaxOutputTokens = 1024;

    public const string System = "Sei un verificatore di formato. Rispondi SOLO con un oggetto JSON conforme allo schema fornito, senza testo prima o dopo.";

    /// <summary>X: induce l'esito Open SENZA motivo (reason null) e DUE candidati. Vietati dal confronto v11 (anyOf Open => reason non vuoto; candidates.maxItems = 0).</summary>
    public const string PromptX =
        "Prova di formato, nessun contenuto reale. Il candidato in esame e f1 fra E1 = «Il pagamento avviene entro 30 giorni.» ed E2 = «Il pagamento avviene entro 60 giorni.». Compila la risposta ESATTAMENTE cosi: "
        + "contract = reconcile-alias-1; descriptor null; noFindings null; items: []; candidates: DUE candidati, f1 fra E1 ed E2 (type Contradicts) e f2 fra E2 ed E1 (type Contradicts), ciascuno con basis e rationale ammessi "
        + "(scegli i valori degli enum fra quelli ammessi dallo schema); focus: outcome \"Open\" e reason null (nessun motivo); patches: [].";

    /// <summary>Y: induce l'esito Unresolved (fuori dall'enum del confronto) con un motivo. Il confronto v11 ammette solo Compared e Open.</summary>
    public const string PromptY =
        "Prova di formato, nessun contenuto reale. Il candidato in esame e f1 fra E1 = «Il pagamento avviene entro 30 giorni.» ed E2 = «Il pagamento avviene entro 60 giorni.». Compila la risposta ESATTAMENTE cosi: "
        + "contract = reconcile-alias-1; descriptor null; noFindings null; items: []; candidates: []; focus: outcome \"Unresolved\" e reason \"non risolto\"; patches: [].";

    public sealed record Call(string Id, string Schema, string Prompt, string? Content, string? Finish, int? Http, string? Error, string? SentSchemaHash, long? InTokens, long? OutTokens, long Millis);

    public static readonly (string Id, string Schema, string Prompt)[] Plan =
    [
        ("P1-control-X", "Full", "X"), ("P2-compare-X", "Compare", "X"), ("P3-control-Y", "Full", "Y"), ("P4-compare-Y", "Compare", "Y"),
    ];

    public static JsonElement SchemaOf(string name) => name == "Full" ? ReconcileAliasContracts.JsonSchema() : ReconcileAliasContracts.SchemaOfByStage(ReconcileBatchSchema.Compare);

    public static string Hash(JsonElement schema) => CanonicalJson.HashText(CanonicalJson.Canonicalize(schema.GetRawText()));

    public static async Task<IReadOnlyList<Call>> RunAsync(InternalModelClient client, ProbeHandler handler, CancellationToken ct)
    {
        var calls = new List<Call>();
        foreach (var (id, schemaName, promptName) in Plan)
        {
            var before = handler.Exchanges.Count;
            var options = client.CreateChatOptions(SchemaOf(schemaName), ReconcileAliasContracts.SchemaName, temperature: 0, phase: AiPhase.Analysis);
            options.MaxOutputTokens = MaxOutputTokens;
            string? content = null, finish = null, error = null;
            long? input = null, output = null;
            var watch = Stopwatch.StartNew();
            try
            {
                var response = await client.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.System, System), new ChatMessage(ChatRole.User, promptName == "X" ? PromptX : PromptY)], options, ct);
                content = response.Text;
                finish = response.FinishReason?.Value;
                input = response.Usage?.InputTokenCount;
                output = response.Usage?.OutputTokenCount;
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                error = e.GetType().Name;
            }

            watch.Stop();
            var exchange = handler.Exchanges.Skip(before).LastOrDefault(x => x.Method == "POST");
            calls.Add(new Call(id, schemaName, promptName, content, finish, exchange?.Status, error, exchange is null ? null : ReconcileSchemaSupportProbe.RequestSchemaHash(exchange.RequestBody), input, output, watch.ElapsedMilliseconds));
        }

        return calls;
    }

    internal sealed record Shape(string? Outcome, bool ReasonPresent, int Candidates, bool Parsed);

    public static Shape Parse(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            var root = doc.RootElement;
            var candidates = root.TryGetProperty("candidates", out var c) && c.ValueKind == JsonValueKind.Array ? c.GetArrayLength() : -1;
            if (!root.TryGetProperty("focus", out var focus) || focus.ValueKind != JsonValueKind.Object)
            {
                return new Shape(null, false, candidates, true);
            }

            var outcome = focus.TryGetProperty("outcome", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            var reason = focus.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String && r.GetString()!.Length > 0;
            return new Shape(outcome, reason, candidates, true);
        }
        catch (JsonException)
        {
            return new Shape(null, false, -1, false);
        }
    }

    /// <summary>Verdetti per funzionalita: «Honored» SOLO se il controllo (schema completo, stesso prompt) ha mostrato il comportamento vietato e il confronto no; senza controllo valido: «Inconclusive».</summary>
    public static IReadOnlyList<FeatureVerdict> Evaluate(IReadOnlyList<Call> calls)
    {
        Call Get(string id) => calls.Single(c => c.Id == id);
        bool Ok(Call c) => c.Http is >= 200 and < 300 && c.Error is null && c.Finish != "length" && c.SentSchemaHash == Hash(SchemaOf(c.Schema));
        FeatureVerdict Judge(string feature, Call control, Call compare, Func<Shape, bool> violates)
        {
            if (!Ok(compare))
            {
                return new(feature, compare.Http is 400 or 422 && Ok(control) ? FeatureOutcome.RejectedByProvider : FeatureOutcome.Inconclusive,
                    $"richiesta del confronto non valutabile (HTTP {compare.Http?.ToString() ?? "-"}, finish {compare.Finish ?? "-"}, errore {compare.Error ?? "-"})");
            }

            var ctl = Parse(control.Content);
            var cmp = Parse(compare.Content);
            if (!Ok(control) || !ctl.Parsed || !violates(ctl))
            {
                return new(feature, FeatureOutcome.Inconclusive, $"il controllo con lo schema completo non ha indotto il comportamento vietato (outcome={ctl.Outcome}, reason={ctl.ReasonPresent}, candidati={ctl.Candidates}): nessuna prova");
            }

            return violates(cmp)
                ? new(feature, FeatureOutcome.IgnoredByProvider, $"il confronto ha tenuto il comportamento vietato (outcome={cmp.Outcome}, reason={cmp.ReasonPresent}, candidati={cmp.Candidates})")
                : new(feature, FeatureOutcome.Honored, $"controllo: outcome={ctl.Outcome}, reason={ctl.ReasonPresent}, candidati={ctl.Candidates}; confronto: outcome={cmp.Outcome}, reason={cmp.ReasonPresent}, candidati={cmp.Candidates}");
        }

        return
        [
            Judge("candidates.maxItems = 0 (confronto)", Get("P1-control-X"), Get("P2-compare-X"), s => s.Candidates > 0),
            Judge("anyOf: Open => reason non vuoto (confronto)", Get("P1-control-X"), Get("P2-compare-X"), s => s.Outcome == "Open" && !s.ReasonPresent),
            Judge("enum di focus.outcome = {Compared, Open} (confronto)", Get("P3-control-Y"), Get("P4-compare-Y"), s => s.Outcome == "Unresolved"),
        ];
    }
}

[Trait("Category", TestCategories.MafReal)]
[Trait("Campaign", "ReconcileV11Real")]
[Collection(RealParser.CollectionName)]
public sealed class ReconcileV11RealProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly MethodInfo InvokeAsyncMethod = typeof(ReconciliationHierarchy).GetMethod("InvokeAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ReconciliationHierarchy.InvokeAsync non trovato: la firma interna e cambiata.");

    private static AiOptions Options()
    {
        var options = TestEnvironment.RequireDgxOptions();   // Skipped senza opt-in, endpoint, chiave o modello
        var problems = RetrievalGapCampaign.ValidateEnvironment(Environment.GetEnvironmentVariable).ToList();
        if (options.StructuredOutputMode != RetrievalGapCampaign.RequiredStructuredOutputMode)
        {
            problems.Add($"StructuredOutputMode {options.StructuredOutputMode}: senza JsonSchema nessuno schema viene inviato");
        }

        Assert.True(problems.Count == 0, "RH-050: configurazione incompatibile, nessuna richiesta inviata: " + string.Join(" | ", problems));
        options.Phases.Analysis.EnableThinking = RetrievalGapCampaign.Thinking;
        options.Phases.Analysis.RequestTimeoutSeconds = 900;
        options.RequestTimeoutSeconds = 900;
        return options;
    }

    private static string ReportPath(string name) => Path.Combine(Path.GetTempPath(), "bprojects-rh050", $"{name}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}Z.json");

    private static void Write(string path, object report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, ReportJson), Encoding.UTF8);
    }

    [Fact]
    public async Task RH050_A_the_provider_honors_the_v11_compare_grammar()
    {
        var options = Options();
        var path = ReportPath("rh050-a-grammar");
        var probe = new ProbeHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        var cap = new RequestCapHandler(probe, ReconcileV11GrammarProbe.MaxRequests);
        IReadOnlyList<ReconcileV11GrammarProbe.Call> calls = [];
        IReadOnlyList<FeatureVerdict> verdicts = [];
        try
        {
            using var client = new InternalModelClient(options, cap);
            calls = await ReconcileV11GrammarProbe.RunAsync(client, probe, Ct);
            verdicts = ReconcileV11GrammarProbe.Evaluate(calls);
        }
        finally
        {
            Write(path, new
            {
                scenario = "rh050-a-grammar", writtenAtUtc = DateTimeOffset.UtcNow, model = RetrievalGapCampaign.LlmModelId, requestBudget = ReconcileV11GrammarProbe.MaxRequests, requestsAttempted = cap.Attempted,
                temperature = 0, maxOutputTokens = ReconcileV11GrammarProbe.MaxOutputTokens, systemPrompt = ReconcileV11GrammarProbe.System, promptX = ReconcileV11GrammarProbe.PromptX, promptY = ReconcileV11GrammarProbe.PromptY,
                compareSchemaHash = ReconcileV11GrammarProbe.Hash(ReconcileV11GrammarProbe.SchemaOf("Compare")), fullSchemaHash = ReconcileV11GrammarProbe.Hash(ReconcileV11GrammarProbe.SchemaOf("Full")),
                compareSchema = JsonNode.Parse(ReconcileV11GrammarProbe.SchemaOf("Compare").GetRawText()),
                perCall = calls.Select(c => new
                {
                    c.Id, c.Schema, c.Prompt, c.Http, c.Finish, c.Error, sentSchemaHash = c.SentSchemaHash, hashMatches = c.SentSchemaHash == ReconcileV11GrammarProbe.Hash(ReconcileV11GrammarProbe.SchemaOf(c.Schema)),
                    inputTokens = c.InTokens, outputTokens = c.OutTokens, millis = c.Millis, rawOutput = c.Content,
                    schemaViolations = c.Content is null ? null : SafeViolations(ReconcileV11GrammarProbe.SchemaOf(c.Schema), c.Content),
                }),
                verdicts = verdicts.Select(v => new { v.Feature, outcome = v.Outcome.ToString(), v.Detail }),
            });
            cap.Dispose();
        }

        Assert.True(verdicts.Count == 3 && verdicts.All(v => v.Outcome == FeatureOutcome.Honored), "RH-050-A: " + string.Join(" | ", verdicts.Select(v => $"{v.Feature}: {v.Outcome} ({v.Detail})")) + $" (rapporto {path})");
    }

    private static IReadOnlyList<string> SafeViolations(JsonElement schema, string json)
    {
        try
        {
            return ReconcileSchemaSubset.Violations(schema, json);
        }
        catch (JsonException e)
        {
            return ["JSON non decodificabile: " + e.Message];
        }
    }

    /// <summary>Batch del replay (RH-050 B): (run, chiamata, motivo).</summary>
    private static readonly (int Run, int Call, string Why)[] Replay =
    [
        (5, 30, "confronto equivalente alla chiamata 030 del Run 5"),
        (5, 26, "foglia B 026 del Run 5 (candidati vs 16 della v5)"),
        (5, 27, "foglia B 027 del Run 5 (candidati vs 16 della v5)"),
        (5, 22, "foglia A 022 del Run 5 (deve produrre items [] dal modello)"),
        (4, 34, "espansione con membership 034 del Run 4"),
        (4, 35, "espansione con membership 035 del Run 4"),
        (5, 28, "riduzione B 028 del Run 5"),
        (5, 29, "cross 029 del Run 5"),
        (4, 30, "confronto 030 del Run 4"),
        (4, 29, "confronto 029 del Run 4"),
    ];

    internal const int ReplayCap = 10;

    [Fact]
    public async Task RH050_B_the_real_model_answers_the_single_v11_batches()
    {
        var options = Options();
        var path = ReportPath("rh050-b-replay");
        var probe = new ProbeHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        var cap = new RequestCapHandler(probe, ReplayCap);
        var rows = new List<object>();
        var instructions = ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV6);
        try
        {
            using var client = new InternalModelClient(options, cap);
            foreach (var (run, call, why) in Replay)
            {
                var fixture = ReplayFixtures.Load(run, call);
                var batch = ReplayFixtureBuilder.Build(fixture);
                var variant = ReconcileAliasContracts.BatchSchemaOf(fixture.Stage, Enum.Parse<ReconcileView>(fixture.View));
                var schema = ReconcileAliasContracts.SchemaOfByStage(variant);
                var messages = batch.Messages();
                var userText = string.Join("\n", messages.Select(m => m.Text));
                var chat = client.CreateChatOptions(schema, ReconcileAliasContracts.SchemaName, temperature: 0.1f, phase: AiPhase.Analysis);
                string? content = null, finish = null, error = null;
                long? input = null, output = null;
                var before = probe.Exchanges.Count;
                var watch = Stopwatch.StartNew();
                try
                {
                    var response = await client.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.System, instructions), .. messages], chat, Ct);
                    content = response.Text;
                    finish = response.FinishReason?.Value;
                    input = response.Usage?.InputTokenCount;
                    output = response.Usage?.OutputTokenCount;
                }
                catch (Exception e) when (!(e is OperationCanceledException && Ct.IsCancellationRequested))
                {
                    error = e.GetType().Name + ": " + e.Message;
                }

                watch.Stop();
                var exchange = probe.Exchanges.Skip(before).LastOrDefault(x => x.Method == "POST");
                var sentHash = exchange is null ? null : ReconcileSchemaSupportProbe.RequestSchemaHash(exchange.RequestBody);
                var expectedHash = ReconcileV11GrammarProbe.Hash(schema);
                var evaluated = content is null ? null : Evaluate(batch, content);
                rows.Add(new
                {
                    fixture = fixture.Name, why, stage = fixture.Stage, view = fixture.View, variant = variant.ToString(), entries = fixture.Entries.Count,
                    inputSha256 = ReplayFixtures.Sha256Hex(instructions + "\n---\n" + userText), userTextChars = userText.Length, schemaHash = expectedHash, sentSchemaHash = sentHash, hashMatches = sentHash == expectedHash,
                    http = exchange?.Status, finish, error, inputTokens = input, outputTokens = output, millis = watch.ElapsedMilliseconds, maxOutputTokens = chat.MaxOutputTokens, rawOutput = content,
                    archivedAssistantCandidates = CountArray(fixture.Assistant, "candidates"), modelCandidates = content is null ? (int?)null : CountArray(content, "candidates"),
                    result = evaluated,
                });
            }
        }
        finally
        {
            Write(path, new
            {
                scenario = "rh050-b-replay", writtenAtUtc = DateTimeOffset.UtcNow, model = RetrievalGapCampaign.LlmModelId, requestBudget = ReplayCap, requestsAttempted = cap.Attempted, temperature = 0.1,
                promptVersion = ReconcileAliasContracts.PromptVersionV6, instructionsSha256 = ReplayFixtures.Sha256Hex(instructions), schemaProfile = ReconcileAliasContracts.SchemaProfileByStage,
                stageContract = ReconcileAliasContracts.StageContract, rows,
            });
            cap.Dispose();
        }

        Assert.Equal(Replay.Length, rows.Count);
    }

    private static int CountArray(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Array ? p.GetArrayLength() : -1;
        }
        catch (JsonException)
        {
            return -1;
        }
    }

    /// <summary>Validazione di UN output con la catena di produzione v11: schema del profilo, compositore (opzioni di contratto per stadio con le evidenze) e motore (regole V2). Mai riparazioni.</summary>
    private static object Evaluate(ReplayBatch batch, string json)
    {
        var variant = ReconcileAliasContracts.BatchSchemaOf(batch.Fixture.Stage, Enum.Parse<ReconcileView>(batch.Fixture.View));
        var schemaViolations = SafeViolations(ReconcileAliasContracts.SchemaOfByStage(variant), json);
        var composeOptions = new ReconcileComposeOptions(ReconcileAliasContracts.StageContract, batch.Evidence);
        var composition = ReconcileAliasComposer.Compose(json, batch.Catalog, batch.Request, batch.Snapshot, batch.Base, null, composeOptions);
        if (!composition.IsValid)
        {
            return new
            {
                schemaViolations, composer = "rejected", composerViolations = composition.Violations,
                typedViolations = composition.TypedViolations.Select(v => new { kind = v.Kind.ToString(), v.Path, v.Message }), engine = (string?)null, composed = (object?)null,
            };
        }

        var request = batch.Request;
        var text = ReconciliationCodec.Encode(composition.Result!);
        ReconcileInvoker invoker = (_, _) => new ValueTask<string>(text);
        var buildsNode = request.Batch.Stage is "leaf" or "reduce";
        string engine;
        try
        {
            var task = (ValueTask<BatchOutcome>)InvokeAsyncMethod.Invoke(
                null, [request.Batch, request.View, request.Level, request.Entries, request.Nodes, batch.Snapshot, new ReconciliationCatalog(request.Nodes, [], []), invoker, buildsNode, request.Focus, CancellationToken.None, request.Groups, ReconcileValidationRules.V2])!;
            _ = task.AsTask().GetAwaiter().GetResult();
            engine = "accepted";
        }
        catch (DocumentWorkException e)
        {
            engine = "rejected: " + e.Message;
        }

        var counts = new Dictionary<string, int>();
        using (var doc = JsonDocument.Parse(text))
        {
            foreach (var p in doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
            {
                counts[p.Name] = p.Value.GetArrayLength();
            }
        }

        return new { schemaViolations, composer = "accepted", composerViolations = Array.Empty<string>(), typedViolations = Array.Empty<object>(), engine, composed = new { arrayCounts = counts, composedText = text } };
    }
}
