using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// Modello FINTO della gerarchia per la simulazione del costo dell'espansione v10 (RH-005): risposte valide e deterministiche per ogni stadio. Foglie: descriptor, un item per voce nella vista A
/// (come la v10 reale, 1:1 con le evidenze) e 0–3 candidati Evidence–Evidence FISSI (funzione di ordinale e vista, uguali per ogni c). Riduzione e Cross: un candidato di gruppo (Node–Node) fra i primi due
/// nodi ricevuti. Espansione: <c>c</c> figli Evidence–Evidence per blocco (coppie rilevanti del blocco, a rotazione sull'ordinale locale del blocco; tutte se sono meno di <c>c</c>). Confronto: <c>Compared</c>.
/// </summary>
internal sealed class SimulatedReconcileModel(DocumentAnalysisInput input, PureReconcileMeasure measure, int childrenPerBlock, bool itemsOnLeafA, bool disjointChildren)
{
    private readonly Dictionary<Guid, DocumentAnalysisEvidence> _evidence = input.Evidence.ToDictionary(e => e.EvidenceId);

    public List<ReconcileBatchRequest> Calls { get; } = [];

    /// <summary>Chiamata alla prima richiesta di espansione di ogni candidato di gruppo (ordinale locale 0), PRIMA di rispondere: lì lo stato del motore è quello su cui ha deciso il precheck.</summary>
    public Action<ReconcileBatchRequest>? OnFirstBlock { get; set; }

    private CandidateEndpoint Evidence(Guid id) => new() { Kind = CandidateEndpointKind.Evidence, Id = id.ToString("D"), Hash = _evidence[id].ContentHash };

    private ReconcileCandidate Candidate(CandidateKind kind, CandidateEndpoint left, CandidateEndpoint right, ReconcileProposedType type, string batchId, string? parent) => new()
    {
        CandidateId = ReconciliationIds.CandidateId(kind, left, right, type, input.Snapshot.SourceHash), Kind = kind, Left = left, Right = right, ProposedType = type,
        Rationale = "collegamento simulato", Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = [batchId], ParentCandidateId = parent,
    };

    public ValueTask<string> Invoke(ReconcileBatchRequest q, CancellationToken ct)
    {
        Calls.Add(q);
        var entries = q.Entries;
        IReadOnlyList<ReconcileCandidate> candidates = [];
        IReadOnlyList<ItemProposal> items = [];
        IReadOnlyList<CandidateDisposition> dispositions = [];
        string? descriptor = q.Batch.Stage is "leaf" or "reduce" ? "gruppo " + q.Batch.BatchId[..8] : null;
        switch (q.Batch.Stage)
        {
            case "leaf":
                if (q.View == ReconcileView.A && itemsOnLeafA)
                {
                    items =
                    [
                        .. entries.Select(e => new ItemProposal
                        {
                            LocalKey = "k-" + e.EvidenceId.ToString("N"), Text = _evidence[e.EvidenceId].Statement, Classification = DocumentAnalysisClassification.Documented,
                            EvidenceIds = [e.EvidenceId], SourceIds = e.SourceIds,
                        }),
                    ];
                }

                var fixedCount = (q.Batch.Ordinal + (q.View == ReconcileView.B ? 1 : 0)) % 4;   // 0–3, fissi e indipendenti da c
                candidates = [.. Enumerable.Range(0, fixedCount).Where(j => 2 * j + 1 < entries.Count).Select(j => Candidate(CandidateKind.Relation, Evidence(entries[2 * j].EvidenceId), Evidence(entries[2 * j + 1].EvidenceId), ReconcileProposedType.SameRule, q.Batch.BatchId, null))];
                break;
            case "reduce" or "cross":
                candidates = [Candidate(CandidateKind.Relation, Node(q.Nodes[0]), Node(q.Nodes[1]), ReconcileProposedType.SameRule, q.Batch.BatchId, null)];
                break;
            case "expand":
                if (q.Batch.Ordinal == 0)
                {
                    OnFirstBlock?.Invoke(q);
                }

                candidates = Children(q);
                break;
            case "compare":
                dispositions =
                [
                    new CandidateDisposition { CandidateId = q.Focus!.Single().CandidateId, State = CandidateState.Compared, ChildCandidateIds = [], ComparisonOutputHashes = [q.Batch.InputHash], Reason = null },
                ];
                break;
        }

        var result = new ReconcileResult
        {
            BatchId = q.Batch.BatchId, InputHash = q.Batch.InputHash, View = q.View, Level = q.Level,
            VisitedEntryIds = [.. entries.Select(e => e.EntryId).Order(StringComparer.Ordinal)], VisitedNodeIds = [.. q.Nodes.Select(n => n.NodeId).Order(StringComparer.Ordinal)],
            Candidates = candidates, Dispositions = dispositions, ItemProposals = items, Patches = [], Descriptor = q.Batch.Stage == "cross" ? null : descriptor,
            NoFindingsReason = items.Count == 0 && candidates.Count == 0 ? "nessun collegamento emerso" : null,   // v10 (I-1): senza proposte né candidati il motivo è obbligatorio, anche nel confronto
        };
        if (q.Batch.Stage == "leaf" && q.View == ReconcileView.A)
        {
            measure.Items.AddRange(items.Select(i => new ReconcileItemRef(i.LocalKey, i.Text, i.EvidenceIds)));
        }

        return new(ReconciliationCodec.Encode(result));
    }

