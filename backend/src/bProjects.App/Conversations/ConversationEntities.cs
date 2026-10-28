namespace bProjects.App.Conversations;

public enum MessageRole
{
    User,
    Assistant,
}

public enum ChatTurnStatus
{
    Queued,
    Running,
    Completed,
    Failed,
}

public sealed class Conversation
{
    public const string CurrentMafVersion = "1.26.0";

    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long LastSequence { get; set; }

    /// <summary>Ultimo snapshot derivato della sessione MAF di chat (JSONB); mai fonte autorevole.</summary>
    public string? MafChatSessionState { get; set; }
    public required string MafVersion { get; set; }
    public string? ChatConfigVersion { get; set; }
    public long ContextThroughSequence { get; set; }
    public long ChatSessionRevision { get; set; }
}

/// <summary>Messaggio originale, append-only: nessuna mutazione né cancellazione applicativa.</summary>
public sealed class ConversationMessage
{
    public const int MaxUserTextLength = 65_536;

    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public long Sequence { get; set; }
    public MessageRole Role { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Solo user
    public Guid? ClientMessageId { get; set; }
    public Guid? AuthorId { get; set; }

    // Solo assistant
    public string? AgentId { get; set; }
    public Guid? ReplyToMessageId { get; set; }
    public Guid? ChatTurnId { get; set; }
}

public sealed class ChatTurn
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid TriggerMessageId { get; set; }
    public long SourceCutoffSequence { get; set; }
    public ChatTurnStatus Status { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset DeadlineAt { get; set; }
    public Guid? ResultMessageId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public long FencingToken { get; set; }
    public string? PromptVersion { get; set; }

    /// <summary>Configurazione risolta del turno (JSONB), senza segreti.</summary>
    public string? AgentConfig { get; set; }
    public long SessionRevisionAtStart { get; set; }
}
