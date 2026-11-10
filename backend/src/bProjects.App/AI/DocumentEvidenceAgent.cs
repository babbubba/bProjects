using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace bProjects.App.AI;

/// <summary>
/// Esecuzione di un'unità di estrazione documentale già in lease (DocumentJob ExtractUnit, T050); deve onorare il token, annullato quando
/// il lease si perde o il root scade. Committa da sé i dati di lavoro e la chiusura dell'unità; un errore risale al dispatcher, che lo classifica.
/// </summary>
public interface IDocumentUnitExecutor
{
    Task ExecuteAsync(DocumentUnitClaim claim, CancellationToken ct);
}

/// <summary>Relazione validata fra evidenze della stessa unità (chiavi locali): il remapping globale è dell'aggregatore, la correlazione semantica dell'analisi.</summary>
public sealed record DocumentUnitRelation(string FromKey, string ToKey, EvidenceRelationType Type, EvidenceRelationBasis Basis, string? Rationale);

/// <summary>Evidenza validata dell'unità: contenuto 1.1 con fonti già risolte dal server (UUID, range, citazione esatta). <c>GlobalKey</c> = ordinale dell'unità + chiave locale.</summary>
public sealed record DocumentUnitEvidence(
    string Key, string GlobalKey, EvidenceKind Kind, EvidencePolarity Polarity, EvidenceCertainty Certainty, string Statement, DocumentEvidenceContent Content);

/// <summary>
/// Payload del WorkOutput di un batch di estrazione: l'unico risultato immutabile dell'unità. Non è una EvidenceExtractionVersion e non
/// rende Ready la preparazione (AI §2.3: l'aggregatore è l'unico proprietario del commit aggregato). Zero evidenze richiede il motivo.
/// </summary>
public sealed record DocumentUnitOutput(
    string SchemaVersion, int UnitOrdinal, string? NoEvidenceReason, IReadOnlyList<DocumentUnitEvidence> Evidences, IReadOnlyList<DocumentUnitRelation> Relations);

