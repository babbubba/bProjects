namespace bProjects.App.Documents;

public enum DocumentJobKind
{
    /// <summary>Conversione del parser (CPU, nessuna AI).</summary>
    Parse,

    /// <summary>Estrazione di un'unità/porzione (AI).</summary>
    ExtractUnit,
}

public enum DocumentJobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// Lavoro di una preparazione, con la stessa macchina a lease dei turni e dei run 001 (owner, scadenza, fencing token, tentativi).
/// Il padre è la preparazione: la sua deadline prevale. Nei test strutturali il job nasce Queued.
/// </summary>
public sealed class DocumentJob
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PreparationId { get; set; }
    public DocumentJobKind Kind { get; set; }

    /// <summary>Unità logica dell'estrazione (porzione); null per Parse.</summary>
    public Guid? UnitId { get; set; }
    public Guid? RequestKey { get; set; }
    public Guid? RetryOfId { get; set; }
    public DocumentJobStatus Status { get; set; }
    public Guid? RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Scadenza del job: mai oltre quella della preparazione (verificato dal servizio che scrive).</summary>
    public DateTimeOffset DeadlineAt { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public long FencingToken { get; set; }

    /// <summary>Configurazione risolta del job (JSONB), senza segreti.</summary>
    public string? ConfigJson { get; set; }
    public string? WorkflowDefinitionVersion { get; set; }

    /// <summary>Hash del risultato validato; obbligatorio per Completed.</summary>
    public string? ResultHash { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
