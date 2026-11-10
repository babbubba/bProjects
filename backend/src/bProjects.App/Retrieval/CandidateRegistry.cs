using System.Text.Json;
using System.Text.Json.Serialization;
using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.App.Retrieval;

/// <summary>
/// Un Gap da interrogare: identificativo canonico dell'evidenza Gap nel run, testo canonico dell'interrogazione e fusione RRF dei suoi due segnali.
/// <paramref name="FinalOrder"/> è l'ordine finale dei <c>BlockId</c> dopo il riordino (T023; permutazione esatta dei blocchi di <paramref name="Fused"/>); assente = ordine RRF
/// (M1, oppure riordino non disponibile: ordine diagnostico).
/// </summary>
public sealed record RegistryGapInput(Guid GapEvidenceId, string Query, IReadOnlyList<FusedBlock> Fused, IReadOnlyList<Guid>? FinalOrder = null);

/// <summary>
/// Budget del pacchetto: <see cref="Limit"/> in unità del costo per evidenza (token dello stadio) e <see cref="Cost"/> = conteggio MISURATO dal chiamante per ogni evidenza candidata.
/// Un costo mancante è un errore (<see cref="CandidatePacker"/>): nessuna evidenza vale zero e nessun costo è stimato.
/// </summary>
public sealed record RegistryBudget(long Limit, IReadOnlyDictionary<Guid, long> Cost)
{
    /// <summary>
    /// Budget del pacchetto dal <see cref="ContextBudget"/> dello stadio: budget di input meno i token del payload fisso (istruzioni, elenco dei Gap, schema della risposta) misurati dal
    /// chiamante. Payload fisso oltre il budget ⇒ limite 0 (nessuna evidenza entra).
    /// </summary>
    public static RegistryBudget For(ContextBudget stage, long fixedPayloadTokens, IReadOnlyDictionary<Guid, long> cost)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fixedPayloadTokens);
        ArgumentNullException.ThrowIfNull(cost);
        return new(Math.Max(0, stage.InputBudgetTokens - fixedPayloadTokens), cost);
    }
}

/// <summary>Modello come dichiarato nel registro (nessun valore dedotto: ciò che la readiness ha osservato).</summary>
public sealed record RegistryModelReadiness(string Model, string Artifact, int? Dimension, string Sha256Status);

public sealed record RegistryCandidate(
    Guid BlockId, Guid DocumentId, string DocumentName, int DocumentVersion, string Section, int[] Range, string TextHash,
    int? LexicalRank, int? DenseRank, int RrfRank, int FinalRank, IReadOnlyList<Guid> EvidenceIds, string Origin, bool InPackage, string? ExcludedReason);

public sealed record RegistryGap(Guid GapEvidenceId, string Query, IReadOnlyList<RegistryCandidate> Candidates);

public sealed record RegistryExclusion(Guid EvidenceId, string Reason);

/// <summary>Evidenza <c>Message</c> non Gap sempre offerta allo stadio (FR-006): il lato PM di un conflitto non si perde mai per il ranking.</summary>
public sealed record RegistryAlwaysIncluded(Guid EvidenceId, bool InPackage);

public sealed record RegistryReadiness(RegistryModelReadiness? Embedding, RegistryModelReadiness? Reranker);

/// <summary>Registro dei candidati <c>retrieval-registry-1</c> (contracts/index-and-registry.md §2): congelabile nel <c>WorkOutput</c> del run. Nessun punteggio presentato come probabilità.</summary>
public sealed record RetrievalRegistry(
    string Contract, string SnapshotHash, string IndexConfigHash, string PolicyVersion, string PolicyHash, string SearchBasis, string Outcome,
    bool RetrievalComplete, bool Degraded, IReadOnlyList<string> Causes, RegistryReadiness? Readiness, IReadOnlyList<RegistryGap> Gaps,
    IReadOnlyList<RegistryAlwaysIncluded> AlwaysIncluded, IReadOnlyList<Guid> PackageEvidence, IReadOnlyList<RegistryExclusion> Excluded)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>JSON canonico (chiavi ordinate, nessuno spazio) usato per l'hash di output e per il congelamento.</summary>
    public string ToCanonicalJson() => CanonicalJson.Canonicalize(JsonSerializer.Serialize(this, Json));

    public string Hash() => CanonicalJson.HashText(ToCanonicalJson());

    /// <summary>Registro dal suo JSON (WorkOutput <c>retrieval-output-1</c>): stesse opzioni della serializzazione, così hash e ricostruzione coincidono.</summary>
    public static RetrievalRegistry FromJson(string json) =>
        JsonSerializer.Deserialize<RetrievalRegistry>(json, Json) ?? throw new JsonException("Registro del recupero vuoto.");
}

