using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace bProjects.App.AI;

/// <summary>
/// Grafo nativo fisso dei workflow documentali multipasso (T043, contracts/ai-workflow.md §5):
/// Load → PrepareBatch → LookupOutput; Hit → Advance; Miss → InvokeBatch → ValidatePersistBatch → Advance;
/// Advance → PrepareBatch (More) oppure Assemble → Commit (Done). Un executor per ruolo, non uno per batch: i batch sono dati.
/// Gli archi condizionali sono esclusivi (Hit|Miss, Hit|Responded, More|Done) e i predicati sono codice della definizione. Il
/// checkpoint è quello nativo MAF; la ripresa riparte dallo stesso grafo e i guard (LookupOutput e il lookup all'ingresso di
/// InvokeBatch) riusano gli output già Committed senza inferenza. Nessun interprete di grafo, scheduler o secondo engine; il ramo
/// bound della 001 resta invariato.
/// </summary>
public static class DocumentBatchWorkflow
{
    public const string LoadId = "Load";
    public const string PrepareId = "PrepareBatch";
    public const string LookupId = "LookupOutput";
    public const string InvokeId = "InvokeBatch";
    public const string ValidatePersistId = "ValidatePersistBatch";
    public const string AdvanceId = "Advance";
    public const string AssembleId = "Assemble";
    public const string CommitId = "Commit";

    public const string StateScope = "document-batch";

    /// <summary>Executor ID stabili della definizione, nell'ordine del grafo.</summary>
    public static IReadOnlyList<string> Stages { get; } =
        [LoadId, PrepareId, LookupId, InvokeId, ValidatePersistId, AdvanceId, AssembleId, CommitId];

    /// <summary>Messaggio d'ingresso del grafo.</summary>
    public static BatchMessage StartMessage { get; } = new(BatchKind.Start, 0);

    /// <summary>Costruisce il grafo nativo con il raccordo <paramref name="port"/>; <paramref name="counter"/> conta gli invii reali.</summary>
    public static Workflow Build(IDocumentBatchPort port, BatchInvocationCounter? counter = null)
    {
        counter ??= new BatchInvocationCounter();
        var load = new LoadExecutor(port).BindExecutor();
        var prepare = new PrepareBatchExecutor(port).BindExecutor();
        var lookup = new LookupOutputExecutor(port).BindExecutor();
        var invoke = new InvokeBatchExecutor(port, counter).BindExecutor();
        var validate = new ValidatePersistBatchExecutor(port).BindExecutor();
        var advance = new AdvanceExecutor(port).BindExecutor();
        var assemble = new AssembleExecutor(port).BindExecutor();
        var commit = new CommitExecutor(port, counter).BindExecutor();

        return new WorkflowBuilder(load)
            .AddEdge(load, prepare)
            .AddEdge(prepare, lookup)
            .AddEdge<BatchMessage>(lookup, advance, m => m is { Kind: BatchKind.Hit })
            .AddEdge<BatchMessage>(lookup, invoke, m => m is { Kind: BatchKind.Miss })
            .AddEdge<BatchMessage>(invoke, advance, m => m is { Kind: BatchKind.Hit })
            .AddEdge<BatchMessage>(invoke, validate, m => m is { Kind: BatchKind.Responded })
            .AddEdge(validate, advance)
            .AddEdge<BatchMessage>(advance, prepare, m => m is { Kind: BatchKind.More })
            .AddEdge<BatchMessage>(advance, assemble, m => m is { Kind: BatchKind.Done })
            .AddEdge(assemble, commit)
            .WithOutputFrom(commit)
            .Build();
    }

    /// <summary>
    /// Esegue il grafo: avvio nativo se non ci sono checkpoint della sessione, altrimenti <c>ResumeStreamingAsync</c> dall'ultimo.
    /// Ritorna il risultato del Commit; qualunque errore di executor/workflow risale come eccezione (nessun retry qui).
    /// </summary>
    public static async Task<BatchRunResult> RunAsync(
        Workflow workflow, CheckpointManager checkpoints, string sessionId, Action<WorkflowEvent>? onEvent, CancellationToken ct,
        CheckpointInfo? resumeFrom = null)
    {
        var latest = resumeFrom ?? await checkpoints.GetLatestCheckpointAsync(sessionId, ct);
        await using var run = latest is null
            ? await InProcessExecution.RunStreamingAsync(workflow, StartMessage, checkpoints, sessionId, ct)
            : await InProcessExecution.ResumeStreamingAsync(workflow, latest, checkpoints, ct);

        BatchRunResult? result = null;
        Exception? failure = null;
        await foreach (var evt in run.WatchStreamAsync(ct))
        {
            onEvent?.Invoke(evt);
            switch (evt)
            {
                case WorkflowOutputEvent { Data: BatchRunResult output }:
                    result = output;
                    break;
                case WorkflowErrorEvent error:
                    failure ??= error.Exception;
                    break;
                case ExecutorFailedEvent failed:
                    failure ??= failed.Data as Exception;
                    break;
            }
        }

        if (failure is not null)
        {
            throw failure;
        }

        return result ?? throw new InvalidOperationException("Il grafo batch è terminato senza risultato.");
    }
}
