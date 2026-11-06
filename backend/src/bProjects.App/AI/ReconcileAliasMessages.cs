using System.Text;
using bProjects.App.Evidence;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>
/// Messaggio unico di un batch alias (contracts/ai-workflow.md §11, T131): intestazione (tipo di batch e vista), voci integrali <c>[E&lt;n&gt;]</c> con enunciato e fonti
/// <c>[S&lt;n&gt;]</c>, nodi <c>[N&lt;n&gt;]</c> con il solo descriptor, candidato in esame, item già proposti <c>[i&lt;n&gt;]</c> e relazioni note <c>[R&lt;n&gt;]</c>.
/// MAI UUID, hash, entryId, batchId, inputHash, id di progetto/analisi/run, offset, blocchi o conteggi. Funzione pura: la stessa che misura il Pack e che invia il batch,
/// quindi il payload misurato è quello realmente inviato. A capo sempre «\n».
/// </summary>
public static class ReconcileAliasMessages
{
    public static string StageLabel(string stage) => stage switch
    {
        "leaf" => "foglia",
        "reduce" => "riduzione",
        "cross" => "cross",
        "expand" => "espansione",
        "compare" => "confronto",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Stadio di riconciliazione sconosciuto."),
    };

    public static IReadOnlyList<ChatMessage> Build(AnalysisAliasCatalog run, ReconcileAliasCatalog catalog, ReconcileBatchRequest request, IReadOnlyDictionary<Guid, DocumentAnalysisEvidence> evidence)
    {
        var text = new StringBuilder();
        text.Append("Tipo di batch: ").Append(StageLabel(request.Batch.Stage)).Append('\n').Append("Vista: ").Append(request.View).Append('\n');
        if (catalog.Focus is { } focus)
        {
            string Side(CandidateEndpoint e) => $"[{catalog.AliasOf(e)}]";
            text.Append($"Candidato in esame: {focus.ProposedType} fra {Side(focus.Left)} e {Side(focus.Right)} (motivo: {focus.Rationale}). Risolvilo con le voci integrali sotto.\n");
        }

        var documentSpans = new List<int>();
        foreach (var entry in catalog.Data.Entries)
        {
            var known = run.Evidence(entry.Alias)!;
            var start = text.Length;
            text.Append($"<<<VOCE [{entry.Alias}] origine={known.Origin} tipo={known.Kind} polarita={known.Polarity}>>>\n").Append(evidence[entry.EvidenceId].Statement).Append('\n');
            foreach (var link in known.Sources)
            {
                text.Append($"<<<FONTE [{link.SourceAlias}] ruolo={link.Role}>>>\n").Append(run.Source(link.SourceAlias)!.Quote).Append("\n<<<FINE FONTE>>>\n");
            }

            text.Append("<<<FINE VOCE>>>\n");
            if (known.Origin == EvidenceOriginKind.Document)
            {
                documentSpans.AddRange([start, text.Length - start]);
            }
        }

        foreach (var node in request.Nodes)
        {
            var alias = catalog.Data.Nodes.First(n => n.Kind == CandidateEndpointKind.Node && n.Id == node.NodeId).Alias;
            text.Append($"<<<NODO [{alias}] vista={node.View}>>>\n").Append(node.Descriptor).Append("\n<<<FINE NODO>>>\n");
        }

        if (catalog.Data.Items.Count > 0)
        {
            text.Append("Item già proposti per le evidenze di questo batch:\n");
            foreach (var item in catalog.Data.Items)
            {
                var cited = string.Join(", ", EvidenceAliases(run, catalog, item.Handle));
                text.Append($"[{item.Handle}] ({cited}) {catalog.ItemTexts[item.Handle]}\n");
            }
        }

        if (catalog.Data.Relations.Count > 0)
        {
            text.Append("Relazioni note fra evidenze di questo batch:\n");
            foreach (var relation in catalog.Data.Relations)
            {
                var known = run.Relation(relation.Alias)!;
                text.Append($"[{relation.Alias}] {known.FromAlias} {known.Type} {known.ToAlias} (base={known.Basis})\n");
            }
        }

        text.Append("\nRispondi ora con l'unico oggetto JSON `reconcile-alias-1`.");
        var message = new ChatMessage(ChatRole.User, text.ToString());
        if (documentSpans.Count > 0)
        {
            message.AdditionalProperties = new() { [ContextComposition.DocumentSpansProperty] = documentSpans.ToArray() };
        }

        return [message];
    }

    private static IEnumerable<string> EvidenceAliases(AnalysisAliasCatalog run, ReconcileAliasCatalog catalog, string handle) =>
        catalog.ItemEvidence[handle].Select(id => run.EvidenceById(id)!.Alias).OrderBy(AnalysisAliasPattern.Number);
}
