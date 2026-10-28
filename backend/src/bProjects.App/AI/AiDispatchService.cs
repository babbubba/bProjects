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
/// ProcessingRun di Analysis (stessa coda, stesso limite globale, FIFO per richiesta); claim, lease e fencing sono nel
/// database (<see cref="ExecutionLeaseStore"/>). Un turno è eseguito dall'<see cref="IChatTurnExecutor"/>; un run di
/// Analysis dal workflow nativo <see cref="AnalysisWorkflow"/>, che sceglie da solo avvio o ripresa dal checkpoint: il
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
    ProcessingRunEvents? runEvents = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ExecutionOptions _policy = options.Value;
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (executor is null && workflow is null)
        {
            logger.LogInformation("Nessun esecutore dei turni né workflow di analisi registrati: il dispatcher non prende in carico lavori.");
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
        if (executor is null && workflow is null)
        {
            return false;
        }

        WorkClaim? work;
        using (var scope = scopes.CreateScope())
        {
            work = await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>()
                .ClaimNextWorkAsync(_owner, turns: executor is not null, runs: workflow is not null, ct);
        }

        if (work is null)
        {
            return false;
        }

        if (work.Turn is { } turn)
        {
            await RunTurnAsync(turn, ct);
        }
        else
        {
            await RunAnalysisAsync(work.Run!, ct);
        }

        return true;
    }

    private async Task RunTurnAsync(ChatTurnClaim claim, CancellationToken ct)
    {
        await PublishState(claim.TurnId); // Running
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
        catch (Exception ex)
        {
            var failure = ModelFailure.Classify(ex);
            logger.LogWarning("Turno {TurnId} fallito: {ExceptionType}, codice {Code}, transitorio {Transient}",
                claim.TurnId, ex.GetType().Name, failure.Code, failure.IsTransient);
            await Finish(claim.TurnId, async (store, c) => (await store.ReleaseAfterFailureAsync(
                claim, failure, failure.Code, "Il modello interno ha rifiutato la richiesta.", c)).Outcome != TurnTransition.Rejected, ct);
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
            using var scope = scopes.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>()
                .FailAsync(claim, "processing_failed", "L'esecuzione è terminata senza produrre una risposta.", ct))
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
    private async Task RunAnalysisAsync(AnalysisRunClaim claim, CancellationToken ct)
    {
        await PublishRunState(claim.RunId); // Running
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopHeartbeat = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync((store, c) => store.RenewAsync(claim, c), claim.RunId, leaseLost, stopHeartbeat.Token);
        var completed = false;
        try
        {
            // La fase osservata è già scritta dal workflow nella proiezione: qui si notifica soltanto, senza bloccare il grafo.
            await workflow!.RunAsync(claim, stage => { _ = PublishRunState(claim.RunId); }, leaseLost.Token);
            completed = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Arresto dell'app: il lease scade e un'istanza successiva fa il reclaim e riprende dal checkpoint.
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            logger.LogWarning("Lease perso durante l'esecuzione dell'analisi {RunId}: il risultato non verrà committato.", claim.RunId);
        }
        catch (Exception ex)
        {
            var failure = ModelFailure.Classify(ex);
            logger.LogWarning("Analisi {RunId} fallita: {ExceptionType}, codice {Code}, transitorio {Transient}",
                claim.RunId, ex.GetType().Name, failure.Code, failure.IsTransient);
            using var scope = scopes.CreateScope();
            var released = await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ReleaseAfterFailureAsync(
                claim, failure, failure.Code, "Il modello interno ha rifiutato la richiesta.", ct);
            if (released.Outcome == TurnTransition.Rejected)
            {
                logger.LogInformation("Analisi {RunId}: transizione rifiutata (lease/fencing non più validi).", claim.RunId);
            }
            else
            {
                await PublishRunState(claim.RunId);
            }
        }
        finally
        {
            await stopHeartbeat.CancelAsync();
            await heartbeat;
        }

        if (completed)
        {
            await PublishRunState(claim.RunId); // Completed o Failed già scritto dal workflow
        }
    }

    private async Task Finish(Guid id, Func<ExecutionLeaseStore, CancellationToken, Task<bool>> transition, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        if (!await transition(scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>(), ct))
        {
            logger.LogInformation("Turno {TurnId}: transizione rifiutata (lease/fencing non più validi).", id);
            return;
        }

        await PublishState(id);
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
