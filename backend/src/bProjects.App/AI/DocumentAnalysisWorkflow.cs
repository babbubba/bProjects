using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace bProjects.App.AI;

/// <summary>Evidenza dello snapshot congelato del run (messaggio PM o documento) con le sue fonti persistite e l'hash di contenuto derivato dal server.</summary>
public sealed record DocumentAnalysisEvidence(
    Guid EvidenceId, Guid VersionId, EvidenceOriginKind Origin, EvidenceKind Kind, EvidencePolarity Polarity, EvidenceCertainty Certainty, string Statement,
    string ContentHash, IReadOnlyList<PersistedSource> Sources);

/// <summary>
/// Input immutabile del run documentale: identità e cutoff, snapshot di riconciliazione (evidenze e relazioni note), evidenze con i testi per il prompt e dialogo.
/// <see cref="BatchKey"/> e <see cref="InputHash"/> sono identità del server dell'unico batch, note prima della chiamata e dunque comunicabili al modello.
/// </summary>
public sealed record DocumentAnalysisInput(
    Guid ProjectId, Guid AnalysisId, long Cutoff, int PmMessageCount, int ReadyDocumentSourceCount, int Unavailable, string SnapshotHash,
    ReconciliationSnapshot Snapshot, IReadOnlyList<DocumentAnalysisEvidence> Evidence, IReadOnlyList<AnalysisDialogMessage> Dialog)
{
    public string BatchKey => DocumentAnalysisWorkflow.Sha($"single-call-batch|{AnalysisId:N}|{Snapshot.SourceHash}");

    public string InputHash => DocumentAnalysisWorkflow.Sha($"single-call-input|{Snapshot.SourceHash}|{string.Join(',', Snapshot.Evidence.Select(e => e.EvidenceId.ToString("N")))}");

    public DocumentAnalysisFacts Facts => new(ProjectId, AnalysisId, Cutoff, PmMessageCount, ReadyDocumentSourceCount);
}

/// <summary>
/// Carica lo snapshot congelato del run (mai la selezione corrente): le versioni Evidence della selezione del run (messaggi PM entro il cutoff e
/// documenti Ready inclusi nello snapshot di fonti) con le loro evidenze, fonti e relazioni note. Le fonti dei messaggi PM non hanno un
/// identificativo persistito: ne è derivato uno deterministico per (evidenza, parte) così ogni fonte di analysis ha un id unico nello snapshot.
/// </summary>
public static class DocumentAnalysisSources
{
    public static async Task<DocumentAnalysisInput> LoadAsync(AppDbContext db, Guid runId, CancellationToken ct)
    {
        var run = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == runId && r.Kind == ProcessingRunKind.Analysis, ct);
        var items = await db.AnalysisEvidenceSelectionItems.AsNoTracking().Where(i => i.RunId == runId).ToListAsync(ct);
        var versionIds = items.Select(i => i.VersionId).ToList();
        var rows = await db.Evidences.AsNoTracking().Where(e => versionIds.Contains(e.VersionId)).ToListAsync(ct);
        var sequences = items.Where(i => i.MessageId != null).ToDictionary(i => i.VersionId, i => i.MessageSequence ?? 0);

        var snapshotItems = run.SourceSnapshotId is { } snapshotId
            ? await db.SourceSnapshotItems.AsNoTracking().Where(s => s.SnapshotId == snapshotId).OrderBy(s => s.DocumentId).ToListAsync(ct)
            : [];
        var documentOrder = snapshotItems.Where(s => s.EvidenceVersionId != null).Select(s => s.EvidenceVersionId!.Value).ToList();
        var snapshotHash = run.SourceSnapshotId is { } id ? (await db.SourceSnapshots.AsNoTracking().SingleAsync(s => s.Id == id, ct)).Hash : "";

        // Ordine deterministico: messaggi PM per sequenza e ordinale, poi documenti nell'ordine dello snapshot di fonti e per ordinale.
        var ordered = rows
            .OrderBy(r => r.OriginKind == EvidenceOriginKind.Message ? 0 : 1)
            .ThenBy(r => r.OriginKind == EvidenceOriginKind.Message ? sequences.GetValueOrDefault(r.VersionId) : documentOrder.IndexOf(r.VersionId))
            .ThenBy(r => r.Ordinal)
            .ToList();

