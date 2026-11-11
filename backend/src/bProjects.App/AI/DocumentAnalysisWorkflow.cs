using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using bProjects.App.Retrieval;
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
/// <see cref="DocumentMeta"/> (D2, <c>message-format-2</c>) è una mappa LATERALE di documento/versione/sezione per le evidenze documentali: non entra in <see cref="Snapshot"/>, nel catalogo né in
/// alcun hash (<c>CatalogHash</c>, <c>RunIndependentHash</c>, <c>SourceHash</c>); è letta solo dal formato 2 del messaggio single-call.
/// </summary>
public sealed record DocumentAnalysisInput(
    Guid ProjectId, Guid AnalysisId, long Cutoff, int PmMessageCount, int ReadyDocumentSourceCount, int Unavailable, string SnapshotHash,
    ReconciliationSnapshot Snapshot, IReadOnlyList<DocumentAnalysisEvidence> Evidence, IReadOnlyList<AnalysisDialogMessage> Dialog,
    IReadOnlyDictionary<Guid, GapStageEvidenceMeta>? DocumentMeta = null)
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
            .Select(r => new SnapshotRelation(r.Id, r.FromEvidenceId, r.ToEvidenceId, r.Type, r.Basis)).ToList();

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

        var input = new DocumentAnalysisInput(
            run.ProjectId, run.Id, run.SourceCutoffSequence!.Value, messages.Count(m => m.Role == MessageRole.User),
            snapshotItems.Count(s => s.Included && s.Availability == SourceAvailability.Ready && s.EvidenceVersionId != null),
            snapshotItems.Count(s => s.Availability == SourceAvailability.Unavailable), snapshotHash,
            new ReconciliationSnapshot(run.ProjectId, sourceHash, [.. evidence.Select(e => new SnapshotEvidence(e.EvidenceId, e.VersionId, e.ContentHash, e.Sources))], snapshotRelations),
            evidence, dialog);

        // D2: metadati documentali (documento, versione, sezione) in una mappa laterale, mai nel catalogo né negli hash; solo lettura.
        return input with { DocumentMeta = await GapStageMetadata.LoadAsync(db, input, ct) };
    }

    /// <summary>Fatti strutturali delle evidenze della selezione congelata del run, per le sole chiavi di routing della directory (T072): mai l'enunciato.</summary>
    public static async Task<IReadOnlyDictionary<Guid, DirectoryFacts>> LoadDirectoryFactsAsync(AppDbContext db, Guid runId, CancellationToken ct)
    {
        var versionIds = await db.AnalysisEvidenceSelectionItems.AsNoTracking().Where(i => i.RunId == runId).Select(i => i.VersionId).ToListAsync(ct);
        var rows = await db.Evidences.AsNoTracking().Where(e => versionIds.Contains(e.VersionId)).ToListAsync(ct);
        var facts = new Dictionary<Guid, DirectoryFacts>();
        foreach (var row in rows)
        {
            try
            {
                if (row.OriginKind == EvidenceOriginKind.Document)
                {
                    var c = DocumentEvidenceCodec.DecodeContent(row.Content);
                    var s = c.Structure;
                    facts[row.Id] = new(new EvidenceStructure(s.Subject, s.Action, s.Object, s.Scope, s.Purpose, s.Instrument, s.Channel, s.Recipient), [.. c.Qualifiers.Select(q => q.Text)]);
                }
                else
                {
                    var c = EvidenceJson.Deserialize<EvidenceContent>(row.Content);
                    facts[row.Id] = new(c.Structure, [.. c.Qualifiers.Select(q => q.Text)]);
                }
            }
            catch (JsonException)
            {
                throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Il contenuto di un'evidenza dello snapshot non è decodificabile.");
            }
        }

        return facts;
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
        return Compute(output.Results.SelectMany(r => r.ItemProposals), snapshot);
    }

    /// <summary>Base del passaggio gerarchico (T076): solo le proposte dei batch foglia, note prima di ogni espansione o confronto, quindi immutabili per le patch successive.</summary>
    public static ReconciliationBase ComputeHierarchical(IEnumerable<BatchOutcome> outcomes, ReconciliationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        return Compute(outcomes.Where(o => o.Batch.Stage == "leaf").OrderBy(o => o.Batch.BatchId, StringComparer.Ordinal).SelectMany(o => o.Result.ItemProposals), snapshot);
    }

    public static ReconciliationBase Compute(IEnumerable<ItemProposal> items, ReconciliationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(snapshot);
        var targets = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
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
    public static ReconciliationValidation Validate(
        string responseText, ReconciliationSnapshot snapshot, DocumentAnalysisFacts facts, string batchKey, string inputHash, GapAssessmentContext? gapAssessments = null)
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
        var result = ReconciliationValidator.ValidateSingleCall(responseText, snapshot, baseState, facts, gapAssessments);
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
        var hash = ReconciliationCompletion.CoverageHash(completion.ExpectedEntryIds, completion.VisitedEntryIdsA, completion.VisitedEntryIdsB, completion.MissingEntryIds, all, gapAssessments);
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

    // v3 (T125, contracts/ai-workflow.md §7.8): contratto con alias; v2 sopra resta storico e immutato. La versione di un run è quella della
    // definizione congelata alla richiesta: mai un downgrade da v3 a v2 né il contrario.
    public const string DefinitionVersionV3 = "project-analysis-documents-v3";
    public const string PromptVersionV3 = "document-analysis-v3";
    public const string AlgorithmVersionV3 = AnalysisAliasComposer.AlgorithmVersion;
    public const string SchemaNameV3 = "document_analysis_alias";
    public const string SchemaVersionV3 = AnalysisAliasContracts.SchemaVersion;
    public const string ValidationVersionV3 = AnalysisAliasComposer.ValidationVersion;

    private static readonly Lazy<string> PromptV3 = new(() => ConversationalAgent.LoadPrompt("document-analysis-v3.md"));

    public static string InstructionsV3 => PromptV3.Value;

    // v5 (T136, contracts/ai-workflow.md §13.1): percorso a chiamata singola con valutazione dei Gap (prompt document-analysis-v4, schema document-analysis-alias-2, contract
    // alias-2, algoritmo analysis-single-v3, validazione document-analysis-validation-v3). La definizione è riconosciuta da ContractOf, instradata da AnalysisWorkflow e congelata dalle NUOVE richieste di Analizza da
    // SourceSnapshotStore (T141). Oltre una chiamata (T143, decisione C + D, §13.6): con almeno un Gap fail-fast
    // senza alcuna invocazione del modello, senza Gap la gerarchia v4 invariata con la sola definizione v5 nel manifest.
    public const string DefinitionVersionV5 = "project-analysis-documents-v5";

    // v6 (D1, AI §13.7): come la v5 per il single-call e la gerarchia v4, ma oltre una chiamata con almeno un Gap la valutazione e UNA chiamata dedicata su ledger completo (stadio Gap).
    // Le chiavi `gap*` sostituiscono `reconcileGapPolicy`; nessuna combinazione mista e riconosciuta.
    public const string DefinitionVersionV6 = "project-analysis-documents-v6";

    // v7 (003 T028, candidate-source.md §4): come la v6 ma con le cinque chiavi `retrieval*` e, oltre una chiamata con almeno un Gap il cui ledger NON entra nel budget dello stadio, il recupero al posto del fail-fast
    // `gap_ledger_exceeds_input` (che resta per la v6). Se il ledger entra, la sorgente resta l'intero ledger come in v6 (FR-002).
    public const string DefinitionVersionV7 = "project-analysis-documents-v7";

    // v8 (003, correzione T040 Run 2 / chiamata 026): come la v7 (stesse chiavi, stesso single-call, stessi stadi Gap e recupero) più la chiave `reconcileSchemaProfile`: nella gerarchia lo schema inviato
    // a ogni batch è la variante di stadio e vista (ReconcileAliasContracts.SchemaProfileByBatch). Stesso DTO reconcile-alias-1, stesso prompt, stesso compositore e validatore. La v7 resta storica e immutata.
    public const string DefinitionVersionV8 = "project-analysis-documents-v8";

    /// <summary>Chiave di freeze del profilo degli schemi della gerarchia (solo v8); la sua presenza in una v6/v7 è una combinazione mista non riconosciuta.</summary>
    public const string ReconcileSchemaProfileKey = "reconcileSchemaProfile";

    /// <summary>
    /// Chiave di freeze <c>reconcileGapPolicy</c> (T143, AI §13.6): marcatore di identità del run che dichiara che la v5 non supporta la valutazione Gap gerarchica.
    /// Vive solo in <c>ProcessingRun.PromptVersions</c> e in <see cref="ContractOf"/>: nessun effetto sul calcolo, né sul manifest né sul <c>configHash</c> del piano.
    /// </summary>
    public const string ReconcileGapPolicyV1 = "hierarchical-gap-incomplete-1";
    public const string PromptVersionV4 = "document-analysis-v4";

    /// <summary>Prompt delle NUOVE richieste v5 (procedura di composizione anti-duplicazione, 002 T142); v4 resta valido e invariato per i run già congelati.</summary>
    public const string PromptVersionV5 = "document-analysis-v5";
    public const string AlgorithmVersionV5 = AnalysisAliasComposer.AlgorithmVersionV3;
    public const string SchemaNameV5 = "document_analysis_alias_2";
    public const string SchemaVersionV5 = AnalysisAliasContracts.SchemaVersionV2;
    public const string ValidationVersionV5 = AnalysisAliasComposer.ValidationVersionV3;

    private static readonly Lazy<string> PromptV4 = new(() => ConversationalAgent.LoadPrompt("document-analysis-v4.md"));

    public static string InstructionsV4 => PromptV4.Value;

    private static readonly Lazy<string> PromptV5 = new(() => ConversationalAgent.LoadPrompt("document-analysis-v5.md"));

    public static string InstructionsV5 => PromptV5.Value;

    /// <summary>Prompt delle NUOVE richieste v5 dopo la seconda prova N4 (002 T142): domande che nominano un documento e duplicazione; v4 e v5 restano validi e invariati per i run già congelati.</summary>
    public const string PromptVersionV6 = "document-analysis-v6";

    private static readonly Lazy<string> PromptV6 = new(() => ConversationalAgent.LoadPrompt("document-analysis-v6.md"));

    public static string InstructionsV6 => PromptV6.Value;

    /// <summary>Chiave delle versioni del run che congela la policy di stima dell'output della chiamata singola (T082): assente nei run storici.</summary>
    public const string OutputPolicyKey = "analysisOutputPolicy";

    /// <summary>Stato della policy di instradamento sull'output congelata in un run: <c>Absent</c> = run storico (solo budget di input), <c>Current</c>, <c>Unsupported</c> = chiave presente con valore ignoto o non testuale (incompatibile).</summary>
    public enum OutputPolicyState
    {
        Absent,
        Current,
        Unsupported,
    }

    public static OutputPolicyState OutputPolicyOf(ProcessingRun run)
    {
        if (run.PromptVersions is null)
        {
            return OutputPolicyState.Absent;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(run.PromptVersions);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || !document.RootElement.TryGetProperty(OutputPolicyKey, out var value))
            {
                return OutputPolicyState.Absent;
            }

            return value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() == AnalysisOutputEstimate.PolicyVersion
                ? OutputPolicyState.Current
                : OutputPolicyState.Unsupported;
        }
        catch (System.Text.Json.JsonException)
        {
            return OutputPolicyState.Unsupported;
        }
    }
    /// <summary>Prompt congelato di un run v5: v4 (storico) o v5 (nuove richieste); le versioni ignote sono già escluse da <see cref="ContractOf"/>.</summary>
    public static string GapPromptOf(ProcessingRun run) =>
        run.PromptVersionMatches("analysis", PromptVersionV6) ? PromptVersionV6 : run.PromptVersionMatches("analysis", PromptVersionV5) ? PromptVersionV5 : PromptVersionV4;

    // v4 (T131, contracts/ai-workflow.md §11): percorso alias v3 se il payload entra in una chiamata, altrimenti riconciliazione gerarchica con alias nel motore condiviso
    // (prompt document-reconcile-v3, DTO reconcile-alias-1). Le nuove richieste di Analizza congelano la v4; v2 e v3 storici o in volo restano invariati.
    public const string DefinitionVersionV4 = "project-analysis-documents-v4";

    /// <summary>Contratto di analisi documentale di un run: v2 (storico, frozen), v3 (alias, solo singola chiamata), v4 (alias con gerarchia) o v5 (single-call con Gap + gerarchia v4 + policy Gap, T141) o v6 (come v5 ma con stadio Gap dedicato su ledger completo al posto della policy Gap, D1/T148); null = definizione, prompt o versioni non supportati o miste (workflow_incompatible).</summary>
    public static int? ContractOf(ProcessingRun run)
    {
        var contract = ContractOfCore(run);

        // D2 (AI §13.8): la chiave `analysisMessageFormat` esiste solo nella definizione v6 e solo con un valore supportato; in v2-v5 (o con valore ignoto) la combinazione è mista ⇒ workflow_incompatible,
        // mai reinterpretata. L'ASSENZA della chiave in una v6 è il formato 1 storico (richieste v6 congelate prima di D2): resta valida e invariata.
        // 003 T028: le chiavi `retrieval*` esistono solo nelle definizioni v7 e v8; in ogni altra definizione la combinazione è mista ⇒ workflow_incompatible, mai reinterpretata.
        if (contract is not (7 or 8) && RetrievalStage.Inspect(run.PromptVersions).AnyKey)
        {
            return null;
        }

        // La chiave `reconcileSchemaProfile` esiste solo nella v8 (riconosciuta da ContractOfCore); altrove la combinazione è mista.
        if (contract is not 8 && HasKey(run, ReconcileSchemaProfileKey))
        {
            return null;
        }

        return HasKey(run, AnalysisMessageFormatKey) && (contract is not (6 or 7 or 8) || MessageFormatOf(run) == MessageFormatState.Unsupported) ? null : contract;
    }

    /// <summary>Formato del messaggio single-call congelato nel run (D2): assente = 1 (storico), <c>message-format-2</c> = 2, altro valore = non supportato.</summary>
    public enum MessageFormatState
    {
        V1,
        V2,
        Unsupported,
    }

    public const string AnalysisMessageFormatKey = "analysisMessageFormat";

    /// <summary>Formato 2 del messaggio single-call (D2, AI §13.8): intestazione VOCE con documento, versione, sezione e certezza come testo-dato.</summary>
    public const string MessageFormatV2 = "message-format-2";

    public static MessageFormatState MessageFormatOf(ProcessingRun run)
    {
        if (!HasKey(run, AnalysisMessageFormatKey))
        {
            return MessageFormatState.V1;
        }

        using var document = System.Text.Json.JsonDocument.Parse(run.PromptVersions!);
        var value = document.RootElement.GetProperty(AnalysisMessageFormatKey);
        return value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() == MessageFormatV2 ? MessageFormatState.V2 : MessageFormatState.Unsupported;
    }

    private static int? ContractOfCore(ProcessingRun run)
    {
        if (run.MafVersion != Conversation.CurrentMafVersion)
        {
            return null;
        }

        if (run.WorkflowDefinitionVersion == DefinitionVersion && run.PromptVersionMatches("analysis", PromptVersion))
        {
            return 2;
        }

        // v3: ogni versione congelata deve coincidere con quella supportata (prompt, schema AI, catalogo, id canonici, algoritmo, validazione).
        var alias = run.PromptVersionMatches("analysis", PromptVersionV3)
            && run.PromptVersionMatches("analysisAiSchema", SchemaVersionV3) && run.PromptVersionMatches("aliasCatalog", AnalysisAliasCatalog.Version)
            && run.PromptVersionMatches("canonIds", AnalysisAliasCatalog.CanonIdsVersion) && run.PromptVersionMatches("algorithm", AlgorithmVersionV3)
            && run.PromptVersionMatches("validation", ValidationVersionV3);
        if (run.WorkflowDefinitionVersion == DefinitionVersionV3 && alias)
        {
            return 3;
        }

        // v5 (T136 + T143): ogni versione del single-call con valutazione dei Gap deve coincidere E ogni versione della gerarchia v4 più la policy Gap temporanea (C + D, §13.6).
        // Chiavi gerarchiche mancanti o diverse (es. document-reconcile-v4, reconcile-alias-2, global-reconcile-v2 della futura gerarchia nominale) ⇒ workflow_incompatible, mai reinterpretate.
        // Anche la versione dello schema del canonico (AnalysisContent 1.1.0, chiave `analysisSchema`) è una chiave di freeze della v5 (T141: «TUTTE le chiavi», AI §13.1): ignota ⇒ incompatibile.
        if (run.WorkflowDefinitionVersion == DefinitionVersionV5 && (run.PromptVersionMatches("analysis", PromptVersionV4) || run.PromptVersionMatches("analysis", PromptVersionV5) || run.PromptVersionMatches("analysis", PromptVersionV6))
            && run.PromptVersionMatches("analysisSchema", DocumentAnalysisContent.SchemaVersionValue)
            && run.PromptVersionMatches("analysisAiSchema", SchemaVersionV5) && run.PromptVersionMatches("aliasCatalog", AnalysisAliasCatalog.Version)
            && run.PromptVersionMatches("canonIds", AnalysisAliasCatalog.CanonIdsVersion) && run.PromptVersionMatches("algorithm", AlgorithmVersionV5)
            && run.PromptVersionMatches("validation", ValidationVersionV5) && HierarchyMatches(run)
            && run.PromptVersionMatches("reconcileGapPolicy", ReconcileGapPolicyV1) && OutputPolicyOf(run) != OutputPolicyState.Unsupported
            && !GapKeys.Any(k => HasKey(run, k)))
        {
            return 5;
        }

        // v6 (D1, AI §13.7): le chiavi del single-call v6 (prompt document-analysis-v6, schema AI alias-2, policy di stima dell'output congelata) e della gerarchia v4, tutte le chiavi `gap*` dello stadio e NESSUNA
        // `reconcileGapPolicy` (sostituita). Chiavi mancanti, diverse o miste con quelle del v5 ⇒ workflow_incompatible, mai reinterpretate. Una v6 senza versioni congelate non e confrontabile.
        if (run.WorkflowDefinitionVersion == DefinitionVersionV6 && StagedKeysMatch(run) && !HasKey(run, ReconcileSchemaProfileKey))
        {
            return 6;
        }

        // v7 (003 T028): le stesse chiavi della v6 più TUTTE le chiavi `retrieval*` con valori supportati (insieme chiuso). Una chiave `retrieval*` mancante o ignota ⇒ workflow_incompatible.
        if (run.WorkflowDefinitionVersion == DefinitionVersionV7 && StagedKeysMatch(run) && RetrievalStage.Inspect(run.PromptVersions).AllSupported && !HasKey(run, ReconcileSchemaProfileKey))
        {
            return 7;
        }

        // v8: le chiavi della v7 più `reconcileSchemaProfile` con il solo valore supportato. Mancante o diversa ⇒ workflow_incompatible, mai reinterpretata come v7.
        if (run.WorkflowDefinitionVersion == DefinitionVersionV8 && StagedKeysMatch(run) && RetrievalStage.Inspect(run.PromptVersions).AllSupported
            && run.PromptVersionMatches(ReconcileSchemaProfileKey, ReconcileAliasContracts.SchemaProfileByBatch))
        {
            return 8;
        }

        // v4: le versioni del percorso alias più quelle della riconciliazione gerarchica con alias (prompt, schema AI, algoritmo del motore, catalogo del batch).
        return run.WorkflowDefinitionVersion == DefinitionVersionV4 && alias && HierarchyMatches(run) ? 4 : null;
    }

    private static readonly string[] GapKeys = [.. GapStage.FreezeKeys.Keys];

    /// <summary>Chiavi comuni a v6 e v7: single-call v6, gerarchia v4, politica di stima dell'output, tutte le chiavi `gap*` e NESSUNA `reconcileGapPolicy`. Una definizione senza versioni congelate non è confrontabile.</summary>
    private static bool StagedKeysMatch(ProcessingRun run) =>
        run.PromptVersions is not null && run.PromptVersionMatches("analysis", PromptVersionV6)
        && run.PromptVersionMatches("analysisSchema", DocumentAnalysisContent.SchemaVersionValue)
        && run.PromptVersionMatches("analysisAiSchema", SchemaVersionV5) && run.PromptVersionMatches("aliasCatalog", AnalysisAliasCatalog.Version)
        && run.PromptVersionMatches("canonIds", AnalysisAliasCatalog.CanonIdsVersion) && run.PromptVersionMatches("algorithm", AlgorithmVersionV5)
        && run.PromptVersionMatches("validation", ValidationVersionV5) && HierarchyMatches(run) && OutputPolicyOf(run) == OutputPolicyState.Current
        && GapStage.FreezeKeys.All(kv => HasKey(run, kv.Key) && run.PromptVersionMatches(kv.Key, kv.Value)) && !HasKey(run, "reconcileGapPolicy");

    /// <summary>La chiave e presente fra le versioni congelate del run (qualunque valore): serve a riconoscere le combinazioni miste fra v5 e v6.</summary>
    private static bool HasKey(ProcessingRun run, string key)
    {
        if (run.PromptVersions is null)
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(run.PromptVersions);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && document.RootElement.TryGetProperty(key, out _);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>Versioni della riconciliazione gerarchica con alias (prompt, schema AI, algoritmo del motore, catalogo del batch): comuni a v4, v5 e v6.</summary>
    private static bool HierarchyMatches(ProcessingRun run) =>
        run.PromptVersionMatches("reconcile", ReconcileAliasContracts.PromptVersion) && run.PromptVersionMatches("reconcileAiSchema", ReconcileAliasContracts.SchemaVersion)
        && run.PromptVersionMatches("reconcileAlgorithm", DocumentReconcilePlan.AlgorithmVersion) && run.PromptVersionMatches("reconcileCatalog", ReconcileAliasContracts.CatalogVersion);

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

        // Limiti del run: unica autorità lo snapshot persistito alla richiesta (riletto a ogni esecuzione, claim, retry o recovery), mai la policy corrente.
        // Mancante, corrotto, con hash incoerente o schema non supportato: config_invalid prima di ogni selezione, misura o invio; nessun ripiego.
        DocumentLimits limits;
        try
        {
            limits = RunLimitsSnapshot.Read(run.DocumentLimitsSnapshot);
        }
        catch (DocumentWorkException invalid)
        {
            await FailAsync(claim, invalid.Code, invalid.Message, ct);
            return await ReadAsync(claim.RunId, ct);
        }

        if (ContractOf(run) is not { } contract)
        {
            // Nessuna migrazione implicita né downgrade (v3 → v2) di un altro grafo: errore esplicito e permanente (T123 instrada le definizioni supportate).
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
            await (contract >= 3 ? RunAliasGraphAsync(db, claim, contract, limits, contract >= 5 ? GapPromptOf(run) : PromptVersionV3, contract >= 5 && OutputPolicyOf(run) == OutputPolicyState.Current, contract is 6 or 7 or 8 && MessageFormatOf(run) == MessageFormatState.V2 ? 2 : 1, ct) : RunGraphAsync(db, claim, limits, ct));
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

    /// <summary>
    /// Schema e profilo di runtime dell'analisi per il preflight di classificazione del regime (T124). Il payload NON è determinabile prima del claim:
    /// la selezione delle evidenze (messaggi PM e documenti) si congela sotto lease in <c>FreezeDocumentSelectionAsync</c> e dipende dal completamento
    /// delle estrazioni dei messaggi PM, quindi il conteggio T è esplicitamente <c>unavailable</c> (regola 6: Extended workload_unknown).
    /// </summary>
    public RegimePayload BuildRegimePayload() => new(AiPhase.Analysis, null, WrapperSchema.GetRawText(), factory.Preflight);

    /// <summary>
    /// Come <see cref="BuildRegimePayload()"/> ma per la definizione congelata sul root: per v3 e v4 lo schema misurato è quello del DTO con alias
    /// (<c>document-analysis-alias-1</c>), non l'involucro v2 (il carico resta non noto prima del claim: Extended). Una definizione diversa resta sulla misura v2
    /// (l'esito incompatibile è del claim).
    /// </summary>
    public RegimePayload BuildRegimePayload(string? definitionVersion) =>
        definitionVersion switch
        {
            DefinitionVersionV3 or DefinitionVersionV4 => new(AiPhase.Analysis, null, AnalysisAliasContracts.JsonSchema().GetRawText(), factory.Preflight),
            DefinitionVersionV5 or DefinitionVersionV6 or DefinitionVersionV7 or DefinitionVersionV8 => new(AiPhase.Analysis, null, AnalysisAliasContracts.JsonSchemaV2().GetRawText(), factory.Preflight),   // v5 (T136)–v8: schema del DTO alias-2
            _ => BuildRegimePayload(),
        };

    private async Task RunGraphAsync(AppDbContext db, AnalysisRunClaim claim, DocumentLimits limits, CancellationToken ct)
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
            // Oltre la capacità di una chiamata (T078): riconciliazione gerarchica globale sullo stesso grafo fisso, un batch per invio e una sessione nuova per batch.
            await RunReconcileGraphAsync(db, claim, input, limits, ct);
            return;
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

    private async Task RunReconcileGraphAsync(AppDbContext db, AnalysisRunClaim claim, DocumentAnalysisInput input, DocumentLimits frozenLimits, CancellationToken ct, AnalysisAliasCatalog? aliasCatalog = null, string definition = DefinitionVersionV4, GapStageMode? gapStage = null)
    {
        var profile = await factory.Preflight.ResolveProfileAsync(AiPhase.Analysis, ct);
        var facts = await DocumentAnalysisSources.LoadDirectoryFactsAsync(db, claim.RunId, ct);
        using var scope = scopes.CreateScope();
        var planDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var writer = new CheckpointWriter(claim.RunId, claim.LeaseOwner, claim.FencingToken);
        var store = new DocumentWorkStore(planDb, writer);
        var policy = await db.ProcessingRuns.AsNoTracking().Where(r => r.Id == claim.RunId).Select(r => r.PolicyHash).SingleAsync(ct);
        // v4 (T131): adattatore alias e tetto di chiamate dallo snapshot dei limiti del run (frozenLimits); v2 conserva il tetto di chiamate storico (opzioni correnti),
        // ma i tetti di livelli, candidati e byte derivati sono sempre quelli dello snapshot.
        var alias = aliasCatalog is null ? null : new ReconcileAliasMode(
            aliasCatalog, factory.Options.StructuredOutputMode == StructuredOutputMode.JsonSchema, ReconcileAliasContracts.JsonSchema(), definition,
            SchemaByBatch: definition == DefinitionVersionV8);
        var plan = new DocumentReconcilePlan(claim, input, facts, factory.Preflight, profile, factory.Options, client, scopes, policy, frozenLimits, alias, gapStage);
        var maxCalls = alias is null ? documents.Value.MaxAiCallsPerRun : frozenLimits.MaxAiCallsPerRun;
        var port = new DocumentBatchPort(store, planDb, writer, factory, plan, new RunCallReservation(planDb, writer, maxCalls));
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

    /// <summary>
    /// Grafo v3/v4 (alias): stesso grafo fisso T043 e stesso port T044 della v2, con messaggio a soli alias, schema del DTO <c>document-analysis-alias-1</c>
    /// e piano che compone il canonico sul server. Il catalogo si costruisce dallo snapshot congelato del run (mai dalla selezione corrente); una fonte
    /// non verificabile è <c>source_snapshot_invalid</c> prima di ogni chiamata. Se il payload non entra in una chiamata: v3 fallisce come sempre (storico), v4 passa
    /// alla riconciliazione gerarchica con alias (T131) con i tetti della policy congelata; v5 (T143, §13.6): con almeno un Gap fail-fast senza chiamate (<c>document_limit_exceeded</c>,
    /// motivo <c>hierarchical_gap_assessment_unsupported</c>), senza Gap gerarchia come v4 con la definizione v5.
    /// </summary>
    private async Task RunAliasGraphAsync(AppDbContext db, AnalysisRunClaim claim, int contract, DocumentLimits limits, string promptVersion, bool outputPolicy, int messageFormat, CancellationToken ct)
    {
        var input = await DocumentAnalysisSources.LoadAsync(db, claim.RunId, ct);
        // v5 (T136): catalogo con il contratto alias-2 nell'identità, prompt document-analysis-v4 e schema alias-2; v3/v4 invariati.
        var gaps = contract >= 5;
        var catalog = AnalysisAliasCatalog.Build(input, gaps ? SchemaVersionV5 : SchemaVersionV3);
        var structured = factory.Options.StructuredOutputMode == StructuredOutputMode.JsonSchema;
        var instructions = !gaps ? InstructionsV3 : promptVersion == PromptVersionV6 ? InstructionsV6 : promptVersion == PromptVersionV5 ? InstructionsV5 : InstructionsV4;
        var schema = gaps ? AnalysisAliasContracts.JsonSchemaV2() : AnalysisAliasContracts.JsonSchema();
        var messages = DocumentAnalysisAliasPlan.BuildMessages(input, catalog, messageFormat);
        var measure = await factory.Preflight.MeasureAsync(
            new DocumentPayload(AiPhase.Analysis, instructions, messages, structured ? schema.GetRawText() : null), ct);
        if (measure.Quality == TokenCountQuality.Unavailable)
        {
            throw new RuntimeUnverifiedException("conteggio del payload dell'analisi non disponibile (nessun tokenizer o calibrazione identificata)");
        }

        // T082: oltre al budget di input, la risposta attesa deve entrare nel tetto di output O del profilo (stima prudente e versionata, congelata nel run). Se non entra o non
        // è stimabile con affidabilità, la chiamata singola non è sicura: stesso ramo del payload oltre contesto (gerarchia senza Gap; con Gap fail-fast C + D, mai un invio che può troncare).
        var outputExceeded = false;
        long outputEstimate = 0;
        if (outputPolicy && measure.Fits)
        {
            var statements = input.Evidence.ToDictionary(x => x.EvidenceId, x => x.Statement.Length);
            var nonGapLengths = catalog.Data.Evidence.Where(x => x.Kind != EvidenceKind.Gap).Select(x => statements[x.EvidenceId]).ToList();
            var gapEvidence = catalog.Data.Evidence.Count(x => x.Kind == EvidenceKind.Gap);
            outputEstimate = AnalysisOutputEstimate.Estimate(nonGapLengths, gapEvidence, catalog.Data.Relations.Count);
            outputExceeded = AnalysisOutputEstimate.Exceeds(nonGapLengths, gapEvidence, catalog.Data.Relations.Count, measure.Profile.OutputTokens);
        }

        if (!measure.Fits || outputExceeded)
        {
            var limitText = outputExceeded
                ? $"hanno una risposta attesa di circa {outputEstimate} token (stima prudente {AnalysisOutputEstimate.PolicyVersion}) che supera il budget di output ({measure.Profile.OutputTokens} token)"
                : $"superano il budget di input dell'analisi a singola chiamata ({measure.InputBudgetTokens} token)";
            if (contract == 4)
            {
                // v4 (T131): oltre una chiamata, riconciliazione gerarchica con alias nello stesso motore (nessuna seconda pipeline).
                await RunReconcileGraphAsync(db, claim, input, limits, ct, catalog);
                return;
            }

            if (contract is 6 or 7 or 8)
            {
                await RunStagedAsync(db, claim, input, catalog, limits, structured, contract switch { 8 => DefinitionVersionV8, 7 => DefinitionVersionV7, _ => DefinitionVersionV6 }, ct);
                return;
            }

            if (gaps)
            {
                // v5 (T143, decisione C + D, AI §13.6). Con almeno un Gap la valutazione gerarchica non è supportata: fail-fast PRIMA di piano, port e riserva di chiamate
                // (zero invocazioni LLM, nessun WorkOutput/batch/checkpoint, CallCount invariato), deterministico su snapshot congelato e misura.
                var gapCount = catalog.Data.Evidence.Count(e => e.Kind == EvidenceKind.Gap);
                if (gapCount > 0)
                {
                    throw new DocumentWorkException(
                        DocumentWorkException.CallLimitExceeded,
                        $"{DocumentWorkException.HierarchicalGapAssessmentUnsupported}: le fonti selezionate ({measure.Tokens} token, qualità della misura: {measure.Quality}) {limitText} "
                        + $"e contengono {gapCount} evidenze di tipo Gap: la valutazione dei Gap oltre il contesto non è ancora supportata "
                        + "e nessuna elaborazione è stata eseguita (nessuna chiamata al modello). Riduci la selezione delle fonti e riprova.");
                }

                // Senza Gap: gerarchia come v4 (stesso motore, prompt, DTO reconcile-alias-1 e algoritmo), con la sola definizione v5 nel manifest. Il catalogo del batch ha identità alias-1
                // come in v4 (il DTO è reconcile-alias-1): distinto da quello alias-2 usato per misurare il single-call.
                await RunReconcileGraphAsync(db, claim, input, limits, ct, AnalysisAliasCatalog.Build(input, SchemaVersionV3), DefinitionVersionV5);
                return;
            }

            throw new DocumentWorkException(
                DocumentWorkException.CallLimitExceeded,
                $"Le fonti selezionate ({measure.Tokens} token) superano la capacità dell'analisi a singola chiamata ({measure.InputBudgetTokens} token): la riconciliazione di più unità non è ancora disponibile.");
        }

        using var scope = scopes.CreateScope();
        var planDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var writer = new CheckpointWriter(claim.RunId, claim.LeaseOwner, claim.FencingToken);
        var store = new DocumentWorkStore(planDb, writer);
        var plan = new DocumentAnalysisAliasPlan(
            claim, input, catalog, messages, instructions, measure, structured, factory, client, schema, scopes,
            contract switch { 8 => DefinitionVersionV8, 7 => DefinitionVersionV7, 6 => DefinitionVersionV6, 5 => DefinitionVersionV5, 4 => DefinitionVersionV4, _ => DefinitionVersionV3 }, promptVersion);
        var maxCalls = contract >= 4 ? limits.MaxAiCallsPerRun : documents.Value.MaxAiCallsPerRun;
        var port = new DocumentBatchPort(store, planDb, writer, factory, plan, new RunCallReservation(planDb, writer, maxCalls));
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

    /// <summary>
    /// Instradamento v6 oltre una chiamata (D1, AI §13.7), deciso a runtime da snapshot congelato e misura. (b2) senza Gap: gerarchia v4-equivalente, nessuno stadio. (b1) con almeno un Gap: preflight dello stadio
    /// (controllo 1, ledger intero + maggiorazione prudente dei riferimenti) PRIMA di piano, port e riserva: oltre budget di input o di output ⇒ fail-fast tipizzato a zero invii in tutto il run (<c>document_limit_exceeded</c>,
    /// <c>gap_ledger_exceeds_input</c> / <c>gap_assessment_exceeds_output</c>). Altrimenti gerarchia v4-equivalente più UN batch dello stadio Gap (nuovo valore di Stage, stesso grafo e stesso port).
    /// </summary>
    private async Task RunStagedAsync(AppDbContext db, AnalysisRunClaim claim, DocumentAnalysisInput input, AnalysisAliasCatalog catalog, DocumentLimits limits, bool structured, string definition, CancellationToken ct)
    {
        var gapCount = catalog.Data.Evidence.Count(e => e.Kind == EvidenceKind.Gap);
        var hierarchyCatalog = AnalysisAliasCatalog.Build(input, SchemaVersionV3);   // identità alias-1 come in v4: il DTO del batch è reconcile-alias-1
        var retrieval = definition is DefinitionVersionV7 or DefinitionVersionV8;   // v8 = v7 + profilo degli schemi per batch: stesso recupero
        if (gapCount == 0)
        {
            await RunReconcileGraphAsync(db, claim, input, limits, ct, hierarchyCatalog, definition);
            return;
        }

        var meta = await GapStageMetadata.LoadAsync(db, input, ct);
        var stageMessages = GapStageBuilder.Message(GapStageBuilder.Build(input, catalog, meta, null));
        var measure = await factory.Preflight.MeasureAsync(
            new DocumentPayload(AiPhase.Analysis, GapStage.Instructions, stageMessages, structured ? GapStageContracts.JsonSchema().GetRawText() : null), ct);
        if (measure.Quality == TokenCountQuality.Unavailable)
        {
            throw new RuntimeUnverifiedException("conteggio del payload dello stadio Gap non disponibile (nessun tokenizer o calibrazione identificata)");
        }

        RetrievalMode? retrievalMode = null;
        if (GapStageBudget.Evaluate(measure, gapCount, routing: true) is { Fits: false } verdict)
        {
            // v6: fail-fast invariato. v7 (b1-retrieval, 003 T028): SOLO il ledger oltre il budget di input passa al recupero; `gap_assessment_exceeds_output` resta un fail-fast (FR-002, FR-024) e il
            // ledger che entra nel budget resta sulla sorgente esaustiva come in v6.
            if (!retrieval || verdict.Reason != DocumentWorkException.GapLedgerExceedsInput)
            {
                throw verdict.ToException();
            }

            if (GapStageBudget.EvaluateOutput(measure, gapCount) is { Fits: false } outputVerdict)
            {
                throw outputVerdict.ToException();
            }

            retrievalMode = new RetrievalMode(await FrozenRetrievalPolicyAsync(db, claim.RunId, ct));
        }

        await RunReconcileGraphAsync(db, claim, input, limits, ct, hierarchyCatalog, definition, new GapStageMode(meta, retrievalMode));
    }

    /// <summary>Politica di recupero congelata nel run v7 (<c>retrievalPolicy</c>): insieme chiuso già verificato da <see cref="ContractOf"/>; una versione ignota qui è un'incompatibilità, mai un valore di ripiego.</summary>
    private static async Task<RetrievalPolicy> FrozenRetrievalPolicyAsync(AppDbContext db, Guid runId, CancellationToken ct)
    {
        var frozen = await db.ProcessingRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.PromptVersions).SingleAsync(ct);
        using var document = JsonDocument.Parse(frozen ?? "{}");
        var version = document.RootElement.TryGetProperty(RetrievalStage.PolicyKey, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return RetrievalPolicy.ByVersion(version)
            ?? throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "La politica di recupero congelata nel run non è supportata.");
    }

    /// <summary>Messaggio unico del batch: dati del server, dialogo come contesto e tutte le evidenze integrali con le loro fonti. A capo sempre «\n».</summary>
    internal static IReadOnlyList<ChatMessage> BuildMessages(DocumentAnalysisInput input)
    {
        var text = new StringBuilder();
        text.Append("Dati affidabili del server:\n")
            .Append($"analysisId: {input.AnalysisId:D}\nprojectId: {input.ProjectId:D}\nsourceCutoffSequence: {input.Cutoff}\nbatchId: {input.BatchKey}\ninputHash: {input.InputHash}\n");
        // T067 (prova reale): il modello non sa ordinare e ricopiare in modo affidabile N hash da 64 caratteri sparsi nelle voci (ha omesso voci e poi ripetuto
        // l'ultimo hash fino a esaurire max_tokens). Il server fornisce l'elenco gia ordinato, con il numero di elementi, da copiare identico: stessa regola del contratto.
        var visited = input.Evidence.Select(e => ReconciliationIds.EntryId(input.Snapshot.SourceHash, e.EvidenceId, e.ContentHash)).Order(StringComparer.Ordinal).ToList();
        text.Append($"visitedEntryIds ({visited.Count} elementi, gia in ordine alfabetico: copiali identici e per intero in ciascuno dei tre results, senza ripetizioni): [{string.Join(",", visited.Select(id => "\"" + id + "\""))}]\n");
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

        // T064: solo le voci di origine documentale sono «fonti documentali»; le voci da messaggi PM, il dialogo e i dati del server restano nella categoria del messaggio.
        var documentSpans = new List<int>();
        for (var i = 0; i < input.Evidence.Count; i++)
        {
            var e = input.Evidence[i];
            var snapshot = input.Snapshot.Evidence[i];
            var entryStart = text.Length;
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
            if (e.Origin == EvidenceOriginKind.Document)
            {
                documentSpans.AddRange([entryStart, text.Length - entryStart]);
            }
        }

        text.Append("\nRispondi ora con l'unico oggetto JSON {\"results\":...,\"analysis\":...,\"evidenceMap\":...}.");
        var payload = new ChatMessage(ChatRole.User, text.ToString());
        if (documentSpans.Count > 0)
        {
            payload.AdditionalProperties = new() { [ContextComposition.DocumentSpansProperty] = documentSpans.ToArray() };
        }

        return [payload];
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
