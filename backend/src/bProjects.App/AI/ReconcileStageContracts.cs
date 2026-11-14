namespace bProjects.App.AI;

// Tabella unica del contratto per stadio della definizione v11 (reconciliation-hardening RH-011, plan §3): per ogni stadio e vista dice quali campi del DTO `reconcile-alias-1` produce il modello e quali sono
// strutturali (null, vuoti o composti dal server). È la FONTE dei test di equivalenza fra schema inviato, compositore e validazione (RH-016): lo schema del profilo `reconcile-alias-schema-by-stage-1` deriva da
// queste righe e il compositore applica le stesse. Nessuna logica di dominio: solo dati.

/// <summary>Regola sul campo <c>focus</c> del DTO per stadio.</summary>
public enum ReconcileFocusRule
{
    /// <summary><c>focus</c> è sempre null (foglie, riduzione, cross).</summary>
    None,

    /// <summary>Espansione: <c>focus</c> è null oppure <c>Unresolved</c> (il server lo traduce in <c>candidate_unresolved</c>).</summary>
    NullOrUnresolved,

    /// <summary>Confronto: <c>focus</c> è obbligatorio, <c>Compared</c> (motivo facoltativo) oppure <c>Open</c> (motivo non vuoto).</summary>
    ComparedOrOpen,
}

/// <summary>
/// Contratto di UN stadio (e vista) nella v11. <paramref name="DescriptorRequired"/>: stringa non vuota, altrimenti null. <paramref name="ModelItems"/>: il modello può produrre <c>items</c> (nella v11 mai).
/// <paramref name="EndpointPrefix"/>: prefisso degli estremi dei candidati (<c>E</c> evidenze, <c>N</c> nodi), null se lo stadio non ha candidati propri. <paramref name="CandidateCap"/>: tetto di
/// <c>candidates</c> (0 = nessun candidato), null = nessun tetto. <paramref name="Patches"/>: patch ammesse. <paramref name="ServerItems"/>: gli item sono composti dal server (RH-020/RH-021, M2).
/// </summary>
public sealed record ReconcileStageContract(
    string Stage, ReconcileBatchSchema Variant, bool DescriptorRequired, bool ModelItems, char? EndpointPrefix, int? CandidateCap, ReconcileFocusRule Focus, bool Patches, bool ServerItems);

public static class ReconcileStageContracts
{
    /// <summary>La tabella, nell'ordine delle varianti di <see cref="ReconcileBatchSchema"/>.</summary>
    public static IReadOnlyList<ReconcileStageContract> Table { get; } =
    [
        new("leaf", ReconcileBatchSchema.LeafA, DescriptorRequired: true, ModelItems: false, EndpointPrefix: 'E', CandidateCap: null, ReconcileFocusRule.None, Patches: false, ServerItems: true),
        new("leaf", ReconcileBatchSchema.LeafB, DescriptorRequired: true, ModelItems: false, EndpointPrefix: 'E', CandidateCap: null, ReconcileFocusRule.None, Patches: false, ServerItems: false),
        new("reduce", ReconcileBatchSchema.Reduce, DescriptorRequired: true, ModelItems: false, EndpointPrefix: 'N', CandidateCap: null, ReconcileFocusRule.None, Patches: false, ServerItems: false),
        new("cross", ReconcileBatchSchema.Cross, DescriptorRequired: false, ModelItems: false, EndpointPrefix: 'N', CandidateCap: null, ReconcileFocusRule.None, Patches: false, ServerItems: false),
        new("expand", ReconcileBatchSchema.Expand, DescriptorRequired: false, ModelItems: false, EndpointPrefix: 'E', ReconcileAliasContracts.ExpansionCandidateCap, ReconcileFocusRule.NullOrUnresolved, Patches: true, ServerItems: false),
        new("compare", ReconcileBatchSchema.Compare, DescriptorRequired: false, ModelItems: false, EndpointPrefix: null, CandidateCap: 0, ReconcileFocusRule.ComparedOrOpen, Patches: true, ServerItems: false),
    ];

    /// <summary>Riga della tabella per la variante; una variante senza riga è un errore (nessun ripiego).</summary>
    public static ReconcileStageContract For(ReconcileBatchSchema variant) =>
        Table.SingleOrDefault(c => c.Variant == variant) ?? throw new ArgumentOutOfRangeException(nameof(variant), variant, "Variante senza contratto di stadio.");
}
