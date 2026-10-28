using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>Esito osservabile di un'esecuzione del workflow: lo stato di dominio del run, riletto dal database.</summary>
public sealed record DocumentRunResult(ProcessingRunStatus Status, Guid? DraftId, string? ErrorCode);

/// <summary>
/// project-document-v1 (T040, contracts/ai-workflow.md §3): LoadConfirmedRevision → DocumentAgent → ValidatePersistDraft, costruito
/// soltanto con primitive native MAF 1.26.0 e nessuna orchestrazione parallela. L'agente è un AIAgentHostExecutor
/// (ChatMessage/TurnToken, ForwardIncomingMessages=false) con id e nome stabili; sessione e checkpoint sono quelli del ProcessingRun
/// Document, distinti da chat e analisi. Avvio e ripresa sono <c>RunStreamingAsync</c>/<c>ResumeStreamingAsync</c>: nessun campo di
/// dominio decide il passo successivo. La validazione (formato, fonti, revisione) precede il commit unico e fenced di bozza +
/// Completed + LatestDraftId; un output non valido fa fallire il run senza riparazione e senza bozza. Un commit riuscito prevale su
/// un guasto successivo del checkpoint (il lease è già rilasciato): il run resta Completed.
/// </summary>
public sealed class DocumentWorkflow(IServiceScopeFactory scopes, DocumentAgent documentAgent)
{
    public const string LoadId = "LoadConfirmedRevision";

    /// <summary>Id nativo dell'executor che ospita l'agente: «{Name}_{Id}» come assegnato da MAF.</summary>
    public const string GenerationId = DocumentAgent.HostExecutorId;

    public const string ValidatePersistId = DocumentRunCompletion.CommitStage;

    /// <summary>Fasi reali del grafo, nell'ordine di definizione: le sole proiettabili in LastObservedStage.</summary>
    public static IReadOnlyList<string> Stages { get; } = [LoadId, GenerationId, ValidatePersistId];

    /// <summary>Costruisce il grafo nativo. Topologia, id degli executor e id/nome dell'agente sono stabili per il resume.</summary>
    public Workflow Build(DocumentRunClaim claim)
    {
        var load = new LoadConfirmedRevisionExecutor(scopes, documentAgent, claim).BindExecutor();
        var generate = documentAgent.CreateAgent().BindAsExecutor(new AIAgentHostOptions { ForwardIncomingMessages = false, ReassignOtherAgentsAsUsers = false });
        var validate = new ValidatePersistDraftExecutor(scopes, claim).BindExecutor();

        return new WorkflowBuilder(load)
            .AddEdge(load, generate)
            .AddEdge(generate, validate)
            .WithOutputFrom(validate)
            .Build();
    }