    private static CandidateEndpoint Node(CatalogNode node) => new() { Kind = CandidateEndpointKind.Node, Id = node.NodeId, Hash = node.MemberSetHash };

    /// <summary>Figli del blocco: <c>c</c> coppie rilevanti (una voce del gruppo sinistro e una del destro, distinte) a rotazione sull'ordinale del blocco.</summary>
    private List<ReconcileCandidate> Children(ReconcileBatchRequest q)
    {
        if (childrenPerBlock == 0)
        {
            return [];
        }

        var tags = q.Groups!.Tags;
        var entries = q.Entries;
        var pairs = new List<(Guid A, Guid B)>();
        var seen = new HashSet<(Guid, Guid)>();
        for (var i = 0; i < entries.Count; i++)
        {
            for (var j = 0; j < entries.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                var left = tags[entries[i].EvidenceId] is ExpansionTag.Left or ExpansionTag.Both;
                var right = tags[entries[j].EvidenceId] is ExpansionTag.Right or ExpansionTag.Both;
                var key = string.CompareOrdinal(entries[i].EvidenceId.ToString("D"), entries[j].EvidenceId.ToString("D")) < 0 ? (entries[i].EvidenceId, entries[j].EvidenceId) : (entries[j].EvidenceId, entries[i].EvidenceId);
                if (left && right && seen.Add(key))
                {
                    pairs.Add(key);
                }
            }
        }

        if (pairs.Count == 0)
        {
            return [];
        }

        var take = Math.Min(childrenPerBlock, pairs.Count);
        var parent = q.Focus!.Single().CandidateId;
        // Politica dei figli: «condivisi» = stessa rotazione per ogni candidato (due espansioni dello stesso spazio di coppie scelgono le stesse coppie: la cache delle coppie del motore evita i confronti ripetuti);
        // «disgiunti» = rotazione diversa per candidato (nessuna coppia in comune fra espansioni diverse: nessun riuso, costo dei confronti al massimo).
        var offset = disjointChildren ? BitConverter.ToUInt32(Convert.FromHexString(parent[..8])) : 0u;
        var start = (int)(((long)q.Batch.Ordinal * childrenPerBlock + offset) % pairs.Count);
        return [.. Enumerable.Range(0, take).Select(t => pairs[(start + t) % pairs.Count]).Select(p => Candidate(CandidateKind.Relation, Evidence(p.A), Evidence(p.B), ReconcileProposedType.SameRule, q.Batch.BatchId, parent))];
    }
}

