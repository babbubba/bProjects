using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Analysis;
using bProjects.App.Persistence;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

// Generazione documentale oltre la capacità di una chiamata (002 T086; contracts/ai-workflow.md §4.5 «Generazione», plan.md «Generare unità editoriali»).
// Il planner editoriale (T085) suddivide la revisione confermata in unità misurate; ogni unità è UN batch del grafo fisso T043 (stesso port T044: guard, riserva chiamate,
// lookup, commit fenced) il cui output è il DTO `document-draft-alias-1` validato contro i SOLI item principali dell'unità e le fonti che li sostengono. L'assemblaggio è
// deterministico e senza LLM: concatena i blocchi canonici ricomposti dagli output Committed nell'ordine delle unità e li valida per intero contro il pacchetto della
// revisione (copertura completa). Il Draft Proposed/NotApproved e Completed si committano insieme solo dopo quel controllo: nessun Markdown parziale è mai una bozza.

/// <summary>Costanti e composizione del messaggio della generazione per unità editoriali (versioni proprie: il percorso a singola chiamata resta invariato).</summary>
public static class DocumentDraftEditorial
{
    public const string PromptVersion = "document-draft-unit-1";
    public const string AlgorithmVersion = "draft-editorial-v1";
    public const string SchemaVersion = "document-draft-unit-1";
    public const string ValidationVersion = "document-draft-unit-validation-1";

    /// <summary>Espansione stimata dell'output rispetto ai token degli item principali (Markdown, marcatori e struttura JSON): parametro congelato nel manifesto.</summary>
    public const double OutputExpansion = 1.5;

    private static readonly Lazy<string> Prompt = new(() => ConversationalAgent.LoadPrompt("document-draft-unit-v1.md"));

    public static string Instructions => Prompt.Value;

    /// <summary>
    /// Messaggio unico dell'unità con soli alias (§7.11): gli item PRINCIPALI con classificazione, sezione e fonti <c>F&lt;n&gt;</c>; gli altri item dei conflitti come CONTESTO in
    /// sola lettura (senza fonti citabili); i conflitti con stato, alternative ed eventuale risoluzione motivata del PM; le sole fonti dei principali. Mai UUID, hash o id.
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildMessages(EditorialSlice slice, DraftAliasCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(catalog);
        string Alias(Guid id) => catalog.AliasOfItem(id) ?? throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"L'item {id:D} non ha un alias nel catalogo della bozza.");
        var primaries = slice.Primary.Select(p => p.Id).ToHashSet();
        var text = new StringBuilder(
            "Parte dell'analisi confermata dal project manager. Gli elementi PRINCIPALI [I<n>] vanno riportati tutti; gli elementi di CONTESTO servono solo a leggere i conflitti e non vanno riportati. "
            + "Le fonti sono alias F<n>: cita solo quelle degli elementi principali.\n");
        foreach (var item in slice.Primary)
        {
            var sources = string.Join(",", catalog.Item(Alias(item.Id))!.SourceAliases);
            text.Append($"\n<<<ELEMENTO [{Alias(item.Id)}] sezione={item.SectionKey} classificazione={item.Classification} fonti={sources}>>>\n")
                .Append(item.Text).Append("\n<<<FINE ELEMENTO>>>\n");
        }

        foreach (var item in slice.Context)
        {
            text.Append($"\n<<<CONTESTO [{Alias(item.Id)}] sezione={item.SectionKey} classificazione={item.Classification}>>>\n")
                .Append(item.Text).Append("\n<<<FINE CONTESTO>>>\n");
        }

        foreach (var conflict in slice.Conflicts)
        {
            text.Append($"\n<<<CONFLITTO stato={conflict.Status}>>>\n").Append(conflict.Description).Append('\n');
            foreach (var alternative in conflict.AlternativeIds)
            {
                text.Append($"Alternativa [{Alias(alternative)}] ({(primaries.Contains(alternative) ? "PRINCIPALE" : "CONTESTO")})\n");
            }

            if (conflict.SelectedAlternativeIds.Count > 0)
            {
                text.Append("Risoluzione del PM: alternativa scelta ").Append(string.Join(", ", conflict.SelectedAlternativeIds.Select(id => $"[{Alias(id)}]")))
                    .Append("; motivo: ").Append(conflict.ResolutionReason).Append('\n');
            }

            text.Append("<<<FINE CONFLITTO>>>\n");
        }

        var citable = slice.Primary.SelectMany(p => p.SourceIds).ToHashSet();
        text.Append("\nFonti citabili:\n");
        var listed = catalog.Sources.Where(s => citable.Contains(s.SourceId)).ToList();
        if (listed.Count == 0)
        {
            text.Append("(nessuna fonte)\n");
        }

