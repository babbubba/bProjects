namespace bProjects.App.AI;

// Riapertura dei candidati (002 T074; contracts/ai-workflow.md §4.3). Ordine stabile: obbligazioni di relazioni note, candidati A, B, Cross, poi candidateId.
// Un candidato di gruppi (Node/Set) riapre TUTTI i membri integrali (manifest = unione esatta e deduplicata, ordinata, suddivisa da Pack); un candidato Evidence
// è confrontato con entrambi gli endpoint integrali. Nessuna disposition terminale si fonda sui soli descriptor: un candidato non visitato resta Pending o
// NotProcessed, mai Open né Compared.

public sealed record CandidateRun(
    IReadOnlyList<ReconcileCandidate> Candidates, IReadOnlyList<CandidateDisposition> Dispositions, IReadOnlyList<BatchOutcome> Outcomes, IReadOnlyList<string> ProcessingOrder)
{
    /// <summary>Candidati senza disposition terminale (Pending, NotProcessed, Expanded con figli non terminali): impediscono la completezza.</summary>
    public IReadOnlyList<string> Unresolved
    {
        get
        {
            var byId = Dispositions.ToDictionary(d => d.CandidateId, StringComparer.Ordinal);
            var memo = new Dictionary<string, bool>(StringComparer.Ordinal);
            return [.. Candidates.Select(c => c.CandidateId).Where(id => !Terminal(id, byId, memo, [])).Order(StringComparer.Ordinal)];
        }
    }

    private static bool Terminal(string id, Dictionary<string, CandidateDisposition> byId, Dictionary<string, bool> memo, HashSet<string> path)
    {
        if (memo.TryGetValue(id, out var known))
        {
            return known;
        }

        if (!byId.TryGetValue(id, out var d) || !path.Add(id))
        {
            return false;
        }

        var result = d.State switch
        {
            CandidateState.Compared or CandidateState.Open => true,
            CandidateState.Expanded => d.ChildCandidateIds.Count > 0 && d.ChildCandidateIds.All(c => Terminal(c, byId, memo, path)),
            _ => false,
        };
        path.Remove(id);
        return memo[id] = result;
    }
}

/// <summary>Limiti globali (§4.3): chiamate (retry inclusi, contati dal chiamante con <see cref="CallBudget.Add"/>), candidati, byte derivati e deadline. Prevalgono; mai pruning.</summary>
public sealed record ReconciliationLimits(int MaxCalls = 512, int MaxCandidates = 100_000, long MaxDerivedBytes = long.MaxValue, DateTimeOffset? Deadline = null);

/// <summary>
/// Policy dell'espansione con membership (definizione v10, algoritmo <see cref="ReconcileAliasContracts.ExpansionAlgorithm"/>), congelata nel run. <paramref name="CandidateCap"/> è il tetto di candidati per risposta
/// (protezione, mai selezione); <paramref name="RunCallLimit"/> è il limite PERSISTITO di chiamate del run (<c>MaxAiCallsPerRun</c> dello snapshot dei limiti), <paramref name="ReservedCalls"/> le chiamate
/// certe che restano dopo la gerarchia (stadio Gap). <c>null</c> = espansione storica (v4–v9), invariata.
/// </summary>
public sealed record ExpansionPolicy(int CandidateCap, int RunCallLimit, int ReservedCalls);

/// <summary>
/// Misura il payload COMPLETO (istruzioni, schema della variante Expand, candidato in esame, membership, voci, fonti, item e relazioni) del messaggio che verrà realmente inviato per un blocco di espansione.
/// Le voci arrivano già nell'ordine del messaggio. Funzione pura dei dati e del tokenizer: stessa misura al replay.
/// </summary>
public delegate ValueTask<UnitMeasure> ExpansionMeasurer(ReconcileCandidate candidate, ReconcileView view, ExpansionGroups groups, IReadOnlyList<CatalogEntry> members, CancellationToken ct);