/// <summary>
/// Agente di estrazione delle evidenze documentali (T049; contracts/ai-workflow.md §2, §3, §5). Esegue UN DocumentJob ExtractUnit:
/// legge le sue porzioni (range proprietari e di contesto sul CanonicalText immutabile), le partiziona in modo esaustivo con
/// <see cref="DocumentPortionPlanner"/> misurando il payload COMPLETO sul runtime verificato, e percorre il grafo nativo fisso di T043 con il
/// port di T044: un batch per invio, ciascuno in una AgentSession nuova senza history, risposta validata dallo schema strict e dal
/// validatore deterministico (T034/T035), output Committed prima di Advance. La preparazione è sempre Extended (nessuna classificazione né
/// promozione: il cap è quello della preparazione). Output non valido, troncato o unità irriducibile sono errori permanenti; trasporto,
/// timeout e runtime non raggiungibile sono transitori (decide il dispatcher). Il budget chiamate è del root (preparazione) e persiste ai retry.
/// </summary>
public sealed class DocumentEvidenceAgent(
    IServiceScopeFactory scopes, AgentFactory factory, InternalModelClient client, IOptions<DocumentOptions> documents) : IDocumentUnitExecutor
{
    public const string AgentName = "DocumentEvidenceAgent";
    public const string AgentId = "document-evidence";
    /// <summary>Prompt storico: lo usano i job creati prima di v3 (la configurazione congelata non ha <c>promptVersion</c>).</summary>
    public const string PromptVersionV2 = "document-extraction-v2";

    /// <summary>Prompt corrente: lo congelano i job creati da ora (002 T082).</summary>
    public const string PromptVersion = "document-extraction-v3";

    /// <summary>Definizione del grafo congelata per i nuovi owner (checkpoint e manifesto); un owner con un'altra definizione non si riprende.</summary>
    public const string DefinitionVersion = "document-extraction-1";

    public const string AlgorithmVersion = "portion-planner-v1";
    public const string ValidationVersion = "document-evidence-validation-v1";
    public const string BatchStage = "extract";

    /// <summary>Moltiplicatore dell'ordinale della prima porzione: ordinale globale di unità = prima porzione × 10000 + ordinale del batch.</summary>
    public const int UnitOrdinalStride = 10_000;

    private static readonly Lazy<string> Prompt = new(() => LoadPrompt("document-extraction-v3.md"));
    private static readonly Lazy<string> PromptV2 = new(() => LoadPrompt("document-extraction-v2.md"));

    public static string Instructions => Prompt.Value;

    public static string InstructionsFor(string promptVersion) => promptVersion == PromptVersionV2 ? PromptV2.Value : Prompt.Value;

    /// <summary>
    /// Versione del prompt di un job dalla sua configurazione congelata. Il ripiego a v2 vale SOLO per la chiave <c>promptVersion</c> assente (ogni job creato prima
    /// di v3 non l'ha e deve riprendere con l'identità con cui è partito: manifesto, batchId, checkpoint). Una versione esplicita diversa da v2/v3, non testuale o una
    /// configurazione non leggibile è un errore di compatibilità permanente (<see cref="DocumentWorkException.CheckpointIncompatible"/>), mai un v2 implicito.
    /// </summary>
    public static string EffectivePromptVersion(string? frozenConfig)
    {
        if (string.IsNullOrWhiteSpace(frozenConfig))
        {
            return PromptVersionV2;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(frozenConfig);
        }
        catch (JsonException)
        {
            throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "La configurazione congelata del job non è leggibile: versione del prompt di estrazione non determinabile.");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "La configurazione congelata del job non è un oggetto: versione del prompt di estrazione non determinabile.");
            }

            if (!doc.RootElement.TryGetProperty("promptVersion", out var v))
            {
                return PromptVersionV2;
            }

            return v.ValueKind == JsonValueKind.String && v.GetString() is PromptVersionV2 or PromptVersion
                ? v.GetString()!
                : throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "La versione del prompt di estrazione congelata nel job non è supportata da questa versione dell'applicazione.");
        }
    }

    public async Task ExecuteAsync(DocumentUnitClaim claim, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UnitInput input;
        using (DocumentTiming.Span("agent.loadInput"))
        {
            input = await UnitInputLoader.LoadAsync(db, claim, ct);
        }

        var definition = input.WorkflowDefinitionVersion ?? DefinitionVersion;
        if (definition != DefinitionVersion)
        {
            // Nessuna migrazione implicita di un'altra definizione: errore esplicito e permanente.
            throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "La definizione del workflow dell'unità non è compatibile con quella corrente.");
        }

        DocumentRuntimeProfile profile;
        using (DocumentTiming.Span("agent.resolveProfile"))
        {
            profile = await factory.Preflight.ResolveProfileAsync(AiPhase.Extraction, ct);
        }
        var limits = new PlannerLimits(profile.InputBudgetTokens, profile.OutputTokens, factory.Options.ExtractionOutputExpansion);
        var structured = factory.Options.StructuredOutputMode == StructuredOutputMode.JsonSchema;
        var promptVersion = EffectivePromptVersion(claim.ConfigJson);
        var partitionSpan = DocumentTiming.Span("agent.partition");
        var partition = await new DocumentPortionPlanner(limits).PartitionAsync(
            input.Portions, (ranges, token) => MeasureAsync(profile, structured, promptVersion, ranges, token), ct);
        partitionSpan?.Dispose();
        if (partition.Batches.Count >= UnitOrdinalStride)
        {
            throw new DocumentWorkException(DocumentWorkException.DocumentIncomplete, "L'unità richiede troppi batch per essere identificata in modo univoco.");
        }

        var writer = CheckpointWriter.ForDocumentJob(claim.JobId, claim.LeaseOwner, claim.FencingToken, definition);
        var store = new DocumentWorkStore(db, writer);
        var plan = new DocumentExtractionPlan(
            claim, input, partition, profile, limits, factory.Options, client, structured, promptVersion,
            async (resultHash, portions, token) =>
            {
                using var completeSpan = DocumentTiming.Span("agent.completeUnit");
                await store.CompleteUnitAsync(resultHash, portions, claim.AttemptCount, token);
            });
        var port = new DocumentBatchPort(store, db, writer, factory, plan, new RootCallCounterReservation(db, writer, FrozenMaxCalls(claim.ConfigJson, documents.Value)));
        var checkpoints = CheckpointManager.CreateJson(new PostgresCheckpointStore(db, writer));
        try
        {
            using (DocumentTiming.Span("agent.workflow"))
            {
                await DocumentBatchWorkflow.RunAsync(DocumentBatchWorkflow.Build(port, new BatchInvocationCounter()), checkpoints, SessionId(claim.JobId), null, ct);
            }
        }
        catch (Exception ex) when (IsLeaseLost(ex))
        {
            // Il dominio prevale sul checkpoint: se l'unità è già Completed sotto il nostro fence (il commit ha rilasciato il lease) la scrittura
            // del checkpoint finale è stata rifiutata dalla guard. Nessuna ripresa, nessun secondo stato finale. Altrimenti il lease è davvero perso.
            if (!await CompletedByAsync(claim, ct))
            {
                throw;
            }
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

    private async Task<bool> CompletedByAsync(DocumentUnitClaim claim, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().DocumentJobs.AsNoTracking()
            .AnyAsync(j => j.Id == claim.JobId && j.Status == DocumentJobStatus.Completed && j.ResultHash != null && j.FencingToken == claim.FencingToken, ct);
    }

    /// <summary>Sessione del workflow MAF dell'unità: stabile sul job, quindi i retry riprendono dallo stesso checkpoint nativo.</summary>
    public static string SessionId(Guid jobId) => $"unit-{jobId:N}";

    /// <summary>
    /// Messaggio utente unico del batch: i dati del server e, per ogni range, un blocco delimitato con la sua <c>rangeKey</c> e il ruolo.
    /// Il testo del range è inserito integralmente e senza alterazioni: la citazione del modello deve esserne una sottostringa esatta.
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildMessages(IReadOnlyList<DocumentSourceRange> ranges)
    {
        // A capo sempre "\n" (mai Environment.NewLine): il payload, quindi il suo hash, non dipende dal sistema operativo.
        var text = new StringBuilder()
            .Append("Dati affidabili del server per questa unità di estrazione:\n")
            .Append($"- schema: {DocumentEvidenceContent.SchemaVersionValue}\n")
            .Append($"- intervalli: {ranges.Count} ({ranges.Count(r => r.Owned)} Primary, {ranges.Count(r => !r.Owned)} Context)\n\n");
        foreach (var range in ranges)
        {
            text.Append($"<<<INTERVALLO rangeKey={range.RangeKey} ruolo={(range.Owned ? "Primary" : "Context")}>>>\n")
                .Append(range.Text.ToString()).Append('\n')
                .Append($"<<<FINE rangeKey={range.RangeKey}>>>\n");
        }

        text.Append("\nProduci ora l'unico oggetto JSON evidence-1.1.0 per i soli intervalli Primary.");
        // Il payload dell'unità è composto dai soli intervalli sorgente e dai dati del server: è la categoria document_sources.
        return [new ChatMessage(ChatRole.User, text.ToString()) { AdditionalProperties = new() { [ContextComposition.CategoryProperty] = ContextComposition.DocumentSources } }];
    }

    private async ValueTask<UnitMeasure> MeasureAsync(DocumentRuntimeProfile profile, bool structured, string promptVersion, IReadOnlyList<DocumentSourceRange> ranges, CancellationToken ct)
    {
        using var measureSpan = DocumentTiming.Span("agent.measure");
        var schema = structured ? DocumentEvidenceSchema.ForUnit(ranges.Select(r => r.RangeKey)).GetRawText() : null;
        var full = await factory.Preflight.MeasureAsync(
            new DocumentPayload(AiPhase.Extraction, InstructionsFor(promptVersion), BuildMessages(ranges), schema), ct, profile.Fingerprint);
        // Base della stima dell'output: i soli intervalli proprietari, misurati senza istruzioni né schema.
        var ownedText = string.Join("\n", ranges.Where(r => r.Owned).Select(r => r.Text.ToString()));
        var owned = await factory.Preflight.MeasureAsync(
            new DocumentPayload(AiPhase.Extraction, "", [new ChatMessage(ChatRole.User, ownedText)]), ct, profile.Fingerprint);
        var quality = full.Quality == TokenCountQuality.Unavailable || owned.Quality == TokenCountQuality.Unavailable
            ? TokenCountQuality.Unavailable
            : full.Quality == TokenCountQuality.Measured && owned.Quality == TokenCountQuality.Measured ? TokenCountQuality.Measured : TokenCountQuality.CalibratedUpperBound;
        return new UnitMeasure(full.Tokens, quality, full.PayloadHash, owned.Tokens);
    }

    /// <summary>MaxAiCallsPerPreparation congelato nella configurazione del job, altrimenti il valore corrente della configurazione server.</summary>
    public static int FrozenMaxCalls(string? configJson, DocumentOptions fallback)
    {
        if (!string.IsNullOrWhiteSpace(configJson))
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, nameof(DocumentOptions.MaxAiCallsPerPreparation), StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var frozen) && frozen > 0)
                    {
                        return frozen;
                    }
                }
            }
        }

        return fallback.MaxAiCallsPerPreparation;
    }

    private static string LoadPrompt(string file)
    {
        using var stream = typeof(DocumentEvidenceAgent).Assembly.GetManifestResourceStream("Prompts." + file)
            ?? throw new InvalidOperationException($"Prompt {file} non incorporato nell'assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }

    internal static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>Input dell'unità letto dal database: porzioni con range e testo canonico dei blocchi.</summary>
internal sealed record UnitInput(
    Guid ProjectId, Guid PreparationId, string PolicyHash, string? WorkflowDefinitionVersion, int FirstPortionOrdinal, string SourceHash,
    IReadOnlyList<PlannedPortion> Portions);

internal static class UnitInputLoader
{
    public static async Task<UnitInput> LoadAsync(AppDbContext db, DocumentUnitClaim claim, CancellationToken ct)
    {
        var job = await db.DocumentJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == claim.JobId && j.Kind == DocumentJobKind.ExtractUnit, ct)
            ?? throw new DocumentWorkException(DocumentWorkException.InvalidInput, "Il lavoro non è un'unità di estrazione.");
        var preparation = await db.DocumentPreparations.AsNoTracking().SingleAsync(p => p.Id == job.PreparationId, ct);
        var version = await db.DocumentVersions.AsNoTracking().SingleAsync(v => v.Id == preparation.DocumentVersionId, ct);
        var artifact = await db.ParsedArtifacts.AsNoTracking().SingleOrDefaultAsync(a => a.PreparationId == preparation.Id, ct)
            ?? throw new DocumentWorkException(DocumentWorkException.InvalidInput, "La preparazione non ha un artefatto di parsing pubblicato.");

        var rows = await db.DocumentPortions.AsNoTracking()
            .Where(p => p.PreparationId == preparation.Id && (p.Id == job.UnitId || p.JobId == job.Id)).OrderBy(p => p.Ordinal).ToListAsync(ct);
        if (rows.Count == 0)
        {
            throw new DocumentWorkException(DocumentWorkException.InvalidInput, "L'unità non ha porzioni.");
        }

        if (rows.Any(r => r.Status != PortionStatus.Pending))
        {
            throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "L'unità contiene porzioni già concluse o fallite.");
        }

        var parsed = rows.Select(r => (Row: r, Owned: ReadRanges(r.RangesJson), Context: ReadRanges(r.ContextRangesJson))).ToList();
        var blockIds = parsed.SelectMany(p => p.Owned.Concat(p.Context)).Select(r => r.BlockId).Distinct().ToList();
        var blocks = await db.DocumentBlocks.AsNoTracking().Where(b => b.ArtifactId == artifact.Id && blockIds.Contains(b.Id))
            .Select(b => new { b.Id, b.CanonicalText }).ToDictionaryAsync(b => b.Id, b => b.CanonicalText, ct);

        DocumentSourceRange Resolve(RawRange raw, bool owned)
        {
            if (!blocks.TryGetValue(raw.BlockId, out var text) || raw.Start < 0 || raw.End <= raw.Start || raw.End > text.Length)
            {
                throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"L'intervallo {raw.RangeKey} non risolve a un blocco della preparazione.");
            }

            if (!string.Equals(DocumentEvidenceAgent.Sha(text[raw.Start..raw.End]), raw.TextHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"Il testo dell'intervallo {raw.RangeKey} non coincide con l'hash registrato dal parsing.");
            }

            return new DocumentSourceRange(
                raw.RangeKey, version.DocumentId, version.Id, preparation.Id, artifact.Id, raw.BlockId, text, raw.Start, raw.End, owned);
        }

        var portions = parsed.Select(p => new PlannedPortion(
            p.Row.Ordinal, [.. p.Owned.Select(r => Resolve(r, owned: true))], [.. p.Context.Select(r => Resolve(r, owned: false))])).ToList();
        var sourceHash = DocumentEvidenceAgent.Sha(string.Join("|", rows.Select(r => $"{r.Ordinal}:{r.TextHash}")));
        var policyHash = System.Text.RegularExpressions.Regex.IsMatch(preparation.PolicyHash, "^[0-9a-f]{64}$")
            ? preparation.PolicyHash
            : DocumentEvidenceAgent.Sha(preparation.PolicyHash);
        return new UnitInput(job.ProjectId, preparation.Id, policyHash, job.WorkflowDefinitionVersion, rows[0].Ordinal, sourceHash, portions);
    }

    private sealed record RawRange(string RangeKey, Guid BlockId, int Start, int End, string TextHash);

    private static List<RawRange> ReadRanges(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(r => new RawRange(
            r.GetProperty("rangeKey").GetString()!, r.GetProperty("blockId").GetGuid(), r.GetProperty("start").GetInt32(), r.GetProperty("end").GetInt32(),
            r.GetProperty("textHash").GetString()!))];
    }
}

