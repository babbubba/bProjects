using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>
/// Parte di dominio del grafo batch per l'analisi documentale v3 con alias (<see cref="IDocumentBatchPlan"/>, contracts/ai-workflow.md §7): un solo
/// batch il cui messaggio espone soli alias, validazione e composizione canonica sul server PRIMA del WorkOutput, payload
/// <c>{aiOutputHash, catalogHash, canonicalHash}</c> con DTO e catalogo, ricomposizione a ogni lettura e a ogni commit (mai una reinferenza) con
/// verifica del <c>canonicalHash</c>. Il manifesto congela l'hash del catalogo senza id del run (<see cref="AnalysisAliasCatalog.RunIndependentHash"/>):
/// un root di retry sugli stessi fonti ha un manifesto con lo stesso contenuto e il DTO è importabile, mentre il <c>catalogHash</c> con l'id del run
/// resta nel WorkOutput del singolo root. Deterministico nel replay: nessun Guid né orologio nell'identità dei batch.
/// </summary>
internal sealed class DocumentAnalysisAliasPlan : IDocumentBatchPlan
{
    private readonly AnalysisRunClaim _claim;
    private readonly DocumentAnalysisInput _input;
    private readonly AnalysisAliasCatalog _catalog;
    private readonly IReadOnlyList<ChatMessage> _messages;
    private readonly PayloadPreflight _measure;
    private readonly IServiceScopeFactory _scopes;
    private readonly string _manifestIdentity;

    public DocumentAnalysisAliasPlan(
        AnalysisRunClaim claim, DocumentAnalysisInput input, AnalysisAliasCatalog catalog, IReadOnlyList<ChatMessage> messages, string instructions,
        PayloadPreflight measure, bool structured, AgentFactory factory, InternalModelClient client, JsonElement schema, IServiceScopeFactory scopes)
    {
        _claim = claim;
        _input = input;
        _catalog = catalog;
        _messages = messages;
        _measure = measure;
        _scopes = scopes;

        var settings = factory.Options.ForPhase(AiPhase.Analysis);
        var configHash = DocumentAnalysisWorkflow.Sha(JsonSerializer.Serialize(new
        {
            prompt = DocumentAnalysisWorkflow.PromptVersionV3, instructions = DocumentAnalysisWorkflow.Sha(instructions), schema = DocumentAnalysisWorkflow.SchemaVersionV3,
            catalog = AnalysisAliasCatalog.Version, canonIds = AnalysisAliasCatalog.CanonIdsVersion, validation = DocumentAnalysisWorkflow.ValidationVersionV3, structured,
            model = settings.ModelId, maxOutput = settings.MaxOutputTokens, thinking = settings.EnableThinking, inputBudget = measure.InputBudgetTokens,
        }));
        Agent = new BatchAgentSpec(
            "DocumentAnalysis", DocumentAnalysisWorkflow.AgentId, instructions,
            structured ? client.CreateChatOptions(schema, DocumentAnalysisWorkflow.SchemaNameV3, temperature: 0.1f, phase: AiPhase.Analysis) : client.CreateChatOptions(temperature: 0.1f, phase: AiPhase.Analysis),
            AiPhase.Analysis, measure.Profile.Fingerprint);

        var policyHash = DocumentAnalysisWorkflow.Sha("document-analysis-policy-v1");
        var planJson = new JsonObject
        {
            ["algorithm"] = DocumentAnalysisWorkflow.AlgorithmVersionV3,
            ["batches"] = new JsonArray(JsonNode.Parse(InputIdsJson())!),
            ["catalog"] = catalog.RunIndependentHash,
            ["definition"] = DocumentAnalysisWorkflow.DefinitionVersionV3,
            ["graph"] = new JsonArray(DocumentBatchWorkflow.Stages.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        }.ToJsonString();
        Manifest = new WorkManifestInput(
            configHash, input.Snapshot.SourceHash, measure.Profile.Fingerprint, DocumentAnalysisWorkflow.DefinitionVersionV3, DocumentAnalysisWorkflow.AlgorithmVersionV3, policyHash, planJson);
        _manifestIdentity = CanonicalJson.Sha256Hex(JsonSerializer.Serialize(new
        {
            configHash, source = input.Snapshot.SourceHash, runtime = measure.Profile.Fingerprint, definition = DocumentAnalysisWorkflow.DefinitionVersionV3,
            algorithm = DocumentAnalysisWorkflow.AlgorithmVersionV3, policy = policyHash, plan = JsonDocument.Parse(planJson).RootElement,
        }));
    }

    /// <summary>Vero dopo il commit atomico del run da questo piano.</summary>
    public bool Committed { get; private set; }

    public WorkManifestInput Manifest { get; }

    public WorkLookupExpectation Expectation => new(DocumentAnalysisWorkflow.SchemaVersionV3, DocumentAnalysisWorkflow.ValidationVersionV3);

    public BatchAgentSpec Agent { get; }

    public bool HasBatch(int ordinal) => ordinal == 0;

    private string InputIdsJson() => JsonSerializer.Serialize(new { ordinal = 0, evidenceIds = _input.Evidence.Select(e => e.EvidenceId).ToArray() });

    /// <summary>
    /// Messaggio unico del batch con soli alias (§7.3): dialogo come contesto, evidenze <c>[E&lt;n&gt;]</c> con enunciato e fonti <c>[S&lt;n&gt;]</c> con la
    /// citazione letterale, relazioni note <c>[R&lt;n&gt;]</c>. Mai UUID, hash, entryId, batchId, inputHash, id di progetto/analisi, offset, blocchi, cutoff
    /// o conteggi. A capo sempre «\n».
    /// </summary>
    internal static IReadOnlyList<ChatMessage> BuildMessages(DocumentAnalysisInput input, AnalysisAliasCatalog catalog)
    {
        var text = new StringBuilder();
        if (input.Unavailable > 0)
        {
            text.Append("Alcune fonti documentali non disponibili o non pronte sono escluse da questa analisi.\n\n");
        }

        text.Append("Dialogo con il project manager (contesto):\n");
        if (input.Dialog.Count == 0)
        {
            text.Append("(nessun messaggio)\n");
        }

        foreach (var message in input.Dialog)
        {
            text.Append($"[messaggio ruolo={(message.Role == AnalysisMessageRole.User ? "user" : "assistant")}]\n").Append(message.Body).Append('\n');
        }

        text.Append("\nEvidenze congelate:\n");
        if (catalog.Data.Evidence.Count == 0)
        {
            text.Append("(nessuna evidenza disponibile)\n");
        }

        var byId = input.Evidence.ToDictionary(e => e.EvidenceId);
        var documentSpans = new List<int>();
        foreach (var entry in catalog.Data.Evidence)
        {
            var entryStart = text.Length;
            text.Append($"<<<VOCE [{entry.Alias}] origine={entry.Origin} tipo={entry.Kind} polarita={entry.Polarity}>>>\n")
                .Append(byId[entry.EvidenceId].Statement).Append('\n');
            foreach (var link in entry.Sources)
            {
                text.Append($"<<<FONTE [{link.SourceAlias}] ruolo={link.Role}>>>\n").Append(catalog.Source(link.SourceAlias)!.Quote).Append("\n<<<FINE FONTE>>>\n");
            }

            text.Append("<<<FINE VOCE>>>\n");
            if (entry.Origin == EvidenceOriginKind.Document)
            {
                documentSpans.AddRange([entryStart, text.Length - entryStart]);
            }
        }

        if (catalog.Data.Relations.Count > 0)
        {
            text.Append("\nRelazioni note fra evidenze:\n");
            foreach (var relation in catalog.Data.Relations)
            {
                text.Append($"[{relation.Alias}] {relation.FromAlias} {relation.Type} {relation.ToAlias} (base={relation.Basis})\n");
            }
        }

        text.Append("\nRispondi ora con l'unico oggetto JSON `alias-1`.");
        var payload = new ChatMessage(ChatRole.User, text.ToString());
        if (documentSpans.Count > 0)
        {
            payload.AdditionalProperties = new() { [ContextComposition.DocumentSpansProperty] = documentSpans.ToArray() };
        }

        return [payload];
    }

    public ValueTask<DerivedBatchInput> PrepareAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        if (ordinal != 0)
        {
            throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"L'analisi a singola chiamata ha un solo batch: ordinale {ordinal} non valido.");
        }