        foreach (var source in listed)
        {
            text.Append($"[{source.Alias}] tipo={source.Kind}\n");
        }

        text.Append("\nRispondi ora con l'unico oggetto JSON `document-draft-alias-1` per i soli elementi PRINCIPALI.");
        return [new ChatMessage(ChatRole.User, text.ToString())];
    }
}

/// <summary>
/// Parte di dominio del grafo batch per la bozza oltre contesto (<see cref="IDocumentBatchPlan"/>): unità deterministiche dal planner T085 (identità append-only con l'hash
/// dell'output della precedente), validazione e composizione canonica PRIMA del WorkOutput, payload con DTO, hash e mappa storica delle fonti, assemblaggio e commit unico.
/// Il numero di batch dipende dagli output già Committed (cursore del grafo, mai un secondo scheduler). Nessun retry guidato: un'unità non valida è errore permanente e gli output
/// già Committed delle altre unità restano riusabili.
/// </summary>
internal sealed class DocumentDraftEditorialPlan : IDocumentBatchPlan
{
    private readonly DocumentRunClaim _claim;
    private readonly DocumentRunInputV11 _input;
    private readonly DraftAliasCatalog _catalog;
    private readonly DocumentDraftPackage _package;
    private readonly DraftSourceLedger _ledger;
    private readonly DocumentEditorialPlanner _planner;
    private readonly IServiceScopeFactory _scopes;
    private readonly string _manifestIdentity;
    private readonly string _instructions;
    private readonly List<EditorialUnit> _units = [];