/// <summary>Chiusura di dominio dell'unità: hash del risultato e esito per porzione; implementata sullo store fenced.</summary>
internal delegate ValueTask UnitCompletion(string resultHash, IReadOnlyList<PortionCompletion> portions, CancellationToken ct);

/// <summary>
/// Parte di dominio del grafo batch per un'unità di estrazione (<see cref="IDocumentBatchPlan"/>): cosa si invia, come si valida, come si
/// chiude. Deterministico nel replay: identità dei batch = hash di owner, manifesto, definizione, stadio, ordinale e payload completo.
/// </summary>
internal sealed class DocumentExtractionPlan : IDocumentBatchPlan, IGuidedRetryPlan
{
    private readonly DocumentUnitClaim _claim;
    private readonly UnitInput _input;
    private readonly ExtractionPartition _partition;
    private readonly DocumentRuntimeProfile _profile;
    private readonly InternalModelClient _client;
    private readonly bool _structured;
    private readonly UnitCompletion _complete;
    private readonly string _promptVersion;
    private readonly string _manifestIdentity;
    private readonly DocumentSourceMap[] _maps;

    public DocumentExtractionPlan(
        DocumentUnitClaim claim, UnitInput input, ExtractionPartition partition, DocumentRuntimeProfile profile, PlannerLimits limits, AiOptions options,
        InternalModelClient client, bool structured, string promptVersion, UnitCompletion complete)
    {
        _promptVersion = promptVersion;
        _claim = claim;
        _input = input;
        _partition = partition;
        _profile = profile;
        _client = client;
        _structured = structured;
        _complete = complete;
        try
        {
            _maps = [.. partition.Batches.Select(b => new DocumentSourceMap(b.Ranges))];
        }
        catch (ArgumentException ex)
        {
            throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"Intervalli del piano non validi: {ex.Message}");
        }

