using System.Text;
using System.Text.Json;

namespace bProjects.App.AI;

// Foglie e riduzione di livelli (002 T073; contracts/ai-workflow.md §4.2 punti 4-5). Il confine con il modello è un delegato che restituisce il testo JSON
// dell'output (il collegamento al grafo MAF/WorkOutput lo fornisce il chiamante): qui si decodifica strict, si valida e si costruisce la gerarchia.
// La membership di ogni nodo è SEMPRE calcolata dal server (unione esatta dei figli); il descriptor del modello serve solo a instradare e non entra in alcun hash di membership.

/// <summary>Richiesta di un batch: foglie con voci integrali, riduzioni e Cross con soli nodi (descriptor); espansioni e confronti con voci integrali e il candidato in <c>Focus</c>.</summary>
public sealed record ReconcileBatchRequest(
    DerivedBatchInput Batch, ReconcileView View, int Level, IReadOnlyList<CatalogEntry> Entries, IReadOnlyList<CatalogNode> Nodes, IReadOnlyList<ReconcileCandidate>? Focus = null,
    ExpansionGroups? Groups = null);

/// <summary>
/// Membership esplicita dell'espansione (formato <c>membership-1</c>, solo definizione v10): per ogni evidenza del batch, il gruppo del candidato in esame a cui appartiene (solo sinistro, solo destro, entrambi).
/// Calcolata dal server sugli insiemi INTERI dei due estremi, mai sul solo blocco. <c>null</c> per ogni altro stadio e per le definizioni v4–v9: il messaggio resta quello storico, byte per byte.
/// </summary>
public sealed record ExpansionGroups(IReadOnlyDictionary<Guid, ExpansionTag> Tags);

/// <summary>Invia il batch al modello (con lookup/riserva/commit del chiamante) e restituisce il testo JSON dell'output.</summary>
public delegate ValueTask<string> ReconcileInvoker(ReconcileBatchRequest request, CancellationToken ct);

/// <summary>Output validato di un batch con il suo hash (JSON canonico del risultato).</summary>
public sealed record BatchOutcome(DerivedBatchInput Batch, string OutputHash, ReconcileResult Result);

public sealed record HierarchyResult(
    IReadOnlyList<CatalogNode> Nodes, IReadOnlyList<CatalogPage> Pages, IReadOnlyDictionary<ReconcileView, string> Roots, IReadOnlyList<string> Carries,
    IReadOnlyList<BatchOutcome> Outcomes);

public static class ReconciliationHierarchy
{
    public const string PromptVersion = "document-reconcile-v2";
    private static readonly Lazy<string> Prompt = new(() => LoadPrompt(PromptVersion + ".md"));

    public static string Instructions => Prompt.Value;