    /// <summary>
    /// Esegue il run per conto dell'owner del lease: avvio nativo oppure, se esiste un checkpoint del run, ripresa nativa
    /// dall'ultimo. Ritorna lo stato di dominio finale (Completed anche se il checkpoint successivo al commit fallisce, Failed per
    /// output non valido o contesto oltre budget). Le eccezioni del modello o dell'infrastruttura risalgono al chiamante, che le
    /// classifica (<see cref="ModelFailure"/>) senza che questo codice ritenti nulla.
    /// </summary>
    public async Task<DocumentRunResult> RunAsync(DocumentRunClaim claim, Action<string>? onStage, CancellationToken ct)
    {
        var current = await ReadAsync(claim.RunId, ct);
        if (current.Status is ProcessingRunStatus.Completed or ProcessingRunStatus.Failed)
        {
            return current; // replay: un risultato terminale non si rielabora
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == claim.RunId && r.Kind == ProcessingRunKind.Document, ct);
        if (run.WorkflowDefinitionVersion != ProcessingRun.DocumentWorkflowVersion || run.MafVersion != Conversation.CurrentMafVersion
            || !run.PromptVersionMatches("document", DocumentAgent.PromptVersion))
        {
            // Nessuna migrazione implicita di un vecchio grafo: errore esplicito e permanente.
            await FailAsync(claim, "workflow_incompatible", "La definizione del workflow del run non è compatibile con quella corrente.", ct);
            return await ReadAsync(claim.RunId, ct);
        }

        var sessionId = await EnsureWorkflowSessionAsync(db, claim, ct);
        var checkpoints = CheckpointManager.CreateJson(new PostgresCheckpointStore(db, new CheckpointWriter(claim.RunId, claim.LeaseOwner, claim.FencingToken)));
        var workflow = Build(claim);
        var latest = await checkpoints.GetLatestCheckpointAsync(sessionId, ct);

        Exception? failure = null;
        string? lastStage = null;
        try
        {
            await using var streaming = latest is null
                ? await InProcessExecution.RunStreamingAsync(workflow, new TurnToken(emitEvents: false), checkpoints, sessionId, ct)
                : await InProcessExecution.ResumeStreamingAsync(workflow, latest, checkpoints, ct);

            await foreach (var evt in streaming.WatchStreamAsync(ct))
            {
                switch (evt)
                {
                    case ExecutorInvokedEvent invoked when Stages.Contains(invoked.ExecutorId) && invoked.ExecutorId != lastStage:
                        // Solo fasi reali, osservate dagli eventi nativi: informativo, mai un prossimo step.
                        lastStage = invoked.ExecutorId;
                        await RecordStageAsync(claim, invoked.ExecutorId, ct);
                        onStage?.Invoke(invoked.ExecutorId);
                        break;
                    case WorkflowErrorEvent error:
                        failure ??= error.Exception;
                        break;
                    case ExecutorFailedEvent failed:
                        failure ??= failed.Data as Exception;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            failure ??= ex;
        }

        // Il risultato di dominio vince sul resto: se il commit è riuscito, un guasto del checkpoint successivo
        // (il lease è già stato rilasciato) non lo degrada, e un Failed registrato dalla validazione non è un errore.
        var result = await ReadAsync(claim.RunId, ct);
        if (result.Status is ProcessingRunStatus.Completed or ProcessingRunStatus.Failed)
        {
            return result;
        }

        ct.ThrowIfCancellationRequested();
        throw failure ?? new InvalidOperationException("Il workflow di generazione è terminato senza produrre un risultato.");
    }

    private async Task<DocumentRunResult> ReadAsync(Guid runId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProcessingRuns.AsNoTracking()
            .SingleAsync(r => r.Id == runId && r.Kind == ProcessingRunKind.Document, ct);
        return new DocumentRunResult(run.Status, run.DraftId, run.ErrorCode);
    }

    private async Task FailAsync(DocumentRunClaim claim, string code, string message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDocumentRunStore>().FailAsync(claim, code, message, ct);
    }

    private async Task RecordStageAsync(DocumentRunClaim claim, string stage, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDocumentRunStore>().RecordStageAsync(claim, stage, ct);
    }

    /// <summary>Identità MAF persistita sul run alla prima esecuzione e riusata dai tentativi successivi (stessa sessione di checkpoint).</summary>
    private static async Task<string> EnsureWorkflowSessionAsync(AppDbContext db, DocumentRunClaim claim, CancellationToken ct)
    {
        var candidate = Guid.NewGuid().ToString("N");
        var session = (await db.Database.SqlQuery<string>($"""
            UPDATE "ProcessingRuns" SET "WorkflowSessionId" = COALESCE("WorkflowSessionId", {candidate}), "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {claim.RunId} AND "Kind" = 'Document' AND "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner} AND "FencingToken" = {claim.FencingToken}
            RETURNING "WorkflowSessionId" AS "Value"
            """).ToListAsync(ct)).SingleOrDefault();
        return session ?? throw new OperationCanceledException("Lease del run non più valido.");
    }
}

/// <summary>
/// LoadConfirmedRevision: legge dal database la revisione confermata congelata nel run (InputRevisionId, mai una più recente),
/// esegue il preflight dei token sull'intero contenuto e la consegna all'agente come unico messaggio utente. Oltre il budget il run
/// fallisce con <c>context_overflow</c> prima di qualsiasi chiamata al modello: nessuna omissione, nessun documento parziale.
/// </summary>
[SendsMessage(typeof(List<ChatMessage>))]
[SendsMessage(typeof(TurnToken))]
internal sealed class LoadConfirmedRevisionExecutor(IServiceScopeFactory scopes, DocumentAgent agent, DocumentRunClaim claim)
    : Executor<TurnToken>(DocumentWorkflow.LoadId)
{
    public override async ValueTask HandleAsync(TurnToken message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentRunStore>();
        DocumentRunInput input;
        try
        {
            input = await store.LoadInputAsync(claim.RunId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // Input incoerente (revisione non confermata o di un'altra analisi): errore permanente, nessuna inferenza.
            await store.FailAsync(claim, "processing_failed", ex.Message, cancellationToken);
            return;
        }

        var messages = DocumentAgent.BuildMessages(new DocumentInput(input.ProjectId, input.AnalysisId, input.RevisionId, input.Version, input.Content));
        try
        {
            await agent.PreflightAsync(messages, cancellationToken);
        }
        catch (ContextOverflowException overflow)
        {
            await store.FailAsync(claim, overflow.Code, "Il contenuto confermato non rientra nel budget di contesto del modello: nessuna generazione.", cancellationToken);
            return;
        }

        await context.SendMessageAsync(messages, cancellationToken: cancellationToken);
        await context.SendMessageAsync(message, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// ValidatePersistDraft: riceve la risposta dell'agente (protocollo ChatMessage/TurnToken), la valida come Markdown con le fonti della
/// revisione di input fidata dal database e solo se valida committa atomicamente bozza + Completed + LatestDraftId. Se non lo è, il
/// run è Failed con codice <c>invalid_agent_output</c>: nessuna bozza, nessuna riparazione, nessuna seconda chiamata al modello.
/// </summary>
internal sealed class ValidatePersistDraftExecutor(IServiceScopeFactory scopes, DocumentRunClaim claim)
    : ChatProtocolExecutor(DocumentWorkflow.ValidatePersistId, new ChatProtocolExecutorOptions { AutoSendTurnToken = false })
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).YieldsOutput<string>();

    protected override async ValueTask TakeTurnAsync(
        List<ChatMessage> messages, IWorkflowContext context, bool? emitEvents, CancellationToken cancellationToken = default)
    {
        // Solo il testo dell'assistente: il ragionamento (TextReasoningContent) non è testo e non entra mai nel documento.
        var text = string.Concat(messages.Where(m => m.Role == ChatRole.Assistant).Select(m => m.Text));

        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentRunStore>();
        var input = await store.LoadInputAsync(claim.RunId, cancellationToken);
        var validation = DocumentDraftValidator.Validate(text, input.ToValidationContext());

        if (!validation.Accepted)
        {
            var summary = $"L'output del modello non è una bozza valida ({validation.Violations.Count} violazioni): " +
                string.Join(" | ", validation.Violations.Take(5));
            await store.FailAsync(claim, DocumentDraftValidator.InvalidAgentOutput, summary, cancellationToken);
            return;
        }

        var commit = await store.CommitAsync(claim, text.Trim(), cancellationToken);
        if (commit.Outcome == DocumentCommitOutcome.Rejected)
        {
            if (commit.Reason == "lease_lost")
            {
                throw new LeaseLostException("Commit della bozza rifiutato (lease_lost): il lease non è più valido.");
            }

            throw new InvalidOperationException($"Commit della bozza rifiutato ({commit.Reason}).");
        }

        await context.YieldOutputAsync(commit.DraftId!.Value.ToString(), cancellationToken);
    }
}
