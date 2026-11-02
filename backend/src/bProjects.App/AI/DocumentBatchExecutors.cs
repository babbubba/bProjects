using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>Fase del messaggio che attraversa il grafo batch: i predicati degli archi condizionali leggono solo questo valore.</summary>
public enum BatchKind
{
    Start,
    Prepared,
    Hit,
    Miss,
    Responded,
    Persisted,
    More,
    Done,
}

/// <summary>
/// Riferimento a un batch derivato già pubblicato (BatchId/InputHash calcolati secondo AI §5.2): solo identità, mai il payload.
/// Serializzabile nel checkpoint nativo.
/// </summary>
public sealed record BatchRef(int Ordinal, string BatchId, string InputHash);

/// <summary>
/// Unico messaggio del grafo fisso. <see cref="Kind"/> decide l'arco; <see cref="Cursor"/> è il prossimo ordinale (cursore applicativo,
/// non sostituisce il checkpoint); <see cref="Text"/> è la risposta grezza del modello, valida solo dopo ValidatePersistBatch.
/// </summary>
public sealed record BatchMessage(BatchKind Kind, int Cursor, BatchRef? Batch = null, string? Text = null);

/// <summary>Risultato finale del grafo: numero di batch percorsi e di invii effettivi al modello in questa esecuzione.</summary>
public sealed record BatchRunResult(int Batches, int Invocations);

/// <summary>
/// Raccordo applicativo del grafo batch: tutto ciò che tocca dati e modello. Il grafo non decide nulla da solo e non legge un
/// «prossimo executor» dal database. Le implementazioni di produzione usano <see cref="DocumentWorkStore"/> (manifesto, batch,
/// lookup, commit fenced) e <see cref="AgentFactory.CreateBatchAgent"/>; i test usano un'implementazione in memoria.
/// </summary>
public interface IDocumentBatchPort
{
    /// <summary>Load: pubblica/verifica il manifesto e ritorna il primo cursore (ordinale del primo batch).</summary>
    ValueTask<int> LoadAsync(CancellationToken ct);

    /// <summary>PrepareBatch: trasforma i dati e pubblica il batch derivato dell'ordinale; deterministico nel replay.</summary>
    ValueTask<BatchRef> PrepareAsync(int cursor, CancellationToken ct);

    /// <summary>Lookup di (owner,batchId,inputHash): vero solo se esiste un WorkOutput Committed compatibile e integro.</summary>
    ValueTask<bool> LookupAsync(BatchRef batch, CancellationToken ct);

    /// <summary>
    /// Riserva atomicamente il contatore chiamate del root e registra invio/attempt/fence PRIMA della rete (AI §5.2 punto 5). Può
    /// rifiutare (limite, deadline, lease perso) lanciando: nessuna sessione è creata prima.
    /// </summary>
    ValueTask ReserveCallAsync(BatchRef batch, CancellationToken ct);

    /// <summary>Agente batch della fase (senza history né tool): ogni invio gira in una sessione nuova, vedi <see cref="AgentFactory.RunBatchAsync"/>.</summary>
    ChatClientAgent CreateAgent(BatchRef batch);

    /// <summary>
    /// Messaggi del solo batch: input e contesto esplicito congelato, già misurati dal preflight del client. Asincrono perché dopo una
    /// ripresa da checkpoint il processo nuovo non ha eseguito PrepareBatch e rilegge dal database ciò che serve (es. output dei genitori).
    /// </summary>
    ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, CancellationToken ct);

    /// <summary>
    /// ValidatePersistBatch: valida la risposta e, solo se valida, committa l'output (e i figli determinabili) in una transazione fenced.
    /// Output non valido o non committabile: eccezione, mai un arco alternativo né una riparazione.
    /// </summary>
    ValueTask PersistAsync(BatchRef batch, string responseText, CancellationToken ct);

    /// <summary>Advance, dopo il commit: vero se resta almeno un batch da elaborare a partire da <paramref name="nextCursor"/>.</summary>
    ValueTask<bool> HasMoreAsync(int nextCursor, CancellationToken ct);

    /// <summary>Assemble: compone il risultato di dominio dagli output Committed, senza inferenza.</summary>
    ValueTask AssembleAsync(CancellationToken ct);

    /// <summary>Commit finale di dominio dell'owner (fenced); ritorna il numero di batch percorsi.</summary>
    ValueTask<int> CommitAsync(CancellationToken ct);
}

/// <summary>Contatore di invii effettivi di una esecuzione del grafo (osservabile dal chiamante, non fa parte dello stato MAF).</summary>
public sealed class BatchInvocationCounter
{
    private int _count;
    public int Value => Volatile.Read(ref _count);
    internal void Increment() => Interlocked.Increment(ref _count);
}

/// <summary>Load: unico ingresso del grafo.</summary>
[SendsMessage(typeof(BatchMessage))]
internal sealed class LoadExecutor(IDocumentBatchPort port) : Executor<BatchMessage>(DocumentBatchWorkflow.LoadId)
{
    public override async ValueTask HandleAsync(BatchMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var cursor = await port.LoadAsync(cancellationToken);
        await context.SendMessageAsync(new BatchMessage(BatchKind.Start, cursor), cancellationToken: cancellationToken);
    }
}

