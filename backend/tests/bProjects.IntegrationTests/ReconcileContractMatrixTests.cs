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
/// Contratto sotto esame: v10 = profilo degli schemi <c>reconcile-alias-schema-by-batch-3</c> (uno schema per stadio); v6 = schema unico <c>reconcile-alias-1</c> senza profilo né membership;
/// v11 (RH-012, M1) = profilo <c>reconcile-alias-schema-by-stage-1</c> con validazione <c>reconcile-validation-v2</c> e membership come la v10.
/// </summary>
public enum ContractProfile { V10, V6, V11 }

/// <summary>
/// Esito di UN batch attraverso tutta la catena di produzione, senza database né modello: ammissione dello schema inviato (<see cref="ReconcileSchemaSubset"/>), compositore alias, validatore canonico
/// (<c>CheckResult</c>, riferimenti, visite, descriptor) e, per conferma, <c>ReconciliationHierarchy.InvokeAsync</c> chiamato per riflessione (è <c>internal</c> e il progetto non ha <c>InternalsVisibleTo</c>).
/// </summary>
internal sealed record ContractVerdict(IReadOnlyList<string> SchemaViolations, ReconcileAliasComposition Composition, IReadOnlyList<string> ValidatorViolations, string? HierarchyError)
{
    public bool SchemaAdmits => SchemaViolations.Count == 0;

    public IReadOnlyList<string> ComposerViolations => Composition.Violations;

    /// <summary>Accettato dal compositore, dal validatore e da <c>InvokeAsync</c>.</summary>
    public bool Accepted => Composition.IsValid && ValidatorViolations.Count == 0 && HierarchyError is null;

    public string Report => $"schema=[{string.Join(" | ", SchemaViolations)}] composer=[{string.Join(" | ", ComposerViolations)}] validator=[{string.Join(" | ", ValidatorViolations)}] hierarchy=[{HierarchyError}]";
}

/// <summary>
/// Un batch concreto (stadio, vista, voci, nodi, candidato in esame, item già proposti, base del passaggio) costruito come fa il piano di riconciliazione: catalogo alias del run, catalogo del batch,
/// snapshot. Condiviso da RH-002 (matrice di contratto) e RH-003 (test di proprietà): stessi dati, stessa catena.
/// </summary>
internal sealed class ReconcileBatchWorld
{
    private static readonly string BatchId = new('b', 64);
    private static readonly string InputHash = new('c', 64);

    public required DocumentAnalysisInput Input { get; init; }
    public required AnalysisAliasCatalog RunCatalog { get; init; }
    public required ReconcileBatchRequest Request { get; init; }
    public required ReconcileAliasCatalog Catalog { get; init; }
    public required ReconciliationBase? Base { get; init; }
    public required ReconciliationCatalog ValidationCatalog { get; init; }
    public required ReconcileBatchSchema Variant { get; init; }
    public required ContractProfile Profile { get; init; }

    /// <summary>Come <c>ReconciliationHierarchy.BuildAsync</c>: i batch di foglia e riduzione costruiscono nodi (descriptor obbligatorio), gli altri no.</summary>
    public bool BuildsNode => Request.Batch.Stage is "leaf" or "reduce";

    public JsonElement Schema => Profile switch
    {
        ContractProfile.V10 => ReconcileAliasContracts.SchemaOfV3(Variant),
        ContractProfile.V11 => ReconcileAliasContracts.SchemaOfByStage(Variant),
        _ => ReconcileAliasContracts.JsonSchema(),
    };

    /// <summary>Regole del validatore canonico della definizione: v2 solo per la v11 (D3), altrimenti v1.</summary>
    public ReconcileValidationRules Rules => Profile == ContractProfile.V11 ? ReconcileValidationRules.V2 : ReconcileValidationRules.V1;

    public static DocumentAnalysisInput DefaultInput() => AliasFixtures.Input(relations: [AliasFixtures.Relation(3, 5, EvidenceRelationType.Contradicts)]);

    /// <summary>Ledger a sei evidenze: le cinque di <see cref="AliasFixtures"/> più E6, una Gap dichiarata dal PM.</summary>
    public static DocumentAnalysisInput InputWithGap()
    {
        var list = AliasFixtures.Evidences();
        list.Add(new DocumentAnalysisEvidence(
            AliasFixtures.EvidenceId(6), AliasFixtures.G(206), EvidenceOriginKind.Message, EvidenceKind.Gap, EvidencePolarity.Affirmed, EvidenceCertainty.Declared,
            "Chi gestisce gli scarti della DESADV?", new string('6', 64), [AliasFixtures.Msg(AliasFixtures.G(308), AliasFixtures.M1, 1, "Servono 10 ordini")]));
        return AliasFixtures.Input(evidence: list, relations: [AliasFixtures.Relation(3, 5, EvidenceRelationType.Contradicts)]);
    }

    public static CatalogEntry EntryOf(DocumentAnalysisInput input, int n)
    {
        var e = input.Evidence[n - 1];
        return new()
        {
            EntryId = ReconciliationIds.EntryId(input.Snapshot.SourceHash, e.EvidenceId, e.ContentHash), EvidenceId = e.EvidenceId, EvidenceVersionId = e.VersionId, ContentHash = e.ContentHash,
            SourceIds = [.. e.Sources.Select(s => s.SourceId).Distinct()], Keys = [], KnownRelationIds = [], UnclassifiedViews = [],
        };
    }

