using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>
/// Adattatore alias del piano di riconciliazione (T131, AI §11): lo STESSO motore e lo stesso piano, con messaggi a soli alias, DTO <c>reconcile-alias-1</c> composto sul
/// server e definizione v4. Senza adattatore (<c>null</c>) il piano è quello v2, storico e immutato.
/// </summary>
internal sealed record ReconcileAliasMode(AnalysisAliasCatalog RunCatalog, bool Structured, JsonElement Schema, string Definition);

/// <summary>
/// Stadio Gap dedicato della definizione v6 (D1, AI §13.7): metadati documentali dello snapshot (mappa laterale) per il formato <c>gap-stage-message-1</c>. Presente solo se l'analisi ha almeno un Gap
/// e il preflight al routing ha ammesso lo stadio; implica un <see cref="ReconcileAliasMode"/>. Non entra nel <c>configHash</c> del piano gerarchico.
/// </summary>
internal sealed record GapStageMode(IReadOnlyDictionary<Guid, GapStageEvidenceMeta> Meta);

/// <summary>
/// Riconciliazione gerarchica globale dell'analisi documentale oltre la capacità di una chiamata (002 T078, contracts/ai-workflow.md §4–§5). Stesso grafo fisso
/// T043 e stesso port T044 della singola chiamata: il piano dice cosa elaborare e come validare, mai come recuperare. Il motore di T072–T075 (directory,
/// livelli, riapertura dei candidati, round finale) è puro rispetto al modello e dichiara un solo confine, <see cref="ReconcileInvoker"/>: qui il confine è
/// il grafo. Ogni invio è un batch del grafo (nuova AgentSession, nessun accumulo di sessione); ripreso in un processo nuovo il motore si rigioca sugli output
/// Committed (<see cref="IBatchOutputs"/>) e si ferma alla prima richiesta senza output: quella è il prossimo batch. Nessun nodo nuovo al replay.
/// Con <see cref="ReconcileAliasMode"/> (v4, T131) il modello vede solo alias e restituisce <c>reconcile-alias-1</c>: il WorkOutput conserva il canonico composto più
/// <c>schemaVersion</c>, <c>catalogHash</c>, <c>aiOutputHash</c> e <c>canonicalHash</c>, verificati a ogni replay senza nuova inferenza.
/// </summary>
internal sealed class DocumentReconcilePlan : IDocumentBatchPlan
{
    public const string AlgorithmVersion = "global-reconcile-v1";
    public const string ValidationVersion = "reconcile-validation-v1";
    public const string SchemaVersion = "reconcile-1";
    public const string NodeStage = "reconcile";

    private const int HeaderReserveTokens = 256;

    private readonly AnalysisRunClaim _claim;
    private readonly DocumentAnalysisInput _input;
    private readonly IReadOnlyDictionary<Guid, DirectoryFacts> _facts;
    private readonly DocumentPreflight _preflight;
    private readonly DocumentRuntimeProfile _profile;
    private readonly string _instructions;
    private readonly IServiceScopeFactory _scopes;
    private readonly string _manifestIdentity;
    private readonly ReconciliationContext _context;
    private readonly ReconcileAliasMode? _alias;
    private readonly string? _schemaJson;
    private readonly string _schemaVersion;
    private readonly Dictionary<string, UnitMeasure> _measures = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (string BatchId, string Text)> _texts = [];
    private readonly Dictionary<int, AliasEntry> _aliasEntries = [];
    private readonly Dictionary<Guid, DocumentAnalysisEvidence> _evidence;
    private ReconciliationDirectoryResult? _directory;
    private ReconcileEngineResult? _finished;
    private ReplayState? _finishedState;
    private ReplayState _state = new();
    private (int Ordinal, AliasEntry Entry)? _pending;
    private ReconcileAssembly? _assembled;
    private readonly GapStageMode? _gap;
    private readonly BatchAgentSpec? _gapAgent;
    private readonly string? _gapSchemaJson;
    private GapStagePlanState? _gapState;
    private int _finishedBatches = -1;
    private bool _verified;

