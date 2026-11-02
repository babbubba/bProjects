using System.Text.Json;
using bProjects.App.AI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// T042 (MAF-FAKE) — gate SDK 1.26.0 del grafo batch fisso (contracts/ai-workflow.md §5): Executor&lt;TInput&gt;, archi condizionali
/// esclusivi, QueueStateUpdate, CheckpointManager e ResumeStreamingAsync con SDK reale e IChatClient finto, senza PostgreSQL né DGX.
/// Prova: tre batch al 60% del budget B in sessioni distinte e senza history cumulativa (la richiesta cumulativa supererebbe B),
/// invio riservato prima della rete, commit prima di Advance, hit senza sessione né inferenza, ripresa da ogni checkpoint con output
/// già Committed senza reinferenza (guard all'ingresso di InvokeBatch), verifica fallita = errore senza avanzamento.
/// </summary>
public sealed class DocumentMafCycleTests
{
    private const int BudgetChars = 1000;
    private const int BatchChars = 600;   // 60% di B: tre batch cumulativi = 180% di B
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Three_batches_at_sixty_percent_run_in_distinct_sessions_without_cumulative_history()
    {
        var port = new FakePort(total: 3);
        var counter = new BatchInvocationCounter();
        var events = new List<WorkflowEvent>();

        var result = await RunAsync(port, counter, events);

        Assert.Equal(new BatchRunResult(3, 3), result);
        Assert.Equal(3, port.Model.Requests.Count);
        Assert.All(port.Model.Requests, request =>
        {
            Assert.Equal(1, request.UserMessages);            // solo il batch corrente
            Assert.Equal(0, request.AssistantMessages);       // nessuna risposta precedente nella history
            Assert.True(request.Chars <= BudgetChars, $"richiesta di {request.Chars} caratteri oltre B");
        });
        Assert.Equal([0, 1, 2], port.Model.Requests.Select(r => r.Batch));
        Assert.True(port.Model.Requests.Sum(r => r.Chars) > BudgetChars);   // la somma supererebbe B: solo sessioni nuove stanno nel budget

        // Ordine delle scritture: riserva prima della rete, commit prima di Advance, Advance prima del batch successivo.
        for (var i = 0; i < 3; i++)
        {
            Assert.True(port.Index($"reserve:{i}") < port.Index($"chat:{i}"));
            Assert.True(port.Index($"chat:{i}") < port.Index($"persist:{i}"));
            Assert.True(port.Index($"persist:{i}") < port.Index($"advance:{i}"));
        }

        Assert.Equal(0, port.Count("lookup-hit"));
        Assert.Equal(3, Invoked(events, DocumentBatchWorkflow.InvokeId));
        Assert.Equal(3, Invoked(events, DocumentBatchWorkflow.ValidatePersistId));
        Assert.Equal(1, Invoked(events, DocumentBatchWorkflow.AssembleId));
        Assert.Equal(1, Invoked(events, DocumentBatchWorkflow.CommitId));
    }

    [Fact]
    public async Task Hit_goes_to_Advance_without_session_inference_or_call_reservation()
    {
        var port = new FakePort(total: 3);
        port.PreCommit(1);
        var counter = new BatchInvocationCounter();
        var events = new List<WorkflowEvent>();

        var result = await RunAsync(port, counter, events);

        Assert.Equal(new BatchRunResult(3, 2), result);
        Assert.Equal([0, 2], port.Model.Requests.Select(r => r.Batch));
        Assert.Equal(0, port.Count("reserve:1"));
        Assert.Equal(2, Invoked(events, DocumentBatchWorkflow.InvokeId));      // il batch in hit non entra in InvokeBatch: rami esclusivi
        Assert.Equal(2, Invoked(events, DocumentBatchWorkflow.ValidatePersistId));
        Assert.Equal(3, Invoked(events, DocumentBatchWorkflow.AdvanceId));
        Assert.Equal(1, port.Count("lookup-hit"));
    }