        var settings = options.ForPhase(AiPhase.Extraction);
        var configHash = DocumentEvidenceAgent.Sha(JsonSerializer.Serialize(new
        {
            prompt = promptVersion, instructions = DocumentEvidenceAgent.Sha(DocumentEvidenceAgent.InstructionsFor(promptVersion)),
            schema = DocumentEvidenceContent.SchemaVersionValue, schemaName = DocumentEvidenceSchema.Name, structured,
            model = settings.ModelId, maxOutput = settings.MaxOutputTokens, thinking = settings.EnableThinking,
            inputBudget = limits.InputBudgetTokens, outputTokens = limits.OutputTokens, outputExpansion = limits.OutputExpansion, expansionCalibration = PlannerLimits.ExtractionCalibrationVersion,
        }));
        var graph = new JsonArray(DocumentBatchWorkflow.Stages.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        var batches = new JsonArray(partition.Batches.Select(b => JsonNode.Parse(DocumentPortionPlanner.DescribeInputs(b))!).ToArray());
        var planJson = new JsonObject
        {
            ["algorithm"] = DocumentEvidenceAgent.AlgorithmVersion,
            ["batches"] = batches,
            ["definition"] = DocumentEvidenceAgent.DefinitionVersion,
            ["graph"] = graph,
            ["regime"] = "Extended",   // la preparazione è sempre Extended: nessuna classificazione né promozione per le unità
        }.ToJsonString();
        Manifest = new WorkManifestInput(
            configHash, input.SourceHash, profile.Fingerprint, DocumentEvidenceAgent.DefinitionVersion, DocumentEvidenceAgent.AlgorithmVersion, input.PolicyHash, planJson);
        _manifestIdentity = CanonicalJson.Sha256Hex(JsonSerializer.Serialize(new
        {
            configHash, source = input.SourceHash, runtime = profile.Fingerprint, definition = DocumentEvidenceAgent.DefinitionVersion,
            algorithm = DocumentEvidenceAgent.AlgorithmVersion, policy = input.PolicyHash, plan = JsonDocument.Parse(planJson).RootElement,
        }));
    }

