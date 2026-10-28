using bProjects.App.Conversations;
using bProjects.App.Identity;
using bProjects.App.Projects;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<IdentityUser<Guid>, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<ChatTurn> ChatTurns => Set<ChatTurn>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        ConfigureProjects(builder);
        ConfigureConversations(builder);

        builder.Entity<RefreshSession>(session =>
        {
            session.HasKey(s => s.Id);
            session.Property(s => s.TokenHash).HasMaxLength(64);
            session.HasIndex(s => s.TokenHash).IsUnique();
            session.HasIndex(s => s.UserId);
            session.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            session.HasOne<RefreshSession>().WithMany().HasForeignKey(s => s.ReplacedById).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureProjects(ModelBuilder builder) =>
        builder.Entity<Project>(project =>
        {
            project.HasKey(p => p.Id);
            project.Property(p => p.Name).HasMaxLength(Project.MaxNameLength);
            project.Property(p => p.Description).HasMaxLength(Project.MaxDescriptionLength);
            project.ToTable(t => t.HasCheckConstraint("CK_Projects_Name_NotBlank", "\"Name\" ~ '\\S'"));
            project.HasIndex(p => new { p.CreatedAt, p.Id }).IsDescending(true, true);
            project.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(p => p.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            // CurrentAnalysisId / LatestDraftId: nessuna FK finché Analysis e Draft non esistono (T025, T038).
        });

    private static void ConfigureConversations(ModelBuilder builder)
    {
        builder.Entity<Conversation>(conversation =>
        {
            conversation.HasKey(c => c.Id);
            conversation.HasIndex(c => c.ProjectId).IsUnique();
            conversation.Property(c => c.MafChatSessionState).HasColumnType("jsonb");
            conversation.Property(c => c.MafVersion).HasMaxLength(32);
            conversation.Property(c => c.ChatConfigVersion).HasMaxLength(64);
            conversation.HasOne<Project>().WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ConversationMessage>(message =>
        {
            message.HasKey(m => m.Id);
            message.Property(m => m.Role).HasConversion<string>().HasMaxLength(16);
            message.Property(m => m.AgentId).HasMaxLength(64);
            message.HasIndex(m => new { m.ConversationId, m.Sequence }).IsUnique();
            message.HasIndex(m => new { m.ConversationId, m.ClientMessageId }).IsUnique(); // NULL escluso: solo i messaggi PM
            message.HasIndex(m => m.ReplyToMessageId).IsUnique();
            message.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Restrict);
            message.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(m => m.AuthorId).OnDelete(DeleteBehavior.Restrict);
            message.HasOne<ConversationMessage>().WithMany().HasForeignKey(m => m.ReplyToMessageId).OnDelete(DeleteBehavior.Restrict);
            message.HasOne<ChatTurn>().WithMany().HasForeignKey(m => m.ChatTurnId).OnDelete(DeleteBehavior.Restrict);
            message.ToTable(t =>
            {
                t.HasCheckConstraint("CK_ConversationMessages_Sequence", "\"Sequence\" >= 1");
                t.HasCheckConstraint("CK_ConversationMessages_Body_NotBlank", "\"Body\" ~ '\\S'");
                t.HasCheckConstraint(
                    "CK_ConversationMessages_Role_Fields",
                    "(\"Role\" = 'User' AND \"ClientMessageId\" IS NOT NULL AND \"AuthorId\" IS NOT NULL AND \"AgentId\" IS NULL AND \"ReplyToMessageId\" IS NULL AND \"ChatTurnId\" IS NULL) " +
                    "OR (\"Role\" = 'Assistant' AND \"ClientMessageId\" IS NULL AND \"AuthorId\" IS NULL AND \"AgentId\" IS NOT NULL AND \"ReplyToMessageId\" IS NOT NULL AND \"ChatTurnId\" IS NOT NULL)");
            });
        });

        builder.Entity<ChatTurn>(turn =>
        {
            turn.HasKey(t => t.Id);
            turn.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
            turn.Property(t => t.ErrorCode).HasMaxLength(64);
            turn.Property(t => t.ErrorMessage).HasMaxLength(1_000);
            turn.Property(t => t.LeaseOwner).HasMaxLength(128);
            turn.Property(t => t.PromptVersion).HasMaxLength(64);
            turn.Property(t => t.AgentConfig).HasColumnType("jsonb");
            turn.HasIndex(t => t.TriggerMessageId).IsUnique();
            // Dispatch (T014): al più un turno Running per conversazione (un solo progetto per conversazione),
            // e un indice parziale sui soli turni aperti per la verifica FIFO del claim.
            turn.HasIndex(t => t.ConversationId).IsUnique().HasFilter("\"Status\" = 'Running'").HasDatabaseName("IX_ChatTurns_OneRunningPerConversation");
            turn.HasIndex(t => new { t.ConversationId, t.SourceCutoffSequence }).HasFilter("\"Status\" IN ('Queued', 'Running')").HasDatabaseName("IX_ChatTurns_Open_Fifo");
            turn.HasOne<Conversation>().WithMany().HasForeignKey(t => t.ConversationId).OnDelete(DeleteBehavior.Restrict);
            turn.HasOne<ConversationMessage>().WithMany().HasForeignKey(t => t.TriggerMessageId).OnDelete(DeleteBehavior.Restrict);
            turn.HasOne<ConversationMessage>().WithMany().HasForeignKey(t => t.ResultMessageId).OnDelete(DeleteBehavior.Restrict);
            turn.ToTable(t =>
            {
                t.HasCheckConstraint("CK_ChatTurns_Attempts", "\"AttemptCount\" >= 0 AND \"MaxAttempts\" >= 1");
                t.HasCheckConstraint("CK_ChatTurns_Completed_Result", "\"Status\" <> 'Completed' OR \"ResultMessageId\" IS NOT NULL");
                t.HasCheckConstraint("CK_ChatTurns_Failed_Error", "\"Status\" <> 'Failed' OR \"ErrorCode\" IS NOT NULL");
            });
        });
    }
}
