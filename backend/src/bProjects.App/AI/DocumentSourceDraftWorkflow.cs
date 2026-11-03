using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace bProjects.App.AI;

/// <summary>
/// project-document-documents-v2 (002 T061, contracts/ai-workflow.md §4.5 «generazione dai soli item integrali confermati»): un run Document
/// con revisione confermata 1.1.0 percorre il grafo fisso T043 con il port T044 (guard, riserva chiamate, lookup/recovery, commit fenced
/// dell'output) e UN solo batch: l'intera revisione confermata come unico messaggio (nessun summarizer, nessuna compattazione) e risposta chiusa
/// <c>{sectionKey,blocks:[{itemIds,markdown,sourceIds}]}</c>. La risposta e validata contro il pacchetto fidato della revisione
/// (<see cref="DocumentDraftValidator.ValidateBlocks"/>) PRIMA del WorkOutput; un output invalido e errore permanente senza riparazione. Il commit
/// finale e atomico (<see cref="DocumentRunCompletion.CommitAsync"/>): Draft Proposed/NotApproved + Completed + LatestDraftId, legato alla
/// revisione congelata all'avvio del run, mai a una successiva. Oltre la capacita di una chiamata: errore esplicito (unita editoriali = T085).
/// Registrazione DI e instradamento sulla definizione congelata sono di T123.
/// </summary>
public sealed class DocumentSourceDraftWorkflow(
    IServiceScopeFactory scopes, AgentFactory factory, InternalModelClient client, IOptions<DocumentOptions> documents)
{
    public const string AgentId = "document-draft";

    /// <summary>Versione del prompt congelata sul run alla richiesta (T057, <see cref="SourceSnapshotStore.DocumentPromptVersion"/>); il file e <c>document-draft-v2.md</c>.</summary>
    public const string PromptVersion = SourceSnapshotStore.DocumentPromptVersion;

    public const string DefinitionVersion = SourceSnapshotStore.DocumentDefinition;
    public const string AlgorithmVersion = "draft-single-v1";
    public const string BatchStage = "draft";
    public const string SchemaName = "document_draft";
    public const string SchemaVersion = "document-draft-1";
    public const string ValidationVersion = "document-draft-validation-v1";

    private static readonly Lazy<string> Prompt = new(() => ConversationalAgent.LoadPrompt("document-draft-v2.md"));

    private static readonly JsonSerializerOptions PromptJson = new(DocumentAnalysisContentJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Schema vincolante dell'output chiuso; l'accettazione e comunque del validatore del server.</summary>
    private static readonly JsonElement OutputSchema = JsonDocument.Parse(
        """
        {"type":"object","additionalProperties":false,"required":["sectionKey","blocks"],
         "properties":{"sectionKey":{"type":"string","minLength":1,"maxLength":64},
          "blocks":{"type":"array","minItems":1,"items":{"type":"object","additionalProperties":false,"required":["itemIds","markdown","sourceIds"],
           "properties":{"itemIds":{"type":"array","minItems":1,"items":{"type":"string"}},"markdown":{"type":"string","minLength":1},"sourceIds":{"type":"array","items":{"type":"string"}}}}}}}
        """).RootElement.Clone();

    public static string Instructions => Prompt.Value;

    public static string SessionId(Guid runId) => $"document-{runId:N}";

    public async Task<DocumentRunResult> ExecuteAsync(DocumentRunClaim claim, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == claim.RunId && r.Kind == ProcessingRunKind.Document, ct);
        if (run.Status is ProcessingRunStatus.Completed or ProcessingRunStatus.Failed)
        {
            return new(run.Status, run.DraftId, run.ErrorCode); // replay: un risultato terminale non si rielabora
        }

        if (run.WorkflowDefinitionVersion != DefinitionVersion || run.MafVersion != Conversation.CurrentMafVersion || !run.PromptVersionMatches("document", PromptVersion))
        {
            // Nessuna migrazione implicita di un altro grafo: errore esplicito e permanente (T123 instrada le definizioni supportate).
            await FailAsync(claim, "workflow_incompatible", "La definizione del workflow del run non e compatibile con quella documentale corrente.", ct);
            return await ReadAsync(claim.RunId, ct);
        }

        try
        {
            await RunGraphAsync(db, claim, ct);
        }
        catch (Exception ex) when (Find<DocumentWorkException>(ex) is { } work)
        {
            await FailAsync(claim, work.Code, work.Message, ct);
        }

        var result = await ReadAsync(claim.RunId, ct);
        return result.Status is ProcessingRunStatus.Completed or ProcessingRunStatus.Failed
            ? result
            : throw new InvalidOperationException("Il workflow di generazione documentale e terminato senza produrre un risultato.");
    }

    /// <summary>
    /// Payload e schema della generazione per il preflight di classificazione del regime (T124): stessa composizione dell'invio, nessuna chiamata al
    /// modello. L'input incoerente (revisione non confermata, di un'altra analisi o non 1.1.0) è un errore permanente, come all'esecuzione.
    /// </summary>
    public async Task<RegimePayload> BuildRegimePayloadAsync(Guid runId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        DocumentRunInputV11 input;
        try
        {
            input = await new DocumentRunCompletion(scope.ServiceProvider.GetRequiredService<AppDbContext>()).LoadDocumentInputAsync(runId, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new DocumentWorkException("processing_failed", ex.Message);
        }

        var structured = factory.Options.StructuredOutputMode == StructuredOutputMode.JsonSchema;
        var payload = new DocumentPayload(AiPhase.Document, Instructions, BuildMessages(input), structured ? OutputSchema.GetRawText() : null);
        return new RegimePayload(AiPhase.Document, payload, OutputSchema.GetRawText(), factory.Preflight);
    }

    private async Task RunGraphAsync(AppDbContext db, DocumentRunClaim claim, CancellationToken ct)
    {
        DocumentRunInputV11 input;
        try
        {
            input = await new DocumentRunCompletion(db).LoadDocumentInputAsync(claim.RunId, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Input incoerente (revisione non confermata, di un'altra analisi o non 1.1.0): errore permanente, nessuna inferenza.
            throw new DocumentWorkException("processing_failed", ex.Message);
        }

        var structured = factory.Options.StructuredOutputMode == StructuredOutputMode.JsonSchema;
        var instructions = Instructions;
        var messages = BuildMessages(input);
        var measure = await factory.Preflight.MeasureAsync(
            new DocumentPayload(AiPhase.Document, instructions, messages, structured ? OutputSchema.GetRawText() : null), ct);
        if (measure.Quality == TokenCountQuality.Unavailable)
        {
            throw new RuntimeUnverifiedException("conteggio del payload della generazione non disponibile (nessun tokenizer o calibrazione identificata)");
        }

        if (!measure.Fits)
        {
            // Mai una compattazione della revisione confermata: oltre la capacita di una chiamata servono le unita editoriali (T085).
            throw new DocumentWorkException(
                DocumentWorkException.CallLimitExceeded,
                $"La revisione confermata ({measure.Tokens} token) supera la capacita della generazione a singola chiamata ({measure.InputBudgetTokens} token): la generazione per unita editoriali non e ancora disponibile.");
        }

        using var scope = scopes.CreateScope();
        var planDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var writer = new CheckpointWriter(claim.RunId, claim.LeaseOwner, claim.FencingToken);
        var store = new DocumentWorkStore(planDb, writer);
        var plan = new DocumentDraftPlan(claim, input, messages, instructions, measure, structured, factory, client, OutputSchema, scopes);
        var port = new DocumentBatchPort(store, planDb, writer, factory, plan, new RunCallReservation(planDb, writer, documents.Value.MaxAiCallsPerRun));
        var checkpoints = CheckpointManager.CreateJson(new PostgresCheckpointStore(planDb, writer));
        try
        {
            await DocumentBatchWorkflow.RunAsync(DocumentBatchWorkflow.Build(port, new BatchInvocationCounter()), checkpoints, SessionId(claim.RunId), null, ct);
        }
        catch (Exception ex) when (Find<LeaseLostException>(ex) is not null)
        {
            // Il dominio prevale sul checkpoint: se il commit atomico ha gia completato il run, la scrittura del checkpoint finale e stata rifiutata.
            if (!plan.Committed)
            {
                throw;
            }
        }
    }

    /// <summary>Messaggio unico del batch: dati del server e intero JSON della revisione confermata 1.1.0. A capo sempre «\n».</summary>
    public static IReadOnlyList<ChatMessage> BuildMessages(DocumentRunInputV11 input)
    {
        var text = new StringBuilder()
            .Append("Dati affidabili del server per questa generazione:\n")
            .Append($"- analysisId: {input.AnalysisId:D}\n- projectId: {input.ProjectId:D}\n- revisionId: {input.RevisionId:D} (versione {input.Version})\n")
            .Append("Analisi confermata dal project manager, JSON AnalysisContent 1.1.0 completo:\n")
            .Append(ModelView(input.Content)).Append('\n')
            .Append("Rispondi ora con l'unico oggetto JSON {\"sectionKey\":...,\"blocks\":[...]}.")
            .ToString();
        return [new ChatMessage(ChatRole.User, text)];
    }

    /// <summary>
    /// Vista dell'analisi per il modello: come il JSON confermato ma senza gli id che il validatore non ammette come citabili, cioe l'<c>id</c> del
    /// conflitto (non e un item: sono item le sue alternative) e le <c>evidenceRefs</c> della risoluzione (fonti che non sostengono alcun item).
    /// Le <c>selectedAlternativeIds</c> e il motivo restano: l'alternativa scelta e un item citabile.
    /// </summary>
    private static string ModelView(DocumentAnalysisContent content)
    {
        var root = JsonSerializer.SerializeToNode(content, PromptJson)!.AsObject();
        foreach (var conflict in root["conflicts"]!.AsArray().OfType<JsonObject>())
        {
            conflict.Remove("id");
            (conflict["resolution"] as JsonObject)?.Remove("evidenceRefs");
        }

        return root.ToJsonString(PromptJson);
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
        await new DocumentRunCompletion(scope.ServiceProvider.GetRequiredService<AppDbContext>()).FailAsync(claim, code, message, ct);
    }

    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is T match)
            {
                return match;
            }
        }

        return null;
    }

    internal static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>
/// Parte di dominio del grafo batch per la generazione documentale a singola chiamata (<see cref="IDocumentBatchPlan"/>): un solo batch, validazione
/// server prima del WorkOutput e commit atomico di bozza + Completed dal Markdown validato. Deterministico nel replay: identita del batch = hash di
/// owner, manifesto, definizione, stadio, ordinale e payload completo.
/// </summary>
internal sealed class DocumentDraftPlan : IDocumentBatchPlan
{
    private readonly DocumentRunClaim _claim;
    private readonly DocumentRunInputV11 _input;
    private readonly DocumentDraftPackage _package;
    private readonly IReadOnlyList<ChatMessage> _messages;
    private readonly PayloadPreflight _measure;
    private readonly IServiceScopeFactory _scopes;
    private readonly string _manifestIdentity;

    public DocumentDraftPlan(
        DocumentRunClaim claim, DocumentRunInputV11 input, IReadOnlyList<ChatMessage> messages, string instructions, PayloadPreflight measure, bool structured,
        AgentFactory factory, InternalModelClient client, JsonElement schema, IServiceScopeFactory scopes)
    {
        _claim = claim;
        _input = input;
        _package = DocumentDraftPackage.From(input.Content);
        _messages = messages;
        _measure = measure;
        _scopes = scopes;

        var settings = factory.Options.ForPhase(AiPhase.Document);
        var configHash = DocumentSourceDraftWorkflow.Sha(JsonSerializer.Serialize(new
        {
            prompt = DocumentSourceDraftWorkflow.PromptVersion, instructions = DocumentSourceDraftWorkflow.Sha(instructions), schema = DocumentSourceDraftWorkflow.SchemaVersion, structured,
            model = settings.ModelId, maxOutput = settings.MaxOutputTokens, thinking = settings.EnableThinking, inputBudget = measure.InputBudgetTokens,
        }));
        Agent = new BatchAgentSpec(
            "DocumentDraft", DocumentSourceDraftWorkflow.AgentId, instructions,
            structured ? client.CreateChatOptions(schema, DocumentSourceDraftWorkflow.SchemaName, temperature: 0.1f, phase: AiPhase.Document) : client.CreateChatOptions(temperature: 0.1f, phase: AiPhase.Document),
            AiPhase.Document, measure.Profile.Fingerprint);

        var sourceHash = DocumentSourceDraftWorkflow.Sha($"draft-source|{input.RevisionId:N}|{DocumentSourceDraftWorkflow.Sha(DocumentAnalysisContentJson.Serialize(input.Content))}");
        var policyHash = DocumentSourceDraftWorkflow.Sha("document-draft-policy-v1");
        var planJson = new JsonObject
        {
            ["algorithm"] = DocumentSourceDraftWorkflow.AlgorithmVersion,
            ["batches"] = new JsonArray(JsonNode.Parse(InputIdsJson())!),
            ["definition"] = DocumentSourceDraftWorkflow.DefinitionVersion,
            ["graph"] = new JsonArray(DocumentBatchWorkflow.Stages.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        }.ToJsonString();
        Manifest = new WorkManifestInput(
            configHash, sourceHash, measure.Profile.Fingerprint, DocumentSourceDraftWorkflow.DefinitionVersion, DocumentSourceDraftWorkflow.AlgorithmVersion, policyHash, planJson);
        _manifestIdentity = CanonicalJson.Sha256Hex(JsonSerializer.Serialize(new
        {
            configHash, source = sourceHash, runtime = measure.Profile.Fingerprint, definition = DocumentSourceDraftWorkflow.DefinitionVersion,
            algorithm = DocumentSourceDraftWorkflow.AlgorithmVersion, policy = policyHash, plan = JsonDocument.Parse(planJson).RootElement,
        }));
    }

    /// <summary>Vero dopo il commit atomico del run da questo piano.</summary>
    public bool Committed { get; private set; }

    public WorkManifestInput Manifest { get; }

    public WorkLookupExpectation Expectation => new(DocumentSourceDraftWorkflow.SchemaVersion, DocumentSourceDraftWorkflow.ValidationVersion);

    public BatchAgentSpec Agent { get; }

    public bool HasBatch(int ordinal) => ordinal == 0;

    private string InputIdsJson() => JsonSerializer.Serialize(new { ordinal = 0, itemIds = _package.ItemSources.Keys.Order().Select(i => i.ToString("D")).ToArray() });

    public ValueTask<DerivedBatchInput> PrepareAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        if (ordinal != 0)
        {
            throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"La generazione a singola chiamata ha un solo batch: ordinale {ordinal} non valido.");
        }

        var inputHash = _measure.PayloadHash;
        var batchId = DocumentSourceDraftWorkflow.Sha(
            $"batch|ProcessingRun|{_claim.RunId:N}|{_manifestIdentity}|{DocumentSourceDraftWorkflow.DefinitionVersion}|{DocumentSourceDraftWorkflow.BatchStage}|{ordinal}|{inputHash}");
        var provenance = _measure.Quality == TokenCountQuality.Measured ? "measured" : "calibrated_upper_bound";
        return new(new DerivedBatchInput(
            batchId, DocumentSourceDraftWorkflow.BatchStage, null, null, null, ordinal, [], InputIdsJson(), _measure.Tokens, provenance, inputHash,
            DocumentSourceDraftWorkflow.SchemaVersion, DocumentSourceDraftWorkflow.PromptVersion));
    }

    public ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, IBatchOutputs prior, CancellationToken ct) => new(_messages);

    /// <summary>Valida PRIMA del WorkOutput: un output non valido e errore permanente (nessun output durevole, nessuna riparazione).</summary>
    public ValidatedBatch Validate(BatchRef batch, string responseText)
    {
        var validation = DocumentDraftValidator.ValidateBlocks(responseText, _package);
        if (!validation.Accepted)
        {
            throw new DocumentWorkException(
                DocumentWorkException.WorkOutputInvalid,
                $"Output della generazione documentale non valido ({validation.Violations.Count} violazioni): " + string.Join(" | ", validation.Violations.Take(5)));
        }

        var output = validation.Output!;
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = DocumentSourceDraftWorkflow.SchemaVersion, revisionId = _input.RevisionId, sectionKey = output.SectionKey,
            blocks = output.Blocks.Select(b => new { itemIds = b.ItemIds.Select(i => i.ToString("D")), markdown = b.Markdown, sourceIds = b.SourceIds.Select(i => i.ToString("D")) }),
            markdown = validation.Markdown,
        });
        var covered = JsonSerializer.Serialize(output.Blocks.SelectMany(b => b.ItemIds).Distinct().Select(i => i.ToString("D")));
        var maps = JsonSerializer.Serialize(new { blocks = output.Blocks.Select(b => new { itemIds = b.ItemIds.Select(i => i.ToString("D")), sourceIds = b.SourceIds.Select(i => i.ToString("D")) }) });
        var work = new WorkOutputInput(
            batch.BatchId, DocumentBatchWorkflow.InvokeId, batch.InputHash, CanonicalJson.Sha256Hex(payload), payload, null, null, covered, maps, null,
            DocumentSourceDraftWorkflow.SchemaVersion, DocumentSourceDraftWorkflow.ValidationVersion, null);
        return new ValidatedBatch(work);
    }

    /// <summary>Rilegge il Markdown validato dall'output Committed e lo ricontrolla contro il pacchetto: il commit non si fida del solo payload persistito.</summary>
    private async ValueTask<string> DecodeAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var output = await outputs.GetByOrdinalAsync(0, ct)
            ?? throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, "Assemble senza l'output della generazione: nessuna bozza da committare.");
        using var document = JsonDocument.Parse(output.PayloadJson ?? throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Payload mancante."));
        var root = document.RootElement;
        if (root.GetProperty("revisionId").GetGuid() != _input.RevisionId)
        {
            throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "L'output committato appartiene a un'altra revisione di input.");
        }

        var rebuilt = JsonSerializer.Serialize(new
        {
            sectionKey = root.GetProperty("sectionKey").GetString(),
            blocks = root.GetProperty("blocks").EnumerateArray().Select(b => new
            {
                itemIds = b.GetProperty("itemIds").EnumerateArray().Select(i => i.GetString()),
                markdown = b.GetProperty("markdown").GetString(),
                sourceIds = b.GetProperty("sourceIds").EnumerateArray().Select(i => i.GetString()),
            }),
        });
        var validation = DocumentDraftValidator.ValidateBlocks(rebuilt, _package);
        return validation.Accepted
            ? validation.Markdown!
            : throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "L'output committato non supera la validazione server: " + string.Join(" | ", validation.Violations.Take(3)));
    }

    public async ValueTask AssembleAsync(IBatchOutputs outputs, CancellationToken ct) => _ = await DecodeAsync(outputs, ct);

    public async ValueTask<int> CommitAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var markdown = await DecodeAsync(outputs, ct);
        using var scope = _scopes.CreateScope();
        var completion = new DocumentRunCompletion(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        var result = await completion.CommitAsync(_claim, markdown, ct);
        if (result.Outcome == DocumentCommitOutcome.Rejected)
        {
            if (result.Reason == "lease_lost")
            {
                throw new LeaseLostException("Commit della bozza rifiutato (lease_lost): il lease non e piu valido.");
            }

            throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"Commit della bozza documentale rifiutato ({result.Reason}).");
        }

        Committed = true;
        return 1;
    }
}
