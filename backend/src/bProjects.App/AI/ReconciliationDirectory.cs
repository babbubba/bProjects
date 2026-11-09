using System.Text;
using System.Text.RegularExpressions;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// Directory esaustiva della riconciliazione gerarchica (002 T072; contracts/ai-workflow.md §4.2 punti 1-3). Funzione pura dello snapshot, dei fatti
// strutturali delle evidenze e dei misuratori: nessun LLM, nessun Guid né orologio, quindi il replay dà gli stessi hash. Nessun indice di ricerca,
// ranking o top-k: le chiavi si uniscono con una scansione esatta ordinata e ogni gruppo di chiavi genera un solo candidato di insiemi.

/// <summary>Fatti strutturali di una evidenza usati solo per le chiavi di routing (struttura e testi dei qualificatori); mai l'enunciato.</summary>
public sealed record DirectoryFacts(EvidenceStructure Structure, IReadOnlyList<string> Qualifiers);

/// <summary>Identità congelata, limiti e versioni condivisi da directory, livelli e round finale.</summary>
public sealed record ReconciliationContext(
    string Owner, string ManifestIdentity, string Definition, PlannerLimits Limits, string SchemaVersion = "reconcile-1", string PromptVersion = "document-reconcile-v2",
    int MaxLevels = 32);

/// <summary>Misuratori del payload completo per il Pack di foglie e di pagine (token non additivi: ogni prefisso è misurato).</summary>
public sealed record ReconciliationMeasurers(PackMeasurer<CatalogEntry> Leaf, PackMeasurer<PageEntry> Page);

/// <summary>Foglia pianificata: membri (ordine di foglia), batch derivato e pagine concatenate. Il nodo si completa col descriptor del batch (<see cref="ReconciliationDirectory.LeafNode"/>).</summary>
public sealed record LeafPlan(
    ReconcileView View, int Ordinal, string NodeId, string MemberSetHash, string BatchId, DerivedBatchInput Batch, IReadOnlyList<CatalogEntry> Entries, IReadOnlyList<CatalogPage> Pages);

public sealed record ReconciliationDirectoryResult(
    string InputHash, IReadOnlyList<CatalogEntry> Entries, IReadOnlyList<LeafPlan> Leaves, IReadOnlyList<CatalogSet> Sets, IReadOnlyList<ReconcileCandidate> Candidates,
    IReadOnlyList<ReconcileCandidate> KnownRelations);

public static partial class ReconciliationDirectory
{
    public const string SharedKeyRationale = "shared_literal_key";
    public const string KnownRelationRationale = "known_relation";

    public static async ValueTask<ReconciliationDirectoryResult> BuildAsync(
        ReconciliationSnapshot snapshot, IReadOnlyDictionary<Guid, DirectoryFacts> facts, ReconciliationContext context, ReconciliationMeasurers measurers, CancellationToken ct)
    {
        var evidence = snapshot.Evidence.ToDictionary(e => e.EvidenceId);
        foreach (var relation in snapshot.Relations.Where(r => !evidence.ContainsKey(r.FromEvidenceId) || !evidence.ContainsKey(r.ToEvidenceId)))
        {
            throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"La relazione nota {relation.RelationId:D} cita evidenze fuori dallo snapshot.");
        }

        var entries = snapshot.Evidence.OrderBy(e => e.EvidenceId.ToString("D"), StringComparer.Ordinal)
            .Select(e => Entry(snapshot, e, facts.GetValueOrDefault(e.EvidenceId), snapshot.Relations)).ToList();
        var inputHash = CanonicalJson.HashText(ReconciliationCodec.Canonical(new { algorithmVersion = ReconcileEnvelope<object>.AlgorithmVersionValue, snapshot.SourceHash, entries }));

