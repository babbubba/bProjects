using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// Esito dell'assembly di una gerarchia v11 nel rig: analisi, mappa evidenze→item e disposizioni delle patch, come li compone <c>DocumentReconcileAssembly.Assemble</c>. <see cref="Raw"/> è il
/// <c>ReconcileAssembly</c> interno, per l'assembly dello stadio Gap (<c>DocumentReconcileGapStage.Verify</c>).
/// </summary>
internal sealed record AssembledV11(DocumentAnalysisContent Analysis, IReadOnlyList<EvidenceMapEntry> Map, IReadOnlyList<RunPatchDispositionRecord> Patches, object Raw);

/// <summary>
/// Motore di riconciliazione reale (directory, gerarchia, <see cref="ReconciliationCandidates"/>) con un modello FINTO della definizione v11 (RH-021): le foglie A non producono item (li compone il server con
/// <see cref="ReconcileLeafProjection"/>), il confronto non ha candidati. Validazione V2 e opzioni del compositore con le evidenze, come il piano. Nessun database. L'assembly è <c>internal</c>: chiamato per
/// riflessione (il progetto non ha <c>InternalsVisibleTo</c>), con le sezioni dei batch e la politica degli estremi dei conflitti della v11, come i due punti di chiamata del piano.
/// </summary>
internal sealed class V11Rig
{
    private static readonly ReconciliationContext Context = new("owner", "manifest", "project-analysis-documents-v11", new PlannerLimits(1_000_000, 1_000_000), Rules: ReconcileValidationRules.V2);

    private static readonly PackMeasurer<CatalogEntry> LeafMeasurer = (members, _) =>
        new ValueTask<UnitMeasure>(new UnitMeasure(10 + 5 * members.Count, TokenCountQuality.Measured, M2Ledger.Sha("leaf|" + string.Join(',', members.Select(m => m.EntryId))), members.Count));

    private static readonly ReconciliationMeasurers Measurers = new(LeafMeasurer, (members, _) => new ValueTask<UnitMeasure>(new UnitMeasure(10, TokenCountQuality.Measured, M2Ledger.Sha("page|" + members.Count), members.Count)));

    private static readonly PackMeasurer<CatalogNode> Descriptors = (members, _) =>
        new ValueTask<UnitMeasure>(new UnitMeasure(10 + 5 * members.Count, TokenCountQuality.Measured, M2Ledger.Sha("nodes|" + string.Join(',', members.Select(m => m.NodeId))), members.Count));

    private readonly Func<V11Rig, ReconcileBatchRequest, string> _candidateStageDto;
    private readonly List<ReconcileItemRef> _items = [];
    private readonly List<ItemProposal> _proposals = [];
    private readonly Dictionary<string, AiSection> _sections = new(StringComparer.Ordinal);
    private readonly List<UngroundedAliasItem> _ungrounded = [];

    private V11Rig(DocumentAnalysisInput input, Func<V11Rig, ReconcileBatchRequest, string> candidateStageDto)
    {
        Input = input;
        RunCatalog = AnalysisAliasCatalog.Build(input);
        Evidence = M2Ledger.ById(input);
        Options = new ReconcileComposeOptions(ReconcileAliasContracts.StageContract, Evidence);
        _candidateStageDto = candidateStageDto;
    }

    public DocumentAnalysisInput Input { get; }
    public AnalysisAliasCatalog RunCatalog { get; }
    public IReadOnlyDictionary<Guid, DocumentAnalysisEvidence> Evidence { get; }
    public ReconcileComposeOptions Options { get; }
    public List<ReconcileBatchRequest> Calls { get; } = [];
    public ReconciliationDirectoryResult Directory { get; private set; } = null!;
    public HierarchyResult Hierarchy { get; private set; } = null!;
    public ReconciliationCandidates Engine { get; private set; } = null!;

    public IReadOnlyList<ReconcileItemRef> Items => _items;

