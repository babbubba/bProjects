using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>Piano di chat documentale che espone il risultato del commit atomico del turno (valorizzato dallo stadio Commit).</summary>
internal interface IChatResultPlan : IDocumentBatchPlan
{
    ChatTurnCommitResult? Result { get; }
}

/// <summary>
/// Nota di lavoro della chat oltre contesto: sintesi di scansione o di riduzione, mai prova documentale. <see cref="SourceIds"/> sono le fonti canoniche (unione server),
/// <see cref="Visited"/> le evidenze visitate dai passaggi da cui discende e <see cref="OriginHash"/> l'hash dell'output Committed che l'ha prodotta.
/// </summary>
internal sealed record ChatNote(string Id, string Kind, string Text, IReadOnlyList<Guid> SourceIds, IReadOnlyList<Guid> Visited, string OriginHash);

/// <summary>
/// project-chat-documents (002 T084, contracts/ai-workflow.md §6): chat documentale oltre la capacità di una chiamata. STESSO grafo fisso T043 e STESSO port T044 della
/// chat piccola; il piano dice cosa elaborare e come validare, mai come recuperare. Tre fasi, ciascuna in una AgentSession nuova:
/// <list type="number">
/// <item><b>scan</b>: TUTTE le evidenze dello snapshot del turno, in batch contigui misurati (Pack T069, nessun indice, ranking o top-k). Ogni batch risponde con supporti
/// citati o <c>no_support</c> motivato; le visite le registra il server (tutte le evidenze del batch), non il modello.</item>
/// <item><b>reduce</b>: finché le note con gli originali pertinenti non entrano in una chiamata finale, le note si uniscono a gruppi di almeno due (Pack con minimo 2, ultimo
/// nodo singolo inoltrato senza AI, livello strettamente decrescente). Il server unisce fonti, visite e conflitto; il modello scrive solo il testo della nota unita, con gli
/// originali delle fonti citate riaperti nel payload.</item>
/// <item><b>final</b>: risposta nel contratto della chat piccola (v2 UUID o v3 alias), con le note, il registro di copertura e le evidenze originali riaperte; cita solo fonti riaperte.</item>
/// </list>
/// I batch sono derivati append-only con i genitori Committed: il numero totale dipende dagli output e il piano si ricostruisce ad ogni replay dagli output Committed
/// (<see cref="IBatchOutputs"/>), senza stato di processo autorevole. Una risposta completa non esiste con evidenze non visitate (<c>coverage_incomplete</c>).
/// </summary>
internal sealed class DocumentLongChatPlan : IChatResultPlan
{
    public const string AlgorithmVersion = "chat-scan-v1";
    public const string SchemaVersion = "document-chat-long-1";
    public const string ValidationVersion = "document-chat-long-validation-v1";
    public const string PromptVersion = "document-chat-long-v1";
    public const string StageScan = "scan";
    public const string StageReduce = "reduce";
    public const string StageFinal = "final";

    /// <summary>Stima conservativa dell'output di scansione per evidenza del batch (nota o voce): limita la dimensione del batch rispetto all'output massimo della fase.</summary>
    public const int ScanOutputTokensPerEvidence = 96;

    private static readonly JsonElement ScanSchema = JsonDocument.Parse(
        """
        {"type":"object","additionalProperties":false,"required":["outcome","reason","notes"],
         "properties":{"outcome":{"type":"string","enum":["support","no_support"]},"reason":{"type":"string"},
           "notes":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["kind","text","sources"],
             "properties":{"kind":{"type":"string","enum":["support","conflict"]},"text":{"type":"string","minLength":1},
               "sources":{"type":"array","minItems":1,"items":{"type":"string","minLength":1}}}}}}}
        """).RootElement.Clone();

    private static readonly JsonElement ReduceSchema = JsonDocument.Parse(
        """
        {"type":"object","additionalProperties":false,"required":["text","conflict"],
         "properties":{"text":{"type":"string","minLength":1},"conflict":{"type":"boolean"}}}
        """).RootElement.Clone();

    private static readonly Lazy<string> NotesAddendum = new(() => ConversationalAgent.LoadPrompt("document-chat-notes.md"));

    private sealed record StageSpec(string Stage, string Instructions, string? SchemaJson, BatchAgentSpec Agent);

    private sealed record Desc(
        int Ordinal, string Stage, int Level, DerivedBatchInput Derived, IReadOnlyList<ChatEvidence> Evidence, IReadOnlyList<ChatNote> Notes,
        IReadOnlyList<ChatEvidence> Reopened, IReadOnlyList<ChatMessage> Messages)
    {
        public BatchRef Ref => new(Ordinal, Derived.BatchId, Derived.InputHash);
    }

    private sealed record Entry(Desc? Desc, ChatNote? Carried);

