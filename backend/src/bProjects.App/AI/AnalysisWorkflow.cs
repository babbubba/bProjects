using System.Text;
using System.Text.Json;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Evidence;
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
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            AIJsonUtilities.CreateJsonSchema(typeof(AnalysisContent), serializerOptions: AnalysisContentJson.Options).GetRawText())!;

        // schemaVersion ha un convertitore dedicato, quindi il generatore emette lo schema «true» (qualunque valore),
        // che il runtime llama.cpp rifiuta con 400 («Unrecognized schema: true»): lo si vincola alla sola versione corrente.
        node["properties"]!["schemaVersion"] = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "string",
            ["enum"] = new System.Text.Json.Nodes.JsonArray(AnalysisContent.CurrentSchemaVersion),
        };

        // Il runtime ignora format "uuid" (stringa libera: il modello inventava id come «g1a2…»): si aggiunge un pattern esplicito.
        ConstrainUuidFormats(node);

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    });

    public static JsonElement Element => Schema.Value;

    /// <summary>
    /// Schema del singolo run: i valori che il modello deve soltanto copiare sono vincolati a quelli fidati del server, così la
    /// grammatica di llama.cpp non può alterarli. analysisId, projectId e sourceCutoffSequence ammettono un solo valore; id e
    /// messageId delle fonti ed evidenceRefs ammettono solo gli id dei messaggi fino al cutoff (una fonte Message ha l'id del suo
    /// messaggio). Sul DGX, con il solo pattern, un id copiato perdeva un carattere e il pattern spostava i trattini
    /// (analysisId diverso dal server). La validazione resta invariata e completa.
    /// </summary>
    public static JsonElement ForRun(AnalysisRunInput input)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Element.GetRawText())!;
        var properties = node["properties"]!;
        properties["analysisId"] = Only("string", input.AnalysisId.ToString());
        properties["projectId"] = Only("string", input.ProjectId.ToString());
        properties["sourceCutoffSequence"] = Only("integer", input.SourceCutoffSequence);
        // Le fonti sono i soli messaggi del PM (quelli dell'assistente sono contesto e non provano fatti): la grammatica elenca una
        // fonte per messaggio, con id, messageId e ruolo già determinati, così il modello non può alterare un id né attribuire a un
        // messaggio dell'assistente il ruolo user (osservato: tutte le fonti «user», messageRole incoerente e run invalid_agent_output).
        var users = input.Dialog.Where(m => m.Role == AnalysisMessageRole.User).ToList();
        if (users.Count > 0)
        {
            var messages = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "string",
                ["enum"] = new System.Text.Json.Nodes.JsonArray([.. users.Select(m => System.Text.Json.Nodes.JsonValue.Create(m.Id.ToString()))]),
            };
            properties["sources"] = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "array",
                ["prefixItems"] = new System.Text.Json.Nodes.JsonArray([.. users.Select(m => (System.Text.Json.Nodes.JsonNode)MessageSource(m.Id))]),
                ["minItems"] = users.Count,
                ["maxItems"] = users.Count,
            };
            ConstrainEvidence(node, messages);
        }

        AddEvidenceKeys(node, input.Evidence?.Aliases ?? []);
        Reorder(node);
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// La grammatica impone l'ordine delle proprietà dello schema, e il modello scrive nell'ordine in cui le trova: conflitti, lacune
    /// ed esclusioni precedono le sezioni dei fatti, così quando compila requisiti e vincoli ha già collocato altrove le posizioni
    /// in conflitto, ciò che il PM non sa e ciò che esclude (osservato: le stesse affermazioni ripetute come requisiti o vincoli e
    /// negazioni finite nei vincoli). L'ordine non cambia il contenuto: il validatore e il DTO non dipendono dall'ordine.
    /// </summary>
    private static void Reorder(System.Text.Json.Nodes.JsonNode node)
    {
        string[] root = ["schemaVersion", "analysisId", "projectId", "sourceCutoffSequence", "validationState", "sources", "goals", "conflicts",
            "openQuestions", "contextAndScope", "requirements", "operationalFlows", "constraints", "dependencies", "assumptions"];
        Sort(node["properties"]!.AsObject(), root);
        if (node["properties"]!["contextAndScope"]?["properties"] is System.Text.Json.Nodes.JsonObject scope)
        {
            Sort(scope, ["excluded", "overview", "included"]);
        }
    }

    private static void Sort(System.Text.Json.Nodes.JsonObject properties, string[] order)
    {
        var entries = properties.ToList();
        properties.Clear();
        foreach (var name in order.Concat(entries.Select(p => p.Key).Except(order)))
        {
            if (entries.FirstOrDefault(p => p.Key == name) is { Value: { } value })
            {
                properties[name] = value;
            }
        }
    }

    /// <summary>Fonte Message di un messaggio del PM con tutti i campi determinati (stessa forma e ordine di <see cref="AnalysisSource"/>).</summary>
    private static System.Text.Json.Nodes.JsonObject MessageSource(Guid messageId) => new()
    {
        ["type"] = "object",
        ["properties"] = new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = Only("string", messageId.ToString()),
            ["kind"] = Only("string", nameof(AnalysisSourceKind.Message)),
            ["messageId"] = Only("string", messageId.ToString()),
            ["messageRole"] = Only("string", "user"),
            ["revisionId"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "null" },
        },
        ["required"] = new System.Text.Json.Nodes.JsonArray("id", "kind", "messageId", "messageRole", "revisionId"),
        ["additionalProperties"] = false,
    };

    private static System.Text.Json.Nodes.JsonObject Only<T>(string type, T value) => new()
    {
        ["type"] = type,
        ["enum"] = new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonValue.Create(value)),
    };

    private static void ConstrainEvidence(System.Text.Json.Nodes.JsonNode? node, System.Text.Json.Nodes.JsonObject messages)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                if (obj["evidenceRefs"] is System.Text.Json.Nodes.JsonObject evidence && evidence["items"] is not null)
                {
                    evidence["items"] = messages.DeepClone();
                }

                foreach (var child in obj.Select(p => p.Value).ToList())
                {
                    ConstrainEvidence(child, messages);
                }

                break;
            case System.Text.Json.Nodes.JsonArray array:
                foreach (var child in array)
                {
                    ConstrainEvidence(child, messages);
                }

                break;
        }
    }

    /// <summary>Proprietà interna della mappa elemento→evidenze: rimossa prima della validazione, mai persistita in AnalysisContent.</summary>
    public const string EvidenceKeysProperty = "evidenceKeys";

    /// <summary>
    /// Ogni oggetto elemento (id, text, classification, evidenceRefs) riceve <c>evidenceKeys</c> obbligatorio: alias della vista congelata
    /// del run, vincolati in enum dalla grammatica. La risoluzione dei conflitti (senza testo) non ne riceve.
    /// </summary>
    private static void AddEvidenceKeys(System.Text.Json.Nodes.JsonNode? node, IReadOnlyList<string> aliases)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                if (obj["properties"] is System.Text.Json.Nodes.JsonObject props && props.ContainsKey("evidenceRefs") && props.ContainsKey("text")
                    && !props.ContainsKey(EvidenceKeysProperty))
                {
                    props[EvidenceKeysProperty] = aliases.Count == 0
                        ? new System.Text.Json.Nodes.JsonObject { ["type"] = "array", ["maxItems"] = 0 }
                        : new System.Text.Json.Nodes.JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new System.Text.Json.Nodes.JsonArray([.. aliases.Select(a => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(a)!)]),
                            },
                        };
                    if (obj["required"] is System.Text.Json.Nodes.JsonArray required)
                    {
                        required.Add(EvidenceKeysProperty);
                    }
                }

                foreach (var child in obj.Select(p => p.Value).ToList())
                {
                    AddEvidenceKeys(child, aliases);
                }

                break;
            case System.Text.Json.Nodes.JsonArray array:
                foreach (var child in array)
                {
                    AddEvidenceKeys(child, aliases);
                }

                break;
        }
    }

    /// <summary>
    /// Separa la mappa elemento→evidenze dal JSON del modello: toglie <c>evidenceKeys</c> da ogni elemento e restituisce il testo
    /// AnalysisContent da validare. Un testo non JSON resta invariato (lo rifiuterà la validazione). Alias non presenti nella vista:
    /// violazioni (con la grammatica attiva non possono comparire).
    /// </summary>
    public static (string Content, Dictionary<Guid, IReadOnlyList<string>> Keys, List<string> Violations) SplitEvidenceKeys(string text, IReadOnlyCollection<string> aliases)
    {
        var keys = new Dictionary<Guid, IReadOnlyList<string>>();
        var violations = new List<string>();
        System.Text.Json.Nodes.JsonNode? root;
        try
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(text.Trim());
        }
        catch (JsonException)
        {
            return (text, keys, violations);
        }

        void Walk(System.Text.Json.Nodes.JsonNode? node)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonObject obj:
                    if (obj.ContainsKey(EvidenceKeysProperty))
                    {
                        var raw = obj[EvidenceKeysProperty];
                        obj.Remove(EvidenceKeysProperty);
                        if (obj["id"] is System.Text.Json.Nodes.JsonValue idValue && idValue.TryGetValue<string>(out var idText) && Guid.TryParse(idText, out var id)
                            && raw is System.Text.Json.Nodes.JsonArray list)
                        {
                            var found = new List<string>();
                            foreach (var entry in list)
                            {
                                if (entry is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var alias) && aliases.Contains(alias))
                                {
                                    found.Add(alias);
                                }
                                else
                                {
                                    violations.Add($"evidenceKeys dell'elemento {idText}: alias «{entry}» non presente nel registro del run");
                                }
                            }

                            keys[id] = [.. found.Distinct()];
                        }
                        else if (raw is not null and not System.Text.Json.Nodes.JsonArray)
                        {
                            violations.Add("evidenceKeys deve essere un array di alias");
                        }
                    }

                    foreach (var child in obj.Select(p => p.Value).ToList())
                    {
                        Walk(child);
                    }

                    break;
                case System.Text.Json.Nodes.JsonArray array:
                    foreach (var child in array)
                    {
                        Walk(child);
                    }

                    break;
            }
        }

        Walk(root);
        return (root?.ToJsonString() ?? text, keys, violations);
    }

    private const string UuidPattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";

    private static void ConstrainUuidFormats(System.Text.Json.Nodes.JsonNode? node)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                if (obj["format"]?.GetValue<string>() == "uuid")
                {
                    obj["pattern"] = UuidPattern;
                }

                foreach (var child in obj.Select(p => p.Value).ToList())
                {
                    ConstrainUuidFormats(child);
                }

                break;
            case System.Text.Json.Nodes.JsonArray array:
                foreach (var child in array)
                {
                    ConstrainUuidFormats(child);
                }

                break;
        }
    }
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
        // Lo schema dipende solo dall'input immutabile del run (cutoff fisso): avvio e ripresa costruiscono lo stesso agente.
        AnalysisRunInput input;
        using (var scope = scopes.CreateScope())
        {
            input = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().LoadInputAsync(claim.RunId, ct);
        }

        var options = InternalModelClient.WithJsonSchema(analysisOptions, AnalysisSchema.ForRun(input), AnalysisSchema.Name);
        // Passo singolo (grafo invariato) o consolidamento incrementale: un agente nativo per passo, separati da un executor che valida
        // l'analisi corrente e consegna il passo successivo. Il piano dipende solo dall'input congelato e dalle opzioni: stessa topologia al resume.
        var plan = await AnalysisConsolidation.PlanAsync(input, conversational.Factory, ct);
        var load = new LoadDialogExecutor(scopes, claim, plan).BindExecutor();
        var validate = new ValidatePersistAnalysisExecutor(scopes, claim).BindExecutor();
        var agents = new List<ExecutorBinding>();
        for (var step = 1; step <= plan.Steps.Count; step++)
        {
            var agent = await conversational.CreateConsolidationAgentAsync(options, ct, step);
            agents.Add(agent.BindAsExecutor(new AIAgentHostOptions { ForwardIncomingMessages = false, ReassignOtherAgentsAsUsers = false }));
        }

        var builder = new WorkflowBuilder(load).AddEdge(load, agents[0]);
        for (var step = 1; step < plan.Steps.Count; step++)
        {
            var next = new ConsolidationStepExecutor(scopes, claim, plan, step).BindExecutor();
            builder.AddEdge(agents[step - 1], next).AddEdge(next, agents[step]);
        }

        return builder
            .AddEdge(agents[^1], validate)
            .WithOutputFrom(validate)
            .Build();
    }

    /// <summary>Fase osservabile di un executor: gli agenti e i passi del consolidamento incrementale sono tutti «consolidamento».</summary>
    private static string? StageOf(string executorId) =>
        Stages.Contains(executorId) ? executorId
        : executorId.StartsWith(ConsolidationId + "-", StringComparison.Ordinal) || executorId.StartsWith("ConsolidationStep", StringComparison.Ordinal) ? ConsolidationId
        : null;

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
        if (run.WorkflowDefinitionVersion != ProcessingRun.AnalysisWorkflowVersion || run.MafVersion != Conversation.CurrentMafVersion
            || !run.PromptVersionMatches("analysis", ConversationalAgent.ConsolidationPromptVersion))
        {
            // Nessuna migrazione implicita di un vecchio grafo: errore esplicito e permanente.
            await FailAsync(claim, "workflow_incompatible", "La definizione del workflow del run non è compatibile con quella corrente.", ct);
            return await ReadAsync(claim.RunId, ct);
        }

        // Registro delle evidenze (contracts/ai-workflow.md §10.6): la selezione si congela al primo tentativo, prima di costruire il grafo
        // (lo schema del run dipende dagli alias); i tentativi successivi e il resume la rileggono identica.
        SelectionResult selection;
        using (var evidenceScope = scopes.CreateScope())
        {
            selection = await evidenceScope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().FreezeEvidenceAsync(claim, ct);
        }

        switch (selection.Outcome)
        {
            case SelectionOutcome.Rejected:
                // Come EnsureWorkflowSessionAsync: un run senza lease valido non è nostro e non tocca il modello.
                throw new OperationCanceledException("Selezione delle evidenze rifiutata: il lease del run non è più valido.");
            case SelectionOutcome.Missing:
                await FailAsync(claim, EvidenceLedgerStore.EvidenceUnavailable,
                    "Estrazione delle evidenze non disponibile per i messaggi PM di sequenza " + string.Join(", ", selection.MissingSequences!)
                    + ": ritentare l'estrazione e avviare una nuova analisi.", ct);
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
                    case ExecutorInvokedEvent invoked when StageOf(invoked.ExecutorId) is { } stage && stage != lastStage:
                        // Solo fasi reali, osservate dagli eventi nativi (l'host dell'agente è invocato più volte per fase): informativo, mai un prossimo step.
                        lastStage = stage;
                        await RecordStageAsync(claim, stage, ct);
                        onStage?.Invoke(stage);
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
internal sealed class LoadDialogExecutor(IServiceScopeFactory scopes, AnalysisRunClaim claim, ConsolidationPlan plan)
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

        // Passo singolo: tutto il dialogo e la vista completa nel messaggio finale; incrementale: il primo intervallo e la sua parte del registro.
        var messages = AnalysisConsolidation.Messages(run, plan, step: 1, prior: null);
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
        // La mappa elemento→evidenze (evidenceKeys) è interna: separata prima della validazione, AnalysisContent 1.0.0 resta il contratto.
        var (content, keys, keyViolations) = AnalysisSchema.SplitEvidenceKeys(text, input.Evidence?.Aliases ?? []);
        var validation = AnalysisContentValidator.ValidateText(content, input.ToValidationContext(), AnalysisActorKind.Ai);
        // Consolidamento incrementale: l'ultimo passo non può perdere le evidenze già rappresentate dai precedenti (stato nativo del workflow).
        var lost = AnalysisConsolidation.Lost(
            await context.ReadStateAsync<List<string>>(AnalysisConsolidation.RepresentedKey, AnalysisConsolidation.StateScope, cancellationToken), keys);
        if (lost.Count > 0)
        {
            keyViolations.Add(AnalysisConsolidation.LostViolation(lost));
        }

        if (!validation.Accepted || keyViolations.Count > 0)
        {
            var violations = validation.Violations.Concat(keyViolations).ToList();
            var summary = $"L'output del modello non è un'analisi valida ({violations.Count} violazioni): " +
                string.Join(" | ", violations.Take(5));
            await committer.FailAsync(claim, validation.Code ?? AnalysisContentValidator.InvalidAgentOutput, summary, cancellationToken);
            return;
        }

        var commit = await committer.CommitAsync(claim, validation.Content!, keys, cancellationToken);
        if (commit.Outcome == AnalysisCommitOutcome.Rejected)
        {
            if (commit.Reason == "lease_lost")
            {
                throw new LeaseLostException("Commit dell'analisi rifiutato (lease_lost): il lease non è più valido.");
            }

            throw new InvalidOperationException($"Commit dell'analisi rifiutato ({commit.Reason}): il lease non è più valido.");
        }

        await context.YieldOutputAsync(commit.RevisionId!.Value.ToString(), cancellationToken);
    }
}
