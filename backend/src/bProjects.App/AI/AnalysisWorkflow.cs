using System.Text;
using System.Text.Json;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>JSON Schema inviato al modello nella modalità JsonSchema: proiezione del DTO tipizzato AnalysisContent 1.0.0.</summary>
public static class AnalysisSchema
{
    public const string Name = "analysis_content";

    private static readonly Lazy<JsonElement> Schema = new(() =>
        AIJsonUtilities.CreateJsonSchema(typeof(AnalysisContent), serializerOptions: AnalysisContentJson.Options).Clone());

    public static JsonElement Element => Schema.Value;
}

/// <summary>Esito osservabile di un'esecuzione del workflow: lo stato di dominio del run, riletto dal database.</summary>
public sealed record AnalysisRunResult(ProcessingRunStatus Status, Guid? ResultRevisionId, string? ErrorCode);

/// <summary>
/// project-analysis-v2 (T029, contracts/ai-workflow.md §3): LoadDialog → ConversationalAgent (consolidamento JSON) →
/// ValidatePersistAnalysis, costruito soltanto con primitive native MAF 1.26.0. L'agente è un AIAgentHostExecutor
/// (ChatMessage/TurnToken, ForwardIncomingMessages=false); lo stato fra i passi vive nei checkpoint nativi su
/// PostgreSQL. Nessun DocumentAgent e nessuna logica applicativa decide il passo successivo: avvio e ripresa sono
/// <c>RunStreamingAsync</c>/<c>ResumeStreamingAsync</c>. La validazione precede il commit di revisione AI + Completed +
/// CurrentAnalysisId; un output non valido fa fallire il run senza riparazione e senza revisione.
/// </summary>
public sealed class AnalysisWorkflow(IServiceScopeFactory scopes, ConversationalAgent conversational, ChatOptions analysisOptions)
{
    public const string LoadDialogId = "LoadDialog";
    /// <summary>Id nativo dell'executor che ospita l'agente: «{Name}_{Id}» come assegnato da MAF.</summary>
    public const string ConsolidationId = $"{ConversationalAgent.AgentName}_{ConversationalAgent.ConsolidationAgentId}";
    public const string ValidatePersistId = AnalysisRunCompletion.CommitStage;

    /// <summary>Fasi reali del grafo, nell'ordine di definizione: le sole proiettabili in LastObservedStage.</summary>
    public static IReadOnlyList<string> Stages { get; } = [LoadDialogId, ConsolidationId, ValidatePersistId];

    /// <summary>Costruisce il grafo nativo. Topologia, id degli executor e id/nome dell'agente sono stabili per il resume.</summary>
    public async Task<Workflow> BuildAsync(AnalysisRunClaim claim, CancellationToken ct)
    {
        var agent = await conversational.CreateConsolidationAgentAsync(analysisOptions, ct);
        var load = new LoadDialogExecutor(scopes, claim).BindExecutor();
        var consolidate = agent.BindAsExecutor(new AIAgentHostOptions { ForwardIncomingMessages = false, ReassignOtherAgentsAsUsers = false });
        var validate = new ValidatePersistAnalysisExecutor(scopes, claim).BindExecutor();

        return new WorkflowBuilder(load)
            .AddEdge(load, consolidate)
            .AddEdge(consolidate, validate)
            .WithOutputFrom(validate)
            .Build();
    }

    /// <summary>
    /// Esegue il run per conto dell'owner del lease: avvio nativo oppure, se esiste un checkpoint del run, ripresa
    /// nativa dall'ultimo. Ritorna lo stato di dominio finale (Completed anche se il checkpoint successivo al commit
    /// fallisce, Failed per output non valido). Le eccezioni del modello o dell'infrastruttura risalgono al chiamante,
    /// che le classifica (<see cref="ModelFailure"/>) senza che questo codice ritenti nulla.
    /// </summary>
    public async Task<AnalysisRunResult> RunAsync(AnalysisRunClaim claim, Action<string>? onStage, CancellationToken ct)
    {
        var current = await ReadAsync(claim.RunId, ct);
        if (current.Status is ProcessingRunStatus.Completed or ProcessingRunStatus.Failed)
        {
            return current; // replay: un risultato terminale non si rielabora
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == claim.RunId && r.Kind == ProcessingRunKind.Analysis, ct);
        if (run.WorkflowDefinitionVersion != ProcessingRun.AnalysisWorkflowVersion || run.MafVersion != Conversation.CurrentMafVersion)
        {
            // Nessuna migrazione implicita di un vecchio grafo: errore esplicito e permanente.
            await FailAsync(claim, "workflow_incompatible", "La definizione del workflow del run non è compatibile con quella corrente.", ct);
            return await ReadAsync(claim.RunId, ct);
        }

        var sessionId = await EnsureWorkflowSessionAsync(db, claim, ct);
        var checkpoints = CheckpointManager.CreateJson(new PostgresCheckpointStore(db, new CheckpointWriter(claim.RunId, claim.LeaseOwner, claim.FencingToken)));
        var workflow = await BuildAsync(claim, ct);
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
                        // Solo fasi reali, osservate dagli eventi nativi (l'host dell'agente è invocato più volte per fase): informativo, mai un prossimo step.
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
        throw failure ?? new InvalidOperationException("Il workflow di analisi è terminato senza produrre un risultato.");
    }

