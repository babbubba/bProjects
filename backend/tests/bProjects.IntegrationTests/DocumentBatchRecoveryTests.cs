using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Persistence;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T044 (MAF-PG) — recovery B2 del grafo batch con il port di produzione (<see cref="DocumentBatchPort"/>): PostgreSQL reale (output,
/// batch, checkpoint nativi, lease/fence), SDK MAF 1.26.0 reale, modello FINTO a livello HTTP (nessun DGX) dietro l'AgentFactory e il
/// preflight reali. Ogni «riavvio» crea contesto EF, store, factory, port e grafo nuovi con un fence nuovo, come un processo nuovo: il
/// database è l'unico stato condiviso. Si prova l'unicità degli effetti, non l'exactly-once delle chiamate al modello.
/// Piano di prova: leaf 0 e 1, join 2 = (0,1) con genitori ordinati, figlio 3 di 0 pubblicato nel commit dell'output 0.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentBatchRecoveryTests : IAsyncLifetime
{
    private const string Schema = "evidence-1.1.0";
    private const string Validation = "v1";
    private const string Definition = "doc-batch-1";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string H(char c) => new(c, 64);
    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private ParsingDatabase? _database;
    private SeededParse _seed = null!;
    private string _session = null!;

    public async ValueTask InitializeAsync()
    {
        if (TestEnvironment.PostgresAdminConnectionString is null)
        {
            return;
        }

        _database = await ParsingDatabase.CreateAsync();
        _seed = await _database.SeedParseAsync();
        _session = "sess-" + _seed.Job.ToString("N");
        await Db.ExecuteAsync($"""
            UPDATE "DocumentJobs" SET "Status" = 'Running', "LeaseOwner" = 'worker-a', "LeaseUntil" = now() + interval '30 minutes', "FencingToken" = 5, "AttemptCount" = 1,
                   "StartedAt" = now(), "WorkflowDefinitionVersion" = '{Definition}' WHERE "Id" = '{_seed.Job}';
            UPDATE "DocumentPreparations" SET "Status" = 'Extracting' WHERE "Id" = '{_seed.Preparation}';
            """);
    }

    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private ParsingDatabase Db => _database ?? throw new InvalidOperationException();

    // ------------------------------------------------------------------ scenari

    [Fact]
    public async Task A_clean_run_reserves_one_call_per_batch_commits_children_and_publishes_the_join_with_ordered_parents()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        await using var process = NewProcess(runtime);

        var result = await process.RunAsync(_session);

        Assert.Equal(new BatchRunResult(4, 4), result);
        Assert.Equal([0, 1, 2, 3], runtime.Requests);
        Assert.Equal(4, await CallCountAsync());
        Assert.Equal(4L, await Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
        Assert.Equal(4L, await Db.ScalarAsync<long>("""SELECT count(*) FROM "DerivedBatches" """));
        var (o0, o1) = (await OutputHashAsync(0), await OutputHashAsync(1));
        Assert.Equal(JsonSerializer.Serialize(new[] { o0, o1 }), await ParentsAsync(2));
        Assert.Equal(JsonSerializer.Serialize(new[] { o0 }), await ParentsAsync(3));
        Assert.True(await Db.ScalarAsync<long>($"""SELECT count(*) FROM "WorkflowCheckpoints" WHERE "DocumentJobId" = '{_seed.Job}'""") >= 8);
    }

    [Fact]
    public async Task Crash_after_the_reservation_and_before_the_network_keeps_the_consumed_call_and_a_restart_reinfers_the_batch()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        await using (var first = NewProcess(runtime, hook: (step, ordinal) => step == "reserve.after" && ordinal == 1 ? throw new CrashException() : ValueTask.CompletedTask))
        {
            await CrashedAsync(first.RunAsync(_session));
        }

        Assert.Equal([0], runtime.Requests);        // la rete non è stata raggiunta per il batch 1
        Assert.Equal(2, await CallCountAsync());    // riserva già consumata, non restituita
        Assert.Equal(1L, await OutputsAsync());

        await TakeOverAsync(6, "worker-b");
        await using var second = NewProcess(runtime, fence: 6, lease: "worker-b");
        Assert.Equal(new BatchRunResult(4, 3), await second.RunAsync(_session));

        Assert.Equal([0, 1, 2, 3], runtime.Requests);
        Assert.Equal(5, await CallCountAsync());    // 4 invii effettivi + 1 riserva consumata senza rete: costo prudenziale, non azzerato
        Assert.Equal(4L, await OutputsAsync());
    }

    [Fact]
    public async Task Crash_after_the_model_answered_but_before_any_output_ignores_the_lost_answer_and_reinfers_with_the_counter_preserved()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel { AfterAnswer = batch => batch == 1 ? throw new IOException("processo morto prima di ricevere la risposta") : null };
        await using (var first = NewProcess(runtime))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => first.RunAsync(_session));
        }

        Assert.Equal([0, 1], runtime.Requests);
        Assert.Equal(2, await CallCountAsync());
        Assert.Equal(1L, await OutputsAsync());      // nessun output su una risposta non ricevuta/non validata

        runtime.AfterAnswer = null;
        await TakeOverAsync(6, "worker-b");
        await using var second = NewProcess(runtime, fence: 6, lease: "worker-b");
        await second.RunAsync(_session);

        Assert.Equal([0, 1, 1, 2, 3], runtime.Requests);   // reinferenza ammessa prima del commit
        Assert.Equal(5, await CallCountAsync());          // ogni invio ha consumato la propria riserva
        Assert.Equal(4L, await OutputsAsync());
    }

    [Fact]
    public async Task Answer_saved_in_the_checkpoint_before_the_commit_is_revalidated_after_restart_without_a_new_inference()
    {
        // DECISIONE (T044, conferma la scelta del grafo T043): il messaggio Responded fa parte del checkpoint nativo; alla ripresa torna a
        // ValidatePersistBatch, che applica la STESSA validazione e lo stesso commit fenced (l'inputHash è riletto dal DB). Non si ignora una
        // risposta già pagata e salvata dal framework; se non fosse valida l'esito è lo stesso errore permanente, mai un'inferenza nascosta.
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        await using (var first = NewProcess(runtime, hook: (step, ordinal) => step == "persist.before" && ordinal == 1 ? throw new CrashException() : ValueTask.CompletedTask))
        {
            await CrashedAsync(first.RunAsync(_session));
        }

        Assert.Equal([0, 1], runtime.Requests);
        Assert.Equal(1L, await OutputsAsync());
        var callsBefore = await CallCountAsync();

        await TakeOverAsync(6, "worker-b");
        await using var second = NewProcess(runtime, fence: 6, lease: "worker-b");
        var result = await second.RunAsync(_session);

        Assert.Equal([0, 1, 2, 3], runtime.Requests);       // il batch 1 non è reinferito
        Assert.Equal(2, callsBefore);
        Assert.Equal(4, await CallCountAsync());             // nessuna riserva duplicata per il batch 1
        Assert.Equal(2, result.Invocations);
        Assert.Equal(6L, await Db.ScalarAsync<long>($"""SELECT "CommittedFence" FROM "WorkOutputs" WHERE "BatchId" = '{BatchId(1)}'"""));
        Assert.Equal(4L, await OutputsAsync());
    }

    [Fact]
    public async Task An_invalid_saved_answer_fails_again_with_the_same_permanent_error_and_never_reinfers_in_hiding()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel { Responder = batch => batch == 1 ? """{"batch":99}""" : null };
        await using (var first = NewProcess(runtime))
        {
            var failure = await Assert.ThrowsAsync<DocumentWorkException>(() => first.RunAsync(_session));
            Assert.Equal(DocumentWorkException.WorkOutputInvalid, failure.Code);
        }

        var calls = await CallCountAsync();
        await TakeOverAsync(6, "worker-b");
        await using var second = NewProcess(runtime, fence: 6, lease: "worker-b");
        var again = await Assert.ThrowsAsync<DocumentWorkException>(() => second.RunAsync(_session));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, again.Code);
        Assert.Equal([0, 1], runtime.Requests);
        Assert.Equal(calls, await CallCountAsync());
        Assert.Equal(1L, await OutputsAsync());
    }

    [Fact]
    public async Task Output_committed_before_the_checkpoint_is_reused_with_the_same_ids_hashes_and_children_and_without_a_call()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        await using (var first = NewProcess(runtime, hook: (step, ordinal) => step == "persist.after" && ordinal == 1 ? throw new CrashException() : ValueTask.CompletedTask))
        {
            await CrashedAsync(first.RunAsync(_session));
        }

        var outputs = await OutputSnapshotAsync();
        var batches = await BatchSnapshotAsync();
        Assert.Equal(2, outputs.Count);
        Assert.Equal(3, batches.Count);          // 0, 1 e il figlio 3 pubblicato dal commit dell'output 0
        var callsBefore = await CallCountAsync();

        await TakeOverAsync(6, "worker-b");
        await using var second = NewProcess(runtime, fence: 6, lease: "worker-b");
        await second.RunAsync(_session);

        Assert.Equal([0, 1, 2, 3], runtime.Requests);
        Assert.Equal(callsBefore + 2, await CallCountAsync());
        var after = await OutputSnapshotAsync();
        Assert.Equal(outputs, after.Take(2).ToList());     // stessi Id, hash e fence originale (5): il replay non riscrive
        Assert.Equal(batches.OrderBy(b => b).ToList(), (await BatchSnapshotAsync()).Where(batches.Contains).OrderBy(b => b).ToList());
    }

    [Fact]
    public async Task Resuming_from_every_earlier_checkpoint_in_a_new_process_reuses_committed_outputs_without_any_call_and_changes_nothing()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        await using (var first = NewProcess(runtime))
        {
            await first.RunAsync(_session);
        }

        var checkpoints = await CheckpointIndexAsync();
        var outputs = await OutputSnapshotAsync();
        var batches = await BatchSnapshotAsync();
        var calls = await CallCountAsync();
        var guardHits = 0;

        foreach (var info in checkpoints.Take(checkpoints.Count - 1))
        {
            await TakeOverAsync(await NextFenceAsync(), "worker-r");
            await using var process = NewProcess(runtime, fence: await CurrentFenceAsync(), lease: "worker-r");
            var invoked = 0;
            var result = await process.RunAsync(_session, info, e => invoked += e is ExecutorInvokedEvent { ExecutorId: DocumentBatchWorkflow.InvokeId } ? 1 : 0);

            Assert.Equal(4, result.Batches);
            Assert.Equal(0, result.Invocations);
            guardHits += invoked;     // un Miss in volo verso InvokeBatch: il guard interno trova l'hit prima della sessione
        }

        Assert.Equal([0, 1, 2, 3], runtime.Requests);        // nessuna nuova richiesta di rete
        Assert.Equal(calls, await CallCountAsync());         // nessuna riserva
        Assert.Equal(outputs, await OutputSnapshotAsync());  // stessi ID, hash, fence e figli
        Assert.Equal(batches, await BatchSnapshotAsync());
        Assert.True(guardHits > 0, "almeno un checkpoint contiene il Miss precedente al commit");
    }

    [Fact]
    public async Task Repeated_Advance_after_a_crash_is_idempotent_and_adds_neither_duplicates_nor_calls()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        await using (var first = NewProcess(runtime, hook: (step, ordinal) => step == "hasmore.after" && ordinal == 2 ? throw new CrashException() : ValueTask.CompletedTask))
        {
            await CrashedAsync(first.RunAsync(_session));      // Advance del batch 1 completato, checkpoint non scritto
        }

        var outputs = await OutputSnapshotAsync();
        await TakeOverAsync(6, "worker-b");
        await using var second = NewProcess(runtime, fence: 6, lease: "worker-b");
        await second.RunAsync(_session);

        Assert.Equal([0, 1, 2, 3], runtime.Requests);
        Assert.Equal(4, await CallCountAsync());
        Assert.Equal(4L, await OutputsAsync());
        Assert.Equal(4L, await Db.ScalarAsync<long>("""SELECT count(*) FROM "DerivedBatches" """));
        Assert.Equal(outputs, (await OutputSnapshotAsync()).Take(outputs.Count).ToList());
        Assert.Equal("""["u0"]""", await Db.ScalarAsync<string>($"""SELECT "CoveredIdsJson"::text FROM "WorkOutputs" WHERE "BatchId" = '{BatchId(0)}'"""));
    }

    [Fact]
    public async Task The_join_is_published_only_after_both_parents_are_committed_and_keeps_the_same_identity_after_a_crash_between_them()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        await using (var first = NewProcess(runtime, hook: (step, ordinal) => step == "persist.after" && ordinal == 1 ? throw new CrashException() : ValueTask.CompletedTask))
        {
            await CrashedAsync(first.RunAsync(_session));
        }

        // Ambedue i genitori sono Committed solo ora: il join non esiste ancora, e prepararlo con un genitore mancante non pubblica nulla.
        Assert.Equal(0L, await Db.ScalarAsync<long>($"""SELECT count(*) FROM "DerivedBatches" WHERE "BatchId" = '{BatchId(2)}'"""));
        await TakeOverAsync(6, "worker-b");
        await using (var probe = NewProcess(runtime, fence: 6, lease: "worker-b"))
        {
            await probe.Real.LoadAsync(Ct);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await probe.Real.PrepareAsync(5, Ct));   // ordinale senza batch
        }

        await using var second = NewProcess(runtime, fence: 6, lease: "worker-b");
        await second.RunAsync(_session);

        var (o0, o1) = (await OutputHashAsync(0), await OutputHashAsync(1));
        Assert.Equal(JsonSerializer.Serialize(new[] { o0, o1 }), await ParentsAsync(2));
        Assert.Equal(FakePlan.JoinInput(o0, o1), await Db.ScalarAsync<string>($"""SELECT "InputHash" FROM "DerivedBatches" WHERE "BatchId" = '{BatchId(2)}'"""));
        Assert.Equal(1L, await Db.ScalarAsync<long>($"""SELECT count(*) FROM "DerivedBatches" WHERE "BatchId" = '{BatchId(2)}'"""));
    }

    [Fact]
    public async Task Join_preparation_with_a_missing_parent_output_fails_without_publishing_or_advancing()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        await using var process = NewProcess(runtime);
        await process.Real.LoadAsync(Ct);
        await process.Real.PrepareAsync(0, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await process.Real.PrepareAsync(2, Ct));

        Assert.Equal(0L, await Db.ScalarAsync<long>($"""SELECT count(*) FROM "DerivedBatches" WHERE "BatchId" = '{BatchId(2)}'"""));
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task A_worker_that_lost_the_lease_writes_no_output_child_or_checkpoint_and_the_new_owner_reuses_only_what_was_committed()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        var checkpointsAtTakeover = -1L;
        runtime.AfterAnswer = batch =>
        {
            if (batch == 1)
            {
                // Il nuovo owner prende il lease (fence 6) mentre il vecchio worker sta ancora ricevendo la risposta.
                TakeOverAsync(6, "worker-b").GetAwaiter().GetResult();
                checkpointsAtTakeover = Db.ScalarAsync<long>($"""SELECT count(*) FROM "WorkflowCheckpoints" WHERE "DocumentJobId" = '{_seed.Job}'""").GetAwaiter().GetResult();
            }

            return null;
        };
        await using var stale = NewProcess(runtime);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => stale.RunAsync(_session));

        Assert.True(IsLeaseLost(failure), failure.ToString());
        Assert.Equal(1L, await OutputsAsync());                                        // niente output del batch 1
        Assert.Equal(0L, await Db.ScalarAsync<long>($"""SELECT count(*) FROM "WorkOutputs" WHERE "BatchId" = '{BatchId(1)}'"""));
        Assert.Equal(checkpointsAtTakeover, await Db.ScalarAsync<long>($"""SELECT count(*) FROM "WorkflowCheckpoints" WHERE "DocumentJobId" = '{_seed.Job}'"""));
        Assert.Equal(2, await CallCountAsync());

        // Anche i tentativi diretti del worker stale sono rifiutati: nessuna riserva, nessun invio, nessun output.
        var batch = new BatchRef(1, BatchId(1), FakePlan.LeafInput(1));
        await Assert.ThrowsAnyAsync<LeaseLostException>(async () => await stale.Real.ReserveCallAsync(batch, Ct));
        await Assert.ThrowsAnyAsync<LeaseLostException>(async () => await stale.Real.PersistAsync(batch, """{"batch":1}""", Ct));
        Assert.Equal(2, await CallCountAsync());
        Assert.Equal([0, 1], runtime.Requests);
        Assert.Equal(1L, await OutputsAsync());

        runtime.AfterAnswer = null;
        await using var owner = NewProcess(runtime, fence: 6, lease: "worker-b");
        await owner.RunAsync(_session);

        Assert.Equal([0, 1, 1, 2, 3], runtime.Requests);                               // il batch 1 non committato è reinferito
        Assert.Equal(4L, await OutputsAsync());
        Assert.Equal(5L, await Db.ScalarAsync<long>($"""SELECT "CommittedFence" FROM "WorkOutputs" WHERE "BatchId" = '{BatchId(0)}'"""));   // il Committed valido resta del vecchio fence
        Assert.Equal(6L, await Db.ScalarAsync<long>($"""SELECT "CommittedFence" FROM "WorkOutputs" WHERE "BatchId" = '{BatchId(1)}'"""));
    }

    [Fact]
    public async Task An_expired_lease_with_the_same_fence_is_refused_before_the_network()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        await using var process = NewProcess(runtime);
        await process.Real.LoadAsync(Ct);
        var batch = await process.Real.PrepareAsync(0, Ct);
        await Db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "LeaseUntil" = now() - interval '1 second' WHERE "Id" = '{_seed.Job}'""");

        await Assert.ThrowsAnyAsync<LeaseLostException>(async () => await process.Real.ReserveCallAsync(batch, Ct));

        Assert.Equal(0, await CallCountAsync());
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task Without_any_checkpoint_the_same_topology_restarts_from_the_entry_and_the_guards_recover_the_committed_outputs()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        await using (var first = NewProcess(runtime, hook: (step, ordinal) => step == "persist.after" && ordinal == 1 ? throw new CrashException() : ValueTask.CompletedTask))
        {
            await CrashedAsync(first.RunAsync(_session));
        }

        var manifest = await Db.ScalarAsync<string>("""SELECT "Hash" FROM "WorkManifests" """);
        var outputs = await OutputSnapshotAsync();
        var calls = await CallCountAsync();
        await TakeOverAsync(6, "worker-b");
        await using var second = NewProcess(runtime, fence: 6, lease: "worker-b");

        var result = await second.RunAsync(_session + "-fresh");    // sessione senza alcun checkpoint: ingresso del grafo

        Assert.Equal(new BatchRunResult(4, 2), result);
        Assert.Equal([0, 1, 2, 3], runtime.Requests);
        Assert.Equal(calls + 2, await CallCountAsync());             // contatore non azzerato
        Assert.Equal(manifest, await Db.ScalarAsync<string>("""SELECT "Hash" FROM "WorkManifests" """));
        Assert.Equal(1L, await Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkManifests" """));
        Assert.Equal(outputs, (await OutputSnapshotAsync()).Take(outputs.Count).ToList());
    }

    [Fact]
    public async Task A_checkpoint_far_behind_the_domain_is_not_authoritative_and_only_missing_batches_are_inferred()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        await using (var first = NewProcess(runtime, hook: (step, ordinal) => step == "reserve.before" && ordinal == 2 ? throw new CrashException() : ValueTask.CompletedTask))
        {
            await CrashedAsync(first.RunAsync(_session));
        }

        var checkpoints = await CheckpointIndexAsync();
        var calls = await CallCountAsync();
        await TakeOverAsync(6, "worker-b");
        await using var second = NewProcess(runtime, fence: 6, lease: "worker-b");

        var result = await second.RunAsync(_session, checkpoints[0]);    // primo checkpoint: dietro di due batch Committed

        Assert.Equal([0, 1, 2, 3], runtime.Requests);
        Assert.Equal(2, result.Invocations);
        Assert.Equal(calls + 2, await CallCountAsync());
        Assert.Equal(4L, await OutputsAsync());
    }

    [Fact]
    public async Task A_corrupt_checkpoint_blob_neither_advances_nor_infers_and_a_foreign_definition_is_incompatible()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        await using (var first = NewProcess(runtime, hook: (step, ordinal) => step == "persist.after" && ordinal == 1 ? throw new CrashException() : ValueTask.CompletedTask))
        {
            await CrashedAsync(first.RunAsync(_session));
        }

        var outputs = await OutputSnapshotAsync();
        var calls = await CallCountAsync();
        await TakeOverAsync(6, "worker-b");

        await using (var foreign = NewProcess(runtime, fence: 6, lease: "worker-b", definition: "doc-batch-2"))
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => foreign.RunAsync(_session));
            Assert.True(Chain(failure).Any(e => e is CheckpointIncompatibleException), failure.ToString());
        }

        await Db.ExecuteAsync($"""
            UPDATE "WorkflowCheckpoints" SET "Payload" = '"broken"'::jsonb
            WHERE "DocumentJobId" = '{_seed.Job}' AND "CommitSequence" = (SELECT max("CommitSequence") FROM "WorkflowCheckpoints" WHERE "DocumentJobId" = '{_seed.Job}')
            """);
        await using (var corrupt = NewProcess(runtime, fence: 6, lease: "worker-b"))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => corrupt.RunAsync(_session));
        }

        Assert.Equal([0, 1], runtime.Requests);          // nessuna inferenza nascosta
        Assert.Equal(calls, await CallCountAsync());
        Assert.Equal(outputs, await OutputSnapshotAsync());
    }

    [Fact]
    public async Task Domain_Completed_before_the_final_checkpoint_prevails_and_a_resume_does_not_infer_or_alter_the_result()
    {
        TestEnvironment.RequirePostgres();
        var runtime = new FakeModel();
        await using (var first = NewProcess(runtime, completeOnCommit: true, hook: (step, _) => step == "commit.after" ? throw new CrashException() : ValueTask.CompletedTask))
        {
            await CrashedAsync(first.RunAsync(_session));
        }

        Assert.Equal("Completed", await Db.ScalarAsync<string>($"""SELECT "Status" FROM "DocumentJobs" WHERE "Id" = '{_seed.Job}'"""));
        var result = await Db.ScalarAsync<string>($"""SELECT "ResultHash" FROM "DocumentJobs" WHERE "Id" = '{_seed.Job}'""");
        var calls = await CallCountAsync();
        var outputs = await OutputSnapshotAsync();

        await using var second = NewProcess(runtime, fence: 5, completeOnCommit: true);
        // Un job Completed non è più scrivibile: la ripresa non può né inferire né pubblicare un secondo stato finale.
        // (Il «leggi il Completed e non riprendere» è del dispatcher, T049; qui si prova che i dati restano intatti.)
        try
        {
            await second.RunAsync(_session);
        }
        catch (Exception ex)
        {
            Assert.True(IsLeaseLost(ex), ex.ToString());
        }

        Assert.Equal([0, 1, 2, 3], runtime.Requests);
        Assert.Equal(calls, await CallCountAsync());
        Assert.Equal(outputs, await OutputSnapshotAsync());
        Assert.Equal("Completed", await Db.ScalarAsync<string>($"""SELECT "Status" FROM "DocumentJobs" WHERE "Id" = '{_seed.Job}'"""));
        Assert.Equal(result, await Db.ScalarAsync<string>($"""SELECT "ResultHash" FROM "DocumentJobs" WHERE "Id" = '{_seed.Job}'"""));
    }

    // ------------------------------------------------------------------ infrastruttura

    private sealed class CrashException : Exception
    {
        public CrashException() : base("crash simulato")
        {
        }
    }

    private static IEnumerable<Exception> Chain(Exception e)
    {
        for (var current = e; current is not null; current = current.InnerException!)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain))
                {
                    yield return inner;
                }
            }

            if (current.InnerException is null)
            {
                yield break;
            }
        }
    }

    private static bool IsLeaseLost(Exception e) => Chain(e).Any(x => x is LeaseLostException);

    private static async Task CrashedAsync(Task<BatchRunResult> run)
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => run);
        Assert.True(Chain(failure).Any(e => e is CrashException), $"atteso il crash simulato, ottenuto: {failure}");
    }

    private Process NewProcess(
        FakeModel model, long fence = 5, string lease = "worker-a", string definition = Definition, bool completeOnCommit = false,
        Func<string, int, ValueTask>? hook = null) =>
        new(Db, _seed, model, fence, lease, definition, completeOnCommit, hook);

    private async Task TakeOverAsync(long fence, string lease) =>
        await Db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "LeaseOwner" = '{lease}', "FencingToken" = {fence}, "LeaseUntil" = now() + interval '30 minutes' WHERE "Id" = '{_seed.Job}'""");

    private Task<long> CurrentFenceAsync() => Db.ScalarAsync<long>($"""SELECT "FencingToken" FROM "DocumentJobs" WHERE "Id" = '{_seed.Job}'""");

    private async Task<long> NextFenceAsync() => await CurrentFenceAsync() + 1;

    private Task<int> CallCountAsync() => Db.ScalarAsync<int>($"""SELECT "CallCount" FROM "DocumentPreparations" WHERE "Id" = '{_seed.Preparation}'""");

    private Task<long> OutputsAsync() => Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """);

    private static string BatchId(int ordinal) => FakePlan.BatchIdOf(ordinal);

    private Task<string> OutputHashAsync(int ordinal) => Db.ScalarAsync<string>($"""SELECT "OutputHash" FROM "WorkOutputs" WHERE "BatchId" = '{BatchId(ordinal)}'""");

    private Task<string> ParentsAsync(int ordinal) => Db.ScalarAsync<string>($"""SELECT "ParentOutputHashes"::text FROM "DerivedBatches" WHERE "BatchId" = '{BatchId(ordinal)}'""")
        .ContinueWith(t => JsonSerializer.Serialize(JsonSerializer.Deserialize<string[]>(t.Result)), Ct);

    /// <summary>Id, batch, hash e fence di ogni output, in ordine di batch: l'identità che il recovery non deve cambiare.</summary>
    private async Task<List<string>> OutputSnapshotAsync()
    {
        var rows = new List<string>();
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""SELECT "Id","BatchId","InputHash","OutputHash","CommittedFence","CoveredIdsJson"::text FROM "WorkOutputs" ORDER BY "BatchId" """, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(string.Join('|', Enumerable.Range(0, 6).Select(i => reader.GetValue(i).ToString())));
        }

        return rows.OrderBy(r => r.Split('|')[1] == BatchId(0) ? 0 : r.Split('|')[1] == BatchId(1) ? 1 : 2).ThenBy(r => r).ToList();
    }

    private async Task<List<string>> BatchSnapshotAsync()
    {
        var rows = new List<string>();
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""SELECT "Id","BatchId","InputHash","ParentOutputHashes"::text,"Ordinal" FROM "DerivedBatches" ORDER BY "Ordinal" """, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(string.Join('|', Enumerable.Range(0, 5).Select(i => reader.GetValue(i).ToString())));
        }

        return rows;
    }

    private async Task<List<CheckpointInfo>> CheckpointIndexAsync()
    {
        await using var context = Db.NewContext();
        var writer = CheckpointWriter.ForDocumentJob(_seed.Job, "reader", 0, Definition);
        var store = new PostgresCheckpointStore(context, writer);
        return (await store.RetrieveIndexAsync(_session)).ToList();
    }

    /// <summary>Un «processo»: contesto EF, store, factory sul modello finto, port di produzione e grafo nuovi. Si condivide solo il database.</summary>
    private sealed class Process : IAsyncDisposable
    {
        private readonly AppDbContext _context;
        private readonly InternalModelClient _client;
        private readonly PostgresCheckpointStore _checkpointStore;

        public Process(ParsingDatabase db, SeededParse seed, FakeModel model, long fence, string lease, string definition, bool completeOnCommit, Func<string, int, ValueTask>? hook)
        {
            _context = db.NewContext();
            var writer = CheckpointWriter.ForDocumentJob(seed.Job, lease, fence, definition);
            var options = Options();
            _client = new InternalModelClient(options, model);
            var monitor = new ModelRuntimeMonitor(_client, options, NullLogger<ModelRuntimeMonitor>.Instance);
            var factory = new AgentFactory(options, _client.ChatClient, (text, ct) => new(_client.CountTokensAsync(text, ct)), null, monitor, _client);
            var plan = new FakePlan(db.ConnectionString, seed.Job, definition, completeOnCommit, _client);
            var store = new DocumentWorkStore(_context, writer);
            Real = new DocumentBatchPort(store, _context, writer, factory, plan, new RootCallCounterReservation(_context, writer));
            Port = new FaultPort(Real, hook ?? ((_, _) => ValueTask.CompletedTask));
            _checkpointStore = new PostgresCheckpointStore(_context, writer);
            Checkpoints = CheckpointManager.CreateJson(_checkpointStore);
        }

        public DocumentBatchPort Real { get; }

        public IDocumentBatchPort Port { get; }

        public CheckpointManager Checkpoints { get; }

        public Task<BatchRunResult> RunAsync(string session, CheckpointInfo? from = null, Action<WorkflowEvent>? onEvent = null) =>
            DocumentBatchWorkflow.RunAsync(DocumentBatchWorkflow.Build(Port, new BatchInvocationCounter()), Checkpoints, session, onEvent, Ct, from);

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            return _context.DisposeAsync();
        }
    }

    private static AiOptions Options() => new()
    {
        Endpoint = "http://dgx.internal:8080/v1/",
        ModelId = "Qwen-A",
        ApiKey = "test-key",
        ContextWindowTokens = 2000,
        MaxOutputTokens = 100,
        SafetyMarginTokens = 20,
        RuntimeCheckSeconds = 60,
    };

    /// <summary>Decorator di fault injection ai confini del port: <c>{op}.before</c>/<c>{op}.after</c> con l'ordinale (o -1).</summary>
    private sealed class FaultPort(IDocumentBatchPort inner, Func<string, int, ValueTask> hook) : IDocumentBatchPort
    {
        public async ValueTask<int> LoadAsync(CancellationToken ct)
        {
            await hook("load.before", -1);
            var r = await inner.LoadAsync(ct);
            await hook("load.after", -1);
            return r;
        }

        public async ValueTask<BatchRef> PrepareAsync(int cursor, CancellationToken ct)
        {
            await hook("prepare.before", cursor);
            var r = await inner.PrepareAsync(cursor, ct);
            await hook("prepare.after", cursor);
            return r;
        }

        public async ValueTask<bool> LookupAsync(BatchRef batch, CancellationToken ct)
        {
            await hook("lookup.before", batch.Ordinal);
            var r = await inner.LookupAsync(batch, ct);
            await hook("lookup.after", batch.Ordinal);
            return r;
        }

        public async ValueTask ReserveCallAsync(BatchRef batch, CancellationToken ct)
        {
            await hook("reserve.before", batch.Ordinal);
            await inner.ReserveCallAsync(batch, ct);
            await hook("reserve.after", batch.Ordinal);
        }

        public Microsoft.Agents.AI.ChatClientAgent CreateAgent(BatchRef batch) => inner.CreateAgent(batch);

        public ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, CancellationToken ct) => inner.ComposeAsync(batch, ct);

        public async ValueTask PersistAsync(BatchRef batch, string responseText, CancellationToken ct)
        {
            await hook("persist.before", batch.Ordinal);
            await inner.PersistAsync(batch, responseText, ct);
            await hook("persist.after", batch.Ordinal);
        }

        public async ValueTask<bool> HasMoreAsync(int nextCursor, CancellationToken ct)
        {
            await hook("hasmore.before", nextCursor);
            var r = await inner.HasMoreAsync(nextCursor, ct);
            await hook("hasmore.after", nextCursor);
            return r;
        }

        public async ValueTask AssembleAsync(CancellationToken ct)
        {
            await hook("assemble.before", -1);
            await inner.AssembleAsync(ct);
            await hook("assemble.after", -1);
        }

        public async ValueTask<int> CommitAsync(CancellationToken ct)
        {
            await hook("commit.before", -1);
            var r = await inner.CommitAsync(ct);
            await hook("commit.after", -1);
            return r;
        }
    }

    /// <summary>
    /// Piano di prova: 0 e 1 leaf; 2 join dei loro output (genitori ordinati); 3 figlio di 0, pubblicato nel commit dell'output 0.
    /// Identità deterministiche: nessun Guid nel replay.
    /// </summary>
    private sealed class FakePlan(string connectionString, Guid job, string definition, bool completeOnCommit, InternalModelClient client) : IDocumentBatchPlan
    {
        public static string BatchIdOf(int ordinal) => Sha($"batch|{ordinal}");

        public static string LeafInput(int ordinal) => Sha($"input|{ordinal}");

        public static string JoinInput(string p0, string p1) => Sha($"input|2|{p0}|{p1}");

        private static string ChildInput(string parent) => Sha($"input|3|{parent}");

        public WorkManifestInput Manifest => new(H('a'), H('b'), "fp-1", definition, "alg-1", H('d'), """{"nodes":["Load","PrepareBatch","LookupOutput"]}""");

        public WorkLookupExpectation Expectation => new(Schema, Validation);

        public BatchAgentSpec Agent => new("BatchAgent", "batch-agent", "Istruzioni della fase batch.", client.CreateChatOptions(phase: AiPhase.Document), AiPhase.Document);

        public bool HasBatch(int ordinal) => ordinal is >= 0 and <= 3;

        private static DerivedBatchInput Make(int ordinal, string input, params string[] parents) =>
            new(BatchIdOf(ordinal), "extract", null, 0, 0, ordinal, parents, $$"""{"ordinal":{{ordinal}}}""", 20, "measured", input, Schema, "p1");

        public async ValueTask<DerivedBatchInput> PrepareAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
        {
            switch (ordinal)
            {
                case 0 or 1:
                    return Make(ordinal, LeafInput(ordinal));
                case 2:
                {
                    var p0 = await prior.GetByOrdinalAsync(0, ct) ?? throw new InvalidOperationException("Genitore 0 non committato: il join non si pubblica.");
                    var p1 = await prior.GetByOrdinalAsync(1, ct) ?? throw new InvalidOperationException("Genitore 1 non committato: il join non si pubblica.");
                    return Make(2, JoinInput(p0.OutputHash, p1.OutputHash), p0.OutputHash, p1.OutputHash);
                }

                case 3:
                {
                    var p0 = await prior.GetByOrdinalAsync(0, ct) ?? throw new InvalidOperationException("Genitore 0 non committato.");
                    return Make(3, ChildInput(p0.OutputHash), p0.OutputHash);
                }

                default:
                    throw new InvalidOperationException($"Nessun batch con ordinale {ordinal}.");
            }
        }

        public async ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, IBatchOutputs prior, CancellationToken ct)
        {
            var context = "";
            if (batch.Ordinal == 2)
            {
                context = string.Join(' ', (await prior.GetByOrdinalAsync(0, ct))!.PayloadJson, (await prior.GetByOrdinalAsync(1, ct))!.PayloadJson);
            }

            return [new ChatMessage(ChatRole.User, $"BATCH-{batch.Ordinal}| contenuto del batch {context}")];
        }

        public ValidatedBatch Validate(BatchRef batch, string responseText)
        {
            using var json = JsonDocument.Parse(responseText);
            if (json.RootElement.GetProperty("batch").GetInt32() != batch.Ordinal)
            {
                throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Output non valido: batch diverso da quello richiesto.");
            }

            var output = new WorkOutputInput(
                batch.BatchId, "InvokeBatch", batch.InputHash, CanonicalJson.Sha256Hex(responseText), responseText, null, null,
                $$"""["u{{batch.Ordinal}}"]""", null, null, Schema, Validation, null);
            return new ValidatedBatch(output, batch.Ordinal == 0 ? [Make(3, ChildInput(output.OutputHash), output.OutputHash)] : null);
        }

        public async ValueTask AssembleAsync(IBatchOutputs outputs, CancellationToken ct)
        {
            for (var i = 0; i < 4; i++)
            {
                _ = await outputs.GetByOrdinalAsync(i, ct) ?? throw new InvalidOperationException($"Assemble senza l'output {i}.");
            }
        }

        public async ValueTask<int> CommitAsync(IBatchOutputs outputs, CancellationToken ct)
        {
            if (completeOnCommit)
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(ct);
                await using var command = new NpgsqlCommand(
                    """UPDATE "DocumentJobs" SET "Status"='Completed',"ResultHash"=@h,"CompletedAt"=now() WHERE "Id"=@id AND "Status"='Running'""", connection);
                command.Parameters.AddWithValue("h", H('9'));
                command.Parameters.AddWithValue("id", job);
                await command.ExecuteNonQueryAsync(ct);
            }

            return 4;
        }
    }

    /// <summary>Modello finto a livello HTTP (llama.cpp simulato): registra i batch realmente richiesti e permette di iniettare guasti.</summary>
    private sealed class FakeModel : HttpMessageHandler
    {
        private static readonly Regex Marker = new(@"BATCH-(\d+)\|", RegexOptions.Compiled);

        public List<int> Requests { get; } = [];

        /// <summary>Chiamato dopo aver prodotto la risposta e prima di consegnarla; può lanciare (processo morto) o agire sul database.</summary>
        public Func<int, string?>? AfterAnswer { get; set; }

        public Func<int, string?>? Responder { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            switch (path)
            {
                case "/v1/models":
                    return Json("""{"object":"list","data":[{"id":"Qwen-A","object":"model","status":{"value":"loaded","args":["llama-server"]}}]}""");
                case "/props":
                    return Json("""{"default_generation_settings":{"n_ctx":1000,"params":{"n_predict":-1}},"total_slots":1}""");
                case "/tokenize":
                {
                    using var doc = JsonDocument.Parse(body);
                    var count = doc.RootElement.GetProperty("content").GetString()!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                    return Json("{\"tokens\":[" + string.Join(',', Enumerable.Range(0, count)) + "]}");
                }

                case "/apply-template":
                {
                    using var doc = JsonDocument.Parse(body);
                    var prompt = string.Join('\n', doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => $"[{m.GetProperty("role").GetString()}] {m.GetProperty("content").GetString()}"));
                    return Json(JsonSerializer.Serialize(new { prompt }));
                }

                case "/v1/chat/completions":
                {
                    var batch = int.Parse(Marker.Match(body).Groups[1].Value);
                    lock (Requests)
                    {
                        Requests.Add(batch);
                    }

                    var content = Responder?.Invoke(batch) ?? $$"""{"batch":{{batch}}}""";
                    AfterAnswer?.Invoke(batch);
                    return Json(JsonSerializer.Serialize(new
                    {
                        id = "x", @object = "chat.completion", created = 1, model = "Qwen-A",
                        choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
                        usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
                    }));
                }

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