    public DocumentReconcilePlan(
        AnalysisRunClaim claim, DocumentAnalysisInput input, IReadOnlyDictionary<Guid, DirectoryFacts> facts, DocumentPreflight preflight, DocumentRuntimeProfile profile,
        AiOptions options, InternalModelClient client, IServiceScopeFactory scopes, string? rootPolicyHash = null, DocumentLimits? limits = null, ReconcileAliasMode? alias = null,
        GapStageMode? gapStage = null)
    {
        _claim = claim;
        _input = input;
        _facts = facts;
        _preflight = preflight;
        _profile = profile;
        _scopes = scopes;
        _alias = alias;
        _gap = alias is null ? null : gapStage;
        if (_gap is not null)
        {
            _gapSchemaJson = alias!.Structured ? GapStageContracts.JsonSchema().GetRawText() : null;
            _gapAgent = new BatchAgentSpec(
                "DocumentGapStage", DocumentAnalysisWorkflow.AgentId + "-gap", GapStage.Instructions,
                alias.Structured
                    ? client.CreateChatOptions(GapStageContracts.JsonSchema(), GapStage.SchemaName, temperature: 0.1f, phase: AiPhase.Analysis)
                    : client.CreateChatOptions(temperature: 0.1f, phase: AiPhase.Analysis),
                AiPhase.Analysis, profile.Fingerprint);
        }

        _instructions = alias is null ? ReconciliationHierarchy.Instructions : ReconcileAliasContracts.Instructions;
        _schemaVersion = alias is null ? SchemaVersion : ReconcileAliasContracts.SchemaVersion;
        _schemaJson = alias is { Structured: true } ? alias.Schema.GetRawText() : null;
        var definition = alias?.Definition ?? DocumentAnalysisWorkflow.DefinitionVersion;
        var promptVersion = alias is null ? ReconciliationHierarchy.PromptVersion : ReconcileAliasContracts.PromptVersion;
        _evidence = input.Evidence.ToDictionary(e => e.EvidenceId);

        // Tetti della policy congelata del root (AI §4.5): senza limiti espliciti restano i default del motore.
        _maxLevels = limits?.MaxReconciliationLevels ?? 32;
        _limits = limits is null ? new ReconciliationLimits() : new ReconciliationLimits(MaxCandidates: limits.MaxCandidatesPerRun, MaxDerivedBytes: limits.MaxDerivedBytes);

        var settings = options.ForPhase(AiPhase.Analysis);
        // v2 (storico): forma e hash di configurazione invariati; v4 aggiunge soltanto la modalità di output strutturato.
        var configHash = DocumentAnalysisWorkflow.Sha(alias is null
            ? JsonSerializer.Serialize(new
            {
                prompt = promptVersion, instructions = DocumentAnalysisWorkflow.Sha(_instructions), schema = _schemaVersion, model = settings.ModelId,
                maxOutput = settings.MaxOutputTokens, thinking = settings.EnableThinking, inputBudget = profile.InputBudgetTokens,
            })
            : JsonSerializer.Serialize(new
            {
                prompt = promptVersion, instructions = DocumentAnalysisWorkflow.Sha(_instructions), schema = _schemaVersion, catalog = ReconcileAliasContracts.CatalogVersion,
                canonIds = AnalysisAliasCatalog.CanonIdsVersion, structured = alias.Structured, model = settings.ModelId, maxOutput = settings.MaxOutputTokens,
                thinking = settings.EnableThinking, inputBudget = profile.InputBudgetTokens,
            }));
        Agent = new BatchAgentSpec(
            "DocumentReconcile", DocumentAnalysisWorkflow.AgentId + "-reconcile", _instructions,
            alias is { Structured: true }
                ? client.CreateChatOptions(alias.Schema, ReconcileAliasContracts.SchemaName, temperature: 0.1f, phase: AiPhase.Analysis)
                : client.CreateChatOptions(temperature: 0.1f, phase: AiPhase.Analysis),
            AiPhase.Analysis, profile.Fingerprint);

        var policyHash = rootPolicyHash is not null && System.Text.RegularExpressions.Regex.IsMatch(rootPolicyHash, "^[0-9a-f]{64}$") ? rootPolicyHash : DocumentAnalysisWorkflow.Sha("document-reconcile-policy-v1");
        var plan = new JsonObject
        {
            ["algorithm"] = AlgorithmVersion,
            ["definition"] = definition,
            ["graph"] = new JsonArray(DocumentBatchWorkflow.Stages.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["maxLevels"] = _maxLevels,
        };
        if (alias is not null)
        {
            plan["adapter"] = ReconcileAliasContracts.SchemaVersion;
            plan["catalog"] = alias.RunCatalog.RunIndependentHash;
        }

        if (_gap is not null)
        {
            // Stadio Gap (v6, AI §13.7): le chiavi dello stadio legano l'identità del manifest (algoritmo `gap-stage-1`), mai il configHash della gerarchia v4.
            plan["gapStage"] = new JsonObject(GapStage.FreezeKeys.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value))));
        }

        var planJson = plan.ToJsonString();
        var manifestAlgorithm = _gap is null ? AlgorithmVersion : GapStage.Algorithm;
        Manifest = new WorkManifestInput(configHash, input.Snapshot.SourceHash, profile.Fingerprint, definition, manifestAlgorithm, policyHash, planJson);
        _manifestIdentity = CanonicalJson.Sha256Hex(JsonSerializer.Serialize(new
        {
            configHash, source = input.Snapshot.SourceHash, runtime = profile.Fingerprint, definition, algorithm = manifestAlgorithm,
            policy = policyHash, plan = JsonDocument.Parse(planJson).RootElement,
        }));
        _context = new ReconciliationContext(
            $"ProcessingRun|{claim.RunId:N}", _manifestIdentity, definition,
            new PlannerLimits(Math.Max(1, profile.InputBudgetTokens - HeaderReserveTokens), profile.OutputTokens, OutputExpansion: 1.0), _schemaVersion, promptVersion, _maxLevels);
    }

    private readonly int _maxLevels;
    private readonly ReconciliationLimits _limits;

    /// <summary>Vero dopo il commit atomico del run da questo piano.</summary>
    public bool Committed { get; private set; }

    public WorkManifestInput Manifest { get; }

    public WorkLookupExpectation Expectation => new(_schemaVersion, ValidationVersion);

    /// <summary>Il batch dello stadio Gap (v6) ha schema e validazione propri; i batch della gerarchia conservano quelli del piano.</summary>
    public WorkLookupExpectation ExpectationFor(BatchRef batch) => IsGapBatch(batch) ? new(GapStage.AiSchema, GapStage.ValidationVersion) : Expectation;

    public BatchAgentSpec Agent { get; }

    public BatchAgentSpec AgentFor(BatchRef batch) => IsGapBatch(batch) ? _gapAgent! : Agent;

    private bool IsGapBatch(BatchRef batch) => _gap is not null && _finishedBatches >= 0 && batch.Ordinal == _finishedBatches;

    public bool HasBatch(int ordinal) => throw new NotSupportedException("Il numero di batch dipende dagli output Committed: usare HasBatchAsync.");

    public ValidatedBatch Validate(BatchRef batch, string responseText) => throw new NotSupportedException("La validazione richiede gli output precedenti: usare ValidateAsync.");

    // ------------------------------------------------------------------ motore rigiocato sugli output Committed

    /// <summary>Output composto di un batch alias (committato o in validazione): risultato canonico e DTO con i suoi hash.</summary>
    private sealed record AliasEntry(string BatchId, ReconcileAliasComposition Composition, string Text, ReconcileAliasCatalog Catalog, string AiCanonical);

    /// <summary>Stato accumulato da UN replay del motore: gli item già proposti dalle foglie A (per i bersagli delle patch) e le sezioni dichiarate dal modello.</summary>
    private sealed class ReplayState
    {
        public List<ReconcileItemRef> Items { get; } = [];

        public List<ItemProposal> Proposals { get; } = [];

        public Dictionary<string, AiSection> Sections { get; } = new(StringComparer.Ordinal);

        public List<UngroundedAliasItem> Ungrounded { get; } = [];

        /// <summary>Vero dopo la costruzione della gerarchia: i batch seguenti sono espansioni e confronti.</summary>
        public bool CandidatePhase { get; set; }

        public void Absorb(ReconcileAliasComposition composition)
        {
            foreach (var proposal in composition.Result!.ItemProposals)
            {
                Items.Add(new(proposal.LocalKey, proposal.Text, proposal.EvidenceIds));
                Proposals.Add(proposal);
            }

            foreach (var (key, section) in composition.Sections)
            {
                Sections[key] = section;
            }

            Ungrounded.AddRange(composition.Ungrounded);
        }
    }

    private sealed record Replay(ReconcileBatchRequest? Request, int Ordinal, ReconcileEngineResult? Finished, ReplayState State);

    private sealed class NeedBatch(ReconcileBatchRequest request, int ordinal) : Exception("richiesta senza output Committed")
    {
        public ReconcileBatchRequest Request { get; } = request;

        public int Ordinal { get; } = ordinal;
    }

    private async ValueTask<Replay> ReplayAsync(IBatchOutputs prior, (int Ordinal, string Text)? candidate, CancellationToken ct)
    {
        if (_finished is not null && candidate is null)
        {
            return new(null, -1, _finished, _finishedState!);
        }

        var state = new ReplayState();
        _state = state;
        var counter = 0;
        async ValueTask<string> Invoke(ReconcileBatchRequest request, CancellationToken token)
        {
            var k = counter++;
            if (candidate is { } c && c.Ordinal == k)
            {
                return _alias is null ? c.Text : AbsorbCandidate(k, request, c.Text, state);
            }

            var text = _alias is null ? await CommittedTextAsync(k, request.Batch.BatchId, prior, token) : await CommittedAliasTextAsync(k, request, state, prior, token);
            return text ?? throw new NeedBatch(request, k);
        }

        try
        {
            var snapshot = _input.Snapshot;
            _directory ??= await ReconciliationDirectory.BuildAsync(snapshot, _facts, _context, new ReconciliationMeasurers(LeafMeasurer, PageMeasurer), ct);
            var hierarchy = await ReconciliationHierarchy.BuildAsync(snapshot, _directory, _context, NodeMeasurer, Invoke, ct);
            state.CandidatePhase = true;
            var engine = new ReconciliationCandidates(snapshot, _directory, hierarchy, _context, LeafMeasurer, Invoke, _limits);
            await engine.RunPhasesAsync(ct);
            var final = await ReconciliationFinalPass.RunAsync(engine, snapshot, _directory, [], ct);
            var finished = new ReconcileEngineResult(_directory, hierarchy, final.Run, final);
            if (candidate is null)
            {
                _finished = finished;
                _finishedState = state;
                _finishedBatches = counter;
            }

            return new(null, -1, finished, state);
        }
        catch (NeedBatch need)
        {
            return new(need.Request, need.Ordinal, null, state);
        }
    }

    private async ValueTask<string?> CommittedTextAsync(int ordinal, string batchId, IBatchOutputs prior, CancellationToken ct)
    {
        if (_texts.TryGetValue(ordinal, out var known))
        {
            return known.BatchId == batchId ? known.Text : throw Incompatible(ordinal);
        }

        var output = await prior.GetByOrdinalAsync(ordinal, ct);
        if (output is null || output.Status != WorkOutputStatus.Committed)
        {
            return null;
        }

        if (output.BatchId != batchId)
        {
            throw Incompatible(ordinal);   // il motore non riproduce lo stesso piano: nessuna reinterpretazione
        }

        using var document = JsonDocument.Parse(output.PayloadJson ?? throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Payload mancante."));
        var text = document.RootElement.GetProperty("output").GetRawText();
        _texts[ordinal] = (batchId, text);
        return text;
    }

    private static DocumentWorkException Incompatible(int ordinal) =>
        new(DocumentWorkException.CheckpointIncompatible, $"L'output del batch {ordinal} non appartiene alla richiesta rigiocata dal piano di riconciliazione.");

    // ------------------------------------------------------------------ adattatore alias (T131)

    private ReconcileAliasCatalog CatalogOf(ReconcileBatchRequest request, ReplayState state) => ReconcileAliasCatalog.Build(_alias!.RunCatalog, request, state.Items);

    /// <summary>Base immutabile del passaggio (proposte delle foglie e relazioni note): nota solo dopo le foglie, serve a firmare le patch di espansione e confronto.</summary>
    private ReconciliationBase? BaseOf(ReconcileBatchRequest request, ReplayState state) =>
        request.Batch.Stage is "expand" or "compare" ? DocumentAnalysisBase.Compute(state.Proposals, _input.Snapshot) : null;

    /// <summary>Compone la risposta del modello (DTO alias) nel canonico del batch: un rifiuto è <c>work_output_invalid</c> permanente, senza riparazione.</summary>
    private string AbsorbCandidate(int k, ReconcileBatchRequest request, string text, ReplayState state)
    {
        var catalog = CatalogOf(request, state);
        var composition = ReconcileAliasComposer.Compose(text, catalog, request, _input.Snapshot, BaseOf(request, state));
        if (!composition.IsValid)
        {
            throw new DocumentWorkException(
                composition.ErrorCode ?? DocumentWorkException.WorkOutputInvalid,
                $"Output del batch {request.Batch.BatchId} non valido ({composition.Violations.Count} violazioni): " + string.Join(" | ", composition.Violations.Take(8)));
        }

        var entry = new AliasEntry(request.Batch.BatchId, composition, ReconciliationCodec.Encode(composition.Result!), catalog, ReconcileAliasContracts.Canonical(ReconcileAliasContracts.Decode(text)));
        _pending = (k, entry);
        state.Absorb(composition);
        return entry.Text;
    }

    /// <summary>
    /// Output Committed di un batch alias: catalogo del batch ricostruito dalla richiesta e verificato contro <c>catalogHash</c> (<c>checkpoint_incompatible</c>), DTO verificato
    /// contro <c>aiOutputHash</c> (<c>work_output_invalid</c>), canonico ricomposto e verificato contro <c>canonicalHash</c> (<c>integrity_conflict</c>). Nessuna inferenza.
    /// </summary>
    private async ValueTask<string?> CommittedAliasTextAsync(int ordinal, ReconcileBatchRequest request, ReplayState state, IBatchOutputs prior, CancellationToken ct)
    {
        var batchId = request.Batch.BatchId;
        if (_aliasEntries.TryGetValue(ordinal, out var known))
        {
            if (known.BatchId != batchId)
            {
                throw Incompatible(ordinal);
            }

            state.Absorb(known.Composition);
            return known.Text;
        }

        var output = await prior.GetByOrdinalAsync(ordinal, ct);
        if (output is null || output.Status != WorkOutputStatus.Committed)
        {
            return null;
        }

        if (output.BatchId != batchId)
        {
            throw Incompatible(ordinal);
        }

        string catalogHash, aiOutputHash, canonicalHash, aiJson, catalogJson, outputJson;
        try
        {
            using var document = JsonDocument.Parse(output.PayloadJson ?? throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Payload mancante."));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetString() != ReconcileAliasContracts.SchemaVersion)
            {
                throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, $"Il WorkOutput del batch {ordinal} non è del contratto con alias.");
            }

            (catalogHash, aiOutputHash, canonicalHash) = (root.GetProperty("catalogHash").GetString()!, root.GetProperty("aiOutputHash").GetString()!, root.GetProperty("canonicalHash").GetString()!);
            (aiJson, catalogJson, outputJson) = (root.GetProperty("ai").GetRawText(), root.GetProperty("catalog").GetRawText(), root.GetProperty("output").GetRawText());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, $"Il payload del WorkOutput del batch {ordinal} non è nel formato con alias.");
        }

        var catalog = CatalogOf(request, state);
        if (catalog.CatalogHash != catalogHash || CanonicalJson.Canonicalize(catalogJson) != catalog.Encode())
        {
            throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, $"Il catalogo ricostruito del batch {ordinal} ha un hash diverso da quello congelato.");
        }

        var composition = ReconcileAliasComposer.Compose(aiJson, catalog, request, _input.Snapshot, BaseOf(request, state), canonicalHash);
        if (!composition.IsValid)
        {
            throw new DocumentWorkException(composition.ErrorCode ?? DocumentWorkException.WorkOutputInvalid, $"WorkOutput del batch {ordinal} non verificabile: " + string.Join(" | ", composition.Violations.Take(5)));
        }

        if (composition.AiOutputHash != aiOutputHash)
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, $"Il DTO committato del batch {ordinal} ha un hash diverso da aiOutputHash.");
        }

        if (ReconcileAliasComposer.CanonicalHash(ReconciliationCodec.DecodeResult(outputJson)) != canonicalHash)
        {
            throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"L'output canonico committato del batch {ordinal} ha un hash diverso da canonicalHash.");
        }

        var entry = new AliasEntry(batchId, composition, ReconciliationCodec.Encode(composition.Result!), catalog, ReconcileAliasContracts.Canonical(ReconcileAliasContracts.Decode(aiJson)));
        _aliasEntries[ordinal] = entry;
        state.Absorb(composition);
        return entry.Text;
    }

    // ------------------------------------------------------------------ IDocumentBatchPlan

    public async ValueTask<bool> HasBatchAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        var replay = await ReplayAsync(prior, null, ct);
        if (replay.Request is not null)
        {
            return replay.Ordinal == ordinal;
        }

        // Stadio Gap (v6): UN batch dopo l'ultimo della gerarchia, solo a gerarchia Complete. Con la gerarchia incompleta non c'è alcuno stadio e nessuna chiamata in più:
        // l'Assemble riporta l'incompletezza come per ogni gerarchia v4.
        return _gap is not null && replay.Finished is not null && ordinal == _finishedBatches && GapState(replay) is not null;
    }

    public async ValueTask<DerivedBatchInput> PrepareAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        if (_gap is not null && await StageReplayAsync(ordinal, prior, ct) is { } stageReplay)
        {
            return await PrepareGapAsync(ordinal, stageReplay, prior, ct);
        }

        var (request, state) = await PendingAsync(ordinal, prior, ct);
        var batch = request.Batch with { Ordinal = ordinal };
        if (_alias is not null)
        {
            batch = await RemeasureAsync(batch, request, state, ct);
        }

        if (ordinal >= 1)
        {
            await PromoteIfOrdinaryAsync(batch, request, ordinal, prior, ct);
        }

        return batch;
    }

    /// <summary>
    /// Il Pack misura il messaggio senza il candidato in esame (non noto al misuratore, coperto dalla riserva di intestazione): prima di pubblicare il batch si misura il messaggio
    /// REALMENTE inviato, con candidato, item e relazioni, e si rifiuta (<c>document_limit_exceeded</c>) ciò che non entra nel budget. L'inputHash pubblicato è quello del payload inviato.
    /// </summary>
    private async ValueTask<DerivedBatchInput> RemeasureAsync(DerivedBatchInput batch, ReconcileBatchRequest request, ReplayState state, CancellationToken ct)
    {
        var messages = ComposeAlias(request, state);
        var measured = await _preflight.MeasureAsync(new DocumentPayload(AiPhase.Analysis, _instructions, messages, _schemaJson), ct, _profile.Fingerprint);
        if (measured.Quality == TokenCountQuality.Unavailable)
        {
            throw new RuntimeUnverifiedException("conteggio del payload del batch non disponibile (nessun tokenizer o calibrazione identificata)");
        }

        if (!measured.Fits)
        {
            throw new DocumentWorkException(
                DocumentWorkException.CallLimitExceeded,
                $"Il messaggio del batch {batch.BatchId} ({measured.Tokens} token) supera il budget di input ({measured.InputBudgetTokens} token) una volta aggiunti candidato in esame, item e relazioni note.");
        }

        return batch with
        {
            InputHash = measured.PayloadHash, MeasuredTokens = measured.Tokens, TokenProvenance = measured.Quality == TokenCountQuality.Measured ? "measured" : "calibrated_upper_bound",
        };
    }

    private async ValueTask<(ReconcileBatchRequest Request, ReplayState State)> PendingAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        var replay = await ReplayAsync(prior, null, ct);
        return replay.Request is { } request && replay.Ordinal == ordinal
            ? (request, replay.State)
            : throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, $"Nessuna richiesta di riconciliazione pendente per l'ordinale {ordinal}.");
    }

    public async ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, IBatchOutputs prior, CancellationToken ct)
    {
        if (_gap is not null && await StageReplayAsync(batch.Ordinal, prior, ct) is { } stageReplay)
        {
            var gapState = GapState(stageReplay) ?? throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Lo stadio Gap non è disponibile.");
            return gapState.Messages;
        }

        var (request, state) = await PendingAsync(batch.Ordinal, prior, ct);
        return request.Batch.BatchId == batch.BatchId
            ? _alias is null ? Compose(request) : ComposeAlias(request, state)
            : throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Il batch da comporre non coincide con la richiesta pendente.");
    }

    /// <summary>
    /// Valida PRIMA del WorkOutput rigiocando il motore con la risposta candidata nel suo punto: il motore applica le stesse regole di T072–T075 (decodifica strict,
    /// riferimenti, visite, descriptor) e un output non valido è un errore permanente senza riparazione. Un output valido è Committed anche con obbligazioni residue.
    /// Con gli alias la risposta è il DTO <c>reconcile-alias-1</c>: il server lo compone nel canonico e il motore valida quello.
    /// </summary>
    public async ValueTask<ValidatedBatch> ValidateAsync(BatchRef batch, string responseText, IBatchOutputs prior, CancellationToken ct)
    {
        if (_gap is not null && await StageReplayAsync(batch.Ordinal, prior, ct) is { } stageReplay)
        {
            return await ValidateGapAsync(batch, responseText, stageReplay, ct);
        }

        var (request, _) = await PendingAsync(batch.Ordinal, prior, ct);
        if (request.Batch.BatchId != batch.BatchId || (_alias is null && request.Batch.InputHash != batch.InputHash))
        {
            throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "La risposta non appartiene alla richiesta pendente.");
        }

        JsonElement root;
        try
        {
            using var parsed = JsonDocument.Parse(responseText);
            root = parsed.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, $"Output del batch {batch.BatchId} non valido: JSON non conforme ({e.Message}).");
        }

        _pending = null;
        await ReplayAsync(prior, (batch.Ordinal, responseText), ct);   // lancia work_output_invalid se la risposta viola il contratto
        string payload;
        if (_alias is null)
        {
            payload = JsonSerializer.Serialize(new { schemaVersion = SchemaVersion, stage = request.Batch.Stage, view = request.View.ToString(), level = request.Level, output = root });
        }
        else
        {
            var entry = _pending is { } pending && pending.Ordinal == batch.Ordinal && pending.Entry.BatchId == batch.BatchId
                ? pending.Entry
                : throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "La risposta validata non è stata composta dal piano.");
            payload = new JsonObject
            {
                ["schemaVersion"] = ReconcileAliasContracts.SchemaVersion,
                ["stage"] = request.Batch.Stage,
                ["view"] = request.View.ToString(),
                ["level"] = request.Level,
                ["aiOutputHash"] = entry.Composition.AiOutputHash,
                ["catalogHash"] = entry.Composition.CatalogHash,
                ["canonicalHash"] = entry.Composition.CanonicalHash,
                ["ai"] = JsonNode.Parse(entry.AiCanonical),
                ["catalog"] = JsonNode.Parse(entry.Catalog.Encode()),
                ["output"] = JsonNode.Parse(entry.Text),
            }.ToJsonString();
        }

        var covered = JsonSerializer.Serialize<string[]>(
            [.. request.Entries.Select(e => e.EntryId), .. request.Nodes.Select(n => n.NodeId)]);
        var output = new WorkOutputInput(
            batch.BatchId, DocumentBatchWorkflow.InvokeId, batch.InputHash, CanonicalJson.Sha256Hex(payload), payload, null, null, covered, null, null, _schemaVersion, ValidationVersion, null);
        return new ValidatedBatch(output);
    }

    /// <summary>
    /// Promozione additional_work (T047/T078): dal secondo batch di un root Ordinary il lavoro necessario è reale (entrate o nodi ancora da visitare) e senza output
    /// compatibile; la ricevuta cita il piano, gli output Committed d'innesco e il batch aggiuntivo. t0, Hard e contatori restano quelli del root. Un root già Extended
    /// (analisi con carico non noto al POST) non ha nulla da promuovere.
    /// </summary>
    private async ValueTask PromoteIfOrdinaryAsync(DerivedBatchInput batch, ReconcileBatchRequest request, int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        var obligations = request.Entries.Select(e => $"{batch.Stage}:{e.EntryId}").Concat(request.Nodes.Select(n => $"{batch.Stage}:{n.NodeId}")).Order(StringComparer.Ordinal).ToList();
        if (obligations.Count == 0)
        {
            obligations.Add($"{batch.Stage}:{batch.BatchId}");
        }

        await PromoteAsync(batch, obligations, ordinal, prior, ct);
    }

    /// <summary>Promozione additional_work con le obbligazioni date (batch della gerarchia o stadio Gap): stesse regole, nessun secondo meccanismo.</summary>
    private async ValueTask PromoteAsync(DerivedBatchInput batch, List<string> obligations, int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var regime = await db.ProcessingRuns.AsNoTracking().Where(r => r.Id == _claim.RunId).Select(r => r.Regime).SingleAsync(ct);
        if (regime != ExecutionRegime.Ordinary)
        {
            return;
        }

        var triggers = new List<string>();
        for (var k = 0; k < ordinal; k++)
        {
            triggers.Add((await prior.GetByOrdinalAsync(k, ct))?.OutputHash ?? throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Output d'innesco mancante."));
        }

        var receipt = new AdditionalWorkReceipt(AdditionalWorkReceipt.AdditionalWork, _input.Snapshot.SourceHash, [.. triggers.Distinct(StringComparer.Ordinal)], obligations, [batch.BatchId], "");
        receipt = receipt with { DerivedPlanHash = DocumentRegimeTransitions.ComputeDerivedPlanHash(receipt, [batch]) };
        var result = await new DocumentRegimeTransitions(db).PromoteAsync(new PromotionRequest(WorkOwnerKind.ProcessingRun, _claim.RunId, _claim.LeaseOwner, _claim.FencingToken, receipt, [batch]), ct);
        switch (result.Outcome)
        {
            case PromotionOutcome.Promoted or PromotionOutcome.AlreadyPromoted or PromotionOutcome.NotEligible:
                return;
            case PromotionOutcome.Fenced or PromotionOutcome.Terminal:
                throw new LeaseLostException("Promozione rifiutata: il lease non è più valido.");
            case PromotionOutcome.DeadlineExceeded:
                throw new DocumentWorkException("deadline_exceeded", "La scadenza del root è trascorsa prima della promozione.");
            case PromotionOutcome.LimitExceeded:
                throw new DocumentWorkException(DocumentWorkException.CallLimitExceeded, "Il lavoro necessario eccede il limite di chiamate del root.");
            default:
                throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"Ricevuta additional_work rifiutata ({result.Outcome}: {result.Reason}).");
        }
    }

    // ------------------------------------------------------------------ stadio Gap dedicato (v6, D1, AI §13.7)

    /// <summary>Stato dello stadio: analisi assemblata dalla gerarchia Complete, riferimenti Open canonici e messaggio (ledger intero + riferimenti). Funzione pura degli output Committed.</summary>
    private sealed record GapStagePlanState(ReconcileAssembly Assembled, GapStageReferences References, IReadOnlyList<ChatMessage> Messages);

    /// <summary>Il replay della gerarchia se l'ordinale è quello dello stadio (subito dopo l'ultimo batch della gerarchia), altrimenti null.</summary>
    private async ValueTask<Replay?> StageReplayAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        var replay = await ReplayAsync(prior, null, ct);
        return replay.Finished is not null && ordinal == _finishedBatches ? replay : null;
    }

    /// <summary>
    /// Stato dello stadio dalla gerarchia terminata, o null se la gerarchia non è Complete (nessuno stadio, nessuna chiamata: l'Assemble riporta l'incompletezza). Se un Gap non ha la propria domanda canonica
    /// nell'analisi assemblata il server NON la crea: nessun invio e <c>coverage_incomplete</c> con l'obbligazione <c>gap_reference_missing:&lt;gap&gt;</c> (la gerarchia è stata eseguita, i WorkOutput restano).
    /// </summary>
    private GapStagePlanState? GapState(Replay replay)
    {
        if (_gapState is not null)
        {
            return _gapState;
        }

        var sections = new HierarchicalItemSections(replay.State.Sections, replay.State.Ungrounded);
        var assembled = DocumentReconcileAssembly.Assemble(_input, replay.Finished!, sections);
        if (!ReconciliationCompletion.EvaluateHierarchical(assembled.Run, assembled.Analysis, assembled.Map).IsComplete
            || DocumentAnalysisContentRules.Check(assembled.Analysis, _input.Facts).Count > 0)
        {
            return null;
        }

        var references = GapStageReferences.From(assembled.Analysis, assembled.Map, _alias!.RunCatalog);
        if (references.MissingGaps.Count > 0)
        {
            var missing = references.MissingGaps;
            throw new DocumentWorkException(
                DocumentWorkException.CoverageIncomplete,
                string.Join(" | ", missing.Take(5).Select(g => $"{DocumentWorkException.GapReferenceMissing}:{g}"))
                + $": {missing.Count} Gap senza una domanda aperta canonica nell'analisi assemblata dalla gerarchia; il server non la crea e lo stadio Gap non è stato eseguito (nessuna chiamata dello stadio).");
        }

        var stageInput = GapStageBuilder.Build(_input, _alias.RunCatalog, _gap!.Meta, references);
        return _gapState = new(assembled, references, GapStageBuilder.Message(stageInput));
    }

    private int GapCount => _alias!.RunCatalog.Data.Evidence.Count(e => e.Kind == EvidenceKind.Gap);

    /// <summary>
    /// Batch unico dello stadio: <c>inputHash</c> = hash di (snapshot, chiavi <c>gap*</c>, catalogo, riferimenti Open e payload effettivo misurato). Il controllo (2) del budget è ESATTO, dopo l'assembly e
    /// prima della riserva della chiamata: oltre budget ⇒ <c>Failed</c> dopo la gerarchia (WorkOutput conservati), nessun invio. Un output già Committed non lo ripete (replay senza inferenza).
    /// </summary>
    private async ValueTask<DerivedBatchInput> PrepareGapAsync(int ordinal, Replay replay, IBatchOutputs prior, CancellationToken ct)
    {
        var state = GapState(replay) ?? throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Lo stadio Gap richiede una gerarchia completa.");
        var measured = await _preflight.MeasureAsync(new DocumentPayload(AiPhase.Analysis, GapStage.Instructions, state.Messages, _gapSchemaJson), ct, _profile.Fingerprint);
        if (measured.Quality == TokenCountQuality.Unavailable)
        {
            throw new RuntimeUnverifiedException("conteggio del payload dello stadio Gap non disponibile (nessun tokenizer o calibrazione identificata)");
        }

        if (await prior.GetByOrdinalAsync(ordinal, ct) is null && GapStageBudget.Evaluate(measured, GapCount, routing: false) is { Fits: false } verdict)
        {
            throw verdict.ToException();
        }

        var keys = string.Join(',', GapStage.FreezeKeys.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
        var inputHash = DocumentAnalysisWorkflow.Sha($"gap-stage-input|{_input.Snapshot.SourceHash}|{keys}|{_alias!.RunCatalog.RunIndependentHash}|{state.References.Hash()}|{measured.PayloadHash}");
        var definition = _alias.Definition;
        var batchId = DocumentAnalysisWorkflow.Sha($"batch|ProcessingRun|{_claim.RunId:N}|{_manifestIdentity}|{definition}|{GapStage.BatchStage}|{ordinal}|{inputHash}");
        var gaps = _alias.RunCatalog.Data.Evidence.Where(e => e.Kind == EvidenceKind.Gap).ToList();
        var batch = new DerivedBatchInput(
            batchId, GapStage.BatchStage, null, null, null, ordinal, [],
            JsonSerializer.Serialize(new
            {
                stage = GapStage.BatchStage, searchBasis = GapStage.SearchBasisLedgerExhaustive, retrievalComplete = true, gaps = gaps.Select(g => g.EvidenceId.ToString("D")).ToArray(),
                candidates = _alias.RunCatalog.Data.Evidence.Count - gaps.Count, references = state.References.Hash(),
            }),
            measured.Tokens, measured.Quality == TokenCountQuality.Measured ? "measured" : "calibrated_upper_bound", inputHash, GapStage.AiSchema, GapStage.Prompt);
        if (ordinal >= 1)
        {
            await PromoteAsync(batch, [.. gaps.Select(g => $"{GapStage.BatchStage}:{g.EvidenceId:D}").Order(StringComparer.Ordinal)], ordinal, prior, ct);
        }

        return batch;
    }

    /// <summary>Valida PRIMA del WorkOutput: record strict e riferimenti offerti (<c>work_output_invalid</c> terminale, nessuna riparazione né retry); record mancanti ⇒ output riutilizzabile ma <c>ValidIncomplete</c>.</summary>
    private async ValueTask<ValidatedBatch> ValidateGapAsync(BatchRef batch, string responseText, Replay replay, CancellationToken ct)
    {
        var state = GapState(replay) ?? throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Lo stadio Gap richiede una gerarchia completa.");
        var validation = GapStageContracts.Validate(responseText, _alias!.RunCatalog, state.References);
        if (!validation.IsReusableWorkOutput)
        {
            throw new DocumentWorkException(
                validation.ErrorCode ?? DocumentWorkException.WorkOutputInvalid,
                $"Output dello stadio Gap non valido ({validation.Violations.Count} violazioni): " + string.Join(" | ", validation.Violations.Take(8)));
        }

        var gaps = _alias.RunCatalog.Data.Evidence.Where(e => e.Kind == EvidenceKind.Gap).ToList();
        var payload = GapStagePayload.Build(validation, _alias.RunCatalog, state.References, _alias.RunCatalog.Data.Evidence.Count - gaps.Count, gaps.Count);
        var covered = JsonSerializer.Serialize(gaps.Select(g => g.EvidenceId.ToString("D")));
        return new ValidatedBatch(new WorkOutputInput(
            batch.BatchId, DocumentBatchWorkflow.InvokeId, batch.InputHash, CanonicalJson.Sha256Hex(payload), payload, null, null, covered, null, null, GapStage.AiSchema, GapStage.ValidationVersion, null));
    }

    /// <summary>Assemble dello stadio: l'output Committed deve esistere, superare l'integrità e portare esattamente un record finale per Gap con riferimenti canonici risolvibili (nessuna correzione del server).</summary>
    private async ValueTask VerifyGapStageAsync(Replay replay, IBatchOutputs outputs, CancellationToken ct)
    {
        var state = GapState(replay) ?? throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, "Stadio Gap non eseguibile: gerarchia incompleta.");
        var output = await outputs.GetByOrdinalAsync(_finishedBatches, ct);
        if (output is null || output.Status != WorkOutputStatus.Committed)
        {
            throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, "Assemble senza l'output dello stadio Gap: nessuna valutazione dei Gap, nessun Completed.");
        }

        if (output.SchemaVersion != GapStage.AiSchema || output.ValidationVersion != GapStage.ValidationVersion)
        {
            throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Il WorkOutput dello stadio Gap non è delle versioni congelate.");
        }

        DocumentReconcileGapStage.Verify(state.Assembled, _alias!.RunCatalog, state.References, output.PayloadJson ?? throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Payload mancante."));
    }

    // ------------------------------------------------------------------ assemblaggio e commit atomico


    /// <summary>Assemble: nessuna inferenza. Un Completed parziale non esiste: ogni gap della copertura server è coverage_incomplete e non lascia revisione.</summary>
    public async ValueTask AssembleAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var replay = await ReplayAsync(outputs, null, ct);
        if (replay.Finished is not { } finished)
        {
            throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, "Assemble prima del termine della riconciliazione: restano batch da elaborare.");
        }

        var sections = _alias is null ? null : new HierarchicalItemSections(replay.State.Sections, replay.State.Ungrounded);
        _assembled = DocumentReconcileAssembly.Assemble(_input, finished, sections);
        var coverage = ReconciliationCompletion.EvaluateHierarchical(_assembled.Run, _assembled.Analysis, _assembled.Map);
        if (!coverage.IsComplete)
        {
            throw new DocumentWorkException(
                coverage.Report.Gaps.FirstOrDefault(g => g.Code is DocumentWorkException.ReconciliationRoundLimit)?.Code ?? DocumentWorkException.CoverageIncomplete,
                $"La riconciliazione non è completa ({coverage.Report.Gaps.Count} obbligazioni residue): " + string.Join(" | ", coverage.Report.Gaps.Take(5).Select(g => $"{g.Code}: {g.Detail}")));
        }

        var violations = DocumentAnalysisContentRules.Check(_assembled.Analysis, _input.Facts);
        if (violations.Count > 0)
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, $"L'analisi assemblata non è valida ({violations.Count}): " + string.Join(" | ", violations.Take(5)));
        }

        if (_gap is not null)
        {
            // Stadio Gap (v6): la revisione si committa solo con esattamente un record finale valido per Gap e riferimenti canonici risolvibili (D1, AI §13.7).
            await VerifyGapStageAsync(replay, outputs, ct);
        }

        _verified = true;
    }

    public async ValueTask<int> CommitAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        if (_assembled is null || !_verified)
        {
            await AssembleAsync(outputs, ct);   // ripresa dopo un riavvio: stessa verifica, mai un commit senza copertura
        }

        var assembled = _assembled!;
        using var scope = _scopes.CreateScope();
        var completion = new AnalysisRunCompletion(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        var rows = DocumentReconcileAssembly.Projections(_input, _claim.RunId, assembled.Run, assembled.Outcomes);
        var result = await completion.CommitDocumentAsync(_claim, assembled.Analysis, assembled.Map, assembled.Relations, ct, rows);
        if (result.Outcome == AnalysisCommitOutcome.Rejected)
        {
            if (result.Reason == "lease_lost")
            {
                throw new LeaseLostException("Commit dell'analisi rifiutato (lease_lost): il lease non è più valido.");
            }

            throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"Commit dell'analisi documentale rifiutato ({result.Reason}).");
        }

        Committed = true;
        return assembled.Outcomes.Count;
    }

    // ------------------------------------------------------------------ misure e messaggi

    private PackMeasurer<CatalogEntry> LeafMeasurer => (members, ct) =>
        MeasureAsync("e|" + string.Join(',', members.Select(m => m.EntryId)) + "|" + VisibleItems(members), () => Compose(Placeholder(members, [])), ct);

    private PackMeasurer<CatalogNode> NodeMeasurer => (members, ct) =>
        MeasureAsync("n|" + string.Join(',', members.Select(m => m.NodeId)), () => Compose(Placeholder([], members)), ct);

    private PackMeasurer<PageEntry> PageMeasurer => (members, ct) => MeasureAsync(
        "p|" + ReconciliationCodec.Canonical(new CatalogPage { PageId = "x", NodeId = "x", Kind = KindOf(members), Ordinal = 0, Entries = members, PreviousPageHash = null }),
        () => [new ChatMessage(ChatRole.User, ReconciliationCodec.Canonical(new CatalogPage { PageId = "x", NodeId = "x", Kind = KindOf(members), Ordinal = 0, Entries = members, PreviousPageHash = null }))], ct, schema: false);

    private static CatalogPageKind KindOf(IReadOnlyList<PageEntry> members) => members.FirstOrDefault() switch
    {
        KeyPageEntry => CatalogPageKind.Keys,
        RelationPageEntry => CatalogPageKind.Relations,
        _ => CatalogPageKind.Members,
    };

    /// <summary>Chiave di cache del Pack di voci: negli stadi di espansione e confronto il messaggio porta anche gli item già proposti per quelle evidenze.</summary>
    private string VisibleItems(IReadOnlyList<CatalogEntry> members)
    {
        if (_alias is null || !_state.CandidatePhase)
        {
            return string.Empty;
        }

        var ids = members.Select(m => m.EvidenceId).ToHashSet();
        return string.Join(',', _state.Items.Select((item, index) => (item, index)).Where(t => t.item.EvidenceIds.Any(ids.Contains)).Select(t => t.index));
    }

    private async ValueTask<UnitMeasure> MeasureAsync(string key, Func<IReadOnlyList<ChatMessage>> messages, CancellationToken ct, bool schema = true)
    {
        if (_measures.TryGetValue(key, out var known))
        {
            return known;
        }

        var measured = await _preflight.MeasureAsync(new DocumentPayload(AiPhase.Analysis, _instructions, messages(), schema ? _schemaJson : null), ct, _profile.Fingerprint);
        return _measures[key] = new UnitMeasure(measured.Tokens, measured.Quality, measured.PayloadHash, measured.Tokens);
    }

    private ReconcileBatchRequest Placeholder(IReadOnlyList<CatalogEntry> entries, IReadOnlyList<CatalogNode> nodes) => new(
        new DerivedBatchInput(
            new string('0', 64), _alias is null ? NodeStage : entries.Count > 0 ? (_state.CandidatePhase ? "expand" : "leaf") : "reduce", null, null, null, 0, [], "{}", null, "measured",
            new string('0', 64), _schemaVersion, _alias is null ? ReconciliationHierarchy.PromptVersion : ReconcileAliasContracts.PromptVersion),
        ReconcileView.A, 0, entries, nodes);

    /// <summary>Messaggio alias del batch (§11): soli alias, nessun UUID né hash. Stessa funzione per il Pack, per la misura finale e per l'invio.</summary>
    private IReadOnlyList<ChatMessage> ComposeAlias(ReconcileBatchRequest request, ReplayState state) =>
        ReconcileAliasMessages.Build(_alias!.RunCatalog, CatalogOf(request, state), request, _evidence);

    /// <summary>Messaggio unico del batch: dati del server, voci integrali (enunciato e fonti) o descriptor dei nodi e il candidato in esame. Nessun testo di altri batch.</summary>
    internal IReadOnlyList<ChatMessage> Compose(ReconcileBatchRequest request)
    {
        if (_alias is not null)
        {
            return ComposeAlias(request, _state);
        }

        var text = new StringBuilder();
        text.Append("Dati affidabili del server:\n")
            .Append($"runId: {_claim.RunId:D}\nprojectId: {_input.ProjectId:D}\nsourceHash: {_input.Snapshot.SourceHash}\nbatchId: {request.Batch.BatchId}\ninputHash: {request.Batch.InputHash}\n")
            .Append($"stage: {request.Batch.Stage}\nview: {request.View}\nlevel: {request.Level}\n");
        var visitedEntries = request.Entries.Select(e => e.EntryId).Order(StringComparer.Ordinal).ToList();
        var visitedNodes = request.Nodes.Select(n => n.NodeId).Order(StringComparer.Ordinal).ToList();
        text.Append($"visitedEntryIds ({visitedEntries.Count} elementi, gia in ordine alfabetico: copiali identici e per intero): [{string.Join(",", visitedEntries.Select(id => "\"" + id + "\""))}]\n")
            .Append($"visitedNodeIds ({visitedNodes.Count} elementi, gia in ordine alfabetico: copiali identici e per intero): [{string.Join(",", visitedNodes.Select(id => "\"" + id + "\""))}]\n");
        foreach (var candidate in request.Focus ?? [])
        {
            text.Append("Candidato in esame (risolvilo con le voci integrali sotto):\n").Append(ReconciliationCodec.Canonical(candidate)).Append('\n');
        }

        var documentSpans = new List<int>();
        foreach (var entry in request.Entries)
        {
            var evidence = _evidence[entry.EvidenceId];
            var snapshot = _input.Snapshot.Evidence.Single(e => e.EvidenceId == entry.EvidenceId);
            var start = text.Length;
            text.Append($"<<<VOCE entryId={entry.EntryId} evidenza={evidence.EvidenceId:D} hash={evidence.ContentHash} origine={evidence.Origin} tipo={evidence.Kind} polarita={evidence.Polarity} certezza={evidence.Certainty}>>>\n")
                .Append(evidence.Statement).Append('\n');
            foreach (var s in snapshot.Sources)
            {
                text.Append(s.Kind == EvidenceSourceKind.Message
                    ? $"<<<FONTE fonte:{s.SourceId:D} ruolo={s.Role} messaggio={s.MessageId:D}>>>\n"
                    : $"<<<FONTE fonte:{s.SourceId:D} ruolo={s.Role} documento={s.DocumentId:D} versione={s.DocumentVersionId:D} preparazione={s.PreparationId:D} artefatto={s.ArtifactId:D} blocco={s.BlockId:D} start={s.Start} end={s.End}>>>\n")
                    .Append(s.Quote).Append("\n<<<FINE FONTE>>>\n");
            }

            text.Append("<<<FINE VOCE>>>\n");
            if (evidence.Origin == EvidenceOriginKind.Document)
            {
                documentSpans.AddRange([start, text.Length - start]);
            }
        }

        foreach (var node in request.Nodes)
        {
            text.Append($"<<<NODO nodeId={node.NodeId} vista={node.View} livello={node.Level} membri={node.MemberCount} memberSetHash={node.MemberSetHash}>>>\n").Append(node.Descriptor).Append("\n<<<FINE NODO>>>\n");
        }

        text.Append("\nRispondi ora con l'unico oggetto JSON reconcile-1 del batch.");
        var message = new ChatMessage(ChatRole.User, text.ToString());
        if (documentSpans.Count > 0)
        {
            message.AdditionalProperties = new() { [ContextComposition.DocumentSpansProperty] = documentSpans.ToArray() };
        }

        return [message];
    }
}
