namespace bProjects.App.AI;

// Proiezioni relazionali minime di cataloghi, candidati e patch `reconcile-1` del run (002 T122; data-model.md «Cataloghi, candidati e patch del run (B3)»).
// L'autorità del payload resta WorkOutput/DerivedBatch (T040): queste righe sono append-only (trigger del database), referenziano il batch d'origine
// e non copiano mai EvidenceItem.Content né creano un indice di ricerca o un registro semantico parallelo. Nessun UPDATE né DELETE.

/// <summary>Nodo di routing del run: payload CatalogNode canonico (JSONB) più le colonne necessarie ai vincoli. Unique(run,view,level,ordinal).</summary>
public sealed class RunCatalogNode
{
    public Guid RunId { get; set; }
    public required string NodeId { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>Sempre ProcessingRun: il run è l'owner del batch che ha prodotto il nodo (FK composta su DerivedBatch).</summary>
    public WorkOwnerKind OwnerKind { get; set; } = WorkOwnerKind.ProcessingRun;
    public required string BatchId { get; set; }
    public ReconcileView View { get; set; }
    public int Level { get; set; }
    public int Ordinal { get; set; }
    public required string MemberSetHash { get; set; }
    public int MemberCount { get; set; }
    public required string DescriptorOutputHash { get; set; }
    public required string PayloadJson { get; set; }
}

/// <summary>
/// Pagina di un nodo (Members/Keys/Relations). La catena è imposta dal database: la pagina n richiede la n-1 dello stesso (nodo,kind) con l'hash
/// citato in <see cref="PreviousPageHash"/>, quindi nessun buco né hash discordante.
/// </summary>
public sealed class RunCatalogPage
{
    public Guid RunId { get; set; }
    public required string PageId { get; set; }
    public required string NodeId { get; set; }
    public CatalogPageKind Kind { get; set; }
    public int Ordinal { get; set; }
    public required string PageHash { get; set; }
    public string? PreviousPageHash { get; set; }

    /// <summary>Generata dal database (ordinal-1, null alla prima pagina): colonna della FK di catena.</summary>
    public int? PreviousOrdinal { get; private set; }
    public required string PayloadJson { get; set; }
}

/// <summary>Membro esatto di una foglia: solo identità (evidenza, versione, hash), mai il contenuto. Unique(run,view,level,evidenza): unione disgiunta per livello.</summary>
public sealed class RunCatalogMember
{
    public Guid RunId { get; set; }
    public required string NodeId { get; set; }
    public Guid EvidenceId { get; set; }
    public Guid EvidenceVersionId { get; set; }
    public required string ContentHash { get; set; }
    public ReconcileView View { get; set; }
    public int Level { get; set; }
}

/// <summary>Insieme di routing: solo membership (unione dei nodi), non una relazione di dominio.</summary>
public sealed class RunCatalogSet
{
    public Guid RunId { get; set; }
    public required string SetId { get; set; }
    public Guid ProjectId { get; set; }
    public ReconcileView View { get; set; }
    public required string MemberSetHash { get; set; }
    public int MemberCount { get; set; }
}

public sealed class RunCatalogSetNode
{
    public Guid RunId { get; set; }
    public required string SetId { get; set; }
    public required string NodeId { get; set; }
}

/// <summary>Candidato del run: unique(run,candidateId); le provenienze multiple sono righe <see cref="RunCandidateOrigin"/>.</summary>
public sealed class RunCandidate
{
    public Guid RunId { get; set; }
    public required string CandidateId { get; set; }
    public Guid ProjectId { get; set; }
    public required string SourceHash { get; set; }
    public required string InputHash { get; set; }
    public CandidateKind Kind { get; set; }
    public ReconcileProposedType ProposedType { get; set; }
    public required string PayloadJson { get; set; }
}

/// <summary>Provenienza append-only di un candidato: batch d'origine, hash dell'output e motivazione di quella proposta (le motivazioni non sono identità).</summary>
public sealed class RunCandidateOrigin
{
    public Guid RunId { get; set; }
    public required string CandidateId { get; set; }
    public required string BatchId { get; set; }
    public WorkOwnerKind OwnerKind { get; set; } = WorkOwnerKind.ProcessingRun;
    public required string OriginOutputHash { get; set; }
    public required string Rationale { get; set; }
}

/// <summary>Disposition versionata per passaggio (version ≥ 1, append-only): Pending, Expanded, Compared, Open, NotProcessed.</summary>
public sealed class RunCandidateDisposition
{
    public Guid RunId { get; set; }
    public required string CandidateId { get; set; }
    public int Version { get; set; }
    public CandidateState State { get; set; }
    public required string ChildCandidateIdsJson { get; set; }
    public required string ComparisonOutputHashesJson { get; set; }
    public string? Reason { get; set; }
}

/// <summary>Patch del run: unique(run,patchId), immutabile. Conserva sourceHash e hash degli output d'origine.</summary>
public sealed class RunPatch
{
    public Guid RunId { get; set; }
    public required string PatchId { get; set; }
    public Guid ProjectId { get; set; }
    public required string SourceHash { get; set; }
    public required string BaseHash { get; set; }
    public PatchTargetKind TargetKind { get; set; }
    public required string TargetId { get; set; }
    public required string ExpectedHash { get; set; }
    public required string PayloadJson { get; set; }