    public static async Task<V11Rig> CreateAsync(DocumentAnalysisInput input, Func<V11Rig, ReconcileBatchRequest, string> candidateStageDto, CancellationToken ct)
    {
        var rig = new V11Rig(input, candidateStageDto);
        var snapshot = input.Snapshot;
        rig.Directory = await ReconciliationDirectory.BuildAsync(snapshot, new Dictionary<Guid, DirectoryFacts>(), Context, Measurers, ct);
        rig.Hierarchy = await ReconciliationHierarchy.BuildAsync(snapshot, rig.Directory, Context, Descriptors, rig.InvokeAsync, ct);
        rig.Engine = new ReconciliationCandidates(snapshot, rig.Directory, rig.Hierarchy, Context, LeafMeasurer, rig.InvokeAsync);
        return rig;
    }

    /// <summary>Alias <c>E&lt;n&gt;</c> del numero del ledger.</summary>
    public static string Alias(int n) => "E" + n;

    /// <summary>Handle <c>i&lt;n&gt;</c> dell'item già proposto per l'evidenza (numero del ledger) nel catalogo del batch dato.</summary>
    public string HandleOf(int evidenceNumber, ReconcileBatchRequest request)
    {
        var catalog = ReconcileAliasCatalog.Build(RunCatalog, request, _items);
        var id = Input.Evidence[evidenceNumber - 1].EvidenceId;
        return catalog.Data.Items.Single(i => catalog.ItemEvidence[i.Handle].Contains(id)).Handle;
    }

    private ValueTask<string> InvokeAsync(ReconcileBatchRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        var candidateStage = request.Batch.Stage is "expand" or "compare";
        var dto = candidateStage ? _candidateStageDto(this, request) : StructuralDto(request);
        var catalog = ReconcileAliasCatalog.Build(RunCatalog, request, _items);
        var baseState = candidateStage ? DocumentAnalysisBase.Compute(_proposals, Input.Snapshot) : null;
        var composed = ReconcileAliasComposer.Compose(dto, catalog, request, Input.Snapshot, baseState, null, Options);
        if (!composed.IsValid)
        {
            throw new DocumentWorkException(composed.ErrorCode ?? DocumentWorkException.WorkOutputInvalid, string.Join(" | ", composed.Violations));
        }

        foreach (var proposal in composed.Result!.ItemProposals)
        {
            _items.Add(new ReconcileItemRef(proposal.LocalKey, proposal.Text, proposal.EvidenceIds));
            _proposals.Add(proposal);
        }

        foreach (var (key, section) in composed.Sections)
        {
            _sections[key] = section;
        }

        _ungrounded.AddRange(composed.Ungrounded);
        return new ValueTask<string>(ReconciliationCodec.Encode(composed.Result));
    }

    /// <summary>Foglie, riduzione e cross della v11: descriptor dove si costruiscono nodi, <c>items</c> sempre vuoto (nella foglia A li compone il server), spiegazione «nessun collegamento».</summary>
    private static string StructuralDto(ReconcileBatchRequest request) =>
        ReconcileContractMatrixTests.Dto(noFindings: "nessun collegamento emerso", descriptor: request.Batch.Stage is "leaf" or "reduce" ? "gruppo" : null).ToJsonString();

    /// <summary>DTO di un confronto <c>Open</c> con il motivo e, facoltative, le patch (array di oggetti JSON).</summary>
    public static string CompareOpen(string reason, params JsonObject[] patches) => ReconcileContractMatrixTests.Dto(noFindings: null, change: o =>
    {
        o["focus"] = JsonNode.Parse(JsonSerializer.Serialize(new { outcome = "Open", reason }));
        o["patches"] = new JsonArray([.. patches.Select(p => (JsonNode)p)]);
    }).ToJsonString();

    /// <summary>DTO di un confronto <c>Compared</c> senza motivo.</summary>
    public static string CompareCompared() => ReconcileContractMatrixTests.Dto(noFindings: null, change: o => o["focus"] = JsonNode.Parse("""{"outcome":"Compared","reason":null}""")).ToJsonString();

    /// <summary>Patch <c>AddRelation</c> sul bersaglio <paramref name="targetItem"/> fra due evidenze del batch.</summary>
    public static JsonObject AddRelation(string key, string targetItem, string from, string to, string type, string rationale) => new()
    {
        ["key"] = key, ["op"] = "AddRelation", ["target"] = new JsonObject { ["item"] = targetItem, ["relation"] = null }, ["evidence"] = new JsonArray(),
        ["value"] = new JsonObject { ["from"] = from, ["to"] = to, ["type"] = type, ["basis"] = "Inferred", ["rationale"] = rationale },
    };