    [Fact]
    public async Task Resume_from_every_earlier_checkpoint_reuses_committed_outputs_without_new_inference()
    {
        var store = new MafApiCompatibilityTests.InMemoryJsonCheckpointStore();
        var checkpoints = CheckpointManager.CreateJson(store);
        var sessionId = Guid.NewGuid().ToString("N");
        var first = new FakePort(total: 3);
        Assert.Equal(new BatchRunResult(3, 3), await DocumentBatchWorkflow.RunAsync(
            DocumentBatchWorkflow.Build(first), checkpoints, sessionId, null, Ct));

        var index = (await store.RetrieveIndexAsync(sessionId)).ToList();
        Assert.True(index.Count >= 8, "un checkpoint per superstep");

        var guardHits = 0;
        foreach (var info in index.Take(index.Count - 1))
        {
            // Nuovo processo: stesso "database" di output Committed, grafo e port nuovi, ripresa nativa dal checkpoint scelto.
            var second = first.NewProcessSharingCommitted();
            var counter = new BatchInvocationCounter();
            var events = new List<WorkflowEvent>();
            var result = await DocumentBatchWorkflow.RunAsync(
                DocumentBatchWorkflow.Build(second, counter), checkpoints, sessionId, events.Add, Ct, resumeFrom: info);

            Assert.Equal(3, result.Batches);
            Assert.Equal(0, result.Invocations);
            Assert.Empty(second.Model.Requests);
            Assert.Equal(0, second.CountPrefix("reserve:"));
            if (Invoked(events, DocumentBatchWorkflow.InvokeId) > 0)
            {
                guardHits++;   // Miss del checkpoint precedente al commit: il guard interno trova l'hit prima di creare la sessione
            }
        }

        Assert.True(guardHits > 0, "almeno un checkpoint contiene un Miss in volo verso InvokeBatch");
    }

    [Fact]
    public async Task Resume_after_partial_progress_continues_with_new_sessions_only_for_missing_batches()
    {
        var store = new MafApiCompatibilityTests.InMemoryJsonCheckpointStore();
        var checkpoints = CheckpointManager.CreateJson(store);
        var sessionId = Guid.NewGuid().ToString("N");
        var first = new FakePort(total: 3) { InvalidAt = 1 };
        await Assert.ThrowsAnyAsync<Exception>(() => DocumentBatchWorkflow.RunAsync(
            DocumentBatchWorkflow.Build(first), checkpoints, sessionId, null, Ct));
        Assert.Equal([0, 1], first.Model.Requests.Select(r => r.Batch));
        Assert.Equal(0, first.Count("advance:1"));   // verifica fallita: nessun avanzamento, nessun terzo invio
        Assert.True(first.IsCommitted(0));
        Assert.False(first.IsCommitted(1));

        var second = first.NewProcessSharingCommitted();
        second.InvalidAt = null;
        var result = await DocumentBatchWorkflow.RunAsync(
            DocumentBatchWorkflow.Build(second), checkpoints, sessionId, null, Ct);

        Assert.Equal(3, result.Batches);
        // Il batch 0 è committato: nessun nuovo invio. Il checkpoint portava già la risposta (non committata) del batch 1, che torna
        // a ValidatePersist; solo il batch 2, mai elaborato, richiede una sessione nuova.
        Assert.DoesNotContain(0, second.Model.Requests.Select(r => r.Batch));
        Assert.Contains(2, second.Model.Requests.Select(r => r.Batch));
        Assert.True(second.IsCommitted(1));
        Assert.All(second.Model.Requests, r => Assert.Equal(0, r.AssistantMessages));
    }

