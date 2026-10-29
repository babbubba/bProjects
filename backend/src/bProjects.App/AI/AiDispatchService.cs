using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.Extensions.Options;

namespace bProjects.App.AI;

/// <summary>
/// Punto di integrazione dell'esecuzione di un ChatTurn già in lease (ConversationalAgent, T017). L'esecutore
/// compone il commit atomico della risposta (T018) con <see cref="ExecutionLeaseStore.CompleteAsync"/>; se lancia
/// un'eccezione il dispatcher la classifica (transitoria/permanente). Deve onorare il token: viene annullato
/// quando il lease si perde.
/// </summary>
public interface IChatTurnExecutor
{
    Task ExecuteAsync(ChatTurnClaim claim, CancellationToken ct);
}

/// <summary>
/// Dispatcher dei lavori AI: un solo ciclo, quindi una sola esecuzione AI per istanza, sia per i ChatTurn sia per i
/// ProcessingRun di Analysis e di Document (stessa coda, stesso limite globale, FIFO per richiesta); claim, lease e fencing sono nel
/// database (<see cref="ExecutionLeaseStore"/>). Un turno è eseguito dall'<see cref="IChatTurnExecutor"/>; un run di
/// Analysis dal workflow nativo <see cref="AnalysisWorkflow"/> e uno di Document da <see cref="DocumentWorkflow"/>, che scelgono da soli avvio o ripresa dal checkpoint: il
/// dispatcher non decide mai il passo successivo. Senza esecutore o workflow registrati il rispettivo tipo di lavoro
/// non viene letto né modificato: resta Queued finché esiste chi può eseguirlo davvero.
/// </summary>
public sealed class AiDispatchService(
    IServiceScopeFactory scopes,
    IOptions<ExecutionOptions> options,
    ILogger<AiDispatchService> logger,
    IChatTurnExecutor? executor = null,
    ChatTurnEvents? events = null,
    AnalysisWorkflow? workflow = null,
    ProcessingRunEvents? runEvents = null,
    DocumentWorkflow? documentWorkflow = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ExecutionOptions _policy = options.Value;
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (executor is null && workflow is null && documentWorkflow is null)
        {
            logger.LogInformation("Nessun esecutore dei turni né workflow formale registrati: il dispatcher non prende in carico lavori.");
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!await RunOnceAsync(stoppingToken))
                    {
                        await Task.Delay(PollInterval, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Database non disponibile o simili: nessun lavoro perde stato (il lease scade), si riprova.
                    logger.LogError("Ciclo di dispatch fallito: {ExceptionType}", ex.GetType().Name);
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Arresto dell'applicazione.
        }
    }

    /// <summary>Prende in carico ed esegue al più un lavoro. False se non c'era nulla di ammissibile.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        if (executor is null && workflow is null && documentWorkflow is null)
        {
            return false;
        }

        WorkClaim? work;
        using (var scope = scopes.CreateScope())
        {
            work = await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>()
                .ClaimNextWorkAsync(_owner, turns: executor is not null, runs: workflow is not null, documents: documentWorkflow is not null, ct);
        }

        if (work is null)
        {
            return false;
        }

        if (work.Turn is { } turn)
        {
            await RunTurnAsync(turn, ct);
        }
        else if (work.Run is { } analysis)
        {
            await RunAnalysisAsync(analysis, ct);
        }
        else
        {
            await RunDocumentAsync(work.Document!, ct);
        }

        return true;
    }

    private async Task RunTurnAsync(ChatTurnClaim claim, CancellationToken ct)
    {
        await PublishState(claim.TurnId); // Running
        // Uno scope di telemetria per tentativo: le chiamate al modello vi confluiscono e si scrivono con il commit o con il rilascio.
        using var inferenceScope = InferenceScope.Begin();
        var inference = InferenceScope.Current!;
        var owner = new InferenceOwner(claim.TurnId, null, claim.AttemptCount, claim.FencingToken);
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopHeartbeat = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync((store, c) => store.RenewAsync(claim, c), claim.TurnId, leaseLost, stopHeartbeat.Token);
        var executed = false;
        try
        {
            await executor!.ExecuteAsync(claim, leaseLost.Token);
            executed = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Arresto dell'app: il lease scade e un'istanza successiva fa il reclaim (nuovo tentativo).
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            logger.LogWarning("Lease perso durante l'esecuzione del turno {TurnId}: il risultato non verrà committato.", claim.TurnId);
        }
        catch (Exception ex) when (IsLeaseLost(ex))
        {
            // Commit rifiutato per lease non più valido: il turno non è più nostro, non si rilascia né si classifica.
            // Scadenza del lease + sweep/reclaim decidono (nuovo tentativo, deadline_exceeded o attempts_exhausted).
            logger.LogWarning("Turno {TurnId}: lease non più valido, nessun rilascio (decide sweep/reclaim).", claim.TurnId);
        }
        catch (Exception ex)
        {
            var failure = ModelFailure.Classify(ex);
            logger.LogWarning("Turno {TurnId} fallito: {ExceptionType}, codice {Code}, transitorio {Transient}",
                claim.TurnId, ex.GetType().Name, failure.Code, failure.IsTransient);
            if (await ReleaseAsync((store, c) => store.ReleaseAfterFailureAsync(
                    claim, failure, failure.Code, failure.Detail ?? "Il modello interno ha rifiutato la richiesta.", c), inference, owner, ct) is null)
            {
                logger.LogInformation("Turno {TurnId}: transizione rifiutata (lease/fencing non più validi).", claim.TurnId);
            }
            else
            {
                await PublishState(claim.TurnId);
            }
        }
        finally
        {
            await stopHeartbeat.CancelAsync();
            await heartbeat;
        }

        if (executed)
        {
            // Fuori dal try dell'esecutore: un errore del database qui non è un errore del modello e non va classificato come tale.
            // Un esecutore che torna senza aver committato Completed ha violato il contratto: Failed, mai un turno a metà.
            // Se il turno è già Completed la guardia di fencing rifiuta l'update, ed è l'esito normale.
            // Commit rifiutato senza perdita del lease (es. session_revision_changed): le chiamate del tentativo si scrivono nella
            // stessa transazione del Failed e solo se owner/fencing sono ancora validi, come per il Failed del validatore dei run.
            if (await ReleaseAsync(async (store, c) =>
                    await store.FailAsync(claim, "processing_failed", "L'esecuzione è terminata senza produrre una risposta.", c)
                        ? new TurnRelease(TurnTransition.Failed, ErrorCode: "processing_failed")
                        : new TurnRelease(TurnTransition.Rejected), inference, owner, ct) is not null)
            {
                await PublishState(claim.TurnId);
            }
        }
    }

    /// <summary>
    /// Esegue un run di Analysis: il workflow nativo avvia o riprende dal checkpoint e committa da sé revisione AI +
    /// Completed (o registra Failed per output non valido). Un errore del modello o dell'infrastruttura è classificato
    /// qui (transitorio: Queued con backoff entro tentativi e deadline; permanente: Failed) con le stesse regole dei turni.
    /// </summary>
    private Task RunAnalysisAsync(AnalysisRunClaim claim, CancellationToken ct) => RunFormalAsync(
        "analisi",
        claim.RunId,
        new InferenceOwner(null, claim.RunId, claim.AttemptCount, claim.FencingToken),
        (store, c) => store.RenewAsync(claim, c),
        // La fase osservata è già scritta dal workflow nella proiezione: qui si notifica soltanto, senza bloccare il grafo.
        c => workflow!.RunAsync(claim, stage => { _ = PublishRunState(claim.RunId); }, c),
        (store, failure, c) => store.ReleaseAfterFailureAsync(claim, failure, failure.Code, failure.Detail ?? "Il modello interno ha rifiutato la richiesta.", c),
        ct);

    /// <summary>
    /// Esegue un run di Document con lo stesso ciclo, lo stesso lease e le stesse regole di retry dell'Analysis: il workflow
    /// nativo project-document-v1 committa da sé bozza + Completed + LatestDraftId (o registra Failed per output non valido o
    /// contesto oltre budget).
    /// </summary>
    private Task RunDocumentAsync(DocumentRunClaim claim, CancellationToken ct) => RunFormalAsync(
        "generazione del documento",
        claim.RunId,
        new InferenceOwner(null, claim.RunId, claim.AttemptCount, claim.FencingToken),
        (store, c) => store.RenewAsync(claim, c),
        c => documentWorkflow!.RunAsync(claim, stage => { _ = PublishRunState(claim.RunId); }, c),
        (store, failure, c) => store.ReleaseAfterFailureAsync(claim, failure, failure.Code, failure.Detail ?? "Il modello interno ha rifiutato la richiesta.", c),
        ct);

    private async Task RunFormalAsync(
        string label,
        Guid runId,
        InferenceOwner owner,
        Func<ExecutionLeaseStore, CancellationToken, Task<bool>> renew,
        Func<CancellationToken, Task> execute,
        Func<ExecutionLeaseStore, ModelFailure, CancellationToken, Task<TurnRelease>> release,
        CancellationToken ct)
    {
        await PublishRunState(runId); // Running
        // Lo scope è ereditato dagli executor del workflow MAF: commit e Failed del validatore vi trovano le chiamate del tentativo.
        using var inferenceScope = InferenceScope.Begin();
        var inference = InferenceScope.Current!;
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopHeartbeat = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(renew, runId, leaseLost, stopHeartbeat.Token);
        var completed = false;
        try
        {
            await execute(leaseLost.Token);
            completed = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Arresto dell'app: il lease scade e un'istanza successiva fa il reclaim e riprende dal checkpoint.
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            logger.LogWarning("Lease perso durante l'esecuzione ({Label}) del run {RunId}: il risultato non verrà committato.", label, runId);
        }
        catch (Exception ex) when (IsLeaseLost(ex))
        {
            // Scrittura rifiutata per lease non più valido: il run non è più nostro, non si rilascia né si classifica.
            // Scadenza del lease + sweep/reclaim decidono (nuovo tentativo, deadline_exceeded o attempts_exhausted).
            logger.LogWarning("Run {RunId} ({Label}): lease non più valido, nessun rilascio (decide sweep/reclaim).", runId, label);
        }
        catch (Exception ex)
        {
            var failure = ModelFailure.Classify(ex);
            logger.LogWarning("Run {RunId} ({Label}) fallito: {ExceptionType}, codice {Code}, transitorio {Transient}",
                runId, label, ex.GetType().Name, failure.Code, failure.IsTransient);
            if (await ReleaseAsync((store, c) => release(store, failure, c), inference, owner, ct) is null)
            {
                logger.LogInformation("Run {RunId} ({Label}): transizione rifiutata (lease/fencing non più validi).", runId, label);
            }
            else
            {
                await PublishRunState(runId);
            }
        }
        finally
        {
            await stopHeartbeat.CancelAsync();
            await heartbeat;
        }

        if (completed)
        {
            await PublishRunState(runId); // Completed o Failed già scritto dal workflow
        }
    }


    /// <summary>
    /// Rilascio dopo un errore osservato dall'owner (o Failed di un turno terminato senza commit), nella stessa transazione delle righe
    /// di telemetria del tentativo (failed/interrupted, mai token inventati): se owner/fencing non sono più validi la transazione si
    /// annulla e non si scrive nulla. Null se la transizione è stata rifiutata.
    /// </summary>
    private async Task<TurnRelease?> ReleaseAsync(
        Func<ExecutionLeaseStore, CancellationToken, Task<TurnRelease>> release, InferenceScope inference, InferenceOwner owner, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var released = await release(scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>(), ct);
        if (released.Outcome == TurnTransition.Rejected)
        {
            return null;
        }

        await InferenceCallLog.AppendAttemptAsync(db, inference, owner, ct);
        await transaction.CommitAsync(ct);
        inference.MarkWritten();
        return released;
    }

    private static bool IsLeaseLost(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is LeaseLostException)
            {
                return true;
            }
        }

        return false;
    }

    private Task PublishState(Guid turnId) => events is null ? Task.CompletedTask : events.PublishStateAsync(turnId);

    private Task PublishRunState(Guid runId) => runEvents is null ? Task.CompletedTask : runEvents.PublishStateAsync(runId);

    /// <summary>Rinnova il lease ogni RenewInterval; se l'owner non è più valido annulla l'esecuzione.</summary>
    private async Task HeartbeatAsync(
        Func<ExecutionLeaseStore, CancellationToken, Task<bool>> renew, Guid workId, CancellationTokenSource leaseLost, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_policy.RenewInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    if (!await renew(scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>(), stop))
                    {
                        await leaseLost.CancelAsync();
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Un guasto momentaneo del database non perde ancora il lease: riprova al prossimo intervallo.
                    logger.LogWarning("Rinnovo del lease del lavoro {WorkId} non riuscito: {ExceptionType}", workId, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fine normale dell'esecuzione.
        }
    }
}
