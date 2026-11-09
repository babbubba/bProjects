namespace bProjects.App.Documents;

public enum SourceAvailability
{
    /// <summary>Estrazione corrente completa: l'unica condizione in cui il documento ha una versione Evidence selezionabile.</summary>
    Ready,

    /// <summary>Incluso ma non utilizzabile alla richiesta (preparazione non pronta, fallita o incompleta).</summary>
    Unavailable,

    /// <summary>Escluso dal PM: registrato per completezza dello snapshot.</summary>
    Excluded,
}

/// <summary>Selezione documentale fissata alla richiesta di un comando (data-model.md): immutabile, mai ricalcolata al claim.</summary>
public sealed class SourceSnapshot
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public long SelectionRevision { get; set; }
    public required string Hash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Una riga per documento del progetto: inclusioni ed esclusioni, con la disponibilità osservata alla richiesta.</summary>
public sealed class SourceSnapshotItem
{
    public Guid Id { get; set; }
    public Guid SnapshotId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid DocumentId { get; set; }
    public bool Included { get; set; }
    public SourceAvailability Availability { get; set; }
    public Guid? DocumentVersionId { get; set; }
    public Guid? PreparationId { get; set; }

    /// <summary>Valorizzata solo per Ready.</summary>
    public Guid? EvidenceVersionId { get; set; }
}
