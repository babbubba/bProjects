using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace bProjects.App.AI;

/// <summary>
/// Risposta finale validata della chat documentale (002 T058): modalità, testo con i marcatori <c>[fonte:UUID]</c> e identificativi distinti delle
/// fonti citate, nell'ordine della prima citazione. Mai creata da un testo non validato (<see cref="DocumentChatContract.Validate"/>).
/// </summary>
public sealed record DocumentChatAnswer(string Mode, string Answer, IReadOnlyList<Guid> SourceIds);

/// <summary>
/// Contratto chiuso della risposta finale (AI/Prompts/document-chat-v2.md): un solo oggetto JSON {mode,answer}, senza proprietà extra né
/// duplicati, nessun recinto di codice e nessuna riparazione. <c>documented</c> richiede almeno un marcatore <c>[fonte:UUID]</c>
/// (UUID minuscolo, formato «D») e tutte le fonti citate devono appartenere allo snapshot del turno; <c>no_document_use</c> non ne ha.
/// Un marcatore malformato invalida l'intera risposta. Gli errori sono permanenti (work_output_invalid) e non riportano mai il testo del modello.
/// </summary>
public static partial class DocumentChatContract
{
    public const string ModeDocumented = "documented";
    public const string ModeNoDocumentUse = "no_document_use";
    public const string SchemaVersion = "document-chat-1";
    public const string ValidationVersion = "document-chat-validation-v1";

    [GeneratedRegex(@"\[fonte:([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\]")]
    private static partial Regex Marker();

    public static DocumentChatAnswer Validate(string responseText, IReadOnlySet<Guid> allowedSourceIds)
    {
        ArgumentNullException.ThrowIfNull(responseText);
        ArgumentNullException.ThrowIfNull(allowedSourceIds);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseText, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            throw Invalid("la risposta non è un unico oggetto JSON valido");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("la risposta non è un oggetto JSON");
            }

            string? mode = null, answer = null;
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "mode" when property.Value.ValueKind == JsonValueKind.String:
                        mode = property.Value.GetString();
                        break;
                    case "answer" when property.Value.ValueKind == JsonValueKind.String:
                        answer = property.Value.GetString();
                        break;
                    default:
                        throw Invalid("proprietà non ammessa o di tipo errato");
                }
            }

            if (mode is not (ModeDocumented or ModeNoDocumentUse))
            {
                throw Invalid("mode assente o non ammesso");
            }

            if (string.IsNullOrWhiteSpace(answer))
            {
                throw Invalid("answer assente o vuota");
            }

            var matches = Marker().Matches(answer);
            var opened = CountOccurrences(answer, "[fonte:");
            if (opened != matches.Count)
            {
                throw Invalid("marcatore di fonte malformato");
            }

            var ids = new List<Guid>();
            foreach (Match match in matches)
            {
                var id = Guid.ParseExact(match.Groups[1].Value, "D");
                if (!allowedSourceIds.Contains(id))
                {
                    throw Invalid("citata una fonte che non appartiene alla selezione congelata del turno");
                }

                if (!ids.Contains(id))
                {
                    ids.Add(id);
                }
            }

            if (mode == ModeDocumented && ids.Count == 0)
            {
                throw Invalid("una risposta documented deve citare almeno una fonte");
            }

            if (mode == ModeNoDocumentUse && ids.Count > 0)
            {
                throw Invalid("una risposta no_document_use non può citare fonti");
            }

            return new DocumentChatAnswer(mode, answer, ids);
        }
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    private static DocumentWorkException Invalid(string reason) =>
        new(DocumentWorkException.WorkOutputInvalid, $"Risposta di chat documentale non valida ({reason}): nessun output committato.");
}

/// <summary>Evidenza documentale dello snapshot del turno, con le sue fonti persistite (testo letterale incluso).</summary>
internal sealed record ChatEvidence(
    Guid EvidenceId, Guid EvidenceVersionId, int Ordinal, EvidenceKind Kind, EvidencePolarity Polarity, EvidenceCertainty Certainty, string Statement,
    IReadOnlyList<PersistedSource> Sources);