    private static CatalogNode NodeOf(char c, int ordinal) => new()
    {
        NodeId = new string(c, 64), View = ReconcileView.A, Level = 0, Ordinal = ordinal, ChildNodeIds = [], MemberSetHash = new string((char)('a' + (c - '0')), 64),
        MemberCount = 2, KeyPageHashes = [], RelationPageHashes = [], Descriptor = $"descriptor {c}", DescriptorOutputHash = new string('d', 64),
    };

    private static CandidateEndpoint EndpointOf(DocumentAnalysisInput input, int n) => ReconciliationFixtures.Endpoint(input.Evidence[n - 1].EvidenceId, input.Evidence[n - 1].ContentHash);

    /// <summary>Candidato Evidence-Evidence in esame in un confronto (kind Conflict per Contradicts/AlternativeTo, altrimenti Relation).</summary>
    public static ReconcileCandidate PairFocus(DocumentAnalysisInput input, int a, int b, ReconcileProposedType type)
    {
        var (l, r) = (EndpointOf(input, a), EndpointOf(input, b));
        var kind = type is ReconcileProposedType.Contradicts or ReconcileProposedType.AlternativeTo ? CandidateKind.Conflict : CandidateKind.Relation;
        return new()
        {
            CandidateId = ReconciliationIds.CandidateId(kind, l, r, type, input.Snapshot.SourceHash), Kind = kind, Left = l, Right = r, ProposedType = type, Rationale = "shared_literal_key",
            Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = ["origine"], ParentCandidateId = null,
        };
    }

    /// <summary>Candidato di insiemi in esame in un'espansione (due Set).</summary>
    public static ReconcileCandidate SetFocus(DocumentAnalysisInput input)
    {
        var l = new CandidateEndpoint { Kind = CandidateEndpointKind.Set, Id = new string('5', 64), Hash = new string('6', 64) };
        var r = new CandidateEndpoint { Kind = CandidateEndpointKind.Set, Id = new string('7', 64), Hash = new string('8', 64) };
        return new()
        {
            CandidateId = ReconciliationIds.CandidateId(CandidateKind.Relation, l, r, ReconcileProposedType.SameRule, input.Snapshot.SourceHash), Kind = CandidateKind.Relation, Left = l, Right = r,
            ProposedType = ReconcileProposedType.SameRule, Rationale = "shared_literal_key", Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = ["origine"], ParentCandidateId = null,
        };
    }

    /// <summary>Item già proposti dalle foglie A usati come bersaglio di patch: i1 cita E1, i2 cita E3, i3 cita E4 ed E5 (dal ledger a cinque evidenze).</summary>
    public static IReadOnlyList<ItemProposal> PriorProposals(DocumentAnalysisInput input)
    {
        ItemProposal P(int k, params int[] evidence) => new()
        {
            LocalKey = $"leaf-A0.i{k}", Text = $"item {k}", Classification = DocumentAnalysisClassification.Documented,
            EvidenceIds = [.. ReconciliationIds.SortedIds(evidence.Select(n => input.Evidence[n - 1].EvidenceId)).Select(Guid.Parse)],
            SourceIds = [.. ReconciliationIds.SortedIds(evidence.SelectMany(n => input.Evidence[n - 1].Sources.Select(s => s.SourceId)).Distinct()).Select(Guid.Parse)],
        };

        return [P(1, 1), P(2, 3), P(3, 4, 5)];
    }

