namespace bProjects.App.Projects;

public sealed class Project
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 4_000;

    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Riferimenti a risultati futuri: semplici UUID nullable, senza FK né navigazioni finché le tabelle
    // non esistono (vincoli aggiunti da T025 per l'analisi e da T038 per la bozza).
    public Guid? CurrentAnalysisId { get; set; }
    public Guid? LatestDraftId { get; set; }
}
