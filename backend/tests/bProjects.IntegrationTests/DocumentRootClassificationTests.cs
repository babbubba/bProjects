using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T124 (deterministico) — dagli input tecnici misurati (o esplicitamente non disponibili) agli input della policy `document-regime-v2` e ai campi
/// provvisori Pending dei root documentali. Nessun database, nessun modello.
/// </summary>
public sealed class DocumentRootClassificationInputTests
{
    private static readonly DocumentLimits Limits = new DocumentOptions().ToLimits();

    internal static RegimeMeasurement Fit => new()
    {
        RuntimeFingerprint = "fp-1", WindowTokens = 32_768, OutputTokens = 4_096, MarginTokens = 512, ReasoningDisabled = true,
        PayloadTokens = 1_000, TokenQuality = TokenCountQuality.Measured, PlannedCalls = 1, HasDependencies = false,
        ShapeFloorTokens = 20, ShapeSkeletonHash = new string('a', 64), Provenance = new("runtime", "config", "config", "tokenizer"),
    };

    private static DocumentRegimeDecision Decide(RegimeMeasurement measurement, double elapsedSeconds = 5)
    {
        var now = DateTimeOffset.UtcNow;
        var requested = now.AddSeconds(-elapsedSeconds);
        var input = DocumentRootClassifier.BuildInput(measurement, requested, requested.AddSeconds(Limits.ExtendedDeadlineSeconds), now, Limits);
        return DocumentRegimePolicy.Classify(input, Limits.ExtendedDeadlineSeconds);
    }

    [Fact]
    public void A_measured_single_call_that_fits_the_budget_is_ordinary_single_call_fit()
    {
        var decision = Decide(Fit);

        Assert.Equal((ClassificationOutcome.AdmitOrdinary, "single_call_fit", ExecutionRegime.Ordinary), (decision.Outcome, decision.Reason, decision.Regime));
        Assert.False(decision.FailDeadlineExceeded);
    }

    [Fact]
    public void The_deadline_is_derived_from_the_original_RequestedAt_never_from_the_classification_instant()
    {
        var now = DateTimeOffset.UtcNow;
        var requested = now.AddSeconds(-130);
        var input = DocumentRootClassifier.BuildInput(Fit, requested, requested.AddSeconds(3_600), now, Limits);

        var decision = DocumentRegimePolicy.Classify(input, 3_600);

        Assert.Equal(requested.AddSeconds(180), decision.EffectiveDeadlineAt);          // 50 s residui
        Assert.Equal(requested, input.RequestedAt);
    }

    [Fact]
    public void A_payload_count_that_is_unavailable_is_workload_unknown_extended_and_never_ordinary()
    {
        var decision = Decide(Fit with { PayloadTokens = null, TokenQuality = TokenCountQuality.Unavailable });

        Assert.Equal((ClassificationOutcome.AdmitExtended, "workload_unknown", ExecutionRegime.Extended), (decision.Outcome, decision.Reason, decision.Regime));
        Assert.Contains("token_count", decision.Unknowns);
    }

    [Fact]
    public void A_runtime_that_cannot_be_identified_fails_runtime_unverified_before_any_inference()
    {
        var decision = Decide(new RegimeMeasurement { PlannedCalls = 1, HasDependencies = false });

        Assert.Equal((ClassificationOutcome.Fail, "runtime_unverified"), (decision.Outcome, decision.ErrorCode));
    }

    [Fact]
    public void A_payload_over_the_budget_or_more_than_one_call_is_extended_multipass_or_capacity()
    {
        Assert.Equal("multipass_or_capacity", Decide(Fit with { PayloadTokens = 40_000 }).Reason);
        Assert.Equal("multipass_or_capacity", Decide(Fit with { PlannedCalls = 3 }).Reason);
    }

    [Fact]
    public void Reasoning_enabled_without_a_verified_cap_leaves_the_visible_reserve_unknown()
    {
        var decision = Decide(Fit with { ReasoningDisabled = false, ReasoningCapTokens = null });

        Assert.Equal("workload_unknown", decision.Reason);
        Assert.Contains("visible_reserve", decision.Unknowns);
    }

    [Fact]
    public void A_planned_call_count_above_the_root_limit_is_a_certain_limit_and_fails_coverage_incomplete()
    {
        var decision = Decide(Fit with { PlannedCalls = Limits.MaxAiCallsPerRun + 1 });

        Assert.Equal(("coverage_incomplete", ClassificationOutcome.Fail), (decision.ErrorCode, decision.Outcome));
    }

    [Fact]
    public void An_invalid_input_is_rejected_with_its_reason()
    {
        var decision = Decide(Fit with { InvalidReason = "processing_failed" });

        Assert.Equal((ClassificationOutcome.Reject, "processing_failed"), (decision.Outcome, decision.ErrorCode));
    }