    private readonly ChatTurnClaim _claim;
    private readonly ChatTurn _turn;
    private readonly Conversation _conversation;
    private readonly ChatSnapshotSources _sources;
    private readonly IReadOnlyList<ChatMessage> _dialogue;
    private readonly string _question;
    private readonly string _finalInstructions;
    private readonly ChatAliasCatalog? _catalog;
    private readonly AgentFactory _factory;
    private readonly IChatTurnCommitter _committer;
    private readonly Func<string, CancellationToken, ValueTask>? _onDelta;
    private readonly DocumentRuntimeProfile _profile;
    private readonly PlannerLimits _limits;
    private readonly int _maxLevels;
    private readonly string _manifestIdentity;
    private readonly string _configHash;
    private readonly string _definition;
    private readonly string _contract;
    private readonly StageSpec _scan;
    private readonly StageSpec _reduce;
    private readonly StageSpec _final;
    private readonly Dictionary<Guid, int> _sourceOrder = [];
    private readonly Dictionary<Guid, int> _evidenceOrder = [];
    private readonly Dictionary<int, WorkOutput> _outputs = [];
    private readonly List<Desc> _descs = [];
    private List<Entry>? _current;
    private int _level;
    private int _scanBatches;
    private bool _finalAdded;
    private bool _coverageVerified;

    public DocumentLongChatPlan(
        ChatTurnClaim claim, ChatTurn turn, Conversation conversation, ChatSnapshotSources sources, IReadOnlyList<ConversationMessage> history, string question,
        string baseInstructions, DocumentRuntimeProfile profile, bool structured, AgentFactory factory, InternalModelClient client, JsonElement answerSchema,
        IChatTurnCommitter committer, Func<string, CancellationToken, ValueTask>? onDelta, ChatAliasCatalog? catalog, int maxLevels)
    {
        _claim = claim;
        _turn = turn;
        _conversation = conversation;
        _sources = sources;
        _dialogue = [.. history.Select(m => new ChatMessage(m.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant, m.Body))];
        _question = question;
        _catalog = catalog;
        _factory = factory;
        _committer = committer;
        _onDelta = onDelta;
        _profile = profile;
        _maxLevels = maxLevels;
        _limits = new PlannerLimits(profile.InputBudgetTokens, profile.OutputTokens, OutputExpansion: 1.0);
        _definition = catalog is null ? DocumentChatWorkflow.DefinitionVersion : DocumentChatWorkflow.DefinitionVersionV3;
        _contract = catalog is null ? "uuid" : "alias";
        _finalInstructions = baseInstructions + "\n\n" + NotesAddendum.Value;

        var order = 0;
        foreach (var (evidence, index) in sources.Evidence.Select((e, i) => (e, i)))
        {
            _evidenceOrder[evidence.EvidenceId] = index;
            foreach (var source in evidence.Sources)
            {
                _sourceOrder.TryAdd(source.SourceId, order++);
            }
        }

        var settings = factory.Options.ForPhase(AiPhase.Chat);
        _scan = Stage(StageScan, DocumentLongChat.ScanInstructions, structured ? ScanSchema : null, "document_chat_scan", client);
        _reduce = Stage(StageReduce, DocumentLongChat.ReduceInstructions, structured ? ReduceSchema : null, "document_chat_reduce", client);
        _final = Stage(StageFinal, _finalInstructions, structured ? answerSchema : null, catalog is null ? "document_chat" : DocumentChatWorkflow.SchemaNameV3, client);

        _configHash = DocumentChatWorkflow.Sha(JsonSerializer.Serialize(new
        {
            prompt = PromptVersion, contract = _contract, scan = DocumentChatWorkflow.Sha(_scan.Instructions), reduce = DocumentChatWorkflow.Sha(_reduce.Instructions),
            final = DocumentChatWorkflow.Sha(_finalInstructions), schema = SchemaVersion, validation = ValidationVersion, catalog = catalog is null ? null : ChatAliasCatalog.Version,
            structured, scanOutputPerEvidence = ScanOutputTokensPerEvidence, maxLevels, model = settings.ModelId, maxOutput = settings.MaxOutputTokens,
            thinking = settings.EnableThinking, inputBudget = profile.InputBudgetTokens,
        }));

        var sourceHash = DocumentChatWorkflow.Sha(
            $"chat-source|{sources.SnapshotHash}|{turn.SourceCutoffSequence}|{turn.TriggerMessageId:N}|{string.Join(',', sources.Evidence.Select(e => e.EvidenceVersionId.ToString("N")).Distinct())}");
        var policyHash = turn.PolicyHash is { } p && Regex.IsMatch(p, "^[0-9a-f]{64}$") ? p : DocumentChatWorkflow.Sha("document-chat-policy-v1");
        var plan = new JsonObject
        {
            ["algorithm"] = AlgorithmVersion,
            ["definition"] = _definition,
            ["evidence"] = DocumentChatWorkflow.Sha(string.Join(',', sources.Evidence.Select(e => e.EvidenceId.ToString("N")))),
            ["graph"] = new JsonArray(DocumentBatchWorkflow.Stages.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["maxLevels"] = maxLevels,
            ["regime"] = turn.Regime?.ToString() ?? "Unclassified",
            ["stages"] = new JsonArray(StageScan, StageReduce, StageFinal),
        };
        if (catalog is not null)
        {
            plan["catalog"] = catalog.CatalogHash;
        }

        var planJson = plan.ToJsonString();
        Manifest = new WorkManifestInput(_configHash, sourceHash, profile.Fingerprint, _definition, AlgorithmVersion, policyHash, planJson);
        _manifestIdentity = CanonicalJson.Sha256Hex(JsonSerializer.Serialize(new
        {
            configHash = _configHash, source = sourceHash, runtime = profile.Fingerprint, definition = _definition,
            algorithm = AlgorithmVersion, policy = policyHash, plan = JsonDocument.Parse(planJson).RootElement,
        }));
    }

    private StageSpec Stage(string stage, string instructions, JsonElement? schema, string schemaName, InternalModelClient client)
    {
        var options = schema is { } s
            ? client.CreateChatOptions(s, schemaName, temperature: 0.2f, phase: AiPhase.Chat)
            : client.CreateChatOptions(temperature: 0.2f, phase: AiPhase.Chat);
        return new StageSpec(
            stage, instructions, schema?.GetRawText(),
            new BatchAgentSpec(ConversationalAgent.AgentName, $"{DocumentChatWorkflow.AgentId}-{stage}", instructions, options, AiPhase.Chat, _profile.Fingerprint));
    }

    public ChatTurnCommitResult? Result { get; private set; }

    public WorkManifestInput Manifest { get; }

    public WorkLookupExpectation Expectation => new(SchemaVersion, ValidationVersion);

    public BatchAgentSpec Agent => _final.Agent;

    public BatchAgentSpec AgentFor(BatchRef batch) => StageOf(DescOf(batch)).Agent;

    public bool HasBatch(int ordinal) => throw new NotSupportedException("Il numero di batch dipende dagli output Committed: usare HasBatchAsync.");

    public ValidatedBatch Validate(BatchRef batch, string responseText) => throw new NotSupportedException("La validazione richiede gli output precedenti: usare ValidateAsync.");

    public async ValueTask<bool> HasBatchAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        await AdvanceAsync(prior, ct);
        return ordinal >= 0 && ordinal < _descs.Count;
    }

