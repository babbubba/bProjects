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