    public static ReconcileBatchWorld Create(ReconcileBatchSchema variant, ContractProfile profile = ContractProfile.V10, DocumentAnalysisInput? input = null, int[]? entries = null, ReconcileCandidate? focus = null)
    {
        input ??= DefaultInput();
        var runCatalog = AnalysisAliasCatalog.Build(input);
        var (stage, view, level) = variant switch
        {
            ReconcileBatchSchema.LeafA => ("leaf", ReconcileView.A, 0),
            ReconcileBatchSchema.LeafB => ("leaf", ReconcileView.B, 0),
            ReconcileBatchSchema.Reduce => ("reduce", ReconcileView.A, 1),
            ReconcileBatchSchema.Cross => ("cross", ReconcileView.Cross, 1),
            ReconcileBatchSchema.Expand => ("expand", ReconcileView.A, 0),
            ReconcileBatchSchema.Compare => ("compare", ReconcileView.A, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        var numbers = entries ?? variant switch
        {
            ReconcileBatchSchema.LeafA or ReconcileBatchSchema.LeafB => new[] { 1, 2, 3 },
            ReconcileBatchSchema.Expand => new[] { 3, 4, 5 },
            ReconcileBatchSchema.Compare => new[] { 3, 5 },
            _ => Array.Empty<int>(),
        };
        var nodes = variant is ReconcileBatchSchema.Reduce or ReconcileBatchSchema.Cross ? new[] { NodeOf('1', 0), NodeOf('2', 1) } : Array.Empty<CatalogNode>();
        var focusList = variant switch
        {
            ReconcileBatchSchema.Expand => new[] { focus ?? SetFocus(input) },
            ReconcileBatchSchema.Compare => new[] { focus ?? PairFocus(input, 3, 5, ReconcileProposedType.SameRule) },
            _ => null,
        };
        var request = new ReconcileBatchRequest(
            new DerivedBatchInput(BatchId, stage, view.ToString(), null, level, 0, [], "{}", 10, "measured", InputHash, "reconcile-alias-1", "document-reconcile-v3"),
            view, level, [.. numbers.Select(n => EntryOf(input, n))], nodes, focusList,
            profile != ContractProfile.V6 && variant == ReconcileBatchSchema.Expand ? new ExpansionGroups(new Dictionary<Guid, ExpansionTag>()) : null);

        var candidateStage = variant is ReconcileBatchSchema.Expand or ReconcileBatchSchema.Compare;
        var proposals = candidateStage && input.Evidence.Count >= 5 ? PriorProposals(input) : [];
        var items = proposals.Select(p => new ReconcileItemRef(p.LocalKey, p.Text, p.EvidenceIds)).ToList();
        return new ReconcileBatchWorld
        {
            Input = input, RunCatalog = runCatalog, Request = request, Catalog = ReconcileAliasCatalog.Build(runCatalog, request, items),
            Base = candidateStage ? DocumentAnalysisBase.Compute(proposals, input.Snapshot) : null, ValidationCatalog = new ReconciliationCatalog(nodes, [], []), Variant = variant, Profile = profile,
        };
    }

    /// <summary>Schema → compositore → validatore canonico → <c>InvokeAsync</c>. La catena si ferma al primo rifiuto del compositore (come l'invoker del piano).</summary>
    public ContractVerdict Evaluate(string json)
    {
        var schemaViolations = ReconcileSchemaSubset.Violations(Schema, json);
        var composition = ReconcileAliasComposer.Compose(json, Catalog, Request, Input.Snapshot, Base);
        if (!composition.IsValid)
        {
            return new ContractVerdict(schemaViolations, composition, [], null);
        }

        var validator = Validate(composition.Result!);
        var hierarchyError = InvokeProduction(composition.Result!);
        if ((validator.Count == 0) != (hierarchyError is null))
        {
            throw new InvalidOperationException($"Il validatore replicato dal test e ReconciliationHierarchy.InvokeAsync divergono: validator=[{string.Join(" | ", validator)}] hierarchy=[{hierarchyError}]");
        }

        return new ContractVerdict(schemaViolations, composition, validator, hierarchyError);
    }

    /// <summary>Replica elenco per elenco i controlli di <c>ReconciliationHierarchy.InvokeAsync</c> (che li unisce in un solo messaggio): serve a classificare i rifiuti. Confrontata con la produzione a ogni batch.</summary>
    private List<string> Validate(ReconcileResult composed)
    {
        var snapshot = Input.Snapshot;
        var result = ReconciliationCodec.DecodeResult(ReconciliationCodec.Encode(composed));
        var v = new List<string>();
        v.AddRange(ReconciliationContractRules.CheckResult(result, snapshot.SourceHash, Rules));
        v.AddRange(ReconciliationReferences.CheckResult(result, snapshot, ValidationCatalog));
        if (!result.VisitedEntryIds.SequenceEqual(Request.Entries.Select(e => e.EntryId).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            v.Add("visitedEntryIds: devono coincidere con le voci ricevute.");
        }

        if (!result.VisitedNodeIds.SequenceEqual(Request.Nodes.Select(n => n.NodeId).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            v.Add("visitedNodeIds: devono coincidere con i nodi ricevuti.");
        }

        if (BuildsNode != (result.Descriptor is not null))
        {
            v.Add(BuildsNode ? "descriptor: obbligatorio nei batch che costruiscono nodi." : "descriptor: null nei batch che non costruiscono nodi.");
        }

        v.AddRange(result.Candidates.Where(c => !c.OriginBatchIds.Contains(Request.Batch.BatchId, StringComparer.Ordinal)).Select(c => $"candidato {c.CandidateId}: originBatchIds deve citare il batch."));
        return [.. v.Distinct(StringComparer.Ordinal)];
    }

    private static readonly MethodInfo InvokeAsyncMethod = typeof(ReconciliationHierarchy).GetMethod("InvokeAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ReconciliationHierarchy.InvokeAsync non trovato: la firma interna è cambiata.");

    /// <summary>Messaggio del <see cref="DocumentWorkException"/> di <c>InvokeAsync</c> (riflessione sul metodo interno), oppure null se il batch è accettato.</summary>
    private string? InvokeProduction(ReconcileResult composed)
    {
        var text = ReconciliationCodec.Encode(composed);
        ReconcileInvoker invoker = (_, _) => new ValueTask<string>(text);
        var task = (ValueTask<BatchOutcome>)InvokeAsyncMethod.Invoke(
            null, [Request.Batch, Request.View, Request.Level, Request.Entries, Request.Nodes, Input.Snapshot, ValidationCatalog, invoker, BuildsNode, Request.Focus, CancellationToken.None, Request.Groups, Rules])!;
        try
        {
            _ = task.AsTask().GetAwaiter().GetResult();
            return null;
        }
        catch (DocumentWorkException e)
        {
            Assert.Equal(DocumentWorkException.WorkOutputInvalid, e.Code);
            return e.Message;
        }
    }
}

/// <summary>
/// Motore di riconciliazione reale (directory, gerarchia, <see cref="ReconciliationCandidates"/>) con un modello FINTO che risponde in alias <c>reconcile-alias-1</c> composto dal compositore di produzione,
/// come l'invoker di <c>DocumentReconcilePlan</c>. Nessun database. I batch di foglia e cross sono validi e minimi; il batch di confronto/espansione è fornito dal test.
/// </summary>
internal sealed class ReconcileEngineRig
{
    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static readonly ReconciliationContext Context = new("owner", "manifest", "project-analysis-documents-v2", new PlannerLimits(1_000_000, 1_000_000));

    private static readonly PackMeasurer<CatalogEntry> LeafMeasurer = (members, _) =>
        new ValueTask<UnitMeasure>(new UnitMeasure(10 + 5 * members.Count, TokenCountQuality.Measured, Sha("leaf|" + string.Join(',', members.Select(m => m.EntryId))), members.Count));

    private static readonly ReconciliationMeasurers Measurers = new(LeafMeasurer, (members, _) => new ValueTask<UnitMeasure>(new UnitMeasure(10, TokenCountQuality.Measured, Sha("page|" + members.Count), members.Count)));

    private static readonly PackMeasurer<CatalogNode> Descriptors = (members, _) =>
        new ValueTask<UnitMeasure>(new UnitMeasure(10 + 5 * members.Count, TokenCountQuality.Measured, Sha("nodes|" + string.Join(',', members.Select(m => m.NodeId))), members.Count));

    private readonly Func<ReconcileBatchRequest, string> _candidateStageDto;
    private readonly List<ReconcileItemRef> _items = [];
    private readonly List<ItemProposal> _proposals = [];

    private ReconcileEngineRig(DocumentAnalysisInput input, Func<ReconcileBatchRequest, string> candidateStageDto)
    {
        Input = input;
        RunCatalog = AnalysisAliasCatalog.Build(input);
        _candidateStageDto = candidateStageDto;
    }

    public DocumentAnalysisInput Input { get; }
    public AnalysisAliasCatalog RunCatalog { get; }
    public List<ReconcileBatchRequest> Calls { get; } = [];
    public ReconciliationDirectoryResult Directory { get; private set; } = null!;
    public HierarchyResult Hierarchy { get; private set; } = null!;
    public ReconciliationCandidates Engine { get; private set; } = null!;

    public int Compares => Calls.Count(c => c.Batch.Stage == "compare");

    public static async Task<ReconcileEngineRig> CreateAsync(Func<ReconcileBatchRequest, string> candidateStageDto, CancellationToken ct, DocumentAnalysisInput? input = null)
    {
        var rig = new ReconcileEngineRig(input ?? AliasFixtures.Input(), candidateStageDto);
        var snapshot = rig.Input.Snapshot;
        rig.Directory = await ReconciliationDirectory.BuildAsync(snapshot, new Dictionary<Guid, DirectoryFacts>(), Context, Measurers, ct);
        rig.Hierarchy = await ReconciliationHierarchy.BuildAsync(snapshot, rig.Directory, Context, Descriptors, rig.InvokeAsync, ct);
        rig.Engine = new ReconciliationCandidates(snapshot, rig.Directory, rig.Hierarchy, Context, LeafMeasurer, rig.InvokeAsync);
        return rig;
    }

    /// <summary>Analisi assemblata dal server (<c>DocumentReconcileAssembly.Assemble</c>, <c>internal</c>: chiamata per riflessione) sullo stato corrente del motore.</summary>
    public DocumentAnalysisContent Assemble()
    {
        var run = Engine.Result();
        var final = new FinalPassResult(FinalPassOutcome.Complete, null, null, [], run, true);
        var assembly = typeof(ReconciliationHierarchy).Assembly;
        var engineResult = Activator.CreateInstance(assembly.GetType("bProjects.App.AI.ReconcileEngineResult")!, Directory, Hierarchy, run, final)!;
        var assemble = assembly.GetType("bProjects.App.AI.DocumentReconcileAssembly")!.GetMethod("Assemble", BindingFlags.Public | BindingFlags.Static)!;
        var assembled = assemble.Invoke(null, [Input, engineResult, null])!;
        return (DocumentAnalysisContent)assembled.GetType().GetProperty("Analysis")!.GetValue(assembled)!;
    }

    private ValueTask<string> InvokeAsync(ReconcileBatchRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        var dto = request.Batch.Stage is "expand" or "compare" ? _candidateStageDto(request) : StructuralDto(request);
        var catalog = ReconcileAliasCatalog.Build(RunCatalog, request, _items);
        var baseState = request.Batch.Stage is "expand" or "compare" ? DocumentAnalysisBase.Compute(_proposals, Input.Snapshot) : null;
        var composed = ReconcileAliasComposer.Compose(dto, catalog, request, Input.Snapshot, baseState);
        if (!composed.IsValid)
        {
            throw new DocumentWorkException(composed.ErrorCode ?? DocumentWorkException.WorkOutputInvalid, string.Join(" | ", composed.Violations));
        }

        foreach (var proposal in composed.Result!.ItemProposals)
        {
            _items.Add(new ReconcileItemRef(proposal.LocalKey, proposal.Text, proposal.EvidenceIds));
            _proposals.Add(proposal);
        }

        return new ValueTask<string>(ReconciliationCodec.Encode(composed.Result));
    }

    /// <summary>Foglie e cross: output valido e minimo (item per ogni voce nella foglia A; altrove solo la spiegazione «nessun collegamento»).</summary>
    private string StructuralDto(ReconcileBatchRequest request)
    {
        var o = ReconcileContractMatrixTests.Dto(noFindings: "nessun collegamento emerso", descriptor: request.Batch.Stage is "leaf" or "reduce" ? "gruppo" : null);
        if (request.Batch.Stage == "leaf" && request.View == ReconcileView.A)
        {
            var items = new JsonArray();
            var k = 1;
            foreach (var entry in request.Entries)
            {
                var evidence = Input.Evidence.Single(e => e.EvidenceId == entry.EvidenceId);
                items.Add(JsonNode.Parse(JsonSerializer.Serialize(new
                {
                    key = $"i{k++}", section = "requirements", text = evidence.Statement,
                    classification = evidence.Origin == EvidenceOriginKind.Message ? "Declared" : "Documented", evidence = new[] { RunCatalog.EvidenceById(entry.EvidenceId)!.Alias },
                })));
            }

            o["items"] = items;
            o["noFindings"] = null;
        }

        return o.ToJsonString();
    }
}

/// <summary>
/// RH-002 (reconciliation-hardening, M0) — matrice di contratto eseguibile: CARATTERIZZA il comportamento ATTUALE di schema, compositore, validatore e motore per i nove casi del §4.1/§4.2
/// dell'assessment, per la definizione v10 (profilo -3, schema per stadio) e per la v6 (schema unico). Nessun database, nessuna inferenza, nessuna modifica del codice di produzione: ogni asserzione
/// descrive ciò che il codice fa OGGI, incoerenze comprese. Se un esito cambia, la diagnosi dell'assessment va rivista; la regola non si allenta per far passare il test.
/// </summary>
public sealed class ReconcileContractMatrixTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string NoFindingsRequired = "noFindingsReason: obbligatorio quando non ci sono proposte, candidati né patch.";
    private const string OpenWithoutReason = "focus.reason: obbligatorio e non vuoto quando l'esito è Open.";

    /// <summary>DTO <c>reconcile-alias-1</c> vuoto (nessun item, candidato, focus, patch) con descriptor e spiegazione indicati.</summary>
    internal static JsonObject Dto(string? noFindings = null, string? descriptor = null, Action<JsonObject>? change = null)
    {
        var o = new JsonObject
        {
            ["contract"] = "reconcile-alias-1", ["descriptor"] = descriptor, ["noFindings"] = noFindings, ["items"] = new JsonArray(), ["candidates"] = new JsonArray(), ["focus"] = null,
            ["patches"] = new JsonArray(),
        };
        change?.Invoke(o);
        return o;
    }

    private static JsonNode Cand(string key, string left, string right, string type) =>
        JsonNode.Parse(JsonSerializer.Serialize(new { key, left, right, type, basis = "Inferred", rationale = "numeri diversi" }))!;

    private static JsonNode Item(string key, string section, string classification, params string[] evidence) =>
        JsonNode.Parse(JsonSerializer.Serialize(new { key, section, text = "testo " + key, classification, evidence }))!;

    private static JsonNode Focus(string outcome, string? reason) => JsonNode.Parse(JsonSerializer.Serialize(new { outcome, reason }))!;

    private static string Admitted(ContractVerdict v) => "lo schema inviato deve ammettere il DTO: " + string.Join(" | ", v.SchemaViolations);

    // ================================================================ 1. I-1: confronto Open con motivo, 0 candidati, noFindings null

    [Theory]
    [InlineData(ContractProfile.V10)]
    [InlineData(ContractProfile.V6)]
    public void Case1_a_compare_open_with_reason_zero_candidates_and_null_no_findings_is_admitted_by_the_schema_and_rejected_by_the_validator(ContractProfile profile)
    {
        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.Compare, profile);
        var json = Dto(change: o => o["focus"] = Focus("Open", "30 giorni contro 45")).ToJsonString();

        var verdict = world.Evaluate(json);

        Assert.True(verdict.SchemaAdmits, Admitted(verdict));
        Assert.True(verdict.Composition.IsValid, verdict.Report);
        var disposition = Assert.Single(verdict.Composition.Result!.Dispositions);
        Assert.Equal((CandidateState.Open, "30 giorni contro 45"), (disposition.State, disposition.Reason));
        Assert.Empty(verdict.Composition.Result.Candidates);
        Assert.Null(verdict.Composition.Result.NoFindingsReason);
        Assert.Equal([NoFindingsRequired], verdict.ValidatorViolations);
        Assert.NotNull(verdict.HierarchyError);
        Assert.Contains(NoFindingsRequired, verdict.HierarchyError);
        Assert.False(verdict.Accepted);

        // La regola conta i campi, non gli esiti: anche un confronto Compared senza candidati è «senza risultati»; con una spiegazione qualsiasi lo stesso Open è accettato.
        var compared = world.Evaluate(Dto(change: o => o["focus"] = Focus("Compared", null)).ToJsonString());
        Assert.True(compared.SchemaAdmits, Admitted(compared));
        Assert.Equal([NoFindingsRequired], compared.ValidatorViolations);
        var explained = world.Evaluate(Dto(noFindings: "esito nel focus", change: o => o["focus"] = Focus("Open", "30 giorni contro 45")).ToJsonString());
        Assert.True(explained.Accepted, explained.Report);
    }

    // ================================================================ 2. I-2: confronto Open con reason null

    [Theory]
    [InlineData(ContractProfile.V10)]
    [InlineData(ContractProfile.V6)]
    public void Case2_a_compare_open_with_null_reason_is_admitted_by_the_schema_and_rejected_by_the_composer(ContractProfile profile)
    {
        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.Compare, profile);

        foreach (var reason in new string?[] { null, "  " })
        {
            var verdict = world.Evaluate(Dto(change: o => o["focus"] = Focus("Open", reason)).ToJsonString());

            Assert.True(verdict.SchemaAdmits, Admitted(verdict));
            Assert.False(verdict.Composition.IsValid);
            Assert.Equal(DocumentWorkException.WorkOutputInvalid, verdict.Composition.ErrorCode);
            Assert.Equal([OpenWithoutReason], verdict.ComposerViolations);
            Assert.Empty(verdict.ValidatorViolations);   // il validatore non è nemmeno raggiunto: il piano lancia al compositore
        }
    }

    // ================================================================ 3. I-3: un Contradicts emesso nel confronto è risolto per cache come Compared (conflitto perso)

    [Theory]
    [InlineData(ContractProfile.V10)]
    [InlineData(ContractProfile.V6)]
    public async Task Case3_a_contradicts_candidate_on_the_same_pair_in_a_compared_comparison_becomes_compared_through_the_pair_cache_without_any_call(ContractProfile profile)
    {
        var input = AliasFixtures.Input();
        var focus = ReconcileBatchWorld.PairFocus(input, 3, 5, ReconcileProposedType.SameRule);
        var json = Dto(change: o =>
        {
            o["focus"] = Focus("Compared", null);
            o["candidates"] = new JsonArray(Cand("f1", "E3", "E5", "Contradicts"));
        }).ToJsonString();

        // Batch: schema, compositore e validatore accettano. Il candidato figlio cita il candidato in esame come padre.
        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.Compare, profile, input, focus: focus);
        var verdict = world.Evaluate(json);
        Assert.True(verdict.SchemaAdmits, Admitted(verdict));
        Assert.True(verdict.Accepted, verdict.Report);
        var child = Assert.Single(verdict.Composition.Result!.Candidates);
        Assert.Equal((CandidateKind.Conflict, ReconcileProposedType.Contradicts, focus.CandidateId), (child.Kind, child.ProposedType, child.ParentCandidateId));
        Assert.NotEqual(focus.CandidateId, child.CandidateId);

        // Motore: il confronto del padre registra la coppia come Compared; il figlio Contradicts, Pending, è chiuso per cache hit senza chiamare il modello.
        var rig = await ReconcileEngineRig.CreateAsync(_ => json, Ct);
        rig.Engine.Add(focus, ReconcileView.A);
        await rig.Engine.ProcessAsync(focus.CandidateId, Ct);
        var parentDisposition = rig.Engine.Disposition(focus.CandidateId);
        Assert.Equal(CandidateState.Compared, parentDisposition.State);
        Assert.Equal(1, rig.Compares);
        var pending = Assert.Single(rig.Engine.Pending);
        Assert.Equal(child.CandidateId, pending);

        await rig.Engine.ProcessAsync(pending, Ct);

        var childDisposition = rig.Engine.Disposition(child.CandidateId);
        Assert.Equal(CandidateState.Compared, childDisposition.State);                                   // un conflitto Contradicts mai confrontato per conto suo
        Assert.Equal(parentDisposition.ComparisonOutputHashes, childDisposition.ComparisonOutputHashes);   // eredita l'hash del confronto del padre
        Assert.Equal(1, rig.Compares);                                                                    // nessuna seconda chiamata
        Assert.Equal(1, rig.Engine.Calls.Used);
        Assert.Empty(rig.Engine.Result().Unresolved);
        Assert.DoesNotContain(rig.Engine.Result().Dispositions, d => d.State == CandidateState.Open);
        Assert.Empty(rig.Assemble().Conflicts);                                                           // il conflitto dichiarato dal modello non arriva al PM
    }

