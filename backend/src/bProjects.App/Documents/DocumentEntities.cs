namespace bProjects.App.Documents;

// Archivio dei documenti di progetto (data-model.md §Documenti, storage e preparazione; §Selezioni). Gli originali sono append-only:
// una versione non si modifica né si cancella. Nessun MessageId sintetico e nessuna sessione MAF: documenti e preferenze sono dati propri.
// Upload, quota aggregata e worker non vivono qui (T011–T013): queste classi sono solo il modello persistente.

public enum UploadReceiptStatus
{
    Receiving,
    Committed,
    Aborted,
}

public enum SourceMode
{
    /// <summary>Usa la versione file più recente con estrazione corrente completa.</summary>
    LatestReady,

    /// <summary>Usa la versione scelta dal PM (<see cref="ProjectSourceSelectionItem.PinnedVersionId"/>).</summary>
    Pinned,
}

/// <summary>Documento logico del progetto: il nome è un'etichetta, non l'identità (nessun vincolo di unicità sul nome).</summary>
public sealed class ProjectDocument
{
    public const int MaxDisplayNameLength = 256;

    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string DisplayName { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Cache transazionale della versione file applicabile; punta solo a una versione di questo stesso documento.</summary>
    public Guid? LatestReadyVersionId { get; set; }
}

/// <summary>Versione file del documento: byte originali immutabili, identificati da hash e da una chiave di storage generata dal server.</summary>
public sealed class DocumentVersion
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid DocumentId { get; set; }
    public int Version { get; set; }
    public required string OriginalName { get; set; }
    public required string MediaType { get; set; }
    public long ByteLength { get; set; }
    public required string Sha256 { get; set; }

    /// <summary>Chiave generata dal server (mai dal client); percorso risolto sotto la radice privata dallo store.</summary>
    public required string StorageKey { get; set; }
    public Guid UploadedBy { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public Guid UploadReceiptId { get; set; }
}

/// <summary>Ricevuta di upload: idempotenza per (progetto, chiave) e prenotazione di quota; l'acquisizione vale solo da Committed.</summary>
public sealed class UploadReceipt
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>Documento di destinazione per una nuova versione; null per un documento nuovo.</summary>
    public Guid? DocumentId { get; set; }
    public Guid RequestKey { get; set; }

    /// <summary>Hash di file, metadati e destinazione: la stessa chiave con input diverso è un conflitto.</summary>
    public required string InputHash { get; set; }
    public long ReservedBytes { get; set; }
    public UploadReceiptStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? ResultVersionId { get; set; }
}

/// <summary>Preferenze di fonte del progetto: una riga per progetto con Revision per il confronto ottimistico.</summary>
public sealed class ProjectSourceSelection
{
    public Guid ProjectId { get; set; }
    public long Revision { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProjectSourceSelectionItem
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid DocumentId { get; set; }
    public bool Included { get; set; }
    public SourceMode Mode { get; set; }

    /// <summary>Presente se e solo se <see cref="Mode"/> è Pinned; appartiene al documento dell'item.</summary>
    public Guid? PinnedVersionId { get; set; }
}