        var evidence = new List<DocumentAnalysisEvidence>();
        foreach (var row in ordered)
        {
            IReadOnlyList<PersistedSource> sources;
            try
            {
                sources = row.OriginKind == EvidenceOriginKind.Document ? DocumentEvidenceCodec.DecodeContent(row.Content).Sources : PmSources(row);
            }
            catch (JsonException)
            {
                throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Il contenuto di un'evidenza dello snapshot non è decodificabile.");
            }

            var node = new JsonObject
            {
                ["kind"] = row.Kind.ToString(), ["polarity"] = row.Polarity.ToString(), ["certainty"] = row.Certainty.ToString(), ["statement"] = row.Statement,
                ["content"] = JsonNode.Parse(row.Content),
            };
            evidence.Add(new(row.Id, row.VersionId, row.OriginKind, row.Kind, row.Polarity, row.Certainty, row.Statement, CanonicalJson.Sha256Hex(node.ToJsonString()), sources));
        }

        var relations = await db.EvidenceRelations.AsNoTracking().Where(r => versionIds.Contains(r.VersionId)).OrderBy(r => r.Id).ToListAsync(ct);
        var known = evidence.Select(e => e.EvidenceId).ToHashSet();
        var snapshotRelations = relations.Where(r => known.Contains(r.FromEvidenceId) && known.Contains(r.ToEvidenceId))
            .Select(r => new SnapshotRelation(r.Id, r.FromEvidenceId, r.ToEvidenceId, r.Type)).ToList();

        var messages = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == run.ConversationId && m.Sequence <= run.SourceCutoffSequence).OrderBy(m => m.Sequence).ToListAsync(ct);
        var dialog = messages.Select(m => new AnalysisDialogMessage(
            m.Id, m.Role == MessageRole.User ? AnalysisMessageRole.User : AnalysisMessageRole.Assistant, m.Sequence, m.Body)).ToList();

        // sourceHash (§4.1) copre snapshot di fonti, cutoff, EvidenceVersion e contentHash.
        var sourceHash = CanonicalJson.Sha256Hex(new JsonObject
        {
            ["snapshot"] = snapshotHash,
            ["cutoff"] = run.SourceCutoffSequence!.Value,
            ["evidence"] = new JsonArray([.. evidence.Select(e => (JsonNode)new JsonObject { ["id"] = e.EvidenceId.ToString("D"), ["version"] = e.VersionId.ToString("D"), ["hash"] = e.ContentHash })]),
        }.ToJsonString());

        return new DocumentAnalysisInput(
            run.ProjectId, run.Id, run.SourceCutoffSequence!.Value, messages.Count(m => m.Role == MessageRole.User),
            snapshotItems.Count(s => s.Included && s.Availability == SourceAvailability.Ready && s.EvidenceVersionId != null),
            snapshotItems.Count(s => s.Availability == SourceAvailability.Unavailable), snapshotHash,
            new ReconciliationSnapshot(run.ProjectId, sourceHash, [.. evidence.Select(e => new SnapshotEvidence(e.EvidenceId, e.VersionId, e.ContentHash, e.Sources))], snapshotRelations),
            evidence, dialog);
    }

    private static IReadOnlyList<PersistedSource> PmSources(EvidenceItem row)
    {
        var content = EvidenceJson.Deserialize<EvidenceContent>(row.Content);
        return [.. content.Sources.Select((s, index) => new PersistedSource
        {
            SourceId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"pm-source|{row.Id:D}|{index}"))[..16]),
            Kind = EvidenceSourceKind.Message, Role = s.Role,
            CitationGroupId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"pm-group|{row.Id:D}|{index}"))[..16]), Part = 0,
            MessageId = s.MessageId, Sequence = s.Sequence, DocumentId = null, DocumentVersionId = null, PreparationId = null, ArtifactId = null, BlockId = null,
            Start = s.Start, End = s.End, Quote = s.Quote,
        })];
    }
}

/// <summary>
/// Base immutabile del passaggio (§4.4) calcolata dal SERVER: gli itemProposals validati e le relazioni note dello snapshot. L'AI non calcola mai
/// baseHash/expectedHash: nel ramo singola chiamata l'ancora di un item è la sua <c>localKey</c>, quella di una relazione il suo id; tutte le
/// patch si validano contro questa stessa base.
/// </summary>
public static class DocumentAnalysisBase
{
    public static string ItemHash(ItemProposal item) => ReconciliationHash("item", new
    {
        localKey = item.LocalKey, text = item.Text, classification = item.Classification,
        evidenceIds = ReconciliationIds.SortedIds(item.EvidenceIds).ToArray(), sourceIds = ReconciliationIds.SortedIds(item.SourceIds).ToArray(),
    });

