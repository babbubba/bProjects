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
/// Dispatcher dei ChatTurn: un solo ciclo, quindi una sola esecuzione AI per istanza; claim FIFO, lease e fencing
/// sono nel database (<see cref="ExecutionLeaseStore"/>). Senza un <see cref="IChatTurnExecutor"/> registrato non
/// legge né modifica alcun turno: i turni restano Queued finché esiste chi può eseguirli davvero.
/// </summary>
public sealed class AiDispatchService(
    IServiceScopeFactory scopes,
    IOptions<ExecutionOptions> options,
    ILogger<AiDispatchService> logger,
    IChatTurnExecutor? executor = null,
    ChatTurnEvents? events = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ExecutionOptions _policy = options.Value;
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (executor is null)
        {
            logger.LogInformation("Nessun esecutore dei turni registrato: il dispatcher non prende in carico turni.");
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
                    // Database non disponibile o simili: nessun turno perde stato (il lease scade), si riprova.
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

    /// <summary>Prende in carico ed esegue al più un turno. False se non c'era nulla di ammissibile.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        if (executor is null)
        {
            return false;
        }

        ChatTurnClaim? claim;
        using (var scope = scopes.CreateScope())
        {
            claim = await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextAsync(_owner, ct);
        }

        if (claim is null)
        {
            return false;
        }

        await PublishState(claim.TurnId); // Running
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopHeartbeat = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(claim, leaseLost, stopHeartbeat.Token);
        var executed = false;
        try
        {
            await executor.ExecuteAsync(claim, leaseLost.Token);
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
            await Finish(claim, async (store, c) => (await store.ReleaseAfterFailureAsync(
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

        return true;
    }

    private async Task Finish(ChatTurnClaim claim, Func<ExecutionLeaseStore, CancellationToken, Task<bool>> transition, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        if (!await transition(scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>(), ct))
        {
            logger.LogInformation("Turno {TurnId}: transizione rifiutata (lease/fencing non più validi).", claim.TurnId);
            return;
        }

        await PublishState(claim.TurnId);
    }

    private Task PublishState(Guid turnId) => events is null ? Task.CompletedTask : events.PublishStateAsync(turnId);

    /// <summary>Rinnova il lease ogni RenewInterval; se l'owner non è più valido annulla l'esecuzione.</summary>
    private async Task HeartbeatAsync(ChatTurnClaim claim, CancellationTokenSource leaseLost, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_policy.RenewInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    if (!await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().RenewAsync(claim, stop))
                    {
                        await leaseLost.CancelAsync();
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Un guasto momentaneo del database non perde ancora il lease: riprova al prossimo intervallo.
                    logger.LogWarning("Rinnovo del lease del turno {TurnId} non riuscito: {ExceptionType}", claim.TurnId, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fine normale dell'esecuzione.
        }
    }
}
