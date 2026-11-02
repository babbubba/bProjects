namespace bProjects.App.Evidence;

// Registro atomico delle evidenze (data-model.md §Registro, contracts/ai-workflow.md §10). Le evidenze sono interpretazioni
// verificabili degli originali PM, mai verità certificate: l'originale append-only resta l'autorità. Versioni, evidenze,
// relazioni, promozioni, selezioni e mappe sono append-only o scritte una sola volta; nessun UPDATE/DELETE applicativo.

public enum ExtractionJobReason
{
    /// <summary>Creato dal salvataggio del messaggio PM, nella stessa transazione.</summary>
    Initial,

    /// <summary>Rianalisi richiesta dal PM (messaggio o progetto) o retry autorizzato di un lavoro fallito.</summary>
    Reanalysis,

    /// <summary>Messaggio senza versione corrente: backfill esplicito o accodamento di Analizza entro il cutoff.</summary>
    Backfill,
}

public enum ExtractionJobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
}

public enum EvidenceKind
{
    Goal,
    Context,
    Requirement,
    Action,
    Constraint,
    Exclusion,
    Dependency,
    Assumption,
    Gap,
    Alternative,
    Decision,
    Other,
}

public enum EvidencePolarity
{
    Affirmed,
    Negated,
    Excluded,
    Prohibited,
    Undefined,
}

public enum EvidenceCertainty
{
    Declared,
    Inferred,
    Hypothesized,
    ToClarify,
}

public enum EvidenceRelationType
{
    Qualifies,
    Confirms,
    Contradicts,
    Corrects,
    Supersedes,
    DependsOn,
    AlternativeTo,
    SameRule,
}

/// <summary>Declared: la relazione è enunciata dal PM nel testo (es. «intendevo 10, non 5»); Inferred: interpretazione dell'AI.</summary>
public enum EvidenceRelationBasis
{
    Declared,
    Inferred,
}

public enum EvidenceSourceRole
{
    /// <summary>Passo del messaggio estratto che porta l'informazione.</summary>
    Primary,

    /// <summary>Passo di un messaggio PM precedente che chiarisce il referente.</summary>
    Context,

    /// <summary>Passo del messaggio dell'assistente a cui il PM risponde: solo referente, mai prova.</summary>
    Referent,
}

/// <summary>Discriminante del ledger (data-model.md §Estensione del ledger): le righe storiche sono tutte Message.</summary>
public enum EvidenceOriginKind
{
    /// <summary>Messaggio PM di una conversazione (ramo 001, evidence-1.0.0).</summary>
    Message,

    /// <summary>Preparazione di una versione file documentale (ramo 002, evidence-1.1.0). Nessun messaggio fittizio.</summary>
    Document,
}

public enum EvidenceBatchKind
{
    Reanalysis,
    Backfill,
}