    [Fact]
    public async Task Invalid_output_terminates_with_an_error_and_never_takes_both_branches()
    {
        var port = new FakePort(total: 3) { InvalidAt = 0 };
        var events = new List<WorkflowEvent>();

        var failure = await Assert.ThrowsAsync<DocumentWorkException>(() => RunAsync(port, new BatchInvocationCounter(), events));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, failure.Code);
        Assert.Single(port.Model.Requests);
        Assert.Equal(0, Invoked(events, DocumentBatchWorkflow.AdvanceId));
        Assert.Equal(0, Invoked(events, DocumentBatchWorkflow.AssembleId));
        Assert.Equal(0, Invoked(events, DocumentBatchWorkflow.CommitId));
    }

    [Fact]
    public async Task Advance_records_the_last_reference_in_workflow_state_and_the_graph_ids_are_stable()
    {
        var port = new FakePort(total: 2);
        var events = new List<WorkflowEvent>();
        await RunAsync(port, new BatchInvocationCounter(), events);

        var seen = events.OfType<ExecutorInvokedEvent>().Select(e => e.ExecutorId).Distinct().ToList();
        Assert.Equal(DocumentBatchWorkflow.Stages.OrderBy(s => s), seen.OrderBy(s => s));
        Assert.Equal(DocumentBatchWorkflow.Stages.Count, DocumentBatchWorkflow.Stages.Distinct().Count());
    }

    // ------------------------------------------------------------------ infrastruttura

    private static Task<BatchRunResult> RunAsync(FakePort port, BatchInvocationCounter counter, List<WorkflowEvent> events) =>
        DocumentBatchWorkflow.RunAsync(
            DocumentBatchWorkflow.Build(port, counter),
            CheckpointManager.CreateJson(new MafApiCompatibilityTests.InMemoryJsonCheckpointStore()),
            Guid.NewGuid().ToString("N"), events.Add, Ct);

    private static int Invoked(IEnumerable<WorkflowEvent> events, string executorId) =>
        events.OfType<ExecutorInvokedEvent>().Count(e => e.ExecutorId == executorId);

    /// <summary>Raccordo in memoria: "database" degli output Committed condiviso fra processi, log ordinato delle scritture.</summary>
    private sealed class FakePort : IDocumentBatchPort
    {
        private readonly int _total;
        private readonly Dictionary<int, string> _committed;
        private readonly List<string> _log = [];

        public FakePort(int total, Dictionary<int, string>? committed = null)
        {
            _total = total;
            _committed = committed ?? [];
            Model = new FakeBatchModel();
        }

        public FakeBatchModel Model { get; }
        public int? InvalidAt { get; set; }

        public void PreCommit(int batch) => _committed[batch] = Output(batch);
        public bool IsCommitted(int batch) => _committed.ContainsKey(batch);
        public FakePort NewProcessSharingCommitted() => new(_total, _committed);
        public int Index(string entry) => _log.IndexOf(entry);
        public int Count(string entry) => _log.Count(e => e == entry);
        public int CountPrefix(string prefix) => _log.Count(e => e.StartsWith(prefix, StringComparison.Ordinal));

        private static string Output(int batch) => $$"""{"batch":{{batch}}}""";

        public ValueTask<int> LoadAsync(CancellationToken ct) => new(0);

        public ValueTask<BatchRef> PrepareAsync(int cursor, CancellationToken ct) =>
            new(new BatchRef(cursor, $"batch-{cursor}", new string((char)('a' + cursor), 64)));

        public ValueTask<bool> LookupAsync(BatchRef batch, CancellationToken ct)
        {
            var hit = _committed.ContainsKey(batch.Ordinal);
            if (hit)
            {
                _log.Add("lookup-hit");
            }

            return new(hit);
        }

        public ValueTask ReserveCallAsync(BatchRef batch, CancellationToken ct)
        {
            _log.Add($"reserve:{batch.Ordinal}");
            return ValueTask.CompletedTask;
        }

        public ChatClientAgent CreateAgent(BatchRef batch) =>
            new(new LoggingClient(Model, _log), new ChatClientAgentOptions
            {
                Id = "batch-agent",
                Name = "BatchAgent",
                ChatOptions = new ChatOptions { Instructions = "Istruzioni della fase batch." },
            });

        public IReadOnlyList<ChatMessage> Compose(BatchRef batch) =>
            [new ChatMessage(ChatRole.User, $"BATCH-{batch.Ordinal}|" + new string('x', BatchChars))];

        public ValueTask PersistAsync(BatchRef batch, string responseText, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(responseText);
            if (InvalidAt == batch.Ordinal || json.RootElement.GetProperty("batch").GetInt32() != batch.Ordinal)
            {
                throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Output non valido.");
            }

            _committed[batch.Ordinal] = responseText;
            _log.Add($"persist:{batch.Ordinal}");
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> HasMoreAsync(int nextCursor, CancellationToken ct)
        {
            _log.Add($"advance:{nextCursor - 1}");
            return new(nextCursor < _total);
        }

        public ValueTask AssembleAsync(CancellationToken ct)
        {
            Assert.Equal(_total, _committed.Count);   // Assemble solo con tutti gli output Committed
            _log.Add("assemble");
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> CommitAsync(CancellationToken ct)
        {
            _log.Add("commit");
            return new(_total);
        }
    }

    private sealed record ModelRequest(int Batch, int UserMessages, int AssistantMessages, int Chars);

    /// <summary>Modello finto: registra ogni richiesta e rifiuta quelle oltre il budget B, come farebbe il preflight reale.</summary>
    private sealed class FakeBatchModel
    {
        public List<ModelRequest> Requests { get; } = [];
    }

    private sealed class LoggingClient(FakeBatchModel model, List<string> log) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var user = list.Where(m => m.Role == ChatRole.User).ToList();
            var chars = list.Sum(m => m.Text.Length) + (options?.Instructions?.Length ?? 0);
            var text = user.Last().Text;
            var batch = int.Parse(text["BATCH-".Length..text.IndexOf('|')]);
            model.Requests.Add(new ModelRequest(batch, user.Count, list.Count(m => m.Role == ChatRole.Assistant), chars));
            log.Add($"chat:{batch}");
            if (chars > BudgetChars)
            {
                throw new InvalidOperationException($"context_overflow: {chars} > {BudgetChars}");
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $$"""{"batch":{{batch}}}""")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Il batch usa RunAsync non streaming.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