    /// <summary>Patch <c>ProposeAlternative</c> sul bersaglio <paramref name="targetItem"/> citando le evidenze indicate.</summary>
    public static JsonObject ProposeAlternative(string key, string targetItem, string text, params string[] evidence) => new()
    {
        ["key"] = key, ["op"] = "ProposeAlternative", ["target"] = new JsonObject { ["item"] = targetItem, ["relation"] = null }, ["evidence"] = new JsonArray([.. evidence.Select(e => (JsonNode)JsonValue.Create(e)!)]),
        ["value"] = new JsonObject { ["text"] = text, ["classification"] = "Inferred", ["reason"] = "alternativa proposta dal confronto" },
    };

    private static readonly Assembly AppAssembly = typeof(ReconciliationHierarchy).Assembly;

    /// <summary>
    /// Assembly v11 per riflessione (<c>DocumentReconcileAssembly.Assemble</c>). <paramref name="openEndpoints"/> è la politica degli estremi dei conflitti passata dal piano nelle sezioni; <c>null</c> = nessuna
    /// politica (assembly come nella v10, con le sezioni dei batch).
    /// </summary>
    public AssembledV11 Assemble(string? openEndpoints = ReconcileAliasContracts.StageOpenEndpointsPolicy)
    {
        var run = Engine.Result();
        var final = new FinalPassResult(FinalPassOutcome.Complete, null, null, [], run, true);
        var engineResult = Activator.CreateInstance(AppAssembly.GetType("bProjects.App.AI.ReconcileEngineResult")!, Directory, Hierarchy, run, final)!;
        var sections = Activator.CreateInstance(AppAssembly.GetType("bProjects.App.AI.HierarchicalItemSections")!, new Dictionary<string, AiSection>(_sections), new List<UngroundedAliasItem>(_ungrounded), openEndpoints)!;
        var assemble = AppAssembly.GetType("bProjects.App.AI.DocumentReconcileAssembly")!.GetMethod("Assemble", BindingFlags.Public | BindingFlags.Static)!;
        var assembled = assemble.Invoke(null, [Input, engineResult, sections])!;
        var type = assembled.GetType();
        var hierarchicalRun = type.GetProperty("Run")!.GetValue(assembled)!;
        var patches = hierarchicalRun.GetType().GetProperty("Patches")!.GetValue(hierarchicalRun)!;
        var dispositions = (IReadOnlyList<RunPatchDispositionRecord>)patches.GetType().GetProperty("RunDispositions")!.GetValue(patches)!;
        return new((DocumentAnalysisContent)type.GetProperty("Analysis")!.GetValue(assembled)!, (IReadOnlyList<EvidenceMapEntry>)type.GetProperty("Map")!.GetValue(assembled)!, dispositions, assembled);
    }

    /// <summary>
    /// Assembly dello stadio Gap (<c>DocumentReconcileGapStage.Verify</c>, per riflessione) sul payload <c>gap-stage-output-1</c> costruito dalla risposta del modello finto: ritorna per ogni Gap
    /// (alias) la domanda e il conflitto canonici risolti. Un rifiuto è l'eccezione di produzione, non un valore.
    /// </summary>
    public IReadOnlyList<(string Gap, Guid? OpenItem, Guid? OpenConflict)> VerifyGapStage(AssembledV11 assembled, string stageResponse)
    {
        var references = References(assembled);
        var validation = GapStageContracts.Validate(stageResponse, RunCatalog, references);
        Assert.True(validation.Outcome == ReconciliationOutcome.Complete, "risposta dello stadio non valida: " + string.Join(" | ", validation.Violations));
        var payload = GapStagePayload.Build(validation, RunCatalog, references, Input.Evidence.Count(e => e.Kind != EvidenceKind.Gap), Input.Evidence.Count(e => e.Kind == EvidenceKind.Gap));
        var verify = AppAssembly.GetType("bProjects.App.AI.DocumentReconcileGapStage")!.GetMethod("Verify", BindingFlags.Public | BindingFlags.Static)!;
        System.Collections.IEnumerable resolved;
        try
        {
            resolved = (System.Collections.IEnumerable)verify.Invoke(null, [assembled.Raw, RunCatalog, references, payload])!;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }

        return [.. resolved.Cast<object>().Select(r =>
        {
            var t = r.GetType();
            return ((string)t.GetProperty("GapAlias")!.GetValue(r)!, (Guid?)t.GetProperty("OpenItemId")!.GetValue(r), (Guid?)t.GetProperty("OpenConflictId")!.GetValue(r));
        })];
    }