/// <summary>Contatore condiviso delle chiamate: ogni invio e ogni retry (che il port dichiara con <see cref="Add"/>) conta, un replay/cache hit no.</summary>
public sealed class CallBudget
{
    public int Used { get; private set; }
    public long DerivedBytes { get; private set; }

    public void Add(int calls = 1, long bytes = 0)
    {
        Used += calls;
        DerivedBytes += bytes;
    }
}

public sealed class ReconciliationCandidates
{
    public const string NoCandidateAfterFullScan = "no_candidate_after_full_scan";
    public const string CandidateUnresolved = "candidate_unresolved";
    public const string PairDoesNotFit = "pair_does_not_fit";
    public const string EntryDoesNotFit = "entry_does_not_fit";
    public const string LimitReached = "limit_reached";

    /// <summary>v10: almeno un blocco dell'espansione ha raggiunto il tetto di candidati; i figli validi restano conservati, il genitore non dichiara la copertura (mai Expanded né Compared). Prevale su <see cref="CandidateUnresolved"/>.</summary>
    public const string ExpansionSaturated = "expansion_saturated";

    /// <summary>v10: le chiamate necessarie all'espansione (e ai confronti dei suoi figli, nel caso peggiore) non entrano nel limite persistito del run: nessuna chiamata spesa per questo candidato.</summary>
    public const string ExpansionBudgetExceeded = "expansion_budget_exceeded";

    /// <summary>v10: la copertura delle coppie rilevanti non è dimostrata (blocco non completo o griglia non verificabile): mai <c>no_candidate_after_full_scan</c>.</summary>
    public const string ExpansionCoverageIncomplete = "expansion_coverage_incomplete";