    public static async ValueTask<HierarchyResult> BuildAsync(
        ReconciliationSnapshot snapshot, ReconciliationDirectoryResult directory, ReconciliationContext context, PackMeasurer<CatalogNode> descriptorMeasurer, ReconcileInvoker invoke,
        CancellationToken ct)
    {
        var nodes = new List<CatalogNode>();
        var pages = new List<CatalogPage>();
        var outcomes = new List<BatchOutcome>();
        var carries = new List<string>();
        var roots = new Dictionary<ReconcileView, string>();
        var catalog = new ReconciliationCatalog(nodes, pages, directory.Sets);   // viste sul catalogo costruito finora, per risolvere gli endpoint dei candidati
        var members = new Dictionary<string, List<MemberPageEntry>>(StringComparer.Ordinal);

        async ValueTask<BatchOutcome> Run(DerivedBatchInput batch, ReconcileView view, int level, IReadOnlyList<CatalogEntry> entries, IReadOnlyList<CatalogNode> inputNodes)
        {
            var outcome = await InvokeAsync(batch, view, level, entries, inputNodes, snapshot, catalog, invoke, view != ReconcileView.Cross, null, ct);
            outcomes.Add(outcome);
            return outcome;
        }

        foreach (var view in new[] { ReconcileView.A, ReconcileView.B })
        {
            var current = new List<CatalogNode>();
            foreach (var leaf in directory.Leaves.Where(l => l.View == view).OrderBy(l => l.Ordinal))
            {
                var outcome = await Run(leaf.Batch, view, 0, leaf.Entries, []);
                var node = ReconciliationDirectory.LeafNode(leaf, directory.InputHash, outcome.Result.Descriptor!, outcome.OutputHash);
                nodes.Add(node);
                pages.AddRange(leaf.Pages);
                members[node.NodeId] = [.. leaf.Entries.OrderBy(e => e.EvidenceId.ToString("D"), StringComparer.Ordinal).Select(Member)];
                current.Add(node);
            }

            var level = 0;
            while (current.Count > 1)
            {
                level++;
                if (level >= context.MaxLevels)
                {
                    throw new DocumentWorkException(
                        DocumentWorkException.CoverageIncomplete, $"Vista {view}: servono più dei {context.MaxLevels} livelli congelati ({current.Count} nodi al livello {level - 1}).");
                }

                var next = new List<CatalogNode>();
                foreach (var batch in await DocumentBatchPacking.PackLevelAsync(current, 2, context.Limits, n => n.NodeId, descriptorMeasurer, ct))
                {
                    if (batch.Carried)
                    {
                        var child = batch.Members[0];
                        var carried = Parent(view, level, batch.Ordinal, [child], members, directory.InputHash, child.Descriptor, child.DescriptorOutputHash);
                        carries.Add(carried.NodeId);
                        next.Add(carried);
                        nodes.Add(carried);
                        continue;
                    }

                    var derived = DocumentBatchPacking.ToDerived(
                        batch, context.Owner, context.ManifestIdentity, context.Definition, "reduce", view.ToString(), level, [.. batch.Members.Select(n => n.DescriptorOutputHash)],
                        n => n.NodeId, context.SchemaVersion, context.PromptVersion);
                    var outcome = await Run(derived, view, level, [], batch.Members);
                    var parent = Parent(view, level, batch.Ordinal, batch.Members, members, directory.InputHash, outcome.Result.Descriptor!, outcome.OutputHash);
                    next.Add(parent);
                    nodes.Add(parent);
                }

                EnsureStrictlyDecreasing(view, level, current.Count, next.Count);
                current = next;
            }

            if (current.Count == 1)
            {
                roots[view] = current[0].NodeId;
            }
        }

        if (roots.Count == 2)
        {
            var rootNodes = new[] { roots[ReconcileView.A], roots[ReconcileView.B] }.Select(id => nodes.Single(n => n.NodeId == id)).ToList();
            var batch = await DocumentBatchPacking.PackNextAsync(rootNodes, 0, 0, 2, context.Limits, n => n.NodeId, descriptorMeasurer, ct);
            var derived = DocumentBatchPacking.ToDerived(
                batch, context.Owner, context.ManifestIdentity, context.Definition, "cross", ReconcileView.Cross.ToString(), rootNodes.Max(n => n.Level),
                [.. batch.Members.Select(n => n.DescriptorOutputHash)], n => n.NodeId, context.SchemaVersion, context.PromptVersion);
            await Run(derived, ReconcileView.Cross, rootNodes.Max(n => n.Level), [], batch.Members);
        }

        return new(nodes, pages, roots, carries, outcomes);
    }

    /// <summary>Il numero di nodi deve diminuire strettamente a ogni livello non singleton: altrimenti stallo, mai tagliare i descriptor per forzare l'avanzamento.</summary>
    public static void EnsureStrictlyDecreasing(ReconcileView view, int level, int before, int after)
    {
        if (after >= before)
        {
            throw new DocumentWorkException(DocumentWorkException.CoverageIncomplete, $"Vista {view}, livello {level}: i nodi non diminuiscono ({before} → {after}), stallo della riduzione.");
        }
    }

    private static MemberPageEntry Member(CatalogEntry e) => new() { EvidenceId = e.EvidenceId, EvidenceVersionId = e.EvidenceVersionId, ContentHash = e.ContentHash };