    // ================================================================ 4. I-3 (forma 029): DependsOn invertito + Open ⇒ due conflitti per la stessa coppia

    [Theory]
    [InlineData(ContractProfile.V10)]
    [InlineData(ContractProfile.V6)]
    public async Task Case4_an_inverted_depends_on_candidate_in_an_open_comparison_inherits_open_and_the_assembly_produces_two_conflicts_for_the_same_pair(ContractProfile profile)
    {
        var input = AliasFixtures.Input();
        var focus = ReconcileBatchWorld.PairFocus(input, 3, 5, ReconcileProposedType.DependsOn);   // E3 DependsOn E5
        const string reason = "30 giorni contro 45";
        var json = Dto(change: o =>
        {
            o["focus"] = Focus("Open", reason);
            o["candidates"] = new JsonArray(Cand("f1", "E5", "E3", "DependsOn"));                   // E5 DependsOn E3: stessa coppia, direzione inversa, identità diversa
        }).ToJsonString();

        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.Compare, profile, input, focus: focus);
        var verdict = world.Evaluate(json);
        Assert.True(verdict.SchemaAdmits, Admitted(verdict));
        Assert.True(verdict.Accepted, verdict.Report);
        var child = Assert.Single(verdict.Composition.Result!.Candidates);
        Assert.NotEqual(focus.CandidateId, child.CandidateId);
        Assert.Equal((focus.Right.Id, focus.Left.Id), (child.Left.Id, child.Right.Id));