/// <summary>
/// Costruzione del registro dei candidati (003 T015, M1: senza riordino). Funzione pura e deterministica: dalle fusioni RRF per Gap, dalla provenienza dei blocchi e dal
/// ledger congelato produce le voci per Gap, l'unione deduplicata ordinata e il pacchetto per rango fino al budget, a passaggi interi (FR-012). Regole:
/// <list type="bullet">
/// <item>blocco senza evidenze ⇒ voce <c>no_evidence_for_block</c>, mai candidato dello stadio (R-3);</item>
/// <item>evidenza = rango del suo blocco migliore; a pari rango, ordine di catalogo; fra Gap, il rango migliore e poi l'ordine di catalogo (R-6);</item>
/// <item>evidenze <c>Message</c> non Gap sempre incluse (<c>always_included</c>, ordine di catalogo) e per prime nel budget; se non entrano ⇒ <c>retrievalComplete = false</c>;</item>
/// <item>cutoff per budget, mai per punteggio; si scende per rango e ci si ferma alla prima evidenza che non entra (nessun salto a evidenze di rango inferiore);</item>
/// <item>cause di servizio ⇒ <c>Failed</c> (embedding/indice) o <c>Diagnostic</c> (solo riordino non disponibile): in entrambi nessun pacchetto, <c>degraded</c> o <c>retrievalComplete = false</c>.</item>
/// </list>
/// Il registro non dichiara mai «assente» e <c>retrievalComplete</c> non è prova di completezza semantica (FR-019, A-10).
/// </summary>
public static class CandidateRegistry
{
    public const string Contract = "retrieval-registry-1";
    public const string SearchBasis = "retrieval";
    public const string OutcomePackage = "Package";
    public const string OutcomeDiagnostic = "Diagnostic";
    public const string OutcomeFailed = "Failed";

    public const string OriginRanked = "ranked";
    public const string OriginAlwaysIncluded = "always_included";

    public const string ReasonBudget = "budget";
    public const string ReasonNoEvidence = "no_evidence_for_block";
    public const string ReasonDegraded = "degraded";

