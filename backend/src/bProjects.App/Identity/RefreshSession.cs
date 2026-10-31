namespace bProjects.App.Identity;

/// <summary>Sessione di refresh: il token raw esiste solo nel cookie, qui ne resta l'hash.</summary>
public sealed class RefreshSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}
