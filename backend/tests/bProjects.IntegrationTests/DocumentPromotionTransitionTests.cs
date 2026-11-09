using bProjects.App.AI;
using bProjects.App.Persistence;
using bProjects.App.Projects;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T047 (PG) — la sola CAS atomica di promozione Ordinary→Extended sotto lock del root e guard T118: precedenza terminale → scadenza/errori/limiti →
/// completamento → promozione; ricevuta additional_work verificata contro WorkOutput Committed, obbligazioni residue reali e batch senza cache
/// compatibile; un solo vincitore; rollback completo su ricevuta/batch invalidi; Hard, t0, fence e contatori non si azzerano; nessuna promozione
/// per tempo, coda o ripetizione. Niente timer, sweep né dispatcher (T119/T048). Nessun AI, nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentPromotionTransitionTests
{
    private const string Lease = "worker-a";
    private const long Fence = 3;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string H(char c) => new(c, 64);

    private sealed record Scenario(
        ParsingDatabase Database, SeededRoot Root, CheckpointWriter Writer, WorkOutput First, AdditionalWorkReceipt Receipt, DerivedBatchInput[] Batches)
    {
        public PromotionRequest Request(PromotionSignals? signals = null, string lease = Lease, long fence = Fence) =>
            new(Root.OwnerKind(), Root.Id, lease, fence, Receipt, Batches, signals);
    }

    private static DerivedBatchInput Batch(string id, string input, params string[] parents) =>
        new(id, "extract", null, 0, 0, 1, parents, """{"ranges":[[0,10]]}""", 120, "measured", input, "evidence-1.1.0", "p1");

    private static AdditionalWorkReceipt Rehash(AdditionalWorkReceipt receipt, IReadOnlyList<DerivedBatchInput> batches) =>
        receipt with { DerivedPlanHash = DocumentRegimeTransitions.ComputeDerivedPlanHash(receipt, batches) };

    private static CheckpointWriter WriterFor(SeededRoot root) => root.Kind == RootKind.Turn
        ? CheckpointWriter.ForChatTurn(root.Id, Lease, Fence, "chat-docs-1")
        : new CheckpointWriter(root.Id, Lease, Fence, WorkOwnerKind.ProcessingRun, "project-analysis-documents-v2", "1.26.0");

    /// <summary>Root Ordinary a 110 s dal t0 con manifesto, un primo batch b1 già Committed (copre l'obbligazione o1) e una ricevuta valida per b2 (copre o2).</summary>
    private static async Task<Scenario> NewScenarioAsync(RootKind kind, RootSpec? spec = null, ParsingDatabase? shared = null)
    {
        var database = shared ?? await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, kind, spec ?? new RootSpec { ElapsedSeconds = 110, CallCount = 1 });
        var writer = WriterFor(root);
        await using var context = database.NewContext();
        var store = new DocumentWorkStore(context, writer);
        await store.PublishManifestAsync(new WorkManifestInput(H('a'), H('b'), "fp-1", writer.DefinitionVersion!, "alg-1", DocumentRegimeRoots.PolicyHash, """{"nodes":["Load","PrepareBatch"]}"""), Ct);
        await store.PublishBatchesAsync([Batch("b1", H('1'))], Ct);
        const string payload = """{"evidences":[],"noEvidenceReason":"nessuna"}""";
        var first = (await store.CommitOutputAsync(
            new WorkOutputInput("b1", "InvokeBatch", H('1'), CanonicalJson.Sha256Hex(payload), payload, null, null, """["o1"]""", null, null, "evidence-1.1.0", "v1", null), null, Ct)).Output;
        DerivedBatchInput[] batches = [Batch("b2", H('2'), first.OutputHash)];
        var receipt = Rehash(new AdditionalWorkReceipt(AdditionalWorkReceipt.AdditionalWork, H('b'), [first.OutputHash], ["o2"], ["b2"], ""), batches);
        return new(database, root, writer, first, receipt, batches);
    }

    private static async Task<PromotionResult> PromoteAsync(Scenario s, PromotionRequest request)
    {
        await using var context = s.Database.NewContext();
        return await new DocumentRegimeTransitions(context).PromoteAsync(request, Ct);
    }

    private static Task<string> SnapshotAsync(Scenario s) => s.Database.ScalarAsync<string>($"""
        SELECT concat_ws('|', "Regime", "InitialRegime", "PromotionCount", "PromotedAt", "DeadlineAt", "EffectiveDeadlineAt", "HardDeadlineAt", "OrdinaryDeadlineAt",
                         "RequestedAt", "FencingToken", "AttemptCount", "CallCount", "Status", "LeaseOwner")
        FROM "{s.Root.Table}" WHERE "Id" = '{s.Root.Id}'
        """);

    private static async Task<(long Batches, long Transitions, long Outputs)> CountsAsync(Scenario s) =>
        (await s.Database.ScalarAsync<long>("SELECT count(*) FROM \"DerivedBatches\""),
         await s.Database.ScalarAsync<long>("SELECT count(*) FROM \"RegimeTransitions\""),
         await s.Database.ScalarAsync<long>("SELECT count(*) FROM \"WorkOutputs\""));

    private static async Task AssertUntouchedAsync(Scenario s, string snapshot, (long, long, long) counts)
    {
        Assert.Equal(snapshot, await SnapshotAsync(s));
        Assert.Equal(counts, await CountsAsync(s));
    }

    // ------------------------------------------------------------------ promozione valida

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task A_valid_additional_work_receipt_promotes_atomically_keeping_hard_t0_fence_and_counters(RootKind kind)
    {
        await using var s = Disposable(await NewScenarioAsync(kind));
        var before = await s.Value.Database.ScalarAsync<string>($"""
            SELECT concat_ws('|', "HardDeadlineAt", "RequestedAt", "FencingToken", "AttemptCount", "CallCount", "OrdinaryDeadlineAt", "InitialRegime")
            FROM "{s.Value.Root.Table}" WHERE "Id" = '{s.Value.Root.Id}'
            """);
        var batchesBefore = (await CountsAsync(s.Value)).Batches;

        var result = await PromoteAsync(s.Value, s.Value.Request());

        Assert.Equal(PromotionOutcome.Promoted, result.Outcome);
        var table = s.Value.Root.Table;
        var id = s.Value.Root.Id;
        Assert.Equal("Extended", await s.Value.Database.ScalarAsync<string>($"SELECT \"Regime\" FROM \"{table}\" WHERE \"Id\" = '{id}'"));
        Assert.Equal(1, await s.Value.Database.ScalarAsync<int>($"SELECT \"PromotionCount\" FROM \"{table}\" WHERE \"Id\" = '{id}'"));
        Assert.Equal("additional_work", await s.Value.Database.ScalarAsync<string>($"SELECT \"PromotedReason\" FROM \"{table}\" WHERE \"Id\" = '{id}'"));
        // DeadlineAt = EffectiveDeadlineAt = HardDeadlineAt; Hard, t0, fence, tentativi, CallCount, OrdinaryDeadlineAt e InitialRegime invariati.
        Assert.Equal(1, await s.Value.Database.ScalarAsync<int>($"SELECT count(*) FROM \"{table}\" WHERE \"Id\" = '{id}' AND \"DeadlineAt\" = \"HardDeadlineAt\" AND \"EffectiveDeadlineAt\" = \"HardDeadlineAt\""));
        var after = await s.Value.Database.ScalarAsync<string>($"""
            SELECT concat_ws('|', "HardDeadlineAt", "RequestedAt", "FencingToken", "AttemptCount", "CallCount", "OrdinaryDeadlineAt", "InitialRegime")
            FROM "{table}" WHERE "Id" = '{id}'
            """);
        Assert.Equal(before, after);

        // La scadenza di arrivo è davvero t0+3600 s, non 3600 s dal cambio: ~3490 s residui a 110 s dal t0.
        await using var context = s.Value.Database.NewContext();
        var authority = await DocumentDeadlineGuard.EvaluateAsync(context, s.Value.Root.OwnerKind(), id, Lease, Fence, lockRow: false, Ct);
        Assert.True(authority.Allowed);
        Assert.InRange(authority.Remaining.TotalSeconds, 3_480, 3_495);

        // Un'unica RegimeTransition con i dati completi, e i batch determinabili pubblicati nella stessa transazione.
        Assert.Equal(1L, await s.Value.Database.ScalarAsync<long>("SELECT count(*) FROM \"RegimeTransitions\""));
        Assert.Equal(1, await s.Value.Database.ScalarAsync<int>($"""
            SELECT count(*) FROM "RegimeTransitions" t JOIN "{table}" r ON r."Id" = t."RootId"
            WHERE t."RootId" = '{id}' AND t."FromRegime" = 'Ordinary' AND t."ToRegime" = 'Extended' AND t."Reason" = 'additional_work'
              AND t."OldDeadlineAt" = r."OrdinaryDeadlineAt" AND t."NewDeadlineAt" = r."HardDeadlineAt" AND t."PolicyHash" = r."PolicyHash" AND t."At" = r."PromotedAt"
              AND t."Fence" = {Fence} AND t."DerivedPlanHash" = '{s.Value.Receipt.DerivedPlanHash}'
              AND t."ElapsedMs" BETWEEN 109000 AND 125000 AND t."ReceiptJson"->>'sourceHash' = '{H('b')}' AND t."ReceiptJson"->'additionalBatchIds' = '["b2"]'::jsonb
            """));
        Assert.Equal(batchesBefore + 1, (await CountsAsync(s.Value)).Batches);
        Assert.Equal(1L, await s.Value.Database.ScalarAsync<long>("SELECT count(*) FROM \"DerivedBatches\" WHERE \"BatchId\" = 'b2'"));
    }

    [Fact]
    public async Task A_second_attempt_is_a_no_op_and_does_not_extend_anything()
    {
        await using var s = Disposable(await NewScenarioAsync(RootKind.Turn));
        Assert.Equal(PromotionOutcome.Promoted, (await PromoteAsync(s.Value, s.Value.Request())).Outcome);
        var snapshot = await SnapshotAsync(s.Value);
        var counts = await CountsAsync(s.Value);

        var again = await PromoteAsync(s.Value, s.Value.Request());

        Assert.Equal(PromotionOutcome.AlreadyPromoted, again.Outcome);
        await AssertUntouchedAsync(s.Value, snapshot, counts);   // né nuova deadline, né seconda transizione, né PromotionCount 2
    }

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task Concurrent_attempts_have_exactly_one_winner(RootKind kind)
    {
        await using var s = Disposable(await NewScenarioAsync(kind));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => PromoteAsync(s.Value, s.Value.Request()), Ct)));

        Assert.Equal(1, results.Count(r => r.Outcome == PromotionOutcome.Promoted));
        Assert.Equal(7, results.Count(r => r.Outcome == PromotionOutcome.AlreadyPromoted));
        Assert.Equal(1L, (await CountsAsync(s.Value)).Transitions);
        Assert.Equal(1, await s.Value.Database.ScalarAsync<int>($"SELECT \"PromotionCount\" FROM \"{s.Value.Root.Table}\" WHERE \"Id\" = '{s.Value.Root.Id}'"));
    }

    // ------------------------------------------------------------------ ricevuta e batch non validi: rollback completo

    [Theory]
    [InlineData("reason_queue_wait", "reason_not_additional_work")]
    [InlineData("reason_slow_inference", "reason_not_additional_work")]
    [InlineData("reason_retry", "reason_not_additional_work")]
    [InlineData("reason_commit_wait", "reason_not_additional_work")]
    [InlineData("source", "source_hash_mismatch")]
    [InlineData("no_trigger", "receipt_incomplete")]
    [InlineData("unknown_trigger", "trigger_output_not_committed")]
    [InlineData("no_obligation", "receipt_incomplete")]
    [InlineData("covered_obligation", "obligation_already_covered")]
    [InlineData("no_batch", "receipt_incomplete")]
    [InlineData("batch_not_in_receipt", "batches_not_in_receipt")]
    [InlineData("bad_plan_hash", "derived_plan_hash_mismatch")]
    [InlineData("unknown_parent", "parent_output_not_committed")]
    [InlineData("cached_batch", "batch_has_compatible_output")]
    [InlineData("cached_input", "batch_has_compatible_output")]
    [InlineData("invalid_input_hash", "invalid_work_input")]
    [InlineData("batch_conflict", "integrity_conflict")]
    public async Task An_invalid_receipt_or_batch_rolls_everything_back(string variant, string expectedReason)
    {
        await using var s = Disposable(await NewScenarioAsync(RootKind.Turn));
        var scenario = s.Value;
        var receipt = scenario.Receipt;
        var batches = scenario.Batches;
        var first = scenario.First.OutputHash;

        switch (variant)
        {
            case var reason when reason.StartsWith("reason_", StringComparison.Ordinal):
                receipt = receipt with { Reason = reason["reason_".Length..] };
                break;
            case "source": receipt = receipt with { SourceHash = H('f') }; break;
            case "no_trigger": receipt = receipt with { TriggerOutputHashes = [] }; break;
            case "unknown_trigger": receipt = receipt with { TriggerOutputHashes = [H('9')] }; break;
            case "no_obligation": receipt = receipt with { RemainingObligationIds = [] }; break;
            case "covered_obligation": receipt = receipt with { RemainingObligationIds = ["o1"] }; break;   // già coperta dall'output Committed di b1
            case "no_batch": batches = []; receipt = receipt with { AdditionalBatchIds = [] }; break;
            case "batch_not_in_receipt": receipt = receipt with { AdditionalBatchIds = ["b2", "b3"] }; break;
            case "bad_plan_hash": receipt = receipt with { DerivedPlanHash = H('e') }; break;
            case "unknown_parent": batches = [Batch("b2", H('2'), H('8'))]; break;
            case "invalid_input_hash": batches = [Batch("b2", H('2'), first), Batch("b3", "zz", first)]; receipt = receipt with { AdditionalBatchIds = ["b2", "b3"] }; break;
            case "cached_input": batches = [Batch("b2", H('1'), first)]; break;   // stesso input di un output già Committed: cache compatibile
            case "cached_batch":
                await using (var context = scenario.Database.NewContext())
                {
                    var store = new DocumentWorkStore(context, scenario.Writer);
                    await store.PublishBatchesAsync([Batch("b2", H('2'), first)], Ct);
                    const string payload = """{"evidences":[],"noEvidenceReason":"altra"}""";
                    await store.CommitOutputAsync(new WorkOutputInput("b2", "InvokeBatch", H('2'), CanonicalJson.Sha256Hex(payload), payload, null, null, """["x"]""", null, null, "evidence-1.1.0", "v1", null), null, Ct);
                }

                break;
            case "batch_conflict":
                await using (var context = scenario.Database.NewContext())
                {
                    await new DocumentWorkStore(context, scenario.Writer).PublishBatchesAsync([Batch("b3", H('7'), first)], Ct);   // stesso batchId, altro input
                }

                batches = [Batch("b2", H('2'), first), Batch("b3", H('6'), first)];
                receipt = receipt with { AdditionalBatchIds = ["b2", "b3"] };
                break;
        }

        // Tutte le varianti (tranne bad_plan_hash e le incompletezze/mancanze volute) hanno un hash coerente: il solo difetto è quello in prova.
        if (variant is not ("bad_plan_hash" or "batch_not_in_receipt"))
        {
            receipt = Rehash(receipt, batches);
        }

        var snapshot = await SnapshotAsync(scenario);
        var counts = await CountsAsync(scenario);

        var result = await PromoteAsync(scenario, scenario.Request() with { Receipt = receipt, Batches = batches });

        Assert.Equal(PromotionOutcome.InvalidReceipt, result.Outcome);
        Assert.Equal(expectedReason, result.Reason);
        await AssertUntouchedAsync(scenario, snapshot, counts);   // regime, deadline, contatori, batch (anche b2 valido accanto a b3 invalido), transizioni
    }

    [Fact]
    public async Task A_failure_after_the_batches_and_the_cas_rolls_everything_back()
    {
        await using var s = Disposable(await NewScenarioAsync(RootKind.Turn));
        // Stato incoerente simulato: una transizione per questo root esiste già (l'unique la rifiuta in coda alla transazione).
        await s.Value.Database.ExecuteAsync($"""
            INSERT INTO "RegimeTransitions" ("Id","ProjectId","RootKind","RootId","FromRegime","ToRegime","At","Reason","OldDeadlineAt","NewDeadlineAt","ElapsedMs","PolicyHash","Fence","ReceiptJson","DerivedPlanHash")
            VALUES ('{Guid.NewGuid()}','{s.Value.Root.Project}','ChatTurn','{s.Value.Root.Id}','Ordinary','Extended',now(),'additional_work',now(),now() + interval '1 hour',1,'{H('c')}',3,jsonb_build_object(),'{H('d')}')
            """);
        var snapshot = await SnapshotAsync(s.Value);
        var counts = await CountsAsync(s.Value);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => PromoteAsync(s.Value, s.Value.Request()));

        Assert.Equal("23505", ex.SqlState);
        await AssertUntouchedAsync(s.Value, snapshot, counts);   // né Regime Extended, né PromotionCount 1, né batch b2
    }

    // ------------------------------------------------------------------ precedenza

    [Theory]
    [InlineData("terminal", PromotionOutcome.Terminal)]                 // root terminale: prevale su tutto (anche scaduto, errori, completamento)
    [InlineData("expired", PromotionOutcome.DeadlineExceeded)]          // scadenza esatta: prevale su errori, limiti, completamento, promozione
    [InlineData("permanent_error", PromotionOutcome.PermanentError)]    // errore permanente: prima del completamento
    [InlineData("limit", PromotionOutcome.LimitExceeded)]               // limite superato: prima del completamento
    [InlineData("call_limit", PromotionOutcome.LimitExceeded)]          // CallCount + batch aggiuntivi oltre il massimo
    [InlineData("complete", PromotionOutcome.CompletionPrecedes)]       // risultato già valido e completo: nessuna promozione per l'attesa del commit
    public async Task Precedence_is_terminal_then_deadline_errors_limits_then_completion_then_promotion(string variant, PromotionOutcome expected)
    {
        RootSpec? spec = variant == "call_limit" ? new RootSpec { ElapsedSeconds = 110, CallCount = 2 } : null;
        await using var s = Disposable(await NewScenarioAsync(RootKind.Run, spec));
        if (variant == "expired")
        {
            // Scadenza «esatta»: la deadline corrente coincide con l'istante appena trascorso (un solo valore per le due colonne).
            await s.Value.Database.ExecuteAsync($"UPDATE \"ProcessingRuns\" x SET \"DeadlineAt\" = v.t, \"EffectiveDeadlineAt\" = v.t FROM (SELECT clock_timestamp() - interval '1 millisecond' AS t) v WHERE x.\"Id\" = '{s.Value.Root.Id}'");
        }

        if (variant == "terminal")
        {
            await s.Value.Database.ExecuteAsync($"UPDATE \"ProcessingRuns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x', \"CompletedAt\" = now(), \"LeaseOwner\" = NULL, \"LeaseUntil\" = NULL WHERE \"Id\" = '{s.Value.Root.Id}'");
        }

        // Ogni variante porta anche i segnali di priorità inferiore: deve vincere il più alto.
        var signals = new PromotionSignals(
            PermanentError: variant is "terminal" or "expired" or "permanent_error" ? "schema_invalid" : null,
            LimitExceeded: variant is "terminal" or "expired" or "permanent_error" or "limit" ? "byte_limit" : null,
            CompleteResultReady: true,
            MaxAiCalls: variant == "call_limit" ? 2 : 512);
        var snapshot = await SnapshotAsync(s.Value);
        var counts = await CountsAsync(s.Value);

        var result = await PromoteAsync(s.Value, s.Value.Request(signals));

        Assert.Equal(expected, result.Outcome);
        await AssertUntouchedAsync(s.Value, snapshot, counts);
    }

    [Fact]
    public async Task A_final_result_exactly_at_the_call_limit_does_not_fail_and_a_valid_promotion_at_the_limit_is_allowed()
    {
        await using var s = Disposable(await NewScenarioAsync(RootKind.Turn, new RootSpec { ElapsedSeconds = 110, CallCount = 1 }));

        // CallCount 1 + un batch aggiuntivo = 2 = massimo ammesso: non è un superamento.
        var result = await PromoteAsync(s.Value, s.Value.Request(new PromotionSignals(MaxAiCalls: 2)));

        Assert.Equal(PromotionOutcome.Promoted, result.Outcome);
    }

    // ------------------------------------------------------------------ idoneità e autorità

    [Fact]
    public async Task Only_the_current_owner_with_fence_and_lease_can_promote()
    {
        await using var s = Disposable(await NewScenarioAsync(RootKind.Turn));
        var counts = await CountsAsync(s.Value);

        Assert.Equal(PromotionOutcome.Fenced, (await PromoteAsync(s.Value, s.Value.Request(fence: Fence - 1))).Outcome);
        Assert.Equal(PromotionOutcome.Fenced, (await PromoteAsync(s.Value, s.Value.Request(lease: "worker-b"))).Outcome);
        await s.Value.Database.ExecuteAsync($"UPDATE \"ChatTurns\" SET \"LeaseUntil\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = '{s.Value.Root.Id}'");
        Assert.Equal(PromotionOutcome.Fenced, (await PromoteAsync(s.Value, s.Value.Request())).Outcome);
        Assert.Equal(1L, await s.Value.Database.ScalarAsync<long>($"SELECT count(*) FROM \"ChatTurns\" WHERE \"Id\" = '{s.Value.Root.Id}' AND \"Regime\" = 'Ordinary' AND \"PromotionCount\" = 0"));
        Assert.Equal(counts, await CountsAsync(s.Value));
    }

    [Fact]
    public async Task Extended_legacy_and_pending_roots_are_not_eligible()
    {
        await using var extended = Disposable(await NewScenarioAsync(RootKind.Turn, new RootSpec { Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Extended, ElapsedSeconds = 110, CallCount = 1 }));
        var result = await PromoteAsync(extended.Value, extended.Value.Request());
        Assert.Equal((PromotionOutcome.NotEligible, "already_extended"), (result.Outcome, result.Reason));
        Assert.Equal(0, await extended.Value.Database.ScalarAsync<int>("SELECT \"PromotionCount\" FROM \"ChatTurns\""));   // un Extended non «si promuove»: nessun reset, nessun conteggio

        await using var legacy = Disposable(await NewScenarioAsync(RootKind.Run, new RootSpec { Legacy = true }));
        Assert.Equal((PromotionOutcome.NotEligible, "legacy_root"), await Pair(PromoteAsync(legacy.Value, legacy.Value.Request())));

        await using var pending = Disposable(await NewScenarioAsync(RootKind.Turn, new RootSpec { Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Extended, ElapsedSeconds = 110, CallCount = 1 }));
        await pending.Value.Database.ExecuteAsync("UPDATE \"ChatTurns\" SET \"ClassificationStatus\" = 'Pending', \"InitialRegime\" = NULL, \"OrdinaryDeadlineAt\" = NULL");
        Assert.Equal(PromotionOutcome.NotEligible, (await PromoteAsync(pending.Value, pending.Value.Request())).Outcome);   // nessuna inferenza/promozione prima della classificazione
    }

    private static async Task<(PromotionOutcome, string?)> Pair(Task<PromotionResult> task)
    {
        var result = await task;
        return (result.Outcome, result.Reason);
    }

    // ------------------------------------------------------------------ schema

    [Fact]
    public async Task Regime_transitions_are_unique_per_root_append_only_and_only_additional_work_promotions_exist()
    {
        await using var s = Disposable(await NewScenarioAsync(RootKind.Turn));
        Assert.Equal(PromotionOutcome.Promoted, (await PromoteAsync(s.Value, s.Value.Request())).Outcome);
        var database = s.Value.Database;

        // Nessun PromotionDueAt: il tempo trascorso non promuove e non esiste una scadenza di promozione da maturare.
        foreach (var table in new[] { "ChatTurns", "ProcessingRuns", "DocumentPreparations" })
        {
            Assert.Equal(0, await database.ScalarAsync<int>($"SELECT count(*) FROM information_schema.columns WHERE table_name = '{table}' AND column_name IN ('PromotionDueAt', 'PromotionAfterSeconds')"));
        }

        string Insert(string root, string from = "Ordinary", string to = "Extended", string reason = "additional_work") => $"""
            INSERT INTO "RegimeTransitions" ("Id","ProjectId","RootKind","RootId","FromRegime","ToRegime","At","Reason","OldDeadlineAt","NewDeadlineAt","ElapsedMs","PolicyHash","Fence","ReceiptJson","DerivedPlanHash")
            VALUES ('{Guid.NewGuid()}','{s.Value.Root.Project}','ChatTurn','{root}','{from}','{to}',now(),'{reason}',now(),now() + interval '1 hour',1,'{H('c')}',3,jsonb_build_object(),'{H('d')}')
            """;

        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(Insert(s.Value.Root.Id.ToString())))).SqlState);   // unique(rootId, to=Extended)
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(Insert(Guid.NewGuid().ToString(), reason: "queue_wait")))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(Insert(Guid.NewGuid().ToString(), from: "Extended", to: "Ordinary")))).SqlState);   // nessun downgrade
        Assert.Equal("23001", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync("UPDATE \"RegimeTransitions\" SET \"Reason\" = 'additional_work'"))).SqlState);
        Assert.Equal("23001", (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync("DELETE FROM \"RegimeTransitions\""))).SqlState);
    }

    [Fact]
    public void The_derived_plan_hash_is_canonical_order_insensitive_for_sets_and_sensitive_to_parents_and_batches()
    {
        var batch = Batch("b2", H('2'), H('1'), H('3'));
        var receipt = new AdditionalWorkReceipt(AdditionalWorkReceipt.AdditionalWork, H('b'), [H('4'), H('5')], ["o2", "o3"], ["b2"], "");
        var hash = DocumentRegimeTransitions.ComputeDerivedPlanHash(receipt, [batch]);

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, DocumentRegimeTransitions.ComputeDerivedPlanHash(receipt with { TriggerOutputHashes = [H('5'), H('4')], RemainingObligationIds = ["o3", "o2"] }, [batch]));   // insiemi ordinati
        Assert.NotEqual(hash, DocumentRegimeTransitions.ComputeDerivedPlanHash(receipt, [Batch("b2", H('2'), H('3'), H('1'))]));   // l'ordine dei genitori è significativo
        Assert.NotEqual(hash, DocumentRegimeTransitions.ComputeDerivedPlanHash(receipt with { RemainingObligationIds = ["o2"] }, [batch]));
        Assert.NotEqual(hash, DocumentRegimeTransitions.ComputeDerivedPlanHash(receipt, [Batch("b2", H('9'), H('1'), H('3'))]));
        Assert.Equal(hash, DocumentRegimeTransitions.ComputeDerivedPlanHash(receipt with { DerivedPlanHash = H('f') }, [batch]));   // il proprio campo è escluso
    }

    // ------------------------------------------------------------------ evento dello stream (T064)

    [Fact]
    public async Task Promotion_publishes_regime_changed_only_when_it_commits()
    {
        await using var s = Disposable(await NewScenarioAsync(RootKind.Turn));
        var hub = new ProjectEventHub();
        using var subscription = hub.Subscribe(s.Value.Root.Project);

        await using (var context = s.Value.Database.NewContext())
        {
            // Rifiuto (fence errato): nessuna transizione, quindi nessun evento.
            var fenced = await new DocumentRegimeTransitions(context, hub).PromoteAsync(s.Value.Request(fence: Fence + 1), Ct);
            Assert.NotEqual(PromotionOutcome.Promoted, fenced.Outcome);
            Assert.False(subscription.Reader.TryRead(out _));
        }

        await using (var context = s.Value.Database.NewContext())
        {
            Assert.Equal(PromotionOutcome.Promoted, (await new DocumentRegimeTransitions(context, hub).PromoteAsync(s.Value.Request(), Ct)).Outcome);
        }

        Assert.True(subscription.Reader.TryRead(out var evt));
        Assert.Equal(ProjectEventHub.ExecutionRegimeChanged, evt.Name);
        using var json = System.Text.Json.JsonDocument.Parse(evt.Data);
        Assert.Equal(s.Value.Root.Id, json.RootElement.GetProperty("rootId").GetGuid());
        Assert.Equal("additional_work", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal(s.Value.Receipt.DerivedPlanHash, json.RootElement.GetProperty("receiptHash").GetString());
        Assert.Equal(1, await s.Value.Database.ScalarAsync<int>($"""SELECT count(*) FROM "{s.Value.Root.Table}" WHERE "Id" = '{s.Value.Root.Id}' AND "HardDeadlineAt" = "EffectiveDeadlineAt" AND to_char("EffectiveDeadlineAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS') = '{json.RootElement.GetProperty("effectiveDeadlineAt").GetDateTimeOffset().UtcDateTime:yyyy-MM-ddTHH:mm:ss}'"""));
        Assert.False(subscription.Reader.TryRead(out _));
    }

    // ------------------------------------------------------------------ supporto

    private static AsyncBox Disposable(Scenario scenario) => new(scenario);

    private sealed class AsyncBox(Scenario scenario) : IAsyncDisposable
    {
        public Scenario Value { get; } = scenario;

        public ValueTask DisposeAsync() => Value.Database.DisposeAsync();
    }
}
