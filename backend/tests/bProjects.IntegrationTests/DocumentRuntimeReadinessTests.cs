using System.Net;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Documents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace bProjects.IntegrationTests;

/// <summary>
/// T126 — readiness bounded del runtime nel preflight di classificazione. Runtime FINTO (llama.cpp simulato) e clock VIRTUALE: il tempo avanza solo
/// quando il test lo spinge, quindi attese di decine di secondi durano millisecondi. Nessun database, nessun modello: ogni test dimostra anche che non
/// parte alcuna richiesta di generazione (/v1/chat/completions) prima della classificazione.
/// </summary>
public sealed class DocumentRuntimeReadinessTests
{
    internal const string Model = "Qwen-A";
    private const string Schema = """{"type":"object"}""";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static AiOptions Options(int maxOutput = 100, int margin = 20) => new()
    {
        Endpoint = "http://dgx.internal:8080/v1/", ModelId = Model, ApiKey = "test-key", ContextWindowTokens = 2000,
        MaxOutputTokens = maxOutput, SafetyMarginTokens = margin, RuntimeCheckSeconds = 60,
    };

    /// <summary>Clock virtuale: i timer scattano quando il test li spinge, nell'ordine delle scadenze, spostando il tempo alla loro scadenza.</summary>
    internal sealed class VirtualTime : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<VirtualTimer> _timers = [];
        private DateTimeOffset _now = new(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Start { get; } = new(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);

        public TimeSpan Elapsed
        {
            get
            {
                lock (_gate)
                {
                    return _now - Start;
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new VirtualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Spinge il tempo finché il task non termina (o il predicato è vero). Tempo reale minimo: yield fra uno scatto e l'altro.</summary>
        public async Task RunAsync(Task task, Func<bool>? until = null)
        {
            for (var idle = 0; !task.IsCompleted && !(until?.Invoke() ?? false);)
            {
                VirtualTimer? next;
                lock (_gate)
                {
                    next = _timers.Where(t => t.Due is not null).OrderBy(t => t.Due).FirstOrDefault();
                }

                if (next is null)
                {
                    await Task.Delay(2, Ct);
                    Assert.True(++idle < 2_000, "il compito non termina e nessun timer è in attesa");
                    continue;
                }

                idle = 0;
                for (var settle = 0; settle < 4 && !task.IsCompleted; settle++)
                {
                    await Task.Delay(30, Ct);   // quiete: lascia terminare il lavoro reale (HTTP finto) e registrare gli altri timer prima di scegliere il prossimo
                }

                if (task.IsCompleted)
                {
                    break;
                }

                lock (_gate)
                {
                    next = _timers.Where(t => t.Due is not null).OrderBy(t => t.Due).FirstOrDefault();
                    if (next is null)
                    {
                        continue;
                    }

                    _now = next.Due!.Value > _now ? next.Due.Value : _now;
                    next.Due = null;
                }

                next.Fire();
                await Task.Delay(30, Ct);   // lascia propagare lo scatto prima di scegliere il prossimo
            }
        }

        private sealed class VirtualTimer(VirtualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                    if (dueTime == Timeout.InfiniteTimeSpan)
                    {
                        Due = null;
                        return true;
                    }

                    Due = owner._now + dueTime;
                    owner._timers.Add(this);
                    return true;
                }
            }

            public void Fire() => ThreadPool.QueueUserWorkItem(_ => callback(state));

            public void Dispose()
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                    Due = null;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>llama.cpp simulato in router mode: lo stato del modello nell'elenco è deciso dal numero della sonda (1ª, 2ª...).</summary>
    internal sealed class FakeRuntime : HttpMessageHandler
    {
        public Func<int, string> StatusAt { get; set; } = _ => "loaded";

        public bool Listed { get; set; } = true;

        public int? Window { get; set; } = 2000;

        public bool TokenizerAvailable { get; set; } = true;

        public bool TemplateAvailable { get; set; } = true;

        public List<string> Paths { get; } = [];

        public int Count(string path)
        {
            lock (Paths)
            {
                return Paths.Count(p => p == path);
            }
        }

        public int Completions => Count("/v1/chat/completions");

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            int probe;
            lock (Paths)
            {
                Paths.Add(path);
                probe = Paths.Count(p => p == "/v1/models");
            }

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            switch (path)
            {
                case "/v1/models":
                    var id = Listed ? Model : "altro-modello";
                    return Json("{\"object\":\"list\",\"data\":[{\"id\":\"" + id + "\",\"object\":\"model\",\"status\":{\"value\":\"" + StatusAt(probe) + "\"}}]}");
                case "/props":
                    return Window is { } w
                        ? Json("{\"default_generation_settings\":{\"n_ctx\":" + w + ",\"params\":{\"n_predict\":-1}},\"total_slots\":1}")
                        : Json("""{"total_slots":1}""");
                case "/tokenize":
                {
                    if (!TokenizerAvailable)
                    {
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }

                    using var doc = JsonDocument.Parse(body);
                    var words = doc.RootElement.GetProperty("content").GetString()!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                    return Json("{\"tokens\":[" + string.Join(',', Enumerable.Range(0, words)) + "]}");
                }

                case "/apply-template":
                    return TemplateAvailable ? Json("""{"prompt":"[user] x [/user]"}""") : new HttpResponseMessage(HttpStatusCode.NotFound);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }

    /// <summary>Un «processo»: monitor, client e preflight nuovi sullo stesso runtime e sullo stesso clock (il riavvio azzera ogni stato in memoria).</summary>
    internal sealed class Process : IDisposable
    {
        private readonly InternalModelClient _client;

        public Process(FakeRuntime runtime, VirtualTime time, AiOptions? options = null)
        {
            var o = options ?? Options();
            _client = new InternalModelClient(o, runtime);
            Monitor = new ModelRuntimeMonitor(_client, o, NullLogger<ModelRuntimeMonitor>.Instance, time);
            Preflight = new DocumentPreflight(o, Monitor, _client);
        }

        public ModelRuntimeMonitor Monitor { get; }

        public DocumentPreflight Preflight { get; }

        public Task<RegimeMeasurement> MeasureAsync(TimeSpan? remaining, CancellationToken ct = default) =>
            RegimeMeasurer.MeasureAsync(
                new RegimePayload(
                    AiPhase.Chat, new DocumentPayload(AiPhase.Chat, "sys", [new ChatMessage(ChatRole.User, "uno due tre")], Schema), Schema, Preflight),
                ct, remaining);

        public void Dispose() => _client.Dispose();
    }

    private static async Task<RegimeMeasurement> MeasureAsync(Process process, VirtualTime time, TimeSpan? remaining)
    {
        var task = process.MeasureAsync(remaining, Ct);
        await time.RunAsync(task);
        return await task;
    }

    private static DocumentRegimeDecision Classify(RegimeMeasurement measurement)
    {
        var limits = new DocumentOptions().ToLimits();
        var now = DateTimeOffset.UtcNow;
        var requested = now.AddSeconds(-5);
        var input = DocumentRootClassifier.BuildInput(measurement, requested, requested.AddSeconds(limits.ExtendedDeadlineSeconds), now, limits);
        return DocumentRegimePolicy.Classify(input, limits.ExtendedDeadlineSeconds);
    }

    [Fact]
    public async Task A_downloaded_model_is_waited_for_with_a_fixed_cap_then_fails_model_unloaded_without_forcing_a_load_or_an_inference()
    {
        var runtime = new FakeRuntime { StatusAt = _ => "unloaded" };
        var time = new VirtualTime();
        using var process = new Process(runtime, time);

        var measurement = await MeasureAsync(process, time, TimeSpan.FromHours(1));

        Assert.Equal("model_unloaded", measurement.RuntimeFailure);
        Assert.Null(measurement.RuntimeFingerprint);
        Assert.Equal(1 + ModelRuntimeMonitor.ReadinessBackoff.Length, runtime.Count("/v1/models"));   // sonda iniziale + un tetto fisso di riprove
        Assert.Equal(ModelRuntimeMonitor.ReadinessBackoff.Aggregate(TimeSpan.Zero, (a, b) => a + b), time.Elapsed);
        Assert.Equal(0, runtime.Count("/props"));                                                      // /props di un modello scaricato potrebbe caricarlo
        Assert.Equal(0, runtime.Completions);
        var decision = Classify(measurement);
        Assert.Equal((ClassificationOutcome.Fail, "model_unloaded", "runtime_unverified"), (decision.Outcome, decision.Reason, decision.ErrorCode));
    }

    [Fact]
    public async Task A_slow_load_that_becomes_ready_within_the_deadline_is_classified_normally_with_no_inference()
    {
        var runtime = new FakeRuntime { StatusAt = probe => probe < 3 ? "loading" : "loaded" };
        var time = new VirtualTime();
        using var process = new Process(runtime, time);

        var measurement = await MeasureAsync(process, time, TimeSpan.FromMinutes(10));

        Assert.Null(measurement.RuntimeFailure);
        Assert.False(string.IsNullOrEmpty(measurement.RuntimeFingerprint));
        Assert.Equal(2000, measurement.WindowTokens);
        Assert.Equal(TimeSpan.FromSeconds(3), time.Elapsed);                                          // 1 s + 2 s di backoff, poi pronto
        Assert.Equal(3, runtime.Count("/v1/models"));
        Assert.Equal(0, runtime.Completions);
        Assert.NotEqual(ClassificationOutcome.Fail, Classify(measurement).Outcome);
    }

    [Fact]
    public async Task A_load_that_outlasts_the_deadline_fails_deadline_exceeded_at_the_original_deadline_never_later()
    {
        var runtime = new FakeRuntime { StatusAt = _ => "loading" };
        var time = new VirtualTime();
        using var process = new Process(runtime, time);

        var measurement = await MeasureAsync(process, time, TimeSpan.FromSeconds(5));

        Assert.Equal("deadline_exceeded", measurement.RuntimeFailure);
        Assert.Equal(TimeSpan.FromSeconds(5), time.Elapsed);                                          // la deadline non si sposta e non si allunga
        Assert.Equal(0, runtime.Completions);
        Assert.Equal("deadline_exceeded", Classify(measurement).ErrorCode);
    }

    [Fact]
    public async Task A_process_restart_mid_wait_keeps_the_original_deadline()
    {
        var runtime = new FakeRuntime { StatusAt = _ => "loading" };
        var time = new VirtualTime();
        var deadline = TimeSpan.FromSeconds(10);

        using (var first = new Process(runtime, time))
        using (var interrupted = new CancellationTokenSource())
        {
            var attempt = first.MeasureAsync(deadline, interrupted.Token);
            await time.RunAsync(attempt, until: () => runtime.Count("/v1/models") >= 2);
            await interrupted.CancelAsync();                                                           // il processo muore a metà attesa
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        }

        var elapsedBeforeRestart = time.Elapsed;
        using var second = new Process(runtime, time);
        var measurement = await MeasureAsync(second, time, deadline - elapsedBeforeRestart);          // residuo dal RequestedAt originale

        Assert.Equal("deadline_exceeded", measurement.RuntimeFailure);
        Assert.Equal(deadline, time.Elapsed);
        Assert.Equal(0, runtime.Completions);
    }

    [Fact]
    public async Task Two_concurrent_requests_share_the_same_bounded_wait_without_duplicate_probes_or_inferences()
    {
        var baselineRuntime = new FakeRuntime { StatusAt = probe => probe < 3 ? "loading" : "loaded" };
        var baselineTime = new VirtualTime();
        using (var baseline = new Process(baselineRuntime, baselineTime))
        {
            await MeasureAsync(baseline, baselineTime, TimeSpan.FromMinutes(10));
        }

        var runtime = new FakeRuntime { StatusAt = probe => probe < 3 ? "loading" : "loaded" };
        var time = new VirtualTime();
        using var process = new Process(runtime, time);
        var a = process.MeasureAsync(TimeSpan.FromMinutes(10), Ct);
        var b = process.MeasureAsync(TimeSpan.FromMinutes(10), Ct);
        await time.RunAsync(Task.WhenAll(a, b));
        var results = await Task.WhenAll(a, b);

        Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.RuntimeFingerprint)));
        Assert.Equal(baselineRuntime.Count("/v1/models"), runtime.Count("/v1/models"));
        Assert.Equal(baselineRuntime.Count("/props"), runtime.Count("/props"));
        Assert.Equal(0, runtime.Completions);
        Assert.Equal(baselineTime.Elapsed, time.Elapsed);
    }

    [Fact]
    public async Task A_model_the_server_does_not_expose_fails_model_not_available_immediately()
    {
        var runtime = new FakeRuntime { Listed = false };
        var time = new VirtualTime();
        using var process = new Process(runtime, time);

        var measurement = await MeasureAsync(process, time, TimeSpan.FromHours(1));

        Assert.Equal("model_not_available", measurement.RuntimeFailure);
        Assert.Equal(TimeSpan.Zero, time.Elapsed);
        Assert.Equal(1, runtime.Count("/v1/models"));
        Assert.Equal(0, runtime.Completions);
        var decision = Classify(measurement);
        Assert.Equal((ClassificationOutcome.Fail, "model_not_available", "model_not_available"), (decision.Outcome, decision.Reason, decision.ErrorCode));
    }

    [Fact]
    public async Task A_runtime_window_smaller_than_the_configured_one_fails_context_window_mismatch_immediately()
    {
        var runtime = new FakeRuntime { Window = 1000 };
        var time = new VirtualTime();
        using var process = new Process(runtime, time);

        var measurement = await MeasureAsync(process, time, TimeSpan.FromHours(1));

        Assert.Equal("context_window_mismatch", measurement.RuntimeFailure);
        Assert.Equal(TimeSpan.Zero, time.Elapsed);
        Assert.Equal(0, runtime.Completions);
        Assert.Equal("context_window_mismatch", Classify(measurement).ErrorCode);
    }

    [Fact]
    public async Task A_window_too_small_for_output_and_margin_fails_context_window_mismatch_immediately()
    {
        var runtime = new FakeRuntime();
        var time = new VirtualTime();
        using var process = new Process(runtime, time, Options(maxOutput: 1990, margin: 20));      // W=2000, O+M=2010: B <= 0

        var measurement = await MeasureAsync(process, time, TimeSpan.FromHours(1));

        Assert.Equal("context_window_mismatch", measurement.RuntimeFailure);
        Assert.Equal(TimeSpan.Zero, time.Elapsed);
        Assert.Equal(0, runtime.Completions);
    }

    [Fact]
    public async Task An_unreachable_server_is_waited_for_within_the_cap_and_then_fails_runtime_unreachable()
    {
        var runtime = new FakeRuntime { StatusAt = _ => throw new HttpRequestException("rete") };
        var time = new VirtualTime();
        using var process = new Process(runtime, time);

        var measurement = await MeasureAsync(process, time, TimeSpan.FromHours(1));

        Assert.Equal("runtime_unreachable", measurement.RuntimeFailure);
        Assert.Equal(1 + ModelRuntimeMonitor.ReadinessBackoff.Length, runtime.Count("/v1/models"));
        Assert.Equal(0, runtime.Completions);
        Assert.Equal((ClassificationOutcome.Fail, "runtime_unverified"), (Classify(measurement).Outcome, Classify(measurement).ErrorCode));
    }

    [Fact]
    public async Task An_unverifiable_tokenizer_is_unavailable_never_ordinary_and_sends_no_inference()
    {
        var runtime = new FakeRuntime { TokenizerAvailable = false, TemplateAvailable = false };
        var time = new VirtualTime();
        using var process = new Process(runtime, time);

        var measurement = await MeasureAsync(process, time, TimeSpan.FromMinutes(10));

        Assert.Equal(TokenCountQuality.Unavailable, measurement.TokenQuality);
        Assert.Null(measurement.PayloadTokens);
        var decision = Classify(measurement);
        Assert.Equal((ClassificationOutcome.AdmitExtended, "workload_unknown", ExecutionRegime.Extended), (decision.Outcome, decision.Reason, decision.Regime));
        Assert.Contains("token_count", decision.Unknowns);
        Assert.Equal(0, runtime.Completions);
    }

    [Fact]
    public async Task No_retry_happens_beyond_the_cap_even_after_a_long_time()
    {
        var runtime = new FakeRuntime { StatusAt = _ => "unloaded" };
        var time = new VirtualTime();
        using var process = new Process(runtime, time);
        await MeasureAsync(process, time, TimeSpan.FromHours(1));
        var probes = runtime.Count("/v1/models");

        // Il cache della sonda è ancora valido: una nuova misura non sonda di nuovo fuori dal tetto del giro.
        var again = process.MeasureAsync(TimeSpan.FromHours(1), Ct);
        await time.RunAsync(again);
        await again;

        Assert.True(runtime.Count("/v1/models") <= probes + ModelRuntimeMonitor.ReadinessBackoff.Length);
        Assert.Equal(0, runtime.Completions);
        Assert.DoesNotContain("/v1/chat/completions", runtime.Paths);
    }

    [Theory]
    [InlineData("model_loading", "runtime_unverified")]
    [InlineData("runtime_unreachable", "runtime_unverified")]
    [InlineData("tokenizer_unverified", "runtime_unverified")]
    [InlineData("model_not_available", "model_not_available")]
    [InlineData("context_window_mismatch", "context_window_mismatch")]
    [InlineData("deadline_exceeded", "deadline_exceeded")]
    public void Each_failure_cause_is_a_distinct_classification_reason_with_its_error_code(string cause, string code)
    {
        var decision = Classify(new RegimeMeasurement { RuntimeFailure = cause, PlannedCalls = 1, HasDependencies = false });

        Assert.Equal((ClassificationOutcome.Fail, cause, code), (decision.Outcome, decision.Reason, decision.ErrorCode));
    }

    [Fact]
    public void A_measurement_without_a_failure_cause_keeps_the_input_hash_of_the_previous_policy_inputs()
    {
        var limits = new DocumentOptions().ToLimits();
        var now = DateTimeOffset.UtcNow;
        var plain = DocumentRootClassifier.BuildInput(DocumentRootClassificationInputTests.Fit, now, now.AddSeconds(3600), now, limits);
        var withNull = DocumentRootClassifier.BuildInput(DocumentRootClassificationInputTests.Fit with { RuntimeFailure = null }, now, now.AddSeconds(3600), now, limits);

        Assert.Equal(DocumentRegimePolicy.ComputeInputHash(plain), DocumentRegimePolicy.ComputeInputHash(withNull));
        Assert.DoesNotContain("runtimeFailure", System.Text.Json.JsonSerializer.Serialize(plain));
    }
}

/// <summary>
/// T126 (PG) — la readiness bounded nel classificatore dei root, dal Pending al ClassificationRecord. PostgreSQL reale; runtime FINTO e clock VIRTUALE
/// (stessi doppi dei test deterministici): il residuo della deadline è quello ORIGINALE dal RequestedAt del root, letto dal database; nessuna
/// richiesta di generazione parte mai, né prima né dopo il fallimento. Nessun DGX, nessun modello.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentRuntimeReadinessRootTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Sonda reale (RegimeMeasurer + preflight sul runtime finto) che registra il residuo della deadline ricevuto dal classificatore.</summary>
    private sealed class ReadinessProbe(DocumentRuntimeReadinessTests.Process process, DocumentRuntimeReadinessTests.VirtualTime time, bool driveTime = true) : IDocumentRegimeProbe
    {
        private readonly List<TimeSpan> _remaining = [];

