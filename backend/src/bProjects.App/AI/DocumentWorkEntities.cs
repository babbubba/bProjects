namespace bProjects.App.AI;

// Dati di lavoro del workflow documentale (002 T040; data-model.md §Selezioni, run, risultati intermedi e §Campi temporali; AI §5).
// Il grafo MAF resta fisso: queste righe sono dati (manifesto, batch derivati, output) e non descrivono mai il «prossimo executor».
// Sono append-only (trigger del database): nessun UPDATE né DELETE, un hash discordante è un errore d'integrità e non un aggiornamento.

/// <summary>Esecuzione proprietaria dei dati di lavoro e dei checkpoint nativi (esclusivi).</summary>
public enum WorkOwnerKind
{
    ProcessingRun,
    ChatTurn,
    DocumentJob,
}

/// <summary>Un output di lavoro esiste solo se validato e durevole: nessuno stato parziale o diagnostico.</summary>
public enum WorkOutputStatus
{
    Committed,
}

/// <summary>
/// Manifesto iniziale e immutabile di un owner: congela fonti, configurazione, policy, algoritmo e topologia fissa, non i futuri batch.
/// Uno solo per owner; <see cref="Hash"/> è calcolato dal server sul JSON canonico dei campi.
/// </summary>
public sealed class WorkManifest
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public WorkOwnerKind OwnerKind { get; set; }
    public Guid OwnerId { get; set; }
    public required string ConfigHash { get; set; }

    /// <summary>Hash delle fonti congelate (FrozenSourceHash del data model).</summary>
    public required string SourceHash { get; set; }
    public required string RuntimeFingerprint { get; set; }
    public required string DefinitionVersion { get; set; }
    public required string AlgorithmVersion { get; set; }
    public required string PolicyHash { get; set; }

    /// <summary>Topologia fissa e regole versionate di partizione/correlazione (JSONB).</summary>
    public required string PlanJson { get; set; }
    public required string Hash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Batch derivato (append-only, unique(owner,batchId)): trasformazione dei dati prodotta da PrepareBatch con l'identità di AI §5.2.
/// BatchId è un hash tipizzato dell'identità di elaborazione, stabile nel replay; attempt e fence non ne fanno parte.
/// </summary>
public sealed class DerivedBatch
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public WorkOwnerKind OwnerKind { get; set; }
    public Guid OwnerId { get; set; }
    public required string BatchId { get; set; }
    public required string Stage { get; set; }
    public string? View { get; set; }
    public int? Pass { get; set; }
    public int? Level { get; set; }
    public int Ordinal { get; set; }

    /// <summary>Hash degli output genitori, ordinati (JSONB array); vuoto per i batch iniziali.</summary>
    public required string ParentOutputHashes { get; set; }

    /// <summary>Id o range d'ingresso del batch (JSONB).</summary>
    public required string InputIdsJson { get; set; }
    public int? MeasuredTokens { get; set; }

    /// <summary>measured | calibrated_upper_bound | unavailable.</summary>
    public required string TokenProvenance { get; set; }
    public required string InputHash { get; set; }
    public required string SchemaVersion { get; set; }
    public required string PromptVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Esito validato e durevole di un batch. Unique(owner,batchId): vale anche per un <see cref="InputHash"/> diverso sullo stesso batch.
/// Il payload è inline (JSONB) oppure su storage privato hash-validato (<see cref="StorageKey"/>); in quel caso <see cref="OutputHash"/>
/// è lo SHA-256 dei byte, altrimenti l'hash del JSON canonico. <see cref="CommittedFence"/> è un attributo del tentativo, fuori dall'identità logica.
/// </summary>
public sealed class WorkOutput
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public WorkOwnerKind OwnerKind { get; set; }
    public Guid OwnerId { get; set; }
    public required string BatchId { get; set; }

    /// <summary>Id stabile dell'executor che ha prodotto l'output.</summary>
    public required string NodeId { get; set; }
    public required string InputHash { get; set; }
    public required string OutputHash { get; set; }
    public string? PayloadJson { get; set; }
    public string? StorageKey { get; set; }
    public long? PayloadByteLength { get; set; }
    public required string CoveredIdsJson { get; set; }

    /// <summary>Mappe item/source dell'output (JSONB), se previste dallo schema.</summary>
    public string? MapsJson { get; set; }

    /// <summary>Ricevuta di espansione deterministica dei figli non ancora pubblicati (JSONB).</summary>
    public string? ExpansionReceiptJson { get; set; }
    public required string SchemaVersion { get; set; }
    public required string ManifestHash { get; set; }
    public WorkOutputStatus Status { get; set; }
    public required string ValidationVersion { get; set; }
    public DateTimeOffset CommittedAt { get; set; }
    public long CommittedFence { get; set; }

    /// <summary>Output di un altro root riusato dopo un retry (nuova ricevuta nel nuovo owner); null per un output generato qui.</summary>
    public string? DerivedFromOutputHash { get; set; }
}