    public DocumentDraftEditorialPlan(
        DocumentRunClaim claim, DocumentRunInputV11 input, DraftAliasCatalog catalog, DraftSourceLedger ledger, PayloadPreflight reference, bool structured,
        AgentFactory factory, InternalModelClient client, JsonElement schema, IServiceScopeFactory scopes)
    {
        _claim = claim;
        _input = input;
        _catalog = catalog;
        _ledger = ledger;
        _scopes = scopes;
        _package = DocumentDraftPackage.From(input.Content);
        _instructions = DocumentDraftEditorial.Instructions;

        var settings = factory.Options.ForPhase(AiPhase.Document);
        var profile = reference.Profile;
        var limits = new PlannerLimits(profile.InputBudgetTokens, profile.OutputTokens, DocumentDraftEditorial.OutputExpansion);
        var configHash = DocumentSourceDraftWorkflow.Sha(JsonSerializer.Serialize(new
        {
            prompt = DocumentDraftEditorial.PromptVersion, instructions = DocumentSourceDraftWorkflow.Sha(_instructions), schema = DocumentSourceDraftWorkflow.SchemaVersionV3,
            unitSchema = DocumentDraftEditorial.SchemaVersion, catalog = DraftAliasCatalog.Version, validation = DocumentDraftEditorial.ValidationVersion, structured,
            model = settings.ModelId, maxOutput = settings.MaxOutputTokens, thinking = settings.EnableThinking, inputBudget = limits.InputBudgetTokens, outputTokens = limits.OutputTokens,
            expansion = limits.OutputExpansion,
        }));
        Agent = new BatchAgentSpec(
            "DocumentDraftUnit", DocumentSourceDraftWorkflow.AgentId, _instructions,
            structured ? client.CreateChatOptions(schema, DocumentSourceDraftWorkflow.SchemaNameV3, temperature: 0.1f, phase: AiPhase.Document) : client.CreateChatOptions(temperature: 0.1f, phase: AiPhase.Document),
            AiPhase.Document, profile.Fingerprint);

        var sourceHash = DocumentSourceDraftWorkflow.Sha($"draft-source|{input.RevisionId:N}|{DocumentSourceDraftWorkflow.Sha(DocumentAnalysisContentJson.Serialize(input.Content))}");
        var policyHash = DocumentSourceDraftWorkflow.Sha("document-draft-policy-v1");
        var planJson = new JsonObject
        {
            ["algorithm"] = DocumentDraftEditorial.AlgorithmVersion,
            ["catalog"] = catalog.CatalogHash,
            ["definition"] = DocumentSourceDraftWorkflow.DefinitionVersionV3,
            ["graph"] = new JsonArray(DocumentBatchWorkflow.Stages.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["limits"] = new JsonObject { ["inputBudgetTokens"] = limits.InputBudgetTokens, ["outputTokens"] = limits.OutputTokens, ["outputExpansion"] = limits.OutputExpansion },
        }.ToJsonString();
        Manifest = new WorkManifestInput(
            configHash, sourceHash, profile.Fingerprint, DocumentSourceDraftWorkflow.DefinitionVersionV3, DocumentDraftEditorial.AlgorithmVersion, policyHash, planJson);
        _manifestIdentity = CanonicalJson.Sha256Hex(JsonSerializer.Serialize(new
        {
            configHash, source = sourceHash, runtime = profile.Fingerprint, definition = DocumentSourceDraftWorkflow.DefinitionVersionV3,
            algorithm = DocumentDraftEditorial.AlgorithmVersion, policy = policyHash, plan = JsonDocument.Parse(planJson).RootElement,
        }));

        var cache = new Dictionary<string, UnitMeasure>(StringComparer.Ordinal);
        _planner = new DocumentEditorialPlanner(
            input.Content, $"ProcessingRun|{claim.RunId:N}", _manifestIdentity, DocumentSourceDraftWorkflow.DefinitionVersionV3, limits,
            async (slice, ct) =>
            {
                var key = string.Join('|', slice.Primary.Select(i => i.Id)) + "#" + string.Join('|', slice.Context.Select(i => i.Id));
                if (cache.TryGetValue(key, out var known))
                {
                    return known;
                }

                var full = await factory.Preflight.MeasureAsync(
                    new DocumentPayload(AiPhase.Document, _instructions, DocumentDraftEditorial.BuildMessages(slice, catalog), structured ? schema.GetRawText() : null), ct, profile.Fingerprint);
                var owned = await factory.Preflight.MeasureAsync(
                    new DocumentPayload(AiPhase.Document, string.Empty, [new ChatMessage(ChatRole.User, string.Join('\n', slice.Primary.Select(i => i.Text)))], null), ct, profile.Fingerprint);
                return cache[key] = new UnitMeasure(full.Tokens, full.Quality, full.PayloadHash, owned.Tokens);
            },
            DocumentDraftEditorial.SchemaVersion, DocumentDraftEditorial.PromptVersion);
    }

    /// <summary>Vero dopo il commit atomico del run da questo piano.</summary>
    public bool Committed { get; private set; }

    public WorkManifestInput Manifest { get; }

    public WorkLookupExpectation Expectation => new(DocumentDraftEditorial.SchemaVersion, DocumentDraftEditorial.ValidationVersion);

    public BatchAgentSpec Agent { get; }

    public bool HasBatch(int ordinal) => ordinal == 0;

    /// <summary>
    /// Anteprima del piano prima di ogni chiamata: pianifica tutte le unità con genitori sintetici (i confini dipendono solo dal misuratore, non dagli hash dei genitori) cosi un
    /// item irriducibile e <see cref="DocumentWorkException.DocumentIncomplete"/> con il suo ID PRIMA di spendere chiamate. Non scrive nulla e non altera le unità reali.
    /// </summary>
    public async ValueTask PreflightAsync(CancellationToken ct) =>
        _ = await _planner.PlanAsync(ordinal => ordinal == 0 ? [] : [DocumentSourceDraftWorkflow.Sha($"preview-parent|{ordinal - 1}")], ct);

    private int Covered => _units.Sum(u => u.PrimaryItemIds.Count);

    /// <summary>
    /// Unità con quell'ordinale. Le unità sono funzione deterministica della revisione e del misuratore; l'unità k dipende dall'hash dell'output Committed della k-1 (append-only),
    /// quindi dopo una ripresa in un processo nuovo si ricostruiscono in ordine rileggendo dal database i soli hash dei genitori (nessuna chiamata al modello).
    /// </summary>
    private async ValueTask<EditorialUnit> UnitAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        while (_units.Count <= ordinal)
        {
            var k = _units.Count;
            if (k > 0 && Covered >= _planner.Items.Count)
            {
                throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"La generazione per unità editoriali non ha un'unità {ordinal}: la revisione è già coperta.");
            }

            EditorialUnit unit;
            if (k == 0)
            {
                unit = await _planner.TrySingleCallAsync(ct) ?? await _planner.NextAsync(0, 0, [], ct);
            }
            else
            {
                var parent = await prior.GetByOrdinalAsync(k - 1, ct)
                    ?? throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, $"L'unità {k} dipende dall'output dell'unità {k - 1}, non ancora committato.");
                unit = await _planner.NextAsync(Covered, k, [parent.OutputHash], ct);
            }