    /// <summary>Genitore (o nodo inoltrato se ha un solo figlio): membership = unione esatta e disgiunta dei figli, pagine keys/relations dei figli citate per hash.</summary>
    private static CatalogNode Parent(
        ReconcileView view, int level, int ordinal, IReadOnlyList<CatalogNode> children, Dictionary<string, List<MemberPageEntry>> members, string inputHash, string descriptor,
        string descriptorOutputHash)
    {
        var union = children.SelectMany(c => members[c.NodeId]).OrderBy(m => m.EvidenceId.ToString("D"), StringComparer.Ordinal).ToList();
        var memberSetHash = ReconciliationIds.MemberSetHash(union);
        var childIds = children.Select(c => c.NodeId).ToList();
        var node = new CatalogNode
        {
            NodeId = ReconciliationIds.NodeId(view, level, ordinal, memberSetHash, childIds, inputHash), View = view, Level = level, Ordinal = ordinal, ChildNodeIds = childIds,
            MemberSetHash = memberSetHash, MemberCount = union.Count, KeyPageHashes = [.. children.SelectMany(c => c.KeyPageHashes)],
            RelationPageHashes = [.. children.SelectMany(c => c.RelationPageHashes)], Descriptor = descriptor, DescriptorOutputHash = descriptorOutputHash,
        };
        members[node.NodeId] = union;
        return node;
    }

    internal static async ValueTask<BatchOutcome> InvokeAsync(
        DerivedBatchInput batch, ReconcileView view, int level, IReadOnlyList<CatalogEntry> entries, IReadOnlyList<CatalogNode> inputNodes, ReconciliationSnapshot snapshot,
        ReconciliationCatalog catalog, ReconcileInvoker invoke, bool buildsNode, IReadOnlyList<ReconcileCandidate>? focus, CancellationToken ct, ExpansionGroups? groups = null)
    {
        var text = await invoke(new ReconcileBatchRequest(batch, view, level, entries, inputNodes, focus, groups), ct);
        ReconcileResult result;
        try
        {
            result = ReconciliationCodec.DecodeResult(text);
        }
        catch (JsonException e)
        {
            throw Invalid(batch, $"JSON non conforme a reconcile-1: {e.Message}");
        }

        var violations = new List<string>();
        if (result.BatchId != batch.BatchId || result.InputHash != batch.InputHash || result.View != view || result.Level != level)
        {
            violations.Add("batchId/inputHash/view/level: diversi dal batch ricevuto.");
        }

        violations.AddRange(ReconciliationContractRules.CheckResult(result, snapshot.SourceHash));
        violations.AddRange(ReconciliationReferences.CheckResult(result, snapshot, catalog));
        if (!result.VisitedEntryIds.SequenceEqual(entries.Select(e => e.EntryId).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            violations.Add("visitedEntryIds: devono coincidere con le voci ricevute.");
        }

        if (!result.VisitedNodeIds.SequenceEqual(inputNodes.Select(n => n.NodeId).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            violations.Add("visitedNodeIds: devono coincidere con i nodi ricevuti.");
        }

        if (buildsNode != (result.Descriptor is not null))
        {
            violations.Add(buildsNode ? "descriptor: obbligatorio nei batch che costruiscono nodi." : "descriptor: null nei batch che non costruiscono nodi.");
        }

        violations.AddRange(result.Candidates.Where(c => !c.OriginBatchIds.Contains(batch.BatchId, StringComparer.Ordinal)).Select(c => $"candidato {c.CandidateId}: originBatchIds deve citare il batch."));
        if (violations.Count > 0)
        {
            throw Invalid(batch, string.Join(" ", violations.Distinct(StringComparer.Ordinal).Take(8)));
        }

        return new(batch, CanonicalJson.HashText(ReconciliationCodec.Canonical(result)), result);
    }

    private static DocumentWorkException Invalid(DerivedBatchInput batch, string detail) =>
        new(DocumentWorkException.WorkOutputInvalid, $"Output del batch {batch.BatchId} non valido: {detail}");

    private static string LoadPrompt(string file)
    {
        using var stream = typeof(ReconciliationHierarchy).Assembly.GetManifestResourceStream("Prompts." + file)
            ?? throw new InvalidOperationException($"Prompt {file} non incorporato nell'assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }
}
