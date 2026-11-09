using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T100 (MAF-PG) — recovery complessivo cross-root sul port di produzione (<see cref="DocumentBatchPort"/>): PostgreSQL reale, SDK MAF 1.26.0 reale,
/// modello FINTO a livello HTTP (nessun DGX). Retry PM verso un nuovo root con RetryOfId, compatibile e incompatibile (runtime, parser, budget), ripresa del
/// nuovo root da processo nuovo e checkpoint arretrato, invio incerto dopo crash e fence vecchio. Si prova l'unicità degli effetti, non l'exactly-once.
/// Piano: leaf 0 e 1, join 2 = (0,1); gli inputHash non dipendono dal root, i batchId sì (AI §5.2).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentCrossRootRecoveryTests : IAsyncLifetime
{
    private const string Schema = "evidence-1.1.0";
    private const string Validation = "v1";
    private const string Definition = "doc-batch-1";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string H(char c) => new(c, 64);
    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private ParsingDatabase? _database;
    private SeededParse _seed = null!;

    private ParsingDatabase Db => _database ?? throw new InvalidOperationException();

    private sealed record Root(Guid Job, Guid Preparation, string Session);

    private sealed record Variant(string Runtime = "fp-1", char Policy = 'd', char Config = 'a');

    public async ValueTask InitializeAsync()
    {
        if (TestEnvironment.PostgresAdminConnectionString is null)
        {
            return;
        }

        _database = await ParsingDatabase.CreateAsync();
        _seed = await _database.SeedParseAsync();
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

    private Root Old => new(_seed.Job, _seed.Preparation, "sess-" + _seed.Job.ToString("N"));

    /// <summary>Retry PM: nuova Preparation + job collegati al terminale (RetryOfId), in esecuzione con lease e fence propri; il vecchio root diventa Failed.</summary>
    private async Task<Root> AddRetryRootAsync(Guid previousJob, Guid previousPreparation, long fence = 7)
    {
        await Db.ExecuteAsync($"""
            UPDATE "DocumentJobs" SET "Status"='Failed',"ErrorCode"='document_incomplete',"ErrorMessage"='x',"CompletedAt"=now(),"LeaseOwner"=NULL,"LeaseUntil"=NULL WHERE "Id"='{previousJob}';
            UPDATE "DocumentPreparations" SET "Status"='Failed',"ErrorCode"='document_incomplete' WHERE "Id"='{previousPreparation}';
            """);
        var (preparation, job) = (Guid.NewGuid(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var hard = now.AddHours(1);
        await using var db = Db.NewContext();
        var generation = await db.DocumentPreparations.Where(p => p.DocumentVersionId == _seed.Version).MaxAsync(p => p.Generation, Ct) + 1;
        db.DocumentPreparations.Add(new DocumentPreparation
        {
            Id = preparation, ProjectId = _seed.Project, DocumentVersionId = _seed.Version, Generation = generation, Status = PreparationStatus.Queued, RetryOfId = previousPreparation,
            PolicyHash = H('e'), RequestedAt = now, HardDeadlineAt = hard, EffectiveDeadlineAt = hard,
        });
        await db.SaveChangesAsync(Ct);
        db.DocumentJobs.Add(new DocumentJob
        {
            Id = job, ProjectId = _seed.Project, PreparationId = preparation, Kind = DocumentJobKind.Parse, Status = DocumentJobStatus.Queued, RequestKey = Guid.NewGuid(), RetryOfId = previousJob,
            RequestedBy = _seed.User, RequestedAt = now, DeadlineAt = hard, MaxAttempts = 3,
            ConfigJson = """{"policyHash":"x","ParseTimeoutSeconds":300,"MaxPages":1000,"MaxBlocks":100000,"MaxExpandedBytes":250000000,"MaxDerivedBytes":250000000}""",
        });
        await db.SaveChangesAsync(Ct);
        (await db.DocumentPreparations.SingleAsync(p => p.Id == preparation, Ct)).ParseJobId = job;
        await db.SaveChangesAsync(Ct);
        await Db.ExecuteAsync($"""
            UPDATE "DocumentJobs" SET "Status"='Running',"LeaseOwner"='worker-a',"LeaseUntil"=now()+interval '30 minutes',"FencingToken"={fence},"AttemptCount"=1,"StartedAt"=now(),
                   "WorkflowDefinitionVersion"='{Definition}' WHERE "Id"='{job}';
            UPDATE "DocumentPreparations" SET "Status"='Extracting' WHERE "Id"='{preparation}';
            """);
        return new Root(job, preparation, "sess-" + job.ToString("N"));
    }

    // ------------------------------------------------------------------ scenari

    [Fact]
    public async Task A_compatible_retry_root_imports_the_committed_outputs_without_ai_and_infers_only_the_missing_batch_leaving_the_old_root_intact()
    {
        TestEnvironment.RequirePostgres();
        var oldModel = new FakeModel();
        await using (var first = NewProcess(Old, oldModel, hook: (step, ordinal) => step == "reserve.after" && ordinal == 2 ? throw new CrashException() : ValueTask.CompletedTask))
        {
            await CrashedAsync(first.RunAsync(Old.Session));
        }

        Assert.Equal([0, 1], oldModel.Requests);   // solo le foglie hanno raggiunto la rete: il join ha riservato e poi il processo è morto
        var oldOutputs = await OutputSnapshotAsync(Old.Job);
        Assert.Equal(2, oldOutputs.Count);
        var oldCalls = await CallCountAsync(Old.Preparation);
        var oldDeadline = await DeadlineAsync(Old.Preparation);
        var retry = await AddRetryRootAsync(Old.Job, Old.Preparation);
        var deadlineBefore = await DeadlineAsync(retry.Preparation);

        var model = new FakeModel();
        await using var process = NewProcess(retry, model);
        var result = await process.RunAsync(retry.Session);

        Assert.Equal(new BatchRunResult(3, 1), result);
        Assert.Equal([2], model.Requests);                                   // soltanto il batch senza output compatibile arriva alla rete
        Assert.Equal(1, await CallCountAsync(retry.Preparation));            // nessuna riserva per i batch importati
        Assert.Equal(deadlineBefore, await DeadlineAsync(retry.Preparation));   // deadline del nuovo root non toccata dall'import
        var derived = await DerivedAsync(retry.Job);
        Assert.Equal((await DerivedAsync(Old.Job)).Select(o => o.OutputHash).OrderBy(x => x), derived.Where(d => d.Derived is not null).Select(d => d.Derived!).OrderBy(x => x));
        Assert.All(derived.Where(d => d.Derived is not null), d => Assert.Equal(d.Derived, d.OutputHash));   // payload/hash riusati esattamente
        Assert.Single(derived, d => d.Derived is null);                      // il join è prodotto qui
        Assert.Equal(await InputOfAsync(Old.Job, 2), await InputOfAsync(retry.Job, 2));   // stesso join perché stessi hash dei genitori
        Assert.Equal(oldOutputs, await OutputSnapshotAsync(Old.Job));        // ID/hash/fence del vecchio root immutati
        Assert.Equal(oldCalls, await CallCountAsync(Old.Preparation));
        Assert.Equal(oldDeadline, await DeadlineAsync(Old.Preparation));
        Assert.Equal(3L, await Db.ScalarAsync<long>($"""SELECT count(*) FROM "WorkOutputs" WHERE "OwnerId" = '{retry.Job}'"""));
    }

    [Fact]
    public async Task A_completed_old_workload_is_fully_reused_by_a_compatible_retry_with_zero_requests_and_zero_reservations()
    {
        TestEnvironment.RequirePostgres();
        await using (var first = NewProcess(Old, new FakeModel()))
        {
            await first.RunAsync(Old.Session);
        }

        var oldOutputs = await OutputSnapshotAsync(Old.Job);
        Assert.Equal(3, oldOutputs.Count);
        var retry = await AddRetryRootAsync(Old.Job, Old.Preparation);
        var model = new FakeModel();

        await using var process = NewProcess(retry, model);
        var result = await process.RunAsync(retry.Session);

        Assert.Equal(new BatchRunResult(3, 0), result);
        Assert.Empty(model.Requests);
        Assert.Equal(0, await CallCountAsync(retry.Preparation));
        var derived = await DerivedAsync(retry.Job);
        Assert.Equal(3, derived.Count(d => d.Derived is not null));
        Assert.Equal(oldOutputs, await OutputSnapshotAsync(Old.Job));
    }

    [Theory]
    [InlineData("runtime")]   // modello/runtime diverso
    [InlineData("parser")]    // parser/config diverso
    [InlineData("budget")]    // budget/policy diverso
    public async Task An_incompatible_retry_root_reuses_nothing_infers_everything_and_never_overwrites_the_old_outputs(string change)
    {
        TestEnvironment.RequirePostgres();
        await using (var first = NewProcess(Old, new FakeModel()))
        {
            await first.RunAsync(Old.Session);
        }

        var oldOutputs = await OutputSnapshotAsync(Old.Job);
        var retry = await AddRetryRootAsync(Old.Job, Old.Preparation);
        var variant = change switch { "runtime" => new Variant(Runtime: "fp-2"), "parser" => new Variant(Config: 'c'), _ => new Variant(Policy: 'f') };
        var model = new FakeModel();

        await using var process = NewProcess(retry, model, variant: variant);
        var result = await process.RunAsync(retry.Session);

        Assert.Equal(new BatchRunResult(3, 3), result);
        Assert.Equal([0, 1, 2], model.Requests);
        Assert.Equal(3, await CallCountAsync(retry.Preparation));
        Assert.All(await DerivedAsync(retry.Job), d => Assert.Null(d.Derived));
        Assert.Equal(oldOutputs, await OutputSnapshotAsync(Old.Job));
    }

    [Fact]
    public async Task A_new_process_resuming_the_retry_root_from_a_stale_checkpoint_keeps_the_imports_the_consumed_uncertain_send_and_the_deadline()
    {
        TestEnvironment.RequirePostgres();
        await using (var first = NewProcess(Old, new FakeModel(), hook: (step, ordinal) => step == "reserve.before" && ordinal == 2 ? throw new CrashException() : ValueTask.CompletedTask))
        {
            await CrashedAsync(first.RunAsync(Old.Session));
        }

        var retry = await AddRetryRootAsync(Old.Job, Old.Preparation);
        var deadlineBefore = await DeadlineAsync(retry.Preparation);
        var model = new FakeModel();
        await using (var second = NewProcess(retry, model, hook: (step, ordinal) => step == "reserve.after" && ordinal == 2 ? throw new CrashException() : ValueTask.CompletedTask))
        {
            await CrashedAsync(second.RunAsync(retry.Session));   // invio incerto: riserva consumata, rete non raggiunta
        }

        Assert.Empty(model.Requests);
        Assert.Equal(1, await CallCountAsync(retry.Preparation));
        var imported = await OutputSnapshotAsync(retry.Job);
        Assert.Equal(2, imported.Count);
        var checkpoints = await CheckpointIndexAsync(retry);

        await TakeOverAsync(retry.Job, 8, "worker-b");
        await using var third = NewProcess(retry, model, fence: 8, lease: "worker-b");
        var result = await third.RunAsync(retry.Session, checkpoints[0]);    // checkpoint arretrato rispetto ai batch già importati

        Assert.Equal(1, result.Invocations);
        Assert.Equal([2], model.Requests);                                   // gli import non si ripetono né si reinferiscono
        Assert.Equal(2, await CallCountAsync(retry.Preparation));            // riserva consumata conservata + quella dell'invio effettivo
        Assert.Equal(imported, (await OutputSnapshotAsync(retry.Job)).Take(2).ToList());   // stessi ID/hash/fence di import
        Assert.Equal(3L, await Db.ScalarAsync<long>($"""SELECT count(*) FROM "WorkOutputs" WHERE "OwnerId" = '{retry.Job}'"""));
        Assert.Equal(deadlineBefore, await DeadlineAsync(retry.Preparation));
    }

    [Fact]
    public async Task A_stale_fence_on_the_retry_root_imports_and_publishes_nothing_and_the_new_owner_then_reuses_the_old_outputs()
    {
        TestEnvironment.RequirePostgres();
        await using (var first = NewProcess(Old, new FakeModel()))
        {
            await first.RunAsync(Old.Session);
        }

        var oldOutputs = await OutputSnapshotAsync(Old.Job);
        var retry = await AddRetryRootAsync(Old.Job, Old.Preparation);
        await TakeOverAsync(retry.Job, 8, "worker-b");
        var model = new FakeModel();

        await using (var stale = NewProcess(retry, model))   // fence 5 del vecchio worker: il lease appartiene ormai a worker-b/fence 8
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => stale.RunAsync(retry.Session));
            Assert.True(Chain(failure).Any(e => e is LeaseLostException), failure.ToString());
        }

        Assert.Empty(model.Requests);
        Assert.Equal(0L, await Db.ScalarAsync<long>($"""SELECT count(*) FROM "WorkOutputs" WHERE "OwnerId" = '{retry.Job}'"""));
        Assert.Equal(0, await CallCountAsync(retry.Preparation));

        await using var owner = NewProcess(retry, model, fence: 8, lease: "worker-b");
        Assert.Equal(new BatchRunResult(3, 0), await owner.RunAsync(retry.Session));
        Assert.Empty(model.Requests);
        Assert.Equal(oldOutputs, await OutputSnapshotAsync(Old.Job));
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

    private static async Task CrashedAsync(Task<BatchRunResult> run)
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => run);
        Assert.True(Chain(failure).Any(e => e is CrashException), $"atteso il crash simulato, ottenuto: {failure}");
    }

    private Process NewProcess(Root root, FakeModel model, long? fence = null, string lease = "worker-a", Variant? variant = null, Func<string, int, ValueTask>? hook = null) =>
        new(Db, root.Job, model, fence ?? (root.Job == _seed.Job ? 5 : 7), lease, variant ?? new Variant(), hook);

    private async Task TakeOverAsync(Guid job, long fence, string lease) =>
        await Db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "LeaseOwner" = '{lease}', "FencingToken" = {fence}, "LeaseUntil" = now() + interval '30 minutes' WHERE "Id" = '{job}'""");

    private Task<int> CallCountAsync(Guid preparation) => Db.ScalarAsync<int>($"""SELECT "CallCount" FROM "DocumentPreparations" WHERE "Id" = '{preparation}'""");

    private Task<string> DeadlineAsync(Guid preparation) =>
        Db.ScalarAsync<string>($"""SELECT "HardDeadlineAt"::text || '|' || "EffectiveDeadlineAt"::text FROM "DocumentPreparations" WHERE "Id" = '{preparation}'""");

    private Task<string> InputOfAsync(Guid job, int ordinal) =>
        Db.ScalarAsync<string>($"""SELECT "InputHash" FROM "DerivedBatches" WHERE "OwnerId" = '{job}' AND "Ordinal" = {ordinal}""");

    /// <summary>Id, input, hash, fence e payload di ogni output del root, ordinati per ordinale del batch: l'identità che il recovery non deve cambiare.</summary>
    private async Task<List<string>> OutputSnapshotAsync(Guid job) => await RowsAsync(
        $"""
        SELECT o."Id" || '|' || o."InputHash" || '|' || o."OutputHash" || '|' || o."CommittedFence" || '|' || coalesce(o."DerivedFromOutputHash", '-') || '|' || o."PayloadJson"::text
        FROM "WorkOutputs" o JOIN "DerivedBatches" b ON b."OwnerKind" = o."OwnerKind" AND b."OwnerId" = o."OwnerId" AND b."BatchId" = o."BatchId"
        WHERE o."OwnerId" = '{job}' ORDER BY b."Ordinal"
        """);

    private async Task<List<(string OutputHash, string? Derived)>> DerivedAsync(Guid job)
    {
        var rows = await RowsAsync(
            $"""SELECT o."OutputHash" || '|' || coalesce(o."DerivedFromOutputHash", '') FROM "WorkOutputs" o JOIN "DerivedBatches" b ON b."OwnerId" = o."OwnerId" AND b."BatchId" = o."BatchId" WHERE o."OwnerId" = '{job}' ORDER BY b."Ordinal" """);
        return rows.Select(r => r.Split('|')).Select(p => (p[0], p[1].Length == 0 ? null : p[1])).ToList();
    }

    private async Task<List<string>> RowsAsync(string sql)
    {
        var rows = new List<string>();
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<List<CheckpointInfo>> CheckpointIndexAsync(Root root)
    {
        await using var context = Db.NewContext();
        var store = new PostgresCheckpointStore(context, CheckpointWriter.ForDocumentJob(root.Job, "reader", 0, Definition));
        return (await store.RetrieveIndexAsync(root.Session)).ToList();
    }

    private sealed class Process : IAsyncDisposable
    {
        private readonly AppDbContext _context;
        private readonly InternalModelClient _client;

        public Process(ParsingDatabase db, Guid job, FakeModel model, long fence, string lease, Variant variant, Func<string, int, ValueTask>? hook)
        {
            _context = db.NewContext();
            var writer = CheckpointWriter.ForDocumentJob(job, lease, fence, Definition);
            var options = Options();
            _client = new InternalModelClient(options, model);
            var monitor = new ModelRuntimeMonitor(_client, options, NullLogger<ModelRuntimeMonitor>.Instance);
            var factory = new AgentFactory(options, _client.ChatClient, (text, ct) => new(_client.CountTokensAsync(text, ct)), null, monitor, _client);
            var real = new DocumentBatchPort(new DocumentWorkStore(_context, writer), _context, writer, factory, new FakePlan(job, variant, _client), new RootCallCounterReservation(_context, writer));
            Port = new FaultPort(real, hook ?? ((_, _) => ValueTask.CompletedTask));
            Checkpoints = CheckpointManager.CreateJson(new PostgresCheckpointStore(_context, writer));
        }

        public IDocumentBatchPort Port { get; }

        public CheckpointManager Checkpoints { get; }

        public Task<BatchRunResult> RunAsync(string session, CheckpointInfo? from = null) =>
            DocumentBatchWorkflow.RunAsync(DocumentBatchWorkflow.Build(Port, new BatchInvocationCounter()), Checkpoints, session, null, Ct, from);

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

    private sealed class FaultPort(IDocumentBatchPort inner, Func<string, int, ValueTask> hook) : IDocumentBatchPort
    {
        public ValueTask<int> LoadAsync(CancellationToken ct) => inner.LoadAsync(ct);

        public ValueTask<BatchRef> PrepareAsync(int cursor, CancellationToken ct) => inner.PrepareAsync(cursor, ct);

        public ValueTask<bool> LookupAsync(BatchRef batch, CancellationToken ct) => inner.LookupAsync(batch, ct);

        public async ValueTask ReserveCallAsync(BatchRef batch, CancellationToken ct)
        {
            await hook("reserve.before", batch.Ordinal);
            await inner.ReserveCallAsync(batch, ct);
            await hook("reserve.after", batch.Ordinal);
        }

        public Microsoft.Agents.AI.ChatClientAgent CreateAgent(BatchRef batch) => inner.CreateAgent(batch);

        public ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, CancellationToken ct) => inner.ComposeAsync(batch, ct);

        public ValueTask PersistAsync(BatchRef batch, string responseText, CancellationToken ct) => inner.PersistAsync(batch, responseText, ct);

        public ValueTask<bool> HasMoreAsync(int nextCursor, CancellationToken ct) => inner.HasMoreAsync(nextCursor, ct);

        public ValueTask AssembleAsync(CancellationToken ct) => inner.AssembleAsync(ct);

        public ValueTask<int> CommitAsync(CancellationToken ct) => inner.CommitAsync(ct);
    }

    /// <summary>Piano: 0 e 1 leaf, 2 join dei loro output. BatchId per root (identità AI §5.2), inputHash indipendente dal root.</summary>
    private sealed class FakePlan(Guid job, Variant variant, InternalModelClient client) : IDocumentBatchPlan
    {
        private string BatchIdOf(int ordinal) => Sha($"batch|{job}|{ordinal}");

        private static string LeafInput(int ordinal) => Sha($"input|{ordinal}");

        private static string JoinInput(string p0, string p1) => Sha($"input|2|{p0}|{p1}");

        public WorkManifestInput Manifest => new(H(variant.Config), H('b'), variant.Runtime, Definition, "alg-1", H(variant.Policy), """{"nodes":["Load","PrepareBatch","LookupOutput"]}""");

        public WorkLookupExpectation Expectation => new(Schema, Validation);

        public BatchAgentSpec Agent => new("BatchAgent", "batch-agent", "Istruzioni della fase batch.", client.CreateChatOptions(phase: AiPhase.Document), AiPhase.Document);

        public bool HasBatch(int ordinal) => ordinal is >= 0 and <= 2;

        private DerivedBatchInput Make(int ordinal, string input, params string[] parents) =>
            new(BatchIdOf(ordinal), "extract", null, 0, 0, ordinal, parents, $$"""{"ordinal":{{ordinal}}}""", 20, "measured", input, Schema, "p1");

        public async ValueTask<DerivedBatchInput> PrepareAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
        {
            if (ordinal is 0 or 1)
            {
                return Make(ordinal, LeafInput(ordinal));
            }

            var p0 = await prior.GetByOrdinalAsync(0, ct) ?? throw new InvalidOperationException("Genitore 0 non committato: il join non si pubblica.");
            var p1 = await prior.GetByOrdinalAsync(1, ct) ?? throw new InvalidOperationException("Genitore 1 non committato: il join non si pubblica.");
            return Make(2, JoinInput(p0.OutputHash, p1.OutputHash), p0.OutputHash, p1.OutputHash);
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

            return new ValidatedBatch(
                new WorkOutputInput(batch.BatchId, "InvokeBatch", batch.InputHash, CanonicalJson.Sha256Hex(responseText), responseText, null, null, $$"""["u{{batch.Ordinal}}"]""", null, null, Schema, Validation, null),
                null);
        }

        public async ValueTask AssembleAsync(IBatchOutputs outputs, CancellationToken ct)
        {
            for (var i = 0; i < 3; i++)
            {
                _ = await outputs.GetByOrdinalAsync(i, ct) ?? throw new InvalidOperationException($"Assemble senza l'output {i}.");
            }
        }

        public ValueTask<int> CommitAsync(IBatchOutputs outputs, CancellationToken ct) => new(3);
    }

    /// <summary>Modello finto a livello HTTP (llama.cpp simulato): registra i batch realmente richiesti.</summary>
    private sealed class FakeModel : HttpMessageHandler
    {
        private static readonly Regex Marker = new(@"BATCH-(\d+)\|", RegexOptions.Compiled);

        public List<int> Requests { get; } = [];

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

                    return Json(JsonSerializer.Serialize(new
                    {
                        id = "x", @object = "chat.completion", created = 1, model = "Qwen-A",
                        choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = $$"""{"batch":{{batch}}}""" } } },
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