    private readonly ReconciliationSnapshot snapshot;
    private readonly ReconciliationDirectoryResult directory;
    private readonly HierarchyResult hierarchy;
    private readonly ReconciliationContext context;
    private readonly PackMeasurer<CatalogEntry> measurer;
    private readonly ReconcileInvoker invoke;
    private readonly ReconciliationLimits limits;
    private readonly TimeProvider time;
    private readonly ReconciliationCatalog catalog;
    private readonly List<CatalogSet> sets;
    private readonly Dictionary<Guid, CatalogEntry> entries;
    private readonly Dictionary<string, List<Guid>> membership = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReconcileCandidate> candidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CandidateDisposition> dispositions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CandidateState State, string? Reason, string OutputHash)> pairCache = new(StringComparer.Ordinal);
    private readonly List<BatchOutcome> outcomes = [];
    private readonly List<string> order = [];
    private readonly Dictionary<string, ReconcileView> views = new(StringComparer.Ordinal);
    private int ordinal;
    private readonly ExpansionPolicy? expansion;
    private readonly ExpansionMeasurer? expansionMeasurer;

    public ReconciliationCandidates(
        ReconciliationSnapshot snapshot, ReconciliationDirectoryResult directory, HierarchyResult hierarchy, ReconciliationContext context, PackMeasurer<CatalogEntry> measurer,
        ReconcileInvoker invoke, ReconciliationLimits? limits = null, CallBudget? calls = null, TimeProvider? time = null, ExpansionPolicy? expansion = null,
        ExpansionMeasurer? expansionMeasurer = null)
    {
        if ((expansion is null) != (expansionMeasurer is null))
        {
            throw new ArgumentException("La policy dell'espansione e il suo misuratore vanno forniti insieme (o nessuno dei due: espansione storica).");
        }

        this.expansion = expansion;
        this.expansionMeasurer = expansionMeasurer;
        this.snapshot = snapshot;
        this.directory = directory;
        this.hierarchy = hierarchy;
        this.context = context;
        this.measurer = measurer;
        this.invoke = invoke;
        this.limits = limits ?? new ReconciliationLimits();
        Calls = calls ?? new CallBudget();
        this.time = time ?? TimeProvider.System;
        sets = [.. directory.Sets];
        catalog = new ReconciliationCatalog(hierarchy.Nodes, hierarchy.Pages, sets);
        entries = directory.Entries.ToDictionary(e => e.EvidenceId);
        BuildMembership();
    }

    public CallBudget Calls { get; }

    public bool Has(string candidateId) => candidates.ContainsKey(candidateId);

    /// <summary>Registra insiemi di routing nati dal merge finale (membership esatta, già calcolata dal server).</summary>
    public void AddSets(IEnumerable<CatalogSet> added)
    {
        foreach (var set in added.Where(s => sets.All(x => x.SetId != s.SetId)))
        {
            sets.Add(set);
        }
    }

    /// <summary>Residuo non risolto: terminale esplicito con motivo (mai Open né Compared).</summary>
    public void MarkNotProcessed(string id, string reason) => Set(id, CandidateState.NotProcessed, [], [], reason);

    /// <summary>Risultato dell'unico round finale, se già eseguito: un secondo invio è un replay e non rifà lavoro.</summary>
    public FinalPassResult? FinalResult { get; set; }

    /// <summary>Candidati ancora Pending, ordinati per candidateId.</summary>
    public IReadOnlyList<string> Pending =>
        [.. dispositions.Values.Where(d => d.State == CandidateState.Pending).Select(d => d.CandidateId).Order(StringComparer.Ordinal)];

    public CandidateRun Result() => new(
        [.. candidates.Values.OrderBy(c => c.CandidateId, StringComparer.Ordinal)], [.. dispositions.Values.OrderBy(d => d.CandidateId, StringComparer.Ordinal)], outcomes, order);

    /// <summary>Fasi A/B/Cross dopo le relazioni note; i figli Evidence nati da un'espansione sono confrontati nella stessa fase, quelli dei confronti restano Pending.</summary>
    public async ValueTask<CandidateRun> RunPhasesAsync(CancellationToken ct)
    {
        var phases = new List<(ReconcileView? View, List<ReconcileCandidate> Items)>
        {
            (null, [.. directory.KnownRelations]),
            (ReconcileView.A, Proposed(ReconcileView.A)),
            (ReconcileView.B, Proposed(ReconcileView.B)),
            (ReconcileView.Cross, Proposed(ReconcileView.Cross)),
        };
        foreach (var (view, items) in phases)
        {
            var ids = new List<string>();
            foreach (var candidate in items)
            {
                var existed = candidates.ContainsKey(candidate.CandidateId);
                Add(candidate, view ?? ViewOf(candidate));
                if (!existed)
                {
                    ids.Add(candidate.CandidateId);
                }
            }

            var phaseChildren = new List<string>();
            foreach (var id in ids.Order(StringComparer.Ordinal))
            {
                phaseChildren.AddRange(await ProcessAsync(id, ct));
            }

            foreach (var child in phaseChildren.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (dispositions[child].State == CandidateState.Pending)
                {
                    await ProcessAsync(child, ct);
                }
            }
        }

        return Result();
    }

    /// <summary>Aggiunge (o unisce le origini di) un candidato; nuovo = Pending. Rifiuta oltre il limite globale di candidati.</summary>
    public void Add(ReconcileCandidate candidate, ReconcileView view)
    {
        if (!candidates.ContainsKey(candidate.CandidateId))
        {
            if (candidates.Count >= limits.MaxCandidates)
            {
                throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, $"{LimitReached}: oltre {limits.MaxCandidates} candidati (candidato {candidate.CandidateId}).");
            }

            dispositions[candidate.CandidateId] = new() { CandidateId = candidate.CandidateId, State = CandidateState.Pending, ChildCandidateIds = [], ComparisonOutputHashes = [], Reason = null };
            views[candidate.CandidateId] = view;
        }

        ReconciliationDirectory.AddCandidate(
            candidates, snapshot, candidate.Kind, candidate.Left, candidate.Right, candidate.ProposedType, candidate.Rationale, candidate.Basis, candidate.OriginBatchIds,
            candidate.ParentCandidateId);
    }

    public ReconcileCandidate Candidate(string id) => candidates[id];

    public CandidateDisposition Disposition(string id) => dispositions[id];

    /// <summary>Risolve un candidato Pending: espansione (gruppi) o confronto (Evidence-Evidence). Ritorna i figli Evidence nati dall'espansione.</summary>
    public async ValueTask<IReadOnlyList<string>> ProcessAsync(string id, CancellationToken ct)
    {
        order.Add(id);
        var candidate = candidates[id];
        return candidate.Left.Kind == CandidateEndpointKind.Evidence && candidate.Right.Kind == CandidateEndpointKind.Evidence
            ? await CompareAsync(candidate, ct)
            : await ExpandAsync(candidate, ct);
    }

    private async ValueTask<IReadOnlyList<string>> CompareAsync(ReconcileCandidate candidate, CancellationToken ct)
    {
        var ids = new[] { candidate.Left.Id, candidate.Right.Id }.Order(StringComparer.Ordinal).ToArray();
        var pairKey = string.Join('|', ids);
        if (pairCache.TryGetValue(pairKey, out var hit))
        {
            Set(candidate.CandidateId, hit.State, [], [hit.OutputHash], hit.Reason);   // cache hit logico: stessa coppia, nessuna nuova AI
            return [];
        }

        var pair = ids.Select(i => entries[Guid.Parse(i)]).ToList();
        PackedBatch<CatalogEntry> batch;
        try
        {
            batch = await DocumentBatchPacking.PackNextAsync(pair, 0, ordinal, 2, context.Limits, e => e.EntryId, measurer, ct);
        }
        catch (DocumentWorkException e) when (e.Code == DocumentWorkException.ReconciliationIrreducible)
        {
            Set(candidate.CandidateId, CandidateState.NotProcessed, [], [], $"{PairDoesNotFit}: {e.Message}");   // mai Open: la coppia integrale non è stata confrontata
            return [];
        }

        var outcome = await RunBatchAsync(batch, "compare", candidate, ct);
        var own = outcome.Result.Dispositions.SingleOrDefault(d => d.CandidateId == candidate.CandidateId);
        if (outcome.Result.Dispositions.Count != 1 || own is null || own.State is not (CandidateState.Compared or CandidateState.Open))
        {
            throw Invalid(outcome, "il confronto deve restituire una sola disposition Compared o Open per il candidato.");
        }

        pairCache[pairKey] = (own.State, own.Reason, outcome.OutputHash);
        Set(candidate.CandidateId, own.State, [], [outcome.OutputHash], own.Reason);
        AddChildren(outcome, candidate);   // collegamenti emersi dal confronto: Pending, per il round finale
        return [];
    }

    /// <summary>
    /// Espansione v10 (algoritmo <c>expansion-grid-1</c>, contratto §4.3 emendato per la sola v10): i membri dei due estremi sono partizionati in una GRIGLIA deterministica di blocchi, calcolata e misurata PRIMA di ogni
    /// chiamata, in modo che ogni coppia rilevante (due evidenze distinte, una per gruppo; una voce in entrambi i gruppi vale per entrambi) compaia in almeno un blocco. Ogni voce dichiara i gruppi di appartenenza
    /// (<see cref="ExpansionGroups"/>, calcolati sugli insiemi interi). Un blocco è Completo (risposta valida, sotto il tetto, <c>focus</c> null), Saturo (tetto raggiunto) o Non risolto (<c>focus</c> Unresolved).
    /// Il genitore diventa <c>Expanded</c>/<c>Compared(no_candidate_after_full_scan)</c> SOLO se tutti i blocchi sono Completi e la copertura è verificata; altrimenti <c>NotProcessed</c> con un motivo
    /// (precedenza: saturazione, poi non risolto). Nessun pruning, top-k o troncamento: i figli validi di OGNI blocco restano candidati e l'hash di OGNI output entra nella disposition.
    /// La sequenza dei blocchi è una funzione pura dei membri, dei tag, del misuratore e dei limiti congelati: il replay ricostruisce gli stessi batch (ordinali locali 0..n-1, livello 0, nessun genitore).
    /// </summary>
    private async ValueTask<IReadOnlyList<string>> ExpandGridAsync(ReconcileCandidate candidate, CancellationToken ct)
    {
        var policy = expansion!;
        var measure = expansionMeasurer!;
        var left = ReconciliationExpansionGrid.Ordered(Members(candidate.Left).Distinct());
        var right = ReconciliationExpansionGrid.Ordered(Members(candidate.Right).Distinct());
        var tags = ReconciliationExpansionGrid.Tags(left, right);
        var groups = new ExpansionGroups(tags);
        var view = ViewOf(candidate);
        var limitsOfBatch = context.Limits;

        List<CatalogEntry> InMessageOrder(IReadOnlyList<Guid> ids) => [.. ReconciliationExpansionGrid.MessageOrder(ids, tags).Select(g => entries[g])];
        ValueTask<UnitMeasure> MeasureAsync(IReadOnlyList<Guid> ids) => measure(candidate, view, groups, InMessageOrder(ids), ct);

        // 1) Griglia: bisezione deterministica del prodotto S1 x S2 (si divide il lato con più membri) finché il messaggio COMPLETO di ogni cella entra nel budget. Lo spazio delle coppie si conserva esattamente.
        string? failure = null;
        var accepted = new List<ExpansionCell>();
        async ValueTask FitAsync(ExpansionCell cell)
        {
            if (failure is not null)
            {
                return;
            }

            var members = ReconciliationExpansionGrid.UnionOf(cell);
            if (!ReconciliationExpansionGrid.IsRelevant(members, tags))
            {
                accepted.Add(cell);   // nessuna coppia rilevante: accettata intera senza misura e poi scartata da Blocks (non genera alcuna chiamata)
                return;
            }

            var measured = await MeasureAsync(members);
            if (measured.Quality == TokenCountQuality.Unavailable)
            {
                // Come il Pack storico: senza tokenizer o calibrazione non si decide nulla (mai «non entra»).
                throw new RuntimeUnverifiedException("conteggio del payload dell'espansione non disponibile (nessun tokenizer o calibrazione identificata)");
            }

            if (limitsOfBatch.Fits(measured))
            {
                accepted.Add(cell);
                return;
            }

            if (ReconciliationExpansionGrid.Split(cell) is not { } halves)
            {
                failure = $"{PairDoesNotFit}: due evidenze del candidato non entrano insieme nel budget di input ({measured.Tokens} token misurati su {limitsOfBatch.InputBudgetTokens}) con il candidato in esame e la membership.";
                return;
            }

            foreach (var half in halves)
            {
                await FitAsync(half);
            }
        }

        await FitAsync(new ExpansionCell(left, right));
        if (failure is not null)
        {
            Set(candidate.CandidateId, CandidateState.NotProcessed, [], [], failure);
            return [];
        }

        var blocks = ReconciliationExpansionGrid.Blocks(accepted, tags);
        var coverage = ReconciliationExpansionGrid.Verify(left, right, blocks);
        if (blocks.Count == 0)
        {
            // Nessuna coppia rilevante (es. un solo membro comune ai due gruppi): nessun output da attestare, quindi nessun Compared senza hash. Fail-closed con un motivo esplicito.
            Set(candidate.CandidateId, CandidateState.NotProcessed, [], [], $"{ExpansionCoverageIncomplete}: i due gruppi non contengono alcuna coppia di evidenze distinte da mettere a confronto; nessuna scansione da attestare.");
            return [];
        }

        if (!coverage.Complete)
        {
            Set(candidate.CandidateId, CandidateState.NotProcessed, [], [], $"{ExpansionCoverageIncomplete}: la griglia copre {coverage.CoveredPairs} coppie rilevanti su {coverage.RequiredPairs}.");
            return [];
        }

        // 2) Precheck del budget di chiamate, SOLO su quantità deterministiche (batch della gerarchia, chiamate del motore, disposition): identico al replay e dopo un crash. Il contatore persistito del port
        // può essere maggiore (retry, riserve già consumate): in quel caso la riserva del port rifiuta (CallLimitExceeded), sempre fail-closed. Il lavoro futuro non prevedibile NON è coperto da questa stima.
        var perBlock = 1 + policy.CandidateCap;   // 1 espansione + fino a CandidateCap confronti dei figli (anche un blocco saturo ne conserva CandidateCap)
        var required = (long)blocks.Count * perBlock;
        var consumed = hierarchy.Outcomes.Count + Calls.Used;
        var owedCompares = dispositions.Values.Count(d => d.State == CandidateState.Pending && d.CandidateId != candidate.CandidateId
            && candidates[d.CandidateId] is { Left.Kind: CandidateEndpointKind.Evidence, Right.Kind: CandidateEndpointKind.Evidence });
        var available = (long)policy.RunCallLimit - policy.ReservedCalls - consumed - owedCompares;
        if (required > available)
        {
            Set(
                candidate.CandidateId, CandidateState.NotProcessed, [], [],
                $"{ExpansionBudgetExceeded}: servono fino a {required} chiamate ({blocks.Count} blocchi x (1 espansione + {policy.CandidateCap} confronti dei figli)) e ne restano {available} "
                + $"({policy.RunCallLimit} del run - {policy.ReservedCalls} riservate - {consumed} già impiegate - {owedCompares} confronti già dovuti): nessuna chiamata spesa per questo candidato.");
            return [];
        }

        // 3) Esecuzione dei blocchi nell'ordine della griglia. Si proseguono tutti i blocchi anche dopo un blocco saturo o non risolto: i figli validi sono candidati reali.
        var hashes = new List<string>();
        var children = new SortedSet<string>(StringComparer.Ordinal);
        var saturated = 0;
        var unresolved = 0;
        foreach (var block in blocks)
        {
            var ordered = InMessageOrder(block.Members);
            var packed = new PackedBatch<CatalogEntry>(block.Ordinal, ordered, await measure(candidate, view, groups, ordered, ct));
            var outcome = await RunBatchAsync(packed, "expand", candidate, ct, groups);
            hashes.Add(outcome.OutputHash);
            var own = outcome.Result.Dispositions.SingleOrDefault();
            if (outcome.Result.Dispositions.Count > 1 || (own is not null && (own.CandidateId != candidate.CandidateId
                || own.State is not (CandidateState.Pending or CandidateState.NotProcessed) || (own.State == CandidateState.NotProcessed && own.Reason != CandidateUnresolved))))
            {
                throw Invalid(outcome, "l'espansione può solo lasciare il candidato Pending o NotProcessed con candidate_unresolved.");
            }

            unresolved += own?.State == CandidateState.NotProcessed ? 1 : 0;
            saturated += outcome.Result.Candidates.Count >= policy.CandidateCap ? 1 : 0;
            foreach (var child in AddChildren(outcome, candidate))
            {
                children.Add(child);
            }
        }

        hashes.Sort(StringComparer.Ordinal);
        if (saturated > 0)
        {
            Set(
                candidate.CandidateId, CandidateState.NotProcessed, [.. children], hashes,
                $"{ExpansionSaturated}: {saturated} blocchi su {blocks.Count} hanno raggiunto il tetto di {policy.CandidateCap} candidati; il collegamento non è dichiarato completo (i figli validi sono conservati).");
        }
        else if (unresolved > 0)
        {
            Set(candidate.CandidateId, CandidateState.NotProcessed, [.. children], hashes, CandidateUnresolved);
        }
        else if (children.Count > 0)
        {
            Set(candidate.CandidateId, CandidateState.Expanded, [.. children], hashes, null);
        }
        else
        {
            // Tutti i blocchi Completi e la griglia verificata: ogni coppia rilevante è stata messa davanti al modello (attestazione di visita, non di assenza certa di conflitti).
            Set(candidate.CandidateId, CandidateState.Compared, [], hashes, NoCandidateAfterFullScan);
        }

        return [.. children];
    }

    private async ValueTask<IReadOnlyList<string>> ExpandAsync(ReconcileCandidate candidate, CancellationToken ct)
    {
        if (expansion is not null)
        {
            return await ExpandGridAsync(candidate, ct);
        }

        var manifest = Members(candidate.Left).Concat(Members(candidate.Right)).Distinct().OrderBy(g => g.ToString("D"), StringComparer.Ordinal).Select(g => entries[g]).ToList();
        IReadOnlyList<PackedBatch<CatalogEntry>> batches;
        try
        {
            batches = await DocumentBatchPacking.PackLevelAsync(manifest, 1, context.Limits, e => e.EntryId, measurer, ct);
        }
        catch (DocumentWorkException e) when (e.Code == DocumentWorkException.DocumentIncomplete)
        {
            Set(candidate.CandidateId, CandidateState.NotProcessed, [], [], $"{EntryDoesNotFit}: {e.Message}");
            return [];
        }

        var hashes = new List<string>();
        var children = new SortedSet<string>(StringComparer.Ordinal);
        var unresolved = false;
        foreach (var batch in batches)
        {
            var outcome = await RunBatchAsync(batch, "expand", candidate, ct);
            hashes.Add(outcome.OutputHash);
            var own = outcome.Result.Dispositions.SingleOrDefault();
            if (outcome.Result.Dispositions.Count > 1 || (own is not null && (own.CandidateId != candidate.CandidateId
                || own.State is not (CandidateState.Pending or CandidateState.NotProcessed) || (own.State == CandidateState.NotProcessed && own.Reason != CandidateUnresolved))))
            {
                throw Invalid(outcome, "l'espansione può solo lasciare il candidato Pending o NotProcessed con candidate_unresolved.");
            }

            unresolved |= own?.State == CandidateState.NotProcessed;
            foreach (var child in AddChildren(outcome, candidate))
            {
                children.Add(child);
            }
        }

        hashes.Sort(StringComparer.Ordinal);
        if (unresolved)
        {
            Set(candidate.CandidateId, CandidateState.NotProcessed, [.. children], hashes, CandidateUnresolved);
        }
        else if (children.Count > 0)
        {
            Set(candidate.CandidateId, CandidateState.Expanded, [.. children], hashes, null);
        }
        else
        {
            // Scansione integrale del gruppo senza collegamenti: attesta la scansione di tutti gli originali, non l'assenza certa di conflitti.
            Set(candidate.CandidateId, CandidateState.Compared, [], hashes, NoCandidateAfterFullScan);
        }

        return [.. children];
    }

    private async ValueTask<BatchOutcome> RunBatchAsync(PackedBatch<CatalogEntry> batch, string stage, ReconcileCandidate candidate, CancellationToken ct, ExpansionGroups? groups = null)
    {
        var view = ViewOf(candidate);
        GuardLimits(candidate);
        var derived = DocumentBatchPacking.ToDerived(
            batch, context.Owner, context.ManifestIdentity + "|" + candidate.CandidateId, context.Definition, stage, view.ToString(), 0, [], e => e.EntryId, context.SchemaVersion,
            context.PromptVersion);
        ordinal++;
        Calls.Add();
        var outcome = await ReconciliationHierarchy.InvokeAsync(derived, view, 0, batch.Members, [], snapshot, catalog, invoke, false, [candidate], ct, groups);
        Calls.Add(0, System.Text.Encoding.UTF8.GetByteCount(ReconciliationCodec.Canonical(outcome.Result)));
        outcomes.Add(outcome);
        return outcome;
    }

    private void GuardLimits(ReconcileCandidate candidate)
    {
        if (Calls.Used >= limits.MaxCalls)
        {
            throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, $"{LimitReached}: {limits.MaxCalls} chiamate raggiunte (retry inclusi) davanti al candidato {candidate.CandidateId}.");
        }

        if (Calls.DerivedBytes >= limits.MaxDerivedBytes)
        {
            throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, $"{LimitReached}: byte derivati esauriti davanti al candidato {candidate.CandidateId}.");
        }

        if (limits.Deadline is { } deadline && time.GetUtcNow() >= deadline)
        {
            throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, $"{LimitReached}: deadline raggiunta davanti al candidato {candidate.CandidateId}.");
        }
    }

    /// <summary>Candidati proposti dal risultato: solo Evidence-Evidence con parent il candidato aperto; entrano Pending.</summary>
    private IEnumerable<string> AddChildren(BatchOutcome outcome, ReconcileCandidate parent)
    {
        var added = new List<string>();
        foreach (var child in outcome.Result.Candidates)
        {
            if (child.Left.Kind != CandidateEndpointKind.Evidence || child.Right.Kind != CandidateEndpointKind.Evidence || child.ParentCandidateId != parent.CandidateId)
            {
                throw Invalid(outcome, $"il candidato {child.CandidateId} deve collegare due evidenze e citare il padre {parent.CandidateId}.");
            }

            if (child.CandidateId == parent.CandidateId)
            {
                continue;
            }

            Add(child, ViewOf(parent));
            added.Add(child.CandidateId);
        }

        return added;
    }

    private void Set(string id, CandidateState state, IReadOnlyList<string> children, IReadOnlyList<string> hashes, string? reason) =>
        dispositions[id] = new() { CandidateId = id, State = state, ChildCandidateIds = children, ComparisonOutputHashes = hashes, Reason = reason };

    private ReconcileView ViewOf(ReconcileCandidate candidate) => views.TryGetValue(candidate.CandidateId, out var view) ? view : DirectoryView(candidate);

    /// <summary>Vista di un candidato del server: quella dell'insieme di routing; le obbligazioni delle relazioni note si riaprono nella vista A.</summary>
    private ReconcileView DirectoryView(ReconcileCandidate candidate) =>
        sets.FirstOrDefault(s => s.SetId == candidate.Left.Id)?.View ?? ReconcileView.A;

    private List<ReconcileCandidate> Proposed(ReconcileView view)
    {
        var fromServer = directory.Candidates.Where(c => directory.Sets.Single(s => s.SetId == c.Left.Id).View == view);
        var fromModel = hierarchy.Outcomes.Where(o => o.Result.View == view).SelectMany(o => o.Result.Candidates);
        return [.. fromServer.Concat(fromModel)];
    }

    private IEnumerable<Guid> Members(CandidateEndpoint endpoint) => endpoint.Kind switch
    {
        CandidateEndpointKind.Evidence => [Guid.Parse(endpoint.Id)],
        CandidateEndpointKind.Node => membership[endpoint.Id],
        _ => sets.Single(s => s.SetId == endpoint.Id).MemberNodeIds.SelectMany(n => membership[n]),
    };

    /// <summary>Membership dei nodi: foglie dalle pagine Members, genitori come unione dei figli (mai dal descriptor).</summary>
    private void BuildMembership()
    {
        foreach (var group in hierarchy.Pages.Where(p => p.Kind == CatalogPageKind.Members).GroupBy(p => p.NodeId))
        {
            membership[group.Key] = [.. group.SelectMany(p => p.Entries).OfType<MemberPageEntry>().Select(m => m.EvidenceId)];
        }

        foreach (var node in hierarchy.Nodes.Where(n => n.Level > 0).OrderBy(n => n.Level))
        {
            membership[node.NodeId] = [.. node.ChildNodeIds.SelectMany(c => membership[c])];
        }
    }

    private static DocumentWorkException Invalid(BatchOutcome outcome, string detail) =>
        new(DocumentWorkException.WorkOutputInvalid, $"Output del batch {outcome.Batch.BatchId} non valido: {detail}");
}