        var inputHash = _measure.PayloadHash;
        var batchId = DocumentAnalysisWorkflow.Sha(
            $"batch|ProcessingRun|{_claim.RunId:N}|{_manifestIdentity}|{DocumentAnalysisWorkflow.DefinitionVersionV3}|{DocumentAnalysisWorkflow.BatchStage}|{ordinal}|{inputHash}");
        var provenance = _measure.Quality == TokenCountQuality.Measured ? "measured" : "calibrated_upper_bound";
        return new(new DerivedBatchInput(
            batchId, DocumentAnalysisWorkflow.BatchStage, null, null, null, ordinal, [], InputIdsJson(), _measure.Tokens, provenance, inputHash,
            DocumentAnalysisWorkflow.SchemaVersionV3, DocumentAnalysisWorkflow.PromptVersionV3));
    }

    public ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, IBatchOutputs prior, CancellationToken ct) => new(_messages);

    /// <summary>
    /// Compone e valida PRIMA del WorkOutput: un rifiuto è errore permanente (codice di §7.9, nessun output, nessuna riparazione); ValidIncomplete o
    /// Complete → output Committed riutilizzabile con l'esito calcolato dal server. La completezza non dipende mai da una dichiarazione del modello.
    /// </summary>
    public ValidatedBatch Validate(BatchRef batch, string responseText)
    {
        var composition = AnalysisAliasComposer.Compose(responseText, _catalog, _input);
        Require(composition);

        var dto = AnalysisAliasContracts.Decode(responseText);
        var completion = composition.Validation!.Completion!;
        var payload = new JsonObject
        {
            ["schemaVersion"] = DocumentAnalysisWorkflow.SchemaVersionV3,
            ["outcome"] = composition.Outcome.ToString(),
            ["gaps"] = new JsonArray([.. completion.Gaps.Select(g => (JsonNode)JsonValue.Create(g.Code + ": " + g.Detail)!)]),
            ["coverageHash"] = completion.CoverageHash,
            ["aiOutputHash"] = composition.AiOutputHash,
            ["catalogHash"] = composition.CatalogHash,
            ["canonicalHash"] = composition.CanonicalHash,
            ["ai"] = JsonNode.Parse(AnalysisAliasContracts.Canonical(dto)),
            ["catalog"] = JsonNode.Parse(_catalog.Encode()),
        }.ToJsonString();
        var covered = JsonSerializer.Serialize(_input.Evidence.Select(e => e.EvidenceId.ToString("D")));
        var maps = JsonSerializer.Serialize(new { evidenceMap = composition.Output!.EvidenceMap.Select(m => new { itemId = m.ItemId.ToString("D"), evidenceIds = m.EvidenceIds.Select(g => g.ToString("D")) }) });
        var output = new WorkOutputInput(
            batch.BatchId, DocumentBatchWorkflow.InvokeId, batch.InputHash, CanonicalJson.Sha256Hex(payload), payload, null, null, covered, maps, null,
            DocumentAnalysisWorkflow.SchemaVersionV3, DocumentAnalysisWorkflow.ValidationVersionV3, null);
        return new ValidatedBatch(output);
    }

    private static void Require(AliasComposition composition)
    {
        if (composition.IsReusableWorkOutput)
        {
            return;
        }

        throw new DocumentWorkException(
            composition.ErrorCode ?? DocumentWorkException.WorkOutputInvalid,
            $"Output dell'analisi documentale non valido ({composition.Violations.Count} violazioni): " + string.Join(" | ", composition.Violations.Take(5)));
    }

    /// <summary>
    /// Ricompone il canonico dal WorkOutput Committed (nessuna inferenza): catalogo ricostruito dal payload e verificato contro l'hash congelato
    /// (<c>checkpoint_incompatible</c>) e contro lo snapshot del run, DTO verificato contro <c>aiOutputHash</c> (<c>work_output_invalid</c>), canonico
    /// ricomposto e verificato contro <c>canonicalHash</c> (<c>integrity_conflict</c>).
    /// </summary>
    private async ValueTask<AliasComposition> RecomposeAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var output = await outputs.GetByOrdinalAsync(0, ct)
            ?? throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, "Assemble senza l'output dell'analisi: nessun risultato da committare.");
        JsonElement root;
        string catalogHash, aiOutputHash, canonicalHash, catalogJson, aiJson;
        try
        {
            using var document = JsonDocument.Parse(output.PayloadJson ?? throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Payload mancante."));
            root = document.RootElement.Clone();
            if (root.GetProperty("schemaVersion").GetString() != DocumentAnalysisWorkflow.SchemaVersionV3)
            {
                throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Il WorkOutput non è del contratto con alias.");
            }

            (catalogHash, aiOutputHash, canonicalHash) = (root.GetProperty("catalogHash").GetString()!, root.GetProperty("aiOutputHash").GetString()!, root.GetProperty("canonicalHash").GetString()!);
            (catalogJson, aiJson) = (root.GetProperty("catalog").GetRawText(), root.GetProperty("ai").GetRawText());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Il payload del WorkOutput dell'analisi non è nel formato con alias.");
        }

        var catalog = AnalysisAliasCatalog.Decode(catalogJson, catalogHash);
        var composition = AnalysisAliasComposer.Compose(aiJson, catalog, _input, expectedCanonicalHash: canonicalHash);
        Require(composition);
        if (composition.AiOutputHash != aiOutputHash)
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Il DTO committato ha un hash diverso da aiOutputHash.");
        }

        return composition;
    }

    /// <summary>Assemble: un WorkOutput valido con obbligazioni residue non completa mai (no Completed parziale); i residui giustificano additional_work (I5).</summary>
    public async ValueTask AssembleAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var composition = await RecomposeAsync(outputs, ct);
        if (composition.Outcome != ReconciliationOutcome.Complete)
        {
            var gaps = composition.Validation!.Completion!.Gaps;
            throw new DocumentWorkException(
                DocumentWorkException.CoverageIncomplete,
                $"Il batch è valido ma le obbligazioni di riconciliazione non sono complete ({gaps.Count}): " + string.Join(" | ", gaps.Take(5).Select(g => g.Code + ": " + g.Detail)));
        }
    }

    public async ValueTask<int> CommitAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        // Ricomposizione e validazione server dal DTO committato: nessun Completed basato sul solo esito persistito.
        var composition = await RecomposeAsync(outputs, ct);
        if (composition.Outcome != ReconciliationOutcome.Complete)
        {
            throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, "Commit rifiutato: l'output committato non è completo.");
        }

        var output = composition.Output!;
        var relations = new List<RunRelation>();
        var accepted = composition.Validation!.Patches!.Dispositions.Where(d => d.Kind is PatchDispositionKind.Applied or PatchDispositionKind.PreservedAsConflict).Select(d => d.PatchId).ToHashSet(StringComparer.Ordinal);
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