    public WorkManifestInput Manifest { get; }

    public WorkLookupExpectation Expectation => new(DocumentEvidenceContent.SchemaVersionValue, DocumentEvidenceAgent.ValidationVersion);

    public BatchAgentSpec Agent => AgentOf(_partition.Batches.SelectMany(b => b.Ranges).Select(r => r.RangeKey));

    public BatchAgentSpec AgentFor(BatchRef batch) => AgentOf(_partition.Batches[batch.Ordinal].Ranges.Select(r => r.RangeKey));

    private BatchAgentSpec AgentOf(IEnumerable<string> rangeKeys) => new(
        DocumentEvidenceAgent.AgentName, DocumentEvidenceAgent.AgentId, DocumentEvidenceAgent.InstructionsFor(_promptVersion),
        _structured
            ? _client.CreateChatOptions(DocumentEvidenceSchema.ForUnit(rangeKeys), DocumentEvidenceSchema.Name, temperature: 0, phase: AiPhase.Extraction)
            : _client.CreateChatOptions(temperature: 0, phase: AiPhase.Extraction),
        AiPhase.Extraction, _profile.Fingerprint);

    public bool HasBatch(int ordinal) => ordinal >= 0 && ordinal < _partition.Batches.Count;

    public ValueTask<DerivedBatchInput> PrepareAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        var planned = _partition.Batches[ordinal];
        var inputHash = planned.Measure.PayloadHash;
        var batchId = DocumentEvidenceAgent.Sha(
            $"batch|DocumentJob|{_claim.JobId:N}|{_manifestIdentity}|{DocumentEvidenceAgent.DefinitionVersion}|{DocumentEvidenceAgent.BatchStage}|{ordinal}|{inputHash}");
        var provenance = planned.Measure.Quality == TokenCountQuality.Measured ? "measured" : "calibrated_upper_bound";
        return new(new DerivedBatchInput(
            batchId, DocumentEvidenceAgent.BatchStage, null, null, null, ordinal, [], DocumentPortionPlanner.DescribeInputs(planned), planned.Measure.Tokens, provenance,
            inputHash, DocumentEvidenceContent.SchemaVersionValue, _promptVersion));
    }

    public ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, IBatchOutputs prior, CancellationToken ct) =>
        new([.. DocumentEvidenceAgent.BuildMessages(_partition.Batches[batch.Ordinal].Ranges)]);

    public int UnitOrdinal(int batchOrdinal) => _input.FirstPortionOrdinal * DocumentEvidenceAgent.UnitOrdinalStride + batchOrdinal;

    private static string DescribeRules(IReadOnlyList<string> violations)
    {
        var text = string.Join(" | ", violations.Take(5).Select(v => System.Text.RegularExpressions.Regex.Replace(v, "«[^»]*»", "«…»")));
        return text.Length <= 600 ? text : text[..600] + "…";
    }

    /// <summary>Stadio del marcatore del retry consumato (T134): batch d'appoggio fuori dal cursore del grafo, mai con un WorkOutput.</summary>
    public const string GuidedRetryStage = "extract_retry";

    /// <summary>Ordinale del marcatore: sopra ogni ordinale di batch dell'unità, così <c>GetByOrdinalAsync</c> non lo incontra mai.</summary>
    private const int GuidedRetryOrdinalBase = 1_000_000;

    /// <summary>
    /// Marcatore del retry consumato (T134): identità = job, manifesto, definizione e batch originale, quindi uguale a ogni replay (un solo retry per batch anche dopo
    /// restart/recovery/altro worker); porta solo classe, conteggio e hash del primo output (nessun testo). Stessi schema e prompt dell'estrazione: nessuna identità cambia.
    /// </summary>
    public DerivedBatchInput GuidedRetryMarker(BatchRef batch, GuidedRetryTrigger trigger)
    {
        var retry = (ExtractionOtherRationaleException)trigger;
        var id = DocumentEvidenceAgent.Sha($"guided-retry|DocumentJob|{_claim.JobId:N}|{_manifestIdentity}|{DocumentEvidenceAgent.DefinitionVersion}|{batch.BatchId}");
        var inputs = JsonSerializer.Serialize(new { guidedRetryOf = batch.BatchId, kind = "other_without_rationale", evidences = retry.EvidenceKeys.Count, firstOutputHash = retry.OutputHash });
        return new DerivedBatchInput(
            id, GuidedRetryStage, null, null, null, GuidedRetryOrdinalBase + batch.Ordinal, [], inputs, null, "calibrated_upper_bound",
            DocumentEvidenceAgent.Sha($"guided-retry-input|{batch.InputHash}"), DocumentEvidenceContent.SchemaVersionValue, _promptVersion);
    }

    /// <summary>
    /// Secondo messaggio: il primo ripetuto per intero (stessi range, stesso schema) più la correzione. Nessun testo né evidenza del primo output: nessun merge, il server non
    /// scrive semantica; il modello rigenera l'estrazione COMPLETA.
    /// </summary>
    public IReadOnlyList<ChatMessage> ComposeGuidedRetry(BatchRef batch, GuidedRetryTrigger trigger)
    {
        var retry = (ExtractionOtherRationaleException)trigger;
        var first = DocumentEvidenceAgent.BuildMessages(_partition.Batches[batch.Ordinal].Ranges);
        var text = new StringBuilder(string.Concat(first.Select(m => m.Text)))
            .Append("\n\nCORREZIONE (unico nuovo tentativo): la tua risposta precedente è stata scartata perché queste evidenze hanno kind Other senza kindRationale:\n")
            .Append(string.Join("\n", retry.EvidenceKeys.Select(k => $"- {k}")))
            .Append("\nLe informazioni narrative non pertinenti al progetto (cronaca, aneddoti, contesto discorsivo senza effetto su requisiti, vincoli, decisioni o dati del progetto) non devono diventare evidenze: ")
            .Append("se un'unità contiene solo questo, restituisci zero evidenze con noEvidenceReason. ")
            .Append("Usa kind Other solo per informazioni pertinenti al progetto che non sono classificabili in nessun altro kind; ogni Other legittimo richiede una kindRationale specifica che spieghi perché nessun altro kind si applica. ")
            .Append("Produci di nuovo l'estrazione COMPLETA, tutte le evidenze e tutti i campi: conserva le evidenze valide della risposta precedente (con le loro condizioni, negazioni e citazioni esatte), ")
            .Append("rimuovi quelle non pertinenti o motiva quelle legittime, e non limitarti alle evidenze corrette. ")
            .Append("Rispondi con l'unico oggetto JSON evidence-1.1.0 per i soli intervalli Primary.");
        var message = new ChatMessage(ChatRole.User, text.ToString());
        if (first.Count > 0 && first[0].AdditionalProperties is { } properties)
        {
            message.AdditionalProperties = new AdditionalPropertiesDictionary(properties);   // categoria document_sources del primo messaggio
        }

        return [message];
    }

    /// <summary>Validazione definitiva (risposta del retry o retry non ammesso): lo stesso validatore, ma un rifiuto è sempre il permanente <c>work_output_invalid</c>, mai un nuovo trigger.</summary>
    public ValidatedBatch ValidateFinal(BatchRef batch, string responseText) => ValidateCore(batch, responseText, allowRetry: false);

    public ValidatedBatch Validate(BatchRef batch, string responseText) => ValidateCore(batch, responseText, allowRetry: true);

    private ValidatedBatch ValidateCore(BatchRef batch, string responseText, bool allowRetry)
    {
        var planned = _partition.Batches[batch.Ordinal];
        var unitOrdinal = UnitOrdinal(batch.Ordinal);
        var context = new DocumentExtractionContext(_input.ProjectId, _input.PreparationId, unitOrdinal, _maps[batch.Ordinal], [], new HashSet<Guid>(), []);
        var result = DocumentEvidenceValidator.ValidateText(responseText, context);
        if (!result.Accepted)
        {
            // Regole violate senza il testo citato: i frammenti fra «» (citazioni del modello/documento) sono sostituiti da «…».
            var message = $"L'output del modello non è un'estrazione valida ({result.Violations.Count} violazioni): nessun output committato. Regole: {DescribeRules(result.Violations)}";
            if (allowRetry && result.OtherWithoutRationaleKeys is { Count: > 0 } keys)
            {
                // Retry guidato (T134): unico caso tipizzato dal validatore (tutte le violazioni sono Other senza motivazione); ogni altra violazione resta permanente.
                throw new ExtractionOtherRationaleException(message, keys, DocumentEvidenceAgent.Sha(responseText));
            }

            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, message);
        }

        var output = new DocumentUnitOutput(
            DocumentEvidenceContent.SchemaVersionValue, unitOrdinal, result.NoEvidenceReason,
            [.. result.Evidences.Select(e => new DocumentUnitEvidence(e.Key, e.GlobalKey, e.Kind, e.Polarity, e.Certainty, e.Statement, e.Content))],
            [.. result.Relations.Select(r => new DocumentUnitRelation(r.FromKey, r.ToKey!, r.Type, r.Basis, r.Rationale))]);
        var payload = JsonSerializer.Serialize(output, DocumentEvidenceCodec.Options);
        var maps = JsonSerializer.Serialize(new
        {
            evidenceSources = output.Evidences.Select(e => new { key = e.Key, sourceIds = e.Content.Sources.Select(s => s.SourceId).ToArray() }),
        });
        var covered = JsonSerializer.Serialize(planned.Owned.Select(r => r.RangeKey));
        var written = new WorkOutputInput(
            batch.BatchId, DocumentBatchWorkflow.InvokeId, batch.InputHash, CanonicalJson.Sha256Hex(payload), payload, null, null, covered, maps, null,
            DocumentEvidenceContent.SchemaVersionValue, DocumentEvidenceAgent.ValidationVersion, null);
        return new ValidatedBatch(written);
    }

    /// <summary>Copertura: ogni batch ha un output Committed che copre esattamente i suoi range proprietari, nell'ordine pianificato.</summary>
    public async ValueTask AssembleAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        for (var i = 0; i < _partition.Batches.Count; i++)
        {
            var output = await outputs.GetByOrdinalAsync(i, ct)
                ?? throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, $"Assemble senza l'output del batch {i}: unità non completa.");
            var covered = JsonSerializer.Deserialize<string[]>(output.CoveredIdsJson) ?? [];
            if (!covered.SequenceEqual(_partition.Batches[i].Owned.Select(r => r.RangeKey), StringComparer.Ordinal))
            {
                throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, $"L'output del batch {i} non copre esattamente i suoi range proprietari.");
            }
        }
    }

    public async ValueTask<int> CommitAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var results = new List<(int Batch, string Hash, int Evidences, string? Reason)>();
        for (var i = 0; i < _partition.Batches.Count; i++)
        {
            var output = await outputs.GetByOrdinalAsync(i, ct)
                ?? throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, $"Commit senza l'output del batch {i}.");
            var decoded = JsonSerializer.Deserialize<DocumentUnitOutput>(output.PayloadJson ?? throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Payload mancante."), DocumentEvidenceCodec.Options)
                ?? throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Payload nullo.");
            results.Add((i, output.OutputHash, decoded.Evidences.Count, decoded.NoEvidenceReason));
        }

        var resultHash = DocumentEvidenceAgent.Sha("unit-result|" + string.Join(',', results.Select(r => r.Hash)));
        var portions = _input.Portions.Select(p =>
        {
            var batches = results.Where(r => _partition.Batches[r.Batch].PortionOrdinals.Contains(p.Ordinal)).ToList();
            var none = batches.Count > 0 && batches.All(b => b.Evidences == 0);
            return new PortionCompletion(p.Ordinal, none ? string.Join(" / ", batches.Select(b => b.Reason).Where(r => r is not null).Distinct()) : null);
        }).ToList();
        await _complete(resultHash, portions, ct);
        return results.Count;
    }
}