    public static string RelationHash(SnapshotRelation relation) => ReconciliationHash("relation", new
    {
        id = relation.RelationId.ToString("D"), from = relation.FromEvidenceId.ToString("D"), to = relation.ToEvidenceId.ToString("D"), type = relation.Type,
    });

    public static ReconciliationBase Compute(SingleCallOutput output, ReconciliationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(snapshot);
        var targets = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in output.Results.SelectMany(r => r.ItemProposals))
        {
            targets.TryAdd(ReconciliationBase.TargetKey(PatchTargetKind.Item, item.LocalKey), ItemHash(item));
        }

        foreach (var relation in snapshot.Relations)
        {
            targets.TryAdd(ReconciliationBase.TargetKey(PatchTargetKind.Relation, relation.RelationId.ToString("D")), RelationHash(relation));
        }

        var baseHash = ReconciliationHash("base", new { sourceHash = snapshot.SourceHash, targets = targets.Select(t => new { key = t.Key, hash = t.Value }).ToArray() });
        return new ReconciliationBase(baseHash, targets);
    }

    private static string ReconciliationHash<T>(string type, T value) => CanonicalJson.HashText(ReconciliationCodec.Canonical(new { t = type, v = value }));
}

/// <summary>
/// Validazione server della singola chiamata documentale: nucleo T120/T121 (<see cref="ReconciliationValidator.ValidateSingleCall"/>) sulla base
/// calcolata dal server, più le regole proprie del ramo — identità del batch (batchId/inputHash/origini sono quelli del server) e conflitti
/// rappresentati: una patch preservata come conflitto è terminale solo se un conflitto della revisione porta tutte le fonti e le proposte del gruppo.
/// </summary>
public static class DocumentAnalysisValidator
{
    public static ReconciliationValidation Validate(string responseText, ReconciliationSnapshot snapshot, DocumentAnalysisFacts facts, string batchKey, string inputHash)
    {
        SingleCallOutput? decoded = null;
        try
        {
            decoded = ReconciliationCodec.DecodeSingleCall(responseText);
        }
        catch (JsonException)
        {
            // Il decoder è ripetuto dal validatore comune, che produce l'errore permanente.
        }

        var baseState = decoded is null ? new ReconciliationBase(new string('0', 64), new Dictionary<string, string>()) : DocumentAnalysisBase.Compute(decoded, snapshot);
        var result = ReconciliationValidator.ValidateSingleCall(responseText, snapshot, baseState, facts);
        if (result.Output is null)
        {
            return result;
        }

        var violations = new List<string>(result.Violations);
        for (var i = 0; i < result.Output.Results.Count; i++)
        {
            var r = result.Output.Results[i];
            if (r.BatchId != batchKey)
            {
                violations.Add($"results[{i}].batchId: diverso dall'identità del batch del server.");
            }

            if (r.InputHash != inputHash)
            {
                violations.Add($"results[{i}].inputHash: diverso dall'inputHash del server.");
            }

            violations.AddRange(r.Candidates.Where(c => c.OriginBatchIds.Any(o => o != batchKey)).Select(c => $"results[{i}].candidates: originBatchIds estranei al batch del server ({c.CandidateId})."));
            violations.AddRange(r.Patches.Where(p => p.OriginBatchIds.Any(o => o != batchKey)).Select(p => $"results[{i}].patches: originBatchIds estranei al batch del server ({p.PatchId})."));
        }

        if (violations.Count > result.Violations.Count)
        {
            return ReconciliationValidation.Fail(
                result.ErrorCode ?? DocumentWorkException.WorkOutputInvalid, [.. violations.Distinct(StringComparer.Ordinal)], result.Output, result.Patches);
        }

        if (result.Outcome != ReconciliationOutcome.Complete)
        {
            return result;
        }

        var gaps = new List<CompletionGap>();
        foreach (var group in result.Patches!.Conflicts)
        {
            var represented = result.Output.Analysis.Conflicts.Any(c =>
                c.Alternatives.Count >= group.Proposals.Count && group.SourceIds.All(s => c.Alternatives.SelectMany(a => a.EvidenceRefs).Contains(s)));
            if (!represented)
            {
                gaps.Add(new("conflict_not_represented", $"gruppo di conflitto {group.GroupId}: nessun conflitto della revisione porta tutte le proposte e fonti."));
            }
        }

        if (gaps.Count == 0)
        {
            return result;
        }

        var completion = result.Completion!;
        var all = completion.Gaps.Concat(gaps).ToList();
        var hash = CanonicalJson.HashText(ReconciliationCodec.Canonical(new
        {
            expected = completion.ExpectedEntryIds, visitedA = completion.VisitedEntryIdsA, visitedB = completion.VisitedEntryIdsB, missing = completion.MissingEntryIds, gaps = all,
        }));
        return result with { Outcome = ReconciliationOutcome.ValidIncomplete, Completion = completion with { IsComplete = false, Gaps = all, CoverageHash = hash } };
    }
}

