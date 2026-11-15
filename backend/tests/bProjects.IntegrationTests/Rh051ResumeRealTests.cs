using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// RH-051 — UNICA prosecuzione REALE della run 4 sul DGX (autorizzata esplicitamente dall'utente), su una COPIA scratch del database della run 4: il run <c>Failed</c> è riaperto come nel harness non
/// generativo (<see cref="Rh051ResumeEngineTests"/>) e il dispatcher applicativo lo riprende senza riestrarre e senza reinviare i 30 batch committati (l'ultimo <c>expand</c> è recuperato dal checkpoint).
/// Differenze rispetto all'harness finto: i completamenti vanno al modello REALE attraverso un gestore con TETTO SPERIMENTALE (200 chiamate reali complessive della run 4 e della sua prosecuzione, contate dalla
/// telemetria persistita <c>InferenceCalls</c>): oltre il tetto la richiesta è RIFIUTATA PRIMA dell'invio. Il limite applicativo congelato (512) non è modificato. Strumentazione non invasiva: ActivityListener sulle
/// sorgenti già presenti, ledger delle chiamate (token, tempi, finish reason, hash delle richieste, risposte grezze) e timeline JSONL nella cartella delle evidenze (fuori da Git). Nessun retry automatico dell'intera
/// run (MaxAttempts della copia = tentativo corrente). Attivo solo con <c>BPROJECTS_RH051_REAL_DB</c>, <c>BPROJECTS_RH051_REAL_EVIDENCE</c> e l'opt-in DGX; altrimenti ignorato.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
public sealed class Rh051ResumeRealTests
{
    private const int ExperimentBudget = 200;

    private sealed class Jsonl(string path)
    {
        private readonly object _gate = new();

        public void Write(string type, JsonObject body)
        {
            body["type"] = type;
            body["ts"] = DateTime.UtcNow.ToString("O");
            lock (_gate)
            {
                File.AppendAllText(path, body.ToJsonString() + "\n", Encoding.UTF8);
            }
        }
    }

    /// <summary>Completamenti al DGX reale con tetto sperimentale applicato PRIMA dell'invio; ogni chiamata è registrata (richiesta e risposta grezze fuori da Git). Ogni altro POST è bloccato.</summary>
    private sealed class BudgetGuardHandler(HttpMessageHandler real, int budget, string evidence, Jsonl timeline) : HttpMessageHandler
    {
        private int _reserved;
        private readonly object _ledger = new();

        public int Sent => Volatile.Read(ref _reserved);

        public int BudgetRefusals { get; private set; }

        public List<string> Blocked { get; } = [];