/// <summary>Esito di UNA simulazione completa del motore puro (directory, gerarchia, fasi dei candidati, round finale) con il modello finto.</summary>
internal sealed record ExpansionSimulation(
    int ChildrenPerBlock, bool Items, bool Disjoint, IReadOnlyList<string> LeafSizes, int HierarchyCalls, int EngineCalls, IReadOnlyDictionary<string, int> CallsByStage, IReadOnlyList<GroupExpansion> Groups,
    IReadOnlyDictionary<string, int> NotProcessedByReason, int NotProcessed, FinalPassOutcome? Final, string? FinalCode, int Unresolved, string? Exception, string Fingerprint)
{
    public int TotalCalls => HierarchyCalls + EngineCalls;

    public int Blocks => Groups.Sum(g => g.Blocks);

    public int ExpandCalls => CallsByStage.GetValueOrDefault("expand");

    public int CompareCalls => CallsByStage.GetValueOrDefault("compare");

    /// <summary>Candidati di gruppo rifiutati dal precheck del budget di chiamate (<c>expansion_budget_exceeded</c>).</summary>
    public int PrecheckRefusals => NotProcessedByReason.GetValueOrDefault(ReconciliationCandidates.ExpansionBudgetExceeded);

    /// <summary>Sintesi canonica pinnata dai test: blocchi per candidato in ordine di elaborazione, chiamate per stadio, NotProcessed per motivo, esito del round finale e candidati non terminali.</summary>
    public string Summary =>
        $"blocks=[{string.Join(',', Groups.OrderBy(g => g.Order).Select(g => g.Blocks))}] calls={TotalCalls} (hier {HierarchyCalls}, expand {ExpandCalls}, compare {CompareCalls}) "
        + $"notProcessed={NotProcessed}[{string.Join(',', NotProcessedByReason.Select(kv => kv.Key + "=" + kv.Value))}] final={(Final?.ToString() ?? "exception")} unresolved={Unresolved}";
}

/// <summary>Un candidato di gruppo (Node o Set) e la sua espansione: lati, blocchi, disposizione, figli e budget residuo su cui il precheck ha deciso.</summary>
internal sealed record GroupExpansion(
    string CandidateId, string Kind, string Origin, int Order, int LeftMembers, int RightMembers, int Blocks, int Children, CandidateState State, string? Reason, int? Available)
{
    /// <summary>Chiamate richieste dal precheck del motore: blocchi × (1 espansione + tetto di 16 confronti dei figli), indipendente da <c>c</c>.</summary>
    public int Required => Blocks * (1 + ReconcileAliasContracts.ExpansionCandidateCap);
}

