using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// Proiezione deterministica degli item delle foglie della vista A (reconciliation-hardening RH-020, definizione v11, tabella `leaf-projection-1`, plan §4, decisione D2). Nella v11 il modello non produce item:
// li compone il server dall'evidenza. È una RAPPRESENTAZIONE INTERMEDIA: preserva le distinzioni del prodotto (fatti documentati, requisiti, decisioni, ipotesi, alternative, conflitti e domande aperte), non
// giudica la qualità della documentazione finale (livelli 7–8) e NON si modifica senza una decisione esplicita dell'autore e una nuova versione (`leaf-projection-2`): ogni modifica della tabella è una versione
// nuova. Nessuna eccezione, nessun ripiego: un tipo, un'origine o una certezza che la tabella non conosce è un errore.

/// <summary>Tabella e funzione di proiezione <see cref="Version"/>: voce → item (testo letterale dell'enunciato, evidenza = la voce stessa, fonti della voce, sezione e classificazione per tipo, origine e certezza).</summary>
public static class ReconcileLeafProjection
{
    public const string Version = "leaf-projection-1";

    /// <summary>
    /// Sezione e classificazione di un'evidenza. La CERTEZZA prevale sul tipo; condizioni nell'ordine: tipo Gap ⇒ openQuestions; certezza <c>ToClarify</c> ⇒ openQuestions; certezza <c>Hypothesized</c> o
    /// tipo Assumption ⇒ assumptions (<c>Assumed</c>); tipo Alternative ⇒ openQuestions (mai una sezione strutturale); certezza <c>Inferred</c> ⇒ sezione del tipo con classificazione <c>Inferred</c>; altrimenti
    /// (certezza <c>Declared</c>) la sezione del tipo. La classificazione «per origine» è <c>Declared</c> per un messaggio del PM e <c>Documented</c> per un documento.
    /// </summary>
    public static (AiSection Section, DocumentAnalysisClassification Classification) Rule(EvidenceKind kind, EvidenceOriginKind origin, EvidenceCertainty certainty)
    {
        var byOrigin = origin switch
        {
            EvidenceOriginKind.Message => DocumentAnalysisClassification.Declared,
            EvidenceOriginKind.Document => DocumentAnalysisClassification.Documented,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Origine dell'evidenza senza regola in leaf-projection-1."),
        };
        var ofKind = kind switch
        {
            EvidenceKind.Goal => AiSection.Goals,
            EvidenceKind.Context => AiSection.ScopeOverview,
            EvidenceKind.Requirement => AiSection.Requirements,
            EvidenceKind.Action => AiSection.Flows,
            EvidenceKind.Constraint => AiSection.Constraints,
            EvidenceKind.Exclusion => AiSection.ScopeExcluded,
            EvidenceKind.Dependency => AiSection.Dependencies,
            EvidenceKind.Decision => AiSection.Requirements,
            EvidenceKind.Other => AiSection.ScopeOverview,
            EvidenceKind.Assumption or EvidenceKind.Gap or EvidenceKind.Alternative => AiSection.OpenQuestions,   // sezione non strutturale: la decidono le condizioni sotto
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Tipo di evidenza senza regola in leaf-projection-1."),
        };
        if (!Enum.IsDefined(certainty))
        {
            throw new ArgumentOutOfRangeException(nameof(certainty), certainty, "Certezza dell'evidenza senza regola in leaf-projection-1.");
        }

        if (kind == EvidenceKind.Gap || certainty == EvidenceCertainty.ToClarify)
        {
            return (AiSection.OpenQuestions, byOrigin);
        }

        if (certainty == EvidenceCertainty.Hypothesized || kind == EvidenceKind.Assumption)
        {
            return (AiSection.Assumptions, DocumentAnalysisClassification.Assumed);
        }

        if (kind == EvidenceKind.Alternative)
        {
            return (AiSection.OpenQuestions, byOrigin);
        }

        return (ofKind, certainty == EvidenceCertainty.Inferred ? DocumentAnalysisClassification.Inferred : byOrigin);
    }

    /// <summary>
    /// Proposte e sezioni di UNA foglia della vista A: per ogni voce della richiesta, in ordine (<c>n</c> = posizione 1-based), chiave locale canonica <c>LocalKey(request, "p" + n)</c>, testo = enunciato LETTERALE
    /// dell'evidenza, evidenza = la voce stessa, fonti della voce nell'ordine canonico di <c>SortedSourceIds</c>. Funzione pura: nessun dato casuale, nessuna dipendenza dall'ordine del dizionario.
    /// </summary>
    public static (IReadOnlyList<ItemProposal> Proposals, IReadOnlyDictionary<string, AiSection> Sections) Project(
        ReconcileBatchRequest request, IReadOnlyDictionary<Guid, DocumentAnalysisEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidence);
        var proposals = new List<ItemProposal>(request.Entries.Count);
        var sections = new Dictionary<string, AiSection>(StringComparer.Ordinal);
        for (var i = 0; i < request.Entries.Count; i++)
        {
            var entry = request.Entries[i];
            var known = evidence.GetValueOrDefault(entry.EvidenceId)
                ?? throw new InvalidOperationException($"L'evidenza {entry.EvidenceId:D} della voce {entry.EntryId} non è nel ledger del run: nessun item può essere proiettato.");
            var (section, classification) = Rule(known.Kind, known.Origin, known.Certainty);
            var localKey = ReconcileAliasComposer.LocalKey(request, "p" + (i + 1));
            proposals.Add(new ItemProposal
            {
                LocalKey = localKey, Text = known.Statement, Classification = classification, EvidenceIds = [entry.EvidenceId],
                SourceIds = [.. ReconciliationIds.SortedIds(entry.SourceIds.Distinct()).Select(Guid.Parse)],
            });
            sections[localKey] = section;
        }

        return (proposals, sections);
    }
}