    // ---------------------------------------------------------------- copertura e riferimenti dello stadio Gap sull'analisi assemblata

    /// <summary>Riferimenti dello stadio Gap come li deriva il piano dall'analisi assemblata (<c>GapStageReferences.From</c>).</summary>
    public GapStageReferences References(AssembledV11 assembled) => GapStageReferences.From(assembled.Analysis, assembled.Map, RunCatalog);

    /// <summary>Input di copertura dell'analisi assemblata: evidenze del ledger, collegamenti item→evidenza della mappa e avviso strutturale D3 acceso (definizione v11).</summary>
    public DocumentCoverageInput CoverageInput(AssembledV11 assembled)
    {
        var evidence = Input.Evidence.Select(e => new DocumentCoverageEvidence(
            e.EvidenceId, RunCatalog.EvidenceById(e.EvidenceId)!.Alias, e.Origin, e.Sources.Select(s => s.MessageId).FirstOrDefault(m => m is not null), e.Kind, e.Polarity, e.Statement, [], null, null,
            [.. e.Sources.Select(s => new CoverageSource(s.SourceId, s.MessageId, s.BlockId, s.Start, s.End, s.Quote))])).ToList();
        var links = assembled.Map.SelectMany(m => m.EvidenceIds.Select(id => (m.ItemId, id))).ToList();
        return new DocumentCoverageInput(evidence, links, assembled.Analysis, assembled.Analysis, null, OriginalTexts(), [], []) with { StructuralRepetition = true };
    }

    /// <summary>Testi originali delle fonti (per blocco o messaggio) tali che ogni citazione stia nel suo intervallo: la copertura verifica gli intervalli contro l'originale.</summary>
    private Dictionary<Guid, string> OriginalTexts()
    {
        var texts = new Dictionary<Guid, string>();
        foreach (var group in Input.Evidence.SelectMany(e => e.Sources).GroupBy(s => s.BlockId ?? s.MessageId!.Value))
        {
            var chars = new char[group.Max(s => s.End)];
            Array.Fill(chars, ' ');
            foreach (var s in group)
            {
                s.Quote.CopyTo(0, chars, s.Start, s.Quote.Length);
            }

            texts[group.Key] = new string(chars);
        }

        return texts;
    }

    public IReadOnlyList<DocumentCoverageFinding> Findings(AssembledV11 assembled) => DocumentCoverage.Check(CoverageInput(assembled));

    /// <summary>Violazioni delle regole strutturali del contenuto (<see cref="DocumentAnalysisContentRules"/>) sull'analisi assemblata.</summary>
    public IReadOnlyList<string> ContentViolations(AssembledV11 assembled) =>
        DocumentAnalysisContentRules.Check(assembled.Analysis, new DocumentAnalysisFacts(Input.ProjectId, Input.AnalysisId, Input.Cutoff, Input.PmMessageCount, Input.ReadyDocumentSourceCount));

    /// <summary>Tutti gli item delle sezioni della revisione (escluse le alternative dei conflitti), con il nome della sezione.</summary>
    public static IEnumerable<(string Section, DocumentAnalysisItem Item)> SectionItems(DocumentAnalysisContent a) =>
        a.Goals.Select(i => ("goals", i)).Concat(a.ContextAndScope.Overview.Select(i => ("scopeOverview", i))).Concat(a.ContextAndScope.Included.Select(i => ("scopeIncluded", i)))
            .Concat(a.ContextAndScope.Excluded.Select(i => ("scopeExcluded", i))).Concat(a.Requirements.Select(i => ("requirements", i))).Concat(a.OperationalFlows.Select(i => ("flows", i)))
            .Concat(a.Constraints.Select(i => ("constraints", i))).Concat(a.Dependencies.Select(i => ("dependencies", i))).Concat(a.Assumptions.Select(i => ("assumptions", i)))
            .Concat(a.OpenQuestions.Select(i => ("openQuestions", i)));
}