        var rig = await ReconcileEngineRig.CreateAsync(_ => json, Ct);
        rig.Engine.Add(focus, ReconcileView.A);
        await rig.Engine.ProcessAsync(focus.CandidateId, Ct);
        Assert.Equal((CandidateState.Open, reason), (rig.Engine.Disposition(focus.CandidateId).State, rig.Engine.Disposition(focus.CandidateId).Reason));
        var pending = Assert.Single(rig.Engine.Pending);
        Assert.Equal(child.CandidateId, pending);

        await rig.Engine.ProcessAsync(pending, Ct);

        var childDisposition = rig.Engine.Disposition(child.CandidateId);
        Assert.Equal((CandidateState.Open, reason), (childDisposition.State, childDisposition.Reason));    // il figlio eredita Open e il motivo senza una chiamata
        Assert.Equal(1, rig.Compares);
        var open = rig.Engine.Result().Dispositions.Where(d => d.State == CandidateState.Open).ToList();
        Assert.Equal(2, open.Count);

        var conflicts = rig.Assemble().Conflicts;
        Assert.Equal(2, conflicts.Count);                                                                   // due conflitti per la stessa coppia di evidenze
        Assert.NotEqual(conflicts[0].Id, conflicts[1].Id);
        Assert.Equal(conflicts[0].Alternatives.Select(a => a.Text).Order(), conflicts[1].Alternatives.Select(a => a.Text).Order());
        Assert.All(conflicts, c => Assert.Equal(reason, c.Description));
    }

    // ================================================================ 5. I-1 latente: blocco di espansione vuoto

    [Theory]
    [InlineData(ContractProfile.V10)]
    [InlineData(ContractProfile.V6)]
    public void Case5_an_empty_expansion_block_with_null_focus_and_null_no_findings_is_admitted_by_the_schema_and_rejected_by_the_validator(ContractProfile profile)
    {
        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.Expand, profile);

        var verdict = world.Evaluate(Dto().ToJsonString());

        Assert.True(verdict.SchemaAdmits, Admitted(verdict));
        Assert.True(verdict.Composition.IsValid, verdict.Report);
        Assert.Empty(verdict.Composition.Result!.Dispositions);
        Assert.Equal([NoFindingsRequired], verdict.ValidatorViolations);
        Assert.NotNull(verdict.HierarchyError);
        Assert.Contains(NoFindingsRequired, verdict.HierarchyError);
        Assert.False(verdict.Accepted);

        Assert.True(world.Evaluate(Dto(noFindings: "nessun collegamento nel blocco").ToJsonString()).Accepted);
    }

    // ================================================================ 6. Foglia B senza candidati né spiegazione

    [Theory]
    [InlineData(ContractProfile.V10)]
    [InlineData(ContractProfile.V6)]
    public void Case6_a_leaf_b_without_candidates_and_with_null_no_findings_is_admitted_by_the_schema_and_rejected_by_the_validator(ContractProfile profile)
    {
        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafB, profile);

        var verdict = world.Evaluate(Dto(descriptor: "gruppo").ToJsonString());

        Assert.True(verdict.SchemaAdmits, Admitted(verdict));
        Assert.True(verdict.Composition.IsValid, verdict.Report);
        Assert.Equal([NoFindingsRequired], verdict.ValidatorViolations);
        Assert.Contains(NoFindingsRequired, verdict.HierarchyError);
        Assert.False(verdict.Accepted);

        Assert.True(world.Evaluate(Dto(noFindings: "nessun collegamento emerso", descriptor: "gruppo").ToJsonString()).Accepted);
    }

    // ================================================================ 7. Forma dei confronti del Run 4: candidato ridondante più Open

    [Theory]
    [InlineData(ContractProfile.V10)]
    [InlineData(ContractProfile.V6)]
    public async Task Case7_the_run_4_comparison_shape_a_redundant_candidate_plus_open_is_accepted_and_the_redundant_candidate_adds_nothing(ContractProfile profile)
    {
        var input = AliasFixtures.Input();
        var focus = ReconcileBatchWorld.PairFocus(input, 3, 5, ReconcileProposedType.SameRule);
        var json = Dto(change: o =>
        {
            o["focus"] = Focus("Open", "30 giorni contro 45");
            o["candidates"] = new JsonArray(Cand("f1", "E3", "E5", "SameRule"));                  // la stessa coppia e lo stesso tipo del candidato in esame
        }).ToJsonString();

        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.Compare, profile, input, focus: focus);
        var verdict = world.Evaluate(json);
        Assert.True(verdict.SchemaAdmits, Admitted(verdict));
        Assert.True(verdict.Accepted, verdict.Report);
        Assert.Equal(focus.CandidateId, Assert.Single(verdict.Composition.Result!.Candidates).CandidateId);   // stessa identità canonica del candidato in esame

        var rig = await ReconcileEngineRig.CreateAsync(_ => json, Ct);
        rig.Engine.Add(focus, ReconcileView.A);
        await rig.Engine.ProcessAsync(focus.CandidateId, Ct);

        Assert.Equal(CandidateState.Open, rig.Engine.Disposition(focus.CandidateId).State);
        Assert.Empty(rig.Engine.Pending);                                                          // il candidato uguale al padre è scartato dal motore, nessun figlio
        Assert.Single(rig.Engine.Result().Candidates);
        Assert.Equal(1, rig.Compares);
        Assert.Single(rig.Assemble().Conflicts);
    }

    // ================================================================ 8. I-4: Gap resa come requirement

    [Theory]
    [InlineData(ContractProfile.V10)]
    [InlineData(ContractProfile.V6)]
    public void Case8_a_leaf_a_that_renders_a_gap_as_a_requirement_is_accepted_by_the_composer_and_the_validator(ContractProfile profile)
    {
        var input = ReconcileBatchWorld.InputWithGap();
        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafA, profile, input, [1, 2, 6]);
        var gap = input.Evidence[5];
        Assert.Equal(EvidenceKind.Gap, gap.Kind);
        var json = Dto(descriptor: "gruppo", change: o => o["items"] = new JsonArray(
            Item("i1", "goals", "Declared", "E1"), Item("i2", "requirements", "Documented", "E2"), Item("i3", "requirements", "Declared", "E6"))).ToJsonString();

        var verdict = world.Evaluate(json);

        Assert.True(verdict.SchemaAdmits, Admitted(verdict));
        Assert.True(verdict.Accepted, verdict.Report);
        var asRequirement = Assert.Single(verdict.Composition.Result!.ItemProposals, p => p.EvidenceIds.Contains(gap.EvidenceId));
        Assert.Equal(AiSection.Requirements, verdict.Composition.Sections[asRequirement.LocalKey]);
        Assert.Empty(verdict.Composition.Ungrounded);                                               // nessuna domanda aperta per la Gap: nulla nel contratto la richiede

        // Controllo: la stessa Gap resa come domanda con evidenza è altrettanto accettata; il contratto della foglia non distingue.
        var question = Dto(descriptor: "gruppo", change: o => o["items"] = new JsonArray(
            Item("i1", "goals", "Declared", "E1"), Item("i2", "requirements", "Documented", "E2"), Item("i3", "openQuestions", "Declared", "E6")));
        Assert.True(world.Evaluate(question.ToJsonString()).Accepted);
    }

    // ================================================================ 9. I-5: openQuestions senza evidenze e una voce non citata

    [Theory]
    [InlineData(ContractProfile.V10)]
    [InlineData(ContractProfile.V6)]
    public void Case9_a_leaf_a_with_an_ungrounded_open_question_and_an_uncited_entry_is_accepted_by_the_composer_and_the_validator(ContractProfile profile)
    {
        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafA, profile);
        var uncited = Dto(descriptor: "gruppo", change: o => o["items"] = new JsonArray(
            Item("i1", "requirements", "Declared", "E1"), Item("i2", "requirements", "Documented", "E2"), Item("i3", "openQuestions", "Assumed"))).ToJsonString();
        var covered = Dto(descriptor: "gruppo", change: o => o["items"] = new JsonArray(
            Item("i1", "requirements", "Declared", "E1"), Item("i2", "requirements", "Documented", "E2"), Item("i3", "requirements", "Documented", "E3"),
            Item("i4", "openQuestions", "Assumed"))).ToJsonString();

        var verdict = world.Evaluate(uncited);

        Assert.True(verdict.SchemaAdmits, Admitted(verdict));
        Assert.True(verdict.Accepted, verdict.Report);
        var question = Assert.Single(verdict.Composition.Ungrounded);
        Assert.Equal(AiSection.OpenQuestions, question.Section);
        Assert.Equal(2, verdict.Composition.Result!.ItemProposals.Count);
        Assert.Equal(3, verdict.Composition.Result.VisitedEntryIds.Count);                           // il batch dichiara tre voci visitate…
        var cited = verdict.Composition.Result.ItemProposals.SelectMany(p => p.EvidenceIds).ToHashSet();
        Assert.DoesNotContain(world.Input.Evidence[2].EvidenceId, cited);                              // …ma E3 non ha alcun item che la citi (evidence_not_mapped a fine gerarchia)

        // Con tutte le voci citate e la stessa domanda senza evidenze l'esito è lo stesso: l'accettazione non dipende dalla copertura.
        Assert.True(world.Evaluate(covered).Accepted);
    }

    // ================================================================ schemi ammessi ma esiti diversi: schema e compositore non coincidono

    [Fact]
    public void The_v6_single_schema_and_the_v10_stage_schemas_admit_the_same_dtos_of_the_nine_cases_except_where_the_stage_schema_is_stricter()
    {
        // La v6 invia sempre lo stesso schema; la v10 uno per stadio. Un Open senza motivo e un blocco vuoto sono ammessi da entrambi: la stretta del -3 non riguarda questi casi.
        var open = Dto(change: o => o["focus"] = Focus("Open", null)).ToJsonString();
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileAliasContracts.SchemaOfV3(ReconcileBatchSchema.Compare), open));
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileAliasContracts.JsonSchema(), open));
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileAliasContracts.SchemaOfV3(ReconcileBatchSchema.Expand), Dto().ToJsonString()));
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileAliasContracts.JsonSchema(), Dto().ToJsonString()));

        // Dove il -3 è più stretto: la v6 ammette un confronto con focus null, la v10 no (e il compositore lo rifiuta comunque).
        var noFocus = Dto(noFindings: "x").ToJsonString();
        Assert.NotEmpty(ReconcileSchemaSubset.Violations(ReconcileAliasContracts.SchemaOfV3(ReconcileBatchSchema.Compare), noFocus));
        Assert.Empty(ReconcileSchemaSubset.Violations(ReconcileAliasContracts.JsonSchema(), noFocus));
        Assert.Contains("focus: il confronto deve dichiarare Compared oppure Open per il candidato in esame.", ReconcileBatchWorld.Create(ReconcileBatchSchema.Compare, ContractProfile.V6).Evaluate(noFocus).ComposerViolations);
    }
}