        public IReadOnlyList<TimeSpan> Remaining
        {
            get
            {
                lock (_remaining)
                {
                    return [.. _remaining];
                }
            }
        }

        public Task<RegimeMeasurement> MeasureAsync(ClassificationOwnerKind kind, Guid rootId, CancellationToken ct) =>
            throw new InvalidOperationException("il classificatore deve passare il residuo della deadline originale al preflight");

        public async Task<RegimeMeasurement> MeasureAsync(ClassificationOwnerKind kind, Guid rootId, TimeSpan remaining, CancellationToken ct)
        {
            lock (_remaining)
            {
                _remaining.Add(remaining);
            }

            var measure = process.MeasureAsync(remaining, ct);
            if (driveTime)
            {
                await time.RunAsync(measure);
            }

            return await measure;
        }
    }

    private static Task<string> Text(ParsingDatabase db, SeededRoot root, string column) => DocumentRootClassificationTests.Text(db, root, column);

    private static async Task<(string, string, string)> TimersAsync(ParsingDatabase db, SeededRoot root) =>
        (await Text(db, root, "RequestedAt"), await Text(db, root, "EffectiveDeadlineAt"), await Text(db, root, "HardDeadlineAt"));

    private static Task<string> RecordAsync(ParsingDatabase db, SeededRoot root, string column) =>
        db.ScalarAsync<string>($"SELECT \"{column}\"::text FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'");