/// <summary>Contenuto documentale congelato del turno: hash della selezione, fonti non disponibili e evidenze delle sole fonti Ready incluse.</summary>
internal sealed record ChatSnapshotSources(string SnapshotHash, int Unavailable, IReadOnlyList<ChatEvidence> Evidence)
{
    public HashSet<Guid> SourceIds { get; } = [.. Evidence.SelectMany(e => e.Sources).Select(s => s.SourceId)];
}

internal static class DocumentChatSources
{
    /// <summary>
    /// Legge lo snapshot del turno (mai la selezione corrente): solo righe Ready e incluse portano evidenze; escluse e non pronte non
    /// contribuiscono. Un contenuto non decodificabile è un conflitto d'integrità permanente, non una fonte omessa.
    /// </summary>
    public static async Task<ChatSnapshotSources> LoadAsync(AppDbContext db, Guid turnId, CancellationToken ct)
    {
        var snapshotId = await db.ChatTurns.AsNoTracking().Where(t => t.Id == turnId).Select(t => t.SourceSnapshotId).SingleOrDefaultAsync(ct);
        if (snapshotId is null)
        {
            return new ChatSnapshotSources("", 0, []);
        }

        var snapshot = await db.SourceSnapshots.AsNoTracking().SingleAsync(s => s.Id == snapshotId, ct);
        var items = await db.SourceSnapshotItems.AsNoTracking().Where(i => i.SnapshotId == snapshotId).OrderBy(i => i.DocumentId).ToListAsync(ct);
        var versions = items.Where(i => i.Included && i.Availability == SourceAvailability.Ready && i.EvidenceVersionId != null)
            .Select(i => i.EvidenceVersionId!.Value).ToList();
        var rows = await db.Evidences.AsNoTracking()
            .Where(e => versions.Contains(e.VersionId) && e.OriginKind == EvidenceOriginKind.Document).ToListAsync(ct);

        var evidence = new List<ChatEvidence>();
        foreach (var version in versions)
        {
            foreach (var row in rows.Where(r => r.VersionId == version).OrderBy(r => r.Ordinal))
            {
                DocumentEvidenceContent content;
                try
                {
                    content = DocumentEvidenceCodec.DecodeContent(row.Content);
                }
                catch (JsonException)
                {
                    throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Il contenuto di un'evidenza dello snapshot non è decodificabile.");
                }

                evidence.Add(new ChatEvidence(row.Id, version, row.Ordinal, row.Kind, row.Polarity, row.Certainty, row.Statement, content.Sources));
            }
        }

        return new ChatSnapshotSources(snapshot.Hash, items.Count(i => i.Availability == SourceAvailability.Unavailable), evidence);
    }
}