        private static string StageOf(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var messages = doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => (Role: m.GetProperty("role").GetString()!, Text: TextOf(m.GetProperty("content")))).ToList();
                var request = new DocumentChatWorkflowTests.ChatRequest(
                    string.Join("\n", messages.Where(m => m.Role is "system" or "developer").Select(m => m.Text)), [.. messages.Where(m => m.Role is not ("system" or "developer"))]);
                return DocumentGapStageWorkflowTests.IsStage(request) ? "gap" : DocumentReconcileAliasWorkflowTests.ParseAlias(request.AllText).Stage;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private static string TextOf(JsonElement content) => content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Concat(content.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/completions", StringComparison.Ordinal))
            {
                int seq;
                lock (_ledger)
                {
                    if (_reserved >= budget)
                    {
                        BudgetRefusals++;
                        timeline.Write("budget_refusal", new JsonObject { ["budget"] = budget, ["reserved"] = _reserved });
                        throw new InvalidOperationException($"tetto sperimentale di {budget} chiamate reali raggiunto: nessun invio");
                    }

                    seq = ++_reserved;
                }

                await request.Content!.LoadIntoBufferAsync(cancellationToken);
                var requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
                var stage = StageOf(requestBody);
                var requestSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestBody)));
                var started = DateTime.UtcNow;
                var watch = Stopwatch.StartNew();
                timeline.Write("call_start", new JsonObject { ["seq"] = seq, ["stage"] = stage, ["requestSha256"] = requestSha });
                var entry = new JsonObject { ["seq"] = seq, ["stage"] = stage, ["startUtc"] = started.ToString("O"), ["requestSha256"] = requestSha, ["requestBytes"] = Encoding.UTF8.GetByteCount(requestBody) };
                try
                {
                    using var requestDoc = JsonDocument.Parse(requestBody);
                    foreach (var key in new[] { "max_tokens", "max_completion_tokens", "temperature", "model", "stream" })
                    {
                        if (requestDoc.RootElement.TryGetProperty(key, out var value))
                        {
                            entry["request_" + key] = JsonNode.Parse(value.GetRawText());
                        }
                    }

                    var response = await new HttpMessageInvoker(real, disposeHandler: false).SendAsync(request, cancellationToken);
                    await response.Content.LoadIntoBufferAsync(cancellationToken);
                    var text = await response.Content.ReadAsStringAsync(cancellationToken);
                    watch.Stop();
                    entry["endUtc"] = DateTime.UtcNow.ToString("O");
                    entry["httpMs"] = watch.ElapsedMilliseconds;
                    entry["status"] = (int)response.StatusCode;
                    entry["responseBytes"] = Encoding.UTF8.GetByteCount(text);
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;
                        foreach (var key in new[] { "id", "model", "usage", "timings", "system_fingerprint" })
                        {
                            if (root.TryGetProperty(key, out var value))
                            {
                                entry[key] = JsonNode.Parse(value.GetRawText());
                            }
                        }

                        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("finish_reason", out var finish))
                        {
                            entry["finish_reason"] = finish.GetString();
                        }
                    }
                    catch (JsonException)
                    {
                        entry["responseNotJson"] = true;
                    }

                    File.WriteAllText(Path.Combine(evidence, "raw", $"{seq:D4}-{stage}-request.json"), requestBody, Encoding.UTF8);
                    File.WriteAllText(Path.Combine(evidence, "raw", $"{seq:D4}-{stage}-response.json"), text, Encoding.UTF8);
                    return response;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    watch.Stop();
                    entry["endUtc"] = DateTime.UtcNow.ToString("O");
                    entry["httpMs"] = watch.ElapsedMilliseconds;
                    entry["error"] = ex.GetType().Name + ": " + ex.Message;
                    throw;
                }
                finally
                {
                    lock (_ledger)
                    {
                        File.AppendAllText(Path.Combine(evidence, "inference-ledger.jsonl"), entry.ToJsonString() + "\n", Encoding.UTF8);
                    }

                    timeline.Write("call_end", new JsonObject { ["seq"] = seq, ["stage"] = stage, ["httpMs"] = watch.ElapsedMilliseconds, ["error"] = entry["error"]?.GetValue<string>() });
                }
            }

            var allowed = request.Method == HttpMethod.Get || (request.Method == HttpMethod.Post && (path.EndsWith("/tokenize", StringComparison.Ordinal) || path.EndsWith("/apply-template", StringComparison.Ordinal)));
            if (!allowed)
            {
                Blocked.Add($"{request.Method} {path}");
                throw new InvalidOperationException($"richiesta non ammessa: {request.Method} {path}");
            }

            return await new HttpMessageInvoker(real, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task The_engine_resumes_the_reopened_failed_run4_on_the_real_model_within_the_experiment_budget()
    {
        var scratch = Environment.GetEnvironmentVariable("BPROJECTS_RH051_REAL_DB");
        var evidence = Environment.GetEnvironmentVariable("BPROJECTS_RH051_REAL_EVIDENCE");
        Assert.SkipWhen(string.IsNullOrEmpty(scratch) || string.IsNullOrEmpty(evidence), "BPROJECTS_RH051_REAL_DB / BPROJECTS_RH051_REAL_EVIDENCE non impostate: prosecuzione reale non richiesta.");
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(evidence!, "raw"));
        var timeline = new Jsonl(Path.Combine(evidence!, "timeline.jsonl"));

        // Opzioni AI del profilo RH-051 validato (finestra 65536, O=8192, M=1024, thinking spento, fattore di estrazione 24 solo sperimentale).
        var ai = TestEnvironment.RequireProductionLikeOptions();
        ai.ContextWindowTokens = 65536;
        ai.SafetyMarginTokens = 1024;
        ai.Phases.Analysis.MaxOutputTokens = 8192;
        ai.Phases.Analysis.EnableThinking = false;
        ai.ExtractionOutputExpansion = 24;

        // Tetto sperimentale = 200 − chiamate reali già persistite (telemetria della run 4): vincolo applicato prima dell'invio dal gestore.
        var prior = Convert.ToInt32(await Rh051ResumeEngineTests.Scalar(scratch!, "SELECT count(*) FROM \"InferenceCalls\""));
        var budget = ExperimentBudget - prior;
        Assert.InRange(budget, 1, ExperimentBudget);
        timeline.Write("harness", new JsonObject { ["event"] = "budget", ["experimentBudget"] = ExperimentBudget, ["priorRealCalls"] = prior, ["remaining"] = budget, ["frozenRunLimit"] = 512 });

        var handler = new BudgetGuardHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }, budget, evidence!, timeline);

        // Span già presenti (DocumentTiming e sorgenti MAF/Extensions.AI): nessuna modifica del motore. I tag che potrebbero contenere testo del modello o dei documenti non sono registrati.
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == DocumentTiming.SourceName || s.Name.StartsWith("Microsoft.Agents", StringComparison.Ordinal) || s.Name.Contains("Microsoft.Extensions.AI", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                var tags = new JsonObject();
                foreach (var tag in a.TagObjects)
                {
                    if (!tag.Key.Contains("message", StringComparison.OrdinalIgnoreCase) && !tag.Key.Contains("content", StringComparison.OrdinalIgnoreCase) && !tag.Key.Contains("prompt", StringComparison.OrdinalIgnoreCase)
                        && !tag.Key.Contains("input", StringComparison.OrdinalIgnoreCase) && !tag.Key.Contains("output", StringComparison.OrdinalIgnoreCase) && !tag.Key.Contains("instructions", StringComparison.OrdinalIgnoreCase))
                    {
                        tags[tag.Key] = tag.Value?.ToString();
                    }
                }

                timeline.Write("span", new JsonObject { ["source"] = a.Source.Name, ["name"] = a.OperationName, ["startUtc"] = a.StartTimeUtc.ToString("O"), ["durationMs"] = a.Duration.TotalMilliseconds, ["status"] = a.Status.ToString(), ["tags"] = tags });
            },
        };
        ActivitySource.AddActivityListener(listener);

        await using var app = await ApiHarness.StartAsync(scratch!, prepare: false, services =>
        {
            services.PostConfigure<AiOptions>(o => Rh051ResumeEngineTests.CopyOptions(ai, o));
            services.Configure<ExecutionOptions>(o => o.AnalysisRunDeadlineSeconds = TestEnvironment.ProductionAnalysisRunDeadlineSeconds);
            services.AddSingleton(sp => new InternalModelClient(sp.GetRequiredService<IOptions<AiOptions>>().Value, handler));
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
                    sp.GetRequiredService<IServiceScopeFactory>(), AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model, sp.GetRequiredService<ModelRuntimeMonitor>(), documents.Value.Calibration.ToCalibration()),
                    model, documents);
            });
        });

        using (var scope = app.Services.CreateScope())
        {
            Assert.NotNull(scope.ServiceProvider.GetService<DocumentAnalysisWorkflow>());   // costruisce l'host: il dispatcher parte
        }

        var run = (Guid)(await Rh051ResumeEngineTests.Scalar(scratch!, "SELECT \"Id\" FROM \"ProcessingRuns\" WHERE \"Kind\"::text = 'Analysis' ORDER BY \"RequestedAt\" DESC LIMIT 1"))!;
        var before = await Rh051ResumeEngineTests.Snapshot(scratch!, run);
        Assert.Equal("Failed", before.Status);
        Assert.Equal(30, before.Committed.Count);
        Assert.Equal(31, before.CallCount);
        Assert.Equal(1, before.AttemptCount);
        timeline.Write("harness", new JsonObject { ["event"] = "before", ["runId"] = run.ToString(), ["status"] = before.Status, ["callCount"] = before.CallCount, ["attempt"] = before.AttemptCount, ["fence"] = before.Fence, ["committed"] = before.Committed.Count, ["batches"] = before.Batches });

        // Unica operazione sul run (sulla copia): riapertura + una sola esecuzione (MaxAttempts = tentativo corrente + 1: nessun retry automatico) + scadenze rinnovate (le originali sono scadute).
        const string reopen = """
            UPDATE "ProcessingRuns" SET "Status" = 'Queued', "ErrorCode" = NULL, "ErrorMessage" = NULL, "CompletedAt" = NULL, "LeaseOwner" = NULL, "LeaseUntil" = NULL, "NextAttemptAt" = NULL,
                   "MaxAttempts" = "AttemptCount" + 1,
                   "DeadlineAt" = now() + interval '5 hours', "EffectiveDeadlineAt" = now() + interval '5 hours', "HardDeadlineAt" = now() + interval '5 hours'
            WHERE "Id" = @r AND "Status" = 'Failed'
            """;
        await Rh051ResumeEngineTests.Execute(scratch!, run, reopen);
        File.WriteAllText(Path.Combine(evidence!, "reopen-operation.sql"), reopen, Encoding.UTF8);
        timeline.Write("harness", new JsonObject { ["event"] = "reopened" });

        // Poll leggero (sola lettura): una riga di timeline a ogni variazione.
        var deadline = DateTime.UtcNow.AddHours(5);
        string status;
        var last = "";
        do
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            await using var connection = new NpgsqlConnection(scratch);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(
                "SELECT p.\"Status\"::text, coalesce(p.\"CallCount\",0), p.\"AttemptCount\", p.\"FencingToken\", p.\"LeaseUntil\", p.\"LastObservedStage\", (SELECT count(*) FROM \"DerivedBatches\" b WHERE b.\"OwnerId\"=p.\"Id\"), "
                + "(SELECT count(*) FROM \"WorkOutputs\" o WHERE o.\"OwnerId\"=p.\"Id\" AND o.\"Status\"::text='Committed') FROM \"ProcessingRuns\" p WHERE p.\"Id\" = @r", connection);
            command.Parameters.AddWithValue("r", run);
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            status = reader.GetString(0);
            var key = $"{status}|{reader.GetInt32(1)}|{reader.GetInt32(2)}|{reader.GetInt64(3)}|{reader.GetInt64(6)}|{reader.GetInt64(7)}|{(reader.IsDBNull(5) ? "" : reader.GetString(5))}";
            if (key != last)
            {
                last = key;
                timeline.Write("state", new JsonObject
                {
                    ["status"] = status, ["callCount"] = reader.GetInt32(1), ["attempt"] = reader.GetInt32(2), ["fence"] = reader.GetInt64(3), ["leaseUntil"] = reader.IsDBNull(4) ? null : reader.GetDateTime(4).ToString("O"),
                    ["lastObservedStage"] = reader.IsDBNull(5) ? null : reader.GetString(5), ["batches"] = reader.GetInt64(6), ["committed"] = reader.GetInt64(7), ["realCallsSent"] = handler.Sent,
                });
            }
        }
        while (status is "Queued" or "Running" && DateTime.UtcNow < deadline);

        var after = await Rh051ResumeEngineTests.Snapshot(scratch!, run);
        var report = new JsonObject
        {
            ["status"] = after.Status, ["errorCode"] = after.ErrorCode, ["errorMessage"] = after.ErrorMessage, ["callCountBefore"] = before.CallCount, ["callCountAfter"] = after.CallCount,
            ["attemptBefore"] = before.AttemptCount, ["attemptAfter"] = after.AttemptCount, ["fenceBefore"] = before.Fence, ["fenceAfter"] = after.Fence,
            ["priorRealCalls"] = prior, ["realCallsSent"] = handler.Sent, ["totalRealCalls"] = prior + handler.Sent, ["experimentBudget"] = ExperimentBudget, ["budgetRefusals"] = handler.BudgetRefusals,
            ["committedBefore"] = before.Committed.Count, ["committedAfter"] = after.Committed.Count, ["batchesBefore"] = before.Batches, ["batchesAfter"] = after.Batches,
            ["extractionJobsBefore"] = before.ExtractionJobs, ["extractionJobsAfter"] = after.ExtractionJobs, ["extractionOutputsBefore"] = before.ExtractionOutputs, ["extractionOutputsAfter"] = after.ExtractionOutputs,
            ["blocked"] = new JsonArray([.. handler.Blocked.Select(s => (JsonNode?)JsonValue.Create(s))]),
        };
        timeline.Write("harness", new JsonObject { ["event"] = "ended", ["status"] = after.Status, ["errorCode"] = after.ErrorCode });
        File.WriteAllText(Path.Combine(evidence!, "run-report.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        TestContext.Current.SendDiagnosticMessage(report.ToJsonString());

        // Integrità: ciò che è già committato non cambia, nessuna riestrazione, tetto rispettato, nessuna richiesta fuori elenco. L'esito della run (Completed/Failed) NON è un'asserzione: è l'evidenza.
        Assert.Empty(handler.Blocked);
        Assert.True(handler.Sent <= budget);
        Assert.Equal(before.ExtractionJobs, after.ExtractionJobs);
        Assert.Equal(before.ExtractionOutputs, after.ExtractionOutputs);
        foreach (var (batch, hash) in before.Committed)
        {
            Assert.Equal(hash, after.Committed[batch]);
        }
    }
}