        var leaves = new List<LeafPlan>();
        foreach (var view in new[] { ReconcileView.A, ReconcileView.B })
        {
            var ordered = entries.OrderBy(e => e, Comparer<CatalogEntry>.Create((a, b) => CompareLeafOrder(view, a, b))).ToList();
            var batches = await DocumentBatchPacking.PackLevelAsync(ordered, 1, context.Limits, e => e.EntryId, measurers.Leaf, ct);
            foreach (var batch in batches)
            {
                leaves.Add(await LeafAsync(view, batch, inputHash, context, measurers, snapshot, ct));
            }
        }

        var (sets, candidates) = SharedKeyCandidates(snapshot, leaves, entries);
        var known = KnownRelations(snapshot, leaves);
        return new(inputHash, entries, leaves, sets, candidates, known);
    }

    /// <summary>Normalizzazione delle sole chiavi: NFC, minuscolo invariante, spazi collassati e trim; vuoto = nessuna chiave (mai inventata).</summary>
    public static string? NormalizeKey(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = Whitespace().Replace(value.Normalize(NormalizationForm.FormC).ToLowerInvariant(), " ").Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>Nodo di foglia dal descriptor del suo batch (nodeId e membership sono già nel piano, indipendenti dalle pagine).</summary>
    public static CatalogNode LeafNode(LeafPlan plan, string inputHash, string descriptor, string descriptorOutputHash) => new()
    {
        NodeId = plan.NodeId, View = plan.View, Level = 0, Ordinal = plan.Ordinal, ChildNodeIds = [], MemberSetHash = plan.MemberSetHash, MemberCount = plan.Entries.Count,
        KeyPageHashes = PageHashes(plan.Pages, CatalogPageKind.Keys), RelationPageHashes = PageHashes(plan.Pages, CatalogPageKind.Relations),
        Descriptor = descriptor, DescriptorOutputHash = descriptorOutputHash,
    };

    public static int CompareKeys(CatalogKey a, CatalogKey b)
    {
        var c = a.View.CompareTo(b.View);
        c = c != 0 ? c : a.Kind.CompareTo(b.Kind);
        c = c != 0 ? c : string.CompareOrdinal(a.Value, b.Value);
        return c != 0 ? c : a.Origin.CompareTo(b.Origin);
    }

    internal static List<string> PageHashes(IEnumerable<CatalogPage> pages, CatalogPageKind kind) =>
        [.. pages.Where(p => p.Kind == kind).OrderBy(p => p.Ordinal).Select(ReconciliationIds.PageHash)];

    /// <summary>Catena di pagine contigue: ogni pagina cita l'hash della precedente; le pagine sono il massimo prefisso che entra in Pack.</summary>
    internal static async ValueTask<List<CatalogPage>> ChainAsync(
        string nodeId, CatalogPageKind kind, IReadOnlyList<PageEntry> all, ReconciliationContext context, ReconciliationMeasurers measurers, CancellationToken ct)
    {
        var pages = new List<CatalogPage>();
        if (all.Count == 0)
        {
            return pages;
        }

        string? previous = null;
        foreach (var batch in await DocumentBatchPacking.PackLevelAsync(all, 1, context.Limits, _ => nodeId, measurers.Page, ct))
        {
            var page = new CatalogPage { PageId = "x", NodeId = nodeId, Kind = kind, Ordinal = batch.Ordinal, Entries = batch.Members, PreviousPageHash = previous };
            page = page with { PageId = ReconciliationIds.PageId(page) };
            pages.Add(page);
            previous = ReconciliationIds.PageHash(page);
        }

        return pages;
    }

    private static CatalogEntry Entry(ReconciliationSnapshot snapshot, SnapshotEvidence evidence, DirectoryFacts? facts, IReadOnlyList<SnapshotRelation> relations)
    {
        var keys = new List<CatalogKey>();
        if (facts is not null)
        {
            void Add(ReconcileView view, CatalogKeyKind kind, string? raw)
            {
                if (NormalizeKey(raw) is { } value)
                {
                    keys.Add(new CatalogKey { View = view, Kind = kind, Value = value, Origin = CatalogKeyOrigin.Literal, OriginOutputHash = null });
                }
            }

            Add(ReconcileView.A, CatalogKeyKind.Subject, facts.Structure.Subject);
            Add(ReconcileView.A, CatalogKeyKind.Object, facts.Structure.Object);
            Add(ReconcileView.A, CatalogKeyKind.Scope, facts.Structure.Scope);
            Add(ReconcileView.B, CatalogKeyKind.Action, facts.Structure.Action);
            Add(ReconcileView.B, CatalogKeyKind.Instrument, facts.Structure.Instrument);
            Add(ReconcileView.B, CatalogKeyKind.Channel, facts.Structure.Channel);
            foreach (var qualifier in facts.Qualifiers)
            {
                Add(ReconcileView.B, CatalogKeyKind.Qualifier, qualifier);
            }
        }

        keys.Sort(CompareKeys);
        var distinct = keys.Where((k, i) => i == 0 || CompareKeys(keys[i - 1], k) != 0).ToList();
        return new()
        {
            EntryId = ReconciliationIds.EntryId(snapshot.SourceHash, evidence.EvidenceId, evidence.ContentHash), EvidenceId = evidence.EvidenceId,
            EvidenceVersionId = evidence.EvidenceVersionId, ContentHash = evidence.ContentHash,
            SourceIds = [.. evidence.Sources.Select(s => s.SourceId).Distinct().OrderBy(g => g.ToString("D"), StringComparer.Ordinal)],
            Keys = distinct,
            KnownRelationIds = [.. relations.Where(r => r.FromEvidenceId == evidence.EvidenceId || r.ToEvidenceId == evidence.EvidenceId).Select(r => r.RelationId)
                .Distinct().OrderBy(g => g.ToString("D"), StringComparer.Ordinal)],
            UnclassifiedViews = [.. new[] { ReconcileView.A, ReconcileView.B }.Where(v => !distinct.Any(k => k.View == v))],
        };
    }

    /// <summary>Ordine foglie (primaria, EvidenceId): primaria = minima (kind,value) della vista, le senza chiavi nel bucket unclassified in coda.</summary>
    private static int CompareLeafOrder(ReconcileView view, CatalogEntry a, CatalogEntry b)
    {
        var pa = a.Keys.FirstOrDefault(k => k.View == view);
        var pb = b.Keys.FirstOrDefault(k => k.View == view);
        var c = (pa is null).CompareTo(pb is null);
        if (c == 0 && pa is not null && pb is not null)
        {
            c = pa.Kind.CompareTo(pb.Kind);
            c = c != 0 ? c : string.CompareOrdinal(pa.Value, pb.Value);
        }

        return c != 0 ? c : string.CompareOrdinal(a.EvidenceId.ToString("D"), b.EvidenceId.ToString("D"));
    }

    private static async ValueTask<LeafPlan> LeafAsync(
        ReconcileView view, PackedBatch<CatalogEntry> batch, string inputHash, ReconciliationContext context, ReconciliationMeasurers measurers, ReconciliationSnapshot snapshot,
        CancellationToken ct)
    {
        var members = batch.Members.OrderBy(e => e.EvidenceId.ToString("D"), StringComparer.Ordinal)
            .Select(e => new MemberPageEntry { EvidenceId = e.EvidenceId, EvidenceVersionId = e.EvidenceVersionId, ContentHash = e.ContentHash }).ToList();
        var memberSetHash = ReconciliationIds.MemberSetHash(members);
        var nodeId = ReconciliationIds.NodeId(view, 0, batch.Ordinal, memberSetHash, [], inputHash);

        var keyEntries = batch.Members.SelectMany(e => e.Keys.Where(k => k.View == view).Select(k => (Key: k, e.EvidenceId)))
            .GroupBy(t => t.Key).OrderBy(g => g.Key, Comparer<CatalogKey>.Create(CompareKeys))
            .Select(g => (PageEntry)new KeyPageEntry
            {
                Key = g.Key, MemberIds = [.. g.Select(t => t.EvidenceId).Distinct().OrderBy(id => id.ToString("D"), StringComparer.Ordinal)], MemberPageRefs = [],
            }).ToList();
        var ids = members.Select(m => m.EvidenceId).ToHashSet();
        var relationEntries = snapshot.Relations.Where(r => ids.Contains(r.FromEvidenceId)).OrderBy(r => r.RelationId.ToString("D"), StringComparer.Ordinal)
            .Select(r => (PageEntry)new RelationPageEntry { RelationId = r.RelationId, FromEvidenceId = r.FromEvidenceId, ToEvidenceId = r.ToEvidenceId, Type = r.Type, Basis = r.Basis }).ToList();

        var pages = new List<CatalogPage>();
        pages.AddRange(await ChainAsync(nodeId, CatalogPageKind.Members, [.. members], context, measurers, ct));
        pages.AddRange(await ChainAsync(nodeId, CatalogPageKind.Keys, keyEntries, context, measurers, ct));
        pages.AddRange(await ChainAsync(nodeId, CatalogPageKind.Relations, relationEntries, context, measurers, ct));

        var derived = DocumentBatchPacking.ToDerived(
            batch, context.Owner, context.ManifestIdentity, context.Definition, "leaf", view.ToString(), 0, [], e => e.EntryId, context.SchemaVersion, context.PromptVersion);
        return new(view, batch.Ordinal, nodeId, memberSetHash, derived.BatchId, derived, batch.Members, pages);
    }

    /// <summary>
    /// Merge esatto delle chiavi per vista: una chiave (kind,value) con membri in k≥2 foglie genera due CatalogSet (prime ceil(k/2) foglie per ordinal e le rimanenti)
    /// e UN candidato fra i due insiemi, senza coppie né scansioni cumulative. Duplicati fra chiavi diverse uniscono le origini.
    /// </summary>
    internal static (IReadOnlyList<CatalogSet> Sets, IReadOnlyList<ReconcileCandidate> Candidates) SharedKeyCandidates(
        ReconciliationSnapshot snapshot, IReadOnlyList<LeafPlan> leaves, IReadOnlyList<CatalogEntry> entries)
    {
        var leafOf = new Dictionary<(ReconcileView, Guid), LeafPlan>();
        foreach (var leaf in leaves)
        {
            foreach (var entry in leaf.Entries)
            {
                leafOf[(leaf.View, entry.EvidenceId)] = leaf;
            }
        }

        var groups = new SortedDictionary<(ReconcileView View, CatalogKeyKind Kind, string Value), SortedSet<int>>(
            Comparer<(ReconcileView View, CatalogKeyKind Kind, string Value)>.Create((a, b) =>
            {
                var c = a.View.CompareTo(b.View);
                c = c != 0 ? c : a.Kind.CompareTo(b.Kind);
                return c != 0 ? c : string.CompareOrdinal(a.Value, b.Value);
            }));
        foreach (var entry in entries)
        {
            foreach (var key in entry.Keys)
            {
                var id = (key.View, key.Kind, key.Value);
                if (!groups.TryGetValue(id, out var set))
                {
                    groups[id] = set = [];
                }

                set.Add(leafOf[(key.View, entry.EvidenceId)].Ordinal);
            }
        }

        var sets = new Dictionary<string, CatalogSet>(StringComparer.Ordinal);
        var candidates = new Dictionary<string, ReconcileCandidate>(StringComparer.Ordinal);
        foreach (var ((view, _, _), ordinals) in groups.Where(g => g.Value.Count >= 2))
        {
            var viewLeaves = ordinals.Select(o => leaves.Single(l => l.View == view && l.Ordinal == o)).ToList();
            var half = (viewLeaves.Count + 1) / 2;
            var left = Routing(view, viewLeaves.Take(half).ToList(), snapshot, sets);
            var right = Routing(view, viewLeaves.Skip(half).ToList(), snapshot, sets);
            var l = new CandidateEndpoint { Kind = CandidateEndpointKind.Set, Id = left.SetId, Hash = left.MemberSetHash };
            var r = new CandidateEndpoint { Kind = CandidateEndpointKind.Set, Id = right.SetId, Hash = right.MemberSetHash };
            AddCandidate(candidates, snapshot, CandidateKind.Relation, l, r, ReconcileProposedType.SameRule, SharedKeyRationale, EvidenceRelationBasis.Inferred, viewLeaves.Select(x => x.BatchId));
        }

        return ([.. sets.Values.OrderBy(s => s.SetId, StringComparer.Ordinal)], [.. candidates.Values.OrderBy(c => c.CandidateId, StringComparer.Ordinal)]);
    }

    /// <summary>Obbligazione diretta sui due EvidenceId di ogni relazione nota (candidato Evidence-Evidence), con origine nelle foglie A degli endpoint.</summary>
    private static IReadOnlyList<ReconcileCandidate> KnownRelations(ReconciliationSnapshot snapshot, IReadOnlyList<LeafPlan> leaves)
    {
        var evidence = snapshot.Evidence.ToDictionary(e => e.EvidenceId);
        var candidates = new Dictionary<string, ReconcileCandidate>(StringComparer.Ordinal);
        foreach (var relation in snapshot.Relations.OrderBy(r => r.RelationId.ToString("D"), StringComparer.Ordinal))
        {
            CandidateEndpoint Endpoint(Guid id) => new() { Kind = CandidateEndpointKind.Evidence, Id = id.ToString("D"), Hash = evidence[id].ContentHash };
            var origins = new[] { relation.FromEvidenceId, relation.ToEvidenceId }
                .Select(id => leaves.Single(l => l.View == ReconcileView.A && l.Entries.Any(e => e.EvidenceId == id)).BatchId);
            var type = Enum.Parse<ReconcileProposedType>(relation.Type.ToString());
            var kind = type is ReconcileProposedType.Contradicts or ReconcileProposedType.AlternativeTo ? CandidateKind.Conflict : CandidateKind.Relation;
            AddCandidate(candidates, snapshot, kind, Endpoint(relation.FromEvidenceId), Endpoint(relation.ToEvidenceId), type, KnownRelationRationale, relation.Basis, origins);
        }

        return [.. candidates.Values.OrderBy(c => c.CandidateId, StringComparer.Ordinal)];
    }

    private static CatalogSet Routing(ReconcileView view, IReadOnlyList<LeafPlan> nodes, ReconciliationSnapshot snapshot, Dictionary<string, CatalogSet> sets)
    {
        var members = nodes.SelectMany(n => n.Entries).Select(e => new MemberPageEntry { EvidenceId = e.EvidenceId, EvidenceVersionId = e.EvidenceVersionId, ContentHash = e.ContentHash }).ToList();
        var hash = ReconciliationIds.MemberSetHash(members);
        var nodeIds = nodes.Select(n => n.NodeId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var set = new CatalogSet { SetId = ReconciliationIds.SetId(view, nodeIds, hash), View = view, MemberNodeIds = nodeIds, MemberSetHash = hash, MemberCount = members.Count };
        sets.TryAdd(set.SetId, set);
        return set;
    }

    internal static void AddCandidate(
        Dictionary<string, ReconcileCandidate> candidates, ReconciliationSnapshot snapshot, CandidateKind kind, CandidateEndpoint left, CandidateEndpoint right,
        ReconcileProposedType type, string rationale, EvidenceRelationBasis basis, IEnumerable<string> originBatchIds, string? parentCandidateId = null)
    {
        var id = ReconciliationIds.CandidateId(kind, left, right, type, snapshot.SourceHash);
        var origins = originBatchIds.Concat(candidates.TryGetValue(id, out var existing) ? existing.OriginBatchIds : []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        candidates[id] = existing is null
            ? new ReconcileCandidate
            {
                CandidateId = id, Kind = kind, Left = left, Right = right, ProposedType = type, Rationale = rationale, Basis = basis, OriginBatchIds = origins,
                ParentCandidateId = parentCandidateId,
            }
            : existing with { OriginBatchIds = origins };
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
