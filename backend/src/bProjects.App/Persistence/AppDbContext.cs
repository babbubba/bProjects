using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Evidence;
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
    public DbSet<Draft> Drafts => Set<Draft>();
    public DbSet<WorkflowCheckpoint> WorkflowCheckpoints => Set<WorkflowCheckpoint>();
    public DbSet<InferenceCall> InferenceCalls => Set<InferenceCall>();
    public DbSet<EvidenceExtractionJob> EvidenceExtractionJobs => Set<EvidenceExtractionJob>();
    public DbSet<EvidenceExtractionVersion> EvidenceExtractionVersions => Set<EvidenceExtractionVersion>();
    public DbSet<EvidenceItem> Evidences => Set<EvidenceItem>();
    public DbSet<EvidenceRelation> EvidenceRelations => Set<EvidenceRelation>();
    public DbSet<EvidenceMessageState> EvidenceMessageStates => Set<EvidenceMessageState>();
    public DbSet<EvidencePromotion> EvidencePromotions => Set<EvidencePromotion>();
    public DbSet<EvidenceBatch> EvidenceBatches => Set<EvidenceBatch>();
    public DbSet<AnalysisEvidenceSelection> AnalysisEvidenceSelections => Set<AnalysisEvidenceSelection>();
    public DbSet<AnalysisEvidenceSelectionItem> AnalysisEvidenceSelectionItems => Set<AnalysisEvidenceSelectionItem>();
    public DbSet<AnalysisEvidenceLink> AnalysisEvidenceLinks => Set<AnalysisEvidenceLink>();
    public DbSet<EvidenceCoverageReport> EvidenceCoverageReports => Set<EvidenceCoverageReport>();
    public DbSet<DocumentPolicy> DocumentPolicies => Set<DocumentPolicy>();
    public DbSet<ProjectDocument> ProjectDocuments => Set<ProjectDocument>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<UploadReceipt> UploadReceipts => Set<UploadReceipt>();
    public DbSet<ProjectSourceSelection> ProjectSourceSelections => Set<ProjectSourceSelection>();
    public DbSet<ProjectSourceSelectionItem> ProjectSourceSelectionItems => Set<ProjectSourceSelectionItem>();
    public DbSet<DocumentPreparation> DocumentPreparations => Set<DocumentPreparation>();
    public DbSet<DocumentJob> DocumentJobs => Set<DocumentJob>();
    public DbSet<SourceSnapshot> SourceSnapshots => Set<SourceSnapshot>();
    public DbSet<SourceSnapshotItem> SourceSnapshotItems => Set<SourceSnapshotItem>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        ConfigureProjects(builder);
        ConfigureConversations(builder);
        ConfigureAnalysis(builder);
        ConfigureDrafts(builder);
        ConfigureCheckpoints(builder);
        ConfigureInferenceCalls(builder);
        ConfigureEvidence(builder);
        ConfigureDocuments(builder);

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

    /// <summary>Documenti di progetto (slice 002): migrazioni additive, nessun dato o vincolo della 001 modificato.</summary>
    private static void ConfigureDocuments(ModelBuilder builder)
    {
        builder.Entity<DocumentPolicy>(policy =>
        {
            policy.HasKey(p => p.Id);
            policy.Property(p => p.Id).ValueGeneratedNever();
            policy.Property(p => p.LimitsJson).HasColumnType("jsonb");
            policy.Property(p => p.PolicyHash).HasMaxLength(64);
            policy.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");
            policy.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(p => p.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
            // Una sola riga globale; la revisione parte da 1 e cresce a ogni PUT.
            policy.ToTable(t =>
            {
                t.HasCheckConstraint("CK_DocumentPolicies_Singleton", "\"Id\" = 1");
                t.HasCheckConstraint("CK_DocumentPolicies_Revision", "\"Revision\" >= 1");
            });
        });

        ConfigureDocumentArchive(builder);
        ConfigureDocumentPreparation(builder);
    }

    /// <summary>
    /// Archivio (T006): FK composte con ProjectId impediscono riferimenti fra progetti; i puntatori fra documento e versioni passano da
    /// chiavi alternative (Id, DocumentId). Le quote aggregate e le riserve non sono vincoli di riga: le impone il servizio di upload.
    /// </summary>
    private static void ConfigureDocumentArchive(ModelBuilder builder)
    {
        builder.Entity<ProjectDocument>(document =>
        {
            document.HasKey(d => d.Id);
            document.Property(d => d.DisplayName).HasMaxLength(ProjectDocument.MaxDisplayNameLength);
            document.Property(d => d.CreatedAt).HasDefaultValueSql("now()");
            document.HasAlternateKey(d => new { d.Id, d.ProjectId });
            document.HasIndex(d => new { d.ProjectId, d.CreatedAt });   // il nome non è identità: nessun indice univoco su DisplayName
            document.HasOne<Project>().WithMany().HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Restrict);
            document.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(d => d.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            // Il puntatore Ready è a una versione di questo stesso documento (con LatestReadyVersionId nullo la FK non vincola).
            document.HasOne<DocumentVersion>().WithMany()
                .HasForeignKey(d => new { d.LatestReadyVersionId, d.Id })
                .HasPrincipalKey(v => new { v.Id, v.DocumentId })
                .OnDelete(DeleteBehavior.Restrict);
            document.ToTable(t => t.HasCheckConstraint("CK_ProjectDocuments_Name_NotBlank", "\"DisplayName\" ~ '\\S'"));
        });

        builder.Entity<DocumentVersion>(version =>
        {
            version.HasKey(v => v.Id);
            version.Property(v => v.OriginalName).HasMaxLength(512);
            version.Property(v => v.MediaType).HasMaxLength(128);
            version.Property(v => v.Sha256).HasMaxLength(64);
            version.Property(v => v.StorageKey).HasMaxLength(256);
            version.Property(v => v.UploadedAt).HasDefaultValueSql("now()");
            version.HasAlternateKey(v => new { v.Id, v.ProjectId });
            version.HasAlternateKey(v => new { v.Id, v.DocumentId });
            version.HasIndex(v => new { v.DocumentId, v.Version }).IsUnique();
            version.HasIndex(v => v.StorageKey).IsUnique();
            version.HasIndex(v => v.UploadReceiptId).IsUnique();
            version.HasOne<ProjectDocument>().WithMany()
                .HasForeignKey(v => new { v.DocumentId, v.ProjectId })
                .HasPrincipalKey(d => new { d.Id, d.ProjectId })
                .OnDelete(DeleteBehavior.Restrict);
            version.HasOne<UploadReceipt>().WithMany()
                .HasForeignKey(v => new { v.UploadReceiptId, v.ProjectId })
                .HasPrincipalKey(r => new { r.Id, r.ProjectId })
                .OnDelete(DeleteBehavior.Restrict);
            version.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(v => v.UploadedBy).OnDelete(DeleteBehavior.Restrict);
            version.ToTable(t =>
            {
                t.HasCheckConstraint("CK_DocumentVersions_Version", "\"Version\" >= 1");
                t.HasCheckConstraint("CK_DocumentVersions_Size", "\"ByteLength\" > 0");
                t.HasCheckConstraint("CK_DocumentVersions_Sha256", "length(\"Sha256\") = 64");
            });
        });

        builder.Entity<UploadReceipt>(receipt =>
        {
            receipt.HasKey(r => r.Id);
            receipt.Property(r => r.InputHash).HasMaxLength(64);
            receipt.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            receipt.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
            receipt.HasAlternateKey(r => new { r.Id, r.ProjectId });
            receipt.HasIndex(r => new { r.ProjectId, r.RequestKey }).IsUnique();
            receipt.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Restrict);
            receipt.HasOne<ProjectDocument>().WithMany()
                .HasForeignKey(r => new { r.DocumentId, r.ProjectId })
                .HasPrincipalKey(d => new { d.Id, d.ProjectId })
                .OnDelete(DeleteBehavior.Restrict);
            receipt.HasOne<DocumentVersion>().WithMany()
                .HasForeignKey(r => new { r.ResultVersionId, r.ProjectId })
                .HasPrincipalKey(v => new { v.Id, v.ProjectId })
                .OnDelete(DeleteBehavior.Restrict);
            receipt.ToTable(t =>
            {
                t.HasCheckConstraint("CK_UploadReceipts_Reserved", "\"ReservedBytes\" >= 0");
                // Acquisizione solo da Committed, con la versione risultante; le altre ricevute non ne hanno.
                t.HasCheckConstraint("CK_UploadReceipts_Result", "(\"Status\" = 'Committed') = (\"ResultVersionId\" IS NOT NULL)");
            });
        });

        builder.Entity<ProjectSourceSelection>(selection =>
        {
            selection.HasKey(s => s.ProjectId);
            selection.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");
            selection.HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Restrict);
            selection.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(s => s.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
            selection.ToTable(t => t.HasCheckConstraint("CK_ProjectSourceSelections_Revision", "\"Revision\" >= 0"));
        });

        builder.Entity<ProjectSourceSelectionItem>(item =>
        {
            item.HasKey(i => i.Id);
            item.Property(i => i.Mode).HasConversion<string>().HasMaxLength(16);
            item.HasIndex(i => new { i.ProjectId, i.DocumentId }).IsUnique();
            item.HasOne<ProjectSourceSelection>().WithMany().HasForeignKey(i => i.ProjectId).OnDelete(DeleteBehavior.Restrict);
            item.HasOne<ProjectDocument>().WithMany()
                .HasForeignKey(i => new { i.DocumentId, i.ProjectId })
                .HasPrincipalKey(d => new { d.Id, d.ProjectId })
                .OnDelete(DeleteBehavior.Restrict);
            // La versione fissata appartiene al documento dell'item.
            item.HasOne<DocumentVersion>().WithMany()
                .HasForeignKey(i => new { i.PinnedVersionId, i.DocumentId })
                .HasPrincipalKey(v => new { v.Id, v.DocumentId })
                .OnDelete(DeleteBehavior.Restrict);
            item.ToTable(t => t.HasCheckConstraint("CK_ProjectSourceSelectionItems_Pinned", "(\"Mode\" = 'Pinned') = (\"PinnedVersionId\" IS NOT NULL)"));
        });
    }

    /// <summary>
    /// Preparazione, job e snapshot (T115): predispone il modello condiviso, non i worker. L'ordine dei vincoli
    /// (unico parse, una generazione aperta per versione, risultato per Completed, errore per Failed) è verificato in T007.
    /// </summary>
    private static void ConfigureDocumentPreparation(ModelBuilder builder)
    {
        builder.Entity<DocumentPreparation>(preparation =>
        {
            preparation.HasKey(p => p.Id);
            preparation.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            preparation.Property(p => p.ParserVersion).HasMaxLength(64);
            preparation.Property(p => p.ConfigHash).HasMaxLength(64);
            preparation.Property(p => p.ManifestHash).HasMaxLength(64);
            preparation.Property(p => p.PolicyHash).HasMaxLength(64);
            preparation.Property(p => p.ErrorCode).HasMaxLength(64);
            preparation.Property(p => p.ErrorMessage).HasMaxLength(1_000);
            preparation.Property(p => p.RequestedAt).HasDefaultValueSql("now()");
            preparation.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            preparation.HasAlternateKey(p => new { p.Id, p.ProjectId });
            preparation.HasIndex(p => new { p.DocumentVersionId, p.Generation }).IsUnique();
            // Una sola generazione aperta per versione file.
            preparation.HasIndex(p => p.DocumentVersionId).IsUnique().HasFilter("\"Status\" IN ('Queued', 'Parsing', 'Extracting')").HasDatabaseName("UX_DocumentPreparations_OneOpenPerVersion");
            preparation.HasIndex(p => p.ParseJobId);
            preparation.HasIndex(p => p.RetryOfId);
            preparation.HasIndex(p => p.CurrentEvidenceVersionId);
            preparation.HasOne<DocumentVersion>().WithMany()
                .HasForeignKey(p => new { p.DocumentVersionId, p.ProjectId })
                .HasPrincipalKey(v => new { v.Id, v.ProjectId })
                .OnDelete(DeleteBehavior.Restrict);
            // Il job Parse appartiene a questa stessa preparazione.
            preparation.HasOne<DocumentJob>().WithMany()
                .HasForeignKey(p => new { p.ParseJobId, p.Id })
                .HasPrincipalKey(j => new { j.Id, j.PreparationId })
                .OnDelete(DeleteBehavior.Restrict);
            preparation.HasOne<DocumentPreparation>().WithMany().HasForeignKey(p => p.RetryOfId).OnDelete(DeleteBehavior.Restrict);
            preparation.HasOne<EvidenceExtractionVersion>().WithMany().HasForeignKey(p => p.CurrentEvidenceVersionId).OnDelete(DeleteBehavior.Restrict);
            preparation.ToTable(t =>
            {
                t.HasCheckConstraint("CK_DocumentPreparations_Generation", "\"Generation\" >= 1");
                t.HasCheckConstraint("CK_DocumentPreparations_Calls", "\"CallCount\" >= 0");
                t.HasCheckConstraint("CK_DocumentPreparations_Deadlines", "\"EffectiveDeadlineAt\" <= \"HardDeadlineAt\" AND \"HardDeadlineAt\" > \"RequestedAt\"");
                t.HasCheckConstraint("CK_DocumentPreparations_Ready_Evidence", "\"Status\" <> 'Ready' OR \"CurrentEvidenceVersionId\" IS NOT NULL");
                t.HasCheckConstraint("CK_DocumentPreparations_Failed_Error", "\"Status\" <> 'Failed' OR \"ErrorCode\" IS NOT NULL");
            });
        });

        builder.Entity<DocumentJob>(job =>
        {
            job.HasKey(j => j.Id);
            job.Property(j => j.Kind).HasConversion<string>().HasMaxLength(16);
            job.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
            job.Property(j => j.ConfigJson).HasColumnType("jsonb");
            job.Property(j => j.WorkflowDefinitionVersion).HasMaxLength(64);
            job.Property(j => j.LeaseOwner).HasMaxLength(128);
            job.Property(j => j.ResultHash).HasMaxLength(64);
            job.Property(j => j.ErrorCode).HasMaxLength(64);
            job.Property(j => j.ErrorMessage).HasMaxLength(1_000);
            job.Property(j => j.RequestedAt).HasDefaultValueSql("now()");
            job.HasAlternateKey(j => new { j.Id, j.PreparationId });
            // Un solo lavoro per owner e unità logica (UnitId nullo = lavoro della preparazione, es. Parse).
            job.HasIndex(j => new { j.PreparationId, j.Kind, j.UnitId }).IsUnique().AreNullsDistinct(false);
            job.HasIndex(j => new { j.ProjectId, j.RequestKey }).IsUnique();
            job.HasIndex(j => j.RetryOfId);
            job.HasIndex(j => new { j.RequestedAt, j.Id }).HasFilter("\"Status\" IN ('Queued', 'Running')").HasDatabaseName("IX_DocumentJobs_Open_Fifo");
            job.HasOne<DocumentPreparation>().WithMany()
                .HasForeignKey(j => new { j.PreparationId, j.ProjectId })
                .HasPrincipalKey(p => new { p.Id, p.ProjectId })
                .OnDelete(DeleteBehavior.Restrict);
            job.HasOne<DocumentJob>().WithMany().HasForeignKey(j => j.RetryOfId).OnDelete(DeleteBehavior.Restrict);
            job.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(j => j.RequestedBy).OnDelete(DeleteBehavior.Restrict);
            job.ToTable(t =>
            {
                t.HasCheckConstraint("CK_DocumentJobs_Attempts", "\"AttemptCount\" >= 0 AND \"MaxAttempts\" >= 1");
                t.HasCheckConstraint("CK_DocumentJobs_Completed_Result", "\"Status\" <> 'Completed' OR \"ResultHash\" IS NOT NULL");
                t.HasCheckConstraint("CK_DocumentJobs_Failed_Error", "\"Status\" <> 'Failed' OR \"ErrorCode\" IS NOT NULL");
            });
        });

        builder.Entity<SourceSnapshot>(snapshot =>
        {
            snapshot.HasKey(s => s.Id);
            snapshot.Property(s => s.Hash).HasMaxLength(64);
            snapshot.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            snapshot.HasAlternateKey(s => new { s.Id, s.ProjectId });
            snapshot.HasIndex(s => new { s.ProjectId, s.CreatedAt });
            snapshot.HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Restrict);
            snapshot.ToTable(t => t.HasCheckConstraint("CK_SourceSnapshots_Revision", "\"SelectionRevision\" >= 0"));
        });

        builder.Entity<SourceSnapshotItem>(item =>
        {
            item.HasKey(i => i.Id);
            item.Property(i => i.Availability).HasConversion<string>().HasMaxLength(16);
            item.HasIndex(i => new { i.SnapshotId, i.DocumentId }).IsUnique();
            item.HasIndex(i => i.PreparationId);
            item.HasIndex(i => i.EvidenceVersionId);
            item.HasOne<SourceSnapshot>().WithMany()
                .HasForeignKey(i => new { i.SnapshotId, i.ProjectId })
                .HasPrincipalKey(s => new { s.Id, s.ProjectId })
                .OnDelete(DeleteBehavior.Restrict);
            item.HasOne<ProjectDocument>().WithMany()
                .HasForeignKey(i => new { i.DocumentId, i.ProjectId })
                .HasPrincipalKey(d => new { d.Id, d.ProjectId })
                .OnDelete(DeleteBehavior.Restrict);
            item.HasOne<DocumentVersion>().WithMany()
                .HasForeignKey(i => new { i.DocumentVersionId, i.DocumentId })
                .HasPrincipalKey(v => new { v.Id, v.DocumentId })
                .OnDelete(DeleteBehavior.Restrict);
            item.HasOne<DocumentPreparation>().WithMany()
                .HasForeignKey(i => new { i.PreparationId, i.ProjectId })
                .HasPrincipalKey(p => new { p.Id, p.ProjectId })
                .OnDelete(DeleteBehavior.Restrict);
            item.HasOne<EvidenceExtractionVersion>().WithMany().HasForeignKey(i => i.EvidenceVersionId).OnDelete(DeleteBehavior.Restrict);
            item.ToTable(t =>
            {
                t.HasCheckConstraint("CK_SourceSnapshotItems_Excluded", "(\"Availability\" = 'Excluded') = (NOT \"Included\")");
                // Solo Ready ha estrazione selezionabile; un'indisponibile non diventa Ready per effetto dei riferimenti.
                t.HasCheckConstraint(
                    "CK_SourceSnapshotItems_Ready",
                    "(\"Availability\" = 'Ready') = (\"DocumentVersionId\" IS NOT NULL AND \"PreparationId\" IS NOT NULL AND \"EvidenceVersionId\" IS NOT NULL)");
                t.HasCheckConstraint("CK_SourceSnapshotItems_EvidenceOnlyReady", "\"EvidenceVersionId\" IS NULL OR \"Availability\" = 'Ready'");
            });
        });

        // Legacy nullable (SourceSnapshotId nullo = ramo 001 invariato).
        builder.Entity<ChatTurn>(turn =>
        {
            turn.HasIndex(t => t.SourceSnapshotId);
            turn.HasOne<SourceSnapshot>().WithMany().HasForeignKey(t => t.SourceSnapshotId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ProcessingRun>(run =>
        {
            run.HasIndex(r => r.SourceSnapshotId);
            run.HasOne<SourceSnapshot>().WithMany()
                .HasForeignKey(r => new { r.SourceSnapshotId, r.ProjectId })
                .HasPrincipalKey(s => new { s.Id, s.ProjectId })
                .OnDelete(DeleteBehavior.Restrict);
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
            project.HasOne<Draft>().WithMany().HasForeignKey(p => p.LatestDraftId).OnDelete(DeleteBehavior.Restrict);
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
            run.HasOne<Draft>().WithMany().HasForeignKey(r => r.DraftId).OnDelete(DeleteBehavior.Restrict);
            run.HasIndex(r => r.DraftId);
            // Bersagli delle chiavi esterne composte di Draft: la bozza appartiene al progetto del run e alla sua revisione di input.
            run.HasAlternateKey(r => new { r.Id, r.ProjectId });
            run.HasIndex(r => new { r.Id, r.InputRevisionId }).IsUnique().HasDatabaseName("UX_ProcessingRuns_Id_InputRevisionId");
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

    private static void ConfigureDrafts(ModelBuilder builder) =>
        builder.Entity<Draft>(draft =>
        {
            draft.HasKey(d => d.Id);
            draft.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
            draft.Property(d => d.ApprovalStatus).HasConversion<string>().HasMaxLength(16);
            // Una sola bozza per run Document: il replay del commit non ne crea una seconda.
            draft.HasIndex(d => d.ProcessingRunId).IsUnique();
            draft.HasIndex(d => new { d.ProjectId, d.CreatedAt });
            draft.HasIndex(d => d.AnalysisRevisionId);
            draft.HasOne<Project>().WithMany().HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Restrict);
            // Il run esiste e appartiene allo stesso progetto della bozza.
            draft.HasOne<ProcessingRun>().WithMany()
                .HasForeignKey(d => new { d.ProcessingRunId, d.ProjectId })
                .HasPrincipalKey(r => new { r.Id, r.ProjectId })
                .OnDelete(DeleteBehavior.Restrict);
            draft.HasOne<AnalysisRevision>().WithMany().HasForeignKey(d => d.AnalysisRevisionId).OnDelete(DeleteBehavior.Restrict);
            // La FK composita (ProcessingRunId, AnalysisRevisionId) → ProcessingRuns(Id, InputRevisionId), che impone Kind=Document e
            // AnalysisRevisionId = InputRevisionId, è creata dalla migrazione: InputRevisionId è nullable e EF non lo ammette in una chiave.
            draft.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Drafts_Markdown_NotBlank", "\"Markdown\" ~ '\\S'");
                t.HasCheckConstraint("CK_Drafts_Status", "\"Status\" = 'Proposed' AND \"ApprovalStatus\" = 'NotApproved'");
            });
        });

    private static void ConfigureCheckpoints(ModelBuilder builder)
    {
        builder.Entity<WorkflowCheckpoint>(checkpoint =>
        {
            checkpoint.HasKey(c => c.Id);
            checkpoint.Property(c => c.WorkflowSessionId).HasMaxLength(128);
            checkpoint.Property(c => c.CheckpointKey).HasMaxLength(128);
            checkpoint.Property(c => c.ParentCheckpointKey).HasMaxLength(128);
            checkpoint.Property(c => c.CheckpointInfo).HasColumnType("jsonb");
            checkpoint.Property(c => c.ParentInfo).HasColumnType("jsonb");
            checkpoint.Property(c => c.Payload).HasColumnType("jsonb");
            checkpoint.Property(c => c.MafVersion).HasMaxLength(32);
            checkpoint.Property(c => c.WorkflowDefinitionVersion).HasMaxLength(64);
            // Unicità V28 e indice oldest-first: l'ordine di scrittura è CommitSequence, univoco per run.
            checkpoint.HasIndex(c => new { c.ProcessingRunId, c.WorkflowSessionId, c.CheckpointKey }).IsUnique();
            checkpoint.HasIndex(c => new { c.ProcessingRunId, c.CommitSequence }).IsUnique().HasDatabaseName("IX_WorkflowCheckpoints_OldestFirst");
            checkpoint.HasOne<ProcessingRun>().WithMany().HasForeignKey(c => c.ProcessingRunId).OnDelete(DeleteBehavior.Restrict);
            // Il parent è un checkpoint già scritto dello stesso run e della stessa sessione.
            checkpoint.HasOne<WorkflowCheckpoint>().WithMany()
                .HasForeignKey(c => new { c.ProcessingRunId, c.WorkflowSessionId, c.ParentCheckpointKey })
                .HasPrincipalKey(c => new { c.ProcessingRunId, c.WorkflowSessionId, c.CheckpointKey })
                .OnDelete(DeleteBehavior.Restrict);
            checkpoint.ToTable(t => t.HasCheckConstraint("CK_WorkflowCheckpoints_Sequence", "\"CommitSequence\" >= 1"));
        });
    }

    /// <summary>Telemetria tecnica delle richieste al modello: tabella additiva, nessun vincolo sulle tabelle esistenti.</summary>
    private static void ConfigureInferenceCalls(ModelBuilder builder) =>
        builder.Entity<InferenceCall>(call =>
        {
            call.HasKey(c => c.Id);
            call.Property(c => c.Phase).HasConversion<string>().HasMaxLength(16);
            call.Property(c => c.State).HasConversion<string>().HasMaxLength(16);
            call.Property(c => c.ModelId).HasMaxLength(256);
            call.Property(c => c.ReasoningEffective).HasMaxLength(16);
            call.Property(c => c.FinishReason).HasMaxLength(32);
            call.Property(c => c.Composition).HasColumnType("jsonb");
            call.Property(c => c.History).HasColumnType("jsonb");
            call.Property(c => c.Compaction).HasColumnType("jsonb");
            call.Property(c => c.Config).HasColumnType("jsonb");
            call.HasOne<Project>().WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Restrict);
            call.HasOne<ChatTurn>().WithMany().HasForeignKey(c => c.ChatTurnId).OnDelete(DeleteBehavior.Restrict);
            call.HasOne<ProcessingRun>().WithMany().HasForeignKey(c => c.ProcessingRunId).OnDelete(DeleteBehavior.Restrict);
            call.HasOne<EvidenceExtractionJob>().WithMany().HasForeignKey(c => c.EvidenceExtractionJobId).OnDelete(DeleteBehavior.Restrict);
            // Letture del risultato logico: righe di un turno/run/lavoro di estrazione per tentativo.
            call.HasIndex(c => new { c.ChatTurnId, c.Attempt });
            call.HasIndex(c => new { c.ProcessingRunId, c.Attempt });
            call.HasIndex(c => new { c.EvidenceExtractionJobId, c.Attempt });
            // Un solo risultato logico per turno, per run e per lavoro di estrazione, anche fra tentativi diversi.
            call.HasIndex(c => c.ChatTurnId).IsUnique().HasFilter("\"IsFinal\"").HasDatabaseName("UX_InferenceCalls_OneFinalPerTurn");
            call.HasIndex(c => c.ProcessingRunId).IsUnique().HasFilter("\"IsFinal\"").HasDatabaseName("UX_InferenceCalls_OneFinalPerRun");
            call.HasIndex(c => c.EvidenceExtractionJobId).IsUnique().HasFilter("\"IsFinal\"").HasDatabaseName("UX_InferenceCalls_OneFinalPerExtraction");
            // Monitor del contesto: ultima inferenza finale del progetto.
            call.HasIndex(c => new { c.ProjectId, c.StartedAt }).IsDescending(false, true).HasFilter("\"IsFinal\"").HasDatabaseName("IX_InferenceCalls_Project_LastFinal");
            call.ToTable(t =>
            {
                t.HasCheckConstraint("CK_InferenceCalls_Owner",
                    "(\"ChatTurnId\" IS NOT NULL)::int + (\"ProcessingRunId\" IS NOT NULL)::int + (\"EvidenceExtractionJobId\" IS NOT NULL)::int = 1");
                t.HasCheckConstraint("CK_InferenceCalls_Final", "NOT \"IsFinal\" OR (\"State\" = 'Completed' AND \"Phase\" <> 'Compaction')");
                t.HasCheckConstraint("CK_InferenceCalls_Tokens",
                    "(\"PromptTokens\" IS NULL OR \"PromptTokens\" >= 0) AND (\"CompletionTokens\" IS NULL OR \"CompletionTokens\" >= 0) " +
                    "AND (\"TotalTokens\" IS NULL OR \"TotalTokens\" >= 0) AND (\"CachedTokens\" IS NULL OR \"CachedTokens\" >= 0)");
            });
        });

    /// <summary>
    /// Registro atomico delle evidenze (data-model.md §Registro): migrazione additiva, nessun vincolo tolto alle tabelle esistenti.
    /// Le appartenenze fra righe dello stesso messaggio/progetto che una FK semplice non esprime sono verificate dal codice che scrive.
    /// </summary>
    private static void ConfigureEvidence(ModelBuilder builder)
    {
        builder.Entity<EvidenceExtractionJob>(job =>
        {
            job.HasKey(j => j.Id);
            job.Property(j => j.Reason).HasConversion<string>().HasMaxLength(16);
            job.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
            job.Property(j => j.ExtractorConfig).HasColumnType("jsonb");
            job.Property(j => j.LeaseOwner).HasMaxLength(128);
            job.Property(j => j.ErrorCode).HasMaxLength(64);
            job.Property(j => j.ErrorMessage).HasMaxLength(1_000);
            job.HasOne<Project>().WithMany().HasForeignKey(j => j.ProjectId).OnDelete(DeleteBehavior.Restrict);
            job.HasOne<Conversation>().WithMany().HasForeignKey(j => j.ConversationId).OnDelete(DeleteBehavior.Restrict);
            job.HasOne<ConversationMessage>().WithMany().HasForeignKey(j => j.MessageId).OnDelete(DeleteBehavior.Restrict);
            job.HasOne<EvidenceBatch>().WithMany().HasForeignKey(j => j.BatchId).OnDelete(DeleteBehavior.Restrict);
            job.HasOne<EvidenceExtractionJob>().WithMany().HasForeignKey(j => j.RetryOfJobId).OnDelete(DeleteBehavior.Restrict);
            job.HasOne<EvidenceExtractionVersion>().WithMany().HasForeignKey(j => j.ResultVersionId).OnDelete(DeleteBehavior.Restrict);
            job.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(j => j.RequestedBy).OnDelete(DeleteBehavior.Restrict);
            // Un solo lavoro aperto per messaggio: doppio invio, rianalisi concorrenti e backfill non lo duplicano.
            job.HasIndex(j => j.MessageId).IsUnique().HasFilter("\"Status\" IN ('Queued', 'Running')").HasDatabaseName("UX_EvidenceExtractionJobs_OneOpenPerMessage");
            job.HasIndex(j => new { j.RequestedAt, j.Id }).HasFilter("\"Status\" IN ('Queued', 'Running')").HasDatabaseName("IX_EvidenceExtractionJobs_Open_Fifo");
            job.HasIndex(j => new { j.ProjectId, j.MessageSequence });
            job.HasIndex(j => j.BatchId);
            job.HasIndex(j => j.ResultVersionId);
            // Idempotenza dei comandi PM (rianalisi di un messaggio, retry): stessa chiave nel progetto = stesso lavoro.
            job.HasIndex(j => new { j.ProjectId, j.RequestKey }).IsUnique().HasFilter("\"RequestKey\" IS NOT NULL");
            job.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EvidenceExtractionJobs_Attempts", "\"AttemptCount\" >= 0 AND \"MaxAttempts\" >= 1");
                t.HasCheckConstraint("CK_EvidenceExtractionJobs_Sequence", "\"MessageSequence\" >= 1");
                t.HasCheckConstraint("CK_EvidenceExtractionJobs_Completed_Result", "\"Status\" <> 'Completed' OR \"ResultVersionId\" IS NOT NULL");
                t.HasCheckConstraint("CK_EvidenceExtractionJobs_Failed_Error", "\"Status\" <> 'Failed' OR \"ErrorCode\" IS NOT NULL");
            });
        });

        builder.Entity<EvidenceExtractionVersion>(version =>
        {
            version.HasKey(v => v.Id);
            version.Property(v => v.SchemaVersion).HasMaxLength(32);
            version.Property(v => v.PromptVersion).HasMaxLength(64);
            version.Property(v => v.ModelId).HasMaxLength(256);
            version.Property(v => v.Parameters).HasColumnType("jsonb");
            version.Property(v => v.NoEvidenceReason).HasMaxLength(2_000);
            version.HasIndex(v => new { v.MessageId, v.Version }).IsUnique();
            version.HasIndex(v => v.JobId).IsUnique();
            version.HasIndex(v => new { v.ProjectId, v.MessageSequence });
            version.HasOne<Project>().WithMany().HasForeignKey(v => v.ProjectId).OnDelete(DeleteBehavior.Restrict);
            version.HasOne<Conversation>().WithMany().HasForeignKey(v => v.ConversationId).OnDelete(DeleteBehavior.Restrict);
            version.HasOne<ConversationMessage>().WithMany().HasForeignKey(v => v.MessageId).OnDelete(DeleteBehavior.Restrict);
            version.HasOne<EvidenceExtractionJob>().WithMany().HasForeignKey(v => v.JobId).OnDelete(DeleteBehavior.Restrict);
            version.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EvidenceExtractionVersions_Version", "\"Version\" >= 1");
                // Zero evidenze è un esito esplicito e motivato, mai un default.
                t.HasCheckConstraint("CK_EvidenceExtractionVersions_NoEvidence",
                    "\"EvidenceCount\" >= 0 AND ((\"EvidenceCount\" = 0) = (\"NoEvidenceReason\" IS NOT NULL)) AND (\"NoEvidenceReason\" IS NULL OR \"NoEvidenceReason\" ~ '\\S')");
            });
        });

        builder.Entity<EvidenceItem>(evidence =>
        {
            evidence.ToTable("Evidences");
            evidence.HasKey(e => e.Id);
            evidence.Property(e => e.Key).HasMaxLength(16);
            evidence.Property(e => e.Kind).HasConversion<string>().HasMaxLength(16);
            evidence.Property(e => e.Polarity).HasConversion<string>().HasMaxLength(16);
            evidence.Property(e => e.Certainty).HasConversion<string>().HasMaxLength(16);
            evidence.Property(e => e.Content).HasColumnType("jsonb");
            evidence.HasIndex(e => new { e.VersionId, e.Ordinal }).IsUnique();
            evidence.HasIndex(e => new { e.VersionId, e.Key }).IsUnique();
            evidence.HasIndex(e => new { e.ProjectId, e.MessageId });
            evidence.HasOne<EvidenceExtractionVersion>().WithMany().HasForeignKey(e => e.VersionId).OnDelete(DeleteBehavior.Restrict);
            evidence.HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Restrict);
            evidence.HasOne<ConversationMessage>().WithMany().HasForeignKey(e => e.MessageId).OnDelete(DeleteBehavior.Restrict);
            evidence.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Evidences_Ordinal", "\"Ordinal\" >= 1");
                t.HasCheckConstraint("CK_Evidences_Statement_NotBlank", "\"Statement\" ~ '\\S'");
            });
        });

        builder.Entity<EvidenceRelation>(relation =>
        {
            relation.HasKey(r => r.Id);
            relation.Property(r => r.Type).HasConversion<string>().HasMaxLength(16);
            relation.Property(r => r.Basis).HasConversion<string>().HasMaxLength(16);
            relation.Property(r => r.Rationale).HasMaxLength(2_000);
            relation.HasIndex(r => new { r.VersionId, r.FromEvidenceId, r.ToEvidenceId, r.Type }).IsUnique();
            relation.HasIndex(r => r.ToEvidenceId);
            relation.HasIndex(r => r.FromEvidenceId);
            relation.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Restrict);
            relation.HasOne<EvidenceExtractionVersion>().WithMany().HasForeignKey(r => r.VersionId).OnDelete(DeleteBehavior.Restrict);
            relation.HasOne<EvidenceItem>().WithMany().HasForeignKey(r => r.FromEvidenceId).OnDelete(DeleteBehavior.Restrict);
            relation.HasOne<EvidenceItem>().WithMany().HasForeignKey(r => r.ToEvidenceId).OnDelete(DeleteBehavior.Restrict);
            relation.ToTable(t => t.HasCheckConstraint("CK_EvidenceRelations_NotSelf", "\"FromEvidenceId\" <> \"ToEvidenceId\""));
        });

        builder.Entity<EvidenceMessageState>(state =>
        {
            state.HasKey(s => s.MessageId);
            state.HasIndex(s => s.ProjectId);
            state.HasOne<ConversationMessage>().WithOne().HasForeignKey<EvidenceMessageState>(s => s.MessageId).OnDelete(DeleteBehavior.Restrict);
            state.HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Restrict);
            state.HasOne<EvidenceExtractionVersion>().WithMany().HasForeignKey(s => s.CurrentVersionId).OnDelete(DeleteBehavior.Restrict);
            state.ToTable(t => t.HasCheckConstraint("CK_EvidenceMessageStates_Revision", "\"Revision\" >= 1"));
        });

        builder.Entity<EvidencePromotion>(promotion =>
        {
            promotion.HasKey(p => p.Id);
            promotion.Property(p => p.Reason).HasMaxLength(1_000);
            promotion.Property(p => p.RelationImpact).HasColumnType("jsonb");
            promotion.HasIndex(p => new { p.MessageId, p.PromotedAt });
            promotion.HasOne<Project>().WithMany().HasForeignKey(p => p.ProjectId).OnDelete(DeleteBehavior.Restrict);
            promotion.HasOne<ConversationMessage>().WithMany().HasForeignKey(p => p.MessageId).OnDelete(DeleteBehavior.Restrict);
            promotion.HasOne<EvidenceExtractionVersion>().WithMany().HasForeignKey(p => p.FromVersionId).OnDelete(DeleteBehavior.Restrict);
            promotion.HasOne<EvidenceExtractionVersion>().WithMany().HasForeignKey(p => p.ToVersionId).OnDelete(DeleteBehavior.Restrict);
            promotion.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(p => p.PromotedBy).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<EvidenceBatch>(batch =>
        {
            batch.HasKey(b => b.Id);
            batch.Property(b => b.Kind).HasConversion<string>().HasMaxLength(16);
            batch.HasIndex(b => new { b.ProjectId, b.Kind, b.RequestKey }).IsUnique();
            batch.HasOne<Project>().WithMany().HasForeignKey(b => b.ProjectId).OnDelete(DeleteBehavior.Restrict);
            batch.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(b => b.RequestedBy).OnDelete(DeleteBehavior.Restrict);
            batch.ToTable(t => t.HasCheckConstraint("CK_EvidenceBatches_Counts", "\"JobCount\" >= 0 AND \"SkippedCount\" >= 0"));
        });

        builder.Entity<AnalysisEvidenceSelection>(selection =>
        {
            selection.HasKey(s => s.RunId);
            selection.Property(s => s.ViewHash).HasMaxLength(64);
            selection.Property(s => s.View).HasColumnType("jsonb");
            selection.HasOne<ProcessingRun>().WithOne().HasForeignKey<AnalysisEvidenceSelection>(s => s.RunId).OnDelete(DeleteBehavior.Restrict);
            selection.HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AnalysisEvidenceSelectionItem>(item =>
        {
            item.HasKey(i => new { i.RunId, i.MessageId });
            item.HasIndex(i => i.VersionId);
            item.HasOne<AnalysisEvidenceSelection>().WithMany().HasForeignKey(i => i.RunId).OnDelete(DeleteBehavior.Restrict);
            item.HasOne<ConversationMessage>().WithMany().HasForeignKey(i => i.MessageId).OnDelete(DeleteBehavior.Restrict);
            item.HasOne<EvidenceExtractionVersion>().WithMany().HasForeignKey(i => i.VersionId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AnalysisEvidenceLink>(link =>
        {
            link.HasKey(l => new { l.RunId, l.ItemId, l.EvidenceId });
            link.HasIndex(l => l.EvidenceId);
            link.HasOne<AnalysisEvidenceSelection>().WithMany().HasForeignKey(l => l.RunId).OnDelete(DeleteBehavior.Restrict);
            link.HasOne<EvidenceItem>().WithMany().HasForeignKey(l => l.EvidenceId).OnDelete(DeleteBehavior.Restrict);
            link.HasOne<Project>().WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<EvidenceCoverageReport>(report =>
        {
            report.HasKey(r => r.Id);
            report.Property(r => r.CheckerVersion).HasMaxLength(32);
            report.Property(r => r.InputHash).HasMaxLength(64);
            report.Property(r => r.Report).HasColumnType("jsonb");
            // NULLS NOT DISTINCT: la stessa terna di input (anche senza revisione o bozza) è un solo report per versione del verificatore.
            report.HasIndex(r => new { r.AnalysisId, r.RevisionId, r.DraftId, r.CheckerVersion }).IsUnique().AreNullsDistinct(false);
            report.HasIndex(r => new { r.ProjectId, r.CreatedAt });
            report.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Restrict);
            report.HasOne<ProcessingRun>().WithMany().HasForeignKey(r => r.AnalysisId).OnDelete(DeleteBehavior.Restrict);
            report.HasOne<AnalysisRevision>().WithMany().HasForeignKey(r => r.RevisionId).OnDelete(DeleteBehavior.Restrict);
            report.HasOne<Draft>().WithMany().HasForeignKey(r => r.DraftId).OnDelete(DeleteBehavior.Restrict);
            report.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(r => r.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            report.ToTable(t => t.HasCheckConstraint("CK_EvidenceCoverageReports_Counts",
                "\"DeterministicErrors\" >= 0 AND \"SemanticRisks\" >= 0 AND \"PmAssessments\" >= 0"));
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