/// <summary>
/// project-analysis-documents-v2 (002 T059, contracts/ai-workflow.md §4 «percorso singola chiamata»): un run di Analizza con snapshot documentale
/// percorre il grafo fisso T043 con il port T044 (guard, riserva chiamate, lookup/recovery, commit fenced dell'output) e UN solo batch: dialogo,
/// tutte le evidenze congelate (PM e documenti) e dati del server, risposta {results:A/B/Cross, analysis 1.1.0, evidenceMap}. La risposta è
/// validata dal nucleo T120/T121 PRIMA del WorkOutput; un output strutturalmente valido ma con obbligazioni residue è persistito (riutilizzabile) ma
/// non completa mai il run (coverage_incomplete, nessuna revisione); un output invalido è errore permanente senza riparazione. Il commit finale è
/// atomico: revisione AI 1.1.0 + Completed + CurrentAnalysisId + mappa item→evidenze + relazioni di run append-only. Registrazione DI e instradamento
/// sulla definizione congelata sono di T123; la promozione additional_work (B1) e il multi-batch sono I5 (T070+).
/// </summary>
public sealed class DocumentAnalysisWorkflow(
    IServiceScopeFactory scopes, AgentFactory factory, InternalModelClient client, IOptions<DocumentOptions> documents)
{
    public const string AgentId = "document-analysis";
    public const string PromptVersion = "document-analysis-v2";
    public const string DefinitionVersion = "project-analysis-documents-v2";
    public const string AlgorithmVersion = "analysis-single-v1";
    public const string BatchStage = "analysis";
    public const string SchemaName = "document_analysis";
    public const string SchemaVersion = "document-analysis-1";
    public const string ValidationVersion = "document-analysis-validation-v1";

    private static readonly Lazy<string> Prompt = new(() => ConversationalAgent.LoadPrompt("document-analysis-v2.md"));

    /// <summary>Schema vincolante del solo involucro {results,analysis,evidenceMap}; il contenuto è validato per intero dal server.</summary>
    private static readonly JsonElement WrapperSchema = JsonDocument.Parse(
        """
        {"type":"object","additionalProperties":false,"required":["results","analysis","evidenceMap"],
         "properties":{"results":{"type":"array","minItems":3,"maxItems":3,"items":{"type":"object"}},"analysis":{"type":"object"},"evidenceMap":{"type":"array","items":{"type":"object"}}}}
        """).RootElement.Clone();

    public static string Instructions => Prompt.Value;

    public static string SessionId(Guid runId) => $"analysis-{runId:N}";

    public async Task<AnalysisRunResult> ExecuteAsync(AnalysisRunClaim claim, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == claim.RunId && r.Kind == ProcessingRunKind.Analysis, ct);
        if (run.Status is ProcessingRunStatus.Completed or ProcessingRunStatus.Failed)
        {
            return new(run.Status, run.ResultRevisionId, run.ErrorCode); // replay: un risultato terminale non si rielabora
        }

        if (run.WorkflowDefinitionVersion != DefinitionVersion || run.MafVersion != Conversation.CurrentMafVersion || !run.PromptVersionMatches("analysis", PromptVersion))
        {
            // Nessuna migrazione implicita di un altro grafo: errore esplicito e permanente (T123 instrada le definizioni supportate).
            await FailAsync(claim, "workflow_incompatible", "La definizione del workflow del run non è compatibile con quella documentale corrente.", ct);
            return await ReadAsync(claim.RunId, ct);
        }

        var selection = await new EvidenceLedgerStore(db).FreezeDocumentSelectionAsync(claim, ct);
        switch (selection.Outcome)
        {
            case SelectionOutcome.Rejected:
                throw new OperationCanceledException("Selezione delle evidenze rifiutata: il lease del run non è più valido.");
            case SelectionOutcome.Missing:
                await FailAsync(claim, EvidenceLedgerStore.EvidenceUnavailable,
                    "Estrazione delle evidenze non disponibile per i messaggi PM di sequenza " + string.Join(", ", selection.MissingSequences!) + ".", ct);
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
            : throw new InvalidOperationException("Il workflow di analisi documentale è terminato senza produrre un risultato.");
    }

    private async Task RunGraphAsync(AppDbContext db, AnalysisRunClaim claim, CancellationToken ct)
    {
        var input = await DocumentAnalysisSources.LoadAsync(db, claim.RunId, ct);
        var structured = factory.Options.StructuredOutputMode == StructuredOutputMode.JsonSchema;
        var instructions = Instructions;
        var messages = BuildMessages(input);
        var measure = await factory.Preflight.MeasureAsync(
            new DocumentPayload(AiPhase.Analysis, instructions, messages, structured ? WrapperSchema.GetRawText() : null), ct);
        if (measure.Quality == TokenCountQuality.Unavailable)
        {
            throw new RuntimeUnverifiedException("conteggio del payload dell'analisi non disponibile (nessun tokenizer o calibrazione identificata)");
        }

        if (!measure.Fits)
        {
            throw new DocumentWorkException(
                DocumentWorkException.CallLimitExceeded,
                $"Le fonti selezionate ({measure.Tokens} token) superano la capacità dell'analisi a singola chiamata ({measure.InputBudgetTokens} token): la riconciliazione di più unità non è ancora disponibile.");
        }

        using var scope = scopes.CreateScope();
        var planDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var writer = new CheckpointWriter(claim.RunId, claim.LeaseOwner, claim.FencingToken);
        var store = new DocumentWorkStore(planDb, writer);
        var plan = new DocumentAnalysisPlan(claim, input, messages, instructions, measure, structured, factory, client, WrapperSchema, scopes);
        var port = new DocumentBatchPort(store, planDb, writer, factory, plan, new RunCallReservation(planDb, writer, documents.Value.MaxAiCallsPerRun));
        var checkpoints = CheckpointManager.CreateJson(new PostgresCheckpointStore(planDb, writer));
        try
        {
            await DocumentBatchWorkflow.RunAsync(DocumentBatchWorkflow.Build(port, new BatchInvocationCounter()), checkpoints, SessionId(claim.RunId), null, ct);
        }
        catch (Exception ex) when (Find<LeaseLostException>(ex) is not null)
        {
            // Il dominio prevale sul checkpoint: se il commit atomico ha già completato il run, la scrittura del checkpoint finale è stata rifiutata.
            if (!plan.Committed)
            {
                throw;
            }
        }
    }

    /// <summary>Messaggio unico del batch: dati del server, dialogo come contesto e tutte le evidenze integrali con le loro fonti. A capo sempre «\n».</summary>
    internal static IReadOnlyList<ChatMessage> BuildMessages(DocumentAnalysisInput input)
    {
        var text = new StringBuilder();
        text.Append("Dati affidabili del server:\n")
            .Append($"analysisId: {input.AnalysisId:D}\nprojectId: {input.ProjectId:D}\nsourceCutoffSequence: {input.Cutoff}\nbatchId: {input.BatchKey}\ninputHash: {input.InputHash}\n");
        if (input.Unavailable > 0)
        {
            text.Append($"Fonti documentali non disponibili o non pronte, escluse da questa analisi: {input.Unavailable}.\n");
        }

        text.Append("\nDialogo con il project manager (contesto):\n");
        if (input.Dialog.Count == 0)
        {
            text.Append("(nessun messaggio)\n");
        }

        foreach (var message in input.Dialog)
        {
            text.Append($"[messaggio id={message.Id:D} sequenza={message.Sequence} ruolo={(message.Role == AnalysisMessageRole.User ? "user" : "assistant")}]\n").Append(message.Body).Append('\n');
        }

        text.Append("\nEvidenze congelate (").Append(input.Evidence.Count).Append("):\n");
        if (input.Evidence.Count == 0)
        {
            text.Append("(nessuna evidenza disponibile)\n");
        }

        for (var i = 0; i < input.Evidence.Count; i++)
        {
            var e = input.Evidence[i];
            var snapshot = input.Snapshot.Evidence[i];
            text.Append($"<<<VOCE entryId={ReconciliationIds.EntryId(input.Snapshot.SourceHash, e.EvidenceId, e.ContentHash)} evidenza={e.EvidenceId:D} hash={e.ContentHash} origine={e.Origin} tipo={e.Kind} polarita={e.Polarity} certezza={e.Certainty}>>>\n")
                .Append(e.Statement).Append('\n');
            foreach (var s in snapshot.Sources)
            {
                text.Append(s.Kind == EvidenceSourceKind.Message
                    ? $"<<<FONTE fonte:{s.SourceId:D} ruolo={s.Role} messaggio={s.MessageId:D}>>>\n"
                    : $"<<<FONTE fonte:{s.SourceId:D} ruolo={s.Role} documento={s.DocumentId:D} versione={s.DocumentVersionId:D} preparazione={s.PreparationId:D} artefatto={s.ArtifactId:D} blocco={s.BlockId:D} start={s.Start} end={s.End}>>>\n")
                    .Append(s.Quote).Append("\n<<<FINE FONTE>>>\n");
            }

            text.Append("<<<FINE VOCE>>>\n");
        }

        text.Append("\nRispondi ora con l'unico oggetto JSON {\"results\":...,\"analysis\":...,\"evidenceMap\":...}.");
        return [new ChatMessage(ChatRole.User, text.ToString())];
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
        await new AnalysisRunCompletion(scope.ServiceProvider.GetRequiredService<AppDbContext>()).FailAsync(claim, code, message, ct);
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
/// Riserva del contatore chiamate del ProcessingRun documentale: il POST non classifica il root (T123), quindi la prima riserva avvia il contatore a
/// zero sotto owner/fence/Running e poi delega alla riserva comune (lock, guard e limite).
/// </summary>
internal sealed class RunCallReservation(AppDbContext db, CheckpointWriter writer, int maxCalls) : IBatchCallReservation
{
    public async ValueTask ReserveAsync(BatchRef batch, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "ProcessingRuns" SET "CallCount" = 0
            WHERE "Id" = {writer.OwnerId} AND "CallCount" IS NULL AND "Status" = 'Running' AND "LeaseOwner" = {writer.LeaseOwner} AND "FencingToken" = {writer.FencingToken}
            """,
            ct);
        await new RootCallCounterReservation(db, writer, maxCalls).ReserveAsync(batch, ct);
    }
}

/// <summary>
/// Parte di dominio del grafo batch per l'analisi documentale a singola chiamata (<see cref="IDocumentBatchPlan"/>): un solo batch, validazione
/// server prima del WorkOutput, esito di completezza persistito nell'output Committed e commit atomico della revisione dall'output stesso.
/// Deterministico nel replay: identità dei batch = hash di owner, manifesto, definizione, stadio, ordinale e payload completo.
/// </summary>
internal sealed class DocumentAnalysisPlan : IDocumentBatchPlan
{
    private readonly AnalysisRunClaim _claim;
    private readonly DocumentAnalysisInput _input;
    private readonly IReadOnlyList<ChatMessage> _messages;
    private readonly PayloadPreflight _measure;
    private readonly IServiceScopeFactory _scopes;
    private readonly string _manifestIdentity;

    public DocumentAnalysisPlan(
        AnalysisRunClaim claim, DocumentAnalysisInput input, IReadOnlyList<ChatMessage> messages, string instructions, PayloadPreflight measure, bool structured,
        AgentFactory factory, InternalModelClient client, JsonElement schema, IServiceScopeFactory scopes)
    {
        _claim = claim;
        _input = input;
        _messages = messages;
        _measure = measure;
        _scopes = scopes;

        var settings = factory.Options.ForPhase(AiPhase.Analysis);
        var configHash = DocumentAnalysisWorkflow.Sha(JsonSerializer.Serialize(new
        {
            prompt = DocumentAnalysisWorkflow.PromptVersion, instructions = DocumentAnalysisWorkflow.Sha(instructions), schema = DocumentAnalysisWorkflow.SchemaVersion, structured,
            model = settings.ModelId, maxOutput = settings.MaxOutputTokens, thinking = settings.EnableThinking, inputBudget = measure.InputBudgetTokens,
        }));
        Agent = new BatchAgentSpec(
            "DocumentAnalysis", DocumentAnalysisWorkflow.AgentId, instructions,
            structured ? client.CreateChatOptions(schema, DocumentAnalysisWorkflow.SchemaName, temperature: 0.1f, phase: AiPhase.Analysis) : client.CreateChatOptions(temperature: 0.1f, phase: AiPhase.Analysis),
            AiPhase.Analysis, measure.Profile.Fingerprint);

        var policyHash = DocumentAnalysisWorkflow.Sha("document-analysis-policy-v1");
        var planJson = new JsonObject
        {
            ["algorithm"] = DocumentAnalysisWorkflow.AlgorithmVersion,
            ["batches"] = new JsonArray(JsonNode.Parse(InputIdsJson())!),
            ["definition"] = DocumentAnalysisWorkflow.DefinitionVersion,
            ["graph"] = new JsonArray(DocumentBatchWorkflow.Stages.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        }.ToJsonString();
        Manifest = new WorkManifestInput(
            configHash, input.Snapshot.SourceHash, measure.Profile.Fingerprint, DocumentAnalysisWorkflow.DefinitionVersion, DocumentAnalysisWorkflow.AlgorithmVersion, policyHash, planJson);
        _manifestIdentity = CanonicalJson.Sha256Hex(JsonSerializer.Serialize(new
        {
            configHash, source = input.Snapshot.SourceHash, runtime = measure.Profile.Fingerprint, definition = DocumentAnalysisWorkflow.DefinitionVersion,
            algorithm = DocumentAnalysisWorkflow.AlgorithmVersion, policy = policyHash, plan = JsonDocument.Parse(planJson).RootElement,
        }));
    }

    /// <summary>Vero dopo il commit atomico del run da questo piano.</summary>
    public bool Committed { get; private set; }

    public WorkManifestInput Manifest { get; }

    public WorkLookupExpectation Expectation => new(DocumentAnalysisWorkflow.SchemaVersion, DocumentAnalysisWorkflow.ValidationVersion);

    public BatchAgentSpec Agent { get; }

    public bool HasBatch(int ordinal) => ordinal == 0;

    private string InputIdsJson() => JsonSerializer.Serialize(new { ordinal = 0, evidenceIds = _input.Evidence.Select(e => e.EvidenceId).ToArray() });

    public ValueTask<DerivedBatchInput> PrepareAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        if (ordinal != 0)
        {
            throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"L'analisi a singola chiamata ha un solo batch: ordinale {ordinal} non valido.");
        }

        var inputHash = _measure.PayloadHash;
        var batchId = DocumentAnalysisWorkflow.Sha(
            $"batch|ProcessingRun|{_claim.RunId:N}|{_manifestIdentity}|{DocumentAnalysisWorkflow.DefinitionVersion}|{DocumentAnalysisWorkflow.BatchStage}|{ordinal}|{inputHash}");
        var provenance = _measure.Quality == TokenCountQuality.Measured ? "measured" : "calibrated_upper_bound";
        return new(new DerivedBatchInput(
            batchId, DocumentAnalysisWorkflow.BatchStage, null, null, null, ordinal, [], InputIdsJson(), _measure.Tokens, provenance, inputHash,
            DocumentAnalysisWorkflow.SchemaVersion, DocumentAnalysisWorkflow.PromptVersion));
    }

    public ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, IBatchOutputs prior, CancellationToken ct) => new(_messages);

    /// <summary>
    /// Valida PRIMA del WorkOutput: Invalid → errore permanente (nessun output, nessuna riparazione); ValidIncomplete/Complete → output Committed
    /// riutilizzabile con l'esito calcolato dal server; la completezza non dipende mai da una dichiarazione del modello.
    /// </summary>
    public ValidatedBatch Validate(BatchRef batch, string responseText)
    {
        var validation = DocumentAnalysisValidator.Validate(responseText, _input.Snapshot, _input.Facts, _input.BatchKey, _input.InputHash);
        if (!validation.IsReusableWorkOutput)
        {
            throw new DocumentWorkException(
                validation.ErrorCode ?? DocumentWorkException.WorkOutputInvalid,
                $"Output dell'analisi documentale non valido ({validation.Violations.Count} violazioni): " + string.Join(" | ", validation.Violations.Take(5)));
        }

        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = DocumentAnalysisWorkflow.SchemaVersion, outcome = validation.Outcome.ToString(),
            gaps = validation.Completion!.Gaps.Select(g => g.Code + ": " + g.Detail).ToArray(), coverageHash = validation.Completion.CoverageHash,
            output = JsonDocument.Parse(ReconciliationCodec.Encode(validation.Output!)).RootElement,
        });
        var covered = JsonSerializer.Serialize(_input.Evidence.Select(e => e.EvidenceId.ToString("D")));
        var maps = JsonSerializer.Serialize(new { evidenceMap = validation.Output!.EvidenceMap.Select(m => new { itemId = m.ItemId.ToString("D"), evidenceIds = m.EvidenceIds.Select(g => g.ToString("D")) }) });
        var output = new WorkOutputInput(
            batch.BatchId, DocumentBatchWorkflow.InvokeId, batch.InputHash, CanonicalJson.Sha256Hex(payload), payload, null, null, covered, maps, null,
            DocumentAnalysisWorkflow.SchemaVersion, DocumentAnalysisWorkflow.ValidationVersion, null);
        return new ValidatedBatch(output);
    }

    private async ValueTask<(string Outcome, string[] Gaps, SingleCallOutput Output)> DecodeAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var output = await outputs.GetByOrdinalAsync(0, ct)
            ?? throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, "Assemble senza l'output dell'analisi: nessun risultato da committare.");
        using var document = JsonDocument.Parse(output.PayloadJson ?? throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Payload mancante."));
        var root = document.RootElement;
        var decoded = ReconciliationCodec.DecodeSingleCall(root.GetProperty("output").GetRawText());
        return (root.GetProperty("outcome").GetString()!, [.. root.GetProperty("gaps").EnumerateArray().Select(g => g.GetString()!)], decoded);
    }

    /// <summary>Assemble: un WorkOutput valido con obbligazioni residue non completa mai (no Completed parziale); i residui giustificano additional_work (I5).</summary>
    public async ValueTask AssembleAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var (outcome, gaps, _) = await DecodeAsync(outputs, ct);
        if (outcome != nameof(ReconciliationOutcome.Complete))
        {
            throw new DocumentWorkException(
                DocumentWorkException.CoverageIncomplete,
                $"Il batch è valido ma le obbligazioni di riconciliazione non sono complete ({gaps.Length}): " + string.Join(" | ", gaps.Take(5)));
        }
    }

    public async ValueTask<int> CommitAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var (outcome, _, output) = await DecodeAsync(outputs, ct);
        if (outcome != nameof(ReconciliationOutcome.Complete))
        {
            throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, "Commit rifiutato: l'output committato non è completo.");
        }

        // Il server ripete la validazione comune sul testo committato: nessun Completed basato sul solo esito persistito.
        var validation = DocumentAnalysisValidator.Validate(ReconciliationCodec.Encode(output), _input.Snapshot, _input.Facts, _input.BatchKey, _input.InputHash);
        if (validation.Outcome != ReconciliationOutcome.Complete)
        {
            throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, "Commit rifiutato: la validazione server dell'output committato non è completa.");
        }

        var relations = new List<RunRelation>();
        var accepted = validation.Patches!.Dispositions.Where(d => d.Kind is PatchDispositionKind.Applied or PatchDispositionKind.PreservedAsConflict).Select(d => d.PatchId).ToHashSet(StringComparer.Ordinal);
        foreach (var patch in output.Results.SelectMany(r => r.Patches).Where(p => accepted.Contains(p.PatchId)))
        {
            if (patch.Value is AddRelationValue r && !relations.Any(x => x.From == r.FromEvidenceId && x.To == r.ToEvidenceId && x.Type == r.Type))
            {
                relations.Add(new RunRelation(r.FromEvidenceId, r.ToEvidenceId, r.Type, r.Basis, r.Rationale));
            }
        }

        using var scope = _scopes.CreateScope();
        var completion = new AnalysisRunCompletion(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        var result = await completion.CommitDocumentAsync(_claim, output.Analysis, output.EvidenceMap, relations, ct);
        if (result.Outcome == AnalysisCommitOutcome.Rejected)
        {
            if (result.Reason == "lease_lost")
            {
                throw new LeaseLostException("Commit dell'analisi rifiutato (lease_lost): il lease non è più valido.");
            }

            throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"Commit dell'analisi documentale rifiutato ({result.Reason}).");
        }

        Committed = true;
        return 1;
    }
}
