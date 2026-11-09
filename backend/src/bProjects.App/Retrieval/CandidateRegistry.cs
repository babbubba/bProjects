using System.Text.Json;
using System.Text.Json.Serialization;
using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.App.Retrieval;

/// <summary>Un Gap da interrogare: identificativo canonico dell'evidenza Gap nel run, testo canonico dell'interrogazione e fusione RRF dei suoi due segnali.</summary>
public sealed record RegistryGapInput(Guid GapEvidenceId, string Query, IReadOnlyList<FusedBlock> Fused);

/// <summary>Budget del pacchetto: <see cref="Limit"/> in unità del costo per evidenza (token del <c>ContextBudget</c> dello stadio, calcolati dal chiamante); evidenza senza costo noto = 0.</summary>
public sealed record RegistryBudget(long Limit, IReadOnlyDictionary<Guid, long> Cost);

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
        IReadOnlyList<string>? causes = null, RegistryBudget? budget = null, RegistryReadiness? readiness = null)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(evidences);
        causes = [.. (causes ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        var mapping = BlockEvidenceMapper.BlockToEvidence(evidences);
        var catalogIndex = evidences.Select((e, index) => (e.EvidenceId, index)).ToDictionary(t => t.EvidenceId, t => t.index);

        // Rango migliore di ogni evidenza per Gap e fra Gap.
        var bestAcross = new Dictionary<Guid, int>();
        var perGap = new List<(RegistryGapInput Gap, List<(FusedBlock Fused, BlockProvenance Prov, IReadOnlyList<Guid> Evidence)> Rows)>();
        foreach (var gap in gaps)
        {
            var rows = new List<(FusedBlock, BlockProvenance, IReadOnlyList<Guid>)>();
            foreach (var fused in gap.Fused.OrderBy(f => f.RrfRank))
            {
                if (!blocks.TryGetValue(fused.BlockId, out var prov))
                {
                    throw new ArgumentException($"Provenienza mancante per il blocco {fused.BlockId}.", nameof(blocks));
                }

                var ids = mapping.GetValueOrDefault(fused.BlockId) ?? [];
                rows.Add((fused, prov, ids));
                foreach (var id in ids)
                {
                    bestAcross[id] = bestAcross.TryGetValue(id, out var current) ? Math.Min(current, fused.RrfRank) : fused.RrfRank;
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
        var package = new List<Guid>();
        var excluded = new List<RegistryExclusion>();
        var alwaysExcluded = false;
        var rankedExcluded = false;
        if (outcome == OutcomePackage)
        {
            var limit = budget?.Limit ?? long.MaxValue;
            long used = 0;
            long Cost(Guid id) => budget?.Cost.GetValueOrDefault(id) ?? 0;
            foreach (var id in always)
            {
                if (used + Cost(id) <= limit)
                {
                    used += Cost(id);
                    package.Add(id);
                }
                else
                {
                    excluded.Add(new(id, ReasonBudget));
                    alwaysExcluded = true;
                }
            }

            var stopped = false;
            foreach (var id in ranked)
            {
                if (!stopped && used + Cost(id) <= limit)
                {
                    used += Cost(id);
                    package.Add(id);
                }
                else
                {
                    stopped = true;
                    excluded.Add(new(id, ReasonBudget));
                    rankedExcluded = true;
                }
            }
        }

        var inPackage = package.ToHashSet();
        var registryGaps = perGap.Select(g => new RegistryGap(g.Gap.GapEvidenceId, g.Gap.Query, [.. g.Rows.Select(row =>
        {
            var (fused, prov, ids) = row;
            var inside = outcome == OutcomePackage && ids.Any(inPackage.Contains);
            var reason = inside ? null
                : ids.Count == 0 ? ReasonNoEvidence
                : outcome != OutcomePackage ? ReasonDegraded
                : ReasonBudget;
            return new RegistryCandidate(
                fused.BlockId, prov.DocumentId, prov.DocumentName, prov.DocumentVersion, prov.Section, [prov.RangeStart, prov.RangeEnd], prov.TextHash,
                fused.LexicalRank, fused.DenseRank, fused.RrfRank, fused.RrfRank, ids, OriginRanked, inside, reason);
        })])).ToList();

        return new(
            Contract, snapshotHash, indexConfigHash, policyVersion, policyHash, SearchBasis, outcome,
            RetrievalComplete: causes.Count == 0 && !alwaysExcluded && !rankedExcluded,
            Degraded: degraded,
            Causes: causes,
            Readiness: readiness,
            Gaps: registryGaps,
            AlwaysIncluded: [.. always.Select(id => new RegistryAlwaysIncluded(id, inPackage.Contains(id)))],
            PackageEvidence: package,
            Excluded: excluded);
    }

    private static bool IsRerankerCause(string cause) =>
        cause is RetrievalException.RerankerUnavailable or RetrievalException.RerankerMalformedResponse;
}