    public enum Case
    {
        Unloaded,
        Missing,
        Window,
        Deadline,
    }

    [Theory]
    [InlineData(RootKind.Turn, Case.Unloaded, "model_unloaded", "runtime_unverified")]
    [InlineData(RootKind.Run, Case.Unloaded, "model_unloaded", "runtime_unverified")]
    [InlineData(RootKind.Turn, Case.Missing, "model_not_available", "model_not_available")]
    [InlineData(RootKind.Run, Case.Missing, "model_not_available", "model_not_available")]
    [InlineData(RootKind.Turn, Case.Window, "context_window_mismatch", "context_window_mismatch")]
    [InlineData(RootKind.Run, Case.Window, "context_window_mismatch", "context_window_mismatch")]
    [InlineData(RootKind.Turn, Case.Deadline, "deadline_exceeded", "deadline_exceeded")]
    [InlineData(RootKind.Run, Case.Deadline, "deadline_exceeded", "deadline_exceeded")]
    public async Task An_exhausted_readiness_fails_the_root_with_the_distinct_reason_and_the_case_code_without_sending_or_touching_the_timers(
        RootKind kind, Case failure, string reason, string code)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var runtime = new DocumentRuntimeReadinessTests.FakeRuntime
        {
            StatusAt = failure == Case.Deadline ? _ => "loading" : failure == Case.Unloaded ? _ => "unloaded" : _ => "loaded",
            Listed = failure != Case.Missing,
            Window = failure == Case.Window ? 1000 : 2000,
        };
        var time = new DocumentRuntimeReadinessTests.VirtualTime();
        using var process = new DocumentRuntimeReadinessTests.Process(runtime, time);
        var probe = new ReadinessProbe(process, time);
        await using var rig = DocumentRootClassificationTests.Rig.Create(database, probe);
        var elapsed = failure == Case.Deadline ? 3_594 : 5;
        var root = await DocumentRootClassificationTests.SeedPendingAsync(database, kind, elapsed);
        var timers = await TimersAsync(database, root);