    public async ValueTask<DerivedBatchInput> PrepareAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        await AdvanceAsync(prior, ct);
        return ordinal >= 0 && ordinal < _descs.Count
            ? _descs[ordinal].Derived
            : throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"La chat oltre contesto non ha il batch con ordinale {ordinal}.");
    }

    public async ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, IBatchOutputs prior, CancellationToken ct)
    {
        await AdvanceAsync(prior, ct);
        return DescOf(batch).Messages;
    }

    public async ValueTask<ValidatedBatch> ValidateAsync(BatchRef batch, string responseText, IBatchOutputs prior, CancellationToken ct)
    {
        await AdvanceAsync(prior, ct);
        var desc = DescOf(batch);
        return desc.Stage switch
        {
            StageScan => ValidateScan(desc, responseText),
            StageReduce => ValidateReduce(desc, responseText),
            _ => ValidateFinal(desc, responseText),
        };
    }

    private Desc DescOf(BatchRef batch)
    {
        if (batch.Ordinal < 0 || batch.Ordinal >= _descs.Count)
        {
            throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"Batch {batch.Ordinal} della chat oltre contesto non pianificato.");
        }

        var desc = _descs[batch.Ordinal];
        return desc.Derived.BatchId == batch.BatchId && desc.Derived.InputHash == batch.InputHash
            ? desc
            : throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, $"Il batch {batch.Ordinal} non appartiene al piano ricostruito della chat oltre contesto.");
    }

    private StageSpec StageOf(Desc desc) => desc.Stage switch { StageScan => _scan, StageReduce => _reduce, _ => _final };

    // ------------------------------------------------------------------ pianificazione (ricostruita dagli output Committed)

    private Func<PersistedSource, string> LabelFunc => source => "fonte:" + LabelOf(source.SourceId);

    private string LabelOf(Guid sourceId) => _catalog is null
        ? sourceId.ToString("D")
        : _catalog.AliasOf(sourceId) ?? throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Fonte dello snapshot senza alias nel catalogo.");

    private Guid? Resolve(string label)
    {
        if (_catalog is not null)
        {
            return _catalog.Source(label)?.SourceId;
        }

        return Guid.TryParseExact(label, "D", out var id) && id.ToString("D") == label && _sources.SourceIds.Contains(id) ? id : null;
    }

    private async ValueTask AdvanceAsync(IBatchOutputs prior, CancellationToken ct)
    {
        if (_current is null)
        {
            await PlanScanAsync(ct);
        }

        while (!_finalAdded)
        {
            var notes = new List<ChatNote>();
            var hashes = new List<string>();
            foreach (var entry in _current!)
            {
                if (entry.Carried is { } carried)
                {
                    notes.Add(carried);
                    hashes.Add(carried.OriginHash);
                    continue;
                }

                var output = await OutputOfAsync(entry.Desc!, prior, ct);
                if (output is null)
                {
                    return;   // il livello non è completo: il prossimo batch è quello senza output
                }

                hashes.Add(output.OutputHash);
                if (entry.Desc!.Stage == StageScan)
                {
                    notes.AddRange(ReadScan(entry.Desc, output));
                }
                else
                {
                    notes.Add(ReadReduce(entry.Desc, output));
                }
            }

            if (_level == 0)
            {
                VerifyCoverage(_current!.Select(e => e.Desc!));
            }

            var unique = hashes.Distinct(StringComparer.Ordinal).ToList();
            var final = await TryFinalAsync(notes, unique, ct);
            if (final is not null)
            {
                _descs.Add(final);
                _finalAdded = true;
                return;
            }

            await PlanReduceAsync(notes, ct);
        }
    }

    private async ValueTask PlanScanAsync(CancellationToken ct)
    {
        var packed = await DocumentBatchPacking.PackLevelAsync(_sources.Evidence, 1, _limits, e => e.EvidenceId.ToString("D"), MeasureScanAsync, ct);
        _current = [];
        foreach (var batch in packed)
        {
            var derived = DocumentBatchPacking.ToDerived(
                batch, $"ChatTurn|{_claim.TurnId:N}", _manifestIdentity, _definition, StageScan, null, 0, [], e => e.EvidenceId.ToString("D"), SchemaVersion, PromptVersion);
            var desc = new Desc(batch.Ordinal, StageScan, 0, derived, batch.Members, [], [], ScanMessages(batch.Members));
            _descs.Add(desc);
            _current.Add(new Entry(desc, null));
        }

        _scanBatches = packed.Count;
    }

    private async ValueTask PlanReduceAsync(IReadOnlyList<ChatNote> notes, CancellationToken ct)
    {
        if (notes.Count < 2)
        {
            var only = notes.Count == 1 ? notes[0].Id : "nessuna";
            throw new DocumentWorkException(
                DocumentWorkException.DocumentIncomplete,
                $"La nota {only} con gli originali pertinenti non entra nel budget della risposta finale ({_limits.InputBudgetTokens} token) e non è riducibile ulteriormente: unità irriducibile.");
        }

        if (++_level > _maxLevels)
        {
            throw new DocumentWorkException(
                DocumentWorkException.CallLimitExceeded, $"La riduzione delle note supera il massimo di {_maxLevels} livelli senza raggiungere la risposta finale.");
        }

        var entries = new List<Entry>();
        var start = 0;
        while (start < notes.Count)
        {
            var batch = await DocumentBatchPacking.PackNextAsync(notes, start, _descs.Count, 2, _limits, n => n.Id, MeasureReduceAsync, ct);
            if (batch.Carried)
            {
                entries.Add(new Entry(null, batch.Members[0]));
            }
            else
            {
                var parents = batch.Members.Select(n => n.OriginHash).Distinct(StringComparer.Ordinal).ToList();
                var derived = DocumentBatchPacking.ToDerived(
                    batch, $"ChatTurn|{_claim.TurnId:N}", _manifestIdentity, _definition, StageReduce, null, _level, parents, n => n.Id, SchemaVersion, PromptVersion);
                var reopened = Reopen(batch.Members.SelectMany(n => n.SourceIds));
                var desc = new Desc(batch.Ordinal, StageReduce, _level, derived, [], batch.Members, reopened, ReduceMessages(batch.Members, reopened));
                _descs.Add(desc);
                entries.Add(new Entry(desc, null));
            }

            start += batch.Members.Count;
        }

        _current = entries;
    }

    private async ValueTask<Desc?> TryFinalAsync(IReadOnlyList<ChatNote> notes, IReadOnlyList<string> parentHashes, CancellationToken ct)
    {
        var reopened = Reopen(notes.SelectMany(n => n.SourceIds));
        var messages = FinalMessages(notes, reopened);
        var measure = await MeasureAsync(_final, messages, 0, ct);
        if (!_limits.Fits(measure))
        {
            return null;
        }

        var packed = new PackedBatch<ChatNote>(_descs.Count, notes, measure);
        var derived = DocumentBatchPacking.ToDerived(
            packed, $"ChatTurn|{_claim.TurnId:N}", _manifestIdentity, _definition, StageFinal, null, _level + 1, parentHashes, n => n.Id, SchemaVersion, PromptVersion);
        return new Desc(packed.Ordinal, StageFinal, _level + 1, derived, [], notes, reopened, messages);
    }

    private IReadOnlyList<ChatEvidence> Reopen(IEnumerable<Guid> sourceIds)
    {
        var cited = sourceIds.ToHashSet();
        return [.. _sources.Evidence.Where(e => e.Sources.Any(s => cited.Contains(s.SourceId)))];
    }

    private async ValueTask<UnitMeasure> MeasureAsync(StageSpec stage, IReadOnlyList<ChatMessage> messages, int owned, CancellationToken ct)
    {
        var measured = await _factory.Preflight.MeasureAsync(new DocumentPayload(AiPhase.Chat, stage.Instructions, messages, stage.SchemaJson), ct, _profile.Fingerprint);
        return new UnitMeasure(measured.Tokens, measured.Quality, measured.PayloadHash, owned);
    }

    private ValueTask<UnitMeasure> MeasureScanAsync(IReadOnlyList<ChatEvidence> members, CancellationToken ct) =>
        MeasureAsync(_scan, ScanMessages(members), members.Count * ScanOutputTokensPerEvidence, ct);

    private ValueTask<UnitMeasure> MeasureReduceAsync(IReadOnlyList<ChatNote> members, CancellationToken ct) =>
        MeasureAsync(_reduce, ReduceMessages(members, Reopen(members.SelectMany(n => n.SourceIds))), 0, ct);

    // ------------------------------------------------------------------ messaggi (testo composto dal solo server)

    private IReadOnlyList<ChatMessage> Compose(StringBuilder text, int documentLength)
    {
        var messages = new List<ChatMessage>(_dialogue)
        {
            new(ChatRole.User, text.ToString()) { AdditionalProperties = new() { [ContextComposition.DocumentSpansProperty] = new[] { 0, documentLength } } },
        };
        return messages;
    }

    private IReadOnlyList<ChatMessage> ScanMessages(IReadOnlyList<ChatEvidence> members)
    {
        var text = new StringBuilder();
        text.Append("Gruppo di evidenze documentali da esaminare (").Append(members.Count).Append("):\n");
        foreach (var evidence in members)
        {
            DocumentChatWorkflow.AppendEvidence(text, evidence, LabelFunc);
        }

        var length = text.Length;
        text.Append("\nMessaggio del project manager:\n").Append(_question)
            .Append("\n\nEsamina TUTTE le evidenze del gruppo e rispondi ora con l'unico oggetto JSON {\"outcome\":...,\"reason\":...,\"notes\":[...]}.");
        return Compose(text, length);
    }

    private IReadOnlyList<ChatMessage> ReduceMessages(IReadOnlyList<ChatNote> notes, IReadOnlyList<ChatEvidence> reopened)
    {
        var text = new StringBuilder();
        AppendReopened(text, reopened, "Evidenze originali riaperte (");
        AppendNotes(text, notes, "Note da unire (");
        var length = text.Length;
        text.Append("\nMessaggio del project manager:\n").Append(_question)
            .Append("\n\nUnisci le note in una sola nota senza perdere nulla e rispondi ora con l'unico oggetto JSON {\"text\":...,\"conflict\":...}.");
        return Compose(text, length);
    }

    private IReadOnlyList<ChatMessage> FinalMessages(IReadOnlyList<ChatNote> notes, IReadOnlyList<ChatEvidence> reopened)
    {
        var text = new StringBuilder();
        AppendReopened(text, reopened, "Evidenze documentali riaperte per questo turno (");
        text.Append("\nRegistro di copertura: tutte le ").Append(_sources.Evidence.Count).Append(" evidenze selezionate sono state esaminate in ")
            .Append(_scanBatches).Append(" passaggi di scansione.\n");
        AppendNotes(text, notes, "Note di scansione (");
        var length = text.Length;
        text.Append("\nMessaggio del project manager:\n").Append(_question)
            .Append("\n\nRispondi ora con l'unico oggetto JSON {\"mode\":...,\"answer\":...}.");
        return Compose(text, length);
    }

    private void AppendReopened(StringBuilder text, IReadOnlyList<ChatEvidence> reopened, string title)
    {
        text.Append(title).Append(reopened.Count).Append("):\n");
        if (reopened.Count == 0)
        {
            text.Append("(nessuna evidenza riaperta)\n");
        }

        foreach (var evidence in reopened)
        {
            DocumentChatWorkflow.AppendEvidence(text, evidence, LabelFunc);
        }
    }

    private void AppendNotes(StringBuilder text, IReadOnlyList<ChatNote> notes, string title)
    {
        text.Append('\n').Append(title).Append(notes.Count).Append("):\n");
        if (notes.Count == 0)
        {
            text.Append("(nessuna nota: nessuna evidenza esaminata sostiene la domanda)\n");
        }

        foreach (var note in notes)
        {
            text.Append($"<<<NOTA tipo={note.Kind} fonti={string.Join(',', note.SourceIds.Select(LabelOf))}>>>\n").Append(note.Text).Append("\n<<<FINE NOTA>>>\n");
        }
    }

    // ------------------------------------------------------------------ lettura degli output Committed

    private async ValueTask<WorkOutput?> OutputOfAsync(Desc desc, IBatchOutputs prior, CancellationToken ct)
    {
        if (_outputs.TryGetValue(desc.Ordinal, out var known))
        {
            return known;
        }

        var output = await prior.GetByOrdinalAsync(desc.Ordinal, ct);
        if (output is null || output.Status != WorkOutputStatus.Committed)
        {
            return null;
        }

        if (output.BatchId != desc.Derived.BatchId || output.InputHash != desc.Derived.InputHash)
        {
            throw new DocumentWorkException(
                DocumentWorkException.CheckpointIncompatible, $"L'output del batch {desc.Ordinal} non appartiene al piano ricostruito della chat oltre contesto.");
        }

        _outputs[desc.Ordinal] = output;
        return output;
    }

    private static JsonElement PayloadOf(WorkOutput output, string stage)
    {
        try
        {
            var root = JsonDocument.Parse(output.PayloadJson ?? throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Payload mancante.")).RootElement;
            return root.GetProperty("schemaVersion").GetString() == SchemaVersion && root.GetProperty("stage").GetString() == stage
                ? root.Clone()
                : throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, $"Il WorkOutput non è del contratto della chat oltre contesto ({stage}).");
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, $"Il payload del WorkOutput della chat oltre contesto ({stage}) non è nel formato atteso.");
        }
    }

    private IReadOnlyList<ChatNote> ReadScan(Desc desc, WorkOutput output)
    {
        var root = PayloadOf(output, StageScan);
        var expected = desc.Evidence.Select(e => e.EvidenceId).ToHashSet();
        var visited = root.GetProperty("visited").EnumerateArray().Select(e => e.GetGuid()).ToList();
        var covered = ParseGuids(output.CoveredIdsJson);
        if (!expected.SetEquals(visited) || !expected.SetEquals(covered))
        {
            throw new DocumentWorkException(
                DocumentWorkException.CoverageIncomplete, $"Il registro di copertura del batch di scansione {desc.Ordinal} non coincide con le evidenze del batch: risultato non completo.");
        }

        var allowed = desc.Evidence.SelectMany(e => e.Sources).Select(s => s.SourceId).ToHashSet();
        var all = desc.Evidence.Select(e => e.EvidenceId).ToList();
        var notes = new List<ChatNote>();
        foreach (var note in root.GetProperty("notes").EnumerateArray())
        {
            var sources = note.GetProperty("sourceIds").EnumerateArray().Select(e => e.GetGuid()).ToList();
            if (sources.Count == 0 || sources.Any(s => !allowed.Contains(s)))
            {
                throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, $"Una nota del batch di scansione {desc.Ordinal} cita fonti fuori dal batch.");
            }

            notes.Add(new ChatNote(note.GetProperty("id").GetString()!, note.GetProperty("kind").GetString()!, note.GetProperty("text").GetString()!, sources, all, output.OutputHash));
        }

        return notes;
    }

    private ChatNote ReadReduce(Desc desc, WorkOutput output)
    {
        var root = PayloadOf(output, StageReduce);
        var (sources, visited, conflict) = Union(desc.Notes);
        var stored = root.GetProperty("sourceIds").EnumerateArray().Select(e => e.GetGuid()).ToList();
        var storedVisited = root.GetProperty("visited").EnumerateArray().Select(e => e.GetGuid()).ToList();
        if (!stored.SequenceEqual(sources) || !storedVisited.SequenceEqual(visited))
        {
            throw new DocumentWorkException(
                DocumentWorkException.IntegrityConflict, $"L'unione di fonti o visite dell'output di riduzione {desc.Ordinal} non coincide con quella ricalcolata dalle note in ingresso.");
        }

        var kind = root.GetProperty("kind").GetString()!;
        if (conflict && kind != "conflict")
        {
            throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"L'output di riduzione {desc.Ordinal} ha perso il conflitto delle note in ingresso.");
        }

        return new ChatNote(root.GetProperty("id").GetString()!, kind, root.GetProperty("text").GetString()!, sources, visited, output.OutputHash);
    }

    private static List<Guid> ParseGuids(string json)
    {
        try
        {
            return [.. JsonSerializer.Deserialize<string[]>(json)!.Select(s => Guid.ParseExact(s, "D"))];
        }
        catch (Exception ex) when (ex is JsonException or FormatException or NullReferenceException)
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Il registro di copertura dell'output non è leggibile.");
        }
    }

    /// <summary>Unione deterministica (server): fonti nell'ordine di lettura, visite nell'ordine dello snapshot, conflitto se una nota in ingresso lo è.</summary>
    private (List<Guid> Sources, List<Guid> Visited, bool Conflict) Union(IReadOnlyList<ChatNote> notes) =>
    (
        [.. notes.SelectMany(n => n.SourceIds).Distinct().OrderBy(s => _sourceOrder.GetValueOrDefault(s, int.MaxValue))],
        [.. notes.SelectMany(n => n.Visited).Distinct().OrderBy(e => _evidenceOrder.GetValueOrDefault(e, int.MaxValue))],
        notes.Any(n => n.Kind == "conflict")
    );

    private void VerifyCoverage(IEnumerable<Desc> scans)
    {
        var visited = scans.SelectMany(d => d.Evidence.Select(e => e.EvidenceId)).ToList();
        var expected = _sources.Evidence.Select(e => e.EvidenceId).ToList();
        var missing = expected.Except(visited).ToList();
        if (missing.Count > 0 || visited.Count != visited.Distinct().Count())
        {
            throw new DocumentWorkException(
                DocumentWorkException.CoverageIncomplete,
                $"{missing.Count} evidenze dello snapshot non risultano visitate (prima: {(missing.Count > 0 ? missing[0].ToString("D") : "n/d")}): nessun risultato completo.");
        }

        _coverageVerified = true;
    }

    // ------------------------------------------------------------------ validazione degli output nuovi

    private static DocumentWorkException Invalid(string stage, string reason) =>
        new(DocumentWorkException.WorkOutputInvalid, $"Output di {stage} non valido ({reason}): nessun output committato.");

    private static JsonElement ParseObject(string text, string stage, params string[] properties)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowDuplicateProperties = false });
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Invalid(stage, "la risposta non è un unico oggetto JSON valido");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(stage, "la risposta non è un oggetto JSON");
        }

        var names = root.EnumerateObject().Select(p => p.Name).ToList();
        return names.Count == properties.Length && properties.All(names.Contains) ? root : throw Invalid(stage, "proprietà mancanti o non ammesse");
    }

    private static string RequireString(JsonElement root, string name, string stage, bool allowEmpty = false)
    {
        var property = root.GetProperty(name);
        return property.ValueKind == JsonValueKind.String && (allowEmpty || !string.IsNullOrWhiteSpace(property.GetString()))
            ? property.GetString()!
            : throw Invalid(stage, $"{name} assente o di tipo errato");
    }

    private static void RequireNoMarker(string text, string stage)
    {
        if (text.Contains("[fonte:", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(stage, "il testo di una nota non può contenere marcatori di fonte");
        }
    }

    private ValidatedBatch ValidateScan(Desc desc, string responseText)
    {
        var root = ParseObject(responseText, StageScan, "outcome", "reason", "notes");
        var outcome = RequireString(root, "outcome", StageScan);
        var reason = RequireString(root, "reason", StageScan, allowEmpty: true);
        var array = root.GetProperty("notes");
        if (outcome is not ("support" or "no_support") || array.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(StageScan, "outcome o notes non ammessi");
        }

        var allowed = desc.Evidence.SelectMany(e => e.Sources).Select(s => s.SourceId).ToHashSet();
        var all = desc.Evidence.Select(e => e.EvidenceId).ToList();
        var notes = new List<object>();
        var citations = new List<string>();
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(StageScan, "nota non valida");
            }

            var names = item.EnumerateObject().Select(p => p.Name).ToList();
            if (names.Count != 3 || !new[] { "kind", "text", "sources" }.All(names.Contains))
            {
                throw Invalid(StageScan, "proprietà della nota mancanti o non ammesse");
            }

            var kind = RequireString(item, "kind", StageScan);
            var text = RequireString(item, "text", StageScan);
            RequireNoMarker(text, StageScan);
            if (kind is not ("support" or "conflict") || item.GetProperty("sources").ValueKind != JsonValueKind.Array)
            {
                throw Invalid(StageScan, "kind o sources non ammessi");
            }

            var sources = new List<Guid>();
            foreach (var label in item.GetProperty("sources").EnumerateArray())
            {
                var resolved = label.ValueKind == JsonValueKind.String ? Resolve(label.GetString()!) : null;
                if (resolved is not { } id || !allowed.Contains(id))
                {
                    throw Invalid(StageScan, "nota che cita una fonte fuori dal batch esaminato");
                }

                if (sources.Contains(id))
                {
                    throw Invalid(StageScan, "fonte duplicata nella stessa nota");
                }

                sources.Add(id);
            }

            if (sources.Count == 0)
            {
                throw Invalid(StageScan, "nota senza fonti");
            }

            sources = [.. sources.OrderBy(s => _sourceOrder.GetValueOrDefault(s, int.MaxValue))];
            var ids = sources.Select(s => s.ToString("D")).ToArray();
            citations.AddRange(ids);
            notes.Add(new { id = $"S{desc.Ordinal}.{++index}", kind, text, sourceIds = ids });
        }

        if (outcome == "support" && (notes.Count == 0 || reason.Length > 0))
        {
            throw Invalid(StageScan, "support richiede almeno una nota e nessun motivo");
        }

        if (outcome == "no_support" && (notes.Count > 0 || string.IsNullOrWhiteSpace(reason)))
        {
            throw Invalid(StageScan, "no_support richiede un motivo e nessuna nota");
        }

        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion, stage = StageScan, outcome, reason, visited = all.Select(e => e.ToString("D")).ToArray(), notes,
            aiOutputHash = CanonicalJson.Sha256Hex(responseText),
        });
        return Validated(desc, payload, all.Select(e => e.ToString("D")), new { citations = citations.Distinct().ToArray() });
    }

    private ValidatedBatch ValidateReduce(Desc desc, string responseText)
    {
        var root = ParseObject(responseText, StageReduce, "text", "conflict");
        var text = RequireString(root, "text", StageReduce);
        RequireNoMarker(text, StageReduce);
        var flag = root.GetProperty("conflict");
        if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid(StageReduce, "conflict non booleano");
        }

        var (sources, visited, conflict) = Union(desc.Notes);
        var kind = conflict || flag.GetBoolean() ? "conflict" : "support";
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion, stage = StageReduce, id = $"R{desc.Ordinal}", kind, text, sourceIds = sources.Select(s => s.ToString("D")).ToArray(),
            visited = visited.Select(e => e.ToString("D")).ToArray(), inputs = desc.Notes.Select(n => n.Id).ToArray(), aiOutputHash = CanonicalJson.Sha256Hex(responseText),
        });
        return Validated(desc, payload, visited.Select(e => e.ToString("D")), new { citations = sources.Select(s => s.ToString("D")).ToArray() });
    }

    private ValidatedBatch ValidateFinal(Desc desc, string responseText)
    {
        var allowed = desc.Reopened.SelectMany(e => e.Sources).Select(s => s.SourceId).ToHashSet();
        string? aiOutputHash = null;
        DocumentChatAnswer answer;
        if (_catalog is null)
        {
            answer = DocumentChatContract.Validate(responseText, allowed);
        }
        else
        {
            var resolved = DocumentChatContract.ValidateAlias(responseText, _catalog);
            answer = resolved.Answer;
            aiOutputHash = resolved.AiOutputHash;
            if (answer.SourceIds.Any(s => !allowed.Contains(s)))
            {
                throw Invalid(StageFinal, "citata una fonte non riaperta nel passaggio finale");
            }
        }

        var all = _sources.Evidence.Select(e => e.EvidenceId.ToString("D")).ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion, stage = StageFinal, contract = _contract, catalogHash = _catalog?.CatalogHash, aiOutputHash, mode = answer.Mode, answer = answer.Answer,
            sourceIds = answer.SourceIds.Select(id => id.ToString("D")).ToArray(), visited = all, notes = desc.Notes.Select(n => n.Id).ToArray(),
        });
        return Validated(desc, payload, all, new { citations = answer.SourceIds.Select(id => id.ToString("D")).ToArray() });
    }

    private ValidatedBatch Validated(Desc desc, string payload, IEnumerable<string> covered, object maps) =>
        new(new WorkOutputInput(
            desc.Derived.BatchId, DocumentBatchWorkflow.InvokeId, desc.Derived.InputHash, CanonicalJson.Sha256Hex(payload), payload, null, null,
            JsonSerializer.Serialize(covered), JsonSerializer.Serialize(maps), null, SchemaVersion, ValidationVersion, null));

    // ------------------------------------------------------------------ assemblaggio e commit

    private async ValueTask<DocumentChatAnswer> DecodeFinalAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        await AdvanceAsync(outputs, ct);
        var desc = _finalAdded ? _descs[^1] : null;
        var output = desc is null ? null : await OutputOfAsync(desc, outputs, ct);
        if (desc is null || output is null)
        {
            throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, "Assemble senza l'output finale della chat: nessuna risposta completa da committare.");
        }

        if (!_coverageVerified)
        {
            VerifyCoverage(_descs.Where(d => d.Stage == StageScan));
        }

        var root = PayloadOf(output, StageFinal);
        var ids = root.GetProperty("sourceIds").EnumerateArray().Select(e => Guid.ParseExact(e.GetString()!, "D")).ToList();
        if (_catalog is not null && root.GetProperty("catalogHash").GetString() != _catalog.CatalogHash)
        {
            throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Il catalogo alias ricostruito dallo snapshot ha un hash diverso da quello committato.");
        }

        var allowed = desc.Reopened.SelectMany(e => e.Sources).Select(s => s.SourceId).ToHashSet();
        var mode = root.GetProperty("mode").GetString()!;
        var answer = root.GetProperty("answer").GetString()!;
        // Validazione esistente sul canonico (fonti riaperte, mode/citazioni coerenti): mai una seconda risoluzione degli alias.
        var validated = DocumentChatContract.Validate(JsonSerializer.Serialize(new { mode, answer }), allowed);
        return validated.SourceIds.SequenceEqual(ids)
            ? validated
            : throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Le fonti committate non coincidono con quelle citate nella risposta canonica.");
    }

    public async ValueTask AssembleAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var answer = await DecodeFinalAsync(outputs, ct);
        if (_onDelta is not null)
        {
            await _onDelta(answer.Answer, ct);
        }
    }

    public async ValueTask<int> CommitAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var answer = await DecodeFinalAsync(outputs, ct);
        Result = await DocumentChatPlan.CommitReplyAsync(
            _committer, _claim, _factory, _final.Agent, _finalInstructions, _conversation, _turn, _sources,
            DocumentChatWorkflow.ChatConfigVersionFor(_sources.SnapshotHash, _configHash, aliasContract: _catalog is not null), answer, ct);
        return _descs.Count;
    }
}