    [Fact]
    public void Pending_fields_of_a_chat_turn_are_extended_provisional_with_the_cap_from_the_original_RequestedAt()
    {
        var now = DateTimeOffset.UtcNow;
        var turn = new ChatTurn { Id = Guid.NewGuid(), RequestedAt = now, DeadlineAt = now.AddSeconds(180) };

        DocumentRootRegime.Pending(turn, now, Limits);

        AssertPending(turn.ClassificationStatus, turn.Regime, turn.InitialRegime, turn.ClassificationReason, turn.ClassificationVersion, turn.PolicyHash, turn.PromotionCount, turn.CallCount);
        Assert.Equal(now.AddSeconds(3_600), turn.HardDeadlineAt);
        Assert.Equal(turn.HardDeadlineAt, turn.EffectiveDeadlineAt);
        Assert.Equal(turn.HardDeadlineAt, turn.DeadlineAt);                              // CHECK DeadlineAt = EffectiveDeadlineAt
        Assert.Null(turn.OrdinaryDeadlineAt);
    }

    [Fact]
    public void Pending_fields_of_a_processing_run_are_extended_provisional_with_the_cap_from_the_original_RequestedAt()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new ProcessingRun { Id = Guid.NewGuid(), WorkflowDefinitionVersion = "project-analysis-documents-v2", MafVersion = "1.26.0", RequestedAt = now, DeadlineAt = now.AddSeconds(180) };

        DocumentRootRegime.Pending(run, now, Limits);

        AssertPending(run.ClassificationStatus, run.Regime, run.InitialRegime, run.ClassificationReason, run.ClassificationVersion, run.PolicyHash, run.PromotionCount, run.CallCount);
        Assert.Equal(now.AddSeconds(3_600), run.HardDeadlineAt);
        Assert.Equal(run.HardDeadlineAt, run.EffectiveDeadlineAt);
        Assert.Equal(run.HardDeadlineAt, run.DeadlineAt);
        Assert.Null(run.OrdinaryDeadlineAt);
    }

    private static void AssertPending(
        ClassificationStatus? status, ExecutionRegime? regime, ExecutionRegime? initial, string? reason, string? version, string? policyHash, int? promotions, int? calls)
    {
        Assert.Equal(ClassificationStatus.Pending, status);
        Assert.Equal(ExecutionRegime.Extended, regime);
        Assert.Null(initial);                                                             // InitialRegime non ancora definitivo
        Assert.Equal(DocumentRegimePolicy.PendingReason, reason);
        Assert.Equal(DocumentRegimePolicy.Version, version);
        Assert.Equal(DocumentRegimePolicy.ComputePolicyHash(Limits.OrdinaryDeadlineSeconds, Limits.ExtendedDeadlineSeconds), policyHash);
        Assert.Equal(0, promotions);
        Assert.Equal(0, calls);
    }
}

