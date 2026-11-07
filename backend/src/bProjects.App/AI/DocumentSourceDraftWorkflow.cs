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
/// revisione congelata all'avvio del run, mai a una successiva. Oltre la capacita di una chiamata: il contratto v2 (storico) resta un errore esplicito, il v3 genera per
/// unita editoriali (T086, <see cref="DocumentDraftEditorialPlan"/>).
/// Registrazione DI e instradamento sulla definizione congelata sono di T123.
/// </summary>
public sealed class DocumentSourceDraftWorkflow(
    IServiceScopeFactory scopes, AgentFactory factory, InternalModelClient client, IOptions<DocumentOptions> documents)
{
    public const string AgentId = "document-draft";

    /// <summary>Versione v2 (storica) del prompt congelata sui run richiesti prima di T127; il file e <c>document-draft-v2.md</c>, non piu modificabile.</summary>
    public const string PromptVersion = "document-documents-v2";

    /// <summary>Definizione v2 (storica): i run gia congelati restano v2 con il proprio prompt e decoder.</summary>
    public const string DefinitionVersion = "project-document-documents-v2";
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

    // v3 (T127, contracts/ai-workflow.md §7.11): contratto con alias nella fase bozza; v2 sopra resta storico e immutato. La versione di un run e quella della
    // definizione congelata alla richiesta: mai un downgrade da v3 a v2 ne il contrario.
    public const string DefinitionVersionV3 = "project-document-documents-v3";
    public const string PromptVersionV3 = "document-draft-v3";
    public const string AlgorithmVersionV3 = DraftAliasComposer.AlgorithmVersion;
    public const string SchemaNameV3 = "document_draft_alias";
    public const string SchemaVersionV3 = DraftAliasContracts.SchemaVersion;
    public const string ValidationVersionV3 = DraftAliasComposer.ValidationVersion;

    private static readonly Lazy<string> PromptV3 = new(() => ConversationalAgent.LoadPrompt("document-draft-v3.md"));

    public static string InstructionsV3 => PromptV3.Value;

    /// <summary>Contratto di bozza di un run: v2 (storico, frozen) o v3 (alias); null = definizione, prompt o versioni non supportati (workflow_incompatible).</summary>
    internal static int? ContractOf(ProcessingRun run)
    {
        if (run.MafVersion != Conversation.CurrentMafVersion)
        {
            return null;
        }

        if (run.WorkflowDefinitionVersion == DefinitionVersion && run.PromptVersionMatches("document", PromptVersion))
        {
            return 2;
        }

        // v3: ogni versione congelata deve coincidere con quella supportata (prompt, schema AI, catalogo, algoritmo, validazione).
        return run.WorkflowDefinitionVersion == DefinitionVersionV3 && run.PromptVersionMatches("document", PromptVersionV3)
            && run.PromptVersionMatches("draftAiSchema", SchemaVersionV3) && run.PromptVersionMatches("draftCatalog", DraftAliasCatalog.Version)
            && run.PromptVersionMatches("algorithm", AlgorithmVersionV3) && run.PromptVersionMatches("validation", ValidationVersionV3)
            ? 3
            : null;
    }

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

        if (ContractOf(run) is not { } contract)
        {
            // Nessuna migrazione implicita di un altro grafo: errore esplicito e permanente (T123 instrada le definizioni supportate).
            await FailAsync(claim, "workflow_incompatible", "La definizione del workflow del run non e compatibile con quella documentale corrente.", ct);
            return await ReadAsync(claim.RunId, ct);
        }

        try
        {
            await (contract == 3 ? RunAliasGraphAsync(db, claim, ct) : RunGraphAsync(db, claim, ct));
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
        var definition = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProcessingRuns.AsNoTracking()
            .Where(r => r.Id == runId && r.Kind == ProcessingRunKind.Document).Select(r => r.WorkflowDefinitionVersion).SingleAsync(ct);
        if (definition == DefinitionVersionV3)
        {
            // v3: il payload misurato e quello reale con alias e lo schema del DTO (il catalogo e funzione della sola revisione confermata, nota prima del claim).
            var schema = DraftAliasContracts.JsonSchema();
            var aliasPayload = new DocumentPayload(AiPhase.Document, InstructionsV3, BuildAliasMessages(input, DraftAliasCatalog.Build(input.Content)), structured ? schema.GetRawText() : null);
            return new RegimePayload(AiPhase.Document, aliasPayload, schema.GetRawText(), factory.Preflight);
        }

        var payload = new DocumentPayload(AiPhase.Document, Instructions, BuildMessages(input), structured ? OutputSchema.GetRawText() : null);
        return new RegimePayload(AiPhase.Document, payload, OutputSchema.GetRawText(), factory.Preflight);
    }

    /// <summary>
    /// Grafo v3 (alias): stesso grafo fisso T043 e stesso port T044 della v2, con messaggio a soli alias, schema del DTO <c>document-draft-alias-1</c> e piano che
    /// compone il canonico sul server. Il catalogo si costruisce dalla sola revisione confermata congelata sul run (mai da una successiva).
    /// </summary>
    private async Task RunAliasGraphAsync(AppDbContext db, DocumentRunClaim claim, CancellationToken ct)
    {
        DocumentRunInputV11 input;
        try
        {
            input = await new DocumentRunCompletion(db).LoadDocumentInputAsync(claim.RunId, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new DocumentWorkException("processing_failed", ex.Message);
        }

        var catalog = DraftAliasCatalog.Build(input.Content);
        var structured = factory.Options.StructuredOutputMode == StructuredOutputMode.JsonSchema;
        var instructions = InstructionsV3;
        var schema = DraftAliasContracts.JsonSchema();
        var messages = BuildAliasMessages(input, catalog);
        var measure = await factory.Preflight.MeasureAsync(
            new DocumentPayload(AiPhase.Document, instructions, messages, structured ? schema.GetRawText() : null), ct);
        if (measure.Quality == TokenCountQuality.Unavailable)
        {
            throw new RuntimeUnverifiedException("conteggio del payload della generazione non disponibile (nessun tokenizer o calibrazione identificata)");
        }

        if (!measure.Fits)
        {
            // Mai una compattazione della revisione confermata: oltre la capacita di una chiamata si generano unita editoriali misurate (T085/T086).
            await RunEditorialGraphAsync(db, claim, input, catalog, measure, structured, schema, ct);
            return;
        }

        using var scope = scopes.CreateScope();
        var planDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var writer = new CheckpointWriter(claim.RunId, claim.LeaseOwner, claim.FencingToken);
        var store = new DocumentWorkStore(planDb, writer);
        var plan = new DocumentDraftAliasPlan(claim, input, catalog, messages, instructions, measure, structured, factory, client, schema, scopes);
        var counter = new BatchInvocationCounter();
        var port = new DocumentBatchPort(store, planDb, writer, factory, plan, new RunCallReservation(planDb, writer, documents.Value.MaxAiCallsPerRun), counter);
        var checkpoints = CheckpointManager.CreateJson(new PostgresCheckpointStore(planDb, writer));
        try
        {
            await DocumentBatchWorkflow.RunAsync(DocumentBatchWorkflow.Build(port, counter), checkpoints, SessionId(claim.RunId), null, ct);
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

    /// <summary>
    /// Grafo v3 oltre la capacita di una chiamata (002 T086): stesso grafo fisso T043 e stesso port T044, un batch per unita editoriale del planner T085 (item principali,
    /// contesto, conflitti e fonti misurati sul payload completo), assemblaggio deterministico senza LLM e commit unico di Draft + Completed a copertura completa. Un item
    /// irriducibile e <c>document_incomplete</c> con il suo ID prima di ogni chiamata; la revisione confermata non si compatta mai e la finestra non si aumenta.
    /// </summary>
    private async Task RunEditorialGraphAsync(
        AppDbContext db, DocumentRunClaim claim, DocumentRunInputV11 input, DraftAliasCatalog catalog, PayloadPreflight measure, bool structured, JsonElement schema, CancellationToken ct)
    {
        var ledger = await DraftSourceLedger.LoadAsync(db, input.AnalysisId, ct);
        using var scope = scopes.CreateScope();
        var planDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var writer = new CheckpointWriter(claim.RunId, claim.LeaseOwner, claim.FencingToken);
        var store = new DocumentWorkStore(planDb, writer);
        var plan = new DocumentDraftEditorialPlan(claim, input, catalog, ledger, measure, structured, factory, client, schema, scopes);
        await plan.PreflightAsync(ct);
        var counter = new BatchInvocationCounter();
        var port = new DocumentBatchPort(store, planDb, writer, factory, plan, new RunCallReservation(planDb, writer, documents.Value.MaxAiCallsPerRun), counter);
        var checkpoints = CheckpointManager.CreateJson(new PostgresCheckpointStore(planDb, writer));
        try
        {
            await DocumentBatchWorkflow.RunAsync(DocumentBatchWorkflow.Build(port, counter), checkpoints, SessionId(claim.RunId), null, ct);
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

    private static readonly (string Name, Func<DocumentAnalysisContent, IReadOnlyList<DocumentAnalysisItem>> Items)[] MessageSections =
    [
        ("goals", c => c.Goals), ("overview", c => c.ContextAndScope.Overview), ("included", c => c.ContextAndScope.Included), ("excluded", c => c.ContextAndScope.Excluded),
        ("requirements", c => c.Requirements), ("operationalFlows", c => c.OperationalFlows), ("constraints", c => c.Constraints), ("dependencies", c => c.Dependencies),
        ("assumptions", c => c.Assumptions), ("openQuestions", c => c.OpenQuestions),
    ];

    /// <summary>
    /// Messaggio unico del batch v3 con soli alias (§7.11): ogni elemento <c>[I&lt;n&gt;]</c> con classificazione, fonti <c>F&lt;n&gt;</c> e testo; i conflitti con le loro
    /// alternative e, se risolti, alias scelto e motivo del PM; l'elenco delle sole fonti citabili. Mai UUID, hash, id di analisi/progetto/revisione, id del
    /// conflitto o fonti della sola risoluzione. A capo sempre «\n».
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildAliasMessages(DocumentRunInputV11 input, DraftAliasCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(catalog);
        var content = input.Content;
        string Refs(DocumentAnalysisItem item) => string.Join(",", catalog.Item(catalog.AliasOfItem(item.Id)!)!.SourceAliases);
        var text = new StringBuilder("Analisi confermata dal project manager. Ogni elemento ha un alias [I<n>] e le sue fonti sono alias F<n>: cita solo questi alias.\n");
        foreach (var (name, items) in MessageSections)
        {
            foreach (var item in items(content))
            {
                text.Append($"\n<<<ELEMENTO [{catalog.AliasOfItem(item.Id)}] sezione={name} classificazione={item.Classification} fonti={Refs(item)}>>>\n")
                    .Append(item.Text).Append("\n<<<FINE ELEMENTO>>>\n");
            }
        }

        foreach (var conflict in content.Conflicts)
        {
            text.Append($"\n<<<CONFLITTO stato={conflict.Status}>>>\n").Append(conflict.Description).Append('\n');
            foreach (var alternative in conflict.Alternatives)
            {
                text.Append($"<<<ALTERNATIVA [{catalog.AliasOfItem(alternative.Id)}] classificazione={alternative.Classification} fonti={Refs(alternative)}>>>\n")
                    .Append(alternative.Text).Append("\n<<<FINE ALTERNATIVA>>>\n");
            }

            if (conflict.Resolution is { } resolution)
            {
                text.Append("Risoluzione del PM: alternativa scelta ").Append(string.Join(", ", resolution.SelectedAlternativeIds.Select(id => $"[{catalog.AliasOfItem(id)}]")))
                    .Append("; motivo: ").Append(resolution.Reason).Append('\n');
            }

            text.Append("<<<FINE CONFLITTO>>>\n");
        }

        text.Append("\nFonti citabili:\n");
        if (catalog.Sources.Count == 0)
        {
            text.Append("(nessuna fonte)\n");
        }

        foreach (var source in catalog.Sources)
        {
            text.Append($"[{source.Alias}] tipo={source.Kind}\n");
        }

        text.Append("\nRispondi ora con l'unico oggetto JSON `document-draft-alias-1`.");
        return [new ChatMessage(ChatRole.User, text.ToString())];
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

/// <summary>
/// Parte di dominio del grafo batch per la bozza v3 con alias (<see cref="IDocumentBatchPlan"/>, contracts/ai-workflow.md §7.11): un solo batch il cui messaggio
/// espone soli alias, validazione e composizione canonica sul server PRIMA del WorkOutput, payload <c>{aiOutputHash, catalogHash, canonicalHash}</c> con il DTO,
/// ricomposizione dal DTO committato a ogni lettura e a ogni commit (mai una reinferenza) con verifica degli hash. Il catalogo è funzione pura della revisione
/// confermata congelata: l'hash è nel manifesto e il catalogo si ricostruisce, non si persiste. Deterministico nel replay: nessun Guid né orologio nell'identità dei batch.
/// Solo questo piano (v3) ammette il retry guidato unico per item obbligatori omessi (T129, AI §9); il piano v2, congelato, non ne ha.
/// </summary>
internal sealed class DocumentDraftAliasPlan : IDocumentBatchPlan, IGuidedRetryPlan
{
    private readonly DocumentRunClaim _claim;
    private readonly DocumentRunInputV11 _input;
    private readonly DraftAliasCatalog _catalog;
    private readonly DocumentDraftPackage _package;
    private readonly IReadOnlyList<ChatMessage> _messages;
    private readonly PayloadPreflight _measure;
    private readonly IServiceScopeFactory _scopes;
    private readonly string _manifestIdentity;

    public DocumentDraftAliasPlan(
        DocumentRunClaim claim, DocumentRunInputV11 input, DraftAliasCatalog catalog, IReadOnlyList<ChatMessage> messages, string instructions, PayloadPreflight measure,
        bool structured, AgentFactory factory, InternalModelClient client, JsonElement schema, IServiceScopeFactory scopes)
    {
        _claim = claim;
        _input = input;
        _catalog = catalog;
        _package = DocumentDraftPackage.From(input.Content);
        _messages = messages;
        _measure = measure;
        _scopes = scopes;

        var settings = factory.Options.ForPhase(AiPhase.Document);
        var configHash = DocumentSourceDraftWorkflow.Sha(JsonSerializer.Serialize(new
        {
            prompt = DocumentSourceDraftWorkflow.PromptVersionV3, instructions = DocumentSourceDraftWorkflow.Sha(instructions), schema = DocumentSourceDraftWorkflow.SchemaVersionV3,
            catalog = DraftAliasCatalog.Version, validation = DocumentSourceDraftWorkflow.ValidationVersionV3, structured,
            model = settings.ModelId, maxOutput = settings.MaxOutputTokens, thinking = settings.EnableThinking, inputBudget = measure.InputBudgetTokens,
        }));
        Agent = new BatchAgentSpec(
            "DocumentDraft", DocumentSourceDraftWorkflow.AgentId, instructions,
            structured ? client.CreateChatOptions(schema, DocumentSourceDraftWorkflow.SchemaNameV3, temperature: 0.1f, phase: AiPhase.Document) : client.CreateChatOptions(temperature: 0.1f, phase: AiPhase.Document),
            AiPhase.Document, measure.Profile.Fingerprint);

        var sourceHash = DocumentSourceDraftWorkflow.Sha($"draft-source|{input.RevisionId:N}|{DocumentSourceDraftWorkflow.Sha(DocumentAnalysisContentJson.Serialize(input.Content))}");
        var policyHash = DocumentSourceDraftWorkflow.Sha("document-draft-policy-v1");
        var planJson = new JsonObject
        {
            ["algorithm"] = DocumentSourceDraftWorkflow.AlgorithmVersionV3,
            ["batches"] = new JsonArray(JsonNode.Parse(InputIdsJson())!),
            ["catalog"] = catalog.CatalogHash,
            ["definition"] = DocumentSourceDraftWorkflow.DefinitionVersionV3,
            ["graph"] = new JsonArray(DocumentBatchWorkflow.Stages.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        }.ToJsonString();
        Manifest = new WorkManifestInput(
            configHash, sourceHash, measure.Profile.Fingerprint, DocumentSourceDraftWorkflow.DefinitionVersionV3, DocumentSourceDraftWorkflow.AlgorithmVersionV3, policyHash, planJson);
        _manifestIdentity = CanonicalJson.Sha256Hex(JsonSerializer.Serialize(new
        {
            configHash, source = sourceHash, runtime = measure.Profile.Fingerprint, definition = DocumentSourceDraftWorkflow.DefinitionVersionV3,
            algorithm = DocumentSourceDraftWorkflow.AlgorithmVersionV3, policy = policyHash, plan = JsonDocument.Parse(planJson).RootElement,
        }));
    }

    /// <summary>Vero dopo il commit atomico del run da questo piano.</summary>
    public bool Committed { get; private set; }

    public WorkManifestInput Manifest { get; }

    public WorkLookupExpectation Expectation => new(DocumentSourceDraftWorkflow.SchemaVersionV3, DocumentSourceDraftWorkflow.ValidationVersionV3);

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
            $"batch|ProcessingRun|{_claim.RunId:N}|{_manifestIdentity}|{DocumentSourceDraftWorkflow.DefinitionVersionV3}|{DocumentSourceDraftWorkflow.BatchStage}|{ordinal}|{inputHash}");
        var provenance = _measure.Quality == TokenCountQuality.Measured ? "measured" : "calibrated_upper_bound";
        return new(new DerivedBatchInput(
            batchId, DocumentSourceDraftWorkflow.BatchStage, null, null, null, ordinal, [], InputIdsJson(), _measure.Tokens, provenance, inputHash,
            DocumentSourceDraftWorkflow.SchemaVersionV3, DocumentSourceDraftWorkflow.PromptVersionV3));
    }

    public ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, IBatchOutputs prior, CancellationToken ct) => new(_messages);

    /// <summary>
    /// Marcatore del retry consumato (T129): batch d'appoggio (stadio <c>draft_retry</c>, ordinale 1, fuori dal cursore del grafo) la cui identità dipende solo da run,
    /// manifesto e batch originale, quindi uguale a ogni replay; non ha mai un WorkOutput. Porta solo conteggio e hash del primo output (diagnostica non sensibile).
    /// </summary>
    public DerivedBatchInput GuidedRetryMarker(BatchRef batch, GuidedRetryTrigger trigger)
    {
        var omission = (DraftOmissionException)trigger;
        var id = DocumentSourceDraftWorkflow.Sha($"guided-retry|ProcessingRun|{_claim.RunId:N}|{_manifestIdentity}|{DocumentSourceDraftWorkflow.DefinitionVersionV3}|{batch.BatchId}");
        var inputs = JsonSerializer.Serialize(new { guidedRetryOf = batch.BatchId, omittedItems = omission.MissingItemAliases.Count, firstOutputHash = omission.OutputHash });
        return new DerivedBatchInput(
            id, GuidedRetryStage, null, null, null, 1, [], inputs, null, "calibrated_upper_bound", DocumentSourceDraftWorkflow.Sha($"guided-retry-input|{batch.InputHash}"),
            DocumentSourceDraftWorkflow.SchemaVersionV3, DocumentSourceDraftWorkflow.PromptVersionV3);
    }

    public const string GuidedRetryStage = "draft_retry";

    /// <summary>
    /// Messaggio del secondo invio: lo stesso messaggio del primo (snapshot confermato, versioni, alias I/F e fonti canoniche) più l'elenco preciso degli alias
    /// mancanti e l'istruzione di rigenerare la bozza COMPLETA. Nessun testo né blocco del primo output: nessun merge, il server non scrive testo di item.
    /// </summary>
    public IReadOnlyList<ChatMessage> ComposeGuidedRetry(BatchRef batch, GuidedRetryTrigger trigger)
    {
        var omission = (DraftOmissionException)trigger;
        var missing = string.Join("\n", omission.MissingItemAliases.Select(a => $"- [{a}] (sezione={_catalog.Item(a)!.Section})"));
        var text = new StringBuilder(string.Concat(_messages.Select(m => m.Text)))
            .Append("\n\nCORREZIONE (unico nuovo tentativo): la tua risposta precedente è stata scartata perché non rappresenta questi elementi obbligatori:\n")
            .Append(missing)
            .Append("\nProduci di nuovo la bozza COMPLETA: ogni elemento dell'analisi confermata, sia quelli già rappresentati nella risposta precedente sia quelli elencati sopra, ")
            .Append("in almeno un blocco ciascuno e con le proprie fonti. Non limitarti agli elementi mancanti e non ometterne nessuno. ")
            .Append("Rispondi con l'unico oggetto JSON `document-draft-alias-1`.");
        return [new ChatMessage(ChatRole.User, text.ToString())];
    }

    private void Require(DraftComposition composition)
    {
        if (composition.Accepted)
        {
            return;
        }

        var message = $"Output della generazione documentale non valido ({composition.Violations.Count} violazioni): " + string.Join(" | ", composition.Violations.Take(5));
        if (composition.OmittedItemIds is { Count: > 0 } omitted)
        {
            // Retry guidato (T129): unico caso tipizzato dal validatore, mai ricavato da stringhe; ogni altra violazione resta permanente.
            var aliases = omitted.Select(_catalog.AliasOfItem).OfType<string>().ToList();
            if (aliases.Count == omitted.Count)
            {
                throw new DraftOmissionException(message, aliases, composition.AiOutputHash);
            }
        }

        throw new DocumentWorkException(composition.ErrorCode ?? DocumentWorkException.WorkOutputInvalid, message);
    }

    /// <summary>Validazione definitiva del port (T130): per la bozza coincide con <see cref="Validate"/> (il trigger di omissione è già il motivo originale).</summary>
    public ValidatedBatch ValidateFinal(BatchRef batch, string responseText) => Validate(batch, responseText);

    /// <summary>Compone e valida PRIMA del WorkOutput: un rifiuto è errore permanente (nessun output durevole, nessuna riparazione); l'unica eccezione è <see cref="DraftOmissionException"/> (T129).</summary>
    public ValidatedBatch Validate(BatchRef batch, string responseText)
    {
        var composition = DraftAliasComposer.Compose(responseText, _catalog, _package);
        Require(composition);

        var dto = DraftAliasContracts.Decode(responseText);
        var output = composition.Validation!.Output!;
        var payload = new JsonObject
        {
            ["schemaVersion"] = DocumentSourceDraftWorkflow.SchemaVersionV3,
            ["revisionId"] = _input.RevisionId.ToString("D"),
            ["aiOutputHash"] = composition.AiOutputHash,
            ["catalogHash"] = composition.CatalogHash,
            ["canonicalHash"] = composition.CanonicalHash,
            ["ai"] = JsonNode.Parse(DraftAliasContracts.Canonical(dto)),
        }.ToJsonString();
        var covered = JsonSerializer.Serialize(output.Blocks.SelectMany(b => b.ItemIds).Distinct().Select(i => i.ToString("D")));
        var maps = JsonSerializer.Serialize(new { blocks = output.Blocks.Select(b => new { itemIds = b.ItemIds.Select(i => i.ToString("D")), sourceIds = b.SourceIds.Select(i => i.ToString("D")) }) });
        var work = new WorkOutputInput(
            batch.BatchId, DocumentBatchWorkflow.InvokeId, batch.InputHash, CanonicalJson.Sha256Hex(payload), payload, null, null, covered, maps, null,
            DocumentSourceDraftWorkflow.SchemaVersionV3, DocumentSourceDraftWorkflow.ValidationVersionV3, null);
        return new ValidatedBatch(work);
    }

    /// <summary>
    /// Ricompone il canonico dal WorkOutput Committed (nessuna inferenza): catalogo ricostruito dalla revisione e verificato contro <c>catalogHash</c>
    /// (<c>checkpoint_incompatible</c>), DTO ricomposto e verificato contro <c>canonicalHash</c> (<c>integrity_conflict</c>) e <c>aiOutputHash</c> (<c>work_output_invalid</c>).
    /// </summary>
    private async ValueTask<string> DecodeAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var output = await outputs.GetByOrdinalAsync(0, ct)
            ?? throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, "Assemble senza l'output della generazione: nessuna bozza da committare.");
        string catalogHash, aiOutputHash, canonicalHash, aiJson;
        try
        {
            using var document = JsonDocument.Parse(output.PayloadJson ?? throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Payload mancante."));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetString() != DocumentSourceDraftWorkflow.SchemaVersionV3)
            {
                throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Il WorkOutput non è del contratto con alias.");
            }

            if (root.GetProperty("revisionId").GetGuid() != _input.RevisionId)
            {
                throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "L'output committato appartiene a un'altra revisione di input.");
            }

            (catalogHash, aiOutputHash, canonicalHash, aiJson) = (
                root.GetProperty("catalogHash").GetString()!, root.GetProperty("aiOutputHash").GetString()!, root.GetProperty("canonicalHash").GetString()!, root.GetProperty("ai").GetRawText());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Il payload del WorkOutput della bozza non è nel formato con alias.");
        }

        if (catalogHash != _catalog.CatalogHash)
        {
            throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Il catalogo alias ricostruito dalla revisione ha un hash diverso da quello committato.");
        }

        var composition = DraftAliasComposer.Compose(aiJson, _catalog, _package, expectedCanonicalHash: canonicalHash);
        Require(composition);
        if (composition.AiOutputHash != aiOutputHash)
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Il DTO committato ha un hash diverso da aiOutputHash.");
        }

        return composition.Validation!.Markdown!;
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
