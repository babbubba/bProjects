using System.Collections.Concurrent;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Evidence;
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
/// dispatcher non decide mai il passo successivo. Le unità di estrazione documentale (DocumentJob ExtractUnit, T050) sono lavoro di sfondo
/// bounded dello stesso slot: una per claim, con precedenza di chat, run ed estrazioni di messaggio alla successiva e nessuna preemption; il
/// routing dei workflow formali e della chat documentale è T123. Senza esecutore o workflow registrati il rispettivo tipo di lavoro
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
    DocumentWorkflow? documentWorkflow = null,
    IEvidenceExtractionExecutor? extractor = null,
    EvidenceEvents? evidenceEvents = null,
    DispatchWakeup? wakeup = null,
    IDocumentUnitExecutor? unitExecutor = null,
    DocumentRootClassifier? classifier = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ExecutionOptions _policy = options.Value;
    private readonly IDocumentUnitExecutor? _unitExecutor = unitExecutor;
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (executor is null && workflow is null && documentWorkflow is null && extractor is null && _unitExecutor is null)
        {
            logger.LogInformation("Nessun esecutore dei turni né workflow formale registrati: il dispatcher non prende in carico lavori.");
            return;
        }

        try
        {
            // Il monitor delle scadenze è un ciclo distinto dal ciclo di lavoro: continua a girare mentre l'unico slot AI è occupato da
            // un'esecuzione (che il ciclo di lavoro attende). Non è uno scheduler: non prende in carico lavoro e non promuove.
            var monitor = MonitorDeadlinesAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!await RunOnceAsync(stoppingToken))
                    {
                        await IdleAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Database non disponibile o simili: nessun lavoro perde stato (il lease scade), si riprova.
                    logger.LogError("Ciclo di dispatch fallito: {ExceptionType}", ex.GetType().Name);
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }

            await monitor;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Arresto dell'applicazione.
        }
    }

    /// <summary>
    /// Monitor/sweep delle scadenze (T119): la prima passata è la riconciliazione al restart (al riavvio le scadenze si leggono dal database, il
    /// budget non riparte), poi una passata per ogni <see cref="ExecutionOptions.DeadlineSweepInterval"/>. Ogni passata rilegge il database:
    /// nessun timer in memoria sopravvive a una promozione o a un reclaim.
    /// </summary>
    private async Task MonitorDeadlinesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_policy.DeadlineSweepInterval);
        try
        {
            do
            {
                try
                {
                    await SweepDeadlinesOnceAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Sweep delle scadenze non riuscito: {ExceptionType}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Arresto dell'applicazione.
        }
    }

    // Esecuzioni in corso in questa istanza, per id del lavoro: lo sweep che chiude un root scaduto ne annulla il token (cooperativo; il fence
    // già impedisce ogni pubblicazione, l'annullamento evita solo calcolo inutile).
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();

    /// <summary>
    /// Una passata dello sweep delle scadenze: chiude i root documentali e i figli scaduti (<see cref="ExecutionLeaseStore.SweepExpiredDocumentWorkAsync"/>),
    /// annulla l'esecuzione in corso del lavoro chiuso e notifica gli stati terminali. Nessuna promozione, nessun claim.
    /// </summary>
    public async Task<ExpiredWork> SweepDeadlinesOnceAsync(CancellationToken ct)
    {
        ExpiredWork closed;
        using (var scope = scopes.CreateScope())
        {
            closed = await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>()
                .SweepExpiredDocumentWorkAsync(turns: executor is not null, analysisRuns: workflow is not null, documentRuns: documentWorkflow is not null, ct);
        }

        foreach (var id in closed.Turns.Concat(closed.Runs).Concat(closed.Extractions).Concat(closed.Jobs))
        {
            if (_active.TryGetValue(id, out var running))
            {
                try
                {
                    await running.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                    // L'esecuzione si è appena conclusa.
                }
            }
        }

        foreach (var id in closed.Turns)
        {
            await PublishState(id);
        }

        foreach (var id in closed.Runs)
        {
            await PublishRunState(id);
        }

        if (evidenceEvents is not null)
        {
            foreach (var id in closed.Extractions)
            {
                await evidenceEvents.PublishStateAsync(id);
            }
        }

        return closed;
    }

    /// <summary>
    /// Attesa a riposo: al massimo un intervallo di polling, interrotta subito da un endpoint che ha appena accodato un turno o un run.
    /// Il lavoro resta comunque letto dal database, quindi un segnale perso costa al più <see cref="PollInterval"/>.
    /// </summary>
    private async Task IdleAsync(CancellationToken ct)
    {
        using var span = DocumentTiming.Span("dispatch.idle");
        await (wakeup is null ? Task.Delay(PollInterval, ct) : wakeup.WaitAsync(PollInterval, ct));
    }

    /// <summary>Prende in carico ed esegue al più un lavoro. False se non c'era nulla di ammissibile.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        if (executor is null && workflow is null && documentWorkflow is null && extractor is null && _unitExecutor is null)
        {
            return false;
        }

        // Classificazione iniziale dei root documentali (T124): preflight tecnico senza LLM, PRIMA di ogni claim. Finche un root e Pending
        // nessuna query di claim lo vede, quindi nessuna inferenza. Non e inferenza e non tiene lo slot AI; un root classificato non si riclassifica mai.
        if (classifier is not null
            && await DocumentTiming.TimeAsync("dispatch.classify", () => classifier.ClassifyNextAsync(turns: executor is not null, analysisRuns: workflow is not null, documentRuns: documentWorkflow is not null, ct)) is { } classified)
        {
            await PublishClassifiedAsync(classified);
            return true;
        }

        // Aggregazione documentale (T051): lavoro deterministico che pubblica la versione Evidence quando tutte le unità sono Completed. Ha claim e lock
        // propri e non entra nel limite AI globale (non è inferenza), quindi non tiene mai lo slot e non attende le unità; un passo per giro del ciclo.
        using (var aggregationScope = scopes.CreateScope())
        {
            if (aggregationScope.ServiceProvider.GetService<Documents.DocumentAggregationRunner>() is { } aggregation
                && await DocumentTiming.TimeAsync("dispatch.aggregate", () => aggregation.RunNextAsync(_owner, ct)) is not null)
            {
                return true;
            }
        }

        WorkClaim? work;
        using (DocumentTiming.Span("dispatch.claim"))
        using (var scope = scopes.CreateScope())
        {
            work = await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>()
                .ClaimNextWorkAsync(_owner, turns: executor is not null, runs: workflow is not null, documents: documentWorkflow is not null,
                    extractions: extractor is not null, units: _unitExecutor is not null, ct);
        }

        if (work is null)
        {
            return false;
        }

        using (DocumentTiming.Span("dispatch.recordQueue"))
        {
            await RecordQueueAsync(work, ct);
        }
        if (work.Turn is { } turn)
        {
            await RunTurnAsync(turn, ct);
        }
        else if (work.Run is { } analysis)
        {
            await RunAnalysisAsync(analysis, ct);
        }
        else if (work.Document is { } document)
        {
            await RunDocumentAsync(document, ct);
        }
        else if (work.Unit is { } unit)
        {
            using (DocumentTiming.Span("dispatch.runUnit"))
            {
                await RunUnitAsync(unit, ct);
            }
        }
        else
        {
            await RunExtractionAsync(work.Extraction!, ct);
        }

        return true;
    }

    /// <summary>Notifica lo stato (best-effort, dopo il commit) del root appena classificato: un terminale (Failed) e visibile subito, senza attendere un claim.</summary>
    private Task PublishClassifiedAsync(ClassificationResult result) =>
        result.Kind == ClassificationOwnerKind.ChatTurn ? PublishState(result.RootId) : PublishRunState(result.RootId);

    private async Task RunTurnAsync(ChatTurnClaim claim, CancellationToken ct)
    {
        await PublishState(claim.TurnId); // Running
        // Uno scope di telemetria per tentativo: le chiamate al modello vi confluiscono e si scrivono con il commit o con il rilascio.
        using var inferenceScope = InferenceScope.Begin();
        var inference = InferenceScope.Current!;
        var owner = new InferenceOwner(claim.TurnId, null, claim.AttemptCount, claim.FencingToken);
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _active[claim.TurnId] = leaseLost;
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
            _active.TryRemove(claim.TurnId, out _);
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

    /// <summary>
    /// Esegue un lavoro di estrazione delle evidenze con lo stesso ciclo, lo stesso lease e le stesse regole di retry dei run formali:
    /// l'estrattore committa da sé versione + Completed (o Failed per output non valido); gli errori del modello sono classificati qui.
    /// </summary>
    private Task RunExtractionAsync(ExtractionJobClaim claim, CancellationToken ct) => RunFormalAsync(
        "estrazione delle evidenze",
        claim.JobId,
        EvidenceLedgerStore.Owner(claim),
        (store, c) => store.RenewAsync(claim, c),
        c => extractor!.ExecuteAsync(claim, c),
        (store, failure, c) => store.ReleaseAfterFailureAsync(claim, failure, failure.Code, failure.Detail ?? "Il modello interno ha rifiutato la richiesta.", c),
        ct,
        publish: id => evidenceEvents is null ? Task.CompletedTask : evidenceEvents.PublishStateAsync(id));

    /// <summary>
    /// Esegue un'unità di estrazione documentale con lo stesso ciclo, lo stesso lease/heartbeat/fence e le stesse regole di retry dei run formali
    /// (T050): l'esecutore committa da sé dati di lavoro e chiusura dell'unità; un errore del modello o dell'infrastruttura è classificato qui
    /// (transitorio: Queued con backoff entro tentativi e tempo; permanente: Failed con la preparazione). Lo slot si libera a fine unità, quindi
    /// il claim successivo ricontrolla chat e run prima di una nuova unità. Nessun evento SSE per le unità (T064).
    /// </summary>
    private Task RunUnitAsync(DocumentUnitClaim claim, CancellationToken ct) => RunFormalAsync(
        "estrazione di un'unità documentale",
        claim.JobId,
        new InferenceOwner(null, null, claim.AttemptCount, claim.FencingToken, null, claim.JobId),
        (store, c) => store.RenewAsync(claim, c),
        c => _unitExecutor!.ExecuteAsync(claim, c),
        (store, failure, c) => store.ReleaseAfterFailureAsync(claim, failure, failure.Code, failure.Detail ?? "Il modello interno ha rifiutato la richiesta.", c),
        ct,
        publish: _ => Task.CompletedTask);

    private async Task RunFormalAsync(
        string label,
        Guid runId,
        InferenceOwner owner,
        Func<ExecutionLeaseStore, CancellationToken, Task<bool>> renew,
        Func<CancellationToken, Task> execute,
        Func<ExecutionLeaseStore, ModelFailure, CancellationToken, Task<TurnRelease>> release,
        CancellationToken ct,
        Func<Guid, Task>? publish = null)
    {
        publish ??= PublishRunState;
        using var formalSpan = DocumentTiming.Span("formal.total");
        await publish(runId); // Running
        // Lo scope è ereditato dagli executor del workflow MAF: commit e Failed del validatore vi trovano le chiamate del tentativo.
        using var inferenceScope = InferenceScope.Begin();
        var inference = InferenceScope.Current!;
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _active[runId] = leaseLost;
        using var stopHeartbeat = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(renew, runId, leaseLost, stopHeartbeat.Token);
        var completed = false;
        try
        {
            using (DocumentTiming.Span("formal.execute"))
            {
                await execute(leaseLost.Token);
            }

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
                await publish(runId);
            }
        }
        finally
        {
            _active.TryRemove(runId, out _);
            await stopHeartbeat.CancelAsync();
            await heartbeat;
        }

        if (completed)
        {
            await publish(runId); // Completed o Failed già scritto dal workflow o dall'estrattore
        }
    }


    /// <summary>
    /// Rilascio dopo un errore osservato dall'owner (o Failed di un turno terminato senza commit), nella stessa transazione delle righe
    /// di telemetria del tentativo (failed/interrupted, mai token inventati): se owner/fencing non sono più validi la transazione si
    /// annulla e non si scrive nulla. Null se la transizione è stata rifiutata.
    /// </summary>
    /// <summary>Telemetria (T064): la coda del primo claim di un root documentale. Un guasto di scrittura non blocca mai il lavoro.</summary>
    private async Task RecordQueueAsync(WorkClaim work, CancellationToken ct)
    {
        (ClassificationOwnerKind Kind, Guid Id) target = work.Turn is { } t ? (ClassificationOwnerKind.ChatTurn, t.TurnId)
            : work.Run is { } r ? (ClassificationOwnerKind.ProcessingRun, r.RunId)
            : work.Document is { } d ? (ClassificationOwnerKind.ProcessingRun, d.RunId)
            : default;
        if (target.Id == Guid.Empty)
        {
            return; // unità/estrazioni: non sono root del regime
        }

        await RecordRecoveryAsync(work, target, ct);

        try
        {
            using var scope = scopes.CreateScope();
            await ExecutionIntervalStore.RecordQueueAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), target.Kind, target.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Intervallo di coda non registrato: {ExceptionType}", ex.GetType().Name);
        }
    }

    /// <summary>Telemetria (T102): il recupero di un lease scaduto, dal suo termine al claim che riprende il root. Un guasto di scrittura non blocca mai il lavoro.</summary>
    private async Task RecordRecoveryAsync(WorkClaim work, (ClassificationOwnerKind Kind, Guid Id) target, CancellationToken ct)
    {
        var (recovery, attempt) = work.Turn is { } t ? (t.Recovery, t.AttemptCount)
            : work.Run is { } r ? (r.Recovery, r.AttemptCount)
            : work.Document is { } d ? (d.Recovery, d.AttemptCount)
            : default;
        if (recovery is not { } interval)
        {
            return;
        }

        try
        {
            using var scope = scopes.CreateScope();
            await ExecutionIntervalStore.RecordRecoveryAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), target.Kind, target.Id, attempt - 1, interval, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Intervallo di recupero non registrato: {ExceptionType}", ex.GetType().Name);
        }
    }

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
        await RecordBackoffAsync(released, owner, ct);
        return released;
    }

    /// <summary>Telemetria (T064): il backoff di un retry di un turno o run documentale, dopo il commit del rilascio. Un guasto di scrittura non blocca mai il lavoro.</summary>
    private async Task RecordBackoffAsync(TurnRelease released, InferenceOwner owner, CancellationToken ct)
    {
        if (released is not { Outcome: TurnTransition.Requeued, NextAttemptAt: { } next, Delay: { } delay })
        {
            return;
        }

        (ClassificationOwnerKind Kind, Guid Id) target = owner.ChatTurnId is { } turn ? (ClassificationOwnerKind.ChatTurn, turn)
            : owner.ProcessingRunId is { } run ? (ClassificationOwnerKind.ProcessingRun, run)
            : default;
        if (target.Id == Guid.Empty)
        {
            return;
        }

        try
        {
            using var scope = scopes.CreateScope();
            await ExecutionIntervalStore.RecordBackoffAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), target.Kind, target.Id, owner.Attempt, next, delay, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Intervallo di backoff non registrato: {ExceptionType}", ex.GetType().Name);
        }
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