    /// <summary>Hash degli output d'origine, ordinati (JSONB array non vuoto).</summary>
    public required string OriginOutputHashesJson { get; set; }
}

/// <summary>Esito versionato di una patch. Terminali ammessi per completare: Applied, Duplicate, PreservedAsConflict; gli altri impediscono la completezza.</summary>
public sealed class RunPatchDisposition
{
    public Guid RunId { get; set; }
    public required string PatchId { get; set; }
    public int Version { get; set; }
    public RunPatchDispositionKind Kind { get; set; }
    public string? ResultHash { get; set; }
    public string? Reason { get; set; }
}

public enum RunPatchDispositionKind { Applied, Duplicate, PreservedAsConflict, Stale, Invalid, NotProcessed }

/// <summary>Righe delle proiezioni a partire dai DTO T071 e dal batch d'origine: il payload è la forma canonica RFC 8785 dei DTO, mai ricalcolata altrove.</summary>
public static class ReconciliationProjection
{
    public static RunCatalogPage Page(Guid runId, CatalogPage page) => new()
    {
        RunId = runId, PageId = page.PageId, NodeId = page.NodeId, Kind = page.Kind, Ordinal = page.Ordinal, PageHash = ReconciliationIds.PageHash(page),
        PreviousPageHash = page.PreviousPageHash, PayloadJson = ReconciliationCodec.Canonical(page),
    };

    public static IReadOnlyList<object> Catalog(Guid projectId, Guid runId, string batchId, ReconciliationCatalog catalog)
    {
        var rows = new List<object>();
        var nodesById = catalog.Nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        var membersByNode = catalog.Pages.Where(p => p.Kind == CatalogPageKind.Members).GroupBy(p => p.NodeId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var node in catalog.Nodes)
        {
            rows.Add(new RunCatalogNode
            {
                RunId = runId, NodeId = node.NodeId, ProjectId = projectId, BatchId = batchId, View = node.View, Level = node.Level, Ordinal = node.Ordinal,
                MemberSetHash = node.MemberSetHash, MemberCount = node.MemberCount, DescriptorOutputHash = node.DescriptorOutputHash, PayloadJson = ReconciliationCodec.Canonical(node),
            });
        }

        foreach (var page in catalog.Pages)
        {
            rows.Add(Page(runId, page));
        }

        foreach (var (nodeId, pages) in membersByNode)
        {
            nodesById.TryGetValue(nodeId, out var node);
            if (node is null)
            {
                continue;
            }

            foreach (var member in pages.SelectMany(p => p.Entries).OfType<MemberPageEntry>())
            {
                rows.Add(new RunCatalogMember
                {
                    RunId = runId, NodeId = nodeId, EvidenceId = member.EvidenceId, EvidenceVersionId = member.EvidenceVersionId, ContentHash = member.ContentHash,
                    View = node.View, Level = node.Level,
                });
            }
        }

        foreach (var set in catalog.Sets)
        {
            rows.Add(new RunCatalogSet { RunId = runId, SetId = set.SetId, ProjectId = projectId, View = set.View, MemberSetHash = set.MemberSetHash, MemberCount = set.MemberCount });
            rows.AddRange(set.MemberNodeIds.Select(nodeId => new RunCatalogSetNode { RunId = runId, SetId = set.SetId, NodeId = nodeId }));
        }

        return rows;
    }

    /// <summary>Candidato più una provenienza per ciascun batch d'origine (hash dell'output dato dal chiamante).</summary>
    public static IReadOnlyList<object> Candidate(Guid projectId, Guid runId, string sourceHash, string inputHash, ReconcileCandidate candidate, IReadOnlyDictionary<string, string> originOutputHashes)
    {
        var rows = new List<object>
        {
            new RunCandidate
            {
                RunId = runId, CandidateId = candidate.CandidateId, ProjectId = projectId, SourceHash = sourceHash, InputHash = inputHash, Kind = candidate.Kind,
                ProposedType = candidate.ProposedType, PayloadJson = ReconciliationCodec.Canonical(candidate),
            },
        };
        rows.AddRange(candidate.OriginBatchIds.Select(batchId => new RunCandidateOrigin
        {
            RunId = runId, CandidateId = candidate.CandidateId, BatchId = batchId, OriginOutputHash = originOutputHashes[batchId], Rationale = candidate.Rationale,
        }));
        return rows;
    }

    public static RunPatch Patch(Guid projectId, Guid runId, string sourceHash, Patch patch, IEnumerable<string> originOutputHashes) => new()
    {
        RunId = runId, PatchId = patch.PatchId, ProjectId = projectId, SourceHash = sourceHash, BaseHash = patch.BaseHash, TargetKind = patch.Target.Kind, TargetId = patch.Target.Id,
        ExpectedHash = patch.Target.ExpectedHash, PayloadJson = ReconciliationCodec.Canonical(patch),
        OriginOutputHashesJson = ReconciliationCodec.Encode(originOutputHashes.OrderBy(h => h, StringComparer.Ordinal).ToList()),
    };
}