/// <summary>
/// 002 reconciliation-hardening RH-005 (T159) — simulazione OFFLINE del costo dell'espansione v10 con il motore PURO di produzione (<see cref="ReconciliationDirectory"/>, gerarchia, <see cref="ReconciliationCandidates"/>
/// con <see cref="ExpansionPolicy"/> e round finale) e un <see cref="ReconcileInvoker"/> finto, sul ledger di RH-004 con <c>DirectoryFacts</c> deterministici. Profilo W=32768, O=16384, M=1024; tetto di chiamate del run 512,
/// una riservata allo stadio Gap. Nessuna modifica dell'algoritmo: sono misure, i valori pinnati sono quelli osservati.
/// </summary>
[Trait("Category", "Offline")]
public sealed class ReconcileExpansionCostSimulationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string V10 = DocumentAnalysisWorkflow.DefinitionVersionV10;

    /// <summary>Chiamate riservate allo stadio Gap: valore di <c>DocumentReconcilePlan.ExpansionReservedCalls</c> (costante interna, congelata nel configHash della v10; verificata per riflessione da un test).</summary>
    internal const int ExpansionReservedCalls = 1;

    internal static readonly int[] ChildrenPerBlock = [0, 1, 2, 3, 5, 16];

    public static TheoryData<int> Children() => new(ChildrenPerBlock);

    internal static async Task<ExpansionSimulation> SimulateAsync(int children, bool items, bool disjoint, CancellationToken ct)
    {
        var input = ReconcileMeasurementBudgetTests.SingleInput();
        var facts = MeasurementLedger.DeterministicFacts(input);
        int inputBudget, outputTokens;
        using (var rig = await MeasurementRig.CreateAsync(ct))
        {
            (inputBudget, outputTokens) = (rig.Profile.InputBudgetTokens, rig.Profile.OutputTokens);
        }

        var catalog = AnalysisAliasCatalog.Build(input, DocumentAnalysisWorkflow.SchemaVersionV3);
        var measure = new PureReconcileMeasure(input, catalog, V10);
        var model = new SimulatedReconcileModel(input, measure, children, items, disjoint);
        var limits = new DocumentOptions().ToLimits();                     // MaxAiCallsPerRun = 512, MaxCandidatesPerRun = 100.000, MaxDerivedBytes = 250.000.000
        var context = new ReconciliationContext(
            "owner", "manifest", V10, new PlannerLimits(Math.Max(1, inputBudget - 256), outputTokens, OutputExpansion: 1.0), ReconcileAliasContracts.SchemaVersion,
            DocumentAnalysisWorkflow.ReconcilePromptOf(V10), limits.MaxReconciliationLevels);
        var snapshot = input.Snapshot;

        var directory = await ReconciliationDirectory.BuildAsync(snapshot, facts, context, measure.Measurers, ct);
        var hierarchy = await ReconciliationHierarchy.BuildAsync(snapshot, directory, context, measure.Node, model.Invoke, ct);
        measure.CandidatePhase = true;
        var engine = new ReconciliationCandidates(
            snapshot, directory, hierarchy, context, measure.Leaf, model.Invoke, new ReconciliationLimits(limits.MaxAiCallsPerRun, limits.MaxCandidatesPerRun, limits.MaxDerivedBytes),
            expansion: new ExpansionPolicy(ReconcileAliasContracts.ExpansionCandidateCap, limits.MaxAiCallsPerRun, ExpansionReservedCalls), expansionMeasurer: measure.Expansion);

        // Il budget su cui il precheck ha deciso, per ogni candidato che ha speso almeno una chiamata (stessa formula del motore: limite − riservate − già impiegate − confronti già dovuti).
        var availableAt = new Dictionary<string, int>(StringComparer.Ordinal);
        model.OnFirstBlock = q =>
        {
            var id = q.Focus!.Single().CandidateId;
            var consumed = hierarchy.Outcomes.Count + engine.Calls.Used - 1;     // la chiamata in corso è già contata da RunBatchAsync
            var owed = engine.Pending.Count(p => p != id && engine.Candidate(p) is { Left.Kind: CandidateEndpointKind.Evidence, Right.Kind: CandidateEndpointKind.Evidence });
            availableAt[id] = limits.MaxAiCallsPerRun - ExpansionReservedCalls - consumed - owed;
        };

        string? exception = null;
        FinalPassResult? final = null;
        try
        {
            await engine.RunPhasesAsync(ct);
            final = await ReconciliationFinalPass.RunAsync(engine, snapshot, directory, [], ct);
        }
        catch (DocumentWorkException e)
        {
            exception = e.Code + ": " + e.Message[..Math.Min(140, e.Message.Length)];
        }

        var run = engine.Result();
        string OriginOf(string id) => directory.Candidates.Any(c => c.CandidateId == id)
            ? "directory"
            : hierarchy.Outcomes.FirstOrDefault(o => o.Result.Candidates.Any(c => c.CandidateId == id)) is { } outcome ? outcome.Batch.Stage : "altro";
        int MembersOf(CandidateEndpoint e) => e.Kind switch
        {
            CandidateEndpointKind.Set => directory.Sets.Single(s => s.SetId == e.Id).MemberCount,
            CandidateEndpointKind.Node => hierarchy.Nodes.Single(n => n.NodeId == e.Id).MemberCount,
            _ => 1,
        };
        var groups = new List<GroupExpansion>();
        foreach (var candidate in run.Candidates.Where(c => c.Left.Kind != CandidateEndpointKind.Evidence))
        {
            var blocks = model.Calls.Count(c => c.Batch.Stage == "expand" && c.Focus!.Single().CandidateId == candidate.CandidateId);
            var disposition = run.Dispositions.Single(d => d.CandidateId == candidate.CandidateId);
            int? available = availableAt.TryGetValue(candidate.CandidateId, out var known) ? known : AvailableFromReason(disposition.Reason);
            groups.Add(new GroupExpansion(
                candidate.CandidateId, candidate.Left.Kind.ToString(), OriginOf(candidate.CandidateId), Math.Max(0, run.ProcessingOrder.ToList().IndexOf(candidate.CandidateId)), MembersOf(candidate.Left), MembersOf(candidate.Right),
                blocks == 0 ? BlocksFromReason(disposition.Reason) : blocks, disposition.ChildCandidateIds.Count, disposition.State, disposition.Reason, available));
        }

        var notProcessed = run.Dispositions.Where(d => d.State == CandidateState.NotProcessed).ToList();
        var byReason = notProcessed.GroupBy(d => (d.Reason ?? string.Empty).Split(':')[0]).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var byStage = model.Calls.GroupBy(c => c.Batch.Stage).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var lines = new List<string>();
        lines.AddRange(run.Dispositions.Select(d => $"{d.CandidateId}|{d.State}|{d.Reason}|{string.Join(',', d.ChildCandidateIds)}|{string.Join(',', d.ComparisonOutputHashes)}"));
        lines.AddRange(model.Calls.Select(c => $"{c.Batch.Stage}|{c.Batch.BatchId}|{c.Entries.Count}|{c.Nodes.Count}"));
        lines.Add(exception ?? string.Empty);
        lines.Add(final?.Outcome.ToString() ?? string.Empty);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
        return new ExpansionSimulation(
            children, items, disjoint, [.. directory.Leaves.GroupBy(l => l.View).OrderBy(g => g.Key).Select(g => g.Key + ":" + string.Join('+', g.OrderBy(l => l.Ordinal).Select(l => l.Entries.Count)))],
            hierarchy.Outcomes.Count, engine.Calls.Used, byStage, groups, byReason, notProcessed.Count, final?.Outcome, final?.Code, run.Unresolved.Count, exception, fingerprint);
    }

    /// <summary>Se il candidato non ha speso chiamate (precheck rifiutato) i blocchi sono nel motivo: «servono fino a R chiamate (B blocchi x …)».</summary>
    private static int BlocksFromReason(string? reason) =>
        reason is not null && Regex.Match(reason, @"\((?<b>\d+) blocchi x") is { Success: true } m ? int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture) : 0;

    /// <summary>Il motivo del rifiuto riporta il budget residuo su cui il precheck ha deciso: «e ne restano A (…)».</summary>
    private static int? AvailableFromReason(string? reason) =>
        reason is not null && Regex.Match(reason, @"e ne restano (?<a>-?\d+) \(") is { Success: true } m ? int.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture) : null;

    // ================================================================ golden (valori OSSERVATI)

    private const string B5 = "blocks=[8,8,8,8,20]";       // con gli item delle foglie A nei messaggi di espansione (v10 reale): 4 candidati 151x131 da 8 blocchi e il Cross 282x282 da 20
    private const string B3 = "blocks=[4,4,4,4,6]";        // senza item: lo stesso spazio di coppie in 22 blocchi invece di 52

    /// <summary>(item delle foglie A nei messaggi, figli disgiunti, c) → sintesi osservata.</summary>
    private static readonly Dictionary<(bool Items, bool Disjoint, int C), string> Golden = new()
    {
        [(true, false, 0)] = $"{B5} calls=63 (hier 7, expand 52, compare 4) notProcessed=0[] final=Complete unresolved=0",
        [(true, false, 1)] = $"{B5} calls=97 (hier 7, expand 52, compare 38) notProcessed=0[] final=Complete unresolved=0",
        [(true, false, 2)] = $"{B5} calls=132 (hier 7, expand 52, compare 73) notProcessed=0[] final=Complete unresolved=0",
        [(true, false, 3)] = $"{B5} calls=167 (hier 7, expand 52, compare 108) notProcessed=0[] final=Complete unresolved=0",
        [(true, false, 5)] = $"{B5} calls=237 (hier 7, expand 52, compare 178) notProcessed=0[] final=Complete unresolved=0",
        [(true, false, 16)] = $"{B5} calls=275 (hier 7, expand 32, compare 236) notProcessed=5[expansion_budget_exceeded=1,expansion_saturated=4] final=CoverageIncomplete unresolved=5",
        [(true, true, 0)] = $"{B5} calls=63 (hier 7, expand 52, compare 4) notProcessed=0[] final=Complete unresolved=0",
        [(true, true, 1)] = $"{B5} calls=115 (hier 7, expand 52, compare 56) notProcessed=0[] final=Complete unresolved=0",
        [(true, true, 2)] = $"{B5} calls=167 (hier 7, expand 52, compare 108) notProcessed=0[] final=Complete unresolved=0",
        [(true, true, 3)] = $"{B5} calls=219 (hier 7, expand 52, compare 160) notProcessed=0[] final=Complete unresolved=0",
        [(true, true, 5)] = $"{B5} calls=203 (hier 7, expand 32, compare 164) notProcessed=1[expansion_budget_exceeded=1] final=CoverageIncomplete unresolved=1",
        [(true, true, 16)] = $"{B5} calls=419 (hier 7, expand 24, compare 388) notProcessed=5[expansion_budget_exceeded=2,expansion_saturated=3] final=CoverageIncomplete unresolved=5",
        [(false, false, 0)] = $"{B3} calls=33 (hier 7, expand 22, compare 4) notProcessed=0[] final=Complete unresolved=0",
        [(false, false, 1)] = $"{B3} calls=45 (hier 7, expand 22, compare 16) notProcessed=0[] final=Complete unresolved=0",
        [(false, false, 2)] = $"{B3} calls=58 (hier 7, expand 22, compare 29) notProcessed=0[] final=Complete unresolved=0",
        [(false, false, 3)] = $"{B3} calls=71 (hier 7, expand 22, compare 42) notProcessed=0[] final=Complete unresolved=0",
        [(false, false, 5)] = $"{B3} calls=97 (hier 7, expand 22, compare 68) notProcessed=0[] final=Complete unresolved=0",
        [(false, false, 16)] = $"{B3} calls=231 (hier 7, expand 22, compare 202) notProcessed=5[expansion_saturated=5] final=CoverageIncomplete unresolved=5",
        [(false, true, 0)] = $"{B3} calls=33 (hier 7, expand 22, compare 4) notProcessed=0[] final=Complete unresolved=0",
        [(false, true, 1)] = $"{B3} calls=55 (hier 7, expand 22, compare 26) notProcessed=0[] final=Complete unresolved=0",
        [(false, true, 2)] = $"{B3} calls=77 (hier 7, expand 22, compare 48) notProcessed=0[] final=Complete unresolved=0",
        [(false, true, 3)] = $"{B3} calls=99 (hier 7, expand 22, compare 70) notProcessed=0[] final=Complete unresolved=0",
        [(false, true, 5)] = $"{B3} calls=143 (hier 7, expand 22, compare 114) notProcessed=0[] final=Complete unresolved=0",
        [(false, true, 16)] = $"{B3} calls=385 (hier 7, expand 22, compare 356) notProcessed=5[expansion_saturated=5] final=CoverageIncomplete unresolved=5",
    };

    public static TheoryData<bool, bool, int> Matrix()
    {
        var data = new TheoryData<bool, bool, int>();
        foreach (var items in new[] { true, false })
        {
            foreach (var disjoint in new[] { false, true })
            {
                foreach (var c in ChildrenPerBlock)
                {
                    data.Add(items, disjoint, c);
                }
            }
        }

        return data;
    }

    [Fact]
    public void The_reserved_calls_constant_is_the_one_of_the_plan()
    {
        var plan = typeof(DocumentAnalysisWorkflow).Assembly.GetType("bProjects.App.AI.DocumentReconcilePlan", throwOnError: true)!;
        var field = plan.GetField("ExpansionReservedCalls", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(ExpansionReservedCalls, (int)field.GetRawConstantValue()!);
        Assert.Equal(512, new DocumentOptions().ToLimits().MaxAiCallsPerRun);
        Assert.Equal(16, ReconcileAliasContracts.ExpansionCandidateCap);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task The_simulated_cost_is_the_observed_golden_and_is_deterministic(bool items, bool disjoint, int c)
    {
        var first = await SimulateAsync(c, items, disjoint, Ct);
        var second = await SimulateAsync(c, items, disjoint, Ct);

        Assert.Null(first.Exception);                                           // nessun errore: il motore non è fermato da un limite del run né da un output rifiutato
        Assert.Equal(first.Fingerprint, second.Fingerprint);                    // determinismo: stesse disposizioni, stessi batch, stessi hash
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(["A:151+131", "B:151+131"], first.LeafSizes);              // stesse foglie di RH-004 (v10, DirectoryFacts deterministici)
        Assert.Equal(7, first.HierarchyCalls);                                  // 4 foglie, 2 riduzioni, 1 cross
        Assert.Equal(Golden[(items, disjoint, c)], first.Summary);
        Assert.True(first.TotalCalls <= 512);                                   // il tetto del run non è mai superato: il precheck e le riserve lo precedono
    }

    [Fact]
    public async Task The_blocks_of_each_candidate_depend_on_the_membership_and_the_tokenizer_never_on_the_children_and_the_precheck_requires_17_calls_per_block()
    {
        foreach (var items in new[] { true, false })
        {
            var runs = new List<ExpansionSimulation>();
            foreach (var c in ChildrenPerBlock)
            {
                runs.Add(await SimulateAsync(c, items, false, Ct));
            }

            Assert.Single(runs.Select(r => string.Join(',', r.Groups.OrderBy(g => g.Order).Select(g => $"{g.Kind}/{g.Origin}/{g.LeftMembers}x{g.RightMembers}:{g.Blocks}"))).Distinct());
            Assert.All(runs.SelectMany(r => r.Groups), g => Assert.Equal(g.Blocks * 17, g.Required));
            Assert.Equal(5, runs[0].Groups.Count);                              // due insiemi della directory (A e B), due riduzioni, un Cross
            Assert.Equal(["Cross:282x282", "Node:151x131", "Node:151x131", "Set:151x131", "Set:151x131"], runs[0].Groups.Select(g => $"{(g.Origin == "cross" ? "Cross" : g.Kind)}:{g.LeftMembers}x{g.RightMembers}").Order(StringComparer.Ordinal));
        }
    }

    /// <summary>Criterio d'ingresso di O5: «il precheck è violato con ≤ 2 figli per blocco». Non lo è, né con figli condivisi né disgiunti, né con ≤ 3: la prima violazione compare a c = 5 (figli disgiunti, item nei messaggi) e a c = 16.</summary>
    [Fact]
    public async Task The_precheck_is_not_violated_with_at_most_3_children_per_block_so_the_entry_criterion_of_option_O5_is_not_met()
    {
        var firstViolation = new List<string>();
        foreach (var (items, disjoint) in new[] { (true, false), (true, true), (false, false), (false, true) })
        {
            foreach (var c in ChildrenPerBlock)
            {
                var s = await SimulateAsync(c, items, disjoint, Ct);
                if (c <= 3)
                {
                    Assert.Equal(0, s.PrecheckRefusals);
                    Assert.Equal(0, s.NotProcessed);
                    Assert.Equal(FinalPassOutcome.Complete, s.Final);
                }

                if (s.PrecheckRefusals > 0)
                {
                    firstViolation.Add($"items={items} disjoint={disjoint} c={c}");
                }
            }
        }

        string[] expected = ["items=True disjoint=False c=16", "items=True disjoint=True c=16", "items=True disjoint=True c=5"];
        Assert.Equal(expected, firstViolation.Order(StringComparer.Ordinal));
    }

    /// <summary>Il precheck non conosce <c>c</c>: stima 1 + 16 chiamate per blocco. Con c ≤ 2 la spesa effettiva è al più 3 per blocco, quindi il precheck sovrastima il bisogno di almeno 5,7 volte.</summary>
    [Fact]
    public async Task The_precheck_estimate_overshoots_the_actual_cost_by_at_least_5_times_when_the_model_returns_at_most_2_children()
    {
        var s = await SimulateAsync(2, true, true, Ct);
        var required = s.Groups.Sum(g => g.Required);
        var spent = s.ExpandCalls + s.CompareCalls;
        Assert.Equal(884, required);                                            // 4 x 136 + 340
        Assert.Equal(160, spent);
        Assert.True(required >= 5 * spent);
    }

    // ================================================================ tabelle delle evidenze (non sono test: stampano e salvano)

    [Fact]
    public async Task Evidence_tables_for_M0_are_written()
    {
        var sb = new StringBuilder();
        foreach (var (items, disjoint) in new[] { (true, false), (true, true), (false, false), (false, true) })
        {
            sb.AppendLine($"### item delle foglie A nei messaggi: {(items ? "sì (v10 reale)" : "no (sensibilità)")}; figli {(disjoint ? "disgiunti" : "condivisi")}");
            sb.AppendLine();
            sb.AppendLine("| c | blocchi per candidato (ordine di elaborazione) | Σ blocchi | chiamate totali | gerarchia | espansione | confronto | NotProcessed (motivo) | precheck rifiutato | completezza (round finale) | non terminali |");
            sb.AppendLine("|---:|---|---:|---:|---:|---:|---:|---|---:|---|---:|");
            foreach (var c in ChildrenPerBlock)
            {
                var s = await SimulateAsync(c, items, disjoint, Ct);
                var reasons = s.NotProcessed == 0 ? "0" : $"{s.NotProcessed} ({string.Join(", ", s.NotProcessedByReason.Select(kv => kv.Key + " " + kv.Value))})";
                sb.AppendLine($"| {c} | {string.Join(" + ", s.Groups.OrderBy(g => g.Order).Select(g => g.Blocks))} | {s.Blocks} | {s.TotalCalls} | {s.HierarchyCalls} | {s.ExpandCalls} | {s.CompareCalls} | {reasons} | {s.PrecheckRefusals} | {s.Final} | {s.Unresolved} |");
            }

            sb.AppendLine();
        }

        var detail = await SimulateAsync(16, true, true, Ct);
        sb.AppendLine("### Precheck per candidato (item sì, figli disgiunti, c = 16)");
        sb.AppendLine();
        sb.AppendLine("| ordine | candidato | origine | membri | blocchi | richieste (17 × blocchi) | residuo al precheck | esito |");
        sb.AppendLine("|---:|---|---|---|---:|---:|---:|---|");
        foreach (var g in detail.Groups.OrderBy(g => g.Order))
        {
            sb.AppendLine($"| {g.Order} | {g.Kind} | {g.Origin} | {g.LeftMembers}×{g.RightMembers} | {g.Blocks} | {g.Required} | {g.Available} | {g.State} {(g.Reason is null ? string.Empty : g.Reason.Split(':')[0])} |");
        }

        var directory = Path.Combine(Path.GetTempPath(), "bprojects-reconcile-measurement");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "rh005-evidence.md"), sb.ToString());
        TestContext.Current.SendDiagnosticMessage(sb.ToString());
        Assert.NotEmpty(sb.ToString());
    }
}