        var result = await rig.Classifier.ClassifyNextAsync(turns: true, analysisRuns: true, documentRuns: true, Ct);

        Assert.Equal(ClassificationAttempt.Failed, result!.Attempt);
        Assert.Equal(("Failed", code, "Rejected", "<null>"),
            (await Text(database, root, "Status"), await Text(database, root, "ErrorCode"),
             await Text(database, root, "ClassificationStatus"), await Text(database, root, "InitialRegime")));
        Assert.Equal(("Fail", reason), (await RecordAsync(database, root, "Outcome"), await RecordAsync(database, root, "Reason")));
        Assert.Equal(1, await DocumentRootClassificationTests.Records(database, root));
        Assert.Equal(timers, await TimersAsync(database, root));                                         // la deadline non si riavvia né si allunga
        Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM \"RegimeTransitions\" WHERE \"RootId\" = '{root.Id}'"));   // attendere non è promuovere

        var remaining = Assert.Single(probe.Remaining);
        Assert.InRange(remaining, TimeSpan.Zero, TimeSpan.FromSeconds(3_600 - elapsed));                 // residuo dal RequestedAt originale, mai 3600 s interi
        switch (failure)
        {
            case Case.Deadline:
                Assert.InRange(time.Elapsed, remaining - TimeSpan.FromMilliseconds(1), remaining);          // l'attesa finisce alla deadline originale (timer al ms), non oltre
                break;
            case Case.Unloaded:
                Assert.Equal(ModelRuntimeMonitor.ReadinessBackoff.Aggregate(TimeSpan.Zero, (a, b) => a + b), time.Elapsed);   // tetto fisso, non la deadline
                break;
            default:
                Assert.Equal(TimeSpan.Zero, time.Elapsed);                                               // fallimento immediato
                break;
        }

        Assert.Equal(0, runtime.Completions);
        Assert.Null(await rig.ClaimAsync());
        Assert.Empty(rig.Executor.StatusAtInvocation);
    }

    [Fact]
    public async Task A_slow_load_that_becomes_ready_within_the_deadline_classifies_the_turn_then_the_dispatcher_executes_it_with_no_inference_before()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var runtime = new DocumentRuntimeReadinessTests.FakeRuntime { StatusAt = probe => probe < 3 ? "loading" : "loaded" };
        var time = new DocumentRuntimeReadinessTests.VirtualTime();
        using var process = new DocumentRuntimeReadinessTests.Process(runtime, time);
        await using var rig = DocumentRootClassificationTests.Rig.Create(database, new ReadinessProbe(process, time));
        var root = await DocumentRootClassificationTests.SeedPendingAsync(database, RootKind.Turn);

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));                                              // passo di classificazione: nessun invio
        Assert.Empty(rig.Executor.StatusAtInvocation);
        Assert.Equal(0, runtime.Completions);
        Assert.Equal("Classified", await Text(database, root, "ClassificationStatus"));
        Assert.NotEqual("Fail", await RecordAsync(database, root, "Outcome"));
        Assert.Equal(TimeSpan.FromSeconds(3), time.Elapsed);                                             // 1 s + 2 s di backoff, poi pronto

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));                                              // claim ed esecuzione solo dopo la classificazione
        Assert.Equal(["Classified"], rig.Executor.StatusAtInvocation);
        Assert.Equal(1, await DocumentRootClassificationTests.Records(database, root));
    }

    [Fact]
    public async Task A_process_restart_mid_wait_leaves_the_root_pending_and_the_next_process_waits_only_for_the_residual_of_the_original_deadline()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var runtime = new DocumentRuntimeReadinessTests.FakeRuntime { StatusAt = _ => "loading" };
        var root = await DocumentRootClassificationTests.SeedPendingAsync(database, RootKind.Turn, elapsed: 3_590);   // restano ~10 s al cap
        var timers = await TimersAsync(database, root);
        TimeSpan firstRemaining;

        var firstTime = new DocumentRuntimeReadinessTests.VirtualTime();
        using (var first = new DocumentRuntimeReadinessTests.Process(runtime, firstTime))
        {
            var probe = new ReadinessProbe(first, firstTime, driveTime: false);
            await using var rig = DocumentRootClassificationTests.Rig.Create(database, probe);
            using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var attempt = rig.Classifier.ClassifyAsync(ClassificationOwnerKind.ChatTurn, root.Id, interrupted.Token);
            await firstTime.RunAsync(attempt, until: () => runtime.Count("/v1/models") >= 2);
            await interrupted.CancelAsync();                                                             // il processo muore a metà attesa
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
            firstRemaining = Assert.Single(probe.Remaining);
        }

        Assert.Equal("Pending", await Text(database, root, "ClassificationStatus"));                     // nulla scritto
        Assert.Equal(0, await DocumentRootClassificationTests.Records(database, root));

        var secondTime = new DocumentRuntimeReadinessTests.VirtualTime();
        using var second = new DocumentRuntimeReadinessTests.Process(runtime, secondTime);
        var secondProbe = new ReadinessProbe(second, secondTime);
        await using var next = DocumentRootClassificationTests.Rig.Create(database, secondProbe);

        var result = await next.Classifier.ClassifyAsync(ClassificationOwnerKind.ChatTurn, root.Id, Ct);

        var secondRemaining = Assert.Single(secondProbe.Remaining);
        Assert.InRange(firstRemaining, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        Assert.True(secondRemaining <= firstRemaining, "il riavvio non ripristina il tempo: il residuo scende, mai sale");
        Assert.InRange(secondTime.Elapsed, secondRemaining - TimeSpan.FromMilliseconds(1), secondRemaining);   // attesa fino alla sola deadline originale (timer al ms), non oltre
        Assert.Equal(ClassificationAttempt.Failed, result.Attempt);
        Assert.Equal(("deadline_exceeded", "deadline_exceeded"), (await Text(database, root, "ErrorCode"), await RecordAsync(database, root, "Reason")));
        Assert.Equal(timers, await TimersAsync(database, root));
        Assert.Equal(0, runtime.Completions);
        Assert.Empty(next.Executor.StatusAtInvocation);
    }

    [Fact]
    public async Task Two_workers_on_the_same_pending_root_share_one_readiness_wait_and_produce_one_record_with_no_duplicate_probe_or_inference()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var runtime = new DocumentRuntimeReadinessTests.FakeRuntime { StatusAt = probe => probe < 3 ? "loading" : "loaded" };
        var time = new DocumentRuntimeReadinessTests.VirtualTime();
        using var process = new DocumentRuntimeReadinessTests.Process(runtime, time);
        var probe = new ReadinessProbe(process, time, driveTime: false);
        await using var a = DocumentRootClassificationTests.Rig.Create(database, probe);
        await using var b = DocumentRootClassificationTests.Rig.Create(database, probe);
        var root = await DocumentRootClassificationTests.SeedPendingAsync(database, RootKind.Turn);

        var both = Task.WhenAll(
            a.Classifier.ClassifyAsync(ClassificationOwnerKind.ChatTurn, root.Id, Ct),
            b.Classifier.ClassifyAsync(ClassificationOwnerKind.ChatTurn, root.Id, Ct));
        await time.RunAsync(both);
        var results = await both;

        Assert.Equal([ClassificationAttempt.Classified, ClassificationAttempt.NotPending], results.Select(r => r.Attempt).Order().ToArray());
        Assert.Equal(1, await DocumentRootClassificationTests.Records(database, root));
        Assert.Equal(3, runtime.Count("/v1/models"));                                                    // un solo giro di sonde, non uno per worker
        Assert.Equal(1, runtime.Count("/props"));
        Assert.Equal(0, runtime.Completions);
    }

    [Fact]
    public async Task An_unverifiable_tokenizer_never_makes_the_root_ordinary_and_sends_nothing()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var runtime = new DocumentRuntimeReadinessTests.FakeRuntime { TokenizerAvailable = false, TemplateAvailable = false };
        var time = new DocumentRuntimeReadinessTests.VirtualTime();
        using var process = new DocumentRuntimeReadinessTests.Process(runtime, time);
        await using var rig = DocumentRootClassificationTests.Rig.Create(database, new ReadinessProbe(process, time));
        var root = await DocumentRootClassificationTests.SeedPendingAsync(database, RootKind.Run);

        var result = await rig.Classifier.ClassifyNextAsync(turns: true, analysisRuns: true, documentRuns: true, Ct);

        Assert.Equal(ClassificationAttempt.Classified, result!.Attempt);
        Assert.Equal(("Classified", "Extended", "workload_unknown"),
            (await Text(database, root, "ClassificationStatus"), await Text(database, root, "InitialRegime"), await Text(database, root, "ClassificationReason")));
        Assert.Equal(0, runtime.Completions);
    }
}