/// <summary>Lavoro durevole di estrazione di un messaggio PM: claim, lease e fencing come ChatTurn e ProcessingRun.</summary>
public sealed class EvidenceExtractionJob
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>XOR: Message → ConversationId/MessageId/MessageSequence valorizzati e PreparationId null; Document → il contrario.</summary>
    public EvidenceOriginKind OriginKind { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? MessageId { get; set; }
    public long? MessageSequence { get; set; }

    /// <summary>Preparazione documentale di cui questo lavoro (unico aggregatore) pubblica la versione Evidence.</summary>
    public Guid? PreparationId { get; set; }
    public ExtractionJobReason Reason { get; set; }
    public Guid? BatchId { get; set; }
    public Guid? RetryOfJobId { get; set; }

    /// <summary>Idempotency-Key del comando PM che l'ha creato (null per i lavori creati dal sistema).</summary>
    public Guid? RequestKey { get; set; }

    public ExtractionJobStatus Status { get; set; }
    public Guid? RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset DeadlineAt { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public long FencingToken { get; set; }

    /// <summary>Configurazione dell'estrattore congelata alla richiesta (JSONB), senza endpoint né credenziali.</summary>
    public required string ExtractorConfig { get; set; }

    public Guid? ResultVersionId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>Esito valido e append-only di un'estrazione: una o più evidenze, oppure zero con motivo esplicito.</summary>
public sealed class EvidenceExtractionVersion
{
    public const string CurrentSchemaVersion = "evidence-1.0.0";

    public const string DocumentSchemaVersion = "evidence-1.1.0";

    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>XOR come <see cref="EvidenceExtractionJob"/>: la numerazione <see cref="Version"/> è per messaggio oppure per preparazione.</summary>
    public EvidenceOriginKind OriginKind { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? MessageId { get; set; }
    public long? MessageSequence { get; set; }
    public Guid? PreparationId { get; set; }

    /// <summary>Hash del manifest delle fonti dell'estrazione documentale (obbligatorio solo per Document).</summary>
    public string? ManifestHash { get; set; }
    public int Version { get; set; }
    public Guid JobId { get; set; }
    public required string SchemaVersion { get; set; }
    public required string PromptVersion { get; set; }
    public required string ModelId { get; set; }

    /// <summary>JSONB: configurazione effettiva e contesto usato (sequenze, versioni precedenti fornite, omissioni per budget).</summary>
    public required string Parameters { get; set; }

    public int EvidenceCount { get; set; }
    public string? NoEvidenceReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Evidenza atomica di una versione: metadati indicizzati e contenuto semantico in JSONB (<see cref="EvidenceContent"/>).</summary>
public sealed class EvidenceItem
{
    public Guid Id { get; set; }
    public Guid VersionId { get; set; }
    public Guid ProjectId { get; set; }
    public EvidenceOriginKind OriginKind { get; set; }
    public Guid? MessageId { get; set; }
    public Guid? PreparationId { get; set; }
    public int Ordinal { get; set; }
    public required string Key { get; set; }
    public EvidenceKind Kind { get; set; }
    public EvidencePolarity Polarity { get; set; }
    public EvidenceCertainty Certainty { get; set; }
    public required string Statement { get; set; }
    public required string Content { get; set; }
}

/// <summary>Relazione tipizzata asserita da una versione; il bersaglio è nella stessa versione o in una versione precedente.</summary>
public sealed class EvidenceRelation
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid VersionId { get; set; }
    public Guid FromEvidenceId { get; set; }
    public Guid ToEvidenceId { get; set; }
    public EvidenceRelationType Type { get; set; }
    public EvidenceRelationBasis Basis { get; set; }
    public string? Rationale { get; set; }
}

/// <summary>Versione corrente di un messaggio; Revision cresce a ogni promozione (concorrenza ottimistica).</summary>
public sealed class EvidenceMessageState
{
    public Guid MessageId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid CurrentVersionId { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Versione Evidence corrente di una versione file documentale (stessa semantica di <see cref="EvidenceMessageState"/>, altro discriminante).
/// Revision parte da 0; entrambi i puntatori null oppure entrambi valorizzati, sulla stessa versione file e sullo stesso progetto.
/// </summary>
public sealed class EvidenceDocumentState
{
    public Guid DocumentVersionId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? CurrentPreparationId { get; set; }
    public Guid? CurrentEvidenceVersionId { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Traccia append-only di un cambio di bundle corrente di una versione file; PromotedBy null = prima estrazione valida.</summary>
public sealed class DocumentPromotion
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid DocumentVersionId { get; set; }
    public Guid? FromPreparationId { get; set; }
    public Guid? FromEvidenceVersionId { get; set; }
    public Guid ToPreparationId { get; set; }
    public Guid ToEvidenceVersionId { get; set; }
    public Guid? PromotedBy { get; set; }
    public DateTimeOffset PromotedAt { get; set; }
    public string? Reason { get; set; }
}

/// <summary>Traccia append-only di un cambio di versione corrente; PromotedBy null = regola automatica della prima versione valida.</summary>
public sealed class EvidencePromotion
{
    public const string FirstValidVersionReason = "prima estrazione valida";

    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid MessageId { get; set; }
    public Guid? FromVersionId { get; set; }
    public Guid ToVersionId { get; set; }
    public Guid? PromotedBy { get; set; }
    public DateTimeOffset PromotedAt { get; set; }
    public string? Reason { get; set; }

    /// <summary>JSONB {remapped, stale, relations:[...]}: effetto sulle relazioni di altri messaggi verso il messaggio promosso.</summary>
    public required string RelationImpact { get; set; }
}

/// <summary>Rianalisi di progetto o backfill: i lavori del batch ne misurano l'avanzamento reale.</summary>
public sealed class EvidenceBatch
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public EvidenceBatchKind Kind { get; set; }
    public Guid RequestKey { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public long ThroughSequence { get; set; }
    public int JobCount { get; set; }
    public int SkippedCount { get; set; }
}

/// <summary>Selezione congelata di un run di Analysis: scritta una volta al primo tentativo, riletta da retry e resume.</summary>
public sealed class AnalysisEvidenceSelection
{
    public Guid RunId { get; set; }
    public Guid ProjectId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string ViewHash { get; set; }

    /// <summary>JSONB della vista deterministica (<see cref="EvidenceSelectionView"/>).</summary>
    public required string View { get; set; }
}

/// <summary>Una versione Evidence selezionata dal run (chiave: RunId+VersionId). Message → MessageId/MessageSequence; Document → PreparationId.</summary>
public sealed class AnalysisEvidenceSelectionItem
{
    public Guid RunId { get; set; }
    public EvidenceOriginKind OriginKind { get; set; }
    public Guid? MessageId { get; set; }
    public long? MessageSequence { get; set; }
    public Guid? PreparationId { get; set; }
    public Guid VersionId { get; set; }
}

/// <summary>Mappa interna elemento di AnalysisContent → evidenza, scritta con la revisione AI (AnalysisContent 1.0.0 invariato).</summary>
public sealed class AnalysisEvidenceLink
{
    public Guid RunId { get; set; }
    public Guid ItemId { get; set; }
    public Guid EvidenceId { get; set; }
    public Guid ProjectId { get; set; }
}

/// <summary>Esito versionato della verifica di copertura e provenienza, legato agli input esatti.</summary>
public sealed class EvidenceCoverageReport
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid AnalysisId { get; set; }
    public Guid? RevisionId { get; set; }
    public Guid? DraftId { get; set; }
    public required string CheckerVersion { get; set; }
    public required string InputHash { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int DeterministicErrors { get; set; }
    public int SemanticRisks { get; set; }
    public int PmAssessments { get; set; }
    public required string Report { get; set; }
}
