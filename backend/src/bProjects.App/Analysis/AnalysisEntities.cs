namespace bProjects.App.Analysis;

public enum ProcessingRunKind
{
    Analysis,
    Document,
}

public enum ProcessingRunStatus
{
    Queued,
    Running,
    Completed,
    Failed,
}

public enum AnalysisRevisionOrigin
{
    Ai,
    PmEdit,
}

public enum AnalysisValidationState
{
    Unvalidated,
    PmConfirmed,
}

/// <summary>
/// Elaborazione formale (Analysis o Document) in un'unica tabella; stati e sessioni distinti dalla chat.
/// Nessun coordinatore di fasi persistito: LastObservedStage è solo una proiezione informativa.
/// </summary>
public sealed class ProcessingRun
{
    /// <summary>v3: il consolidamento riceve la vista congelata del registro delle evidenze e restituisce la mappa elementi→evidenze.</summary>
    public const string AnalysisWorkflowVersion = "project-analysis-v3";
    public const string DocumentWorkflowVersion = "project-document-v1";

    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public ProcessingRunKind Kind { get; set; }

    // Solo Analysis: input immutabile.
    public Guid? ConversationId { get; set; }
    public long? SourceCutoffSequence { get; set; }

    // Solo Document: analisi riuscita e sua revisione corrente confermata all'avvio.
    public Guid? AnalysisId { get; set; }
    public Guid? InputRevisionId { get; set; }

    public Guid RequestKey { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }

    public ProcessingRunStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset DeadlineAt { get; set; }

    public required string WorkflowDefinitionVersion { get; set; }
    public required string MafVersion { get; set; }

    /// <summary>Configurazione risolta immutabile (JSONB), senza segreti.</summary>
    public string? AgentConfig { get; set; }

    /// <summary>Versioni dei prompt (JSONB).</summary>
    public string? PromptVersions { get; set; }

    /// <summary>
    /// La versione del prompt congelata al comando coincide con quella del grafo che sta per riprendere (contracts/ai-workflow.md §2 e §8:
    /// nessun checkpoint di altra configurazione è riutilizzabile). Un run senza versioni congelate (nate fuori dal comando) non è confrontabile.
    /// </summary>
    public bool PromptVersionMatches(string key, string expected)
    {
        if (PromptVersions is null)
        {
            return true;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(PromptVersions);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty(key, out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                && value.GetString() == expected;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
    public string? WorkflowSessionId { get; set; }

    /// <summary>Selezione documentale fissata alla richiesta (slice 002); null per i run legacy e per i progetti senza documenti.</summary>
    public Guid? SourceSnapshotId { get; set; }

    // ---- Root del regime (document-regime-v2, Plan §Classificazione). Nullable: i record 001 non li valorizzano e mantengono DeadlineAt/regole 001.
    public bProjects.App.AI.ExecutionRegime? InitialRegime { get; set; }
    public bProjects.App.AI.ExecutionRegime? Regime { get; set; }
    public bProjects.App.AI.ClassificationStatus? ClassificationStatus { get; set; }
    public string? ClassificationVersion { get; set; }
    public string? ClassificationReason { get; set; }

    /// <summary>t0+180 s nel regime Ordinary; null per Extended e per i root legacy. PromotionDueAt non esiste (sempre null per Plan).</summary>
    public DateTimeOffset? OrdinaryDeadlineAt { get; set; }

    /// <summary>0 o 1: l'unica promozione Ordinary→Extended (additional_work, T047).</summary>
    public int? PromotionCount { get; set; }
    public DateTimeOffset? PromotedAt { get; set; }
    public string? PromotedReason { get; set; }

    /// <summary>Hash della policy congelata alla richiesta; null per i root legacy.</summary>
    public string? PolicyHash { get; set; }

    /// <summary>Cap finito dal t0 (documentale); null per i root legacy.</summary>
    public DateTimeOffset? HardDeadlineAt { get; set; }

    /// <summary>Autorità temporale corrente; per i root documentali DeadlineAt = EffectiveDeadlineAt (CHECK).</summary>
    public DateTimeOffset? EffectiveDeadlineAt { get; set; }
    public int? CallCount { get; set; }
    public Guid? RetryOfId { get; set; }

    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public long FencingToken { get; set; }

    public string? LastObservedStage { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Esattamente il risultato pertinente a Kind: ResultRevisionId per Analysis, DraftId (FK verso Draft, T038) per Document.
    public Guid? ResultRevisionId { get; set; }
    public Guid? DraftId { get; set; }

    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>Revisione dell'anteprima di analisi; la corrente è la Version massima per AnalysisId.</summary>
public sealed class AnalysisRevision
{
    public const string CurrentSchemaVersion = "1.0.0";

    public Guid Id { get; set; }
    public Guid AnalysisId { get; set; }
    public int Version { get; set; }
    public Guid? ParentRevisionId { get; set; }
    public AnalysisRevisionOrigin Origin { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string SchemaVersion { get; set; }

    /// <summary>AnalysisContent serializzato (JSONB), validato prima del commit.</summary>
    public required string ContentJson { get; set; }
    public AnalysisValidationState ValidationState { get; set; }
    public Guid? ConfirmedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
}