            _units.Add(unit);
        }

        return _units[ordinal];
    }

    public async ValueTask<bool> HasBatchAsync(int ordinal, IBatchOutputs prior, CancellationToken ct)
    {
        if (ordinal == 0)
        {
            return true;
        }

        await UnitAsync(ordinal - 1, prior, ct);
        return Covered < _planner.Items.Count;
    }

    public async ValueTask<DerivedBatchInput> PrepareAsync(int ordinal, IBatchOutputs prior, CancellationToken ct) => (await UnitAsync(ordinal, prior, ct)).Derived;

    public async ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, IBatchOutputs prior, CancellationToken ct)
    {
        var unit = await UnitAsync(batch.Ordinal, prior, ct);
        return DocumentDraftEditorial.BuildMessages(_planner.SliceOf(PrimariesOf(unit)), _catalog);
    }

    private IReadOnlyList<EditorialItem> PrimariesOf(EditorialUnit unit)
    {
        var byId = _planner.Items.ToDictionary(i => i.Id);
        return [.. unit.PrimaryItemIds.Select(id => byId[id])];
    }

    public async ValueTask<ValidatedBatch> ValidateAsync(BatchRef batch, string responseText, IBatchOutputs prior, CancellationToken ct)
    {
        await UnitAsync(batch.Ordinal, prior, ct);
        return Validate(batch, responseText);
    }

    /// <summary>Compone e valida PRIMA del WorkOutput contro i soli item principali dell'unità: un rifiuto è errore permanente (nessun output durevole, nessuna riparazione).</summary>
    public ValidatedBatch Validate(BatchRef batch, string responseText)
    {
        var unit = batch.Ordinal < _units.Count ? _units[batch.Ordinal] : throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"L'unità {batch.Ordinal} non è pianificata.");
        if (unit.Derived.BatchId != batch.BatchId)
        {
            throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"Il batch {batch.Ordinal} non corrisponde all'unità pianificata.");
        }

        var composition = DraftAliasComposer.Compose(responseText, _catalog, _package.ForItems(unit.PrimaryItemIds));
        if (!composition.Accepted)
        {
            throw new DocumentWorkException(
                composition.ErrorCode ?? DocumentWorkException.WorkOutputInvalid,
                $"Output dell'unità editoriale {batch.Ordinal} non valido ({composition.Violations.Count} violazioni): " + string.Join(" | ", composition.Violations.Take(5)));
        }

        var dto = DraftAliasContracts.Decode(responseText);
        var output = composition.Validation!.Output!;
        var primaries = unit.PrimaryItemIds.Select(i => i.ToString("D")).ToList();
        var map = DraftSourceMap.Build(output.Blocks, _package.ItemSources, _ledger);
        var payload = new JsonObject
        {
            ["schemaVersion"] = DocumentDraftEditorial.SchemaVersion,
            ["revisionId"] = _input.RevisionId.ToString("D"),
            ["ordinal"] = unit.Ordinal,
            ["primaryItemIds"] = new JsonArray([.. primaries.Select(p => (JsonNode)JsonValue.Create(p)!)]),
            ["aiOutputHash"] = composition.AiOutputHash,
            ["catalogHash"] = composition.CatalogHash,
            ["canonicalHash"] = composition.CanonicalHash,
            ["ai"] = JsonNode.Parse(DraftAliasContracts.Canonical(dto)),
            ["sourceMap"] = DraftSourceMap.ToJson(map),
        }.ToJsonString();
        var covered = JsonSerializer.Serialize(primaries);
        var maps = new JsonObject
        {
            ["blocks"] = new JsonArray([.. output.Blocks.Select(b => (JsonNode)new JsonObject
            {
                ["itemIds"] = new JsonArray([.. b.ItemIds.Select(i => (JsonNode)JsonValue.Create(i.ToString("D"))!)]),
                ["sourceIds"] = new JsonArray([.. b.SourceIds.Select(i => (JsonNode)JsonValue.Create(i.ToString("D"))!)]),
            })]),
            ["sourceMap"] = DraftSourceMap.ToJson(map),
        }.ToJsonString();
        var work = new WorkOutputInput(
            batch.BatchId, DocumentBatchWorkflow.InvokeId, batch.InputHash, CanonicalJson.Sha256Hex(payload), payload, null, null, covered, maps, null,
            DocumentDraftEditorial.SchemaVersion, DocumentDraftEditorial.ValidationVersion, null);
        return new ValidatedBatch(work);
    }

    /// <summary>
    /// Ricompone i blocchi canonici di una unità dal WorkOutput Committed (nessuna inferenza): schema, revisione, ordinale e membership dell'unità verificati contro il piano,
    /// catalogo ricostruito verificato contro <c>catalogHash</c>, DTO ricomposto verificato contro <c>canonicalHash</c> e <c>aiOutputHash</c>.
    /// </summary>
    private IReadOnlyList<DraftBlock> Decode(EditorialUnit unit, WorkOutput output)
    {
        string catalogHash, aiOutputHash, canonicalHash, aiJson;
        try
        {
            using var document = JsonDocument.Parse(output.PayloadJson ?? throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Payload mancante."));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetString() != DocumentDraftEditorial.SchemaVersion)
            {
                throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Il WorkOutput non è del contratto per unità editoriali.");
            }

            if (root.GetProperty("revisionId").GetGuid() != _input.RevisionId)
            {
                throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "L'output committato appartiene a un'altra revisione di input.");
            }

            var recorded = root.GetProperty("primaryItemIds").EnumerateArray().Select(e => e.GetGuid()).ToList();
            if (root.GetProperty("ordinal").GetInt32() != unit.Ordinal || !recorded.SequenceEqual(unit.PrimaryItemIds))
            {
                throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, $"L'output committato non corrisponde all'unità pianificata {unit.Ordinal}.");
            }

            (catalogHash, aiOutputHash, canonicalHash, aiJson) = (
                root.GetProperty("catalogHash").GetString()!, root.GetProperty("aiOutputHash").GetString()!, root.GetProperty("canonicalHash").GetString()!, root.GetProperty("ai").GetRawText());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, $"Il payload del WorkOutput dell'unità {unit.Ordinal} non è nel formato atteso.");
        }

        if (catalogHash != _catalog.CatalogHash)
        {
            throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Il catalogo alias ricostruito dalla revisione ha un hash diverso da quello committato.");
        }

        var composition = DraftAliasComposer.Compose(aiJson, _catalog, _package.ForItems(unit.PrimaryItemIds), expectedCanonicalHash: canonicalHash);
        if (!composition.Accepted)
        {
            throw new DocumentWorkException(
                composition.ErrorCode ?? DocumentWorkException.WorkOutputInvalid,
                $"L'output committato dell'unità {unit.Ordinal} non supera la validazione server: " + string.Join(" | ", composition.Violations.Take(3)));
        }

        return composition.AiOutputHash == aiOutputHash
            ? composition.Validation!.Output!.Blocks
            : throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, $"Il DTO committato dell'unità {unit.Ordinal} ha un hash diverso da aiOutputHash.");
    }

    /// <summary>
    /// Assemblaggio deterministico senza inferenza: i blocchi di tutte le unità, nell'ordine, validati per intero contro il pacchetto della revisione. Una unità senza output
    /// Committed o un item non rappresentato è <see cref="DocumentWorkException.CoverageIncomplete"/>: nessuna bozza.
    /// </summary>
    private async ValueTask<string> AssembleMarkdownAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var blocks = new List<DraftBlock>();
        var ordinal = 0;
        do
        {
            var unit = await UnitAsync(ordinal, outputs, ct);
            var output = await outputs.GetByOrdinalAsync(ordinal, ct)
                ?? throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, $"Assemble senza l'output dell'unità {ordinal}: nessuna bozza da committare.");
            blocks.AddRange(Decode(unit, output));
            ordinal++;
        }
        while (_units.Take(ordinal).Sum(u => u.PrimaryItemIds.Count) < _planner.Items.Count);

        _planner.VerifyCovered(_units);
        var canonical = new JsonObject
        {
            ["sectionKey"] = "documento",
            ["blocks"] = new JsonArray([.. blocks.Select(b => (JsonNode)new JsonObject
            {
                ["itemIds"] = new JsonArray([.. b.ItemIds.Select(i => (JsonNode)JsonValue.Create(i.ToString("D"))!)]),
                ["markdown"] = b.Markdown,
                ["sourceIds"] = new JsonArray([.. b.SourceIds.Select(i => (JsonNode)JsonValue.Create(i.ToString("D"))!)]),
            })]),
        }.ToJsonString();
        var validation = DocumentDraftValidator.ValidateBlocks(canonical, _package);
        return validation.Accepted
            ? validation.Markdown!
            : throw new DocumentWorkException(
                DocumentWorkException.CoverageIncomplete, "La bozza assemblata dalle unità non copre l'intera revisione confermata: " + string.Join(" | ", validation.Violations.Take(3)));
    }

    public async ValueTask AssembleAsync(IBatchOutputs outputs, CancellationToken ct) => _ = await AssembleMarkdownAsync(outputs, ct);

    public async ValueTask<int> CommitAsync(IBatchOutputs outputs, CancellationToken ct)
    {
        var markdown = await AssembleMarkdownAsync(outputs, ct);
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
        return _units.Count;
    }
}
