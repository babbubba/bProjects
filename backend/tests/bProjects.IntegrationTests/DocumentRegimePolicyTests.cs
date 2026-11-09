using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// T045 (DET) — tabella B1 di document-regime-v2 (Plan §Classificazione, QS §B1). Solo la policy pura: nessun database, nessun DGX, nessun orologio
/// proprio. Con W=10000, O=2000, M=1000 si ha B=7000 e, a ragionamento disabilitato, V=O=2000.
/// </summary>
public sealed class DocumentRegimePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    private static DocumentRegimeInput Base(double nowSeconds = 0) => new()
    {
        RequestedAt = T0,
        Now = T0.AddSeconds(nowSeconds),
        HardDeadlineAt = T0.AddSeconds(3_600),
        OrdinaryDeadlineSeconds = 180,
        RuntimeFingerprint = "fp-1",
        WindowTokens = 10_000,
        OutputTokens = 2_000,
        MarginTokens = 1_000,
        PayloadTokens = 3_000,
        TokenQuality = TokenCountQuality.Measured,
        PlannedCalls = 1,
        HasDependencies = false,
        ReasoningDisabled = true,
        ShapeFloorTokens = 100,
        ShapeSkeletonHash = new string('a', 64),
        Provenance = new ClassificationProvenance("runtime", "config", "config", "tokenizer"),
    };

    private static DocumentRegimeDecision Run(DocumentRegimeInput input) => DocumentRegimePolicy.Classify(input);

    // ------------------------------------------------------------------ soglie T/B e floor/V

    [Theory]
    [InlineData(6_999, ClassificationOutcome.AdmitOrdinary, "single_call_fit")]
    [InlineData(7_000, ClassificationOutcome.AdmitOrdinary, "single_call_fit")]
    [InlineData(7_001, ClassificationOutcome.AdmitExtended, "multipass_or_capacity")]
    public void Token_threshold_T_against_B_is_inclusive_at_equality(int tokens, ClassificationOutcome outcome, string reason)
    {
        var decision = Run(Base() with { PayloadTokens = tokens });
        Assert.Equal((outcome, reason), (decision.Outcome, decision.Reason));
        Assert.Equal(7_000, decision.InputBudgetTokens);
    }

    [Theory]
    [InlineData(1_999, ClassificationOutcome.AdmitOrdinary)]
    [InlineData(2_000, ClassificationOutcome.AdmitOrdinary)]
    [InlineData(2_001, ClassificationOutcome.AdmitExtended)]
    public void Shape_floor_against_visible_reserve_is_inclusive_at_equality(int floor, ClassificationOutcome outcome)
    {
        var decision = Run(Base() with { ShapeFloorTokens = floor });
        Assert.Equal(outcome, decision.Outcome);
        Assert.Equal(2_000, decision.VisibleReserveTokens);
        if (outcome == ClassificationOutcome.AdmitExtended)
        {
            Assert.Equal(["floor_exceeds_visible_reserve"], decision.Causes);
        }
    }

    [Theory]
    [InlineData(500, 1_500, ClassificationOutcome.AdmitOrdinary)]
    [InlineData(500, 1_501, ClassificationOutcome.AdmitExtended)]
    public void Visible_reserve_with_a_verified_reasoning_cap_is_O_minus_R(int cap, int floor, ClassificationOutcome outcome)
    {
        var decision = Run(Base() with { ReasoningDisabled = false, ReasoningCapTokens = cap, ShapeFloorTokens = floor });
        Assert.Equal(1_500, decision.VisibleReserveTokens);
        Assert.Equal(outcome, decision.Outcome);
    }

    [Fact]
    public void Multiple_calls_or_dependencies_make_the_plan_extended_with_verifiable_causes()
    {
        var multipass = Run(Base() with { PlannedCalls = 4, HasDependencies = true, PayloadTokens = 9_000 });
        Assert.Equal((ClassificationOutcome.AdmitExtended, "multipass_or_capacity", ExecutionRegime.Extended), (multipass.Outcome, multipass.Reason, multipass.Regime));
        Assert.Equal(["multiple_calls", "dependencies", "tokens_exceed_budget"], multipass.Causes);
        Assert.Equal(T0.AddSeconds(3_600), multipass.EffectiveDeadlineAt);

        var single = Run(Base() with { PlannedCalls = 1, HasDependencies = true });
        Assert.Equal(["dependencies"], single.Causes);
    }

    [Fact]
    public void Structural_multipass_beyond_context_is_extended_automatically_without_pm_choice_or_timer()
    {
        var late = Run(Base(nowSeconds: 200) with { PlannedCalls = 7, HasDependencies = true, PayloadTokens = 90_000 });
        Assert.Equal((ClassificationOutcome.AdmitExtended, false), (late.Outcome, late.FailDeadlineExceeded));
        Assert.Null(late.ErrorCode);
    }

    // ------------------------------------------------------------------ prima regola vince

    [Fact]
    public void First_matching_rule_wins_across_simultaneous_conditions()
    {
        // 1 Reject batte tutto.
        var all = Base() with
        {
            InputValid = false, InvalidReason = "sources_invalid", RootTerminal = true, Now = T0.AddSeconds(4_000),
            RuntimeFingerprint = null, IsPreparation = true, IrreducibleInput = true, PlannedCalls = null,
        };
        Assert.Equal((ClassificationOutcome.Reject, "sources_invalid", ClassificationDisposition.NewRecord),
            (Run(all).Outcome, Run(all).Reason, Run(all).Disposition));

        // 2 terminale batte runtime/preparazione/limiti.
        var terminal = all with { InputValid = true };
        Assert.Equal((ClassificationDisposition.ReadTerminal, (ClassificationOutcome?)null), (Run(terminal).Disposition, Run(terminal).Outcome));

        // 2 hard cap batte runtime non verificato.
        var cap = terminal with { RootTerminal = false };
        Assert.Equal((ClassificationOutcome.Fail, "deadline_exceeded"), (Run(cap).Outcome, Run(cap).Reason));

        // 3 runtime non verificato batte preparazione.
        var runtime = cap with { Now = T0.AddSeconds(5) };
        Assert.Equal("runtime_unverified", Run(runtime).Reason);

        // 4 preparazione batte limiti irriducibili.
        var preparation = runtime with { RuntimeFingerprint = "fp-1" };
        Assert.Equal((ClassificationOutcome.AdmitExtended, "preparation"), (Run(preparation).Outcome, Run(preparation).Reason));

        // 5 limite irriducibile batte unknown e single_call.
        var irreducible = preparation with { IsPreparation = false };
        Assert.Equal("document_limit_exceeded", Run(irreducible).Reason);

        // 6 unknown batte la tabella 7/8.
        var unknown = irreducible with { IrreducibleInput = false };
        Assert.Equal((ClassificationOutcome.AdmitExtended, "workload_unknown"), (Run(unknown).Outcome, Run(unknown).Reason));

        // 7 quando tutto è determinabile e adatto.
        var fit = unknown with { PlannedCalls = 1, HasDependencies = false };
        Assert.Equal("single_call_fit", Run(fit).Reason);
    }

    // ------------------------------------------------------------------ unknown / reject / fail

    [Fact]
    public void Each_non_determinable_input_yields_workload_unknown_with_the_missing_fields()
    {
        static void Check(DocumentRegimeInput input, params string[] unknowns)
        {
            var decision = Run(input);
            Assert.Equal((ClassificationOutcome.AdmitExtended, "workload_unknown", ExecutionRegime.Extended), (decision.Outcome, decision.Reason, decision.Regime));
            Assert.Equal(unknowns, decision.Unknowns);
        }

        Check(Base() with { ShapeFloorTokens = null }, "shape_floor");
        Check(Base() with { ShapeSkeletonHash = null }, "shape_floor");
        Check(Base() with { ReasoningDisabled = false, ReasoningCapTokens = null }, "visible_reserve");
        Check(Base() with { TokenQuality = TokenCountQuality.Unavailable }, "token_count");
        Check(Base() with { PayloadTokens = null }, "token_count");
        Check(Base() with { PlannedCalls = null }, "planned_calls");
        Check(Base() with { HasDependencies = null }, "dependencies");
        Check(Base() with { PlannedCalls = null, HasDependencies = null, ShapeFloorTokens = null, TokenQuality = TokenCountQuality.Unavailable },
            "token_count", "planned_calls", "dependencies", "shape_floor");
    }

    [Fact]
    public void Calibrated_upper_bound_counts_like_a_measure_but_an_estimate_does_not()
    {
        Assert.Equal("single_call_fit", Run(Base() with { TokenQuality = TokenCountQuality.CalibratedUpperBound }).Reason);
        Assert.Equal("workload_unknown", Run(Base() with { TokenQuality = TokenCountQuality.Unavailable }).Reason);
    }

    [Theory]
    [InlineData(null, 10_000, 2_000, 1_000, "runtime_unverified")]
    [InlineData("", 10_000, 2_000, 1_000, "runtime_unverified")]
    [InlineData("fp", null, 2_000, 1_000, "runtime_unverified")]
    [InlineData("fp", 0, 2_000, 1_000, "runtime_unverified")]
    [InlineData("fp", 10_000, 0, 1_000, "config_invalid")]
    [InlineData("fp", 10_000, null, 1_000, "config_invalid")]
    [InlineData("fp", 10_000, 2_000, -1, "config_invalid")]
    [InlineData("fp", 3_000, 2_000, 1_000, "config_invalid")]
    [InlineData("fp", 2_500, 2_000, 1_000, "config_invalid")]
    public void Unverified_runtime_or_invalid_budget_fails_before_inferring(string? fingerprint, int? window, int? output, int? margin, string reason)
    {
        var decision = Run(Base() with { RuntimeFingerprint = fingerprint, WindowTokens = window, OutputTokens = output, MarginTokens = margin });
        Assert.Equal((ClassificationOutcome.Fail, reason, reason), (decision.Outcome, decision.Reason, decision.ErrorCode));
        Assert.Null(decision.Regime);
    }

    [Fact]
    public void Invalid_input_is_rejected_with_a_specific_error_and_irreducible_or_over_limit_plans_fail()
    {
        var rejected = Run(Base() with { InputValid = false, InvalidReason = "quota_exceeded" });
        Assert.Equal((ClassificationOutcome.Reject, "quota_exceeded", "quota_exceeded"), (rejected.Outcome, rejected.Reason, rejected.ErrorCode));
        Assert.Equal("input_invalid", Run(Base() with { InputValid = false }).Reason);

        Assert.Equal("document_limit_exceeded", Run(Base() with { IrreducibleInput = true }).ErrorCode);
        Assert.Equal("coverage_incomplete", Run(Base() with { CallLimitExceeded = true }).ErrorCode);
    }

    [Fact]
    public void Document_preparation_is_extended_with_its_own_deadline()
    {
        var decision = Run(Base() with { IsPreparation = true, PlannedCalls = null, PayloadTokens = null, TokenQuality = TokenCountQuality.Unavailable });
        Assert.Equal((ClassificationOutcome.AdmitExtended, "preparation", ExecutionRegime.Extended), (decision.Outcome, decision.Reason, decision.Regime));
        Assert.Equal(T0.AddSeconds(3_600), decision.EffectiveDeadlineAt);
    }

    // ------------------------------------------------------------------ Pending e tempo

    [Theory]
    [InlineData(0)]
    [InlineData(130)]
    [InlineData(190)]
    [InlineData(3_000)]
    public void Pending_is_provisional_extended_and_never_allows_inference(double nowSeconds)
    {
        var pending = DocumentRegimePolicy.Pending(Base(nowSeconds));
        Assert.Equal((ExecutionRegime.Extended, "preflight_pending", false), (pending.Regime, pending.Reason, pending.AiAllowed));
        Assert.Equal(T0.AddSeconds(3_600), pending.EffectiveDeadlineAt);
    }

    [Fact]
    public void Preflight_concluded_at_130_s_is_ordinary_with_50_s_left_from_the_original_t0()
    {
        var decision = Run(Base(nowSeconds: 130));
        Assert.Equal((ClassificationOutcome.AdmitOrdinary, ExecutionRegime.Ordinary, false), (decision.Outcome, decision.Regime, decision.FailDeadlineExceeded));
        Assert.Equal(T0.AddSeconds(180), decision.EffectiveDeadlineAt);
        Assert.Equal(T0.AddSeconds(180), decision.OrdinaryDeadlineAt);
        Assert.Equal(TimeSpan.FromSeconds(50), decision.EffectiveDeadlineAt - T0.AddSeconds(130));
    }

    [Fact]
    public void Preflight_concluded_at_190_s_classifies_ordinary_and_fails_without_ever_becoming_extended()
    {
        var decision = Run(Base(nowSeconds: 190));
        Assert.Equal((ClassificationOutcome.AdmitOrdinary, ExecutionRegime.Ordinary, true), (decision.Outcome, decision.Regime, decision.FailDeadlineExceeded));
        Assert.Equal("deadline_exceeded", decision.ErrorCode);
    }

    [Theory]
    [InlineData(119.999, false)]
    [InlineData(120.0, false)]
    [InlineData(179.999, false)]
    [InlineData(180.0, true)]
    public void No_time_threshold_changes_the_regime_only_the_current_deadline_ends_it(double nowSeconds, bool expired)
    {
        var decision = Run(Base(nowSeconds));
        Assert.Equal(ExecutionRegime.Ordinary, decision.Regime);
        Assert.Equal(T0.AddSeconds(180), decision.EffectiveDeadlineAt);
        Assert.Equal(expired, decision.FailDeadlineExceeded);
    }

    [Fact]
    public void Extended_admission_after_180_s_is_valid_until_its_own_hard_cap()
    {
        var plan = Base() with { PlannedCalls = 3 };
        Assert.False(Run(plan with { Now = T0.AddSeconds(3_599.999) }).FailDeadlineExceeded);
        var atCap = Run(plan with { Now = T0.AddSeconds(3_600) });
        Assert.Equal((ClassificationOutcome.Fail, "deadline_exceeded"), (atCap.Outcome, atCap.Reason));
    }

    [Fact]
    public void An_already_classified_root_is_not_reclassified_and_expires_at_its_current_deadline()
    {
        var alive = Run(Base(nowSeconds: 179.999) with { ClassifiedEffectiveDeadlineAt = T0.AddSeconds(180), PlannedCalls = 9 });
        Assert.Equal((ClassificationDisposition.ReuseExisting, (ClassificationOutcome?)null, (ExecutionRegime?)null), (alive.Disposition, alive.Outcome, alive.Regime));

        var dead = Run(Base(nowSeconds: 180) with { ClassifiedEffectiveDeadlineAt = T0.AddSeconds(180) });
        Assert.Equal((ClassificationOutcome.Fail, "deadline_exceeded"), (dead.Outcome, dead.Reason));

        // Promosso: la deadline corrente è l'hard cap, non il vecchio 180.
        var promoted = Run(Base(nowSeconds: 200) with { ClassifiedEffectiveDeadlineAt = T0.AddSeconds(3_600) });
        Assert.Equal(ClassificationDisposition.ReuseExisting, promoted.Disposition);
    }

    // ------------------------------------------------------------------ determinismo e assenza di predicati di carico

    [Fact]
    public void Same_input_gives_the_same_decision_and_hashes_and_now_is_not_part_of_the_input_hash()
    {
        var first = Run(Base(nowSeconds: 10));
        var second = Run(Base(nowSeconds: 10));
        Assert.Equal(first, second);

        var later = Run(Base(nowSeconds: 170));
        Assert.Equal(first.InputHash, later.InputHash);
        Assert.Equal((first.Outcome, first.Reason), (later.Outcome, later.Reason));
        Assert.Matches("^[0-9a-f]{64}$", first.InputHash);
        Assert.NotEqual(first.InputHash, Run(Base() with { PayloadTokens = 3_001 }).InputHash);
    }

    [Fact]
    public void Policy_hash_freezes_version_and_time_parameters()
    {
        var hash = DocumentRegimePolicy.ComputePolicyHash(180, 3_600);
        Assert.Equal(hash, DocumentRegimePolicy.ComputePolicyHash(180, 3_600));
        Assert.NotEqual(hash, DocumentRegimePolicy.ComputePolicyHash(120, 3_600));
        Assert.NotEqual(hash, DocumentRegimePolicy.ComputePolicyHash(180, 7_200));
        Assert.Equal("document-regime-v2", DocumentRegimePolicy.Version);
        Assert.Equal(hash, Run(Base()).PolicyHash);
    }

    [Fact]
    public void The_input_contract_has_no_queue_timer_duration_load_or_promotion_timer_predicate()
    {
        var forbidden = new[] { "queue", "timer", "duration", "load", "slot", "latency", "elapsed", "speed", "promotion", "wait" };
        var names = typeof(DocumentRegimeInput).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToArray();
        var words = typeof(DocumentRegimeInput).GetProperties()
            .SelectMany(p => System.Text.RegularExpressions.Regex.Matches(p.Name, "[A-Z][a-z0-9]*").Select(m => m.Value.ToLowerInvariant()))
            .ToArray();
        Assert.DoesNotContain(words, forbidden.Contains);
        // L'unico istante non tecnico è Now (clock DB per la regola 2 e il controllo di deadline), escluso dall'hash.
        Assert.Contains("now", names);
    }

    // ------------------------------------------------------------------ ClassificationRecord e skeleton

    [Fact]
    public void Record_carries_the_full_snapshot_without_load_or_speed()
    {
        var input = Base() with { ReasoningDisabled = false, ReasoningCapTokens = 500, ShapeFloorTokens = null, ShapeSkeletonHash = null };
        var decision = Run(input);
        var record = DocumentRegimePolicy.ToRecord(ClassificationOwnerKind.ProcessingRun, Guid.NewGuid(), Guid.NewGuid(), 1, input, decision);

        Assert.Equal(("document-regime-v2", ClassificationOutcome.AdmitExtended, "workload_unknown"), (record.Version, record.Outcome, record.Reason));
        Assert.Equal((decision.InputHash, decision.PolicyHash), (record.InputHash, record.PolicyHash));
        Assert.Equal((10_000, 2_000, 1_000, 7_000, 3_000, "measured"),
            (record.WindowTokens, record.OutputTokens, record.MarginTokens, record.InputBudgetTokens, record.PayloadTokens, record.TokenQuality));
        Assert.Equal(1_500, record.VisibleReserveTokens);
        Assert.Equal("""["shape_floor"]""", record.UnknownReasonsJson);
        Assert.Contains("\"tokens\":\"tokenizer\"", record.ProvenanceJson);

        var terminal = Run(Base() with { RootTerminal = true });
        Assert.Throws<InvalidOperationException>(() =>
            DocumentRegimePolicy.ToRecord(ClassificationOwnerKind.ChatTurn, Guid.NewGuid(), Guid.NewGuid(), 1, Base(), terminal));
    }

    [Fact]
    public void Shape_skeleton_is_deterministic_versioned_and_follows_the_schema_order()
    {
        const string schema = """
            {"type":"object","required":["items","note","count","kind","ok"],
             "properties":{
               "items":{"type":"array","minItems":2,"items":{"type":"object","required":["id","text"],
                 "properties":{"id":{"type":"string","minLength":3},"text":{"type":"string"}}}},
               "note":{"type":"string","minLength":2},
               "count":{"type":"integer","minimum":5},
               "kind":{"enum":["a","b"]},
               "ok":{"type":"boolean"},
               "optional":{"type":"string","minLength":50}}}
            """;
        var skeleton = ShapeSkeletonBuilder.Build(schema)!;
        Assert.Equal("""{"items":[{"id":"xxx","text":""},{"id":"xxx","text":""}],"note":"xx","count":5,"kind":"a","ok":false}""", skeleton.Json);
        Assert.Equal("shape-skeleton-v1", skeleton.Version);
        Assert.Equal(skeleton.Hash, ShapeSkeletonBuilder.Build(schema)!.Hash);
        Assert.Matches("^[0-9a-f]{64}$", skeleton.Hash);

        var ids = new Dictionary<string, IReadOnlyList<string>> { ["items"] = ["e-2", "e-1"] };
        var withIds = ShapeSkeletonBuilder.Build(schema, ids)!;
        Assert.StartsWith("""{"items":["e-1","e-2"],""", withIds.Json);
        Assert.NotEqual(skeleton.Hash, withIds.Hash);
    }

    [Fact]
    public void Schema_without_a_determinable_skeleton_is_unknown_not_a_guess()
    {
        Assert.Null(ShapeSkeletonBuilder.Build("""{"type":"object","required":["x"],"properties":{"x":{"$ref":"#/$defs/missing"}}}"""));
        Assert.Null(ShapeSkeletonBuilder.Build("""{"type":"object","required":["x"],"properties":{"x":{}}}"""));
        Assert.Null(ShapeSkeletonBuilder.Build("not json"));
        Assert.NotNull(ShapeSkeletonBuilder.Build("""{"type":"object","required":["x"],"properties":{"x":{"$ref":"#/$defs/s"}},"$defs":{"s":{"type":"string","minLength":1}}}"""));
    }

    // ------------------------------------------------------------------ fixture A-G e formula N indipendente dal regime

    [Fact]
    public void FixtureA_nominal_without_queue_committed_at_150_s_is_functional_and_temporal_pass_without_promotion()
    {
        var decision = Run(Base());
        Assert.Equal(ExecutionRegime.Ordinary, decision.Regime);
        var result = NominalProfile.Evaluate(true, true, T0, T0.AddSeconds(150));
        Assert.Equal((true, true, true), (result.Functional, result.Temporal, result.Pass));
    }

    [Fact]
    public void FixtureB_promoted_with_valid_additional_work_and_committed_at_150_s_passes_with_the_same_formula()
    {
        // La formula non conosce il regime: il risultato coincide con la fixture A.
        Assert.Equal(NominalProfile.Evaluate(true, true, T0, T0.AddSeconds(150)), NominalProfile.Evaluate(true, true, T0, T0.AddSeconds(150)));
        Assert.DoesNotContain(typeof(NominalProfile).GetMethod(nameof(NominalProfile.Evaluate))!.GetParameters(),
            p => p.ParameterType == typeof(ExecutionRegime) || p.ParameterType == typeof(ExecutionRegime?));
    }

    [Fact]
    public void FixtureC_promoted_and_complete_at_190_s_is_functional_success_with_temporal_fail()
    {
        var result = NominalProfile.Evaluate(true, true, T0, T0.AddSeconds(190));
        Assert.Equal((true, false, false), (result.Functional, result.Temporal, result.Pass));
    }

    [Fact]
    public void FixtureD_unpromoted_ordinary_has_no_late_commit_and_is_a_nominal_fail()
    {
        Assert.True(Run(Base(nowSeconds: 180)).FailDeadlineExceeded);
        var result = NominalProfile.Evaluate(false, true, T0, null);
        Assert.Equal((false, false, false), (result.Functional, result.Temporal, result.Pass));
    }

    [Theory]
    [InlineData(180.0, true)]
    [InlineData(180.001, false)]
    public void FixtureD_exact_180_s_boundary_counts_as_within_nominal(double commitSeconds, bool temporal) =>
        Assert.Equal(temporal, NominalProfile.Evaluate(true, true, T0, T0.AddSeconds(commitSeconds)).Temporal);

    [Fact]
    public void FixtureE_job_queued_for_120_s_keeps_the_same_regime_and_deadline_as_without_queue()
    {
        var withoutQueue = Run(Base(nowSeconds: 1));
        var queued = Run(Base(nowSeconds: 120));
        Assert.Equal((withoutQueue.Regime, withoutQueue.Outcome, withoutQueue.Reason, withoutQueue.EffectiveDeadlineAt),
            (queued.Regime, queued.Outcome, queued.Reason, queued.EffectiveDeadlineAt));
        Assert.False(queued.FailDeadlineExceeded);
    }

    [Fact]
    public void FixtureF_structural_multipass_is_extended_before_any_ai_and_never_a_nominal_profile_after_the_fact()
    {
        var decision = Run(Base() with { PlannedCalls = 12, HasDependencies = true });
        Assert.Equal((ExecutionRegime.Extended, "multipass_or_capacity"), (decision.Regime, decision.Reason));
        // Il dataset non diventa E ex post: la valutazione N dipende solo da completezza, qualità e tempo.
        Assert.True(NominalProfile.Evaluate(true, true, T0, T0.AddSeconds(170)).Pass);
        Assert.False(NominalProfile.Evaluate(true, false, T0, T0.AddSeconds(170)).Pass);
    }

    [Fact]
    public void FixtureG_pending_preflight_at_130_s_and_190_s_never_invokes_ai_and_is_not_extended_by_waiting()
    {
        Assert.All(new[] { 130d, 190d }, now => Assert.False(DocumentRegimePolicy.Pending(Base(now)).AiAllowed));
        Assert.Equal(ExecutionRegime.Ordinary, Run(Base(130)).Regime);
        Assert.Equal(ExecutionRegime.Ordinary, Run(Base(190)).Regime);
    }
}