    public static RetrievalRegistry Build(
        string snapshotHash, string indexConfigHash, string policyVersion, string policyHash,
        IReadOnlyList<RegistryGapInput> gaps, IReadOnlyDictionary<Guid, BlockProvenance> blocks, IReadOnlyList<RegistryEvidence> evidences,
        IReadOnlyList<string>? causes = null, RegistryBudget? budget = null, RegistryReadiness? readiness = null, int? rerankWindow = null)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(evidences);
        causes = [.. (causes ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        var mapping = BlockEvidenceMapper.BlockToEvidence(evidences);
        var catalogIndex = evidences.Select((e, index) => (e.EvidenceId, index)).ToDictionary(t => t.EvidenceId, t => t.index);

        // Rango migliore di ogni evidenza per Gap e fra Gap.
        var bestAcross = new Dictionary<Guid, int>();
        var perGap = new List<(RegistryGapInput Gap, List<(FusedBlock Fused, BlockProvenance Prov, IReadOnlyList<Guid> Evidence, int FinalRank)> Rows)>();
        foreach (var gap in gaps)
        {
            var finalRanks = FinalRanks(gap);
            var rows = new List<(FusedBlock, BlockProvenance, IReadOnlyList<Guid>, int)>();
            foreach (var fused in gap.Fused.OrderBy(f => finalRanks[f.BlockId]))
            {
                if (!blocks.TryGetValue(fused.BlockId, out var prov))
                {
                    throw new ArgumentException($"Provenienza mancante per il blocco {fused.BlockId}.", nameof(blocks));
                }

                var ids = mapping.GetValueOrDefault(fused.BlockId) ?? [];
                var finalRank = finalRanks[fused.BlockId];
                rows.Add((fused, prov, ids, finalRank));
                foreach (var id in ids)
                {
                    bestAcross[id] = bestAcross.TryGetValue(id, out var current) ? Math.Min(current, finalRank) : finalRank;
                }
            }

            perGap.Add((gap, rows));
        }

        var ranked = bestAcross.Keys.OrderBy(id => bestAcross[id]).ThenBy(id => catalogIndex.GetValueOrDefault(id, int.MaxValue)).ThenBy(id => id).ToList();
        var always = evidences.Where(e => e.Origin == EvidenceOriginKind.Message && e.Kind != EvidenceKind.Gap).Select(e => e.EvidenceId).ToList();

        var failed = causes.Any(c => !IsRerankerCause(c));
        var degraded = causes.Count > 0;
        var outcome = causes.Count == 0 ? OutcomePackage : failed ? OutcomeFailed : OutcomeDiagnostic;

        // Pacchetto: nessuno con cause di servizio; altrimenti always_included e poi rango, a passaggi interi fino al budget.
        var packed = outcome == OutcomePackage
            ? CandidatePacker.Pack(always, [.. ranked.Select(id => (id, bestAcross[id]))], budget, rerankWindow)
            : new PackResult([], [], Complete: false);
        var package = packed.Package;
        var excluded = packed.Excluded;

        var inPackage = package.ToHashSet();
        var registryGaps = perGap.Select(g => new RegistryGap(g.Gap.GapEvidenceId, g.Gap.Query, [.. g.Rows.Select(row =>
        {
            var (fused, prov, ids, finalRank) = row;
            var inside = outcome == OutcomePackage && ids.Any(inPackage.Contains);
            var reason = inside ? null
                : ids.Count == 0 ? ReasonNoEvidence
                : outcome != OutcomePackage ? ReasonDegraded
                : ReasonBudget;
            return new RegistryCandidate(
                fused.BlockId, prov.DocumentId, prov.DocumentName, prov.DocumentVersion, prov.Section, [prov.RangeStart, prov.RangeEnd], prov.TextHash,
                fused.LexicalRank, fused.DenseRank, fused.RrfRank, finalRank, ids, OriginRanked, inside, reason);
        })])).ToList();

        return new(
            Contract, snapshotHash, indexConfigHash, policyVersion, policyHash, SearchBasis, outcome,
            RetrievalComplete: causes.Count == 0 && packed.Complete,
            Degraded: degraded,
            Causes: causes,
            Readiness: readiness,
            Gaps: registryGaps,
            AlwaysIncluded: [.. always.Select(id => new RegistryAlwaysIncluded(id, inPackage.Contains(id)))],
            PackageEvidence: package,
            Excluded: excluded);
    }

    /// <summary>Rango finale 1-based di ogni blocco del Gap: la posizione nell'ordine riordinato oppure, senza riordino, il rango RRF. L'ordine riordinato deve essere una permutazione esatta.</summary>
    private static Dictionary<Guid, int> FinalRanks(RegistryGapInput gap)
    {
        if (gap.FinalOrder is null)
        {
            return gap.Fused.ToDictionary(f => f.BlockId, f => f.RrfRank);
        }

        var expected = gap.Fused.Select(f => f.BlockId).ToHashSet();
        if (gap.FinalOrder.Count != expected.Count || !gap.FinalOrder.ToHashSet().SetEquals(expected))
        {
            throw new ArgumentException("L'ordine finale deve contenere esattamente una volta i blocchi della fusione del Gap.", nameof(gap));
        }

        return gap.FinalOrder.Select((id, index) => (id, rank: index + 1)).ToDictionary(t => t.id, t => t.rank);
    }

    private static bool IsRerankerCause(string cause) =>
        cause is RetrievalException.RerankerUnavailable or RetrievalException.RerankerMalformedResponse;
}
