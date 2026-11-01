namespace bProjects.App.Documents;

// Metadati minimi di preparazione condivisi (T115). Solo il modello: nessun worker, classificatore, WorkOutput o dato Evidence
// documentale. T028/T029 e T057 estendono questi stessi tipi; PromotionDueAt non esiste (Plan §Classificazione: sempre null).

public enum PreparationStatus
{
    Queued,
    Parsing,
    Extracting,
    Ready,
    Incomplete,
    Failed,
}

/// <summary>Generazione di preparazione di una versione file. Ogni generazione è immutabile nei risultati; il retry ne crea una nuova.</summary>
public sealed class DocumentPreparation
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid DocumentVersionId { get; set; }
    public int Generation { get; set; }

    /// <summary>Job Parse della preparazione; popolato dopo la creazione del job (la FK parte dal job verso la preparazione).</summary>
    public Guid? ParseJobId { get; set; }
    public string? ParserVersion { get; set; }
    public string? ConfigHash { get; set; }
    public string? ManifestHash { get; set; }
    public PreparationStatus Status { get; set; }

    /// <summary>Versione Evidence corrente; null finché la preparazione non è Ready (FK verso il ledger esistente).</summary>
    public Guid? CurrentEvidenceVersionId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? RetryOfId { get; set; }

    /// <summary>t0: istante di acquisizione (DB). Coda inclusa, da qui decorrono le scadenze.</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Hash della policy congelata alla richiesta (limiti e timeout correnti al momento dell'acquisizione).</summary>
    public required string PolicyHash { get; set; }

    /// <summary>Cap di preparazione congelato (t0 + PreparationDeadlineSeconds); i job figli non lo superano.</summary>
    public DateTimeOffset HardDeadlineAt { get; set; }
    public DateTimeOffset EffectiveDeadlineAt { get; set; }

    /// <summary>Chiamate AI già riservate dal root (limite MaxAiCallsPerPreparation, controllato dal servizio).</summary>
    public int CallCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// ---- Artefatti di parsing (T028) ----------------------------------------------------------------------------------------------------
// Il parsing è distinto dall'estrazione e da Ready: questi record descrivono solo ciò che il parser ha prodotto e il backend ha validato.
// Sono append-only (una pubblicazione per preparazione, mai aggiornati) e non dipendono da sessioni MAF.

/// <summary>
/// Artefatto del parsing di una preparazione: il corpo del manifest (con il DoclingDocument originale) è su file privato, identificato da
/// <see cref="StorageKey"/> e <see cref="Sha256"/>. Una sola pubblicazione per preparazione, sotto il fence del job Parse
/// (<see cref="JobId"/>/<see cref="FencingToken"/> ne conservano l'autorità).
/// </summary>
public sealed class ParsedArtifact
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PreparationId { get; set; }
    public Guid JobId { get; set; }
    public long FencingToken { get; set; }
    public required string SchemaVersion { get; set; }
    public required string StorageKey { get; set; }
    public required string Sha256 { get; set; }
    public long ByteLength { get; set; }
    public required string ParserConfigHash { get; set; }
    public int BlockCount { get; set; }
    public int IssueCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Blocco di testo canonico, immutabile, in ordine di lettura. Le citazioni sono range [start,end) UTF-16 su <see cref="CanonicalText"/>.
/// Pagina, bbox e righe sono quelle native del manifest o nulle: nessuna pagina inventata.
/// </summary>
public sealed class DocumentBlock
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ArtifactId { get; set; }
    public int Ordinal { get; set; }

    /// <summary>Ref stabile nell'artefatto (dal parser), unico per artefatto.</summary>
    public required string Ref { get; set; }
    public Guid? ParentId { get; set; }
    public BlockType Type { get; set; }
    public required string CanonicalText { get; set; }
    public required string TextHash { get; set; }

    /// <summary>Percorso dei titoli come array JSON di stringhe.</summary>
    public required string HeadingPathJson { get; set; }
    public int? Page { get; set; }

    /// <summary>Bounding box come array JSON di quattro numeri, nel sistema <see cref="CoordinateSystem"/>.</summary>
    public string? BboxJson { get; set; }
    public string? CoordinateSystem { get; set; }
    public string? DoclingRef { get; set; }
    public int? LineStart { get; set; }
    public int? LineEnd { get; set; }

    /// <summary>Celle di tabella (righe, colonne, span, intestazioni, testo) come JSON; solo per i blocchi di tipo table.</summary>
    public string? TableCellsJson { get; set; }
}

public enum PortionStatus
{
    /// <summary>Creata dal parsing; l'estrazione non l'ha ancora trattata.</summary>
    Pending,
    Completed,
    Failed,
}

/// <summary>
/// Unità di estrazione: range Primary (proprietari) e Context (copie di contesto, mai proprietarie) dei blocchi originali. L'unità non viene
/// modificata da un retry; il risultato dell'estrazione (T057 in poi) è nei campi di stato.
/// </summary>
public sealed class DocumentPortion
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PreparationId { get; set; }
    public int Ordinal { get; set; }

    /// <summary>Range Primary come array JSON di {rangeKey, blockId, blockRef, start, end, textHash} (UTF-16).</summary>
    public required string RangesJson { get; set; }
    public required string ContextRangesJson { get; set; }
    public required string TextHash { get; set; }
    public PortionStatus Status { get; set; }
    public Guid? JobId { get; set; }
    public string? ResultHash { get; set; }
    public string? NoEvidenceReason { get; set; }
}

/// <summary>Segnalazione del parsing (del parser o del backend). Una che incide sulla completezza rende la preparazione Incomplete.</summary>
public sealed class PreparationIssue
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PreparationId { get; set; }
    public Guid? BlockId { get; set; }
    public int? Page { get; set; }
    public required string Code { get; set; }
    public IssueSeverity Severity { get; set; }
    public required string Reason { get; set; }
    public bool AffectsCompleteness { get; set; }
}