/// <summary>
/// Riserva del contatore chiamate di un ChatTurn: il turno della chat documentale nasce senza contatore (il POST /messages non classifica il
/// root, T123), quindi la prima riserva lo avvia a zero sotto owner/fence/Running e poi delega alla riserva comune (lock, guard e limite).
/// </summary>
internal sealed class ChatCallReservation(AppDbContext db, CheckpointWriter writer, int maxCalls) : IBatchCallReservation
{
    public async ValueTask ReserveAsync(BatchRef batch, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "ChatTurns" SET "CallCount" = 0
            WHERE "Id" = {writer.OwnerId} AND "CallCount" IS NULL AND "Status" = 'Running' AND "LeaseOwner" = {writer.LeaseOwner} AND "FencingToken" = {writer.FencingToken}
            """,
            ct);
        await new RootCallCounterReservation(db, writer, maxCalls).ReserveAsync(batch, ct);
    }
}

/// <summary>
/// project-chat-documents-v2 (002 T058, contracts/ai-workflow.md §6, chat piccola): un ChatTurn con snapshot documentale percorre il grafo fisso T043
/// con il port T044 (stessi guard, riserva chiamate, lookup/recovery, commit fenced dell'output) e un solo batch: dialogo operativo + tutte le
/// evidenze delle fonti Ready congelate + domanda. La risposta è un oggetto chiuso validato (<see cref="DocumentChatContract"/>) e committata dal
/// commit atomico esistente (<see cref="ChatTurnCompletion"/>): nessuna revisione formale, Evidence o run. Le fonti non pronte dello snapshot sono
/// dichiarate al modello e nella risposta senza attenderle. Il testo candidato è inoltrato come delta provvisorio solo dopo la validazione e prima
/// del commit; il commit rifiuta citazioni fuori snapshot nella stessa transazione. Payload oltre la capacità della fase → document_limit_exceeded
/// (la scansione multi-unità è US2/T084). Registrazione, selezione della definizione congelata e instradamento sono di T123.
/// </summary>
public sealed class DocumentChatWorkflow(
    IServiceScopeFactory scopes, AgentFactory factory, InternalModelClient client, IOptions<DocumentOptions> documents)
{
    public const string AgentId = "document-chat";
    public const string PromptVersion = "document-chat-v2";

    /// <summary>Definizione del grafo congelata per i nuovi turni documentali (checkpoint e manifesto); un'altra definizione non si riprende.</summary>
    public const string DefinitionVersion = "project-chat-documents-v2";

    public const string AlgorithmVersion = "chat-single-v1";
    public const string BatchStage = "chat";

    private static readonly Lazy<string> Prompt = new(() => ConversationalAgent.LoadPrompt("document-chat-v2.md"));

    private static readonly JsonElement AnswerSchema = JsonDocument.Parse(
        """
        {"type":"object","additionalProperties":false,"required":["mode","answer"],
         "properties":{"mode":{"type":"string","enum":["documented","no_document_use"]},"answer":{"type":"string","minLength":1}}}
        """).RootElement.Clone();

    /// <summary>Istruzioni della chat documentale (AI/Prompts/document-chat-v2.md, risorsa incorporata).</summary>
    public static string Instructions => Prompt.Value;

    /// <summary>Sessione del workflow MAF del turno: stabile sul turno, quindi un nuovo tentativo riprende dallo stesso checkpoint nativo.</summary>
    public static string SessionId(Guid turnId) => $"chat-{turnId:N}";

    /// <summary>Versione di configurazione della sessione: cambia con selezione (hash snapshot), prompt, modello e limiti e invalida gli snapshot di sessione.</summary>
    public static string ChatConfigVersionFor(string snapshotHash, string configHash) =>
        "document-chat-v2:" + Sha($"{snapshotHash}|{configHash}")[..32];

    public async Task<ChatTurnCommitResult> ExecuteAsync(ChatTurnClaim claim, Func<string, CancellationToken, ValueTask>? onDelta, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (turn, conversation, sources, messages, instructions, structured) = await LoadInputAsync(db, claim.TurnId, ct);
        var measure = await factory.Preflight.MeasureAsync(PayloadOf(instructions, messages, structured), ct);
        if (measure.Quality == TokenCountQuality.Unavailable)
        {
            throw new RuntimeUnverifiedException("conteggio del payload della chat non disponibile (nessun tokenizer o calibrazione identificata)");
        }

        if (!measure.Fits)
        {
            throw new DocumentWorkException(
                DocumentWorkException.CallLimitExceeded,
                $"Le fonti selezionate e il dialogo ({measure.Tokens} token) superano la capacità della chat documentale piccola ({measure.InputBudgetTokens} token): la scansione di più unità non è ancora disponibile.");
        }

        var definition = DefinitionVersion;
        var writer = CheckpointWriter.ForChatTurn(claim.TurnId, claim.LeaseOwner, claim.FencingToken, definition);
        var store = new DocumentWorkStore(db, writer);
        var committer = scope.ServiceProvider.GetRequiredService<IChatTurnCommitter>();
        var plan = new DocumentChatPlan(
            claim, turn, conversation, sources, messages, instructions, measure, structured, factory, client, AnswerSchema, committer, onDelta, ct);
        var port = new DocumentBatchPort(store, db, writer, factory, plan, new ChatCallReservation(db, writer, documents.Value.MaxAiCallsPerRun));
        var checkpoints = CheckpointManager.CreateJson(new PostgresCheckpointStore(db, writer));
        try
        {
            await DocumentBatchWorkflow.RunAsync(DocumentBatchWorkflow.Build(port, new BatchInvocationCounter()), checkpoints, SessionId(claim.TurnId), null, ct);
        }
        catch (Exception ex) when (IsLeaseLost(ex))
        {
            // Il dominio prevale sul checkpoint: se il commit atomico ha già completato il turno (rilasciando il lease), la scrittura del
            // checkpoint finale è stata rifiutata dalla guard; non c'è ripresa né un secondo stato finale. Altrimenti il lease è davvero perso.
            if (plan.Result is not { Outcome: not ChatTurnCommitOutcome.Rejected })
            {
                throw;
            }
        }

        return plan.Result ?? await ReconcileAsync(db, claim.TurnId, ct);
    }

    /// <summary>Carica turno, snapshot di fonti e dialogo e compone il payload del solo batch: usato sia dall'esecuzione sia dal preflight di classificazione (T124), così non divergono.</summary>
    private async Task<(ChatTurn Turn, Conversation Conversation, ChatSnapshotSources Sources, IReadOnlyList<ChatMessage> Messages, string Instructions, bool Structured)> LoadInputAsync(
        AppDbContext db, Guid turnId, CancellationToken ct)
    {
        var turn = await db.ChatTurns.AsNoTracking().SingleAsync(t => t.Id == turnId, ct);
        if (turn.SourceSnapshotId is null)
        {
            throw new DocumentWorkException(DocumentWorkException.InvalidInput, "Il turno non ha uno snapshot di fonti documentali.");
        }

        var conversation = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == turn.ConversationId, ct);
        var trigger = await db.ConversationMessages.AsNoTracking()
            .SingleAsync(m => m.Id == turn.TriggerMessageId && m.ConversationId == conversation.Id && m.Role == MessageRole.User, ct);
        var sources = await DocumentChatSources.LoadAsync(db, turnId, ct);
        var history = await PostgresChatHistoryProvider.LoadAdmittedAsync(db, conversation.Id, turn.SourceCutoffSequence, turn.TriggerMessageId, ct, sources.SnapshotHash);

        var instructions = sources.Unavailable > 0 ? Instructions + "\n\n" + ChatSourceNotice.ModelGuard(sources.Unavailable) : Instructions;
        var messages = BuildMessages(history, sources, trigger.Body);
        var structured = factory.Options.StructuredOutputMode == StructuredOutputMode.JsonSchema;
        return (turn, conversation, sources, messages, instructions, structured);
    }

    private static DocumentPayload PayloadOf(string instructions, IReadOnlyList<ChatMessage> messages, bool structured) =>
        new(AiPhase.Chat, instructions, messages, structured ? AnswerSchema.GetRawText() : null);

    /// <summary>Payload e schema del turno per il preflight di classificazione del regime (T124): stessa composizione dell'invio, nessuna chiamata al modello.</summary>
    public async Task<RegimePayload> BuildRegimePayloadAsync(Guid turnId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var (_, _, _, messages, instructions, structured) = await LoadInputAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), turnId, ct);
        return new RegimePayload(AiPhase.Chat, PayloadOf(instructions, messages, structured), AnswerSchema.GetRawText(), factory.Preflight);
    }

    /// <summary>Ripresa da un checkpoint successivo al commit: l'unica risposta è quella già committata, mai una seconda.</summary>
    private static async Task<ChatTurnCommitResult> ReconcileAsync(AppDbContext db, Guid turnId, CancellationToken ct)
    {
        var turn = await db.ChatTurns.AsNoTracking().SingleAsync(t => t.Id == turnId, ct);
        return turn is { Status: ChatTurnStatus.Completed, ResultMessageId: { } id }
            ? new ChatTurnCommitResult(ChatTurnCommitOutcome.AlreadyCompleted, id)
            : throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Il workflow di chat è terminato senza una risposta committata.");
    }

    /// <summary>
    /// Input del solo batch: il dialogo operativo come messaggi normali (FIFO, già filtrato dal cutoff e dalla selezione) e, nell'ultimo messaggio utente, le
    /// evidenze congelate con le fonti e la domanda. A capo sempre «\n»: il payload, quindi il suo hash, non dipende dal sistema operativo.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> BuildMessages(IReadOnlyList<ConversationMessage> history, ChatSnapshotSources sources, string question)
    {
        var text = new StringBuilder();
        text.Append("Evidenze documentali congelate per questo turno (").Append(sources.Evidence.Count).Append("):\n");
        if (sources.Evidence.Count == 0)
        {
            text.Append("(nessuna evidenza documentale disponibile)\n");
        }

        foreach (var evidence in sources.Evidence)
        {
            text.Append($"<<<EVIDENZA tipo={evidence.Kind} polarita={evidence.Polarity} certezza={evidence.Certainty}>>>\n")
                .Append(evidence.Statement).Append('\n');
            foreach (var source in evidence.Sources)
            {
                text.Append($"<<<FONTE fonte:{source.SourceId:D} ruolo={source.Role}>>>\n").Append(source.Quote).Append("\n<<<FINE FONTE>>>\n");
            }

            text.Append("<<<FINE EVIDENZA>>>\n");
        }

        text.Append("\nMessaggio del project manager:\n").Append(question)
            .Append("\n\nRispondi ora con l'unico oggetto JSON {\"mode\":...,\"answer\":...}.");

        var messages = history.Select(m => new ChatMessage(m.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant, m.Body)).ToList();
        messages.Add(new ChatMessage(ChatRole.User, text.ToString()));
        return messages;
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

    internal static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>
/// Parte di dominio del grafo batch per un turno di chat documentale (<see cref="IDocumentBatchPlan"/>): un solo batch, validazione chiusa della
/// risposta, composizione del risultato dall'output Committed e commit atomico del turno. Deterministico nel replay: identità dei batch = hash di
/// owner, manifesto, definizione, stadio, ordinale e payload completo.
/// </summary>
internal sealed class DocumentChatPlan : IDocumentBatchPlan
{
    private readonly ChatTurnClaim _claim;
    private readonly ChatTurn _turn;
    private readonly Conversation _conversation;
    private readonly ChatSnapshotSources _sources;
    private readonly IReadOnlyList<ChatMessage> _messages;
    private readonly string _instructions;
    private readonly PayloadPreflight _measure;
    private readonly BatchAgentSpec _agent;
    private readonly AgentFactory _factory;
    private readonly IChatTurnCommitter _committer;
    private readonly Func<string, CancellationToken, ValueTask>? _onDelta;
    private readonly CancellationToken _ct;
    private readonly string _manifestIdentity;
    private readonly string _configHash;

    public DocumentChatPlan(
        ChatTurnClaim claim, ChatTurn turn, Conversation conversation, ChatSnapshotSources sources, IReadOnlyList<ChatMessage> messages,
        string instructions, PayloadPreflight measure, bool structured, AgentFactory factory, InternalModelClient client, JsonElement schema,
        IChatTurnCommitter committer, Func<string, CancellationToken, ValueTask>? onDelta, CancellationToken ct)
    {
        _claim = claim;
        _turn = turn;
        _conversation = conversation;
        _sources = sources;
        _messages = messages;
        _instructions = instructions;
        _measure = measure;
        _factory = factory;
        _committer = committer;
        _onDelta = onDelta;
        _ct = ct;

        var settings = factory.Options.ForPhase(AiPhase.Chat);
        _configHash = DocumentChatWorkflow.Sha(JsonSerializer.Serialize(new
        {
            prompt = DocumentChatWorkflow.PromptVersion, instructions = DocumentChatWorkflow.Sha(instructions), schema = DocumentChatContract.SchemaVersion, structured,
            model = settings.ModelId, maxOutput = settings.MaxOutputTokens, thinking = settings.EnableThinking, inputBudget = measure.InputBudgetTokens,
        }));
        _agent = new BatchAgentSpec(
            ConversationalAgent.AgentName, DocumentChatWorkflow.AgentId, instructions,
            structured ? client.CreateChatOptions(schema, "document_chat", temperature: 0.2f, phase: AiPhase.Chat) : client.CreateChatOptions(temperature: 0.2f, phase: AiPhase.Chat),
            AiPhase.Chat, measure.Profile.Fingerprint);

        var sourceHash = DocumentChatWorkflow.Sha(
            $"chat-source|{sources.SnapshotHash}|{turn.SourceCutoffSequence}|{turn.TriggerMessageId:N}|{string.Join(',', sources.Evidence.Select(e => e.EvidenceVersionId.ToString("N")).Distinct())}");
        var policyHash = turn.PolicyHash is { } p && Regex.IsMatch(p, "^[0-9a-f]{64}$") ? p : DocumentChatWorkflow.Sha("document-chat-policy-v1");
        var graph = new JsonArray(DocumentBatchWorkflow.Stages.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        var planJson = new JsonObject
        {
            ["algorithm"] = DocumentChatWorkflow.AlgorithmVersion,
            ["batches"] = new JsonArray(JsonNode.Parse(InputIdsJson())!),
            ["definition"] = DocumentChatWorkflow.DefinitionVersion,
            ["graph"] = graph,
            ["regime"] = turn.Regime?.ToString() ?? "Unclassified",
        }.ToJsonString();
        Manifest = new WorkManifestInput(
            _configHash, sourceHash, measure.Profile.Fingerprint, DocumentChatWorkflow.DefinitionVersion, DocumentChatWorkflow.AlgorithmVersion, policyHash, planJson);
        _manifestIdentity = CanonicalJson.Sha256Hex(JsonSerializer.Serialize(new
        {
            configHash = _configHash, source = sourceHash, runtime = measure.Profile.Fingerprint, definition = DocumentChatWorkflow.DefinitionVersion,
            algorithm = DocumentChatWorkflow.AlgorithmVersion, policy = policyHash, plan = JsonDocument.Parse(planJson).RootElement,
        }));
    }

    /// <summary>Risultato del commit atomico del turno, valorizzato dallo stadio Commit.</summary>
    public ChatTurnCommitResult? Result { get; private set; }

    public WorkManifestInput Manifest { get; }

    public WorkLookupExpectation Expectation => new(DocumentChatContract.SchemaVersion, DocumentChatContract.ValidationVersion);

    public BatchAgentSpec Agent => _agent;

    public bool HasBatch(int ordinal) => ordinal == 0;

    private string InputIdsJson() => JsonSerializer.Serialize(new { ordinal = 0, evidenceIds = _sources.Evidence.Select(e => e.EvidenceId).ToArray() });

    public ValueTask<DerivedBatchInput> PrepareAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        if (ordinal != 0)
        {
            throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"La chat piccola ha un solo batch: ordinale {ordinal} non valido.");
        }

        var inputHash = _measure.PayloadHash;
        var batchId = DocumentChatWorkflow.Sha(
            $"batch|ChatTurn|{_claim.TurnId:N}|{_manifestIdentity}|{DocumentChatWorkflow.DefinitionVersion}|{DocumentChatWorkflow.BatchStage}|{ordinal}|{inputHash}");
        var provenance = _measure.Quality == TokenCountQuality.Measured ? "measured" : "calibrated_upper_bound";
        return new(new DerivedBatchInput(
            batchId, DocumentChatWorkflow.BatchStage, null, null, null, ordinal, [], InputIdsJson(), _measure.Tokens, provenance, inputHash,
            DocumentChatContract.SchemaVersion, DocumentChatWorkflow.PromptVersion));
    }

    public ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, IBatchOutputs prior, CancellationToken ct) => new(_messages);

    public ValidatedBatch Validate(BatchRef batch, string responseText)
    {
        var answer = DocumentChatContract.Validate(responseText, _sources.SourceIds);
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = DocumentChatContract.SchemaVersion, mode = answer.Mode, answer = answer.Answer, sourceIds = answer.SourceIds.Select(id => id.ToString("D")).ToArray(),
        });
        var covered = JsonSerializer.Serialize(_sources.Evidence.Select(e => e.EvidenceId.ToString("D")));
        var maps = JsonSerializer.Serialize(new { citations = answer.SourceIds.Select(id => id.ToString("D")).ToArray() });
        var output = new WorkOutputInput(
            batch.BatchId, DocumentBatchWorkflow.InvokeId, batch.InputHash, CanonicalJson.Sha256Hex(payload), payload, null, null, covered, maps, null,
            DocumentChatContract.SchemaVersion, DocumentChatContract.ValidationVersion, null);
        return new ValidatedBatch(output);
    }

    private async ValueTask<DocumentChatAnswer> DecodeAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var output = await outputs.GetByOrdinalAsync(0, ct)
            ?? throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, "Assemble senza l'output della chat: nessuna risposta da committare.");
        using var document = JsonDocument.Parse(output.PayloadJson ?? throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Payload mancante."));
        var root = document.RootElement;
        var ids = root.GetProperty("sourceIds").EnumerateArray().Select(e => Guid.ParseExact(e.GetString()!, "D")).ToList();
        if (ids.Any(id => !_sources.SourceIds.Contains(id)))
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "L'output committato cita fonti fuori dallo snapshot del turno.");
        }

        return new DocumentChatAnswer(root.GetProperty("mode").GetString()!, root.GetProperty("answer").GetString()!, ids);
    }

    /// <summary>Assemble: l'unico output Committed è la risposta; il testo candidato è inoltrato come delta provvisorio (validato, non ancora committato).</summary>
    public async ValueTask AssembleAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var answer = await DecodeAsync(outputs, ct);
        if (_onDelta is not null)
        {
            await _onDelta(answer.Answer, ct);
        }
    }

    public async ValueTask<int> CommitAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var answer = await DecodeAsync(outputs, ct);
        var text = ChatSourceNotice.Append(answer.Answer, _sources.Unavailable);
        var agent = _factory.CreateBatchAgent(_agent.Name, _agent.Id, _instructions, _agent.ChatOptions, _agent.Phase, _agent.ExpectedFingerprint);
        var session = await agent.CreateSessionAsync(ct);
        var reply = new ConversationalReply(
            text, agent, session, new RestoredChatSession(session, false, "document_chat"), _conversation, _turn,
            DocumentChatWorkflow.ChatConfigVersionFor(_sources.SnapshotHash, _configHash), _sources.SnapshotHash, answer);

        var result = await _committer.CommitAsync(_claim, reply, ct);
        if (result is { Outcome: ChatTurnCommitOutcome.Rejected, Reason: "lease_lost" })
        {
            // Come il dialogo 001: non è un errore del modello né un turno concluso; decidono lo sweep o il reclaim.
            throw new LeaseLostException("Commit della risposta rifiutato (lease_lost): il lease non è più valido.");
        }

        Result = result;
        return 1;
    }
}

/// <summary>
/// Esecutore dei ChatTurn documentali per il dispatcher (<see cref="IChatTurnExecutor"/>): esegue <see cref="DocumentChatWorkflow"/> e, dopo il
/// commit, pubblica lo stato del turno. La registrazione e la scelta fra questo esecutore e <see cref="ConversationalTurnExecutor"/> sono di T123.
/// </summary>
public sealed class DocumentChatTurnExecutor(DocumentChatWorkflow workflow, ChatTurnEvents? events = null) : IChatTurnExecutor
{
    public async Task ExecuteAsync(ChatTurnClaim claim, CancellationToken ct)
    {
        var result = await workflow.ExecuteAsync(claim, events?.DeltaSink(claim), ct);
        if (events is not null && result.Outcome != ChatTurnCommitOutcome.Rejected)
        {
            await events.PublishStateAsync(claim.TurnId); // post-commit: Completed rimanda al resultMessageId persistito
        }
    }
}
