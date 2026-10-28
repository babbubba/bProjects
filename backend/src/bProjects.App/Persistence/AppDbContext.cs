using bProjects.App.Analysis;
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
    public DbSet<ProcessingRun> ProcessingRuns => Set<ProcessingRun>();
    public DbSet<AnalysisRevision> AnalysisRevisions => Set<AnalysisRevision>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        ConfigureProjects(builder);
        ConfigureConversations(builder);
        ConfigureAnalysis(builder);

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
            project.HasOne<ProcessingRun>().WithMany().HasForeignKey(p => p.CurrentAnalysisId).OnDelete(DeleteBehavior.Restrict);
            // LatestDraftId: nessuna FK finché Draft non esiste (T038).
        });

    private static void ConfigureAnalysis(ModelBuilder builder)
    {
        builder.Entity<ProcessingRun>(run =>
        {
            run.HasKey(r => r.Id);
            run.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
            run.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            run.Property(r => r.WorkflowDefinitionVersion).HasMaxLength(64);
            run.Property(r => r.MafVersion).HasMaxLength(32);
            run.Property(r => r.AgentConfig).HasColumnType("jsonb");
            run.Property(r => r.PromptVersions).HasColumnType("jsonb");
            run.Property(r => r.WorkflowSessionId).HasMaxLength(128);
            run.Property(r => r.LeaseOwner).HasMaxLength(128);
            run.Property(r => r.LastObservedStage).HasMaxLength(64);
            run.Property(r => r.ErrorCode).HasMaxLength(64);
            run.Property(r => r.ErrorMessage).HasMaxLength(1_000);
            // Idempotenza: stessa chiave per (progetto, Kind); l'input differente è un conflitto applicativo.
            run.HasIndex(r => new { r.ProjectId, r.Kind, r.RequestKey }).IsUnique();
            // Al più un'elaborazione formale attiva per progetto, di qualsiasi Kind.
            run.HasIndex(r => r.ProjectId).IsUnique().HasFilter("\"Status\" IN ('Queued', 'Running')").HasDatabaseName("IX_ProcessingRuns_OneActivePerProject");
            // Dispatch/claim: coda FIFO dei soli run aperti.
            run.HasIndex(r => new { r.RequestedAt, r.Id }).HasFilter("\"Status\" IN ('Queued', 'Running')").HasDatabaseName("IX_ProcessingRuns_Open_Fifo");
            run.HasIndex(r => r.AnalysisId);
            run.HasIndex(r => r.ResultRevisionId);
            run.HasIndex(r => r.InputRevisionId);
            run.HasIndex(r => r.ConversationId);
            run.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Restrict);
            run.HasOne<Conversation>().WithMany().HasForeignKey(r => r.ConversationId).OnDelete(DeleteBehavior.Restrict);
            run.HasOne<ProcessingRun>().WithMany().HasForeignKey(r => r.AnalysisId).OnDelete(DeleteBehavior.Restrict);
            run.HasOne<AnalysisRevision>().WithMany().HasForeignKey(r => r.InputRevisionId).OnDelete(DeleteBehavior.Restrict);
            run.HasOne<AnalysisRevision>().WithMany().HasForeignKey(r => r.ResultRevisionId).OnDelete(DeleteBehavior.Restrict);
            run.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(r => r.RequestedBy).OnDelete(DeleteBehavior.Restrict);
            // DraftId: UUID nullable senza FK né navigazione finché Draft non esiste (T038).
            run.ToTable(t =>
            {
                t.HasCheckConstraint("CK_ProcessingRuns_Attempts", "\"AttemptCount\" >= 0 AND \"MaxAttempts\" >= 1");
                t.HasCheckConstraint(
                    "CK_ProcessingRuns_Kind_Fields",
                    "(\"Kind\" = 'Analysis' AND \"ConversationId\" IS NOT NULL AND \"SourceCutoffSequence\" IS NOT NULL AND \"SourceCutoffSequence\" >= 0 AND \"AnalysisId\" IS NULL AND \"InputRevisionId\" IS NULL AND \"DraftId\" IS NULL) " +
                    "OR (\"Kind\" = 'Document' AND \"AnalysisId\" IS NOT NULL AND \"InputRevisionId\" IS NOT NULL AND \"ResultRevisionId\" IS NULL)");
                t.HasCheckConstraint(
                    "CK_ProcessingRuns_Completed_Result",
                    "\"Status\" <> 'Completed' OR (\"Kind\" = 'Analysis' AND \"ResultRevisionId\" IS NOT NULL) OR (\"Kind\" = 'Document' AND \"DraftId\" IS NOT NULL)");
                t.HasCheckConstraint("CK_ProcessingRuns_Failed_Error", "\"Status\" <> 'Failed' OR \"ErrorCode\" IS NOT NULL");
            });
        });

        builder.Entity<AnalysisRevision>(revision =>
        {
            revision.HasKey(r => r.Id);
            revision.Property(r => r.Origin).HasConversion<string>().HasMaxLength(16);
            revision.Property(r => r.ValidationState).HasConversion<string>().HasMaxLength(16);
            revision.Property(r => r.SchemaVersion).HasMaxLength(16);
            revision.Property(r => r.ContentJson).HasColumnType("jsonb");
            revision.HasIndex(r => new { r.AnalysisId, r.Version }).IsUnique();
            // Prima revisione AI univoca per analisi: il replay del run non ne crea una seconda.
            revision.HasIndex(r => r.AnalysisId).IsUnique().HasFilter("\"Origin\" = 'Ai'").HasDatabaseName("IX_AnalysisRevisions_OneAiPerAnalysis");
            revision.HasIndex(r => r.ParentRevisionId);
            revision.HasOne<ProcessingRun>().WithMany().HasForeignKey(r => r.AnalysisId).OnDelete(DeleteBehavior.Restrict);
            revision.HasOne<AnalysisRevision>().WithMany().HasForeignKey(r => r.ParentRevisionId).OnDelete(DeleteBehavior.Restrict);
            revision.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(r => r.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            revision.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(r => r.ConfirmedBy).OnDelete(DeleteBehavior.Restrict);
            revision.ToTable(t =>
            {
                t.HasCheckConstraint("CK_AnalysisRevisions_Version", "\"Version\" >= 1");
                t.HasCheckConstraint(
                    "CK_AnalysisRevisions_Origin_Fields",
                    "(\"Origin\" = 'Ai' AND \"Version\" = 1 AND \"ParentRevisionId\" IS NULL) OR (\"Origin\" = 'PmEdit' AND \"Version\" >= 2 AND \"ParentRevisionId\" IS NOT NULL)");
                t.HasCheckConstraint(
                    "CK_AnalysisRevisions_Confirmation",
                    "(\"ValidationState\" = 'Unvalidated' AND \"ConfirmedBy\" IS NULL AND \"ConfirmedAt\" IS NULL) OR (\"ValidationState\" = 'PmConfirmed' AND \"ConfirmedBy\" IS NOT NULL AND \"ConfirmedAt\" IS NOT NULL)");
            });
        });
    }

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