/// <summary>PrepareBatch: pubblica DerivedBatch per il cursore ricevuto (da Load o da Advance).</summary>
[SendsMessage(typeof(BatchMessage))]
internal sealed class PrepareBatchExecutor(IDocumentBatchPort port) : Executor<BatchMessage>(DocumentBatchWorkflow.PrepareId)
{
    public override async ValueTask HandleAsync(BatchMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var batch = await port.PrepareAsync(message.Cursor, cancellationToken);
        await context.SendMessageAsync(new BatchMessage(BatchKind.Prepared, batch.Ordinal, batch), cancellationToken: cancellationToken);
    }
}

/// <summary>LookupOutput: Hit emette il solo riferimento verso Advance, senza creare sessione; Miss va a InvokeBatch.</summary>
[SendsMessage(typeof(BatchMessage))]
internal sealed class LookupOutputExecutor(IDocumentBatchPort port) : Executor<BatchMessage>(DocumentBatchWorkflow.LookupId)
{
    public override async ValueTask HandleAsync(BatchMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var batch = message.Batch ?? throw new InvalidOperationException("LookupOutput senza batch.");
        var hit = await port.LookupAsync(batch, cancellationToken);
        await context.SendMessageAsync(message with { Kind = hit ? BatchKind.Hit : BatchKind.Miss, Text = null }, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// InvokeBatch: ripete il lookup all'ingresso, perché un checkpoint ripreso può contenere il Miss precedente al commit (hit →
/// Advance, nessuna sessione né rete). Su miss riserva il contatore prima della rete, poi esegue UN invio in una AgentSession
/// nuova (<see cref="AgentFactory.RunBatchAsync"/>): nessun turno precedente è implicito.
/// </summary>
[SendsMessage(typeof(BatchMessage))]
internal sealed class InvokeBatchExecutor(IDocumentBatchPort port, BatchInvocationCounter counter) : Executor<BatchMessage>(DocumentBatchWorkflow.InvokeId)
{
    public override async ValueTask HandleAsync(BatchMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var batch = message.Batch ?? throw new InvalidOperationException("InvokeBatch senza batch.");
        if (await port.LookupAsync(batch, cancellationToken))
        {
            await context.SendMessageAsync(message with { Kind = BatchKind.Hit, Text = null }, cancellationToken: cancellationToken);
            return;
        }

        await port.ReserveCallAsync(batch, cancellationToken);
        counter.Increment();
        var response = await AgentFactory.RunBatchAsync(port.CreateAgent(batch), await port.ComposeAsync(batch, cancellationToken), cancellationToken);
        await context.SendMessageAsync(message with { Kind = BatchKind.Responded, Text = response.Text }, cancellationToken: cancellationToken);
    }
}

/// <summary>ValidatePersistBatch: nessun avanzamento su risposta in memoria; l'output è Committed prima del messaggio verso Advance.</summary>
[SendsMessage(typeof(BatchMessage))]
internal sealed class ValidatePersistBatchExecutor(IDocumentBatchPort port) : Executor<BatchMessage>(DocumentBatchWorkflow.ValidatePersistId)
{
    public override async ValueTask HandleAsync(BatchMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var batch = message.Batch ?? throw new InvalidOperationException("ValidatePersistBatch senza batch.");
        await port.PersistAsync(batch, message.Text ?? "", cancellationToken);
        await context.SendMessageAsync(message with { Kind = BatchKind.Persisted, Text = null }, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Advance: dopo Hit o commit registra nello stato MAF solo il riferimento al batch (visibile agli altri executor dal superstep
/// successivo: il cursore viaggia anche nel messaggio) e decide More|Done con archi esclusivi.
/// </summary>
[SendsMessage(typeof(BatchMessage))]
internal sealed class AdvanceExecutor(IDocumentBatchPort port) : Executor<BatchMessage>(DocumentBatchWorkflow.AdvanceId)
{
    public const string StateKey = "lastAdvanced";

    public override async ValueTask HandleAsync(BatchMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var batch = message.Batch ?? throw new InvalidOperationException("Advance senza batch.");
        var next = batch.Ordinal + 1;
        await context.QueueStateUpdateAsync(StateKey, batch, DocumentBatchWorkflow.StateScope, cancellationToken);
        var more = await port.HasMoreAsync(next, cancellationToken);
        await context.SendMessageAsync(new BatchMessage(more ? BatchKind.More : BatchKind.Done, next, batch), cancellationToken: cancellationToken);
    }
}

/// <summary>Assemble: composizione senza inferenza dagli output Committed.</summary>
[SendsMessage(typeof(BatchMessage))]
internal sealed class AssembleExecutor(IDocumentBatchPort port) : Executor<BatchMessage>(DocumentBatchWorkflow.AssembleId)
{
    public override async ValueTask HandleAsync(BatchMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        await port.AssembleAsync(cancellationToken);
        await context.SendMessageAsync(message, cancellationToken: cancellationToken);
    }
}

/// <summary>Commit: commit finale di dominio; unico punto che produce l'output del workflow.</summary>
[YieldsOutput(typeof(BatchRunResult))]
internal sealed class CommitExecutor(IDocumentBatchPort port, BatchInvocationCounter counter) : Executor<BatchMessage>(DocumentBatchWorkflow.CommitId)
{
    public override async ValueTask HandleAsync(BatchMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var batches = await port.CommitAsync(cancellationToken);
        await context.YieldOutputAsync(new BatchRunResult(batches, counter.Value), cancellationToken);
    }
}
