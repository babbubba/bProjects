namespace bProjects.App.Analysis;

/// <summary>Stato della bozza: nella slice esiste soltanto la proposta dell'AI, da revisionare.</summary>
public enum DraftStatus
{
    Proposed,
}

/// <summary>Approvazione cliente: nella slice nessuna bozza è approvata e non esiste un percorso per approvarla.</summary>
public enum DraftApprovalStatus
{
    NotApproved,
}

/// <summary>
/// Bozza Markdown prodotta da un ProcessingRun Kind=Document (data-model.md, «Draft»). Una per run (ProcessingRunId univoco) e
/// legata alla revisione confermata usata come input (AnalysisRevisionId coincide con InputRevisionId del run, imposto dal
/// database). Non ha limiti di parole. Le bozze precedenti restano consultabili; Project.LatestDraftId punta all'ultima riuscita.
/// </summary>
public sealed class Draft
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ProcessingRunId { get; set; }
    public Guid AnalysisRevisionId { get; set; }

    /// <summary>Markdown non vuoto, validato prima del commit.</summary>
    public required string Markdown { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DraftStatus Status { get; set; } = DraftStatus.Proposed;
    public DraftApprovalStatus ApprovalStatus { get; set; } = DraftApprovalStatus.NotApproved;
}