    private async Task<AnalysisRunResult> ReadAsync(Guid runId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProcessingRuns.AsNoTracking()
            .SingleAsync(r => r.Id == runId && r.Kind == ProcessingRunKind.Analysis, ct);
        return new AnalysisRunResult(run.Status, run.ResultRevisionId, run.ErrorCode);
    }

    private async Task FailAsync(AnalysisRunClaim claim, string code, string message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().FailAsync(claim, code, message, ct);
    }

    private async Task RecordStageAsync(AnalysisRunClaim claim, string stage, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().RecordStageAsync(claim, stage, ct);
    }

    /// <summary>Identità MAF persistita sul run alla prima esecuzione e riusata dai tentativi successivi (stessa sessione di checkpoint).</summary>
    private static async Task<string> EnsureWorkflowSessionAsync(AppDbContext db, AnalysisRunClaim claim, CancellationToken ct)
    {
        var candidate = Guid.NewGuid().ToString("N");
        var session = (await db.Database.SqlQuery<string>($"""
            UPDATE "ProcessingRuns" SET "WorkflowSessionId" = COALESCE("WorkflowSessionId", {candidate}), "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {claim.RunId} AND "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner} AND "FencingToken" = {claim.FencingToken}
            RETURNING "WorkflowSessionId" AS "Value"
            """).ToListAsync(ct)).SingleOrDefault();
        return session ?? throw new OperationCanceledException("Lease del run non più valido.");
    }
}

/// <summary>
/// LoadDialog: legge dal database la conversazione fino al cutoff immutabile del run (mai oltre) e la consegna
/// all'agente come messaggi nativi con il loro ruolo, ciascuno con id e sequenza, più i dati affidabili del server.
/// </summary>
[SendsMessage(typeof(List<ChatMessage>))]
[SendsMessage(typeof(TurnToken))]
internal sealed class LoadDialogExecutor(IServiceScopeFactory scopes, AnalysisRunClaim claim)
    : Executor<TurnToken>(AnalysisWorkflow.LoadDialogId)
{
    public override async ValueTask HandleAsync(TurnToken message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().LoadInputAsync(claim.RunId, cancellationToken);
        if (!run.Dialog.Any(m => m.Role == AnalysisMessageRole.User))
        {
            throw new InvalidOperationException("Nessun messaggio del project manager fino al cutoff: niente da analizzare.");
        }

        var messages = run.Dialog
            .Select(m => new ChatMessage(
                m.Role == AnalysisMessageRole.User ? ChatRole.User : ChatRole.Assistant,
                $"[messaggio id={m.Id} sequenza={m.Sequence}]\n{m.Body}"))
            .ToList();
        messages.Add(new ChatMessage(ChatRole.User, new StringBuilder()
            .AppendLine("Dati affidabili del server per questa analisi:")
            .AppendLine($"- analysisId: {run.AnalysisId}")
            .AppendLine($"- projectId: {run.ProjectId}")
            .AppendLine($"- sourceCutoffSequence: {run.SourceCutoffSequence}")
            .AppendLine("Produci ora l'unico oggetto JSON AnalysisContent 1.0.0.")
            .ToString()));

        await context.SendMessageAsync(messages, cancellationToken: cancellationToken);
        await context.SendMessageAsync(message, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// ValidatePersistAnalysis: riceve la risposta dell'agente (protocollo ChatMessage/TurnToken), la valida come testo JSON
/// puro contro lo schema tipizzato e le regole semantiche con input fidato dal database, e solo se valida committa
/// atomicamente revisione AI + Completed + CurrentAnalysisId. Se non lo è, il run è Failed con codice
/// <c>invalid_agent_output</c>: nessuna revisione, nessuna riparazione, nessuna seconda chiamata al modello.
/// </summary>
internal sealed class ValidatePersistAnalysisExecutor(IServiceScopeFactory scopes, AnalysisRunClaim claim)
    : ChatProtocolExecutor(AnalysisWorkflow.ValidatePersistId, new ChatProtocolExecutorOptions { AutoSendTurnToken = false })
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).YieldsOutput<string>();

    protected override async ValueTask TakeTurnAsync(
        List<ChatMessage> messages, IWorkflowContext context, bool? emitEvents, CancellationToken cancellationToken = default)
    {
        // Solo il testo dell'assistente: il ragionamento (TextReasoningContent) non è testo e non entra mai nel contenuto.
        var text = string.Concat(messages.Where(m => m.Role == ChatRole.Assistant).Select(m => m.Text));

        using var scope = scopes.CreateScope();
        var committer = scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>();
        var input = await committer.LoadInputAsync(claim.RunId, cancellationToken);
        var validation = AnalysisContentValidator.ValidateText(text, input.ToValidationContext(), AnalysisActorKind.Ai);

        if (!validation.Accepted)
        {
            var summary = $"L'output del modello non è un'analisi valida ({validation.Violations.Count} violazioni): " +
                string.Join(" | ", validation.Violations.Take(5));
            await committer.FailAsync(claim, validation.Code ?? AnalysisContentValidator.InvalidAgentOutput, summary, cancellationToken);
            return;
        }

        var commit = await committer.CommitAsync(claim, validation.Content!, cancellationToken);
        if (commit.Outcome == AnalysisCommitOutcome.Rejected)
        {
            throw new InvalidOperationException($"Commit dell'analisi rifiutato ({commit.Reason}): il lease non è più valido.");
        }

        await context.YieldOutputAsync(commit.RevisionId!.Value.ToString(), cancellationToken);
    }
}
