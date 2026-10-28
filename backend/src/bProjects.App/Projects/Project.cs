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

    // CurrentAnalysisId: FK verso ProcessingRun (T025). LatestDraftId: FK verso Draft (T038), cambia solo al commit di una bozza.
    public Guid? CurrentAnalysisId { get; set; }
    public Guid? LatestDraftId { get; set; }
}