/// <summary>
/// T124 (PG) — classificazione iniziale dei root documentali nel dispatcher esistente, prima del primo claim: Pending (nessun invio), Ordinary, Extended,
/// runtime non disponibile, deadline scaduta, restart/replay, concorrenza di due worker e compatibilità legacy. PostgreSQL reale; la misura tecnica è
/// una sonda finta (nessun DGX, nessun modello).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentRootClassificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static string PolicyHash => DocumentRegimePolicy.ComputePolicyHash(180, 3_600);

    private static RegimeMeasurement Fit => DocumentRootClassificationInputTests.Fit;

    internal sealed class FakeProbe(Func<ClassificationOwnerKind, Guid, RegimeMeasurement> measure) : IDocumentRegimeProbe
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<RegimeMeasurement> MeasureAsync(ClassificationOwnerKind kind, Guid rootId, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(measure(kind, rootId));
        }
    }

    private sealed class BarrierProbe(int parties, RegimeMeasurement measurement) : IDocumentRegimeProbe
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task<RegimeMeasurement> MeasureAsync(ClassificationOwnerKind kind, Guid rootId, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrived) == parties)
            {
                _all.SetResult();
            }

            await _all.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return measurement;
        }
    }

    /// <summary>Esecutore dei turni finto: registra lo stato di classificazione del turno nell'istante in cui il dispatcher lo invoca.</summary>
    internal sealed class RecordingExecutor(ParsingDatabase database) : IChatTurnExecutor
    {
        public List<string> StatusAtInvocation { get; } = [];

        public async Task ExecuteAsync(ChatTurnClaim claim, CancellationToken ct)
        {
            var status = await database.ScalarAsync<string>($"SELECT COALESCE(\"ClassificationStatus\", '<null>') FROM \"ChatTurns\" WHERE \"Id\" = '{claim.TurnId}'");
            lock (StatusAtInvocation)
            {
                StatusAtInvocation.Add(status);
            }
        }
    }

    internal sealed class Rig : IAsyncDisposable
    {
        public required ParsingDatabase Database { get; init; }
        public required ServiceProvider Services { get; init; }
        public required RecordingExecutor Executor { get; init; }
        public required DocumentRootClassifier Classifier { get; init; }
        public required AiDispatchService Dispatcher { get; init; }

        public static Rig Create(ParsingDatabase database, IDocumentRegimeProbe probe, bool withClassifier = true)
        {
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString));
            services.AddSingleton(Options.Create(new ExecutionOptions()));
            services.AddSingleton(Options.Create(new DocumentOptions()));
            services.AddScoped<ExecutionLeaseStore>();
            services.AddScoped<DocumentPolicyService>();
            var provider = services.BuildServiceProvider();
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var executor = new RecordingExecutor(database);
            var classifier = new DocumentRootClassifier(scopes, probe);
            var dispatcher = new AiDispatchService(
                scopes, Options.Create(new ExecutionOptions()), NullLogger<AiDispatchService>.Instance, executor: executor, classifier: withClassifier ? classifier : null);
            return new Rig { Database = database, Services = provider, Executor = executor, Classifier = classifier, Dispatcher = dispatcher };
        }

        public async Task<WorkClaim?> ClaimAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextWorkAsync("w", turns: true, runs: true, documents: true, Ct);
        }

        public async ValueTask DisposeAsync() => await Services.DisposeAsync();
    }

    /// <summary>Root documentale appena richiesto dal POST: Queued, t0 spostato indietro di <paramref name="elapsed"/> s, Pending Extended provvisorio con cap dal t0.</summary>
    internal static async Task<SeededRoot> SeedPendingAsync(ParsingDatabase database, RootKind kind, double elapsed = 5)
    {
        var root = await DocumentRegimeRoots.SeedAsync(
            database, kind, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, Legacy = true });
        var seconds = elapsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await database.ExecuteAsync($"""
            UPDATE "{root.Table}" x SET "RequestedAt" = v.r, "ClassificationStatus" = 'Pending', "Regime" = 'Extended', "ClassificationVersion" = 'document-regime-v2',
                "ClassificationReason" = 'preflight_pending', "PolicyHash" = '{PolicyHash}', "HardDeadlineAt" = v.h, "EffectiveDeadlineAt" = v.h, "DeadlineAt" = v.h,
                "PromotionCount" = 0, "CallCount" = 0
            FROM (SELECT q.r, q.r + interval '3600 seconds' AS h FROM (SELECT clock_timestamp() - interval '{seconds} seconds' AS r) q) v
            WHERE x."Id" = '{root.Id}'
            """);
        return root;
    }

    internal static ClassificationOwnerKind OwnerOf(SeededRoot root) => root.Kind == RootKind.Turn ? ClassificationOwnerKind.ChatTurn : ClassificationOwnerKind.ProcessingRun;

    internal static Task<string> Text(ParsingDatabase db, SeededRoot root, string column) =>
        db.ScalarAsync<string>($"SELECT COALESCE(\"{column}\"::text, '<null>') FROM \"{root.Table}\" WHERE \"Id\" = '{root.Id}'");

    internal static Task<long> Records(ParsingDatabase db, SeededRoot root) =>
        db.ScalarAsync<long>($"SELECT count(*) FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'");

    internal static Task<bool> Holds(ParsingDatabase db, SeededRoot root, string predicate) =>
        db.ScalarAsync<bool>($"SELECT {predicate} FROM \"{root.Table}\" WHERE \"Id\" = '{root.Id}'");

    // ================================================================ Pending: nessun invio

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task A_pending_root_is_never_claimed_and_nothing_is_sent(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var probe = new FakeProbe((_, _) => Fit);
        await using var rig = Rig.Create(database, probe, withClassifier: false);
        var root = await SeedPendingAsync(database, kind);

        Assert.Null(await rig.ClaimAsync());                                                   // nessun claim finche il root e Pending
        if (kind == RootKind.Turn)
        {
            Assert.False(await rig.Dispatcher.RunOnceAsync(Ct));                               // senza classificatore il dispatcher non invia nulla
            Assert.Empty(rig.Executor.StatusAtInvocation);
        }

        Assert.Equal("Queued", await Text(database, root, "Status"));
        Assert.Equal("Pending", await Text(database, root, "ClassificationStatus"));
        Assert.Equal("<null>", await Text(database, root, "InitialRegime"));
        Assert.Equal(0, await Records(database, root));
    }

    [Fact]
    public async Task The_dispatcher_classifies_a_pending_turn_before_the_first_claim_and_only_then_invokes_the_executor()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var rig = Rig.Create(database, new FakeProbe((_, _) => Fit));
        var root = await SeedPendingAsync(database, RootKind.Turn);

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));                                    // passo di classificazione: nessun invio
        Assert.Empty(rig.Executor.StatusAtInvocation);
        Assert.Equal("Queued", await Text(database, root, "Status"));
        Assert.Equal("Classified", await Text(database, root, "ClassificationStatus"));

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));                                    // claim ed esecuzione
        Assert.Equal(["Classified"], rig.Executor.StatusAtInvocation);
        Assert.Equal(1, await Records(database, root));
    }

    // ================================================================ Ordinary

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task A_single_call_that_fits_is_classified_ordinary_with_the_record_and_the_deadline_from_the_original_RequestedAt(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var rig = Rig.Create(database, new FakeProbe((_, _) => Fit));
        var root = await SeedPendingAsync(database, kind, elapsed: 130);                       // classificato a 130 s: restano 50 s, non 180

        var result = await rig.Classifier.ClassifyNextAsync(turns: true, analysisRuns: true, documentRuns: true, Ct);

        Assert.Equal((root.Id, ClassificationAttempt.Classified), (result!.RootId, result.Attempt));
        Assert.Equal(("Classified", "Ordinary", "Ordinary", "single_call_fit", "document-regime-v2"),
            (await Text(database, root, "ClassificationStatus"), await Text(database, root, "InitialRegime"), await Text(database, root, "Regime"),
             await Text(database, root, "ClassificationReason"), await Text(database, root, "ClassificationVersion")));
        Assert.Equal(PolicyHash, await Text(database, root, "PolicyHash"));
        Assert.True(await Holds(database, root,
            "\"OrdinaryDeadlineAt\" = \"RequestedAt\" + interval '180 seconds' AND \"EffectiveDeadlineAt\" = \"OrdinaryDeadlineAt\" AND \"DeadlineAt\" = \"EffectiveDeadlineAt\" AND \"HardDeadlineAt\" = \"RequestedAt\" + interval '3600 seconds'"));
        Assert.Equal("Queued", await Text(database, root, "Status"));                          // vivo: nessuna terminazione
        Assert.Equal("0", await Text(database, root, "CallCount"));
        Assert.Equal("0", await Text(database, root, "PromotionCount"));

        Assert.Equal(1, await Records(database, root));
        Assert.Equal(("AdmitOrdinary", "single_call_fit", "measured", 1),
            (await database.ScalarAsync<string>($"SELECT \"Outcome\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"),
             await database.ScalarAsync<string>($"SELECT \"Reason\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"),
             await database.ScalarAsync<string>($"SELECT \"TokenQuality\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"),
             await database.ScalarAsync<int>($"SELECT \"PlannedCalls\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'")));
        Assert.Equal(("fp-1", 32_768, 4_096, 512, 1_000, 20),
            (await database.ScalarAsync<string>($"SELECT \"RuntimeFingerprint\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"),
             await database.ScalarAsync<int>($"SELECT \"WindowTokens\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"),
             await database.ScalarAsync<int>($"SELECT \"OutputTokens\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"),
             await database.ScalarAsync<int>($"SELECT \"MarginTokens\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"),
             await database.ScalarAsync<int>($"SELECT \"PayloadTokens\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"),
             await database.ScalarAsync<int>($"SELECT \"ShapeFloorTokens\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'")));
        Assert.Equal(PolicyHash, await database.ScalarAsync<string>($"SELECT \"PolicyHash\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"));
        Assert.Equal(OwnerOf(root).ToString(), await database.ScalarAsync<string>($"SELECT \"OwnerKind\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"));

        // Dopo la classificazione il root e claimabile.
        var claim = await rig.ClaimAsync();
        Assert.NotNull(claim);
    }

    // ================================================================ Extended

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task A_multipass_or_oversized_plan_is_classified_extended_with_the_cap_from_the_original_RequestedAt(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var rig = Rig.Create(database, new FakeProbe((_, _) => Fit with { PayloadTokens = 40_000 }));   // T oltre B
        var root = await SeedPendingAsync(database, kind, elapsed: 190);                       // oltre 180 s, ma Extended strutturale: ammesso fino al cap

        var result = await rig.Classifier.ClassifyNextAsync(turns: true, analysisRuns: true, documentRuns: true, Ct);

        Assert.Equal(ClassificationAttempt.Classified, result!.Attempt);
        Assert.Equal(("Classified", "Extended", "Extended", "multipass_or_capacity", "Queued"),
            (await Text(database, root, "ClassificationStatus"), await Text(database, root, "InitialRegime"), await Text(database, root, "Regime"),
             await Text(database, root, "ClassificationReason"), await Text(database, root, "Status")));
        Assert.True(await Holds(database, root,
            "\"OrdinaryDeadlineAt\" IS NULL AND \"EffectiveDeadlineAt\" = \"HardDeadlineAt\" AND \"DeadlineAt\" = \"HardDeadlineAt\" AND \"HardDeadlineAt\" = \"RequestedAt\" + interval '3600 seconds'"));
        Assert.Equal("AdmitExtended", await database.ScalarAsync<string>($"SELECT \"Outcome\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"));
    }

    [Fact]
    public async Task An_unavailable_payload_count_is_extended_workload_unknown_and_the_unknown_fields_are_persisted()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var rig = Rig.Create(database, new FakeProbe((_, _) => Fit with { PayloadTokens = null, TokenQuality = TokenCountQuality.Unavailable }));
        var root = await SeedPendingAsync(database, RootKind.Run);

        await rig.Classifier.ClassifyNextAsync(turns: true, analysisRuns: true, documentRuns: true, Ct);

        Assert.Equal(("Extended", "workload_unknown"), (await Text(database, root, "InitialRegime"), await Text(database, root, "ClassificationReason")));
        Assert.Equal("""["token_count"]""", await database.ScalarAsync<string>($"SELECT \"UnknownReasonsJson\"::text FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"));
        Assert.Equal("unavailable", await database.ScalarAsync<string>($"SELECT \"TokenQuality\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"));
    }

    // ================================================================ runtime non disponibile

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task An_unavailable_runtime_fails_the_root_runtime_unverified_before_any_inference(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var probe = new FakeProbe((_, _) => new RegimeMeasurement());                          // nessuna impronta, nessuna finestra
        await using var rig = Rig.Create(database, probe);
        var root = await SeedPendingAsync(database, kind);

        var result = await rig.Classifier.ClassifyNextAsync(turns: true, analysisRuns: true, documentRuns: true, Ct);

        Assert.Equal(ClassificationAttempt.Failed, result!.Attempt);
        Assert.Equal(("Failed", "runtime_unverified", "Rejected", "<null>"),
            (await Text(database, root, "Status"), await Text(database, root, "ErrorCode"), await Text(database, root, "ClassificationStatus"), await Text(database, root, "InitialRegime")));
        Assert.Equal(("Fail", "runtime_unverified"),
            (await database.ScalarAsync<string>($"SELECT \"Outcome\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"),
             await database.ScalarAsync<string>($"SELECT \"Reason\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'")));
        Assert.True(await Holds(database, root, "\"CompletedAt\" IS NOT NULL AND \"LeaseOwner\" IS NULL"));
        Assert.Null(await rig.ClaimAsync());                                                   // niente da inviare
        Assert.Empty(rig.Executor.StatusAtInvocation);
    }

    [Fact]
    public async Task An_invalid_input_rejects_the_root_with_the_specific_error_and_sends_nothing()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var rig = Rig.Create(database, new FakeProbe((_, _) => Fit with { InvalidReason = "processing_failed" }));
        var root = await SeedPendingAsync(database, RootKind.Turn);

        await rig.Dispatcher.RunOnceAsync(Ct);

        Assert.Equal(("Failed", "processing_failed", "Rejected"),
            (await Text(database, root, "Status"), await Text(database, root, "ErrorCode"), await Text(database, root, "ClassificationStatus")));
        Assert.Equal("Reject", await database.ScalarAsync<string>($"SELECT \"Outcome\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"));
        Assert.Empty(rig.Executor.StatusAtInvocation);
    }

    [Fact]
    public async Task A_policy_frozen_at_the_request_that_no_longer_matches_is_not_reinterpreted_and_fails_config_invalid()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var rig = Rig.Create(database, new FakeProbe((_, _) => Fit));
        var root = await SeedPendingAsync(database, RootKind.Turn);
        await database.ExecuteAsync($"UPDATE \"ChatTurns\" SET \"PolicyHash\" = '{new string('9', 64)}' WHERE \"Id\" = '{root.Id}'");

        await rig.Classifier.ClassifyAsync(OwnerOf(root), root.Id, Ct);

        Assert.Equal(("Failed", "config_invalid"), (await Text(database, root, "Status"), await Text(database, root, "ErrorCode")));
        Assert.Equal("<null>", await Text(database, root, "InitialRegime"));
    }

    // ================================================================ deadline

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task An_ordinary_classification_after_the_ordinary_deadline_fails_deadline_exceeded_without_sending_and_never_becomes_extended(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var rig = Rig.Create(database, new FakeProbe((_, _) => Fit));
        var root = await SeedPendingAsync(database, kind, elapsed: 190);                       // preflight concluso a 190 s con piano valido

        var result = await rig.Classifier.ClassifyNextAsync(turns: true, analysisRuns: true, documentRuns: true, Ct);

        Assert.Equal(ClassificationAttempt.Failed, result!.Attempt);
        Assert.Equal(("Failed", "deadline_exceeded", "Classified", "Ordinary", "Ordinary"),
            (await Text(database, root, "Status"), await Text(database, root, "ErrorCode"), await Text(database, root, "ClassificationStatus"),
             await Text(database, root, "InitialRegime"), await Text(database, root, "Regime")));
        Assert.True(await Holds(database, root, "\"EffectiveDeadlineAt\" = \"RequestedAt\" + interval '180 seconds' AND \"DeadlineAt\" = \"EffectiveDeadlineAt\" AND \"CompletedAt\" IS NOT NULL"));
        Assert.Equal(1, await Records(database, root));
        Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM \"RegimeTransitions\" WHERE \"RootId\" = '{root.Id}'"));
        Assert.Null(await rig.ClaimAsync());
        Assert.Empty(rig.Executor.StatusAtInvocation);
    }

    [Fact]
    public async Task A_root_past_its_hard_cap_at_classification_fails_deadline_exceeded_without_admission()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var rig = Rig.Create(database, new FakeProbe((_, _) => Fit with { PayloadTokens = 40_000 }));
        var root = await SeedPendingAsync(database, RootKind.Turn, elapsed: 3_700);

        var result = await rig.Classifier.ClassifyAsync(OwnerOf(root), root.Id, Ct);

        Assert.Equal(ClassificationAttempt.Failed, result.Attempt);
        Assert.Equal(("Failed", "deadline_exceeded", "<null>"), (await Text(database, root, "Status"), await Text(database, root, "ErrorCode"), await Text(database, root, "InitialRegime")));
        Assert.Equal(("Fail", "deadline_exceeded"),
            (await database.ScalarAsync<string>($"SELECT \"Outcome\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"),
             await database.ScalarAsync<string>($"SELECT \"Reason\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'")));
    }

    // ================================================================ restart / replay: idempotenza

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task A_classified_root_is_never_reclassified_after_restart_or_replay_and_nothing_resets_or_promotes_it(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await SeedPendingAsync(database, kind, elapsed: 20);
        await using (var first = Rig.Create(database, new FakeProbe((_, _) => Fit)))
        {
            await first.Classifier.ClassifyNextAsync(turns: true, analysisRuns: true, documentRuns: true, Ct);
        }

        // Stato al momento della prima classificazione, poi tempo trascorso e budget consumato.
        await database.ExecuteAsync($"UPDATE \"{root.Table}\" SET \"CallCount\" = 2, \"RequestedAt\" = \"RequestedAt\" - interval '100 seconds', \"OrdinaryDeadlineAt\" = \"OrdinaryDeadlineAt\" - interval '100 seconds', \"EffectiveDeadlineAt\" = \"EffectiveDeadlineAt\" - interval '100 seconds', \"DeadlineAt\" = \"DeadlineAt\" - interval '100 seconds', \"HardDeadlineAt\" = \"HardDeadlineAt\" - interval '100 seconds' WHERE \"Id\" = '{root.Id}'");
        var before = (await Text(database, root, "EffectiveDeadlineAt"), await Text(database, root, "RequestedAt"));
        var recordHash = await database.ScalarAsync<string>($"SELECT \"InputHash\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'");

        var probe = new FakeProbe((_, _) => Fit with { PayloadTokens = 40_000 });             // un'altra misura non puo cambiare l'ammissione
        await using var restarted = Rig.Create(database, probe);
        Assert.Null(await restarted.Classifier.ClassifyNextAsync(turns: true, analysisRuns: true, documentRuns: true, Ct));
        var replay = await restarted.Classifier.ClassifyAsync(OwnerOf(root), root.Id, Ct);

        Assert.Equal(ClassificationAttempt.NotPending, replay.Attempt);
        Assert.Equal(0, probe.Calls);                                                          // nessuna nuova misura
        Assert.Equal(1, await Records(database, root));
        Assert.Equal(recordHash, await database.ScalarAsync<string>($"SELECT \"InputHash\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}'"));
        Assert.Equal(("Classified", "Ordinary", "Ordinary", "2", "0"),
            (await Text(database, root, "ClassificationStatus"), await Text(database, root, "InitialRegime"), await Text(database, root, "Regime"),
             await Text(database, root, "CallCount"), await Text(database, root, "PromotionCount")));
        Assert.Equal(before, (await Text(database, root, "EffectiveDeadlineAt"), await Text(database, root, "RequestedAt")));
        Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM \"RegimeTransitions\" WHERE \"RootId\" = '{root.Id}'"));   // nessuna promozione per solo tempo
    }

    [Fact]
    public async Task A_restart_before_the_classification_is_completed_classifies_once_from_the_original_RequestedAt_and_then_claims()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await SeedPendingAsync(database, RootKind.Turn, elapsed: 60);
        await using (var crashed = Rig.Create(database, new FakeProbe((_, _) => throw new InvalidOperationException("crash del processo durante la misura"))))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => crashed.Classifier.ClassifyAsync(OwnerOf(root), root.Id, Ct));
            Assert.Equal("Pending", await Text(database, root, "ClassificationStatus"));      // nulla scritto, ancora Pending
            Assert.Equal(0, await Records(database, root));
        }

        await using var next = Rig.Create(database, new FakeProbe((_, _) => Fit));
        Assert.True(await next.Dispatcher.RunOnceAsync(Ct));
        Assert.True(await next.Dispatcher.RunOnceAsync(Ct));

        Assert.Equal(["Classified"], next.Executor.StatusAtInvocation);
        Assert.Equal(1, await Records(database, root));
        Assert.True(await Holds(database, root, "\"EffectiveDeadlineAt\" = \"RequestedAt\" + interval '180 seconds'"));
    }

    // ================================================================ concorrenza di due worker

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task Two_workers_classifying_the_same_pending_root_produce_exactly_one_record_and_one_winner(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await SeedPendingAsync(database, kind);
        var barrier = new BarrierProbe(2, Fit);                                                // entrambi misurano prima che uno qualunque committi
        await using var a = Rig.Create(database, barrier);
        await using var b = Rig.Create(database, barrier);

        var results = await Task.WhenAll(
            a.Classifier.ClassifyAsync(OwnerOf(root), root.Id, Ct),
            b.Classifier.ClassifyAsync(OwnerOf(root), root.Id, Ct));

        Assert.Equal([ClassificationAttempt.Classified, ClassificationAttempt.NotPending], results.Select(r => r.Attempt).Order().ToArray());
        Assert.Equal(1, await Records(database, root));
        Assert.Equal(("Classified", "Ordinary"), (await Text(database, root, "ClassificationStatus"), await Text(database, root, "InitialRegime")));
        Assert.Equal(1, await database.ScalarAsync<long>($"SELECT count(*) FROM \"ClassificationRecords\" WHERE \"OwnerId\" = '{root.Id}' AND \"Ordinal\" = 1"));
    }

    // ================================================================ compatibilita legacy

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task Legacy_roots_without_regime_fields_are_claimed_unchanged_and_never_classified(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var probe = new FakeProbe((_, _) => Fit);
        await using var rig = Rig.Create(database, probe);
        var root = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, Legacy = true });

        Assert.Null(await rig.Classifier.ClassifyNextAsync(turns: true, analysisRuns: true, documentRuns: true, Ct));
        Assert.Equal(ClassificationAttempt.NotPending, (await rig.Classifier.ClassifyAsync(OwnerOf(root), root.Id, Ct)).Attempt);
        Assert.Equal(0, probe.Calls);
        Assert.Equal(0, await Records(database, root));
        Assert.Equal(("<null>", "<null>", "<null>", "<null>"),
            (await Text(database, root, "ClassificationStatus"), await Text(database, root, "InitialRegime"), await Text(database, root, "Regime"), await Text(database, root, "EffectiveDeadlineAt")));

        var claim = await rig.ClaimAsync();                                                    // la 001 (e i root pre-T124) restano claimabili come prima
        Assert.NotNull(claim);
        Assert.Equal("Running", await Text(database, root, "Status"));
    }

    [Fact]
    public async Task The_dispatcher_runs_a_legacy_turn_without_any_classification_step()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var probe = new FakeProbe((_, _) => Fit);
        await using var rig = Rig.Create(database, probe);
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0, Legacy = true });

        Assert.True(await rig.Dispatcher.RunOnceAsync(Ct));                                    // un solo giro: claim + esecuzione

        Assert.Equal(["<null>"], rig.Executor.StatusAtInvocation);
        Assert.Equal(0, probe.Calls);
        Assert.Equal(0, await Records(database, root));
    }
}

/// <summary>
/// T124 (API + PG) — i POST documentali di chat, Analizza e Genera persistono RequestedAt, snapshot congelato e Pending (Extended provvisorio,
/// preflight_pending) restando asincroni e senza alcuna inferenza; senza documenti i root restano quelli della 001. Applicazione reale.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentRootClassificationApiTests : ArchiveTestBase
{
    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;
        options.ProjectQuotaBytes = 50_000;
    }

    private sealed record Doc(Guid Document, Guid Version);

    private async Task<Doc> NewDocument(HttpClient client, Guid project, string name, bool ready)
    {
        var created = await Json(await Upload(client, project, Text(name), name + ".txt", displayName: name));
        var doc = new Doc(created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid());
        if (ready)
        {
            await MakeReady(doc);
        }

        return doc;
    }

    private async Task MakeReady(Doc doc)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == doc.Version, Ct);
        var now = DateTimeOffset.UtcNow;
        var (job, evidence) = (Guid.NewGuid(), Guid.NewGuid());
        db.EvidenceExtractionJobs.Add(new EvidenceExtractionJob
        {
            Id = job, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Reason = ExtractionJobReason.Initial,
            Status = ExtractionJobStatus.Queued, RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
        });
        db.EvidenceExtractionVersions.Add(new EvidenceExtractionVersion
        {
            Id = evidence, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, ManifestHash = new string('1', 64),
            Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = 0,
            NoEvidenceReason = "Documento di prova senza requisiti.", CreatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = doc.Version, ProjectId = preparation.ProjectId, CurrentPreparationId = preparation.Id, CurrentEvidenceVersionId = evidence, Revision = 1, UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        preparation.Status = PreparationStatus.Ready;
        preparation.CurrentEvidenceVersionId = evidence;
        await db.SaveChangesAsync(Ct);
        await db.ProjectDocuments.Where(d => d.Id == doc.Document).ExecuteUpdateAsync(x => x.SetProperty(d => d.LatestReadyVersionId, doc.Version), Ct);
    }

    private static async Task<Guid> PostChat(HttpClient client, Guid project, string text = "Quanti ordini al giorno?")
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("chatTurnId").GetGuid();
    }

    private static async Task<Guid> PostAnalysis(HttpClient client, Guid project, Guid? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<(Guid Analysis, Guid Revision)> ConfirmedAnalysis(Guid project, Guid runId)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ProcessingRuns.SingleAsync(r => r.Id == runId, Ct);
        var now = DateTimeOffset.UtcNow;
        var revision = new AnalysisRevision
        {
            Id = Guid.NewGuid(), AnalysisId = run.Id, Version = 1, Origin = AnalysisRevisionOrigin.Ai, CreatedBy = run.RequestedBy, CreatedAt = now,
            SchemaVersion = AnalysisRevision.CurrentSchemaVersion, ContentJson = "{\"seed\":true}", ValidationState = AnalysisValidationState.PmConfirmed,
            ConfirmedBy = run.RequestedBy, ConfirmedAt = now,
        };
        db.AnalysisRevisions.Add(revision);
        run.Status = ProcessingRunStatus.Completed;
        run.ResultRevisionId = revision.Id;
        run.CompletedAt = now;
        await db.SaveChangesAsync(Ct);
        await db.Projects.Where(p => p.Id == project).ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentAnalysisId, run.Id), Ct);
        return (run.Id, revision.Id);
    }

    private static async Task<Guid> PostGeneration(HttpClient client, Guid project, Guid analysis, Guid revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/document-generations") { Content = JsonContent.Create(new { revisionId = revision }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private Task<string> Text(string table, Guid id, string column) =>
        Scalar<string>($"SELECT COALESCE(\"{column}\"::text, '<null>') FROM \"{table}\" WHERE \"Id\" = @i", ("i", id));

    private async Task AssertPending(string table, Guid id)
    {
        Assert.Equal(("Queued", "Pending", "Extended", "<null>", "preflight_pending", "document-regime-v2"),
            (await Text(table, id, "Status"), await Text(table, id, "ClassificationStatus"), await Text(table, id, "Regime"), await Text(table, id, "InitialRegime"),
             await Text(table, id, "ClassificationReason"), await Text(table, id, "ClassificationVersion")));
        Assert.Equal(DocumentRegimePolicy.ComputePolicyHash(180, 3_600), await Text(table, id, "PolicyHash"));
        Assert.Equal(("0", "0"), (await Text(table, id, "PromotionCount"), await Text(table, id, "CallCount")));
        Assert.True(await Scalar<bool>(
            $"SELECT \"HardDeadlineAt\" = \"RequestedAt\" + interval '3600 seconds' AND \"EffectiveDeadlineAt\" = \"HardDeadlineAt\" AND \"DeadlineAt\" = \"EffectiveDeadlineAt\" AND \"OrdinaryDeadlineAt\" IS NULL AND \"RequestedAt\" IS NOT NULL FROM \"{table}\" WHERE \"Id\" = @i",
            ("i", id)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ClassificationRecords\" WHERE \"OwnerId\" = @i", ("i", id)));   // nessuna classificazione nel POST
        Assert.Equal(0, await Count("SELECT count(*) FROM \"InferenceCalls\" WHERE \"ChatTurnId\" = @i OR \"ProcessingRunId\" = @i", ("i", id)));   // nessuna inferenza
    }

    private async Task AssertLegacy(string table, Guid id)
    {
        Assert.Equal(("<null>", "<null>", "<null>", "<null>", "<null>", "<null>", "<null>"),
            (await Text(table, id, "ClassificationStatus"), await Text(table, id, "Regime"), await Text(table, id, "InitialRegime"), await Text(table, id, "PolicyHash"),
             await Text(table, id, "HardDeadlineAt"), await Text(table, id, "EffectiveDeadlineAt"), await Text(table, id, "CallCount")));
        Assert.True(await Scalar<bool>($"SELECT \"DeadlineAt\" = \"RequestedAt\" + interval '180 seconds' FROM \"{table}\" WHERE \"Id\" = @i", ("i", id)));
    }

    private async Task<WorkClaim?> ClaimAsync()
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextWorkAsync("w", turns: true, runs: true, documents: true, Ct);
    }

    // ================================================================ POST documentali: Pending

    [Fact]
    public async Task A_chat_post_with_a_ready_document_persists_the_snapshot_and_Pending_and_sends_nothing()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", ready: true);

        var turn = await PostChat(client, project);

        await AssertPending("ChatTurns", turn);
        Assert.NotEqual("<null>", await Text("ChatTurns", turn, "SourceSnapshotId"));
        Assert.Null(await ClaimAsync());                                                       // Pending: nessun claim, quindi nessuna inferenza
    }

    [Fact]
    public async Task An_analyze_post_with_a_ready_document_persists_the_snapshot_and_Pending_and_sends_nothing()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", ready: true);

        var run = await PostAnalysis(client, project);

        await AssertPending("ProcessingRuns", run);
        Assert.NotEqual("<null>", await Text("ProcessingRuns", run, "SourceSnapshotId"));
        Assert.Null(await ClaimAsync());
    }

    [Fact]
    public async Task A_generation_post_from_a_documentary_analysis_inherits_the_snapshot_and_persists_Pending()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", ready: true);
        var analysisRun = await PostAnalysis(client, project);
        var (analysis, revision) = await ConfirmedAnalysis(project, analysisRun);

        var generation = await PostGeneration(client, project, analysis, revision);

        await AssertPending("ProcessingRuns", generation);
        Assert.Equal(await Text("ProcessingRuns", analysisRun, "SourceSnapshotId"), await Text("ProcessingRuns", generation, "SourceSnapshotId"));
        Assert.Null(await ClaimAsync());
    }

    [Fact]
    public async Task An_analyze_replay_returns_the_original_root_without_touching_its_classification_fields()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", ready: true);
        var key = Guid.NewGuid();
        var run = await PostAnalysis(client, project, key);
        var before = await Scalar<string>("SELECT md5(to_jsonb(r)::text) FROM \"ProcessingRuns\" r WHERE \"Id\" = @i", ("i", run));

        var replay = await PostAnalysis(client, project, key);

        Assert.Equal(run, replay);
        Assert.Equal(before, await Scalar<string>("SELECT md5(to_jsonb(r)::text) FROM \"ProcessingRuns\" r WHERE \"Id\" = @i", ("i", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProcessingRuns\""));
    }

    // ================================================================ senza documenti: 001 invariata

    [Fact]
    public async Task Chat_analyze_and_generate_without_ready_documents_keep_the_legacy_001_roots()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var turn = await PostChat(client, project, "Vorrei un portale clienti.");
        var run = await PostAnalysis(client, project);
        var (analysis, revision) = await ConfirmedAnalysis(project, run);
        var generation = await PostGeneration(client, project, analysis, revision);

        await AssertLegacy("ChatTurns", turn);
        await AssertLegacy("ProcessingRuns", run);
        await AssertLegacy("ProcessingRuns", generation);
    }

    [Fact]
    public async Task A_chat_post_whose_documents_are_all_unavailable_stays_a_legacy_root()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "attesa", ready: false);

        var turn = await PostChat(client, project);

        await AssertLegacy("ChatTurns", turn);
        Assert.NotNull(await ClaimAsync());                                                    // claimabile subito come nella 001
    }
}
